using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Pki;

namespace MirbadLLM.Orchestration.Pki;

/// <summary>Tunables of <see cref="CaRotationService"/>.</summary>
public sealed class CaRotationOptions
{
    /// <summary>Flag file in <c>{DataPath}/pki</c> that starts a CA rotation at the next pass (actor <c>config</c>), then is deleted.</summary>
    public const string StartFlagFileName = "rotate-ca";

    /// <summary>Flag file in <c>{DataPath}/pki</c> that closes the open rotation early at the next pass, then is deleted.</summary>
    public const string CloseEarlyFlagFileName = "close-ca-rotation";

    /// <summary>How often the background loop looks for flag files and resumes an open rotation (default 5 minutes).</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>A CA rotation step failed on the master's side; the record keeps its stage and the next pass resumes it.</summary>
public sealed class CaRotationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CaRotationException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public CaRotationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public CaRotationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A CA rotation request that is refused as asked (nothing changed): <see cref="Code"/> is the API error code a caller
/// answers with (409): <see cref="ErrorCodes.InvalidState"/> while a rotation is open, before the re-issue for an early close,
/// or when <c>pki/trust.json</c> needs repair; <see cref="ErrorCodes.NotSupported"/> for a CA that comes from configuration.
/// </summary>
public sealed class CaRotationRefusedException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public CaRotationRefusedException()
        : this(ErrorCodes.InvalidState, "The CA rotation request was refused.")
    {
    }

    /// <summary>Creates the exception with a message (code <see cref="ErrorCodes.InvalidState"/>).</summary>
    public CaRotationRefusedException(string message)
        : this(ErrorCodes.InvalidState, message)
    {
    }

    /// <summary>Creates the exception with a message and its cause (code <see cref="ErrorCodes.InvalidState"/>).</summary>
    public CaRotationRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = ErrorCodes.InvalidState;
    }

    /// <summary>Creates the exception with an API error code and a message.</summary>
    public CaRotationRefusedException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        Code = code;
    }

    /// <summary>The API error code (<see cref="ErrorCodes"/>).</summary>
    public string Code { get; }
}

/// <summary>
/// The CA rotation procedure (design §10.2 "CA rotation", D80; MN-116): replace a leaked master CA with both sides holding
/// both roots in between. The <see cref="CaRotationRecord"/> in <c>pki/trust.json</c> (<see cref="MasterTrustStore"/>) is
/// persisted <b>before</b> each step it names, every step is idempotent, and the record decides where a resumed run
/// continues, so a master crash at any point resumes (<see cref="ResumeAsync"/>, or the background loop):
/// <list type="number">
/// <item><see cref="CaRotationStage.Started"/>: a new CA is generated (<c>ca-next.key</c> 0600 + <c>ca-next.crt</c>) and the
/// record is written with <b>both</b> roots as the master's anchors, so agent certificates under either validate.</item>
/// <item><see cref="CaRotationStage.AgentsTrusting"/>: <c>PUT /pki/trust</c> [old, new] on every participant (a node that
/// does not answer is recorded and retried later). Then the current master client certificate is kept as the previous one
/// (<see cref="MasterCertificates.RetainPreviousClient"/>).</item>
/// <item><see cref="CaRotationStage.MasterReissued"/>: the new CA becomes <c>ca.key</c>/<c>ca.crt</c> (the leaked key is
/// overwritten; <see cref="MasterTrustStore.RecoverCaFiles"/> completes a promotion a crash split), it becomes the issuing
/// authority, and the master client and enroll-listener certificates are re-issued under it. A node that still trusts only
/// the old CA is reached with the previous client certificate (<see cref="AgentClientFactory.SetPreviousMasterClient"/>).</item>
/// <item><see cref="CaRotationStage.AgentsRotating"/>: per node: add the trust if still missing, rotate its agent
/// certificate under the new CA (the two-phase rotation of <see cref="CertificateRotationService"/>, which now signs with
/// the new authority), then <b>verify</b> on a fresh connection that validates the agent under the new root only and
/// presents the new master client certificate; only then is the node marked rotated. A node rotated under the old CA (an
/// ordinary renewal that was already open) is rotated again.</item>
/// <item><see cref="CaRotationStage.Closing"/>: once every participant is rotated, the master removes the old root from its
/// anchors, then <c>PUT /pki/trust</c> [new] narrows each agent; the new CA files and the previous client certificate are
/// deleted and the record reaches <see cref="CaRotationStage.Closed"/>. Nodes that were offline keep the rotation open (and
/// the old root trusted on the master) until they rotate; <see cref="CloseEarlyAsync"/> closes anyway
/// (<see cref="CaRotationStage.ClosedEarly"/>) and those nodes must be re-paired (<c>join --rekey</c>).</item>
/// </list>
/// Participants are the non-revoked, enrolled nodes; nodes enrolled meanwhile are added at each pass, revoked or deleted
/// nodes dropped, a re-enrolled node (new uid) starts over. One audit line (<see cref="AuditActions.PkiCaRotation"/>,
/// target = rotation id) per transition: start, each node's trust, re-issue, each node's rotation (also
/// <see cref="AuditActions.NodeRotateCert"/>), narrowing and close; a node failure is written once per new error text.
/// <para>Started manually: <see cref="StartAsync"/> or the flag file <see cref="CaRotationOptions.StartFlagFileName"/> in
/// <c>{DataPath}/pki</c> (a Nodes-page action is P5). Only for a CA kept in <c>{DataPath}/pki</c>; a configured CA (topology
/// b, D63) is refused even when the PKI directory holds the same CA (a volume moved from the DGX): promoting a new CA to
/// disk would make the next start refuse the configured one (<see cref="CaStore"/>'s conflict rule). It is rotated by
/// replacing the secret and re-pairing the nodes. Never logs or audits key material.</para>
/// <para><b>Who follows the new CA</b> (MN-126): enrollment, rekey, join commands, the bootstrap bundle and backups read
/// <see cref="MasterCertificates.Authority"/> per use, so from step 3 on they issue under, pin to and back up the new CA
/// without a restart. A node enrolled or re-paired under the new CA during the rotation already holds what step 4
/// produces: it is recognised on a fresh connection under the new root (presenting the new client certificate) and marked
/// rotated without a trust change or a second rotation. The new CA instance is owned by this service and never disposed
/// (<see cref="MasterCertificates"/> keeps signing with it); the boot CA stays owned by the host.</para>
/// </summary>
public sealed class CaRotationService : BackgroundService
{
    /// <summary>The error a node keeps when the rotation closed early before it rotated.</summary>
    public const string EarlyCloseError = "Not rotated before the rotation was closed early: re-pair it with join --rekey.";

    private readonly INodeRegistry _registry;
    private readonly AgentClientFactory _clients;
    private readonly MasterTrustStore _trust;
    private readonly MasterCertificates _master;
    private readonly CertificateRotationService _rotations;
    private readonly TimeProvider _time;
    private readonly IAuditLog _audit;
    private readonly ILogger _logger;
    private readonly CaRotationOptions _options;
    private readonly bool _configuredCa;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _failedThisPass = new(StringComparer.Ordinal);
    private readonly object _previousGate = new();
    private readonly HashSet<string> _enrolledUnderNewCa = new(StringComparer.Ordinal);
    private CertificateAuthority? _nextCa;
    private bool _initialized;

    /// <summary>Creates the service.</summary>
    /// <param name="registry">Node registry (participants, pins).</param>
    /// <param name="clients">The agent client factory built over <paramref name="trust"/>.</param>
    /// <param name="trust">The master's trust store (<c>pki/trust.json</c>); its directory holds the CA files.</param>
    /// <param name="masterCertificates">The master certificates (issuing authority, re-issue, previous client).</param>
    /// <param name="rotations">The agent-certificate rotation service sharing the same <paramref name="masterCertificates"/>.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="audit">Audit log; null = none.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="options">Tunables.</param>
    /// <param name="pki">The master's PKI options; a CA set there (<c>MirbadLLM:Pki:CaCertPem</c>, topology b) is never rotated here.</param>
    public CaRotationService(
        INodeRegistry registry,
        AgentClientFactory clients,
        MasterTrustStore trust,
        MasterCertificates masterCertificates,
        CertificateRotationService rotations,
        TimeProvider timeProvider,
        IAuditLog? audit = null,
        ILogger<CaRotationService>? logger = null,
        CaRotationOptions? options = null,
        MasterPkiOptions? pki = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(masterCertificates);
        ArgumentNullException.ThrowIfNull(rotations);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _registry = registry;
        _clients = clients;
        _trust = trust;
        _master = masterCertificates;
        _rotations = rotations;
        _time = timeProvider;
        _audit = audit ?? NullAuditLog.Instance;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _options = options ?? new CaRotationOptions();
        _configuredCa = !string.IsNullOrWhiteSpace(pki?.CaCertPem) || !string.IsNullOrWhiteSpace(pki?.CaKeyPem);
    }

    /// <summary>Test hook: awaited at named points (<c>persisted:{stage}</c>, <c>sent:trust</c>, …); a throwing hook simulates a master crash there.</summary>
    internal Func<string, Task>? FaultInjection { get; set; }

    /// <summary>The open or last rotation, or null.</summary>
    public CaRotationRecord? Rotation => _trust.Document.Rotation;

    private string PkiDirectory => _trust.Directory;

    /// <summary>
    /// Restores what a running rotation needs after a master start: the new CA as issuing authority once it was promoted,
    /// and the previous client certificate for the nodes that do not trust the new CA yet. Idempotent; the other entry
    /// points call it first.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await InitializeCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Starts a CA rotation (step 1) and drives it as far as it goes now. Refused (<see cref="InvalidOperationException"/>)
    /// while one is open, and when the CA is not the one in <c>{DataPath}/pki</c> (a configured CA).
    /// </summary>
    public async Task<CaRotationRecord> StartAsync(string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(actor);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await InitializeCoreAsync(ct).ConfigureAwait(false);
            if (Rotation is { } open && MasterTrustStore.IsOpen(open.Stage))
            {
                throw new CaRotationRefusedException($"CA rotation {open.RotationId} is open (stage {open.Stage}); resume or close it first.");
            }

            CertificateAuthority current = _master.Authority;
            string? onDisk = PkiFiles.ReadIfExists(Path.Combine(PkiDirectory, CaStore.CertificateFileName));
            if (_configuredCa || onDisk is null || !SameCertificate(onDisk, current.CertificatePem))
            {
                throw new CaRotationRefusedException(ErrorCodes.NotSupported,
                    "The master CA comes from configuration (MirbadLLM:Pki:CaCertPem, topology b): it is not rotated here. Replace the configured secret with a new CA and re-pair the nodes (join --rekey).");
            }

            if (_trust.Document.Anchors.Count != 1)
            {
                throw new CaRotationRefusedException("The master trusts more than one CA outside a rotation; repair pki/trust.json first.");
            }

            CertificateAuthority next = CertificateAuthority.Create(_time);
            try
            {
                PkiFiles.WriteSecret(Path.Combine(PkiDirectory, MasterTrustStore.NextCaKeyFileName), next.ExportPrivateKeyPem());
                await HookAsync("next:key-written").ConfigureAwait(false);
                PkiFiles.WritePublic(Path.Combine(PkiDirectory, MasterTrustStore.NextCaCertificateFileName), next.CertificatePem);

                DateTimeOffset now = _time.GetUtcNow();
                var record = new CaRotationRecord
                {
                    RotationId = "carot-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
                    OldCaSpkiSha256 = current.SpkiSha256,
                    NewCaSpkiSha256 = next.SpkiSha256,
                    Stage = CaRotationStage.Started,
                    StartedAt = now,
                    UpdatedAt = now,
                    Actor = actor,
                    Nodes = await ParticipantsAsync([], ct).ConfigureAwait(false),
                };
                await _trust.UpdateAsync(d => d with
                {
                    Anchors = [d.Anchors[0], MasterTrustStore.AnchorOf(next.Certificate, now)],
                    Rotation = record,
                }, ct).ConfigureAwait(false);
            }
            catch
            {
                next.Dispose();
                throw;
            }

            ReplaceNextCa(next);
            CaRotationLog.Started(_logger, Rotation!.RotationId, Rotation.NewCaSpkiSha256, Rotation.Nodes.Count);
            await AuditAsync(actor, null, AuditResults.Succeeded, $"started: new CA {Rotation.NewCaSpkiSha256}, {Rotation.Nodes.Count} node(s)").ConfigureAwait(false);
            await HookAsync("persisted:" + CaRotationStage.Started).ConfigureAwait(false);
            return await DriveAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Resumes the open rotation from its stored stage and drives it as far as it goes now (no-op when none is open).</summary>
    public async Task<CaRotationRecord?> ResumeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await InitializeCoreAsync(ct).ConfigureAwait(false);
            return Rotation is { } open && MasterTrustStore.IsOpen(open.Stage) ? await DriveAsync(ct).ConfigureAwait(false) : Rotation;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Closes the open rotation without waiting for nodes that have not rotated (design §10.2 step 5): the master and the
    /// rotated agents keep only the new CA; the others are left untrusted and must be re-paired (<c>join --rekey</c>).
    /// Allowed once the master certificates were re-issued (<see cref="CaRotationStage.MasterReissued"/> or later).
    /// </summary>
    public async Task<CaRotationRecord> CloseEarlyAsync(string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(actor);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await InitializeCoreAsync(ct).ConfigureAwait(false);
            CaRotationRecord rotation = Rotation is { } open && MasterTrustStore.IsOpen(open.Stage)
                ? open
                : throw new CaRotationRefusedException("No CA rotation is open.");
            if (rotation.Stage < CaRotationStage.MasterReissued)
            {
                throw new CaRotationRefusedException($"CA rotation {rotation.RotationId} is at {rotation.Stage}: resume it until the master certificates are re-issued before closing early.");
            }

            if (rotation.Stage != CaRotationStage.Closing)
            {
                await PersistStageAsync(CaRotationStage.Closing, ct).ConfigureAwait(false);
            }

            string waiting = string.Join(", ", rotation.Nodes.Where(n => !n.CertificateRotated).Select(n => n.NodeId));
            await AuditAsync(actor, null, AuditResults.Succeeded, waiting.Length == 0 ? "close requested" : $"close early requested; not rotated: {waiting}").ConfigureAwait(false);
            return await DriveAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>One pass of the background loop: flag files, then the open rotation. Failures are logged, never thrown.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        string startFlag = Path.Combine(PkiDirectory, CaRotationOptions.StartFlagFileName);
        string closeFlag = Path.Combine(PkiDirectory, CaRotationOptions.CloseEarlyFlagFileName);
        try
        {
            if (File.Exists(startFlag))
            {
                try
                {
                    await StartAsync(AuditActors.Config, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    CaRotationLog.FlagRefused(_logger, CaRotationOptions.StartFlagFileName, ex.Message);
                }

                File.Delete(startFlag);
            }

            if (File.Exists(closeFlag))
            {
                try
                {
                    await CloseEarlyAsync(AuditActors.Config, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    CaRotationLog.FlagRefused(_logger, CaRotationOptions.CloseEarlyFlagFileName, ex.Message);
                }

                File.Delete(closeFlag);
            }

            await ResumeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CaRotationException or IOException or PkiException or KeyNotFoundException or UnauthorizedAccessException)
        {
            CaRotationLog.PassFailed(_logger, ex.Message);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(_options.CheckInterval, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();

        // The authority stays referenced by MasterCertificates for its half-life re-issues; it is public material plus a key
        // object the GC finalizes, so it is not disposed here while the master certificates may still sign with it.
    }

    private static bool SameCertificate(string a, string b) =>
        string.Equals(a.Trim().ReplaceLineEndings("\n"), b.Trim().ReplaceLineEndings("\n"), StringComparison.Ordinal);

    private static string NewOperationId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private void ReplaceNextCa(CertificateAuthority next)
    {
        // Never dispose an authority MasterCertificates may hold (see Dispose); a replaced unused one is just dropped.
        _nextCa = next;
    }

    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        if (Rotation is { } rotation && MasterTrustStore.IsOpen(rotation.Stage) && rotation.Stage > CaRotationStage.MasterReissued)
        {
            // Past step 3: the new CA issues (normally the boot CA already, after the promoted files were loaded).
            await EnsureNewAuthorityAsync(rotation, ct).ConfigureAwait(false);
        }

        if ((Rotation is null || !MasterTrustStore.IsOpen(Rotation.Stage)) && _master.PreviousClientCredential is not null)
        {
            // Left by a rotation that closed (or by hand): it never outlives the rotation's close.
            _master.DropPreviousClient();
        }

        ApplyPreviousClients();
        _initialized = true;
    }

    /// <summary>Drives the open rotation until it closes or waits for nodes. Must hold the gate.</summary>
    private async Task<CaRotationRecord> DriveAsync(CancellationToken ct)
    {
        // A node that failed once in this pass is not tried again before the next one (an offline node costs its timeouts once).
        _failedThisPass.Clear();
        while (true)
        {
            CaRotationRecord rotation = Rotation ?? throw new CaRotationException("The CA rotation record disappeared.");
            switch (rotation.Stage)
            {
                case CaRotationStage.Started:
                    await PersistStageAsync(CaRotationStage.AgentsTrusting, ct).ConfigureAwait(false);
                    break;

                case CaRotationStage.AgentsTrusting:
                    await SyncParticipantsAsync(ct).ConfigureAwait(false);
                    foreach (CaRotationNodeProgress node in Rotation!.Nodes.Where(n => !n.TrustAdded).ToList())
                    {
                        await AddTrustAsync(node, ct).ConfigureAwait(false);
                    }

                    // Before the re-issue: the client certificate nodes without the new CA still accept.
                    _master.RetainPreviousClient();
                    await HookAsync("previous-retained").ConfigureAwait(false);
                    await PersistStageAsync(CaRotationStage.MasterReissued, ct).ConfigureAwait(false);
                    break;

                case CaRotationStage.MasterReissued:
                    await EnsureNewAuthorityAsync(rotation, ct).ConfigureAwait(false);
                    await AuditAsync(AuditActors.SystemPki, null, AuditResults.Succeeded, "new CA promoted; master certificates re-issued under it").ConfigureAwait(false);
                    await PersistStageAsync(CaRotationStage.AgentsRotating, ct).ConfigureAwait(false);
                    break;

                case CaRotationStage.AgentsRotating:
                    await SyncParticipantsAsync(ct).ConfigureAwait(false);
                    foreach (CaRotationNodeProgress node in Rotation!.Nodes.Where(n => !n.CertificateRotated && !_failedThisPass.Contains(n.NodeId)).ToList())
                    {
                        // Enrolled or re-paired under the new CA (it trusts only the new CA, so the previous client
                        // certificate would be refused): adopted as it is. An offline node is skipped for this pass.
                        if (!node.TrustAdded && (await TryAdoptAsync(node.NodeId, "adding the new CA", ct).ConfigureAwait(false) || _failedThisPass.Contains(node.NodeId)))
                        {
                            continue;
                        }

                        if (!node.TrustAdded && !await AddTrustAsync(node, ct).ConfigureAwait(false))
                        {
                            continue;
                        }

                        await RotateAndVerifyAsync(node.NodeId, ct).ConfigureAwait(false);
                    }

                    if (Rotation!.Nodes.Any(n => !n.CertificateRotated))
                    {
                        string waiting = string.Join(", ", Rotation.Nodes.Where(n => !n.CertificateRotated).Select(n => n.NodeId));
                        CaRotationLog.Waiting(_logger, rotation.RotationId, waiting);
                        return Rotation;
                    }

                    await PersistStageAsync(CaRotationStage.Closing, ct).ConfigureAwait(false);
                    break;

                case CaRotationStage.Closing:
                    if (!await CloseAsync(ct).ConfigureAwait(false))
                    {
                        return Rotation!;
                    }

                    break;

                default:
                    return rotation;
            }
        }
    }

    /// <summary>
    /// Step 3, idempotent: the new CA's files become <c>ca.key</c>/<c>ca.crt</c>, it becomes the issuing authority, the
    /// master certificates are re-issued under it and the client factory presents the new client certificate.
    /// </summary>
    private async Task EnsureNewAuthorityAsync(CaRotationRecord rotation, CancellationToken ct)
    {
        CertificateAuthority authority = _master.Authority;
        if (!Spki.PinEquals(authority.SpkiSha256, rotation.NewCaSpkiSha256))
        {
            authority = LoadNextCa(rotation);
        }

        // Promote (idempotent: a file already holding the new CA is not rewritten). The new CA's key is read from its own
        // file, never exported from memory.
        string? nextKey = PkiFiles.ReadIfExists(Path.Combine(PkiDirectory, MasterTrustStore.NextCaKeyFileName));
        if (nextKey is not null)
        {
            if (MasterTrustStore.PromoteCaFiles(PkiDirectory, authority.CertificatePem, nextKey, () => HookAsync("promote:key-written").GetAwaiter().GetResult()))
            {
                // No restart: the master's consumers follow MasterCertificates.Authority (MN-126; RestartRecommended removed, MN-124).
                CaRotationLog.Promoted(_logger, rotation.RotationId, rotation.NewCaSpkiSha256);
            }
        }
        else if (PkiFiles.ReadIfExists(Path.Combine(PkiDirectory, CaStore.CertificateFileName)) is not { } onDisk || !SameCertificate(onDisk, authority.CertificatePem))
        {
            throw new CaRotationException($"CA rotation {rotation.RotationId}: the new CA's files are gone and {CaStore.CertificateFileName} does not hold it.");
        }

        await HookAsync("promoted").ConfigureAwait(false);
        MasterCertificateChange change = _master.ReissueUnder(authority);
        if (change != MasterCertificateChange.None)
        {
            using TrustAnchorSet trust = _trust.CreateTrustSet();
            _clients.UpdateMasterTls(_master.ClientCredential, trust);
            CaRotationLog.Reissued(_logger, rotation.RotationId, _master.ClientCredential.SpkiSha256);
        }

        ApplyPreviousClients();
        await HookAsync("reissued").ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    private CertificateAuthority LoadNextCa(CaRotationRecord rotation)
    {
        if (_nextCa is { } loaded && Spki.PinEquals(loaded.SpkiSha256, rotation.NewCaSpkiSha256))
        {
            return loaded;
        }

        string? certificatePem = PkiFiles.ReadIfExists(Path.Combine(PkiDirectory, MasterTrustStore.NextCaCertificateFileName));
        string? keyPem = PkiFiles.ReadIfExists(Path.Combine(PkiDirectory, MasterTrustStore.NextCaKeyFileName));
        if (certificatePem is null || keyPem is null)
        {
            throw new CaRotationException($"CA rotation {rotation.RotationId}: the new CA's files ({MasterTrustStore.NextCaCertificateFileName}, {MasterTrustStore.NextCaKeyFileName}) are missing.");
        }

        CertificateAuthority next = CertificateAuthority.Load(certificatePem, keyPem, _time);
        if (!Spki.PinEquals(next.SpkiSha256, rotation.NewCaSpkiSha256))
        {
            next.Dispose();
            throw new CaRotationException($"CA rotation {rotation.RotationId}: {MasterTrustStore.NextCaCertificateFileName} is not the recorded new CA.");
        }

        ReplaceNextCa(next);
        return next;
    }

    /// <summary>
    /// Nodes that do not trust the new CA yet are reached with the previous client certificate (after step 3 only); a node
    /// (re-)enrolled under the new CA since (<see cref="NodeEnrolled"/>) is not. Serialized, so the last caller's view wins.
    /// </summary>
    private void ApplyPreviousClients()
    {
        lock (_previousGate)
        {
            CaRotationRecord? rotation = Rotation;
            IssuedCredential? previous = _master.PreviousClientCredential;
            // Not from Closing on: the master then narrows to the new root, and a node left behind by an early close is re-paired.
            bool active = rotation is not null && rotation.Stage is CaRotationStage.MasterReissued or CaRotationStage.AgentsRotating && previous is not null &&
                          Spki.PinEquals(_master.Authority.SpkiSha256, rotation.NewCaSpkiSha256);
            if (!active)
            {
                _enrolledUnderNewCa.Clear();
            }

            string[] nodes = active
                ? [.. rotation!.Nodes.Where(n => !n.TrustAdded && !_enrolledUnderNewCa.Contains(n.NodeId)).Select(n => n.NodeId)]
                : [];
            _clients.SetPreviousMasterClient(nodes.Length == 0 ? null : previous, nodes);
        }
    }

    /// <summary>
    /// Called by the enroll listener after it issued <paramref name="nodeId"/> a certificate under the CA with
    /// <paramref name="issuerSpkiSha256"/> (an enrollment or a rekey, MN-124): issued under an open rotation's new CA, the
    /// node trusts the new CA only, so the master reaches it with its current client certificate at once instead of the
    /// previous one until the next pass adopts it. No-op otherwise (no rotation, or issued under the old CA).
    /// </summary>
    public void NodeEnrolled(string nodeId, string issuerSpkiSha256)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentException.ThrowIfNullOrEmpty(issuerSpkiSha256);
        if (Rotation is not { } rotation || !MasterTrustStore.IsOpen(rotation.Stage) || !Spki.PinEquals(issuerSpkiSha256, rotation.NewCaSpkiSha256))
        {
            return;
        }

        lock (_previousGate)
        {
            _enrolledUnderNewCa.Add(nodeId);
        }

        ApplyPreviousClients();
        CaRotationLog.EnrolledUnderNewCa(_logger, rotation.RotationId, nodeId);
    }

    /// <summary>Step 2 for one node: <c>PUT /pki/trust</c> [old, new]. True when the node now trusts both.</summary>
    private async Task<bool> AddTrustAsync(CaRotationNodeProgress node, CancellationToken ct)
    {
        IReadOnlyList<MasterTrustAnchor> anchors = _trust.Document.Anchors;
        string newPin = Rotation!.NewCaSpkiSha256;
        try
        {
            await SetAgentTrustAsync(node.NodeId, [.. anchors.Select(a => a.CaCertPem)], [.. anchors.Select(a => a.SpkiSha256)], ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNodeFailure(ex))
        {
            await NodeFailedAsync(node.NodeId, "adding the new CA", ex).ConfigureAwait(false);
            return false;
        }

        await UpdateNodeAsync(node.NodeId, p => p with { TrustAdded = true, Error = null }, ct).ConfigureAwait(false);
        ApplyPreviousClients();
        await AuditAsync(AuditActors.SystemPki, node.NodeId, AuditResults.Succeeded, $"agent trusts the new CA {newPin}").ConfigureAwait(false);
        return true;
    }

    /// <summary>Step 4 for one node: rotate under the new CA, then prove it on a fresh connection under the new root only.</summary>
    private async Task RotateAndVerifyAsync(string nodeId, CancellationToken ct)
    {
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Proof proof = await VerifyAsync(nodeId, ct).ConfigureAwait(false);
                switch (proof.Kind)
                {
                    case ProofKind.Verified:
                        await UpdateNodeAsync(nodeId, p => p with { CertificateRotated = true, Error = null }, ct).ConfigureAwait(false);
                        CaRotationLog.NodeRotated(_logger, Rotation!.RotationId, nodeId);
                        await AuditAsync(AuditActors.SystemPki, nodeId, AuditResults.Succeeded, "agent certificate under the new CA verified on a fresh connection").ConfigureAwait(false);
                        return;
                    case ProofKind.Unreachable:
                        throw new AgentUnreachableException(proof.Error ?? "the agent did not answer");
                    case ProofKind.NotTrusting:
                        // The agent no longer lists the new CA (state lost): add it again before anything else.
                        await UpdateNodeAsync(nodeId, p => p with { TrustAdded = false }, ct).ConfigureAwait(false);
                        if (!await AddTrustAsync(Rotation!.Nodes.First(n => n.NodeId == nodeId), ct).ConfigureAwait(false))
                        {
                            return;
                        }

                        continue;
                }

                // Old root: rotate (resumes an open ordinary rotation, else starts one; both sign with the new CA now).
                await HookAsync("rotating").ConfigureAwait(false);
                CertificateRotationRecord rotated = await _rotations.RotateAsync(nodeId, ct).ConfigureAwait(false);
                await UpdateNodeAsync(nodeId, p => p with { CertificateRotationId = rotated.RotationId }, ct).ConfigureAwait(false);
                await AuditAsync(AuditActors.SystemPki, nodeId, rotated.Stage == CertificateRotationStage.Committed ? AuditResults.Succeeded : AuditResults.Failed,
                    $"certificate rotation {rotated.RotationId} ended {rotated.Stage}", action: AuditActions.NodeRotateCert).ConfigureAwait(false);
                await HookAsync("rotated").ConfigureAwait(false);
            }

            throw new CaRotationException($"Node {nodeId} still presents a certificate that does not chain to the new CA after rotating.");
        }
        catch (Exception ex) when (IsNodeFailure(ex))
        {
            await NodeFailedAsync(nodeId, "rotating its certificate under the new CA", ex).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The proof (design §10.2 step 4): a fresh connection that validates the agent under the new root only, pinned to the
    /// registry's current pin, presenting the current (new) master client certificate, and whose <c>GET /pki/trust</c> lists
    /// the new CA. When it fails at TLS, a second fresh connection under every anchor tells an old-root certificate from a
    /// node that does not answer.
    /// </summary>
    private async Task<Proof> VerifyAsync(string nodeId, CancellationToken ct)
    {
        MasterTrustAnchor newAnchor = NewAnchor();
        try
        {
            PkiTrustResponse trust = await ReadTrustUnderNewRootAsync(nodeId, newAnchor, ct).ConfigureAwait(false);
            return trust.Anchors.Any(a => Spki.PinEquals(a.SpkiSha256, newAnchor.SpkiSha256)) ? new Proof(ProofKind.Verified) : new Proof(ProofKind.NotTrusting);
        }
        catch (AgentUnreachableException)
        {
            try
            {
                // The record's current and pending pins: an installed but uncommitted certificate is what new connections get.
                IAgentClientLease lease = await _clients.CreateFreshClientAsync(nodeId, ct).ConfigureAwait(false);
                await using (lease.ConfigureAwait(false))
                {
                    await lease.Client.GetInfoAsync(ct).ConfigureAwait(false);
                    return new Proof(ProofKind.OldRoot);
                }
            }
            catch (AgentUnreachableException ex)
            {
                return new Proof(ProofKind.Unreachable, ex.Message);
            }
        }
    }

    private MasterTrustAnchor NewAnchor() =>
        _trust.Document.Anchors.FirstOrDefault(a => Spki.PinEquals(a.SpkiSha256, Rotation!.NewCaSpkiSha256))
        ?? throw new CaRotationException("The new CA is not among the master's anchors.");

    /// <summary>
    /// <c>GET /pki/trust</c> on a fresh connection that validates the agent under the new root only, pinned to the registry's
    /// current pin, presenting the current (new) master client certificate (<see cref="AgentClientFactory.CreateFreshClientAsync(string, IReadOnlyCollection{string}?, IReadOnlyList{string}?, CancellationToken)"/>).
    /// </summary>
    private async Task<PkiTrustResponse> ReadTrustUnderNewRootAsync(string nodeId, MasterTrustAnchor newAnchor, CancellationToken ct)
    {
        NodeRecord record = await _registry.GetAsync(nodeId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Node {nodeId} is not in the registry.");
        if (string.IsNullOrEmpty(record.CertSpkiSha256))
        {
            throw new KeyNotFoundException($"Node {nodeId} has no certificate pin.");
        }

        IAgentClientLease lease = await _clients.CreateFreshClientAsync(nodeId, [record.CertSpkiSha256], [newAnchor.CaCertPem], ct).ConfigureAwait(false);
        await using (lease.ConfigureAwait(false))
        {
            return await lease.Client.GetTrustAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// After step 3, for a node that has not taken the new CA through this rotation: when it already presents a certificate
    /// under the new root and accepts the new master client certificate (it was enrolled or re-paired under the new CA, MN-126),
    /// it is marked trusting and rotated (and narrowed when it does not list the old CA) without any change on the agent.
    /// True when adopted. A node that does not answer at all is recorded as failed at <paramref name="failureStep"/> for this
    /// pass (the step it is waiting for; null = not recorded); one whose TLS fails (an old-CA node) is left to the normal steps.
    /// </summary>
    private async Task<bool> TryAdoptAsync(string nodeId, string? failureStep, CancellationToken ct)
    {
        CaRotationRecord rotation = Rotation!;
        if (rotation.Stage < CaRotationStage.MasterReissued || !Spki.PinEquals(_master.Authority.SpkiSha256, rotation.NewCaSpkiSha256))
        {
            return false;
        }

        PkiTrustResponse trust;
        try
        {
            trust = await ReadTrustUnderNewRootAsync(nodeId, NewAnchor(), ct).ConfigureAwait(false);
        }
        catch (AgentUnreachableException ex) when (ex.InnerException is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError })
        {
            if (failureStep is not null)
            {
                await NodeFailedAsync(nodeId, failureStep, ex).ConfigureAwait(false);
            }

            return false;
        }
        catch (Exception ex) when (IsNodeFailure(ex))
        {
            return false; // TLS refused (an old-CA node) or another answer: the normal steps apply.
        }

        if (!trust.Anchors.Any(a => Spki.PinEquals(a.SpkiSha256, rotation.NewCaSpkiSha256)))
        {
            return false;
        }

        bool listsOld = trust.Anchors.Any(a => Spki.PinEquals(a.SpkiSha256, rotation.OldCaSpkiSha256));
        await UpdateNodeAsync(nodeId, p => p with { TrustAdded = true, CertificateRotated = true, OldTrustRemoved = !listsOld, Error = null }, ct).ConfigureAwait(false);
        ApplyPreviousClients();
        CaRotationLog.NodeRotated(_logger, rotation.RotationId, nodeId);
        await AuditAsync(AuditActors.SystemPki, nodeId, AuditResults.Succeeded, "agent already under the new CA (enrolled or re-paired during the rotation); verified on a fresh connection").ConfigureAwait(false);
        return true;
    }

    /// <summary>Step 5: the master drops the old root, then every rotated agent is narrowed; then the rotation closes. True when closed.</summary>
    private async Task<bool> CloseAsync(CancellationToken ct)
    {
        await SyncParticipantsAsync(ct).ConfigureAwait(false);
        CaRotationRecord rotation = Rotation!;
        MasterTrustAnchor newAnchor = _trust.Document.Anchors.FirstOrDefault(a => Spki.PinEquals(a.SpkiSha256, rotation.NewCaSpkiSha256))
            ?? throw new CaRotationException("The new CA is not among the master's anchors.");
        if (_trust.Document.Anchors.Count > 1)
        {
            await _trust.UpdateAsync(d => d with { Anchors = [newAnchor], Rotation = d.Rotation! with { UpdatedAt = _time.GetUtcNow() } }, ct).ConfigureAwait(false);
            CaRotationLog.MasterNarrowed(_logger, rotation.RotationId);
            await AuditAsync(AuditActors.SystemPki, null, AuditResults.Succeeded, "the master trusts only the new CA").ConfigureAwait(false);
            await HookAsync("master-narrowed").ConfigureAwait(false);
        }

        // Nodes left behind by an early close can no longer be reached: they are re-paired. A node enrolled (or re-paired)
        // under the new CA meanwhile is not left behind: it is adopted.
        _clients.SetPreviousMasterClient(null, []);
        foreach (CaRotationNodeProgress node in Rotation!.Nodes.Where(n => !n.CertificateRotated && !string.Equals(n.Error, EarlyCloseError, StringComparison.Ordinal)).ToList())
        {
            if (!node.TrustAdded && await TryAdoptAsync(node.NodeId, failureStep: null, ct).ConfigureAwait(false))
            {
                continue;
            }

            await UpdateNodeAsync(node.NodeId, p => p with { Error = EarlyCloseError }, ct).ConfigureAwait(false);
            await AuditAsync(AuditActors.SystemPki, node.NodeId, AuditResults.Failed, "not rotated before the early close; re-pair with join --rekey").ConfigureAwait(false);
        }

        bool allNarrowed = true;
        foreach (CaRotationNodeProgress node in Rotation!.Nodes.Where(n => n.CertificateRotated && !n.OldTrustRemoved).ToList())
        {
            if (_failedThisPass.Contains(node.NodeId))
            {
                allNarrowed = false;
                continue;
            }

            try
            {
                await SetAgentTrustAsync(node.NodeId, [newAnchor.CaCertPem], [newAnchor.SpkiSha256], ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsNodeFailure(ex))
            {
                await NodeFailedAsync(node.NodeId, "removing the old CA", ex).ConfigureAwait(false);
                allNarrowed = false;
                continue;
            }

            await UpdateNodeAsync(node.NodeId, p => p with { OldTrustRemoved = true, Error = null }, ct).ConfigureAwait(false);
            await AuditAsync(AuditActors.SystemPki, node.NodeId, AuditResults.Succeeded, "agent trusts only the new CA").ConfigureAwait(false);
        }

        if (!allNarrowed)
        {
            return false;
        }

        // Done: the new CA is ca.key/ca.crt; its rotation copies and the previous client certificate go.
        File.Delete(Path.Combine(PkiDirectory, MasterTrustStore.NextCaKeyFileName));
        File.Delete(Path.Combine(PkiDirectory, MasterTrustStore.NextCaCertificateFileName));
        DirectorySync.TryFlushParent(Path.Combine(PkiDirectory, MasterTrustStore.NextCaKeyFileName));
        _master.DropPreviousClient();
        await HookAsync("cleaned").ConfigureAwait(false);
        bool early = Rotation!.Nodes.Any(n => !n.CertificateRotated);
        CaRotationStage final = early ? CaRotationStage.ClosedEarly : CaRotationStage.Closed;
        await PersistStageAsync(final, ct).ConfigureAwait(false);
        CaRotationLog.Closed(_logger, rotation.RotationId, final);
        await AuditAsync(AuditActors.SystemPki, null, AuditResults.Succeeded, early ? "closed early" : "closed: only the new CA is trusted").ConfigureAwait(false);
        return true;
    }

    /// <summary><c>PUT /pki/trust</c>; a lost answer is resolved by reading the agent's anchors.</summary>
    private async Task SetAgentTrustAsync(string nodeId, IReadOnlyList<string> pems, IReadOnlyList<string> pins, CancellationToken ct)
    {
        IAgentClient client = _clients.GetClient(nodeId);
        DateTimeOffset now = _time.GetUtcNow();
        var request = new PkiTrustSetRequest
        {
            OperationId = NewOperationId(),
            IssuedAt = now,
            ExpiresAt = now + OperationRetention.EnvelopeLifetime,
            Anchors = pems,
        };

        IReadOnlyList<TrustAnchorInfo>? answered;
        try
        {
            OperationStatus op = await client.SetTrustAsync(request, ct).ConfigureAwait(false);
            if (op.State != OperationState.Succeeded)
            {
                throw new CaRotationException($"The agent did not store the trust anchors ({op.State}: {op.Error?.Code}).");
            }

            answered = op.ReadResult<PkiTrustResponse>()?.Anchors;
        }
        catch (AgentUnreachableException ex) when (ex.InnerException is not HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError })
        {
            answered = null; // sent, answer lost: read below (a refused connection sent nothing and is rethrown)
        }

        await HookAsync("sent:trust").ConfigureAwait(false);
        answered ??= (await client.GetTrustAsync(ct).ConfigureAwait(false)).Anchors;
        if (answered.Count != pins.Count || !pins.All(pin => answered.Any(a => Spki.PinEquals(a.SpkiSha256, pin))))
        {
            throw new CaRotationException($"Node {nodeId} reports other trust anchors than were set.");
        }
    }

    /// <summary>Adds nodes enrolled meanwhile, drops revoked or deleted ones, restarts re-enrolled ones. Persisted when changed.</summary>
    private async Task SyncParticipantsAsync(CancellationToken ct)
    {
        IReadOnlyList<CaRotationNodeProgress> current = Rotation!.Nodes;
        IReadOnlyList<CaRotationNodeProgress> next = await ParticipantsAsync(current, ct).ConfigureAwait(false);
        if (!next.SequenceEqual(current))
        {
            await PersistAsync(r => r with { Nodes = next }, ct).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<CaRotationNodeProgress>> ParticipantsAsync(IReadOnlyList<CaRotationNodeProgress> current, CancellationToken ct)
    {
        List<CaRotationNodeProgress> result = [];
        foreach (NodeRecord node in (await _registry.ListAsync(ct).ConfigureAwait(false)).OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            if (node.Revoked || string.IsNullOrEmpty(node.CertSpkiSha256))
            {
                continue;
            }

            CaRotationNodeProgress? known = current.FirstOrDefault(p => string.Equals(p.NodeId, node.Id, StringComparison.Ordinal));
            // A rekey (new EnrolledAt) resets the agent's trust to the issuing CA only, so its entry starts over (W7 review).
            bool same = known is not null && string.Equals(known.NodeUid, node.Uid, StringComparison.Ordinal)
                && (known.EnrolledAt is null || known.EnrolledAt == node.EnrolledAt);
            result.Add(same ? known! : new CaRotationNodeProgress { NodeId = node.Id, NodeUid = node.Uid, EnrolledAt = node.EnrolledAt });
        }

        return result;
    }

    private async Task PersistStageAsync(CaRotationStage stage, CancellationToken ct)
    {
        await PersistAsync(r => r with { Stage = stage }, ct).ConfigureAwait(false);
        CaRotationLog.StagePersisted(_logger, Rotation!.RotationId, stage);
        await HookAsync("persisted:" + stage).ConfigureAwait(false);
    }

    private Task UpdateNodeAsync(string nodeId, Func<CaRotationNodeProgress, CaRotationNodeProgress> change, CancellationToken ct) =>
        PersistAsync(r => r with
        {
            Nodes = [.. r.Nodes.Select(n => string.Equals(n.NodeId, nodeId, StringComparison.Ordinal) ? change(n) : n)],
        }, ct);

    private async Task PersistAsync(Func<CaRotationRecord, CaRotationRecord> change, CancellationToken ct)
    {
        string rotationId = Rotation!.RotationId;
        DateTimeOffset now = _time.GetUtcNow();
        await _trust.UpdateAsync(d =>
        {
            if (d.Rotation is not { } current || !string.Equals(current.RotationId, rotationId, StringComparison.Ordinal))
            {
                throw new CaRotationException($"CA rotation {rotationId} was replaced concurrently.");
            }

            return d with { Rotation = change(current) with { UpdatedAt = now } };
        }, ct).ConfigureAwait(false);
    }

    private async Task NodeFailedAsync(string nodeId, string step, Exception ex)
    {
        _failedThisPass.Add(nodeId);
        string error = $"{step}: {ex.Message}";
        CaRotationNodeProgress? before = Rotation!.Nodes.FirstOrDefault(n => n.NodeId == nodeId);
        CaRotationLog.NodeFailed(_logger, Rotation.RotationId, nodeId, error);
        try
        {
            await UpdateNodeAsync(nodeId, p => p with { Error = error }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception persist) when (persist is IOException or CaRotationException or PkiException)
        {
            CaRotationLog.NodeFailed(_logger, Rotation.RotationId, nodeId, "the error could not be recorded: " + persist.Message);
        }

        if (!string.Equals(before?.Error, error, StringComparison.Ordinal))
        {
            await AuditAsync(AuditActors.SystemPki, nodeId, AuditResults.Failed, error, errorCode: (ex as AgentException)?.Code).ConfigureAwait(false);
        }
    }

    private static bool IsNodeFailure(Exception ex) =>
        ex is AgentException or AgentUnreachableException or KeyNotFoundException or CertificateRotationException or PkiException or CaRotationException
            || (ex is InvalidOperationException && ex is not ObjectDisposedException);

    private Task AuditAsync(string actor, string? nodeId, string result, string detail, string? errorCode = null, string action = AuditActions.PkiCaRotation) =>
        _audit.WriteAsync(new AuditRecord
        {
            Time = _time.GetUtcNow(),
            Actor = actor,
            Action = action,
            NodeId = nodeId,
            Target = Rotation?.RotationId,
            Result = result,
            ErrorCode = errorCode,
            Detail = $"stage {Rotation?.Stage}: {detail}",
        }, CancellationToken.None);

    private Task HookAsync(string point) => FaultInjection?.Invoke(point) ?? Task.CompletedTask;

    private enum ProofKind
    {
        Verified,
        OldRoot,
        NotTrusting,
        Unreachable,
    }

    private readonly record struct Proof(ProofKind Kind, string? Error = null);
}

/// <summary>Source-generated log messages of the CA rotation. Ids, stages and SPKI pins only.</summary>
internal static partial class CaRotationLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "CA rotation {RotationId} started: new CA {Spki}, {Nodes} node(s); the master trusts both roots.")]
    public static partial void Started(ILogger logger, string rotationId, string spki, int nodes);

    [LoggerMessage(Level = LogLevel.Information, Message = "CA rotation {RotationId}: node {NodeId} was enrolled under the new CA; reached with the current client certificate.")]
    public static partial void EnrolledUnderNewCa(ILogger logger, string rotationId, string nodeId);

    [LoggerMessage(Level = LogLevel.Information, Message = "CA rotation {RotationId}: stage {Stage} persisted.")]
    public static partial void StagePersisted(ILogger logger, string rotationId, CaRotationStage stage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "CA rotation {RotationId}: the new CA {Spki} replaced ca.key/ca.crt and issues from now on (enrollment, join commands, the bootstrap bundle and backups use it).")]
    public static partial void Promoted(ILogger logger, string rotationId, string spki);

    [LoggerMessage(Level = LogLevel.Information, Message = "CA rotation {RotationId}: master certificates re-issued under the new CA (client {Spki}).")]
    public static partial void Reissued(ILogger logger, string rotationId, string spki);

    [LoggerMessage(Level = LogLevel.Information, Message = "CA rotation {RotationId}: node {NodeId} rotated and verified under the new CA.")]
    public static partial void NodeRotated(ILogger logger, string rotationId, string nodeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "CA rotation {RotationId}: node {NodeId}: {Reason}")]
    public static partial void NodeFailed(ILogger logger, string rotationId, string nodeId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "CA rotation {RotationId} waits for node(s) {Nodes}; both roots stay trusted on the master.")]
    public static partial void Waiting(ILogger logger, string rotationId, string nodes);

    [LoggerMessage(Level = LogLevel.Information, Message = "CA rotation {RotationId}: the master removed the old root.")]
    public static partial void MasterNarrowed(ILogger logger, string rotationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "CA rotation {RotationId} ended {Stage}.")]
    public static partial void Closed(ILogger logger, string rotationId, CaRotationStage stage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "CA rotation flag {Flag} refused: {Reason}")]
    public static partial void FlagRefused(ILogger logger, string flag, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The CA rotation pass failed: {Reason}")]
    public static partial void PassFailed(ILogger logger, string reason);
}
