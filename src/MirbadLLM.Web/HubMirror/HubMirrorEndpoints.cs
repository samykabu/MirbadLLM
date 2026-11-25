using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Caching.Memory;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Downloads.Native;

namespace MirbadLLM.Web.HubMirror;

/// <summary>Anonymous, pinned model-info and resolve endpoints for the dedicated mirror listener.</summary>
public sealed class HubMirrorEndpoints : IDisposable
{
    public const string ConnectionMarker = "MirbadLLM.HubMirrorListener";
    private const string ContentSourceHeader = "MirbadLLM-Content-Source";
    private static readonly TimeSpan AuthorizationTtl = TimeSpan.FromMinutes(5);
    private readonly HubMirrorCatalog _catalog;
    private readonly SecureDownloadTransport _transport;
    private readonly HuggingFaceHubClient _hub;
    private readonly TimeProvider _time;
    private readonly MemoryCache _authorizations = new(new MemoryCacheOptions { SizeLimit = 2048 });
    private readonly MemoryCache _listings = new(new MemoryCacheOptions { SizeLimit = 24 });
    private readonly MemoryCache _digests = new(new MemoryCacheOptions { SizeLimit = 2048 });
    private readonly SemaphoreSlim[] _authorizationGates = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1)).ToArray();
    private readonly SemaphoreSlim[] _listingGates = Enumerable.Range(0, 16).Select(_ => new SemaphoreSlim(1)).ToArray();
    private readonly Lock _digestLock = new();
    private readonly Dictionary<DigestKey, Task> _pendingDigests = [];
    private readonly SemaphoreSlim _hashSlots = new(2);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<ContentDigest, Stream, CancellationToken, Task<bool>> _hash;
    private bool _disposed;

    public HubMirrorEndpoints(HubMirrorCatalog catalog, HubHttpClients clients, TimeProvider time)
        : this(catalog, clients, time, static (digest, file, ct) => digest.MatchesAsync(file, ct))
    {
    }

    internal HubMirrorEndpoints(HubMirrorCatalog catalog, HubHttpClients clients, TimeProvider time,
        Func<ContentDigest, Stream, CancellationToken, Task<bool>> hash)
    {
        _catalog = catalog;
        _transport = clients.OfficialTransport();
        _hub = new HuggingFaceHubClient(_transport, clients.Official.BaseAddress ?? HubOrigin.OfficialEndpoint);
        _time = time;
        _hash = hash;
    }

    public static IApplicationBuilder UseHubMirrorListener(IApplicationBuilder app) =>
        app.Use((context, next) => context.Features.Get<IConnectionItemsFeature>()?.Items.ContainsKey(ConnectionMarker) == true
            ? context.RequestServices.GetRequiredService<HubMirrorEndpoints>().HandleAsync(context) : next(context));

    public async Task HandleAsync(HttpContext context)
    {
        if (context.Request.Headers.ContainsKey("Authorization"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        bool head = HttpMethods.IsHead(context.Request.Method);
        if ((!head && !HttpMethods.IsGet(context.Request.Method)) || Parse(context.Request) is not { } route ||
            head && route.Path is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var ct = context.RequestAborted;
        try
        {
            if (route.Path is null)
            {
                await ListAsync(context, route, ct).ConfigureAwait(false);
            }
            else
            {
                await ResolveAsync(context, route, head, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is NativeDownloadException or HttpRequestException or IOException ||
                                   ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
            }
            else
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }
    }

    private async Task ListAsync(HttpContext context, MirrorRoute route, CancellationToken ct)
    {
        using var response = await _hub.GetModelInfoAsync(route.Repo, route.Revision, null, ct).ConfigureAwait(false);
        context.Response.StatusCode = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        byte[] body = await _hub.ReadModelInfoAsync(response, ct).ConfigureAwait(false);
        if (!_hub.ParseSnapshot(route.Repo, body).IsPublicUngated)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        context.Response.ContentType = "application/json";
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, ct).ConfigureAwait(false);
    }

    private async Task ResolveAsync(HttpContext context, MirrorRoute route, bool head, CancellationToken ct)
    {
        string commit = route.Revision!;
        string path = route.Path!;
        var authorization = await AuthorizeAsync(route.Repo, commit, path, ct).ConfigureAwait(false);
        if (authorization.File is not { } file || authorization.Status != HttpStatusCode.OK)
        {
            context.Response.StatusCode = (int)authorization.Status;
            return;
        }

        var digest = file.LfsSha256 is { } lfs ? ContentDigest.Sha256(lfs)
            : file.BlobSha1 is { } blob ? ContentDigest.GitBlobSha1(blob, file.Size) : null;
        string? etag = digest is null ? authorization.UpstreamETag : $"\"{digest.ExpectedHex}\"";
        context.Response.Headers["X-Repo-Commit"] = commit;
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.Headers.CacheControl = "no-store";
        if (etag is not null)
        {
            context.Response.Headers.ETag = etag;
        }

        var range = head ? null : RequestedRange(context, etag, file.Size);
        if (range is { Length: 0 })
        {
            context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            context.Response.Headers.ContentRange = $"bytes */{file.Size}";
            return;
        }

        if (digest is not null)
        {
            foreach (string folder in await _catalog.FindCandidatesAsync(route.Repo, commit, path, ct).ConfigureAwait(false))
            {
                string relative = $"{folder}/{path}";
                await using var candidate = ConfinedModelFile.Open(_catalog.Root, relative);
                if (candidate is null || !CanServeLocal(candidate, relative, file.Size, digest))
                {
                    continue;
                }

                context.Response.Headers[ContentSourceHeader] = "local";
                SetContentHeaders(context, file.Size, range);
                if (!head)
                {
                    candidate.Position = range?.Start ?? 0;
                    await CopyExactlyAsync(candidate, context.Response.Body, range?.Length ?? file.Size, ct).ConfigureAwait(false);
                }

                return;
            }
        }

        context.Response.Headers[ContentSourceHeader] = "hub";
        if (!head)
        {
            using var response = await _transport.GetAsync(_hub.FileUrl(route.Repo, commit, path), null,
                range is { } bytes ? message => message.Headers.Range = new RangeHeaderValue(bytes.Start, bytes.End) : null, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                context.Response.StatusCode = (int)response.StatusCode;
                return;
            }

            if (response.StatusCode != (range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent) ||
                response.Content.Headers.ContentLength is { } declared && declared != (range?.Length ?? file.Size) ||
                range is { } requested && (response.Content.Headers.ContentRange is not { Unit: "bytes" } actual ||
                    actual.From != requested.Start || actual.To != requested.End || actual.Length != file.Size))
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }

            SetContentHeaders(context, file.Size, range);
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await CopyExactlyAsync(source, context.Response.Body, range?.Length ?? file.Size, ct).ConfigureAwait(false);
            return;
        }

        SetContentHeaders(context, file.Size, null);
    }

    private async Task<Authorization> AuthorizeAsync(string repo, string commit, string path, CancellationToken ct)
    {
        var key = (repo, commit, path);
        var gate = Gate(_authorizationGates, key);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (_authorizations.TryGetValue(key, out Authorization? cached) && cached!.Expires > now)
            {
                return cached;
            }

            var listing = await GetListingAsync(repo, commit, ct).ConfigureAwait(false);
            HubFile? file = listing.Files?.GetValueOrDefault(path);
            HttpStatusCode status = listing.Status;
            string? upstreamTag = null;
            if (status == HttpStatusCode.OK && file is null)
            {
                status = HttpStatusCode.NotFound;
            }

            if (status == HttpStatusCode.OK)
            {
                using var response = await _transport.SendAsync(HttpMethod.Head, _hub.FileUrl(repo, commit, path), null, null, ct)
                    .ConfigureAwait(false);
                status = response.IsSuccessStatusCode ? HttpStatusCode.OK : response.StatusCode;
                upstreamTag = response.Headers.ETag is { IsWeak: false } tag ? tag.Tag : null;
            }

            var expires = listing.Status == HttpStatusCode.OK ? listing.Expires : now + AuthorizationTtl;
            var verdict = new Authorization(status, file, upstreamTag, expires);
            if (status is HttpStatusCode.OK or HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                _authorizations.Set(key, verdict, new MemoryCacheEntryOptions().SetSize(1).SetAbsoluteExpiration(AuthorizationTtl));
            }

            return verdict;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Listing> GetListingAsync(string repo, string commit, CancellationToken ct)
    {
        var key = (repo, commit);
        var gate = Gate(_listingGates, key);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (_listings.TryGetValue(key, out Listing? cached) && cached!.Expires > now)
            {
                return cached;
            }

            using var response = await _hub.GetModelInfoAsync(repo, commit, null, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new Listing(response.StatusCode, null, now);
            }

            var snapshot = _hub.ParseSnapshot(repo, await _hub.ReadModelInfoAsync(response, ct).ConfigureAwait(false));
            if (!snapshot.IsPublicUngated)
            {
                return new Listing(HttpStatusCode.Forbidden, null, now);
            }

            if (snapshot.CommitSha != commit || RepoPathRules.CheckAll(snapshot.Files.Select(file => file.Path)) is not null)
            {
                return new Listing(HttpStatusCode.BadGateway, null, now);
            }

            var listing = new Listing(HttpStatusCode.OK, snapshot.Files.ToDictionary(file => file.Path, StringComparer.Ordinal), now + AuthorizationTtl);
            _listings.Set(key, listing, new MemoryCacheEntryOptions().SetSize(1).SetAbsoluteExpiration(AuthorizationTtl));
            return listing;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool CanServeLocal(FileStream candidate, string relative, long size, ContentDigest digest)
    {
        if (ConfinedModelFile.Identify(candidate.SafeFileHandle) is not { } before || before.Size != size)
        {
            return false;
        }

        var key = new DigestKey(Path.Combine(_catalog.Root, relative), before, digest.Algorithm, digest.ExpectedHex);
        lock (_digestLock)
        {
            if (_disposed)
            {
                return false;
            }

            if (_digests.TryGetValue(key, out bool valid))
            {
                return valid;
            }

            if (_pendingDigests.Count < 32 && !_pendingDigests.ContainsKey(key))
            {
                _pendingDigests.Add(key, Task.Run(() => CheckDigestAsync(key, relative, digest)));
            }

            // The independent receiver verifies the official digest before publishing the download.
            return true;
        }
    }

    private async Task CheckDigestAsync(DigestKey key, string relative, ContentDigest digest)
    {
        var ct = _shutdown.Token;
        try
        {
            await _hashSlots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var file = ConfinedModelFile.Open(_catalog.Root, relative);
                if (file is null || ConfinedModelFile.Identify(file.SafeFileHandle) != key.Identity)
                {
                    return;
                }

                bool valid = await _hash(digest, file, ct).ConfigureAwait(false);
                if (ConfinedModelFile.Identify(file.SafeFileHandle) == key.Identity)
                {
                    _digests.Set(key, valid, new MemoryCacheEntryOptions().SetSize(1));
                }
            }
            finally
            {
                _hashSlots.Release();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException ||
                                          exception is OperationCanceledException && ct.IsCancellationRequested)
        {
            // A failed read has no digest verdict. A later request can try again.
        }
        finally
        {
            lock (_digestLock)
            {
                _pendingDigests.Remove(key);
            }
        }
    }

    internal Task PendingDigestChecks()
    {
        lock (_digestLock)
        {
            return Task.WhenAll(_pendingDigests.Values);
        }
    }

    private static SemaphoreSlim Gate<T>(SemaphoreSlim[] gates, T key) where T : notnull =>
        gates[(int)((uint)key.GetHashCode() % (uint)gates.Length)];

    private static ByteRange? RequestedRange(HttpContext context, string? etag, long size)
    {
        string raw = context.Request.Headers.Range.ToString();
        string ifRange = context.Request.Headers.IfRange.ToString();
        if (raw.Length == 0 || ifRange.Length > 0 && ifRange != etag)
        {
            return null;
        }

        if (!RangeHeaderValue.TryParse(raw, out var range) || range.Unit != "bytes" || range.Ranges.Count != 1)
        {
            return new ByteRange(0, 0);
        }

        var value = range.Ranges.Single();
        if (size == 0 || value.From is { } start && start >= size || value.From is null && value.To == 0)
        {
            return new ByteRange(0, 0);
        }

        long end = value.From is null ? size - 1 : Math.Min(value.To ?? size - 1, size - 1);
        long from = value.From ?? Math.Max(0, size - value.To!.Value);
        return new ByteRange(from, end - from + 1);
    }

    private static void SetContentHeaders(HttpContext context, long size, ByteRange? range)
    {
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = range?.Length ?? size;
        if (range is { } bytes)
        {
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.Headers.ContentRange = $"bytes {bytes.Start}-{bytes.End}/{size}";
        }
    }

    private static async Task CopyExactlyAsync(Stream source, Stream destination, long length, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            while (length > 0)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(length, buffer.Length)), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("The Hub file changed while it was being read.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                length -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static MirrorRoute? Parse(HttpRequest request)
    {
        string target = request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget ?? "";
        string[] raw = target.Split('?', 2);
        if (!raw[0].StartsWith('/') || raw[0].Length > 8192 || raw.Length > 1 && raw[1] != "blobs=true")
        {
            return null;
        }

        string[] segments = raw[0][1..].Split('/');
        if (segments.Length >= 3 && segments[0] == "api" && segments[1] == "models")
        {
            string[] parts = segments[2..];
            bool hasRevision = parts.Length is 3 or 4 && parts[^2] == "revision";
            int repoLength = parts.Length - (hasRevision ? 2 : 0);
            if (repoLength is not (1 or 2) || !TryRepo(parts[..repoLength], out string repo))
            {
                return null;
            }

            string? revision = hasRevision ? Uri.UnescapeDataString(parts[^1]) : null;
            return HuggingFaceHubClient.IsValidRevision(revision) && revision != "."
                ? new MirrorRoute(repo, revision, null) : null;
        }

        if (raw.Length != 1)
        {
            return null;
        }

        int resolve = segments.Length >= 4 && segments[1] == "resolve" && ContentDigest.IsHex(segments[2], 40) ? 1 : 2;
        if (segments.Length < resolve + 3 || segments[resolve] != "resolve" ||
            !ContentDigest.IsHex(segments[resolve + 1], 40) || !TryRepo(segments[..resolve], out string fileRepo))
        {
            return null;
        }

        var partsOfPath = segments[(resolve + 2)..].Select(Uri.UnescapeDataString).ToArray();
        string path = string.Join('/', partsOfPath);
        return !partsOfPath.Any(part => part.Contains('/') || part.Contains('\\')) && RepoPathRules.Check(path) is null
            ? new MirrorRoute(fileRepo, segments[resolve + 1].ToLowerInvariant(), path) : null;
    }

    private static bool TryRepo(string[] segments, out string repo)
    {
        var parts = segments.Select(Uri.UnescapeDataString).ToArray();
        repo = string.Join('/', parts);
        return !parts.Any(part => part.Contains('/')) && HuggingFaceHubClient.IsValidRepoId(repo);
    }

    public void Dispose()
    {
        lock (_digestLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _shutdown.Cancel();
        PendingDigestChecks().GetAwaiter().GetResult();
        _shutdown.Dispose();
        _hashSlots.Dispose();
        _authorizations.Dispose();
        _listings.Dispose();
        _digests.Dispose();
        foreach (var gate in _authorizationGates.Concat(_listingGates))
        {
            gate.Dispose();
        }
    }

    private sealed record MirrorRoute(string Repo, string? Revision, string? Path);
    private sealed record Authorization(HttpStatusCode Status, HubFile? File, string? UpstreamETag, DateTimeOffset Expires);
    private sealed record Listing(HttpStatusCode Status, IReadOnlyDictionary<string, HubFile>? Files, DateTimeOffset Expires);
    private readonly record struct DigestKey(string Path, ConfinedModelFile.Identity Identity,
        System.Security.Cryptography.HashAlgorithmName Algorithm, string ExpectedDigest);
    private readonly record struct ByteRange(long Start, long Length)
    {
        public long End => Start + Length - 1;
    }
}
