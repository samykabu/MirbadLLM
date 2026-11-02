using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>
/// <see cref="IOwnershipStore"/> over <c>{DataPath}/ownership.json</c> (design §6.7, MN-108), written with
/// <see cref="MasterStateJson.Options"/>.
/// <para>Durability: every change is written to <c>ownership.json.tmp</c>, flushed to disk, renamed over the file and
/// then the directory is fsync-ed (<see cref="DirectorySync.Flush"/>, strict: unlike <c>models.json</c> a lost rename is
/// not fail-safe here, since it would silently drop a hold). A crash at any point leaves either the old or the new file
/// in place; a stray temp file from a crash is ignored and overwritten by the next write. The in-memory copy is replaced
/// only after the rename succeeded, so a failed write changes nothing. When only the directory flush fails, the rename
/// happened (the new state is in memory and on disk, its survival of a power loss is not assured) and the call throws
/// <see cref="IOException"/>; acquiring the same record again is idempotent, so the caller can retry.</para>
/// <para>Invariants: at most one record per model and one per id; one <see cref="SemaphoreSlim"/> guards every read
/// and write; records are kept until released, across restarts. A file that cannot be read (bad JSON, other schema,
/// two records for one model) is refused with <see cref="InvalidDataException"/> and left untouched, never read as
/// "no holds".</para>
/// </summary>
public sealed partial class OwnershipStore : IOwnershipStore, IDisposable
{
    private readonly string _filePath;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<OwnershipRecord>? _records;

    /// <summary>Creates the store under <see cref="MirbadLLMOptions.DataPath"/>.</summary>
    public OwnershipStore(IOptions<MirbadLLMOptions> options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _filePath = Path.Combine(options.Value.DataPath, OwnershipDocument.FileName);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Test seam (crash simulation): runs after the temp file is written and flushed, before it is renamed over
    /// <c>ownership.json</c>. A throw here is a crash between the two writes.
    /// </summary>
    internal Func<string, Task>? BeforeReplaceForTests { get; set; }

    /// <summary>The file this store writes.</summary>
    public string FilePath => _filePath;

    /// <inheritdoc/>
    public void Dispose() => _lock.Dispose();

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OwnershipRecord>> ListAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return [.. await LoadUnlockedAsync(ct).ConfigureAwait(false)];
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<OwnershipRecord?> GetForModelAsync(string modelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return (await LoadUnlockedAsync(ct).ConfigureAwait(false)).FirstOrDefault(r => r.ModelId == modelId);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Idempotent per record id: acquiring a record whose id is already stored for the same model returns the stored
    /// record as acquired (a retry after a lost answer). An id stored for another model is refused with
    /// <see cref="InvalidOperationException"/>. <see cref="OwnershipRecord.UpdatedAt"/> is set to now when it is unset.
    /// </remarks>
    public async Task<OwnershipAcquireResult> TryAcquireAsync(OwnershipRecord record, CancellationToken ct = default)
    {
        Check(record);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await LoadUnlockedAsync(ct).ConfigureAwait(false);
            if (records.FirstOrDefault(r => r.Id == record.Id) is { } same)
            {
                return same.ModelId == record.ModelId
                    ? new OwnershipAcquireResult(true, same)
                    : throw new InvalidOperationException(
                        $"Ownership record id '{record.Id}' is already used for model '{same.ModelId}'.");
            }

            if (records.FirstOrDefault(r => r.ModelId == record.ModelId) is { } holder)
            {
                return new OwnershipAcquireResult(false, holder);
            }

            var stored = record.UpdatedAt == default ? record with { UpdatedAt = _timeProvider.GetUtcNow() } : record;
            await WriteUnlockedAsync([.. records, stored], ct).ConfigureAwait(false);
            return new OwnershipAcquireResult(true, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <paramref name="update"/> runs under the lock and must not change the record's id or model
    /// (<see cref="InvalidOperationException"/>); <see cref="OwnershipRecord.UpdatedAt"/> is set to now.
    /// </remarks>
    public async Task<OwnershipRecord?> UpdateAsync(string recordId, Func<OwnershipRecord, OwnershipRecord> update, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(recordId);
        ArgumentNullException.ThrowIfNull(update);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await LoadUnlockedAsync(ct).ConfigureAwait(false);
            int index = records.FindIndex(r => r.Id == recordId);
            if (index < 0)
            {
                return null;
            }

            var current = records[index];
            var changed = update(current) ?? throw new InvalidOperationException("The ownership update returned null.");
            if (changed.Id != current.Id || changed.ModelId != current.ModelId)
            {
                throw new InvalidOperationException(
                    $"An ownership update must not change the record id or model (record '{current.Id}', model '{current.ModelId}').");
            }

            Check(changed);
            changed = changed with { UpdatedAt = _timeProvider.GetUtcNow() };
            var updated = new List<OwnershipRecord>(records) { [index] = changed };
            await WriteUnlockedAsync(updated, ct).ConfigureAwait(false);
            return changed;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<bool> ReleaseAsync(string recordId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(recordId);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await LoadUnlockedAsync(ct).ConfigureAwait(false);
            if (!records.Exists(r => r.Id == recordId))
            {
                return false;
            }

            await WriteUnlockedAsync([.. records.Where(r => r.Id != recordId)], ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static void Check(OwnershipRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.Id))
        {
            throw new ArgumentException("An ownership record needs an id.", nameof(record));
        }

        if (!ModelIdPattern().IsMatch(record.ModelId ?? ""))
        {
            throw new ArgumentException($"Ownership record '{record.Id}' names an invalid model id '{record.ModelId}'.", nameof(record));
        }

        if (string.IsNullOrWhiteSpace(record.Stage))
        {
            throw new ArgumentException($"Ownership record '{record.Id}' needs a stage.", nameof(record));
        }
    }

    /// <summary>Must be called while holding <see cref="_lock"/>. Reads the file once; later calls use the copy.</summary>
    private async Task<List<OwnershipRecord>> LoadUnlockedAsync(CancellationToken ct)
    {
        if (_records is not null)
        {
            return _records;
        }

        if (!File.Exists(_filePath))
        {
            _records = [];
            return _records;
        }

        byte[] bytes = await File.ReadAllBytesAsync(_filePath, ct).ConfigureAwait(false);
        OwnershipDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<OwnershipDocument>(bytes, MasterStateJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{_filePath} is not a valid ownership file; it is left untouched.", ex);
        }

        if (document is null || document.SchemaVersion != OwnershipDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"{_filePath} has schemaVersion {document?.SchemaVersion}; this version of mirbadllm reads version " +
                $"{OwnershipDocument.CurrentSchemaVersion}. It is left untouched.");
        }

        var records = (document.Records ?? []).ToList();
        var duplicateModel = records.GroupBy(r => r.ModelId, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicateModel is not null)
        {
            throw new InvalidDataException($"{_filePath} holds more than one record for model '{duplicateModel.Key}'; it is left untouched.");
        }

        var duplicateId = records.GroupBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicateId is not null)
        {
            throw new InvalidDataException($"{_filePath} holds record id '{duplicateId.Key}' twice; it is left untouched.");
        }

        _records = records;
        return _records;
    }

    /// <summary>Must be called while holding <see cref="_lock"/>. Temp + flush + rename + directory fsync; the copy changes only after the rename.</summary>
    private async Task WriteUnlockedAsync(List<OwnershipRecord> records, CancellationToken ct)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(_filePath))!;
        Directory.CreateDirectory(directory);

        string tempPath = _filePath + ".tmp";
        var document = new OwnershipDocument { SchemaVersion = OwnershipDocument.CurrentSchemaVersion, Records = records };
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, document, MasterStateJson.Options, ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        if (BeforeReplaceForTests is { } hook)
        {
            await hook(tempPath).ConfigureAwait(false);
        }

        await MoveIntoPlaceAsync(tempPath, _filePath, ct).ConfigureAwait(false);
        _records = records;
        DirectorySync.Flush(directory);
    }

    private static async Task MoveIntoPlaceAsync(string tempPath, string target, CancellationToken ct)
    {
        // On Windows (dev/test) a reader or scanner briefly holding the target makes the replace fail with access
        // denied; retry a few times, as JsonModelRegistry does. On Linux the rename is atomic and succeeds first time.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempPath, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(20 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex ModelIdPattern();
}
