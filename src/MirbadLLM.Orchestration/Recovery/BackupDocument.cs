using System.Text.Json;
using System.Text.Json.Serialization;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Recovery;

/// <summary>Bound from <c>MirbadLLM:Backup</c>: where and when scheduled backups are written.</summary>
public sealed class BackupOptions
{
    public const string SectionName = "MirbadLLM:Backup";

    /// <summary>Directory scheduled backups go to (a host directory mounted into the container). Empty turns them off.</summary>
    public string Directory { get; set; } = "/backups";

    /// <summary>Daily run time, <c>HH:mm</c>, in the controller's local time zone.</summary>
    public string DailyAt { get; set; } = "03:00";

    /// <summary>How many scheduled backups to keep; older ones are deleted after each run.</summary>
    public int Keep { get; set; } = 14;

    /// <summary>Encrypts the secrets section of scheduled backups. Empty = scheduled backups carry no secrets.</summary>
    public string? Passphrase { get; set; }

    /// <summary>Name used in backup files and shown on restore. Empty = the machine name (a container id inside Docker).</summary>
    public string? HostName { get; set; }

    /// <summary>
    /// <c>MirbadLLM:Backup:IncludeCa</c> (design §5.5, decision D9): put the master CA (certificate and private key) in the
    /// encrypted secrets section. Only ever with a passphrase; without one the CA is never written. Default true.
    /// </summary>
    public bool IncludeCa { get; set; } = true;

    /// <summary>The configured <see cref="DailyAt"/>, or null when it is not a valid <c>HH:mm</c> time.</summary>
    public TimeOnly? ParsedDailyAt =>
        TimeOnly.TryParseExact(DailyAt?.Trim(), ["HH:mm", "H:mm"], System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var at) ? at : null;
}

/// <summary>
/// One backup file: the model definitions (tags, sources and placements included), the Services page's container
/// whitelist, and — only when a passphrase was given — an AES-GCM encrypted <see cref="Secrets"/> section with the API
/// key records (and, in version 2, the master CA, saved tokens and the backup share's settings and password). Later
/// additions (running models, node units, history, agent updates) are optional fields of version 2: absent in older files.
/// </summary>
/// <remarks>
/// <para>Version 1 is the single-box format. Version 2 (design §5.5) adds <see cref="Master"/>, <see cref="Nodes"/>,
/// per-node host services (<see cref="HostServicesBackup.Nodes"/>), the model placement fields (carried by
/// <see cref="ModelDefinition"/>) and <see cref="BackupSecrets.Pki"/>. <see cref="BackupService"/> writes version 2 and
/// <see cref="BackupService.Parse"/> reads both; a version-1 restore places every model on the bootstrap node
/// (<see cref="WithDefaultPlacement"/>).</para>
/// <para><see cref="CurrentSchemaVersion"/> keeps the value 1 (frozen by MN-001: the constructor default and the
/// version-1 constant); the version written is <see cref="MultiNodeSchemaVersion"/>. <see cref="BackupCrypto"/> binds the
/// document's own version into the secrets section's associated data, so version-1 files keep decrypting and a
/// version-2 section (which may hold the CA) only decrypts inside a version-2 document (W3 review MN-094).</para>
/// </remarks>
public sealed record BackupDocument
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>The multi-node format (design §5.5). Written from MN-034 on; a P1 master restores version 1 too (every model to <c>dgx</c>).</summary>
    public const int MultiNodeSchemaVersion = 2;

    /// <summary>Every schema version <see cref="BackupService.Parse"/> reads.</summary>
    public static IReadOnlyList<int> ReadableSchemaVersions { get; } = [CurrentSchemaVersion, MultiNodeSchemaVersion];

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The controller's informational version (with the commit when the build recorded one).</summary>
    public string AppVersion { get; init; } = "";

    public string HostName { get; init; } = "";

    public IReadOnlyList<ModelDefinition> Models { get; init; } = [];

    /// <summary>Contents of <c>host-services.json</c>; null when the Services store was not available.</summary>
    public HostServicesBackup? HostServices { get; init; }

    /// <summary>Null when the backup was made without a passphrase.</summary>
    public EncryptedSection? Secrets { get; init; }

    /// <summary>
    /// Where scheduled backups were going when this file was made (a NAS share), without any password; null for the
    /// DGX's own disk. A restore shows it so the owner can reconnect the share (re-entering the password).
    /// </summary>
    public BackupDestinationInfo? BackupDestination { get; init; }

    /// <summary>
    /// Version 2: the master's fencing identity at backup time (non-secret; fleet recovery restores it and takes over
    /// from there, design §9.4). Null in version 1.
    /// </summary>
    public BackupMasterInfo? Master { get; init; }

    /// <summary>
    /// Version 2: every node with its identity and security state (approved, draining, revoked, pins), not only its name
    /// (design §5.5). Never join tokens, bootstrap bundles or private keys. Null in version 1.
    /// </summary>
    public IReadOnlyList<BackupNode>? Nodes { get; init; }

    /// <summary>
    /// The models meant to be running at backup time, by node id (<c>running-models.json</c>, <see cref="ModelRecoveryState"/>).
    /// A configuration import can start them like auto-start models; fleet recovery seeds them as the running intent so
    /// they come back after the restart. Null in backups made before this field existed.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? RunningModels { get; init; }

    /// <summary>The fleet's automatic agent updates setting (<c>agent-updates.json</c>); null when not recorded.</summary>
    public bool? AgentUpdatesAutomatic { get; init; }

    /// <summary>Benchmark and tuning results and start-time measurements; null when not recorded.</summary>
    public BackupHistory? History { get; init; }

    /// <summary>
    /// Each node's managed systemd/Windows services (the Services page's unit whitelist, kept by hostd or the agent on the
    /// node), by node id; only nodes whose list could be read. Null when not recorded.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<BackupHostUnit>>? HostUnits { get; init; }

    /// <summary>True when <paramref name="modelId"/> was meant to be running on some node when the backup was made.</summary>
    public bool WasRunning(string modelId) => RunningModels?.Values.Any(ids => ids.Contains(modelId, StringComparer.Ordinal)) == true;

    /// <summary>
    /// The Services container list and restart policies for <paramref name="nodeId"/>: the node's entry of a version-2
    /// <c>hostServices.nodes</c> map, or — for a version-1 file, whose lists sit at the top level — those lists. Null when
    /// the backup has none for that node.
    /// </summary>
    public HostServicesBackup? HostServicesFor(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        if (HostServices is null)
        {
            return null;
        }

        return HostServices.Nodes is { } nodes ? nodes.GetValueOrDefault(nodeId) : HostServices;
    }

    /// <summary>
    /// A version-1 backup has no placements: every model goes to <paramref name="bootstrapNodeId"/> (design §5.5, P1
    /// compatibility), as the one-time <c>models.json</c> migration does. A version-2 backup is returned unchanged (an
    /// unplaced model stays unplaced, §5.1).
    /// </summary>
    public BackupDocument WithDefaultPlacement(string bootstrapNodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bootstrapNodeId);
        if (SchemaVersion != CurrentSchemaVersion || Models.All(m => m.Placement is not null))
        {
            return this;
        }

        var placement = new ModelPlacement(bootstrapNodeId);
        return this with { Models = [.. Models.Select(m => m.Placement is null ? m with { Placement = placement } : m)] };
    }

    /// <summary>The backup file format: web (camelCase) names, string enums, indented.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// An encrypted payload and everything needed to decrypt it except the passphrase: AES-256-GCM with a key
/// derived by PBKDF2-SHA256 (<see cref="Iterations"/> rounds, random <see cref="Salt"/>), a random 12-byte
/// <see cref="Nonce"/> and the 16-byte authentication <see cref="Tag"/>. Binary fields are base64.
/// </summary>
public sealed record EncryptedSection(
    string Algorithm,
    string Kdf,
    int Iterations,
    string Salt,
    string Nonce,
    string Tag,
    string Ciphertext);

/// <summary>The decrypted secrets section: API key records, saved tokens and (version 2) the master CA.</summary>
public sealed record BackupSecrets(IReadOnlyList<ApiKeyRecord> ApiKeys)
{
    /// <summary>
    /// The saved tokens (Access > Tokens) with their values; null in backups made before saved tokens existed. Their
    /// values are only ever written inside this passphrase-encrypted section.
    /// </summary>
    public IReadOnlyList<SavedTokenSecret>? Tokens { get; init; }

    /// <summary>
    /// Version 2: the master CA (design §5.5, decision D9), included by default when a passphrase is set
    /// (<c>MirbadLLM:Backup:IncludeCa</c>). Null when not included or in version 1.
    /// </summary>
    public BackupPkiSecrets? Pki { get; init; }

    /// <summary>
    /// The backup destination with every setting needed to apply it again, the SMB password included when the master kept
    /// it (the owner's decision: share passwords travel only inside this passphrase-encrypted section). Null for the local
    /// disk and in older backups.
    /// </summary>
    public BackupDestinationRequest? BackupDestination { get; init; }
}

/// <summary>Benchmark and tuning runs (raw files of <c>BenchmarkStore</c>) and the start-time measurements.</summary>
/// <param name="OmittedRuns">Older runs left out to keep the file small (<see cref="BackupHistoryFiles.MaxRunBytes"/>).</param>
public sealed record BackupHistory(
    IReadOnlyList<BackupRunFile> Benchmarks,
    IReadOnlyList<BackupRunFile> Tuning,
    IReadOnlyDictionary<string, double> StartupTimes,
    int OmittedRuns = 0);

/// <summary>One stored run, kept as its file's JSON so every field (old and new) survives the round trip.</summary>
public sealed record BackupRunFile(string ModelId, string Id, JsonElement Run);

/// <summary>One managed systemd unit or Windows service of a node's Services page.</summary>
/// <param name="Name">The managed name (the typed confirmation when adding it back).</param>
/// <param name="Unit">The unit or service.</param>
public sealed record BackupHostUnit(string Name, string Unit, HostServiceKind Kind, string? DisplayName, bool Protected);

/// <summary>
/// The master CA in a backup's encrypted secrets (design §5.5, §9.4). A class, not a record, so the key never appears in a
/// generated <c>ToString()</c>. Anyone with the file and the passphrase can mint certificates; rotate the CA if both leak.
/// </summary>
public sealed class BackupPkiSecrets
{
    /// <summary>CA certificate (PEM).</summary>
    public string CaCertPem { get; set; } = "";

    /// <summary>CA private key (PKCS#8 PEM). Secret.</summary>
    public string CaKeyPem { get; set; } = "";
}

/// <summary>Version 2 <c>master</c> section (design §5.5): the fencing identity at backup time.</summary>
/// <param name="MasterInstanceId">The master's instance id.</param>
/// <param name="Epoch">Its epoch when the backup was made.</param>
public sealed record BackupMasterInfo(string MasterInstanceId, long Epoch);

/// <summary>One node in a version-2 backup (design §5.5): identity, addresses, operator settings and security state.</summary>
public sealed record BackupNode
{
    /// <summary>Node id.</summary>
    public required string Id { get; init; }

    /// <summary>Node uid (configuration import keeps a placement only when a node here has the same uid, design §9.4).</summary>
    public required string Uid { get; init; }

    /// <summary>Display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Agent address.</summary>
    public required string Address { get; init; }

    /// <summary>Other addresses.</summary>
    public IReadOnlyList<string> AltAddresses { get; init; } = [];

    /// <summary>Labels.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Approved.</summary>
    public bool Approved { get; init; }

    /// <summary>Draining.</summary>
    public bool Draining { get; init; }

    /// <summary>Revoked.</summary>
    public bool Revoked { get; init; }

    /// <summary>Memory reserve.</summary>
    public double MemoryReserveGiB { get; init; }

    /// <summary>Per-GPU reserves by UUID.</summary>
    public IReadOnlyDictionary<string, double> GpuReserveGiB { get; init; } = new Dictionary<string, double>();

    /// <summary>Node default vLLM image.</summary>
    public string? DefaultVllmImage { get; init; }

    /// <summary>
    /// Approved digest of <see cref="DefaultVllmImage"/> (<c>NodeRecord.DefaultVllmImageDigest</c>, MN-107; additive, MN-113).
    /// Not a secret. Omitted when null, so a backup of a node without one reads as before.
    /// </summary>
    public string? DefaultVllmImageDigest { get; init; }

    /// <summary>Node default llama.cpp image (<c>NodeRecord.DefaultLlamaCppImage</c>, P3; additive, MN-113). Omitted when null.</summary>
    public string? DefaultLlamaCppImage { get; init; }

    /// <summary>Approved digest of <see cref="DefaultLlamaCppImage"/> (additive, MN-113). Omitted when null.</summary>
    public string? DefaultLlamaCppImageDigest { get; init; }

    /// <summary>
    /// Concurrent starts allowed on the node (<c>NodeRecord.MaxConcurrentStarts</c>, design §4.8); null = the node's
    /// default. An operator setting like the reserves, so it travels with them (W3 review MN-094, additive).
    /// </summary>
    public int? MaxConcurrentStarts { get; init; }

    /// <summary>The agent certificate pin (public-key hash, not a secret).</summary>
    public string? CertSpkiSha256 { get; init; }

    /// <summary>Agent certificate expiry.</summary>
    public DateTimeOffset? CertNotAfter { get; init; }

    /// <summary>Last capability report.</summary>
    public NodeCapabilities? LastCapabilities { get; init; }
}

/// <summary>The file is not a backup this version can read (bad JSON, missing fields, another schema version).</summary>
public sealed class BackupFormatException : Exception
{
    public BackupFormatException()
    {
    }

    public BackupFormatException(string message)
        : base(message)
    {
    }

    public BackupFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The secrets section did not decrypt: a wrong passphrase, or the file was changed after it was written.</summary>
public sealed class BackupPassphraseException : Exception
{
    public BackupPassphraseException()
    {
    }

    public BackupPassphraseException(string message)
        : base(message)
    {
    }

    public BackupPassphraseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
