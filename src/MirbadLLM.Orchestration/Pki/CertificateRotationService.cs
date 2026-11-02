using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Pki;

namespace MirbadLLM.Orchestration.Pki;

/// <summary>Tunables of <see cref="CertificateRotationService"/> (defaults = design §10.2).</summary>
public sealed class CertificateRotationOptions
{
    /// <summary>
    /// An agent certificate is renewed this long before it expires: 30 days, i.e. at 60 days of its 90
    /// (<see cref="PkiDefaults.AgentCertificateLifetime"/> − <see cref="PkiDefaults.AgentRenewalAge"/>).
    /// </summary>
    public TimeSpan RenewBeforeExpiry { get; init; } = PkiDefaults.AgentCertificateLifetime - PkiDefaults.AgentRenewalAge;

    /// <summary>Renewal problems put the node in Degraded from this long before expiry (14 days).</summary>
    public TimeSpan DegradedBeforeExpiry { get; init; } = PkiDefaults.RenewalWarningWindow;

    /// <summary>How often the background loop looks for due renewals, open rotations and master re-issues.</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Lifetime of re-issued agent certificates (default 90 days).</summary>
    public TimeSpan? AgentCertificateLifetime { get; init; }
}

/// <summary>A certificate rotation step failed; the rotation record keeps its stage (and the error) and is resumed later.</summary>
public sealed class CertificateRotationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CertificateRotationException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public CertificateRotationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public CertificateRotationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The master side of agent-certificate rotation (design §10.2, finding 10), driven from the rotation record in
/// <c>nodes.json</c> (<see cref="NodeRecord.Rotation"/>), whose stage is persisted <b>before</b> each step it names, so a
/// master crash resumes from the stored stage (<see cref="RotateAsync"/>, or the background loop):
/// <list type="number">
/// <item><see cref="CertificateRotationStage.Requested"/>: record written; <c>POST /pki/rotations</c> → the agent's CSR
/// (the same CSR on every retry). <see cref="CertificateRotationStage.CsrReceived"/>: its key is recorded.</item>
/// <item><see cref="CertificateRotationStage.CertIssued"/>: the master signed it (server-controlled content, §3.2) and
/// stored the new SPKI as the node's <b>pending pin</b>, so both of the node's handlers accept either pin on their next
/// connection; then <c>PUT /pki/rotations/{id}/cert</c> → <see cref="CertificateRotationStage.CertInstalled"/>.</item>
/// <item>A <b>fresh</b> handler pinned to the new SPKI only (<see cref="AgentClientFactory.CreateFreshClientAsync(string, IReadOnlyCollection{string}?, CancellationToken)"/>,
/// no pooled connection, no TLS resume) connects and reads the rotation: that handshake proves the new certificate.
/// Nothing is committed or deleted before this proof.</item>
/// <item><see cref="CertificateRotationStage.Committing"/> is persisted (from here on abort is refused), then
/// <c>commit</c> is sent over that same fresh connection. A lost answer is resolved by reading the rotation or
/// re-sending <c>commit</c> (idempotent). Then the pending pin is promoted, the old pin dropped
/// (<see cref="CertificateRotationStage.Committed"/>) and the node's client refreshed, which replaces <b>both</b> handlers
/// (control and data) so pooled connections using the old certificate drain.</item>
/// </list>
/// <see cref="AbortAsync"/> returns both sides to the old material, but only before "committing". When the agent reports a
/// different stage than the record (a lost answer, an agent restart that dropped unusable pending material, an abort
/// the master did not record), the record follows the agent while that is still safe.
/// <para>The background loop (<see cref="RunDueAsync"/>, every <see cref="CertificateRotationOptions.CheckInterval"/> on
/// the <see cref="TimeProvider"/>) re-issues the master certificates at half life, resumes open rotations and starts one
/// for every approved node whose certificate is within <see cref="CertificateRotationOptions.RenewBeforeExpiry"/> of
/// expiry (60 days of 90). <see cref="RenewalProblem"/> is the Degraded reason from 14 days before expiry.</para>
/// <para>Not registered in DI here (MN-040). Never logs key material.</para>
/// </summary>
public sealed class CertificateRotationService : BackgroundService
{
    private readonly INodeRegistry _registry;
    private readonly AgentClientFactory _clients;
    private readonly CertificateAuthority _ca;
    private readonly MasterCertificates? _masterCertificates;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CertificateRotationOptions _options;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _nodeGates = new(StringComparer.Ordinal);

    /// <summary>Creates the service.</summary>
    /// <param name="registry">Node registry holding the rotation records and pins.</param>
    /// <param name="clients">The master's agent client factory (fresh handlers, refresh, master TLS updates).</param>
    /// <param name="ca">The master CA that signs agent certificates.</param>
    /// <param name="timeProvider">Clock for due dates, envelopes and the background loop.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="options">Tunables; defaults per design §10.2.</param>
    /// <param name="masterCertificates">The master's own certificates, re-issued at half life by <see cref="RunDueAsync"/>; null = not managed here.</param>
    public CertificateRotationService(
        INodeRegistry registry,
        AgentClientFactory clients,
        CertificateAuthority ca,
        TimeProvider timeProvider,
        ILogger<CertificateRotationService>? logger = null,
        CertificateRotationOptions? options = null,
        MasterCertificates? masterCertificates = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(ca);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _registry = registry;
        _clients = clients;
        _ca = ca;
        _time = timeProvider;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _options = options ?? new CertificateRotationOptions();
        _masterCertificates = masterCertificates;
    }

    /// <summary>
    /// The CA that signs agent certificates: the master certificates' current authority (the new CA once a CA rotation
    /// re-issued them, MN-116), else the CA given at construction.
    /// </summary>
    internal CertificateAuthority IssuingAuthority => _masterCertificates?.Authority ?? _ca;

    /// <summary>Test hook: awaited at named points (<c>persisted:{stage}</c>, <c>sent:{step}</c>); a throwing hook simulates a master crash there.</summary>
    internal Func<string, Task>? FaultInjection { get; set; }

    /// <summary>When the node's certificate is due for renewal (null when its expiry is unknown).</summary>
    public static DateTimeOffset? RenewalDue(NodeRecord node, CertificateRotationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.CertNotAfter - (options ?? new CertificateRotationOptions()).RenewBeforeExpiry;
    }

    /// <summary>
    /// The Degraded reason for a node whose certificate renewal has not happened in time (design §10.2: from 14 days
    /// before expiry), or null when there is none. The node monitor adds it to the node's reasons.
    /// </summary>
    public static string? RenewalProblem(NodeRecord node, DateTimeOffset now, CertificateRotationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Revoked || node.CertNotAfter is not { } notAfter)
        {
            return null;
        }

        TimeSpan left = notAfter - now;
        if (left > (options ?? new CertificateRotationOptions()).DegradedBeforeExpiry)
        {
            return null;
        }

        string why = node.Rotation is { Error: { } error } ? $" Last renewal error: {error}" : "";
        return left <= TimeSpan.Zero
            ? $"The agent certificate expired at {notAfter:O}; re-pair the node (join --rekey).{why}"
            : $"The agent certificate expires at {notAfter:O} and has not been renewed.{why}";
    }

    /// <summary>
    /// Rotates the node's agent certificate: resumes its open rotation from the stored stage, or starts a new one, and
    /// drives it to <see cref="CertificateRotationStage.Committed"/> (or <see cref="CertificateRotationStage.Aborted"/>
    /// when the agent abandoned it). Throws <see cref="CertificateRotationException"/> (the record keeps its stage and the
    /// error) when a step fails; calling again resumes.
    /// </summary>
    public async Task<CertificateRotationRecord> RotateAsync(string nodeId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        SemaphoreSlim gate = _nodeGates.GetOrAdd(nodeId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await DriveAsync(nodeId, startNew: true, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Under the node's gate: resumes an open rotation, or starts one when the certificate is due. The node is re-read
    /// after the gate is taken, so a rotation that finished meanwhile (a manual one) is not followed by a second.
    /// </summary>
    private async Task RotateIfDueAsync(string nodeId, CancellationToken ct)
    {
        SemaphoreSlim gate = _nodeGates.GetOrAdd(nodeId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            NodeRecord node = await GetNodeAsync(nodeId, ct).ConfigureAwait(false);
            bool open = node.Rotation is { } rotation && IsOpen(rotation.Stage);
            bool due = RenewalDue(node, _options) is { } dueAt && _time.GetUtcNow() >= dueAt;
            if (open || due)
            {
                if (!open)
                {
                    RotationLog.RenewalDue(_logger, node.Id, node.CertNotAfter);
                }

                await DriveAsync(nodeId, startNew: due, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Aborts the node's open rotation (design §10.2 step 5): the agent drops its pending key and certificate and the
    /// master its pending pin; both keep the old material. Refused (<see cref="InvalidOperationException"/>) once the master
    /// persisted <see cref="CertificateRotationStage.Committing"/>. Returns the record (unchanged when nothing is open).
    /// </summary>
    public async Task<CertificateRotationRecord?> AbortAsync(string nodeId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        SemaphoreSlim gate = _nodeGates.GetOrAdd(nodeId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            NodeRecord node = await GetNodeAsync(nodeId, ct).ConfigureAwait(false);
            if (node.Rotation is not { } rotation || !IsOpen(rotation.Stage))
            {
                return node.Rotation;
            }

            if (rotation.Stage == CertificateRotationStage.Committing)
            {
                throw new InvalidOperationException($"Rotation {rotation.RotationId} of node {nodeId} is committing; it can no longer be aborted. Resume it instead.");
            }

            var session = new Session(nodeId, rotation);
            try
            {
                DateTimeOffset now = _time.GetUtcNow();
                await _clients.GetClient(nodeId).AbortRotationAsync(new PkiRotationAbortRequest
                {
                    OperationId = NewOperationId(),
                    IssuedAt = now,
                    ExpiresAt = now + OperationRetention.EnvelopeLifetime,
                    RotationId = rotation.RotationId,
                }, ct).ConfigureAwait(false);
            }
            catch (AgentException ex) when (ex.Code == ErrorCodes.NotFound)
            {
                // The agent never started it: nothing to drop there.
            }
            catch (AgentException ex) when (ex.Code == ErrorCodes.AlreadyCommitted)
            {
                // Not possible by protocol (commit is sent only after "committing"), but if it happened the new
                // certificate is the agent's identity now: finish the rotation instead.
                throw new InvalidOperationException($"Rotation {rotation.RotationId} of node {nodeId} is already committed on the agent; resume it instead.", ex);
            }
            catch (Exception ex) when (ex is AgentException or AgentUnreachableException or KeyNotFoundException)
            {
                await RecordErrorAsync(session, ex.Message).ConfigureAwait(false);
                throw new CertificateRotationException($"Could not abort rotation {rotation.RotationId} on node {nodeId}: {ex.Message}", ex);
            }

            await FinishAbortAsync(session, "aborted by the master", ct).ConfigureAwait(false);
            return session.Rotation;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// One pass of the background loop: re-issues master certificates at half life (and updates the agent clients),
    /// resumes open rotations and starts due renewals. Failures are logged and recorded per node; they never stop the pass.
    /// </summary>
    public async Task RunDueAsync(CancellationToken ct = default)
    {
        if (_masterCertificates is not null)
        {
            MasterCertificateChange change = _masterCertificates.ReissueIfDue();
            if (change.HasFlag(MasterCertificateChange.Client))
            {
                // The certificate alone: a re-issue at half life never changes the trust (during a CA rotation the factory
                // validates under both roots, and narrowing it to the issuing CA here would cut off the unrotated nodes).
                _clients.UpdateMasterClient(_masterCertificates.ClientCredential);
            }

            if (change != MasterCertificateChange.None)
            {
                RotationLog.MasterCertificatesReissued(_logger, change);
            }
        }

        DateTimeOffset now = _time.GetUtcNow();
        foreach (NodeRecord node in await _registry.ListAsync(ct).ConfigureAwait(false))
        {
            if (node.Revoked || !node.Approved || string.IsNullOrEmpty(node.CertSpkiSha256))
            {
                continue;
            }

            bool open = node.Rotation is { } rotation && IsOpen(rotation.Stage);
            bool due = RenewalDue(node, _options) is { } dueAt && now >= dueAt;
            if (!open && !due)
            {
                continue;
            }

            try
            {
                await RotateIfDueAsync(node.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is CertificateRotationException or InvalidOperationException or KeyNotFoundException)
            {
                RotationLog.RotationFailed(_logger, node.Id, ex.Message);
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RotationLog.PassFailed(_logger, ex);
            }

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
        foreach (SemaphoreSlim gate in _nodeGates.Values)
        {
            gate.Dispose();
        }

        base.Dispose();
    }

    private static bool IsOpen(CertificateRotationStage stage) =>
        stage is not (CertificateRotationStage.Committed or CertificateRotationStage.Aborted);

    private static string NewOperationId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private async Task<CertificateRotationRecord> DriveAsync(string nodeId, bool startNew, CancellationToken ct)
    {
        NodeRecord node = await GetNodeAsync(nodeId, ct).ConfigureAwait(false);
        if (!node.Approved || string.IsNullOrEmpty(node.CertSpkiSha256))
        {
            throw new InvalidOperationException($"Node {nodeId} is not an approved, enrolled node; its certificate is not rotated.");
        }

        CertificateRotationRecord? rotation = node.Rotation;
        if (rotation is not null && IsOpen(rotation.Stage) && !string.Equals(rotation.NodeUid, node.Uid, StringComparison.Ordinal))
        {
            // The node was re-enrolled: the old rotation belongs to another identity and is never continued.
            await FinishAbortAsync(new Session(nodeId, rotation), "the node was re-enrolled", ct).ConfigureAwait(false);
            rotation = (await GetNodeAsync(nodeId, ct).ConfigureAwait(false)).Rotation;
        }

        if (rotation is null || !IsOpen(rotation.Stage))
        {
            if (!startNew)
            {
                return rotation!;
            }

            rotation = await BeginAsync(node, ct).ConfigureAwait(false);
        }

        var session = new Session(nodeId, rotation);
        try
        {
            while (true)
            {
                switch (session.Rotation.Stage)
                {
                    case CertificateRotationStage.Requested:
                    case CertificateRotationStage.CsrReceived:
                        await RequestAndSignAsync(session, ct).ConfigureAwait(false);
                        break;
                    case CertificateRotationStage.CertIssued:
                        await InstallAsync(session, ct).ConfigureAwait(false);
                        break;
                    case CertificateRotationStage.CertInstalled:
                        await ProveAndCommitAsync(session, ct).ConfigureAwait(false);
                        break;
                    case CertificateRotationStage.Committing:
                        await CommitAsync(session, lease: null, ct).ConfigureAwait(false);
                        break;
                    default:
                        RotationLog.RotationEnded(_logger, nodeId, session.Rotation.RotationId, session.Rotation.Stage);
                        return session.Rotation;
                }
            }
        }
        catch (Exception ex) when (ex is AgentException or AgentUnreachableException or PkiException or KeyNotFoundException or CertificateRotationException)
        {
            await RecordErrorAsync(session, ex.Message).ConfigureAwait(false);
            RotationLog.StepFailed(_logger, nodeId, session.Rotation.RotationId, session.Rotation.Stage, ex.Message);
            throw ex as CertificateRotationException
                ?? new CertificateRotationException($"Rotation {session.Rotation.RotationId} of node {nodeId} stopped at {session.Rotation.Stage}: {ex.Message}", ex);
        }
    }

    private async Task<CertificateRotationRecord> BeginAsync(NodeRecord node, CancellationToken ct)
    {
        DateTimeOffset now = _time.GetUtcNow();
        var fresh = new CertificateRotationRecord
        {
            RotationId = "rot-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
            NodeUid = node.Uid,
            Stage = CertificateRotationStage.Requested,
            StartedAt = now,
            UpdatedAt = now,
        };

        NodeRecord stored = await _registry.UpdateAsync(node.Id, n => n.Rotation is { } open && IsOpen(open.Stage) ? n : n with { Rotation = fresh }, ct)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException($"Node {node.Id} is not in the registry.");
        CertificateRotationRecord rotation = stored.Rotation!;
        RotationLog.RotationStarted(_logger, node.Id, rotation.RotationId);
        await HookAsync("persisted:" + rotation.Stage).ConfigureAwait(false);
        return rotation;
    }

    /// <summary>Requested / CsrReceived → CertIssued: the agent's CSR, its key recorded, a certificate signed, the pending pin stored.</summary>
    private async Task RequestAndSignAsync(Session session, CancellationToken ct)
    {
        PkiRotationCsr? csr = await StartOnAgentAsync(session, ct).ConfigureAwait(false);
        if (csr is null)
        {
            return; // The record followed the agent (abandoned rotation).
        }

        string spki = CsrSpki(session, csr);
        if (session.Rotation.Stage == CertificateRotationStage.Requested)
        {
            await PersistAsync(session, (n, r) => n with
            {
                Rotation = r with { Stage = CertificateRotationStage.CsrReceived, NewSpkiSha256 = spki, OperationId = session.LastOperationId, Error = null },
            }, ct).ConfigureAwait(false);
        }

        NodeRecord node = await GetNodeAsync(session.NodeId, ct).ConfigureAwait(false);
        using IssuedCertificate issued = IssuingAuthority.IssueAgentCertificate(csr.CsrPem, session.NodeId, SanAddresses(node), _options.AgentCertificateLifetime);
        if (!Spki.PinEquals(issued.SpkiSha256, spki))
        {
            throw new CertificateRotationException("The issued certificate does not carry the CSR's key.");
        }

        session.CertificatePem = issued.CertificatePem;
        await PersistAsync(session, (n, r) => n with
        {
            PendingCertSpkiSha256 = spki,
            Rotation = r with { Stage = CertificateRotationStage.CertIssued, NewCertNotAfter = issued.NotAfter, Error = null },
        }, ct).ConfigureAwait(false);

        // The pending pin applies to the next new connection of both existing handlers (MN-090); nothing is rebuilt.
        await _clients.RefreshNodeAsync(session.NodeId, ct).ConfigureAwait(false);
    }

    /// <summary>CertIssued → CertInstalled.</summary>
    private async Task InstallAsync(Session session, CancellationToken ct)
    {
        if (session.CertificatePem is null)
        {
            // Resumed after a master restart: the certificate is not stored, so sign the (same) CSR again.
            PkiRotationCsr? csr = await StartOnAgentAsync(session, ct).ConfigureAwait(false);
            if (csr is null)
            {
                return;
            }

            CsrSpki(session, csr);
            NodeRecord node = await GetNodeAsync(session.NodeId, ct).ConfigureAwait(false);
            using IssuedCertificate issued = IssuingAuthority.IssueAgentCertificate(csr.CsrPem, session.NodeId, SanAddresses(node), _options.AgentCertificateLifetime);
            session.CertificatePem = issued.CertificatePem;
            await PersistAsync(session, (n, r) => n with
            {
                PendingCertSpkiSha256 = r.NewSpkiSha256,
                Rotation = r with { NewCertNotAfter = issued.NotAfter },
            }, ct).ConfigureAwait(false);
            await _clients.RefreshNodeAsync(session.NodeId, ct).ConfigureAwait(false);
        }

        DateTimeOffset now = _time.GetUtcNow();
        var request = new PkiRotationCertRequest
        {
            OperationId = session.NewOperation(),
            IssuedAt = now,
            ExpiresAt = now + OperationRetention.EnvelopeLifetime,
            RotationId = session.Rotation.RotationId,
            CertPem = session.CertificatePem,
        };

        OperationStatus op;
        try
        {
            op = await _clients.GetClient(session.NodeId).InstallRotationCertificateAsync(request, ct).ConfigureAwait(false);
        }
        catch (AgentException ex) when (ex.Code is ErrorCodes.InvalidState or ErrorCodes.NotFound)
        {
            if (await FollowAgentAsync(session, ct).ConfigureAwait(false) ||
                (ex.Code == ErrorCodes.InvalidState && await AbandonUnusableKeyAsync(session, ct).ConfigureAwait(false)))
            {
                return;
            }

            throw;
        }

        await HookAsync("sent:cert").ConfigureAwait(false);
        PkiRotationStatus? status = op.ReadResult<PkiRotationStatus>();
        if (status is null || status.Stage != PkiRotationStage.CertInstalled || !Spki.PinEquals(status.PendingSpkiSha256, session.Rotation.NewSpkiSha256))
        {
            // A stored answer of an interrupted operation carries no result: read the rotation instead.
            if (await FollowAgentAsync(session, ct).ConfigureAwait(false))
            {
                return;
            }

            throw new CertificateRotationException($"The agent did not confirm the installed certificate (operation {op.State}).");
        }

        await PersistAsync(session, (n, r) => n with
        {
            Rotation = r with { Stage = CertificateRotationStage.CertInstalled, OperationId = session.LastOperationId, Error = null },
        }, ct).ConfigureAwait(false);
    }

    /// <summary>CertInstalled → Committing → Committed: prove the new certificate on a fresh connection, then commit over it.</summary>
    private async Task ProveAndCommitAsync(Session session, CancellationToken ct)
    {
        string newPin = session.Rotation.NewSpkiSha256 ?? throw new CertificateRotationException("The rotation record has no new key.");
        IAgentClientLease lease = await _clients.CreateFreshClientAsync(session.NodeId, [newPin], ct).ConfigureAwait(false);
        await using (lease.ConfigureAwait(false))
        {
            PkiRotationStatus? proof;
            try
            {
                // This handshake accepts only the new pin: it is the proof (design §10.2 step 3).
                proof = await lease.Client.GetRotationAsync(session.Rotation.RotationId, ct).ConfigureAwait(false);
            }
            catch (AgentUnreachableException ex)
            {
                if (await FollowAgentAsync(session, ct).ConfigureAwait(false))
                {
                    return;
                }

                throw new CertificateRotationException(
                    $"A fresh connection to node {session.NodeId} did not negotiate the new certificate: {ex.Message}", ex);
            }

            if (proof?.Stage != PkiRotationStage.CertInstalled || !Spki.PinEquals(proof.PendingSpkiSha256, newPin))
            {
                if (await FollowAgentAsync(session, ct).ConfigureAwait(false))
                {
                    return;
                }

                throw new CertificateRotationException($"The agent reports rotation {session.Rotation.RotationId} as {proof?.Stage.ToString() ?? "unknown"}.");
            }

            // The point of no return on the master: abort is refused from here on.
            await PersistAsync(session, (n, r) => n with
            {
                Rotation = r with { Stage = CertificateRotationStage.Committing, Error = null },
            }, ct).ConfigureAwait(false);
            await CommitAsync(session, lease, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Committing → Committed. Idempotent: a lost commit answer is resolved by reading the rotation or committing again.</summary>
    private async Task CommitAsync(Session session, IAgentClientLease? lease, CancellationToken ct)
    {
        string newPin = session.Rotation.NewSpkiSha256 ?? throw new CertificateRotationException("The rotation record has no new key.");
        IAgentClientLease? owned = null;
        if (lease is null)
        {
            // After commit the agent presents the new certificate to every new connection, before it the pending one:
            // a fresh connection pinned to the new key works in both cases.
            owned = await _clients.CreateFreshClientAsync(session.NodeId, [newPin], ct).ConfigureAwait(false);
            lease = owned;
        }

        try
        {
            DateTimeOffset now = _time.GetUtcNow();
            var request = new PkiRotationCommitRequest
            {
                OperationId = session.NewOperation(),
                IssuedAt = now,
                ExpiresAt = now + OperationRetention.EnvelopeLifetime,
                RotationId = session.Rotation.RotationId,
            };

            PkiRotationStatus? committed = null;
            for (int attempt = 0; attempt < 2 && committed is null; attempt++)
            {
                try
                {
                    OperationStatus op = await lease.Client.CommitRotationAsync(request, ct).ConfigureAwait(false);
                    committed = op.ReadResult<PkiRotationStatus>();
                }
                catch (AgentUnreachableException ex)
                {
                    RotationLog.CommitAnswerLost(_logger, session.NodeId, session.Rotation.RotationId, ex.Message);
                }

                if (committed is null)
                {
                    // Lost (or result-less) answer: the rotation as the agent knows it decides.
                    PkiRotationStatus? known = await lease.Client.GetRotationAsync(session.Rotation.RotationId, ct).ConfigureAwait(false);
                    if (known?.Stage == PkiRotationStage.Committed)
                    {
                        committed = known;
                    }
                }
            }

            await HookAsync("sent:commit").ConfigureAwait(false);
            if (committed?.Stage != PkiRotationStage.Committed || !Spki.PinEquals(committed.CurrentSpkiSha256, newPin))
            {
                throw new CertificateRotationException($"The agent did not confirm the commit of rotation {session.Rotation.RotationId}.");
            }

            await PersistAsync(session, (n, r) => n with
            {
                CertSpkiSha256 = newPin,
                PendingCertSpkiSha256 = null,
                CertNotAfter = r.NewCertNotAfter ?? committed.CertNotAfter ?? n.CertNotAfter,
                Rotation = r with { Stage = CertificateRotationStage.Committed, OperationId = session.LastOperationId, Error = null },
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }

        // The old pin is gone from the record: the refresh replaces both of the node's handlers and the old ones drain.
        await _clients.RefreshNodeAsync(session.NodeId, ct).ConfigureAwait(false);
        RotationLog.RotationCommitted(_logger, session.NodeId, session.Rotation.RotationId, newPin);
    }

    /// <summary>
    /// <c>POST /pki/rotations</c> (idempotent per rotation id). An open rotation the master has no record of (a lost
    /// record) is aborted first. Returns null when the record followed the agent instead (abandoned rotation).
    /// </summary>
    private async Task<PkiRotationCsr?> StartOnAgentAsync(Session session, CancellationToken ct)
    {
        IAgentClient client = _clients.GetClient(session.NodeId);
        for (int attempt = 0; ; attempt++)
        {
            DateTimeOffset now = _time.GetUtcNow();
            var request = new PkiRotationStartRequest
            {
                OperationId = session.NewOperation(),
                IssuedAt = now,
                ExpiresAt = now + OperationRetention.EnvelopeLifetime,
                RotationId = session.Rotation.RotationId,
            };

            OperationStatus op;
            try
            {
                op = await client.StartRotationAsync(request, ct).ConfigureAwait(false);
            }
            catch (AgentException ex) when (ex.Code == ErrorCodes.InvalidState && attempt == 0 &&
                                            ActiveRotationId(ex) is { } orphan && !string.Equals(orphan, session.Rotation.RotationId, StringComparison.Ordinal))
            {
                RotationLog.OrphanAborted(_logger, session.NodeId, orphan);
                now = _time.GetUtcNow();
                await client.AbortRotationAsync(new PkiRotationAbortRequest
                {
                    OperationId = NewOperationId(),
                    IssuedAt = now,
                    ExpiresAt = now + OperationRetention.EnvelopeLifetime,
                    RotationId = orphan,
                }, ct).ConfigureAwait(false);
                continue;
            }
            catch (AgentException ex) when (ex.Code == ErrorCodes.InvalidState)
            {
                if (await FollowAgentAsync(session, ct).ConfigureAwait(false))
                {
                    return null;
                }

                throw;
            }

            await HookAsync("sent:start").ConfigureAwait(false);
            return op.ReadResult<PkiRotationCsr>() is { } csr && string.Equals(csr.RotationId, session.Rotation.RotationId, StringComparison.Ordinal)
                ? csr
                : throw new CertificateRotationException($"The agent returned no CSR for rotation {session.Rotation.RotationId} (operation {op.State}).");
        }
    }

    /// <summary>
    /// Aligns the record with the agent's view of the rotation when they disagree, before "committing" only. Returns true
    /// when the record changed (the driver continues from the new stage).
    /// </summary>
    private async Task<bool> FollowAgentAsync(Session session, CancellationToken ct)
    {
        if (session.Rotation.Stage is CertificateRotationStage.Committing or CertificateRotationStage.Committed or CertificateRotationStage.Aborted)
        {
            return false;
        }

        PkiRotationStatus? agent;
        try
        {
            agent = await _clients.GetClient(session.NodeId).GetRotationAsync(session.Rotation.RotationId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AgentException or AgentUnreachableException)
        {
            return false;
        }

        CertificateRotationStage stage = session.Rotation.Stage;
        switch (agent?.Stage)
        {
            case null when stage != CertificateRotationStage.Requested:
                await FinishAbortAsync(session, "the agent no longer knows the rotation", ct).ConfigureAwait(false);
                return true;
            case PkiRotationStage.Aborted:
                await FinishAbortAsync(session, "the agent aborted the rotation", ct).ConfigureAwait(false);
                return true;
            case PkiRotationStage.Pending when stage is CertificateRotationStage.CertInstalled:
                // The agent dropped an unusable installed certificate at restart: install it again.
                session.CertificatePem = null;
                await PersistAsync(session, (n, r) => n with { Rotation = r with { Stage = CertificateRotationStage.CertIssued } }, ct).ConfigureAwait(false);
                return true;
            case PkiRotationStage.CertInstalled when stage is CertificateRotationStage.CertIssued &&
                                                     Spki.PinEquals(agent.PendingSpkiSha256, session.Rotation.NewSpkiSha256):
                await PersistAsync(session, (n, r) => n with { Rotation = r with { Stage = CertificateRotationStage.CertInstalled } }, ct).ConfigureAwait(false);
                return true;
            case PkiRotationStage.Committed:
                // Only a commit makes this stage, and commit follows "committing": record it and finish.
                await PersistAsync(session, (n, r) => n with { Rotation = r with { Stage = CertificateRotationStage.Committing } }, ct).ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The agent refused a certificate for its own pending key while the rotation is still Pending there: the pending key
    /// cannot be used (unreadable after a restart, W3 review MN-094). Before "committing" nothing depends on it, so the
    /// rotation is aborted on both sides instead of failing at every pass until an operator aborts it; the next pass
    /// starts a new rotation while the certificate is still due. Returns false when the agent shows anything else.
    /// </summary>
    private async Task<bool> AbandonUnusableKeyAsync(Session session, CancellationToken ct)
    {
        IAgentClient client = _clients.GetClient(session.NodeId);
        PkiRotationStatus? agent;
        try
        {
            agent = await client.GetRotationAsync(session.Rotation.RotationId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AgentException or AgentUnreachableException)
        {
            return false;
        }

        if (agent?.Stage != PkiRotationStage.Pending || !Spki.PinEquals(agent.PendingSpkiSha256, session.Rotation.NewSpkiSha256))
        {
            return false;
        }

        DateTimeOffset now = _time.GetUtcNow();
        await client.AbortRotationAsync(new PkiRotationAbortRequest
        {
            OperationId = NewOperationId(),
            IssuedAt = now,
            ExpiresAt = now + OperationRetention.EnvelopeLifetime,
            RotationId = session.Rotation.RotationId,
        }, ct).ConfigureAwait(false);
        await FinishAbortAsync(session, "the agent cannot use its pending key", ct).ConfigureAwait(false);
        return true;
    }

    private async Task FinishAbortAsync(Session session, string reason, CancellationToken ct)
    {
        string? newPin = session.Rotation.NewSpkiSha256;
        await PersistAsync(session, (n, r) => n with
        {
            PendingCertSpkiSha256 = newPin is not null && Spki.PinEquals(n.PendingCertSpkiSha256, newPin) ? null : n.PendingCertSpkiSha256,
            Rotation = r with { Stage = CertificateRotationStage.Aborted, Error = null },
        }, ct).ConfigureAwait(false);
        await _clients.RefreshNodeAsync(session.NodeId, ct).ConfigureAwait(false);
        RotationLog.RotationAborted(_logger, session.NodeId, session.Rotation.RotationId, reason);
    }

    private static string CsrSpki(Session session, PkiRotationCsr csr)
    {
        string spki = Spki.Sha256(CertificateSigningRequests.VerifyAndExtractPublicKey(csr.CsrPem));
        if (session.Rotation.NewSpkiSha256 is { } known && !Spki.PinEquals(known, spki))
        {
            throw new CertificateRotationException($"The agent returned a CSR for another key than rotation {session.Rotation.RotationId} recorded.");
        }

        return spki;
    }

    /// <summary>Atomic read-modify-write of the node; refuses when the record now holds another rotation.</summary>
    private async Task PersistAsync(Session session, Func<NodeRecord, CertificateRotationRecord, NodeRecord> change, CancellationToken ct)
    {
        DateTimeOffset now = _time.GetUtcNow();
        string rotationId = session.Rotation.RotationId;
        NodeRecord stored = await _registry.UpdateAsync(session.NodeId, n =>
        {
            if (n.Rotation is not { } current || !string.Equals(current.RotationId, rotationId, StringComparison.Ordinal))
            {
                throw new CertificateRotationException($"Rotation {rotationId} of node {session.NodeId} was replaced concurrently.");
            }

            NodeRecord next = change(n, current);
            return next with { Rotation = next.Rotation! with { UpdatedAt = now } };
        }, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Node {session.NodeId} is not in the registry.");

        session.Rotation = stored.Rotation!;
        RotationLog.StagePersisted(_logger, session.NodeId, rotationId, session.Rotation.Stage);
        await HookAsync("persisted:" + session.Rotation.Stage).ConfigureAwait(false);
    }

    private async Task RecordErrorAsync(Session session, string message)
    {
        try
        {
            NodeRecord? stored = await _registry.UpdateAsync(session.NodeId, n =>
                n.Rotation is { } current && string.Equals(current.RotationId, session.Rotation.RotationId, StringComparison.Ordinal)
                    ? n with { Rotation = current with { Error = message, UpdatedAt = _time.GetUtcNow() } }
                    : n).ConfigureAwait(false);
            if (stored?.Rotation is { } rotation && string.Equals(rotation.RotationId, session.Rotation.RotationId, StringComparison.Ordinal))
            {
                session.Rotation = rotation;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or CertificateRotationException)
        {
            RotationLog.StepFailed(_logger, session.NodeId, session.Rotation.RotationId, session.Rotation.Stage, "the error could not be recorded: " + ex.Message);
        }
    }

    private async Task<NodeRecord> GetNodeAsync(string nodeId, CancellationToken ct)
    {
        NodeRecord node = await _registry.GetAsync(nodeId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Node {nodeId} is not in the registry.");
        return node.Revoked ? throw new InvalidOperationException($"Node {nodeId} is revoked.") : node;
    }

    private Task HookAsync(string point) => FaultInjection?.Invoke(point) ?? Task.CompletedTask;

    /// <summary>The SAN addresses of a re-issued certificate: the hosts the master reaches the agent at.</summary>
    private static List<string> SanAddresses(NodeRecord node)
    {
        List<string> hosts = [];
        foreach (string address in new[] { node.Address }.Concat(node.AltAddresses))
        {
            if (AgentClientFactory.TryParseAddress(address, out Uri? uri) && !hosts.Contains(uri!.DnsSafeHost, StringComparer.OrdinalIgnoreCase))
            {
                hosts.Add(uri.DnsSafeHost);
            }
        }

        return hosts;
    }

    private static string? ActiveRotationId(AgentException ex)
    {
        if (ex.Error.Details is { ValueKind: JsonValueKind.Object } details &&
            details.TryGetProperty("activeRotationId", out JsonElement id) && id.ValueKind == JsonValueKind.String)
        {
            return id.GetString();
        }

        return null;
    }

    /// <summary>One drive of one rotation: the record as last persisted and what is only kept in memory.</summary>
    private sealed class Session(string nodeId, CertificateRotationRecord rotation)
    {
        public string NodeId { get; } = nodeId;

        public CertificateRotationRecord Rotation { get; set; } = rotation;

        /// <summary>The signed certificate (public). Not persisted: after a restart the CSR is signed again.</summary>
        public string? CertificatePem { get; set; }

        public string? LastOperationId { get; private set; } = rotation.OperationId;

        public string NewOperation() => LastOperationId = NewOperationId();
    }
}

/// <summary>Source-generated log messages of the master's certificate rotation. Ids, stages and SPKI pins only.</summary>
internal static partial class RotationLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Certificate of node {NodeId} is due for renewal (expires {NotAfter:O}).")]
    public static partial void RenewalDue(ILogger logger, string nodeId, DateTimeOffset? notAfter);

    [LoggerMessage(Level = LogLevel.Information, Message = "Certificate rotation {RotationId} of node {NodeId} started.")]
    public static partial void RotationStarted(ILogger logger, string nodeId, string rotationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Certificate rotation {RotationId} of node {NodeId}: stage {Stage} persisted.")]
    public static partial void StagePersisted(ILogger logger, string nodeId, string rotationId, CertificateRotationStage stage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Certificate rotation {RotationId} of node {NodeId} committed: pin {Spki} promoted, both handlers replaced.")]
    public static partial void RotationCommitted(ILogger logger, string nodeId, string rotationId, string spki);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Certificate rotation {RotationId} of node {NodeId} aborted: {Reason}.")]
    public static partial void RotationAborted(ILogger logger, string nodeId, string rotationId, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Certificate rotation {RotationId} of node {NodeId} ended at {Stage}.")]
    public static partial void RotationEnded(ILogger logger, string nodeId, string rotationId, CertificateRotationStage stage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Certificate rotation {RotationId} of node {NodeId} stopped at {Stage}: {Reason}")]
    public static partial void StepFailed(ILogger logger, string nodeId, string rotationId, CertificateRotationStage stage, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The commit answer of rotation {RotationId} (node {NodeId}) was lost: {Reason}; reading the rotation.")]
    public static partial void CommitAnswerLost(ILogger logger, string nodeId, string rotationId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {NodeId} had an open rotation {RotationId} the master has no record of; aborting it.")]
    public static partial void OrphanAborted(ILogger logger, string nodeId, string rotationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Certificate renewal of node {NodeId} failed: {Reason}")]
    public static partial void RotationFailed(ILogger logger, string nodeId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Master certificates re-issued at half life: {Change}.")]
    public static partial void MasterCertificatesReissued(ILogger logger, MasterCertificateChange change);

    [LoggerMessage(Level = LogLevel.Error, Message = "The certificate rotation pass failed.")]
    public static partial void PassFailed(ILogger logger, Exception exception);
}
