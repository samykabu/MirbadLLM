using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration;

/// <summary>
/// File-backed <see cref="IModelRegistry"/>: <c>{DataPath}/models.json</c>, atomically written (temp file
/// + move) and guarded by a <see cref="SemaphoreSlim"/>. Seeds itself with the Phase 0 model on first run.
/// Every definition is normalized (<see cref="VllmArgsNormalizer"/>) on load and on save: owned flags in ExtraArgs
/// are lifted into their typed fields, so a raw override can't exist. A file written before that migrates in memory
/// on read and is persisted in the canonical shape on the next save.
/// <para>
/// The file is the version-2 envelope <see cref="ModelRegistryDocument"/> (design §5.4). A version-1 file (a bare JSON
/// array) is migrated exactly once, inside the load path under the registry lock: the original bytes are copied to
/// <c>models.v1.json</c> first, then every definition gets <c>Placement = {NodeId: bootstrap}</c>,
/// <see cref="RuntimeKind.Vllm"/> and, for the configured ids, <see cref="ModelDefinition.KeepServingDuringJobs"/>,
/// and the file is rewritten as version 2. The rewrite stores the definitions as read plus those fields (not
/// normalized), so the normalizer's "persisted on the next save" behaviour and every config hash stay as they were.
/// In a version-2 file a null placement means unplaced and is preserved; nothing re-places a model on load.
/// </para>
/// <para>
/// P2 extension points (MN-101, design D76), both no-ops by default so P1 behaviour is unchanged and no Wave 7 task edits
/// this file: every <see cref="IModelDefinitionValidator"/> passed to the constructor runs on each save and delete, after
/// the built-in checks and under the registry lock with the stored definition (MN-108: placement validation, placement
/// generation, ownership); and <see cref="ModelRegistryOptions.SeedPolicy"/> = <see cref="ModelSeedPolicy.None"/> skips
/// the seed model on a missing file (recovery mode, MN-113/MN-130).
/// </para>
/// </summary>
public sealed partial class JsonModelRegistry : IModelRegistry, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly string _legacyCopyPath;
    private readonly RegistryMigrationOptions _migration;
    private readonly IModelDefinitionValidator[] _validators;
    private readonly ModelSeedPolicy _seedPolicy;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _initialized;

    public JsonModelRegistry(IOptions<MirbadLLMOptions> options)
        : this(options, Options.Create(new RegistryMigrationOptions()))
    {
    }

    public JsonModelRegistry(IOptions<MirbadLLMOptions> options, IOptions<RegistryMigrationOptions> migrationOptions)
        : this(options, migrationOptions, validators: null, registryOptions: null)
    {
    }

    /// <summary>Creates the registry with the P2 extension points (design D76).</summary>
    /// <param name="options">Data path.</param>
    /// <param name="migrationOptions">v1 → v2 migration settings.</param>
    /// <param name="validators">Validators run on every save and delete under the lock, in order; null or empty = none (P1).</param>
    /// <param name="registryOptions">Seed policy; null = <see cref="ModelSeedPolicy.Seed"/> (P1).</param>
    public JsonModelRegistry(
        IOptions<MirbadLLMOptions> options,
        IOptions<RegistryMigrationOptions> migrationOptions,
        IEnumerable<IModelDefinitionValidator>? validators,
        IOptions<ModelRegistryOptions>? registryOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(migrationOptions);
        _filePath = Path.Combine(options.Value.DataPath, ModelRegistryDocument.FileName);
        _legacyCopyPath = Path.Combine(options.Value.DataPath, ModelRegistryDocument.LegacyCopyFileName);
        _migration = migrationOptions.Value;
        _validators = [.. validators ?? []];
        _seedPolicy = registryOptions?.Value.SeedPolicy ?? ModelSeedPolicy.Seed;
    }

    public void Dispose() => _lock.Dispose();

    public async Task<IReadOnlyList<ModelDefinition>> ListAsync(CancellationToken ct = default)
    {
        var models = await ReadAllAsync(ct).ConfigureAwait(false);
        return models;
    }

    public async Task<ModelDefinition?> GetAsync(string id, CancellationToken ct = default)
    {
        var models = await ReadAllAsync(ct).ConfigureAwait(false);
        return models.FirstOrDefault(m => m.Id == id);
    }

    public async Task UpsertAsync(ModelDefinition model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        model = NormalizeForSave(model);
        Validate(model);
        model = model with { Tags = ModelTags.NormalizeAll(model.Tags), Sources = model.Sources ?? [] };

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);
            var models = await LoadUnlockedAsync(ct).ConfigureAwait(false);

            if (models.Any(m => m.Id != model.Id && string.Equals(m.ServedName, model.ServedName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"ServedName '{model.ServedName}' is already used by another model.", nameof(model));
            }

            var stored = models.FirstOrDefault(m => m.Id == model.Id);
            foreach (var validator in _validators)
            {
                var validated = await validator.ValidateAsync(new ModelDefinitionChange(stored, model), ct).ConfigureAwait(false) ?? model;
                if (!string.Equals(validated.Id, model.Id, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Validator {validator.GetType().Name} changed the model id '{model.Id}'.");
                }

                model = validated;
            }

            var updated = models.Where(m => m.Id != model.Id).Append(model).ToList();
            await WriteAllUnlockedAsync(updated, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);
            var models = await LoadUnlockedAsync(ct).ConfigureAwait(false);
            var stored = models.FirstOrDefault(m => m.Id == id);
            if (stored is null)
            {
                return false;
            }

            foreach (var validator in _validators)
            {
                await validator.ValidateAsync(new ModelDefinitionChange(stored, null), ct).ConfigureAwait(false);
            }

            models.RemoveAll(m => m.Id == id);

            await WriteAllUnlockedAsync(models, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IReadOnlyList<ModelDefinition>> ReadAllAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);
            return await LoadUnlockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Must be called while holding <see cref="_lock"/>. Creates the seeded file the first time it runs.</summary>
    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(_filePath) && _seedPolicy == ModelSeedPolicy.Seed)
        {
            // A fresh install has exactly one node, the bootstrap node, so the seed is placed there.
            var seed = SeedModel with { Placement = new ModelPlacement(_migration.EffectiveBootstrapNodeId) };
            await WriteAllUnlockedAsync([seed], ct).ConfigureAwait(false);
        }

        _initialized = true;
    }

    /// <summary>Must be called while holding <see cref="_lock"/>. Migrates a version-1 file (once) before returning its models.</summary>
    private async Task<List<ModelDefinition>> LoadUnlockedAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        byte[] original = await File.ReadAllBytesAsync(_filePath, ct).ConfigureAwait(false);

        // A file saved by a Windows editor may start with a UTF-8 BOM; the stream reader used before MN-006 skipped it,
        // the span-based reader does not (MN-090). The v1 copy keeps the original bytes.
        byte[] bytes = original.AsSpan().StartsWith(Utf8Bom) ? original[Utf8Bom.Length..] : original;
        var models = IsLegacyArray(bytes)
            ? await MigrateLegacyUnlockedAsync(bytes, original, ct).ConfigureAwait(false)
            : ReadEnvelope(bytes);

        // Files written before tags and sources existed simply lack them (the [] defaults apply); an explicit
        // null is normalized the same way so callers never see a null list.
        return models
            .Select(FillNullLists)
            .Select(VllmArgsNormalizer.Apply)
            .ToList();
    }

    private static ModelDefinition FillNullLists(ModelDefinition m) =>
        m.Tags is null || m.Sources is null || m.ExtraArgs is null
            ? m with { Tags = m.Tags ?? [], Sources = m.Sources ?? [], ExtraArgs = m.ExtraArgs ?? [] }
            : m;

    /// <summary>A version-1 file is a bare JSON array (design §5.4); anything else must be the envelope.</summary>
    private static bool IsLegacyArray(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        return reader.Read() && reader.TokenType == JsonTokenType.StartArray;
    }

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private List<ModelDefinition> ReadEnvelope(byte[] bytes)
    {
        // The envelope's keys are required and exact (MN-090): defaulting a missing "schemaVersion" to 2 or a missing
        // "models" to [] would read a hand-edited or foreign object as an empty registry, and the next save would then
        // overwrite every definition. Refuse instead, leaving the file untouched.
        using (var probe = JsonDocument.Parse(bytes, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }))
        {
            var root = probe.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
                !root.TryGetProperty("models", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    $"{_filePath} is neither a version-1 array nor a {{\"schemaVersion\": N, \"models\": [...]}} envelope " +
                    "(both keys required, camelCase); it is left untouched.");
            }
        }

        var document = JsonSerializer.Deserialize<ModelRegistryDocument>(bytes, JsonOptions)
            ?? throw new InvalidDataException($"{_filePath} holds JSON null.");
        if (document.SchemaVersion != ModelRegistryDocument.CurrentSchemaVersion)
        {
            // A newer file is never rewritten by older code, and an unknown older envelope is not guessed at.
            throw new InvalidDataException(
                $"{_filePath} has schemaVersion {document.SchemaVersion}; this version of mirbadllm reads version " +
                $"{ModelRegistryDocument.CurrentSchemaVersion} (or a version-1 bare array, which it migrates once).");
        }

        return [.. document.Models ?? []];
    }

    /// <summary>
    /// The one-time v1 → v2 migration (design §5.4 step 2). Crash-safe and idempotent: the original is copied to
    /// <c>models.v1.json</c> before <c>models.json</c> is replaced, so a crash in between leaves a v1 file that the
    /// next load migrates again, with the same result and the same copy. The definitions are stored as read plus the
    /// new fields; normalization stays a load-time transform persisted on the next save, as before.
    /// </summary>
    private async Task<List<ModelDefinition>> MigrateLegacyUnlockedAsync(byte[] json, byte[] original, CancellationToken ct)
    {
        var legacy = JsonSerializer.Deserialize<List<ModelDefinition>>(json, JsonOptions) ?? [];
        var placement = new ModelPlacement(_migration.EffectiveBootstrapNodeId);
        var keepServing = _migration.EffectiveKeepServingDuringJobs;

        var migrated = legacy
            .Select(FillNullLists)
            .Select(m => m with
            {
                Placement = placement,
                Runtime = RuntimeKind.Vllm,
                KeepServingDuringJobs = m.KeepServingDuringJobs || keepServing.Contains(m.Id),
            })
            .ToList();

        await WriteLegacyCopyUnlockedAsync(original, ct).ConfigureAwait(false);
        await WriteAllUnlockedAsync(migrated, ct).ConfigureAwait(false);
        return migrated;
    }

    /// <summary>
    /// Keeps the original version-1 bytes as <c>models.v1.json</c>. An existing copy is never overwritten: when it
    /// holds the same bytes (a migration interrupted after the copy) nothing is written; when it differs (another v1
    /// file was put in place later) this one is kept next to it under a UTC-timestamped name.
    /// </summary>
    private async Task WriteLegacyCopyUnlockedAsync(byte[] original, CancellationToken ct)
    {
        string target = _legacyCopyPath;
        if (File.Exists(target))
        {
            byte[] existing = await File.ReadAllBytesAsync(target, ct).ConfigureAwait(false);
            if (existing.AsSpan().SequenceEqual(original))
            {
                return;
            }

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
            target = Path.Combine(
                Path.GetDirectoryName(_legacyCopyPath) ?? "",
                $"{Path.GetFileNameWithoutExtension(_legacyCopyPath)}.{stamp}.json");
        }

        string tempPath = target + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await stream.WriteAsync(original, ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true); // durable before the rename (MN-090)
        }

        await MoveIntoPlaceAsync(tempPath, target, ct).ConfigureAwait(false);
    }

    private async Task WriteAllUnlockedAsync(List<ModelDefinition> models, CancellationToken ct)
    {
        string tempPath = _filePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            var document = new ModelRegistryDocument { SchemaVersion = ModelRegistryDocument.CurrentSchemaVersion, Models = models };
            await JsonSerializer.SerializeAsync(stream, document, JsonOptions, ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true); // durable before the rename (MN-090): the v1 migration runs exactly once
        }

        await MoveIntoPlaceAsync(tempPath, _filePath, ct).ConfigureAwait(false);
    }

    private static async Task MoveIntoPlaceAsync(string tempPath, string target, CancellationToken ct)
    {
        // On Windows (dev/test) a reader or scanner briefly holding the target makes the replace fail with
        // access denied; retry a few times. On Linux the rename is atomic and succeeds first time.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempPath, target, overwrite: true);
                DirectorySync.TryFlushParent(target); // best effort (D69)
                break;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(20 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The canonical definition to store. Throws <see cref="ArgumentException"/> when an owned flag in ExtraArgs can't
    /// be read (missing or invalid value), since storing it would let a raw override survive.
    /// </summary>
    public static ModelDefinition NormalizeForSave(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var normalized = VllmArgsNormalizer.Normalize(model with { ExtraArgs = model.ExtraArgs ?? [] });
        if (normalized.Problems.Count > 0)
        {
            throw new ArgumentException("Extra args: " + string.Join(" ", normalized.Problems), nameof(model));
        }

        return normalized.Definition;
    }

    internal static void Validate(ModelDefinition model)
    {
        if (!IdPattern().IsMatch(model.Id))
        {
            throw new ArgumentException(
                $"Model Id '{model.Id}' is invalid; it must match ^[a-z0-9][a-z0-9-]{{0,39}}$.", nameof(model));
        }

        if (string.IsNullOrWhiteSpace(model.ServedName))
        {
            throw new ArgumentException("ServedName must not be empty.", nameof(model));
        }

        if (string.IsNullOrWhiteSpace(model.ModelPath))
        {
            throw new ArgumentException("ModelPath must not be empty.", nameof(model));
        }

        if (model.MemoryBudgetGiB <= 0)
        {
            throw new ArgumentException("MemoryBudgetGiB must be positive.", nameof(model));
        }

        if (model.MaxModelLen <= 0)
        {
            throw new ArgumentException("MaxModelLen must be positive.", nameof(model));
        }

        if (model.MaxNumSeqs <= 0)
        {
            throw new ArgumentException("MaxNumSeqs must be positive.", nameof(model));
        }

        if (model.IdleStopMinutes is <= 0)
        {
            throw new ArgumentException("IdleStopMinutes must be at least 1; leave it out to never stop the model for being idle.", nameof(model));
        }

        if (model.Speculative is { NumSpeculativeTokens: <= 0 })
        {
            throw new ArgumentException("Speculative.NumSpeculativeTokens must be positive.", nameof(model));
        }

        if (model.Speculative is { } speculative && string.IsNullOrWhiteSpace(speculative.Method) && string.IsNullOrWhiteSpace(speculative.DraftModel))
        {
            throw new ArgumentException("Speculative decoding needs a method or a draft model.", nameof(model));
        }

        if (model.ImageDigest is { } digest && !ImageDigestPattern().IsMatch(digest))
        {
            throw new ArgumentException($"ImageDigest '{digest}' is invalid; it must be sha256: followed by 64 lowercase hex characters.", nameof(model));
        }

        // Throws ArgumentException for an invalid tag or too many tags.
        _ = ModelTags.NormalizeAll(model.Tags);
        ValidateSources(model.Sources ?? []);
    }

    /// <summary>Sources must name a repo and a plain folder directly under the models root, each folder once.</summary>
    internal static void ValidateSources(IReadOnlyList<ModelSource> sources)
    {
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.RepoId) || !RepoIdPattern().IsMatch(source.RepoId))
            {
                throw new ArgumentException(
                    $"Source repo id '{source?.RepoId}' is invalid; it must look like owner/name.", nameof(sources));
            }

            if (string.IsNullOrWhiteSpace(source.Folder) || !FolderPattern().IsMatch(source.Folder) || source.Folder is "." or "..")
            {
                throw new ArgumentException(
                    $"Source folder '{source.Folder}' is invalid; it must be one folder name under the models root (letters, digits, '.', '_' or '-').",
                    nameof(sources));
            }

            if (!folders.Add(source.Folder))
            {
                throw new ArgumentException($"Source folder '{source.Folder}' is listed twice.", nameof(sources));
            }
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex RepoIdPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,200}$")]
    private static partial Regex FolderPattern();

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageDigestPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex IdPattern();

    /// <summary>Phase 0 uncensored FP8 model, seeded the first time the registry runs.</summary>
    internal static readonly ModelDefinition SeedModel = new()
    {
        Id = "ornith-9b-fp8",
        ServedName = "Ornith-Uncensored-FP8",
        ModelPath = "Ornith-1.5-9B-uncensored",
        Quantization = "fp8",
        KvCacheDtype = "fp8",
        MaxModelLen = 262_144,
        MaxNumSeqs = 16,
        MemoryBudgetGiB = 36,
        ToolCallParser = "qwen3_coder",
        ReasoningParser = "qwen3",
        EnablePrefixCaching = true,
        AutoStart = true,
    };
}
