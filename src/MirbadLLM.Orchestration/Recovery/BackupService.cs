using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Pki;
using MirbadLLM.Pki;

namespace MirbadLLM.Orchestration.Recovery;

/// <summary>
/// Builds, writes and reads backup files (<see cref="BackupDocument"/>): gathers the model definitions, the
/// Services container whitelist and — with a passphrase — the encrypted API key records; parses and validates
/// an uploaded file; decrypts its secrets. Restoring is <see cref="RestorePlanner"/> and <see cref="RestoreJobService"/>.
/// </summary>
/// <remarks>
/// Writes schema version 2 (design §5.5): the master's fencing identity (<paramref name="master"/>), every node of
/// <paramref name="nodes"/> with its security state, the Services lists under the bootstrap node, and — only with a
/// passphrase and <see cref="BackupOptions.IncludeCa"/> — the master CA (<paramref name="certificateAuthority"/>) in the
/// encrypted section. Each of those is left out when its source is not composed (the single-box wiring).
/// <para>The CA written is the <b>current issuing</b> CA (<paramref name="masterCertificates"/>' authority when composed,
/// read per backup): before a CA rotation's step 3 that is still the old CA (every certificate chains to it), from step 3
/// on the new one, so a backup taken after a rotation never carries the replaced (possibly leaked) key (MN-126).</para>
/// </remarks>
/// <param name="hostServices">The Services store (<c>host-services.json</c>); in P1 it holds the bootstrap node's lists.</param>
/// <param name="destinations">The backup destination manager, recorded so a restore can show where backups went.</param>
/// <param name="master">The master's fencing identity, for the <c>master</c> section.</param>
/// <param name="nodes">The node registry, for the <c>nodes</c> section.</param>
/// <param name="certificateAuthority">The master CA loaded at boot; not owned (never disposed here).</param>
/// <param name="bootstrapNodeId">Node id the Services store belongs to; null = <see cref="RegistryMigrationOptions.DefaultBootstrapNodeId"/>.</param>
/// <param name="masterCertificates">The master certificates; their <see cref="MasterCertificates.Authority"/> is the CA written (null = <paramref name="certificateAuthority"/>).</param>
/// <param name="recovery">The running intent per node, for <see cref="BackupDocument.RunningModels"/>.</param>
/// <param name="agentUpdates">The fleet's automatic agent updates setting.</param>
/// <param name="dataPath">The master's data path, for the benchmark and tuning results (<see cref="BackupHistoryFiles"/>); null = none.</param>
/// <param name="startupTimes">The start-time measurements, carried with the history.</param>
/// <param name="hostServiceManagers">Each node's Services manager, for its managed units (<see cref="BackupDocument.HostUnits"/>).</param>
public sealed partial class BackupService(
    IModelRegistry registry,
    IApiKeyStore apiKeys,
    IOptions<BackupOptions> options,
    TimeProvider timeProvider,
    HostServicesStore? hostServices = null,
    IBackupDestinationManager? destinations = null,
    IMasterIdentity? master = null,
    INodeRegistry? nodes = null,
    CertificateAuthority? certificateAuthority = null,
    string? bootstrapNodeId = null,
    MasterCertificates? masterCertificates = null,
    ITokenStore? tokens = null,
    ModelRecoveryState? recovery = null,
    AgentUpdateSettingsStore? agentUpdates = null,
    string? dataPath = null,
    StartupTimeTracker? startupTimes = null,
    INodeHostServiceManagers? hostServiceManagers = null)
{
    /// <summary>Largest backup file accepted for restore (a backup of a few dozen models is tens of kilobytes).</summary>
    public const long MaxFileBytes = 16 * 1024 * 1024;

    private readonly string _bootstrapNodeId =
        string.IsNullOrWhiteSpace(bootstrapNodeId) ? RegistryMigrationOptions.DefaultBootstrapNodeId : bootstrapNodeId.Trim();

    public string HostName => SafeHostName(string.IsNullOrWhiteSpace(options.Value.HostName) ? Environment.MachineName : options.Value.HostName);

    /// <summary>The controller's informational version (it carries the commit after a '+' when the build recorded one).</summary>
    public static string AppVersion { get; } =
        (Assembly.GetEntryAssembly() ?? typeof(BackupService).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>
    /// Snapshot of the current configuration (schema version 2). A null or empty <paramref name="passphrase"/> leaves the
    /// secrets section out entirely (no API keys and never the CA in the file).
    /// </summary>
    public async Task<BackupDocument> CreateAsync(string? passphrase, CancellationToken ct = default)
    {
        var models = await registry.ListAsync(ct).ConfigureAwait(false);

        IReadOnlyList<NodeRecord>? records = nodes is null ? null : await nodes.ListAsync(ct).ConfigureAwait(false);
        var services = await ExportHostServicesAsync(records, ct).ConfigureAwait(false);

        BackupDestinationInfo? destination = null;
        if (destinations is not null)
        {
            try
            {
                destination = await destinations.DescribeForBackupAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HostServiceException or IOException)
            {
                // Recording the destination is a convenience for a later restore; the backup itself matters more.
            }
        }

        IReadOnlyList<BackupNode>? backupNodes = records is null
            ? null
            : [.. records.OrderBy(n => n.Id, StringComparer.Ordinal).Select(ToBackupNode)];

        EncryptedSection? secrets = null;
        if (!string.IsNullOrEmpty(passphrase))
        {
            var keys = await apiKeys.ListAsync(ct).ConfigureAwait(false);
            var content = new BackupSecrets(keys)
            {
                Pki = options.Value.IncludeCa ? ExportCa() : null,
                Tokens = tokens is null ? null : await tokens.ExportAsync(ct).ConfigureAwait(false),
                BackupDestination = destination is null ? null : await ExportDestinationAsync(ct).ConfigureAwait(false),
            };
            byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(content, BackupDocument.JsonOptions);
            try
            {
                secrets = BackupCrypto.Encrypt(plaintext, passphrase, BackupDocument.MultiNodeSchemaVersion);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        return new BackupDocument
        {
            SchemaVersion = BackupDocument.MultiNodeSchemaVersion,
            CreatedAt = timeProvider.GetUtcNow(),
            AppVersion = AppVersion,
            HostName = HostName,
            Models = models.OrderBy(m => m.Id, StringComparer.Ordinal).ToList(),
            HostServices = services,
            Secrets = secrets,
            BackupDestination = destination,
            Master = master is null ? null : new BackupMasterInfo(master.InstanceId, master.Epoch),
            Nodes = backupNodes,
            RunningModels = recovery?.RunningModels(),
            AgentUpdatesAutomatic = agentUpdates?.Automatic,
            History = string.IsNullOrEmpty(dataPath)
                ? null
                : BackupHistoryFiles.Export(dataPath, startupTimes?.Export() ?? new Dictionary<string, double>()),
            HostUnits = await ExportHostUnitsAsync(records, ct).ConfigureAwait(false),
        };
    }

    private async Task<BackupDestinationRequest?> ExportDestinationAsync(CancellationToken ct)
    {
        try
        {
            return await destinations!.ExportForBackupAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HostServiceException or IOException)
        {
            return null; // as for the description: the rest of the backup matters more
        }
    }

    /// <summary>
    /// Every approved node's managed units (not containers: those are <see cref="HostServices"/>), read through the Services
    /// page's manager. A node that does not answer is left out, so a restore never reads "no units" into a missing list.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<BackupHostUnit>>?> ExportHostUnitsAsync(
        IReadOnlyList<NodeRecord>? records, CancellationToken ct)
    {
        if (hostServiceManagers is null)
        {
            return null;
        }

        IEnumerable<string> nodeIds = records is null
            ? [_bootstrapNodeId]
            : records.Where(n => n.Approved && !n.Revoked).Select(n => n.Id).Order(StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<BackupHostUnit>>(StringComparer.Ordinal);
        foreach (string nodeId in nodeIds)
        {
            try
            {
                if (await hostServiceManagers.ForNodeAsync(nodeId, ct).ConfigureAwait(false) is not { } manager)
                {
                    continue;
                }

                var snapshot = await manager.ListAsync(ct).ConfigureAwait(false);
                if (snapshot.HostAgentAvailable)
                {
                    result[nodeId] = [.. snapshot.Services.Where(s => s.Kind != HostServiceKind.Container)
                        .OrderBy(s => s.Name, StringComparer.Ordinal)
                        .Select(s => new BackupHostUnit(s.Name, s.Unit, s.Kind, s.DisplayName, s.Protected))];
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // An offline, slow or older node: its list stays out of this backup (it is still on the node).
            }
        }

        return result;
    }

    /// <summary>
    /// The <c>hostServices.nodes</c> map (design §5.5): the bootstrap node's lists from <c>host-services.json</c>, as before,
    /// plus every other node's <c>host-services.{nodeId}.json</c> next to it (MN-120's per-node Services page; W7 review
    /// MN-126). With a node registry only nodes still registered are included (a rejected node's file is left out). A file
    /// that cannot be read is left out (its node's page reports it); the rest of the backup still matters. Null without a
    /// Services store (the single-box test wiring), so the DGX's backup is unchanged when no other node has a file.
    /// </summary>
    private async Task<HostServicesBackup?> ExportHostServicesAsync(IReadOnlyList<NodeRecord>? records, CancellationToken ct)
    {
        if (hostServices is null)
        {
            return null;
        }

        var perNode = new Dictionary<string, HostServicesBackup>(StringComparer.Ordinal);
        try
        {
            perNode[_bootstrapNodeId] = await hostServices.ExportAsync(ct).ConfigureAwait(false);
        }
        catch (HostServiceException)
        {
            // A corrupt host-services.json is reported on the Services page; the rest of the backup still matters.
        }

        string? dataPath = Path.GetDirectoryName(Path.GetFullPath(hostServices.FilePath));
        if (dataPath is not null)
        {
            var registered = records?.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            foreach (string nodeId in NodeHostServicesFiles.ListNodeIds(dataPath))
            {
                if (nodeId == _bootstrapNodeId || (registered is not null && !registered.Contains(nodeId)))
                {
                    continue;
                }

                try
                {
                    using var store = new HostServicesStore(Path.Combine(dataPath, NodeHostServicesFiles.FileName(nodeId)));
                    perNode[nodeId] = await store.ExportAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HostServiceException or IOException or UnauthorizedAccessException)
                {
                    // Reported on that node's Services page.
                }
            }
        }

        return perNode.Count == 0 ? null : new HostServicesBackup(null, null) { Nodes = perNode };
    }

    public static byte[] Serialize(BackupDocument document) =>
        JsonSerializer.SerializeToUtf8Bytes(document, BackupDocument.JsonOptions);

    /// <summary><c>mirbadllm-backup-&lt;host&gt;-&lt;yyyyMMdd-HHmmss&gt;.json</c>, the timestamp in UTC.</summary>
    public static string FileName(BackupDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return string.Create(CultureInfo.InvariantCulture,
            $"mirbadllm-backup-{SafeHostName(document.HostName)}-{document.CreatedAt.UtcDateTime:yyyyMMdd-HHmmss}.json");
    }

    /// <summary>Reads and validates a backup file. Never touches the current configuration.</summary>
    /// <remarks>
    /// Reads schema versions 1 and 2. A version-1 file has no multi-node sections (anything a hand-edited file carries
    /// there is dropped) and no placements; a version-2 file's nodes, master and per-node Services lists are validated.
    /// </remarks>
    /// <exception cref="BackupFormatException">Not JSON, not a mirbadllm backup, another schema version, or invalid content.</exception>
    public static BackupDocument Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaxFileBytes)
        {
            throw new BackupFormatException($"The file is larger than {MaxFileBytes / (1024 * 1024)} MiB; a mirbadllm backup is much smaller.");
        }

        JsonDocument json;
        try
        {
            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            json = JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException ex)
        {
            throw new BackupFormatException($"The file is not valid JSON ({ex.Message}).", ex);
        }

        using (json)
        {
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(json.RootElement, "schemaVersion", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number)
            {
                throw new BackupFormatException("This is not a mirbadllm backup: it has no schemaVersion.");
            }

            if (!versionElement.TryGetInt32(out int version) || !BackupDocument.ReadableSchemaVersions.Contains(version))
            {
                throw new BackupFormatException(
                    $"This backup uses schema version {versionElement.GetRawText()}; this version of mirbadllm reads versions " +
                    $"{string.Join(" and ", BackupDocument.ReadableSchemaVersions)}.");
            }

            BackupDocument? document;
            try
            {
                document = json.RootElement.Deserialize<BackupDocument>(BackupDocument.JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new BackupFormatException($"The backup's content is not in the expected shape ({ex.Message}).", ex);
            }

            if (document is null || document.Models is null)
            {
                throw new BackupFormatException("The backup has no models list.");
            }

            bool v1 = version == BackupDocument.CurrentSchemaVersion;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var models = new List<ModelDefinition>(document.Models.Count);
            foreach (var model in document.Models)
            {
                if (model is null || string.IsNullOrWhiteSpace(model.Id))
                {
                    throw new BackupFormatException("The backup has a model without an id.");
                }

                if (!ids.Add(model.Id))
                {
                    throw new BackupFormatException($"The backup lists model '{model.Id}' twice.");
                }

                if (model.Placement is { } placement && !v1 && !IsNodeId(placement.NodeId))
                {
                    throw new BackupFormatException($"Model '{model.Id}' is placed on '{placement.NodeId}', which is not a valid node id.");
                }

                models.Add(model with
                {
                    Tags = model.Tags ?? [],
                    Sources = model.Sources ?? [],
                    ExtraArgs = model.ExtraArgs ?? [],
                    Placement = v1 ? null : model.Placement,
                });
            }

            if (document.Secrets is not null)
            {
                BackupCrypto.Validate(document.Secrets);
            }

            if (v1)
            {
                // Version 1 has no multi-node sections; whatever a hand-edited file carries there is not read.
                return document with
                {
                    Models = models,
                    Master = null,
                    Nodes = null,
                    HostServices = document.HostServices is null ? null : document.HostServices with { Nodes = null },
                    RunningModels = null,
                    HostUnits = null,
                };
            }

            return document with
            {
                Models = models,
                Master = ValidateMaster(document.Master),
                Nodes = ValidateNodes(document.Nodes),
                HostServices = ValidateHostServices(document.HostServices),
                RunningModels = ValidatePerNode(document.RunningModels, "running models", id => !string.IsNullOrWhiteSpace(id)),
                HostUnits = ValidatePerNode(document.HostUnits, "managed services",
                    u => u is not null && !string.IsNullOrWhiteSpace(u.Name) && !string.IsNullOrWhiteSpace(u.Unit)),
            };
        }
    }

    /// <summary>The decrypted secrets, or null when the backup has none.</summary>
    /// <remarks>
    /// A CA in the section (<see cref="BackupSecrets.Pki"/>, version 2 only) is checked to be a matching certificate/key
    /// pair before it is returned (design §10.2: every CA load checks the pair).
    /// </remarks>
    /// <exception cref="BackupPassphraseException">Wrong or missing passphrase, or a changed file.</exception>
    /// <exception cref="BackupFormatException">The section decrypted but is not in the expected shape, or its CA is not a valid pair.</exception>
    public static BackupSecrets? DecryptSecrets(BackupDocument document, string? passphrase)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Secrets is null)
        {
            return null;
        }

        byte[] plaintext = BackupCrypto.Decrypt(document.Secrets, passphrase ?? "", document.SchemaVersion);
        try
        {
            var secrets = JsonSerializer.Deserialize<BackupSecrets>(plaintext, BackupDocument.JsonOptions);
            if (secrets is null)
            {
                return new BackupSecrets([]);
            }

            if (document.SchemaVersion == BackupDocument.CurrentSchemaVersion)
            {
                secrets = secrets with { Pki = null };
            }
            else if (secrets.Pki is { } pki)
            {
                ValidateCa(pki);
            }

            return secrets with { ApiKeys = secrets.ApiKeys ?? [] };
        }
        catch (JsonException ex)
        {
            throw new BackupFormatException("The secrets section decrypted but is not in the expected shape.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>The current issuing CA (see the remarks), read once so the certificate and key are one pair.</summary>
    internal CertificateAuthority? CurrentCertificateAuthority => masterCertificates?.Authority ?? certificateAuthority;

    private BackupPkiSecrets? ExportCa() =>
        CurrentCertificateAuthority is not { } ca
            ? null
            : new BackupPkiSecrets { CaCertPem = ca.CertificatePem, CaKeyPem = ca.ExportPrivateKeyPem() };

    /// <summary>The non-secret part of a node record (design §5.5): never join tokens, pending pins or keys.</summary>
    private static BackupNode ToBackupNode(NodeRecord node) => new()
    {
        Id = node.Id,
        Uid = node.Uid,
        DisplayName = node.DisplayName,
        Address = node.Address,
        AltAddresses = [.. node.AltAddresses ?? []],
        Labels = [.. node.Labels ?? []],
        Approved = node.Approved,
        Draining = node.Draining,
        Revoked = node.Revoked,
        MemoryReserveGiB = node.MemoryReserveGiB,
        MaxConcurrentStarts = node.MaxConcurrentStarts,
        GpuReserveGiB = node.GpuReserveGiB is null
            ? new Dictionary<string, double>()
            : new Dictionary<string, double>(node.GpuReserveGiB, StringComparer.Ordinal),
        DefaultVllmImage = node.DefaultVllmImage,
        DefaultVllmImageDigest = node.DefaultVllmImageDigest,
        DefaultLlamaCppImage = node.DefaultLlamaCppImage,
        DefaultLlamaCppImageDigest = node.DefaultLlamaCppImageDigest,
        CertSpkiSha256 = node.CertSpkiSha256,
        CertNotAfter = node.CertNotAfter,
        LastCapabilities = node.LastCapabilities,
    };

    /// <summary>The CA must be a complete, matching pair; the error never quotes the material.</summary>
    private static void ValidateCa(BackupPkiSecrets pki)
    {
        if (string.IsNullOrWhiteSpace(pki.CaCertPem) || string.IsNullOrWhiteSpace(pki.CaKeyPem))
        {
            throw new BackupFormatException("The backup's CA is incomplete: it needs both the certificate and the private key.");
        }

        try
        {
            using var ca = CertificateAuthority.Load(pki.CaCertPem, pki.CaKeyPem);
        }
        catch (Exception ex) when (ex is PkiException or CryptographicException or ArgumentException or FormatException)
        {
            throw new BackupFormatException("The backup's CA is not a valid CA certificate with its matching private key.", ex);
        }
    }

    private static BackupMasterInfo? ValidateMaster(BackupMasterInfo? master)
    {
        if (master is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(master.MasterInstanceId) || master.MasterInstanceId.Contains('/', StringComparison.Ordinal) || master.Epoch < 0)
        {
            throw new BackupFormatException("The backup's master section needs an instance id (without '/') and a non-negative epoch.");
        }

        return master;
    }

    private static List<BackupNode>? ValidateNodes(IReadOnlyList<BackupNode>? nodes)
    {
        if (nodes is null)
        {
            return null;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var uids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<BackupNode>(nodes.Count);
        foreach (var node in nodes)
        {
            if (node is null || !IsNodeId(node.Id))
            {
                throw new BackupFormatException($"The backup has a node without a valid id ({Protocol.NodeIdPattern}).");
            }

            if (!ids.Add(node.Id))
            {
                throw new BackupFormatException($"The backup lists node '{node.Id}' twice.");
            }

            if (string.IsNullOrWhiteSpace(node.Uid) || !uids.Add(node.Uid))
            {
                throw new BackupFormatException($"Node '{node.Id}' has no uid, or shares its uid with another node.");
            }

            if (string.IsNullOrWhiteSpace(node.Address))
            {
                throw new BackupFormatException($"Node '{node.Id}' has no address.");
            }

            result.Add(node with
            {
                AltAddresses = node.AltAddresses ?? [],
                Labels = node.Labels ?? [],
                GpuReserveGiB = node.GpuReserveGiB ?? new Dictionary<string, double>(),
            });
        }

        return result;
    }

    private static HostServicesBackup? ValidateHostServices(HostServicesBackup? services)
    {
        if (services?.Nodes is not { } perNode)
        {
            return services;
        }

        foreach (var (nodeId, lists) in perNode)
        {
            if (!IsNodeId(nodeId) || lists is null)
            {
                throw new BackupFormatException($"The backup's Services lists name '{nodeId}', which is not a valid node id.");
            }
        }

        return services;
    }

    /// <summary>A per-node map of an uploaded file: valid node ids and complete entries only.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<T>>? ValidatePerNode<T>(
        IReadOnlyDictionary<string, IReadOnlyList<T>>? perNode, string what, Func<T, bool> valid)
    {
        if (perNode is null)
        {
            return null;
        }

        foreach (var (nodeId, entries) in perNode)
        {
            if (!IsNodeId(nodeId) || entries is null || !entries.All(valid))
            {
                throw new BackupFormatException($"The backup's {what} for '{nodeId}' are not valid.");
            }
        }

        return perNode;
    }

    private static bool IsNodeId(string? id) => id is not null && NodeIdRegex().IsMatch(id);

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string SafeHostName(string? host)
    {
        string safe = UnsafeHostChars().Replace((host ?? "").Trim(), "-").Trim('-', '.');
        if (safe.Length > 64)
        {
            safe = safe[..64];
        }

        return safe.Length == 0 ? "host" : safe;
    }

    [GeneratedRegex("[^A-Za-z0-9.-]+")]
    private static partial Regex UnsafeHostChars();

    [GeneratedRegex(Protocol.NodeIdPattern, RegexOptions.CultureInvariant)]
    private static partial Regex NodeIdRegex();
}
