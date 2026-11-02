using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace MirbadLLM.NodeRuntime.Downloads.Native;

/// <summary>One file to fetch with <see cref="RangeDownloader"/>.</summary>
public sealed record RangeDownloadRequest
{
    /// <summary>Absolute URL of the file.</summary>
    public required Uri Url { get; init; }

    /// <summary>Local file (already confined by the caller); created when missing, resumed when shorter than expected.</summary>
    public required string FilePath { get; init; }

    /// <summary>Name used in messages (the repo-relative path).</summary>
    public required string DisplayName { get; init; }

    /// <summary>Expected size in bytes; null when unknown (then no range resume past a 416 and no size check).</summary>
    public long? ExpectedLength { get; init; }

    /// <summary>Expected digest, checked once the file is complete; null = size check only.</summary>
    public ContentDigest? Digest { get; init; }

    /// <summary>Credential, sent only to the transport's credential origin.</summary>
    public AuthenticationHeaderValue? Credential { get; init; }

    /// <summary>
    /// Folder the opened file must lie in after links are resolved (Windows: <c>GetFinalPathNameByHandle</c> of the file
    /// handle under that of the folder, compared case-insensitively; design-p3p4 §A.2.3). Null = no handle check.
    /// </summary>
    public string? ConfinementRoot { get; init; }

    /// <summary>Never prints the credential.</summary>
    public override string ToString() =>
        $"RangeDownloadRequest {{ DisplayName = {DisplayName}, ExpectedLength = {ExpectedLength}, Credential = {(Credential is null ? "null" : "***")} }}";
}

/// <summary>How the server answered the request that finished the file (design §6.4 "Range handling").</summary>
public enum RangeResponseKind
{
    /// <summary>206: the body was appended to the bytes already present.</summary>
    Partial,

    /// <summary>200: the server sent the whole file (it ignored the range, or none was asked); the file was rewritten.</summary>
    Full,

    /// <summary>416 with the local size equal to the expected size: the file was only verified.</summary>
    AlreadyComplete,
}

/// <summary>Outcome of <see cref="RangeDownloader.DownloadAsync"/>.</summary>
/// <param name="Verified">True when size and digest match. False: the caller deletes the file (and retries once, §6.4).</param>
/// <param name="Length">Final local length.</param>
/// <param name="Response">How the last response was handled.</param>
/// <param name="ActualDigestHex">Digest computed over the local file (lower-case hex); null when no digest was expected.</param>
public sealed record RangeDownloadResult(bool Verified, long Length, RangeResponseKind Response, string? ActualDigestHex);

/// <summary>
/// Downloads one file with resume and verification (design §6.4 "Range handling" and "Integrity"; reused by the Ollama
/// puller, MN-221):
/// <list type="bullet">
/// <item>A partial local file is resumed with <c>Range: bytes={have}-</c>. <b>206</b> → appended (the
/// <c>Content-Range</c> must start at <c>have</c>, else the file is restarted once); <b>200</b> → the server ignored the
/// range: truncate and write the full body; <b>416</b> → when the local size equals the expected size the file is
/// verified, otherwise it is deleted and restarted (once).</item>
/// <item>The digest is computed while writing (the kept prefix is hashed first), so a resumed file is hashed exactly
/// once; size and digest are compared when the body ends. A body longer than expected is a mismatch, a shorter one a
/// <see cref="DownloadTransientException"/> (the bytes are kept for the next attempt).</item>
/// <item>Write errors that mean "volume full" become <see cref="DownloadDiskFullException"/> (the file is kept); a
/// body that is idle for <see cref="NativeDownloadOptions.ReadIdleTimeout"/>, a connection reset, 408/429/5xx are
/// <see cref="DownloadTransientException"/>; 401/403/404/410 and anything else unexpected are
/// <see cref="DownloadRefusedException"/>.</item>
/// </list>
/// </summary>
public sealed class RangeDownloader
{
    private readonly SecureDownloadTransport _transport;
    private readonly TimeProvider _time;
    private readonly Func<string, Stream> _openWrite;

    /// <summary>Creates the downloader.</summary>
    /// <param name="transport">HTTPS-only transport with the credential rule.</param>
    /// <param name="time">Clock for the idle timeout; system clock when null.</param>
    /// <param name="openWrite">
    /// Opens a file for read/write without truncating it (a seekable stream). Tests inject a stream that fails like a
    /// full disk; default <see cref="OpenFile"/>.
    /// </param>
    public RangeDownloader(SecureDownloadTransport transport, TimeProvider? time = null, Func<string, Stream>? openWrite = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        _time = time ?? TimeProvider.System;
        _openWrite = openWrite ?? OpenFile;
    }

    /// <summary>Opens <paramref name="path"/> for read/write, creating it when missing (never truncating, never deleting).</summary>
    public static Stream OpenFile(string path) =>
        new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);

    /// <summary>Fetches, resumes or verifies <paramref name="request"/>'s file. <paramref name="onLength"/> gets the file's length as it grows.</summary>
    public async Task<RangeDownloadResult> DownloadAsync(RangeDownloadRequest request, Action<long>? onLength, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool restarted = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            long have = CurrentLength(request.FilePath);
            if (request.ExpectedLength is { } tooLong && have > tooLong)
            {
                File.Delete(request.FilePath);
                have = 0;
            }

            onLength?.Invoke(have);
            long rangeFrom = have;
            using var response = await SendAsync(request, rangeFrom, ct).ConfigureAwait(false);
            switch (response.StatusCode)
            {
                case HttpStatusCode.PartialContent:
                    var range = response.Content.Headers.ContentRange;
                    bool consistent = range is { HasRange: true, From: { } from } && from == rangeFrom &&
                        (request.ExpectedLength is not { } expected || range.Length is not { } total || total == expected);
                    if (!consistent)
                    {
                        if (restarted)
                        {
                            throw new DownloadRefusedException(
                                $"The server answered {request.DisplayName} with an inconsistent range ({range?.ToString() ?? "none"}) twice.");
                        }

                        File.Delete(request.FilePath);
                        restarted = true;
                        continue;
                    }

                    return await WriteAsync(request, response, rangeFrom, RangeResponseKind.Partial, onLength, ct).ConfigureAwait(false);

                case HttpStatusCode.OK:
                    return await WriteAsync(request, response, 0, RangeResponseKind.Full, onLength, ct).ConfigureAwait(false);

                case HttpStatusCode.RequestedRangeNotSatisfiable:
                    if (request.ExpectedLength is { } size && have == size)
                    {
                        return await VerifyExistingAsync(request, ct).ConfigureAwait(false);
                    }

                    if (restarted)
                    {
                        throw new DownloadRefusedException($"The server refused every range of {request.DisplayName} (HTTP 416).", 416);
                    }

                    File.Delete(request.FilePath);
                    restarted = true;
                    continue;

                default:
                    throw Classify(request, response.StatusCode);
            }
        }
    }

    /// <summary>
    /// True when <paramref name="exception"/> means the volume is full (Windows <c>ERROR_DISK_FULL</c> /
    /// <c>ERROR_HANDLE_DISK_FULL</c>, Unix <c>ENOSPC</c> / <c>EDQUOT</c>).
    /// </summary>
    public static bool IsDiskFull(IOException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        int hr = exception.HResult;
        const int DiskFull = unchecked((int)0x80070070);
        const int HandleDiskFull = unchecked((int)0x80070027);
        if (hr is DiskFull or HandleDiskFull)
        {
            return true;
        }

        return !OperatingSystem.IsWindows() && hr is 28 or 122;
    }

    private async Task<HttpResponseMessage> SendAsync(RangeDownloadRequest request, long rangeFrom, CancellationToken ct)
    {
        try
        {
            return await _transport.GetAsync(request.Url, request.Credential,
                rangeFrom > 0 ? r => r.Headers.Range = new RangeHeaderValue(rangeFrom, null) : null, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new DownloadTransientException($"Requesting {request.DisplayName} from {SecureDownloadTransport.Describe(request.Url)} failed: {ex.Message}", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new DownloadTransientException($"Requesting {request.DisplayName} timed out.", ex);
        }
    }

    private async Task<RangeDownloadResult> WriteAsync(
        RangeDownloadRequest request, HttpResponseMessage response, long startAt, RangeResponseKind kind, Action<long>? onLength, CancellationToken ct)
    {
        using var hash = request.Digest?.CreateHash();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(4096, _transport.Options.BufferSize));
        try
        {
            var file = Open(request);
            await using (file.ConfigureAwait(false))
            {
                EnsureConfined(request, file);
                long position = startAt;
                try
                {
                    if (startAt > 0)
                    {
                        if (hash is not null)
                        {
                            await HashPrefixAsync(file, startAt, hash, buffer, ct).ConfigureAwait(false);
                        }

                        file.SetLength(startAt);
                        file.Position = startAt;
                    }
                    else
                    {
                        file.SetLength(0);
                        file.Position = 0;
                    }
                }
                catch (IOException ex) when (IsDiskFull(ex))
                {
                    throw DiskFull(request, ex);
                }

                onLength?.Invoke(position);
                bool oversize = false;
                var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using (body.ConfigureAwait(false))
                {
                    using var idle = new CancellationTokenSource(Timeout.InfiniteTimeSpan, _time);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
                    while (true)
                    {
                        int read;
                        idle.CancelAfter(_transport.Options.ReadIdleTimeout);
                        try
                        {
                            read = await body.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                        {
                            await FlushAsync(request, file).ConfigureAwait(false);
                            throw new DownloadTransientException(
                                string.Create(CultureInfo.InvariantCulture, $"{request.DisplayName}: no data for {_transport.Options.ReadIdleTimeout.TotalSeconds:0} s at byte {position}."), ex);
                        }
                        catch (Exception ex) when (ex is IOException or HttpRequestException)
                        {
                            await FlushAsync(request, file).ConfigureAwait(false);
                            throw new DownloadTransientException(
                                string.Create(CultureInfo.InvariantCulture, $"{request.DisplayName}: the connection broke at byte {position}: {ex.Message}"), ex);
                        }

                        if (read == 0)
                        {
                            break;
                        }

                        int keep = read;
                        if (request.ExpectedLength is { } expected && position + read > expected)
                        {
                            keep = (int)Math.Max(0, expected - position);
                            oversize = true;
                        }

                        if (keep > 0)
                        {
                            try
                            {
                                await file.WriteAsync(buffer.AsMemory(0, keep), ct).ConfigureAwait(false);
                            }
                            catch (IOException ex) when (IsDiskFull(ex))
                            {
                                throw DiskFull(request, ex);
                            }

                            hash?.AppendData(buffer, 0, keep);
                            position += keep;
                            onLength?.Invoke(position);
                        }

                        if (oversize)
                        {
                            break;
                        }
                    }
                }

                await FlushAsync(request, file).ConfigureAwait(false);
                if (!oversize && request.ExpectedLength is { } size && position < size)
                {
                    throw new DownloadTransientException(
                        string.Create(CultureInfo.InvariantCulture, $"{request.DisplayName}: the body ended at byte {position} of {size}."));
                }

                string? actual = hash is null ? null : Convert.ToHexStringLower(hash.GetHashAndReset());
                bool verified = !oversize && (request.ExpectedLength is not { } exact || position == exact) &&
                    (request.Digest is null || string.Equals(actual, request.Digest.ExpectedHex, StringComparison.Ordinal));
                return new RangeDownloadResult(verified, position, kind, actual);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<RangeDownloadResult> VerifyExistingAsync(RangeDownloadRequest request, CancellationToken ct)
    {
        long length = CurrentLength(request.FilePath);
        if (request.Digest is null)
        {
            return new RangeDownloadResult(request.ExpectedLength is not { } size || length == size, length, RangeResponseKind.AlreadyComplete, null);
        }

        using var hash = request.Digest.CreateHash();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            var file = new FileStream(request.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);
            await using (file.ConfigureAwait(false))
            {
                int read;
                while ((read = await file.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        return new RangeDownloadResult(string.Equals(actual, request.Digest.ExpectedHex, StringComparison.Ordinal), length,
            RangeResponseKind.AlreadyComplete, actual);
    }

    private static async Task HashPrefixAsync(Stream file, long length, System.Security.Cryptography.IncrementalHash hash, byte[] buffer, CancellationToken ct)
    {
        file.Position = 0;
        long left = length;
        while (left > 0)
        {
            int read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new DownloadTransientException("The partial file shrank while it was being resumed.");
            }

            hash.AppendData(buffer, 0, read);
            left -= read;
        }
    }

    private static async Task FlushAsync(RangeDownloadRequest request, Stream file)
    {
        try
        {
            await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            if (file is FileStream fs)
            {
                fs.Flush(flushToDisk: true);
            }
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            throw DiskFull(request, ex);
        }
    }

    private Stream Open(RangeDownloadRequest request)
    {
        if (request.ConfinementRoot is not null && OperatingSystem.IsWindows() &&
            Path.GetDirectoryName(Path.GetFullPath(request.FilePath)) is { } parent &&
            WindowsFinalPath.CheckDirectory(parent, request.ConfinementRoot) is { } parentEscape)
        {
            // Checked before the file is created, so nothing (not even an empty file) lands behind a link.
            throw new DownloadPathException($"{request.DisplayName}: {parentEscape}");
        }

        try
        {
            return _openWrite(request.FilePath);
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            throw DiskFull(request, ex);
        }
    }

    private static void EnsureConfined(RangeDownloadRequest request, Stream file)
    {
        if (request.ConfinementRoot is null || file is not FileStream fs || !OperatingSystem.IsWindows())
        {
            return;
        }

        if (WindowsFinalPath.Check(fs.SafeFileHandle, request.ConfinementRoot) is { } escape)
        {
            throw new DownloadPathException($"{request.DisplayName}: {escape}");
        }
    }

    private static long CurrentLength(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : 0;
    }

    private static DownloadDiskFullException DiskFull(RangeDownloadRequest request, IOException ex) =>
        new($"The volume is full while writing {request.DisplayName}.", ex);

    private static NativeDownloadException Classify(RangeDownloadRequest request, HttpStatusCode status)
    {
        int code = (int)status;
        string where = SecureDownloadTransport.Describe(request.Url);
        return code switch
        {
            401 or 403 => new DownloadRefusedException(
                $"{where} refused {request.DisplayName} (HTTP {code}): the token is missing, wrong or has no access to this repo.", code),
            404 or 410 => new DownloadRefusedException($"{where} has no {request.DisplayName} (HTTP {code}).", code),
            408 or 429 or >= 500 => new DownloadTransientException($"{where} answered {request.DisplayName} with HTTP {code}."),
            _ => new DownloadRefusedException($"{where} answered {request.DisplayName} with an unexpected HTTP {code}.", code),
        };
    }
}
