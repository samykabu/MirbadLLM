using System.Runtime.CompilerServices;
using System.Text;

namespace MirbadLLM.NodeRuntime.Runtimes.Process;

/// <summary>
/// The per-instance log files of supervised processes (design-p3p4 §A.1.3 step 3): <c>{state}/logs/ss-{id}.log</c>, the
/// file the host binds the child's stdout and stderr to (a file, not a pipe, so the child survives an agent restart).
/// Rotation happens before a start (a file at or above <see cref="ProcessRuntimeOptions.LogMaxBytes"/> becomes <c>.1</c>,
/// <c>.1</c> becomes <c>.2</c>, and so on up to <see cref="ProcessRuntimeOptions.LogFilesKept"/>) and, while the child runs
/// and holds the file open, by copy and truncate (<see cref="RotateWhileRunning"/>, MN-219, D128). Reads share the file
/// with the writer.
/// </summary>
public sealed class ProcessLogFiles
{
    private readonly ProcessRuntimeOptions _options;

    /// <summary>Creates the log-file helper.</summary>
    public ProcessLogFiles(ProcessRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>The log file of <paramref name="modelId"/>.</summary>
    public string PathFor(string modelId)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        return Path.Combine(_options.LogsDirectory, $"ss-{modelId}.log");
    }

    /// <summary>
    /// Makes the log directory and rotates <paramref name="modelId"/>'s file when it reached the size limit; returns its
    /// path. A file that cannot be rotated (held open by a process that outlived its record) is left as it is: the next
    /// start appends to it.
    /// </summary>
    public string PrepareForStart(string modelId)
    {
        string path = PathFor(modelId);
        Directory.CreateDirectory(_options.LogsDirectory);
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length >= _options.LogMaxBytes)
            {
                Rotate(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Rotation is housekeeping; never a reason to refuse a start.
        }

        return path;
    }

    /// <summary>
    /// The running child's file reached the size limit: shift the older files, copy it to <c>.1</c> and truncate it in
    /// place (MN-219, D128). The child writes in append mode (the Windows host opens the file with
    /// <c>FILE_APPEND_DATA</c> and shares it for writing), so its next write lands at the new end; lines written between
    /// the copy and the truncation are lost. Housekeeping: never throws for I/O. True when it rotated.
    /// </summary>
    public bool RotateWhileRunning(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < _options.LogMaxBytes)
            {
                return false;
            }

            int kept = _options.LogFilesKept;
            if (kept > 0)
            {
                ShiftOlder(path, kept);
                using var source = OpenShared(path);
                using var copy = new FileStream($"{path}.1", FileMode.Create, FileAccess.Write, FileShare.Read);
                source.CopyTo(copy);
            }

            using var truncate = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            truncate.SetLength(0);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ShiftOlder(string path, int kept)
    {
        string oldest = $"{path}.{kept}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (int i = kept - 1; i >= 1; i--)
        {
            string from = $"{path}.{i}";
            if (File.Exists(from))
            {
                File.Move(from, $"{path}.{i + 1}");
            }
        }
    }

    private void Rotate(string path)
    {
        int kept = _options.LogFilesKept;
        if (kept == 0)
        {
            File.Delete(path);
            return;
        }

        string oldest = $"{path}.{kept}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (int i = kept - 1; i >= 1; i--)
        {
            string from = $"{path}.{i}";
            if (File.Exists(from))
            {
                File.Move(from, $"{path}.{i + 1}");
            }
        }

        File.Move(path, $"{path}.1");
    }

    /// <summary>The file's length now (0 when it does not exist or cannot be read): where the next start's output begins.</summary>
    public static long LengthOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>The last <paramref name="lines"/> lines of <paramref name="path"/> (empty when it does not exist).</summary>
    public static Task<IReadOnlyList<string>> ReadTailAsync(string path, int lines, CancellationToken ct = default) =>
        ReadTailAsync(path, 0, lines, ct);

    /// <summary>
    /// The last <paramref name="lines"/> lines written at or after byte <paramref name="from"/> (the whole file when it
    /// is now shorter: it was rotated). Empty when the file does not exist.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReadTailAsync(string path, long from, int lines, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (lines <= 0 || !File.Exists(path))
        {
            return [];
        }

        var tail = new Queue<string>(Math.Min(lines, 4096));
        try
        {
            await using var stream = OpenShared(path);
            if (from > 0 && from <= stream.Length)
            {
                stream.Seek(from, SeekOrigin.Begin);
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (tail.Count == lines)
                {
                    tail.Dequeue();
                }

                tail.Enqueue(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [.. tail];
        }

        return [.. tail];
    }

    /// <summary>
    /// The last <paramref name="tail"/> lines, then (when <paramref name="follow"/>) every line appended until
    /// <paramref name="keepFollowing"/> answers false or <paramref name="ct"/> is cancelled. Polls every
    /// <paramref name="pollInterval"/>; a partial last line is held back until its newline arrives.
    /// </summary>
    public static async IAsyncEnumerable<string> StreamAsync(
        string path, int tail, bool follow, TimeSpan pollInterval, Func<bool> keepFollowing,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(keepFollowing);
        foreach (string line in await ReadTailAsync(path, tail, ct).ConfigureAwait(false))
        {
            yield return line;
        }

        if (!follow)
        {
            yield break;
        }

        long position = File.Exists(path) ? new FileInfo(path).Length : 0;
        var pending = new StringBuilder();
        while (!ct.IsCancellationRequested && keepFollowing())
        {
            try
            {
                await Task.Delay(pollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            string chunk;
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                await using var stream = OpenShared(path);
                if (stream.Length < position)
                {
                    position = 0; // rotated or truncated
                }

                stream.Seek(position, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                chunk = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                position = stream.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            pending.Append(chunk);
            string text = pending.ToString();
            int last = text.LastIndexOf('\n');
            if (last < 0)
            {
                continue;
            }

            pending.Clear().Append(text[(last + 1)..]);
            foreach (string line in text[..last].Split('\n'))
            {
                yield return line.TrimEnd('\r');
            }
        }
    }

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
}
