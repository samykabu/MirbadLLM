using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Pki;

namespace MirbadLLM.Orchestration.Recovery;

/// <summary>Where fleet recovery is (design §9.4 recovery mode; the Restore tab shows it).</summary>
public enum FleetRecoveryStage
{
    /// <summary>The master runs normally: fleet recovery is not available (configuration import is).</summary>
    NotInRecoveryMode,

    /// <summary>Recovery mode: waiting for a version-2 backup with its CA and passphrase.</summary>
    AwaitingBackup,

    /// <summary>The backup is being installed into the data directory.</summary>
    Installing,

    /// <summary>Installed; the marker is gone and the master restarts into normal mode, where the takeover runs.</summary>
    Installed,
}

/// <summary>A snapshot of <see cref="FleetRecovery"/> for the Restore tab.</summary>
/// <param name="Stage">The stage.</param>
/// <param name="Result">What the install wrote, once <see cref="FleetRecoveryStage.Installed"/>.</param>
/// <param name="LastError">Why the last attempt was refused or failed (plain words, never secret material).</param>
public sealed record FleetRecoveryStatus(FleetRecoveryStage Stage, FleetRecoveryResult? Result, string? LastError);

/// <summary>What a fleet recovery installed (design §9.4 items 1–3).</summary>
/// <param name="MasterInstanceId">The restored master identity (from the backup).</param>
/// <param name="Epoch">The restored epoch; the next boot increments it, then the takeover moves above every agent's.</param>
/// <param name="Nodes">Nodes restored with uid, pins, addresses and approved/draining/revoked state.</param>
/// <param name="Models">Model definitions restored with their placements.</param>
/// <param name="ApiKeys">API key records restored.</param>
/// <param name="ServiceContainers">Managed containers restored into the bootstrap node's Services list.</param>
/// <param name="CaWrittenToDisk">False when the CA comes from configuration (it is then never copied to disk, D63).</param>
/// <param name="Notes">Per-node and per-model notes: expired certificates to re-pair, skipped definitions, lists not restored.</param>
public sealed record FleetRecoveryResult(
    string MasterInstanceId,
    long Epoch,
    int Nodes,
    int Models,
    int ApiKeys,
    int ServiceContainers,
    bool CaWrittenToDisk,
    IReadOnlyList<string> Notes);

/// <summary>
/// A fleet recovery refusal with its admin-API code (<see cref="ErrorCodes.MasterNotEmpty"/> 409,
/// <see cref="ErrorCodes.BackupUnsuitable"/> 400, <see cref="ErrorCodes.InvalidState"/> 409 outside recovery mode).
/// The message never quotes key material.
/// </summary>
public sealed class FleetRecoveryException : Exception
{
    public FleetRecoveryException()
        : this(ErrorCodes.InvalidState, "Fleet recovery was refused.")
    {
    }

    public FleetRecoveryException(string message)
        : this(ErrorCodes.InvalidState, message)
    {
    }

    public FleetRecoveryException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = ErrorCodes.InvalidState;
    }

    public FleetRecoveryException(string code, string message, IReadOnlyList<string>? blockers = null)
        : base(message)
    {
        Code = code;
        Blockers = blockers ?? [];
    }

    /// <summary>The <see cref="ErrorCodes"/> value for the ProblemDetails <c>code</c>.</summary>
    public string Code { get; }

    /// <summary>HTTP status for <see cref="Code"/> (<see cref="ErrorCodes.ApiStatus"/>).</summary>
    public int Status => ErrorCodes.ApiStatus(Code);

    /// <summary>For <see cref="ErrorCodes.MasterNotEmpty"/>: what makes the master non-empty.</summary>
    public IReadOnlyList<string> Blockers { get; } = [];
}

/// <summary>
/// <c>{DataPath}/recovery-installed.json</c>: written when a fleet recovery finished installing, so the option
/// <c>MirbadLLM__Recovery__Mode=FleetRestore</c> left set does not start recovery mode again (it is one-shot, like the
/// takeover option, D80), and the marker the install could not delete is recognised as stale.
/// </summary>
public sealed record FleetRecoveryRecord
{
    /// <summary>File name under the data path.</summary>
    public const string FileName = "recovery-installed.json";

    /// <summary>When the install finished.</summary>
    public required DateTimeOffset InstalledAt { get; init; }

    /// <summary>The backup's creation time.</summary>
    public DateTimeOffset BackupCreatedAt { get; init; }

    /// <summary>The host the backup was made on.</summary>
    public string BackupHostName { get; init; } = "";

    /// <summary>The restored master instance id.</summary>
    public required string MasterInstanceId { get; init; }

    /// <summary>The restored epoch.</summary>
    public long Epoch { get; init; }
}

/// <summary>
/// Fleet disaster recovery (design §9.4, <see cref="RestoreMode.FleetRecovery"/>, MN-113): on an <b>empty</b> master
/// started in recovery mode, installs a version-2 backup with its CA — <c>master.json</c> (the backup's instance id and
/// epoch), <c>pki/</c> (the backup's CA), <c>nodes.json</c> (uid, pins, addresses, approved/draining/revoked),
/// <c>models.json</c> (v2, placements), <c>apikeys.json</c>, <c>host-services.json</c> and every other node's <c>host-services.{nodeId}.json</c>,
/// and when the backup has them the running intent (<c>running-models.json</c>, seeded so the models come back), the
/// agent-update setting, the benchmark/tuning history and the backup destination's kept settings — before normal registry and PKI
/// initialization, requests the takeover (<c>{DataPath}/takeover</c>), removes the recovery marker and asks the host to
/// restart into normal mode, where the operator confirms the takeover (design §4.2, MN-115).
/// </summary>
/// <remarks>
/// <para><b>State machine.</b> <see cref="FleetRecoveryStage.NotInRecoveryMode"/> (normal boot: refused with
/// <c>invalid_state</c>) · <see cref="FleetRecoveryStage.AwaitingBackup"/> → <see cref="FleetRecoveryStage.Installing"/> →
/// <see cref="FleetRecoveryStage.Installed"/> (restart pending), or back to <see cref="FleetRecoveryStage.AwaitingBackup"/>
/// with <see cref="FleetRecoveryStatus.LastError"/> on a refusal or failure (nothing in the data directory changed).</para>
/// <para><b>Crash safety.</b> Everything is written into <c>{DataPath}/recovery-staging/</c> with the real stores, then a
/// <c>.complete</c> marker, then moved into place; <see cref="PrepareBoot"/> (run once by the composition before anything
/// touches the data path) finishes a complete staging left by a crash and discards an incomplete one, so an install is
/// either fully in place or not at all.</para>
/// <para><b>Provisional material</b> (a normal boot before the restore) is replaced: the seed model, a CA and master
/// certificates that never issued an agent certificate (the whole <c>pki/</c> directory), an unused bootstrap
/// token/bundle and the not-yet-enrolled bootstrap node record (<c>nodes.json</c>), empty key and Services stores. Data
/// Protection keys are kept (users log in again anyway). Anything else blocks: <see cref="CheckEmptinessAsync"/>.</para>
/// <para>Composition (MN-130): read <see cref="PrepareBoot"/> once, register the restricted host when it returns
/// <see cref="RecoveryMode.FleetRestore"/> (no seeding — <see cref="ModelSeedPolicy.None"/> —, no CA generation, bootstrap
/// node, auto-start, scheduler or agent connections; <see cref="RecoveryMaintenanceWindow"/> as <see cref="IMaintenanceMode"/>
/// so <c>/v1</c> answers 503 "restoring"), register this class with a restart callback
/// (<c>IHostApplicationLifetime.StopApplication</c>) and keep the master lock (<c>master.lock</c>) held.</para>
/// </remarks>
public sealed partial class FleetRecovery : IDisposable
{
    /// <summary>Staging directory under the data path.</summary>
    public const string StagingDirectoryName = "recovery-staging";

    /// <summary>Written last into the staging directory: everything before it is complete.</summary>
    public const string StagingCompleteFileName = ".complete";

    private const string ApiKeysFileName = "apikeys.json";
    private const string HostServicesFileName = "host-services.json";

    private readonly string _dataPath;
    private readonly string _bootstrapNodeId;
    private readonly MasterPkiOptions _pki;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IAuditLog _audit;
    private readonly Action? _requestRestart;
    private readonly IDataProtectionProvider? _protection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateGate = new();
    private FleetRecoveryStatus _status;

    /// <param name="options">The data path.</param>
    /// <param name="recovery">The recovery option; with the marker it decides <see cref="Mode"/> (read once, here).</param>
    /// <param name="pki">A CA supplied by configuration (topology b): it must equal the backup's and is never written to disk.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger (never given secret material).</param>
    /// <param name="audit">Audit log; null = <see cref="NullAuditLog"/>.</param>
    /// <param name="bootstrapNodeId">Node whose Services list lives in <c>host-services.json</c>; null = <c>dgx</c>.</param>
    /// <param name="requestRestart">Called after a successful install (the host stops; its restart policy starts it in normal mode).</param>
    public FleetRecovery(
        IOptions<MirbadLLMOptions> options,
        IOptions<RecoveryOptions>? recovery,
        IOptions<MasterPkiOptions>? pki,
        TimeProvider timeProvider,
        ILogger<FleetRecovery>? logger = null,
        IAuditLog? audit = null,
        string? bootstrapNodeId = null,
        Action? requestRestart = null,
        IDataProtectionProvider? protection = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Value.DataPath, nameof(options));
        _dataPath = Path.GetFullPath(options.Value.DataPath);
        _bootstrapNodeId = string.IsNullOrWhiteSpace(bootstrapNodeId) ? RegistryMigrationOptions.DefaultBootstrapNodeId : bootstrapNodeId.Trim();
        _pki = pki?.Value ?? new MasterPkiOptions();
        _time = timeProvider;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _audit = audit ?? NullAuditLog.Instance;
        _requestRestart = requestRestart;
        _protection = protection;
        Mode = EffectiveMode(recovery?.Value, _dataPath);
        _status = new FleetRecoveryStatus(
            Mode == RecoveryMode.FleetRestore ? FleetRecoveryStage.AwaitingBackup : FleetRecoveryStage.NotInRecoveryMode, null, null);
    }

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    /// <summary>Raised (on the installing thread) whenever <see cref="Status"/> changes.</summary>
    public event Action<FleetRecoveryStatus>? Changed;

    /// <summary>How this master booted (read once at construction).</summary>
    public RecoveryMode Mode { get; }

    /// <summary>True when the master runs in recovery mode, so fleet recovery may run.</summary>
    public bool IsActive => Mode == RecoveryMode.FleetRestore;

    /// <summary>The current stage.</summary>
    public FleetRecoveryStatus Status
    {
        get
        {
            lock (_stateGate)
            {
                return _status;
            }
        }
    }

    /// <summary>
    /// Run once at startup, before anything touches <paramref name="dataPath"/> (MN-130): finishes an install whose staging
    /// is complete, discards an incomplete staging, drops a recovery marker left behind by a finished install, and returns
    /// the mode to boot in. <see cref="RecoveryOptions.Mode"/> counts only until an install finished on this volume
    /// (<see cref="FleetRecoveryRecord"/>); a marker created after that install starts recovery mode again (and fleet
    /// recovery then refuses, the master no longer being empty).
    /// </summary>
    public static RecoveryMode PrepareBoot(RecoveryOptions? options, string dataPath, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        ILogger log = logger ?? NullLogger.Instance;
        string data = Path.GetFullPath(dataPath);
        string staging = Path.Combine(data, StagingDirectoryName);
        if (File.Exists(Path.Combine(staging, StagingCompleteFileName)))
        {
            Commit(data);
            Log.ResumedInstall(log, data);
        }
        else if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
            Log.DiscardedStaging(log, staging);
        }

        string marker = Path.Combine(data, RecoveryOptions.MarkerFileName);
        if (ReadRecord(data) is { } record && File.Exists(marker) && File.GetLastWriteTimeUtc(marker) <= record.InstalledAt.UtcDateTime)
        {
            File.Delete(marker);
        }

        var mode = EffectiveMode(options, data);
        if (mode == RecoveryMode.None && options?.Mode == RecoveryMode.FleetRestore)
        {
            Log.OptionIgnored(log, data);
        }

        return mode;
    }

    /// <summary>
    /// The effective mode: <see cref="RecoveryOptions.Effective"/>, except that the option no longer counts once an install
    /// finished on this volume (only a marker newer than that install does).
    /// </summary>
    public static RecoveryMode EffectiveMode(RecoveryOptions? options, string dataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        if (ReadRecord(dataPath) is not { } record)
        {
            return RecoveryOptions.Effective(options, dataPath);
        }

        string marker = Path.Combine(dataPath, RecoveryOptions.MarkerFileName);
        return File.Exists(marker) && File.GetLastWriteTimeUtc(marker) > record.InstalledAt.UtcDateTime
            ? RecoveryMode.FleetRestore
            : RecoveryMode.None;
    }

    /// <summary>
    /// Whether the data directory is empty enough for fleet recovery (design §9.4 provisional material). Blocks: any node
    /// but a not-yet-enrolled bootstrap node, an enrolled or revoked bootstrap node (an agent certificate was issued), a used
    /// join token, any model but the unchanged seed model, any API key, and a state file that cannot be read.
    /// </summary>
    public async Task<MasterEmptiness> CheckEmptinessAsync(CancellationToken ct = default)
    {
        var blockers = new List<string>();
        var data = Options.Create(new MirbadLLMOptions { DataPath = _dataPath });

        try
        {
            using var nodes = new JsonNodeRegistry(data, _time);
            foreach (var node in await nodes.ListAsync(ct).ConfigureAwait(false))
            {
                if (!string.Equals(node.Id, _bootstrapNodeId, StringComparison.Ordinal))
                {
                    blockers.Add($"Node '{node.Id}' is registered here.");
                }
                else if (node.Revoked || node.CertSpkiSha256 is not null || node.PendingCertSpkiSha256 is not null || node.EnrolledAt is not null)
                {
                    blockers.Add($"The bootstrap node '{node.Id}' enrolled here (an agent certificate was issued).");
                }
            }

            foreach (var token in await nodes.ListJoinTokensAsync(ct).ConfigureAwait(false))
            {
                if (token.UsedAt is not null)
                {
                    blockers.Add($"A join token for '{token.NodeId}' was used.");
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            blockers.Add($"{NodeRegistryDocument.FileName} cannot be read ({ex.Message}).");
        }

        try
        {
            using var models = new JsonModelRegistry(
                data, Options.Create(new RegistryMigrationOptions { Nodes = { BootstrapNodeId = _bootstrapNodeId } }), validators: null,
                Options.Create(new ModelRegistryOptions { SeedPolicy = ModelSeedPolicy.None }));
            foreach (var model in await models.ListAsync(ct).ConfigureAwait(false))
            {
                if (!IsUnchangedSeed(model))
                {
                    blockers.Add($"Model '{model.Id}' is defined here.");
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            blockers.Add($"{ModelRegistryDocument.FileName} cannot be read ({ex.Message}).");
        }

        try
        {
            using var keys = new JsonApiKeyStore(data, _time, NullLogger<JsonApiKeyStore>.Instance);
            int count = (await keys.ListAsync(ct).ConfigureAwait(false)).Count;
            if (count > 0)
            {
                blockers.Add($"{count} API key{(count == 1 ? " exists" : "s exist")} here.");
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            blockers.Add($"{ApiKeysFileName} cannot be read ({ex.Message}).");
        }

        return new MasterEmptiness(blockers.Count == 0, blockers);
    }

    /// <summary>
    /// Design §9.4 item 1: a version-2 backup with the master and nodes sections, the passphrase (decrypted
    /// <paramref name="secrets"/>) and the <c>pki</c> secret section.
    /// </summary>
    /// <exception cref="FleetRecoveryException"><see cref="ErrorCodes.BackupUnsuitable"/>.</exception>
    public static void ValidateBackup(BackupDocument backup, BackupSecrets? secrets)
    {
        ArgumentNullException.ThrowIfNull(backup);
        string? problem =
            backup.SchemaVersion != BackupDocument.MultiNodeSchemaVersion
                ? $"Fleet recovery needs a version-{BackupDocument.MultiNodeSchemaVersion} backup; this file is version {backup.SchemaVersion} (use configuration import)."
            : backup.Master is null ? "The backup has no master section (it was made without the master identity)."
            : backup.Nodes is null ? "The backup has no nodes section (it was made without the node registry)."
            : backup.Secrets is null ? "The backup was made without a passphrase, so it holds no CA."
            : secrets is null ? "Enter the backup's passphrase: fleet recovery needs the CA in its encrypted section."
            : secrets.Pki is null ? "The backup's encrypted section holds no CA (it was made with MirbadLLM:Backup:IncludeCa=false)."
            : null;
        if (problem is not null)
        {
            throw new FleetRecoveryException(ErrorCodes.BackupUnsuitable, problem);
        }
    }

    /// <summary>
    /// Installs <paramref name="backup"/> (design §9.4 items 1–3): refuses outside recovery mode, on an unsuitable backup,
    /// on a CA that differs from a configured one, and on a non-empty master; otherwise stages, commits, requests the
    /// takeover, removes the marker and calls the restart callback.
    /// </summary>
    /// <param name="backup">Parsed backup (<see cref="BackupService.Parse"/>).</param>
    /// <param name="secrets">Its decrypted secrets (<see cref="BackupService.DecryptSecrets"/>).</param>
    /// <param name="actor">Audit actor (the admin session); null = <see cref="AuditActors.SystemRestore"/>.</param>
    /// <exception cref="FleetRecoveryException">Refused; nothing in the data directory changed.</exception>
    public async Task<FleetRecoveryResult> InstallAsync(BackupDocument backup, BackupSecrets? secrets, string? actor = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new FleetRecoveryException(ErrorCodes.InvalidState, "A fleet recovery is already being installed.");
        }

        try
        {
            if (!IsActive)
            {
                throw await RefuseAsync(actor, new FleetRecoveryException(ErrorCodes.InvalidState,
                    "Fleet recovery runs only in recovery mode, on an empty data volume: start the master with " +
                    "MirbadLLM__Recovery__Mode=FleetRestore or an empty 'recovery' file in its data directory.")).ConfigureAwait(false);
            }

            if (Status.Stage == FleetRecoveryStage.Installed)
            {
                throw new FleetRecoveryException(ErrorCodes.InvalidState, "The backup is already installed; the master is restarting into normal mode.");
            }

            try
            {
                ValidateBackup(backup, secrets);
                CheckConfiguredCa(secrets!.Pki!);
            }
            catch (FleetRecoveryException ex)
            {
                throw await RefuseAsync(actor, ex).ConfigureAwait(false);
            }

            var emptiness = await CheckEmptinessAsync(ct).ConfigureAwait(false);
            if (!emptiness.IsEmpty)
            {
                throw await RefuseAsync(actor, new FleetRecoveryException(ErrorCodes.MasterNotEmpty,
                    "This master is not empty, so fleet recovery would merge two fleets: " + string.Join(" ", emptiness.Blockers) +
                    " Use configuration import, or start from an empty data volume.", emptiness.Blockers)).ConfigureAwait(false);
            }

            SetStatus(new FleetRecoveryStatus(FleetRecoveryStage.Installing, null, null));
            Log.Installing(_logger, backup.Master!.MasterInstanceId, backup.Master.Epoch, backup.Nodes!.Count, backup.Models.Count);
            FleetRecoveryResult result;
            try
            {
                result = await StageAsync(backup, secrets!, ct).ConfigureAwait(false);
                Commit(_dataPath);
                WriteRecord(_dataPath, new FleetRecoveryRecord
                {
                    InstalledAt = _time.GetUtcNow(),
                    BackupCreatedAt = backup.CreatedAt,
                    BackupHostName = backup.HostName,
                    MasterInstanceId = result.MasterInstanceId,
                    Epoch = result.Epoch,
                });
                File.Delete(Path.Combine(_dataPath, RecoveryOptions.MarkerFileName));
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not FleetRecoveryException)
            {
                DiscardIncompleteStaging();
                Log.InstallFailed(_logger, ex);
                SetStatus(new FleetRecoveryStatus(FleetRecoveryStage.AwaitingBackup, null, $"The install failed: {ex.Message}"));
                await WriteAuditAsync(actor, AuditResults.Failed, null, "fleet recovery failed").ConfigureAwait(false);
                throw;
            }
            catch (FleetRecoveryException ex)
            {
                // A backup node the registry refuses: nothing was committed.
                DiscardIncompleteStaging();
                SetStatus(new FleetRecoveryStatus(FleetRecoveryStage.AwaitingBackup, null, null));
                throw await RefuseAsync(actor, ex).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                DiscardIncompleteStaging();
                SetStatus(new FleetRecoveryStatus(FleetRecoveryStage.AwaitingBackup, null, null));
                throw;
            }

            SetStatus(new FleetRecoveryStatus(FleetRecoveryStage.Installed, result, null));
            Log.Installed(_logger, result.MasterInstanceId, result.Epoch, result.Nodes, result.Models);
            await WriteAuditAsync(actor, AuditResults.Succeeded, null,
                $"fleet recovery: {result.Nodes} node(s), {result.Models} model(s), epoch {result.Epoch}").ConfigureAwait(false);
            _requestRestart?.Invoke();
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The seed model as a normal first boot writes it (any placement): provisional, replaced by the restore.</summary>
    internal static bool IsUnchangedSeed(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!string.Equals(model.Id, JsonModelRegistry.SeedModel.Id, StringComparison.Ordinal))
        {
            return false;
        }

        var seed = VllmArgsNormalizer.Apply(JsonModelRegistry.SeedModel with { Placement = model.Placement });
        return RestorePlanner.ChangedFields(seed, VllmArgsNormalizer.Apply(model)).Count == 0;
    }

    /// <summary>Design §9.4 item 2: a CA supplied by configuration must be the backup's CA; the message names both fingerprints.</summary>
    private void CheckConfiguredCa(BackupPkiSecrets backupCa)
    {
        if (string.IsNullOrWhiteSpace(_pki.CaCertPem) && string.IsNullOrWhiteSpace(_pki.CaKeyPem))
        {
            return;
        }

        using var restored = CertificateAuthority.Load(backupCa.CaCertPem, backupCa.CaKeyPem, _time);
        CertificateAuthority configured;
        try
        {
            configured = CertificateAuthority.Load(_pki.CaCertPem ?? "", _pki.CaKeyPem ?? "", _time);
        }
        catch (Exception ex) when (ex is PkiException or CryptographicException or ArgumentException or FormatException)
        {
            throw new FleetRecoveryException(ErrorCodes.BackupUnsuitable,
                "The CA supplied by configuration (MirbadLLM__Pki__CaCertPem/CaKeyPem) is not a valid certificate and key pair.");
        }

        using (configured)
        {
            if (!configured.Certificate.RawData.AsSpan().SequenceEqual(restored.Certificate.RawData))
            {
                throw new FleetRecoveryException(ErrorCodes.BackupUnsuitable,
                    $"The backup's CA ({Spki.ToDisplayFingerprint(restored.SpkiSha256)}) differs from the CA supplied by configuration " +
                    $"({Spki.ToDisplayFingerprint(configured.SpkiSha256)}). Remove one of them or restore the matching backup.");
            }
        }
    }

    /// <summary>Writes every restored file into the staging directory with the real stores, then the completion marker.</summary>
    private async Task<FleetRecoveryResult> StageAsync(BackupDocument backup, BackupSecrets secrets, CancellationToken ct)
    {
        string staging = Path.Combine(_dataPath, StagingDirectoryName);
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);
        var stagingOptions = Options.Create(new MirbadLLMOptions { DataPath = staging });
        DateTimeOffset now = _time.GetUtcNow();
        var notes = new List<string>();
        var master = backup.Master!;

        // nodes.json: identity, addresses, operator settings and security state (pins, approved/draining/revoked), never tokens.
        int nodeCount = 0;
        using (var nodes = new JsonNodeRegistry(stagingOptions, _time))
        {
            foreach (var node in backup.Nodes!)
            {
                try
                {
                    await nodes.UpsertAsync(ToNodeRecord(node, now), ct).ConfigureAwait(false);
                }
                catch (ArgumentException ex)
                {
                    throw new FleetRecoveryException(ErrorCodes.BackupUnsuitable, $"Node '{node.Id}' in the backup is not valid: {ex.Message}");
                }

                nodeCount++;
                if (node.Revoked)
                {
                    continue;
                }

                if (node.CertSpkiSha256 is null)
                {
                    notes.Add($"Node '{node.Id}' had not enrolled when the backup was made: give it a join token from the Nodes page.");
                }
                else if (node.CertNotAfter is { } notAfter && notAfter <= now)
                {
                    notes.Add($"Node '{node.Id}': its certificate expired on {notAfter.UtcDateTime:yyyy-MM-dd}, so it cannot reattach. " +
                        "Re-pair it with 'mirbadllm-agent join --rekey' (it keeps its models and state; only the key and certificate change).");
                }
            }
        }

        WriteIfMissing(Path.Combine(staging, NodeRegistryDocument.FileName), new NodeRegistryDocument());

        // models.json (v2): definitions with their placements as backed up (the nodes come with them). No validators run:
        // the placements belong to the restored nodes, which this master has not contacted yet.
        int modelCount = 0;
        using (var models = new JsonModelRegistry(
            stagingOptions, Options.Create(new RegistryMigrationOptions { Nodes = { BootstrapNodeId = _bootstrapNodeId } }), validators: null,
            Options.Create(new ModelRegistryOptions { SeedPolicy = ModelSeedPolicy.None })))
        {
            foreach (var model in backup.Models)
            {
                try
                {
                    await models.UpsertAsync(model, ct).ConfigureAwait(false);
                    modelCount++;
                }
                catch (ArgumentException ex)
                {
                    notes.Add($"Model '{model.Id}' was not restored: {ex.Message}");
                }
            }
        }

        WriteIfMissing(Path.Combine(staging, ModelRegistryDocument.FileName), new ModelRegistryDocument());

        // apikeys.json: the key records (hashes only) from the encrypted section.
        int keyCount = 0;
        if (secrets.ApiKeys.Count > 0)
        {
            using var keys = new JsonApiKeyStore(stagingOptions, _time, NullLogger<JsonApiKeyStore>.Instance);
            keyCount = (await keys.ImportAsync(secrets.ApiKeys, ct).ConfigureAwait(false)).Added;
        }

        // tokens.json: saved tokens, encrypted with this master's Data Protection keys, which recovery keeps.
        if (secrets.Tokens is { Count: > 0 } savedTokens)
        {
            if (_protection is null)
            {
                notes.Add($"{savedTokens.Count} saved token(s) were not restored: add them again under Access > Tokens.");
            }
            else
            {
                using var tokenStore = new JsonTokenStore(stagingOptions, _protection, _time);
                int added = (await tokenStore.ImportAsync(savedTokens, ct).ConfigureAwait(false)).Added;
                notes.Add($"Saved tokens restored: {added}.");
            }
        }

        // host-services.json holds the bootstrap node's lists; other nodes' lists have no store on the master yet.
        int containers = 0;
        if (backup.HostServicesFor(_bootstrapNodeId) is { } lists)
        {
            await KeepReplacedServicesListAsync(staging, notes, ct).ConfigureAwait(false);
            using var services = new HostServicesStore(Path.Combine(staging, HostServicesFileName));
            await services.ImportAsync(lists, ct).ConfigureAwait(false);
            containers = lists.Containers?.Count ?? 0;
        }

        // Every other node's lists go to its host-services.{nodeId}.json (MN-120's per-node Services page; W7 review MN-126):
        // fleet recovery restores the whole fleet, so the node ids are the backup's own. Lists of a node the backup does not
        // list (a hand-edited file) are not restored.
        foreach (var (nodeId, nodeLists) in (backup.HostServices?.Nodes ?? new Dictionary<string, HostServicesBackup>()).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (nodeId == _bootstrapNodeId || nodeLists is null)
            {
                continue;
            }

            if (!backup.Nodes!.Any(n => n.Id == nodeId))
            {
                notes.Add($"The Services lists of '{nodeId}' were not restored: the backup has no node '{nodeId}'.");
                continue;
            }

            int count = await NodeHostServicesFiles.ImportAsync(staging, nodeId, nodeLists, ct).ConfigureAwait(false);
            notes.Add($"Services of node '{nodeId}': {count} managed container{(count == 1 ? "" : "s")} restored.");
        }

        // running-models.json: what was running, seeded under a boot id no agent reports, so each node's first snapshot after
        // the restart queues the models it no longer runs (ModelRecoveryState), as after a node restart.
        if (backup.RunningModels is { Count: > 0 } running)
        {
            var seeded = running
                .Select(kv => (kv.Key, Node: backup.Nodes!.FirstOrDefault(n => n.Id == kv.Key && !n.Revoked), Models: kv.Value))
                .Where(r => r.Node is not null)
                .Select(r => (NodeId: r.Key, NodeUid: r.Node!.Uid, r.Models))
                .ToList();
            ModelRecoveryState.WriteSeed(staging, seeded);
            notes.Add($"{seeded.Sum(s => s.Models.Count)} model(s) that were running come back on their nodes after the restart.");
        }

        if (backup.AgentUpdatesAutomatic is bool automatic)
        {
            new AgentUpdateSettingsStore(Path.Combine(staging, AgentUpdateSettingsStore.FileName)).SetAutomatic(automatic);
        }

        // benchmarks/, tuning/ and startup-times.json: the history, written by the real stores' rules.
        if (backup.History is { } history)
        {
            int runs = await BackupHistoryFiles.ImportAsync(staging, history, ct).ConfigureAwait(false);
            new StartupTimeTracker(stagingOptions).ImportMissing(history.StartupTimes ?? new Dictionary<string, double>());
            notes.Add($"Benchmark and tuning results restored: {runs}.");
        }

        // backup-destination.json: the share's settings and password, encrypted with this master's Data Protection keys (kept
        // by recovery), so later backups carry them again. The share stays mounted by the bootstrap node's host agent.
        if (secrets.BackupDestination is { } destination && _protection is not null)
        {
            new BackupDestinationSecretStore(staging, _protection).Save(destination);
            notes.Add($"Backup destination //{destination.Server}/{destination.Share} kept; if it is not mounted after the restart, save it again on the Back up tab.");
        }

        // master.json: the backup's identity and epoch; the next boot increments it and the takeover moves above the agents'.
        WriteJson(Path.Combine(staging, MasterStateDocument.FileName), new MasterStateDocument
        {
            MasterInstanceId = master.MasterInstanceId,
            Epoch = master.Epoch,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await File.WriteAllBytesAsync(Path.Combine(staging, MasterStateDocument.TakeoverFlagFileName), [], ct).ConfigureAwait(false);

        // pki/: the backup's CA, unless configuration supplies it (equal, checked above; never copied to disk, D63). The
        // staged directory replaces the provisional one whole, so its master certificates are re-issued from this CA.
        string pkiStaging = MasterPkiOptions.DirectoryOf(staging);
        PkiFiles.EnsureDirectory(pkiStaging);
        bool caToDisk = string.IsNullOrWhiteSpace(_pki.CaCertPem);
        if (caToDisk)
        {
            PkiFiles.WriteSecret(Path.Combine(pkiStaging, CaStore.KeyFileName), secrets.Pki!.CaKeyPem);
            PkiFiles.WritePublic(Path.Combine(pkiStaging, CaStore.CertificateFileName), secrets.Pki.CaCertPem);
        }

        await File.WriteAllBytesAsync(Path.Combine(staging, StagingCompleteFileName), [], ct).ConfigureAwait(false);
        return new FleetRecoveryResult(master.MasterInstanceId, master.Epoch, nodeCount, modelCount, keyCount, containers, caToDisk, notes);
    }

    /// <summary>File the replaced <c>host-services.json</c> of a first normal boot is kept as (W7 review MN-126).</summary>
    public const string ReplacedServicesFileName = "host-services.json.pre-recovery";

    /// <summary>
    /// <c>host-services.json</c> is not a blocker of <see cref="CheckEmptinessAsync"/> (a first normal boot seeds it with the
    /// default containers as soon as the Services page opens), but an operator may have edited it then. Rather than
    /// discard that, a file with a container list or remembered restart policies is copied into the staging as
    /// <see cref="ReplacedServicesFileName"/>, so the commit keeps it next to the restored list, and noted.
    /// </summary>
    private async Task KeepReplacedServicesListAsync(string staging, List<string> notes, CancellationToken ct)
    {
        string current = Path.Combine(_dataPath, HostServicesFileName);
        if (!File.Exists(current))
        {
            return;
        }

        HostServicesBackup existing;
        try
        {
            using var store = new HostServicesStore(current);
            existing = await store.ExportAsync(ct).ConfigureAwait(false);
        }
        catch (HostServiceException)
        {
            existing = new HostServicesBackup(null, []); // unreadable: keep the bytes too
        }

        if (existing.Containers is null && existing.RestartPolicies is not { Count: > 0 })
        {
            return; // nothing in it
        }

        File.Copy(current, Path.Combine(staging, ReplacedServicesFileName), overwrite: true);
        notes.Add($"The Services list that was here is replaced by the backup's; the old one is kept as {ReplacedServicesFileName}.");
    }

    /// <summary>
    /// Moves a complete staging into the data path (idempotent, so a crash part-way is finished by <see cref="PrepareBoot"/>):
    /// the staged <c>pki/</c> replaces the provisional one whole, every staged file replaces its namesake, then the staging
    /// directory goes.
    /// </summary>
    private static void Commit(string dataPath)
    {
        string staging = Path.Combine(dataPath, StagingDirectoryName);
        string stagedPki = MasterPkiOptions.DirectoryOf(staging);
        string pki = MasterPkiOptions.DirectoryOf(dataPath);
        if (Directory.Exists(stagedPki))
        {
            if (Directory.Exists(pki))
            {
                Directory.Delete(pki, recursive: true);
            }

            Directory.Move(stagedPki, pki);
        }

        foreach (string file in Directory.EnumerateFiles(staging))
        {
            string name = Path.GetFileName(file);
            if (name != StagingCompleteFileName)
            {
                File.Move(file, Path.Combine(dataPath, name), overwrite: true);
            }
        }

        // benchmarks/ and tuning/ (one folder per model): merged file by file, so a re-run after a crash finishes the move.
        foreach (string file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(staging, file);
            if (relative.StartsWith("benchmarks" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith("tuning" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                string target = Path.Combine(dataPath, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file, target, overwrite: true);
            }
        }

        DirectorySync.TryFlushParent(Path.Combine(dataPath, MasterStateDocument.FileName));
        Directory.Delete(staging, recursive: true);
    }

    private void DiscardIncompleteStaging()
    {
        string staging = Path.Combine(_dataPath, StagingDirectoryName);
        try
        {
            if (Directory.Exists(staging) && !File.Exists(Path.Combine(staging, StagingCompleteFileName)))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
        catch (IOException)
        {
            // PrepareBoot discards it on the next start.
        }
    }

    private static NodeRecord ToNodeRecord(BackupNode node, DateTimeOffset now) => new()
    {
        Id = node.Id,
        Uid = node.Uid,
        DisplayName = node.DisplayName,
        Address = node.Address,
        AltAddresses = [.. node.AltAddresses],
        Labels = [.. node.Labels],
        Approved = node.Approved,
        Draining = node.Draining,
        Revoked = node.Revoked,
        MemoryReserveGiB = node.MemoryReserveGiB,
        GpuReserveGiB = new Dictionary<string, double>(node.GpuReserveGiB, StringComparer.Ordinal),
        MaxConcurrentStarts = node.MaxConcurrentStarts,
        DefaultVllmImage = node.DefaultVllmImage,
        DefaultVllmImageDigest = node.DefaultVllmImageDigest,
        DefaultLlamaCppImage = node.DefaultLlamaCppImage,
        DefaultLlamaCppImageDigest = node.DefaultLlamaCppImageDigest,
        CertSpkiSha256 = node.CertSpkiSha256,
        CertNotAfter = node.CertNotAfter,
        LastCapabilities = node.LastCapabilities,
        CreatedAt = now,
        ApprovedAt = node.Approved ? now : null,
        RevokedAt = node.Revoked ? now : null,
    };

    private static FleetRecoveryRecord? ReadRecord(string dataPath)
    {
        string path = Path.Combine(dataPath, FleetRecoveryRecord.FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<FleetRecoveryRecord>(File.ReadAllBytes(path), MasterStateJson.Options);
        }
        catch (JsonException)
        {
            // An unreadable record is treated as none: the option then counts again, and a non-empty master refuses anyway.
            return null;
        }
    }

    private static void WriteRecord(string dataPath, FleetRecoveryRecord record) =>
        WriteJson(Path.Combine(dataPath, FleetRecoveryRecord.FileName), record);

    private static void WriteIfMissing<T>(string path, T value)
    {
        if (!File.Exists(path))
        {
            WriteJson(path, value);
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, MasterStateJson.Options);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    private async Task<FleetRecoveryException> RefuseAsync(string? actor, FleetRecoveryException refusal)
    {
        SetStatus(Status with { LastError = refusal.Message });
        Log.Refused(_logger, refusal.Code);
        await WriteAuditAsync(actor, AuditResults.Refused, refusal.Code, "fleet recovery refused").ConfigureAwait(false);
        return refusal;
    }

    private Task WriteAuditAsync(string? actor, string result, string? code, string detail) =>
        _audit.WriteAsync(new AuditRecord
        {
            Time = _time.GetUtcNow(),
            Actor = actor ?? AuditActors.SystemRestore,
            Action = AuditActions.Restore,
            Result = result,
            ErrorCode = code,
            Detail = detail,
        }, CancellationToken.None);

    private void SetStatus(FleetRecoveryStatus status)
    {
        lock (_stateGate)
        {
            _status = status;
        }

        Changed?.Invoke(status);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Fleet recovery: installing master {InstanceId} at epoch {Epoch} with {Nodes} node(s) and {Models} model(s).")]
        public static partial void Installing(ILogger logger, string instanceId, long epoch, int nodes, int models);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Fleet recovery: installed master {InstanceId} at epoch {Epoch} ({Nodes} node(s), {Models} model(s)); restarting into normal mode for the takeover.")]
        public static partial void Installed(ILogger logger, string instanceId, long epoch, int nodes, int models);

        [LoggerMessage(Level = LogLevel.Error, Message = "Fleet recovery: the install failed; the data directory was not changed.")]
        public static partial void InstallFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Fleet recovery refused ({Code}).")]
        public static partial void Refused(ILogger logger, string code);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Fleet recovery: finished an install interrupted after staging in {DataPath}.")]
        public static partial void ResumedInstall(ILogger logger, string dataPath);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Fleet recovery: discarded an incomplete staging directory {Path}.")]
        public static partial void DiscardedStaging(ILogger logger, string path);

        [LoggerMessage(Level = LogLevel.Warning, Message = "MirbadLLM__Recovery__Mode=FleetRestore is still set, but a fleet recovery already finished in {DataPath}; starting normally. Remove the option.")]
        public static partial void OptionIgnored(ILogger logger, string dataPath);
    }
}

/// <summary>
/// The global "restoring" maintenance window of recovery mode (design §9.4, D76, D79): registered as
/// <see cref="IMaintenanceMode"/> by the restricted host (MN-130), so every <c>/v1</c> call answers 503 <c>maintenance</c>
/// "mirbadllm is restoring a backup" with no router change. Global (<see cref="MaintenanceState.NodeId"/> null) for as long
/// as the process runs; its estimate rolls forward so <c>Retry-After</c> stays sensible.
/// </summary>
public sealed class RecoveryMaintenanceWindow(TimeProvider timeProvider) : IMaintenanceMode
{
    /// <summary>The window's job id.</summary>
    public const string WindowJobId = "recovery";

    /// <summary>How far ahead the estimate is kept.</summary>
    public static readonly TimeSpan EstimateAhead = TimeSpan.FromMinutes(5);

    private readonly DateTimeOffset _startedAt = timeProvider.GetUtcNow();

    /// <inheritdoc/>
    public MaintenanceState? Current =>
        new(WindowJobId, MaintenanceJobKinds.Restore, "", _startedAt, timeProvider.GetUtcNow() + EstimateAhead, NodeId: null);
}
