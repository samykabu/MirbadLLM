using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Audit;

/// <summary>Bounds of <see cref="JsonlAuditLog"/> (design §10.5: 12 months; sizes are this task's bounds).</summary>
public sealed class AuditLogOptions
{
    /// <summary>Months of files kept (default <see cref="AuditLogFiles.RetentionMonths"/>, 12).</summary>
    public int RetentionMonths { get; init; } = AuditLogFiles.RetentionMonths;

    /// <summary>
    /// A month's active file is rolled to a numbered segment (<c>{yyyy-MM}.{n}.jsonl</c>) once a line would take it past
    /// this size (default 16 MiB).
    /// </summary>
    public long MaxFileBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>All audit files together are kept below this size; the oldest files go first (default 256 MiB).</summary>
    public long MaxTotalBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Most lines one <see cref="IAuditLog.ReadAsync"/> returns, whatever the query asks (default 1000).</summary>
    public int MaxReadLines { get; init; } = 1000;
}

/// <summary>
/// The master's durable audit log (design §10.5, D80): <c>{DataPath}/audit/{yyyy-MM}.jsonl</c>, one
/// <see cref="AuditRecord"/> per line (<see cref="MasterStateJson.Options"/>, not indented), append-only.
/// <list type="bullet">
/// <item><b>Append-only.</b> A line is appended and flushed to disk (<c>Flush(true)</c>) before <see cref="WriteAsync"/>
/// returns (a new file's directory is flushed after its first line, a roll after the rename: D69 best effort); nothing
/// is ever rewritten. A torn last line (a crash mid-write) is skipped by the reader and closed with a
/// newline before the next append, so it never merges with a later line.</item>
/// <item><b>Bounded.</b> A month's file rolls to <c>{yyyy-MM}.{n}.jsonl</c> at <see cref="AuditLogOptions.MaxFileBytes"/>;
/// files older than <see cref="AuditLogOptions.RetentionMonths"/> months are deleted, and the oldest files go while the
/// directory is above <see cref="AuditLogOptions.MaxTotalBytes"/> (the active file is never deleted).</item>
/// <item><b>Never fails the action.</b> An I/O error is logged and the line dropped (the <see cref="IAuditLog"/>
/// contract).</item>
/// <item><b>Never holds a secret.</b> Every field goes through <see cref="AuditRedaction.Sanitize"/> first (PEM blocks,
/// API keys, Hugging Face and join tokens become <c>***</c>; control characters and over-long values are cut).</item>
/// </list>
/// Thread-safe; one instance per data path (the master owns <c>/data</c> exclusively, design §4.2).
/// </summary>
public sealed partial class JsonlAuditLog : IAuditLog, IDisposable
{
    private const string Extension = ".jsonl";
    private static readonly JsonSerializerOptions LineOptions = CreateLineOptions();

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly AuditLogOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _prunedForMonth;
    private long _dropped;

    /// <summary>Creates the log under <c>{DataPath}/audit</c>.</summary>
    public JsonlAuditLog(IOptions<MirbadLLMOptions> options, TimeProvider timeProvider, ILogger<JsonlAuditLog>? logger = null, AuditLogOptions? auditOptions = null)
        : this(Path.Combine((options ?? throw new ArgumentNullException(nameof(options))).Value.DataPath, AuditLogFiles.DirectoryName), timeProvider, logger, auditOptions)
    {
    }

    /// <summary>Creates the log in <paramref name="directory"/> (tests, tools).</summary>
    public JsonlAuditLog(string directory, TimeProvider timeProvider, ILogger<JsonlAuditLog>? logger = null, AuditLogOptions? auditOptions = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _directory = Path.GetFullPath(directory);
        _time = timeProvider;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _options = auditOptions ?? new AuditLogOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.RetentionMonths, 1, nameof(auditOptions));
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxFileBytes, 1024, nameof(auditOptions));
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxTotalBytes, _options.MaxFileBytes, nameof(auditOptions));
    }

    /// <summary>The audit directory.</summary>
    public string Directory => _directory;

    /// <summary>Lines dropped because they could not be written (diagnostics).</summary>
    public long DroppedLines => Interlocked.Read(ref _dropped);

    /// <inheritdoc />
    public async Task WriteAsync(AuditRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        byte[] line;
        try
        {
            line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(AuditRedaction.Sanitize(record), LineOptions) + "\n");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            Drop(record, ex);
            return;
        }

        // Cancellation does not stop a line: the action it records already happened.
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = _time.GetUtcNow();
            System.IO.Directory.CreateDirectory(_directory);
            string month = MonthOf(now);
            string active = Path.Combine(_directory, month + Extension);
            bool rolled = RollIfFull(active, month, line.Length);
            Append(active, line);
            if (rolled || !string.Equals(_prunedForMonth, month, StringComparison.Ordinal))
            {
                Prune(now, active);
                _prunedForMonth = month;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Drop(record, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditRecord>> ReadAsync(AuditQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        int max = Math.Clamp(query.Max, 1, _options.MaxReadLines);
        List<AuditRecord> result = [];
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? sinceMonth = query.Since is { } since ? MonthOf(since) : null;
            foreach (AuditFile file in ListFiles().OrderByDescending(f => f.Month, StringComparer.Ordinal).ThenByDescending(f => f.Order))
            {
                ct.ThrowIfCancellationRequested();
                if (sinceMonth is not null && string.CompareOrdinal(file.Month, sinceMonth) < 0)
                {
                    break;
                }

                List<AuditRecord> lines = ReadLines(file.Path);
                for (int i = lines.Count - 1; i >= 0 && result.Count < max; i--)
                {
                    if (Matches(lines[i], query))
                    {
                        result.Add(lines[i]);
                    }
                }

                if (result.Count >= max)
                {
                    break;
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        // Files are month- and segment-ordered and lines append-ordered; a clock step back could still interleave.
        return [.. result.OrderByDescending(r => r.Time)];
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private static bool Matches(AuditRecord record, AuditQuery query) =>
        (query.NodeId is null || string.Equals(record.NodeId, query.NodeId, StringComparison.Ordinal)) &&
        (query.ModelId is null || string.Equals(record.ModelId, query.ModelId, StringComparison.Ordinal)) &&
        (query.Since is null || record.Time >= query.Since.Value);

    private static string MonthOf(DateTimeOffset time) => AuditLogFiles.FileNameFor(time)[..^Extension.Length];

    /// <summary>Appends one line; closes a torn previous line first.</summary>
    private static void Append(string path, byte[] line)
    {
        FileStreamOptions options = new()
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        }

        using FileStream stream = new(path, options);
        long length = stream.Length;
        if (length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n')
            {
                stream.Seek(0, SeekOrigin.End);
                stream.WriteByte((byte)'\n');
            }
        }

        stream.Seek(0, SeekOrigin.End);
        stream.Write(line);
        stream.Flush(flushToDisk: true);
        if (length == 0)
        {
            // A new month's (or rolled) file: its directory entry is made durable too, so a power loss right after the
            // first line cannot lose the file holding it (D69, best effort; Linux only).
            DirectorySync.TryFlushParent(path);
        }
    }

    /// <summary>Rolls the active file to the next segment number when the line would take it past the bound.</summary>
    private bool RollIfFull(string active, string month, int lineLength)
    {
        var info = new FileInfo(active);
        if (!info.Exists || info.Length == 0 || info.Length + lineLength <= _options.MaxFileBytes)
        {
            return false;
        }

        int next = ListFiles().Where(f => f.Month == month && f.Segment is not null).Select(f => f.Segment!.Value).DefaultIfEmpty(0).Max() + 1;
        File.Move(active, Path.Combine(_directory, string.Create(CultureInfo.InvariantCulture, $"{month}.{next}{Extension}")));
        DirectorySync.TryFlushParent(active);
        return true;
    }

    /// <summary>Deletes files past the retention, then the oldest while above the size bound (never the active file).</summary>
    private void Prune(DateTimeOffset now, string active)
    {
        string oldestKept = MonthOf(new DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1 - _options.RetentionMonths));
        List<AuditFile> files = [.. ListFiles().OrderBy(f => f.Month, StringComparer.Ordinal).ThenBy(f => f.Order)];
        long total = files.Sum(f => f.Length);
        foreach (AuditFile file in files)
        {
            bool expired = string.CompareOrdinal(file.Month, oldestKept) < 0;
            bool overSize = total > _options.MaxTotalBytes;
            if (string.Equals(file.Path, active, StringComparison.Ordinal) || (!expired && !overSize))
            {
                continue;
            }

            string name = Path.GetFileName(file.Path);
            string why = expired ? "older than the retention" : "over the size bound";
            try
            {
                File.Delete(file.Path);
                total -= file.Length;
                AuditLog.FileDeleted(_logger, name, why);
            }
            catch (IOException ex)
            {
                AuditLog.PruneFailed(_logger, name, ex.Message);
            }
        }
    }

    private List<AuditFile> ListFiles()
    {
        List<AuditFile> files = [];
        if (!System.IO.Directory.Exists(_directory))
        {
            return files;
        }

        foreach (string path in System.IO.Directory.EnumerateFiles(_directory, "*" + Extension))
        {
            if (TryParseName(Path.GetFileName(path), out string month, out int? segment))
            {
                long length;
                try
                {
                    length = new FileInfo(path).Length;
                }
                catch (IOException)
                {
                    continue;
                }

                files.Add(new AuditFile(path, month, segment, length));
            }
        }

        return files;
    }

    /// <summary><c>yyyy-MM.jsonl</c> (active) or <c>yyyy-MM.{n}.jsonl</c> (rolled segment n ≥ 1).</summary>
    internal static bool TryParseName(string name, out string month, out int? segment)
    {
        month = "";
        segment = null;
        if (!name.EndsWith(Extension, StringComparison.Ordinal) || name.Length < 7 + Extension.Length)
        {
            return false;
        }

        string stem = name[..^Extension.Length];
        string candidate = stem[..7];
        if (!DateTime.TryParseExact(candidate, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return false;
        }

        if (stem.Length == 7)
        {
            month = candidate;
            return true;
        }

        if (stem[7] != '.' || !int.TryParse(stem.AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 1)
        {
            return false;
        }

        month = candidate;
        segment = n;
        return true;
    }

    private List<AuditRecord> ReadLines(string path)
    {
        List<AuditRecord> lines = [];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } text)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<AuditRecord>(text, LineOptions) is { } record)
                    {
                        lines.Add(record);
                    }
                }
                catch (JsonException)
                {
                    // A torn line (crash mid-write) or a damaged one: skipped, the rest of the file still reads.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string name = Path.GetFileName(path);
            AuditLog.ReadFailed(_logger, name, ex.Message);
        }

        return lines;
    }

    private void Drop(AuditRecord record, Exception ex)
    {
        Interlocked.Increment(ref _dropped);
        string type = ex.GetType().Name;
        AuditLog.LineDropped(_logger, record.Action, type, ex.Message);
    }

    private static JsonSerializerOptions CreateLineOptions()
    {
        var options = new JsonSerializerOptions(MasterStateJson.Options) { WriteIndented = false };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>One file: the active file has no segment and sorts after the month's rolled segments.</summary>
    private sealed record AuditFile(string Path, string Month, int? Segment, long Length)
    {
        public int Order => Segment ?? int.MaxValue;
    }
}

/// <summary>Source-generated log messages of the audit log. Action names and file names only, never line contents.</summary>
internal static partial class AuditLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Audit line for {Action} dropped ({ErrorType}): {Reason}")]
    public static partial void LineDropped(ILogger logger, string action, string errorType, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit file {File} deleted ({Why}).")]
    public static partial void FileDeleted(ILogger logger, string file, string why);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audit file {File} could not be deleted: {Reason}")]
    public static partial void PruneFailed(ILogger logger, string file, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audit file {File} could not be read: {Reason}")]
    public static partial void ReadFailed(ILogger logger, string file, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audit line for {Action} not written by the request filter: {Reason}")]
    public static partial void FilterFailed(ILogger logger, string action, string reason);
}
