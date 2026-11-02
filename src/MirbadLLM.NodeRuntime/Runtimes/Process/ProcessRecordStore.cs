using System.Text.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.NodeRuntime.Runtimes.Process;

/// <summary>
/// <see cref="IProcessRecordStore"/> over <c>{state}/processes.json</c> (<see cref="ProcessRecordsDocument"/>, camelCase
/// <see cref="AgentJson"/>; design-p3p4 §A.1.3 steps 4 and 6, MN-207 D121). Every write replaces the whole document
/// atomically and durably before it returns: temp file + <c>Flush(true)</c> + rename + directory flush, as the operation
/// log and the fencing state do, so a crash leaves either the old or the new document, never a mix (a torn temp file is
/// ignored). A document that cannot be read (corrupt, a newer schema, duplicate model ids) is reported with
/// <see cref="InvalidDataException"/> and left untouched: it is never silently emptied, because forgetting a record would
/// orphan its process and its port.
/// </summary>
public sealed class ProcessRecordStore : IProcessRecordStore, IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ProcessRecordsDocument? _document;

    /// <summary>Creates the store over <paramref name="path"/> (the file need not exist yet).</summary>
    public ProcessRecordStore(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = Path.GetFullPath(path);
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>The document's path.</summary>
    public string FilePath => _path;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProcessRecord>> ListAsync(CancellationToken ct = default) =>
        (await ReadAsync(ct).ConfigureAwait(false)).Processes;

    /// <inheritdoc />
    public async Task<ProcessRecord?> GetAsync(string modelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        return (await ReadAsync(ct).ConfigureAwait(false)).Processes
            .FirstOrDefault(r => string.Equals(r.ModelId, modelId, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public Task UpsertAsync(ProcessRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return MutateAsync(d => d with { Processes = [.. Without(d.Processes, record.ModelId), record] }, ct);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string modelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        bool removed = false;
        await MutateAsync(d =>
        {
            var kept = Without(d.Processes, modelId);
            removed = kept.Count != d.Processes.Count;
            return removed ? d with { Processes = kept } : null;
        }, ct).ConfigureAwait(false);
        return removed;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PendingProcessStart>> ListPendingAsync(CancellationToken ct = default) =>
        (await ReadAsync(ct).ConfigureAwait(false)).Pending;

    /// <inheritdoc />
    public Task AddPendingAsync(PendingProcessStart pending, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pending);
        return MutateAsync(d => d with { Pending = [.. Without(d.Pending, pending.ModelId), pending] }, ct);
    }

    /// <inheritdoc />
    public Task CommitPendingAsync(ProcessRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return MutateAsync(d => d with
        {
            Processes = [.. Without(d.Processes, record.ModelId), record],
            Pending = Without(d.Pending, record.ModelId),
        }, ct);
    }

    /// <inheritdoc />
    public async Task<bool> RemovePendingAsync(string modelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        bool removed = false;
        await MutateAsync(d =>
        {
            var kept = Without(d.Pending, modelId);
            removed = kept.Count != d.Pending.Count;
            return removed ? d with { Pending = kept } : null;
        }, ct).ConfigureAwait(false);
        return removed;
    }

    private static List<ProcessRecord> Without(IReadOnlyList<ProcessRecord> records, string modelId) =>
        [.. records.Where(r => !string.Equals(r.ModelId, modelId, StringComparison.Ordinal))];

    private static List<PendingProcessStart> Without(IReadOnlyList<PendingProcessStart> pending, string modelId) =>
        [.. pending.Where(p => !string.Equals(p.ModelId, modelId, StringComparison.Ordinal))];

    private async Task<ProcessRecordsDocument> ReadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _document ??= Load(_path);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Applies <paramref name="change"/> (null = nothing to write) and persists the result before returning.</summary>
    private async Task MutateAsync(Func<ProcessRecordsDocument, ProcessRecordsDocument?> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _document ??= Load(_path);
            var next = change(current);
            if (next is null)
            {
                return;
            }

            Persist(_path, next);
            _document = next;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads the document; a missing file is an empty document, anything unreadable is an error.</summary>
    internal static ProcessRecordsDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            return new ProcessRecordsDocument();
        }

        ProcessRecordsDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ProcessRecordsDocument>(File.ReadAllBytes(path), AgentJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"'{path}' is not a valid process record document ({ex.Message}). It is not reset automatically: its records " +
                "name the processes this agent supervises. Restore it, or stop the listed processes before deleting it.", ex);
        }

        if (document is null)
        {
            throw new InvalidDataException($"'{path}' is empty. Restore it, or stop the agent's processes before deleting it.");
        }

        if (document.SchemaVersion > ProcessRecordsDocument.CurrentSchemaVersion || document.SchemaVersion < 1)
        {
            throw new InvalidDataException(
                $"'{path}' has schema version {document.SchemaVersion}; this agent reads up to {ProcessRecordsDocument.CurrentSchemaVersion}.");
        }

        // Older writers or hand edits may leave the lists null.
        document = document with { Processes = document.Processes ?? [], Pending = document.Pending ?? [] };
        if (document.Processes.Any(r => r is null || string.IsNullOrEmpty(r.ModelId) || r.Process is null || r.Spec is null) ||
            document.Processes.GroupBy(r => r.ModelId, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            throw new InvalidDataException($"'{path}' has an incomplete record or two records for one model.");
        }

        if (document.Pending.Any(p => p is null || string.IsNullOrEmpty(p.ModelId)))
        {
            throw new InvalidDataException($"'{path}' has an incomplete pending start.");
        }

        return document;
    }

    private static void Persist(string path, ProcessRecordsDocument document)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, document, AgentJson.Options);
            stream.Flush(flushToDisk: true);
        }

        // On Windows a scanner briefly holding the target can make the replace fail; retry a few times (FencingState's rule).
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                break;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(20 * attempt);
            }
        }

        if (!string.IsNullOrEmpty(directory))
        {
            DirectorySync.Flush(directory);
        }
    }
}
