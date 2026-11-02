using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Core;
using MirbadLLM.Pki;

namespace MirbadLLM.Agent.Pki;

/// <summary>
/// The agent side of the PKI routes (design §4.3 "PKI", §10.2): two-phase certificate rotation and the master trust
/// anchors. Every mutation is an operation (<see cref="IOperationLog"/>): refusals are decided before the durable accept
/// and record nothing (D41); a repeated operation id returns the stored answer. Rotation steps are also idempotent per
/// <b>rotation id</b>, so the master may retry a step under a new operation id after a crash.
/// <list type="number">
/// <item><b>Start</b>: a new P-256 key is written (0600) before the stage that names it; the CSR is stored and returned
/// again on a repeated start. One open rotation at a time (409 <c>invalid_state</c> naming the open one).</item>
/// <item><b>Cert</b>: the certificate must be for the pending key, for this node, and chain to a trust anchor; it is
/// written, the stage persisted, and from then on presented to new connections (the old one stays on established ones).</item>
/// <item><b>Commit</b>: accepted only over a connection that negotiated the pending certificate (the master's fresh
/// connection, step 3). "Committed" is persisted first (irreversible); then the pending key and certificate replace
/// <c>agent.key</c>/<c>agent.crt</c> (the old key is deleted only now) and the pending files are removed. A repeated
/// commit succeeds.</item>
/// <item><b>Abort</b>: only before commit (afterwards 409 <c>already_committed</c>); drops the pending material.</item>
/// </list>
/// Thread-safe: one gate serializes every PKI mutation. Never logs key material.
/// </summary>
public sealed partial class AgentPkiService : IDisposable
{
    private readonly RotatingAgentCredentials _credentials;
    private readonly IOperationLog _log;
    private readonly IFencingState _fencing;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentPkiService> _logger;
    private readonly AgentListenerTls? _listener;
    private readonly IEventBus? _events;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the service.</summary>
    public AgentPkiService(
        RotatingAgentCredentials credentials,
        IOperationLog log,
        IFencingState fencing,
        TimeProvider time,
        ILogger<AgentPkiService> logger,
        AgentListenerTls? listener = null,
        IEventBus? events = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(fencing);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _credentials = credentials;
        _log = log;
        _fencing = fencing;
        _time = time;
        _logger = logger;
        _listener = listener;
        _events = events;
    }

    /// <summary>The PKI operation kinds (for recovery).</summary>
    public static IReadOnlyList<string> Kinds { get; } =
    [
        OperationKinds.PkiRotationStart, OperationKinds.PkiRotationCert, OperationKinds.PkiRotationCommit,
        OperationKinds.PkiRotationAbort, OperationKinds.PkiTrustSet,
    ];

    private AgentRotationStore Store => _credentials.Store;

    /// <summary><c>POST /pki/rotations</c>: pending key + CSR (same CSR on retry).</summary>
    public async Task<OperationStatus> StartAsync(PkiRotationStartRequest request, MasterTriple master, JsonElement body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await ReplayAsync(request, OperationKinds.PkiRotationStart, request.RotationId, master, body, ct).ConfigureAwait(false) is { } stored)
            {
                return stored;
            }

            RequireRotationId(request.RotationId);
            RequireEnrolled();
            AgentRotationDocument document = Store.Document;
            AgentRotationEntry? existing = document.Find(request.RotationId);
            if (existing is not null && existing.Stage is not (PkiRotationStage.Pending or PkiRotationStage.CertInstalled))
            {
                throw Refuse(ErrorCodes.InvalidState, $"Rotation {existing.RotationId} is {existing.Stage}; start a new rotation id.");
            }

            if (existing is null && document.Active is { } active)
            {
                throw Refuse(ErrorCodes.InvalidState, $"Rotation {active.RotationId} is still {active.Stage}; commit or abort it first.",
                    new { activeRotationId = active.RotationId, stage = active.Stage });
            }

            OperationStatus op = await AcceptAsync(request, OperationKinds.PkiRotationStart, request.RotationId, master, body, ct).ConfigureAwait(false);
            AgentRotationEntry entry;
            try
            {
                entry = existing ?? CreatePendingKey(request.RotationId);
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                return await FailAsync(op, ex).ConfigureAwait(false);
            }

            await StageAsync(op, OperationStages.KeyPending).ConfigureAwait(false);
            return await CompleteAsync(op, new PkiRotationCsr(entry.RotationId, entry.CsrPem)).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary><c>PUT /pki/rotations/{rotationId}/cert</c>: install the new certificate as pending (presented on new connections).</summary>
    public async Task<OperationStatus> InstallCertificateAsync(string rotationId, PkiRotationCertRequest request, MasterTriple master, JsonElement body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await ReplayAsync(request, OperationKinds.PkiRotationCert, rotationId, master, body, ct).ConfigureAwait(false) is { } stored)
            {
                return stored;
            }

            RequireSameRotation(request.RotationId, rotationId);
            AgentRotationEntry entry = Store.Document.Find(rotationId) ?? throw NotFound(rotationId);
            if (entry.Stage is not (PkiRotationStage.Pending or PkiRotationStage.CertInstalled))
            {
                throw Refuse(ErrorCodes.InvalidState, $"Rotation {rotationId} is {entry.Stage}.");
            }

            RequireEnrolled();
            (string leafPem, DateTimeOffset notAfter) = CheckIssuedCertificate(request.CertPem, entry);
            X509Certificate2 tls;
            try
            {
                tls = _credentials.LoadPending(leafPem, entry.PendingSpkiSha256);
            }
            catch (Exception ex) when (ex is IOException or CryptographicException or PkiException)
            {
                throw Refuse(ErrorCodes.InvalidState, $"Rotation {rotationId}: the pending key cannot be used ({ex.GetType().Name}); abort it and start again.");
            }

            OperationStatus op = await AcceptAsync(request, OperationKinds.PkiRotationCert, rotationId, master, body, ct).ConfigureAwait(false);
            try
            {
                Store.WritePendingCertificate(leafPem);
                Store.FaultInjection?.Invoke("cert:written");
                entry = entry with { Stage = PkiRotationStage.CertInstalled, CertNotAfter = notAfter, UpdatedAt = _time.GetUtcNow() };
                Store.Save(Store.Document with { Active = entry });
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                tls.Dispose();
                return await FailAsync(op, ex).ConfigureAwait(false);
            }

            _credentials.SetPending(tls);
            _listener?.Refresh();
            AgentPkiLog.RotationStage(_logger, rotationId, entry.Stage, entry.PendingSpkiSha256);
            await StageAsync(op, OperationStages.CertInstalled).ConfigureAwait(false);
            return await CompleteAsync(op, StatusOf(entry)).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// <c>POST /pki/rotations/{rotationId}/commit</c>. <paramref name="connectionSpki"/> is the SPKI pin of the server
    /// certificate the request's TLS connection negotiated (null when unknown): before the commit it must be the pending
    /// one, which proves the master reached the agent over a fresh connection with the new certificate (§10.2 step 3).
    /// </summary>
    public async Task<OperationStatus> CommitAsync(string rotationId, PkiRotationCommitRequest request, MasterTriple master, JsonElement body, string? connectionSpki, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await ReplayAsync(request, OperationKinds.PkiRotationCommit, rotationId, master, body, ct).ConfigureAwait(false) is { } stored)
            {
                return stored;
            }

            RequireSameRotation(request.RotationId, rotationId);
            AgentRotationEntry entry = Store.Document.Find(rotationId) ?? throw NotFound(rotationId);
            switch (entry.Stage)
            {
                case PkiRotationStage.Pending:
                    throw Refuse(ErrorCodes.InvalidState, $"Rotation {rotationId} has no installed certificate yet.");
                case PkiRotationStage.Aborted:
                    throw Refuse(ErrorCodes.InvalidState, $"Rotation {rotationId} was aborted.");
                case PkiRotationStage.CertInstalled when !Spki.PinEquals(connectionSpki, entry.PendingSpkiSha256):
                    throw Refuse(ErrorCodes.InvalidState,
                        $"Rotation {rotationId} must be committed over a fresh connection that negotiated the new certificate.");
                case PkiRotationStage.CertInstalled when !_fencing.IsCurrent(master):
                    throw Refuse(ErrorCodes.StaleMaster, "Another master took over this node; the rotation is not committed.");
            }

            OperationStatus op = await AcceptAsync(request, OperationKinds.PkiRotationCommit, rotationId, master, body, ct).ConfigureAwait(false);
            if (entry.Stage == PkiRotationStage.CertInstalled)
            {
                entry = entry with { Stage = PkiRotationStage.Committed, Finalized = false, UpdatedAt = _time.GetUtcNow() };
                try
                {
                    // The point of no return: on disk before the old key is replaced.
                    Store.Save(Store.Document with { Active = entry });
                }
                catch (Exception ex) when (IsStorageFailure(ex))
                {
                    return await FailAsync(op, ex).ConfigureAwait(false);
                }

                AgentPkiLog.RotationStage(_logger, rotationId, entry.Stage, entry.PendingSpkiSha256);
                Store.FaultInjection?.Invoke("commit:persisted");
                await StageAsync(op, OperationStages.Committed).ConfigureAwait(false);
            }

            if (entry.Stage == PkiRotationStage.Committed && !entry.Finalized)
            {
                try
                {
                    _credentials.FinishCommit(entry);
                    _listener?.Refresh();
                }
                catch (Exception ex) when (IsStorageFailure(ex))
                {
                    // Committed stays committed: the pending certificate is still presented, and the next commit retry or
                    // agent start finishes the promotion.
                    AgentPkiLog.PromotionFailed(_logger, ex, rotationId);
                }

                entry = Store.Document.Find(rotationId) ?? entry;
            }

            return await CompleteAsync(op, StatusOf(entry)).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary><c>POST /pki/rotations/{rotationId}/abort</c>: before commit only.</summary>
    public async Task<OperationStatus> AbortAsync(string rotationId, PkiRotationAbortRequest request, MasterTriple master, JsonElement body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await ReplayAsync(request, OperationKinds.PkiRotationAbort, rotationId, master, body, ct).ConfigureAwait(false) is { } stored)
            {
                return stored;
            }

            RequireSameRotation(request.RotationId, rotationId);
            AgentRotationEntry entry = Store.Document.Find(rotationId) ?? throw NotFound(rotationId);
            if (entry.Stage == PkiRotationStage.Committed)
            {
                throw Refuse(ErrorCodes.AlreadyCommitted, $"Rotation {rotationId} is committed; it cannot be aborted.");
            }

            OperationStatus op = await AcceptAsync(request, OperationKinds.PkiRotationAbort, rotationId, master, body, ct).ConfigureAwait(false);
            if (entry.Stage != PkiRotationStage.Aborted)
            {
                entry = entry with { Stage = PkiRotationStage.Aborted, UpdatedAt = _time.GetUtcNow() };
                try
                {
                    Store.Save(Store.Document.Finish(entry));
                    Store.FaultInjection?.Invoke("abort:persisted");
                    _credentials.SetPending(null);
                    _listener?.Refresh();
                    Store.DeletePendingFiles();
                }
                catch (Exception ex) when (IsStorageFailure(ex))
                {
                    return await FailAsync(op, ex).ConfigureAwait(false);
                }

                AgentPkiLog.RotationStage(_logger, rotationId, entry.Stage, entry.PendingSpkiSha256);
            }

            await StageAsync(op, OperationStages.Aborted).ConfigureAwait(false);
            return await CompleteAsync(op, StatusOf(entry)).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary><c>GET /pki/rotations/{rotationId}</c>; 404 <c>not_found</c> when the agent does not know it.</summary>
    public PkiRotationStatus GetRotation(string rotationId)
    {
        AgentRotationEntry entry = Store.Document.Find(rotationId) ?? throw NotFound(rotationId);
        return StatusOf(entry);
    }

    /// <summary><c>GET /pki/trust</c>.</summary>
    public PkiTrustResponse GetTrust() => new()
    {
        Anchors = [.. _credentials.TrustAnchors.Select(TrustAnchorSet.Describe)],
        MasterSan = Protocol.IssuedMasterSpiffeId,
    };

    /// <summary>
    /// <c>PUT /pki/trust</c> (CA rotation, design §10.2; the procedure is MN-116): replaces the anchors masters must chain
    /// to. The new set must still validate <paramref name="caller"/> (the calling master's client certificate), else 409
    /// <c>invalid_state</c>. <c>ca.crt</c> is replaced atomically and the listener's master validator swapped, so the next
    /// handshake uses the new anchors.
    /// </summary>
    public async Task<OperationStatus> SetTrustAsync(PkiTrustSetRequest request, MasterTriple master, JsonElement body, X509Certificate2? caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await ReplayAsync(request, OperationKinds.PkiTrustSet, "", master, body, ct).ConfigureAwait(false) is { } stored)
            {
                return stored;
            }

            RequireEnrolled();
            using TrustAnchorSet next = ParseAnchors(request.Anchors);
            if (caller is null || !PeerValidator.ForMasterClient(next, _time).Validate(caller).IsValid)
            {
                throw Refuse(ErrorCodes.InvalidState, "The new anchors must keep one that validates the calling master.");
            }

            IReadOnlyList<string> pins = next.SpkiSha256s;
            OperationStatus op = await AcceptAsync(request, OperationKinds.PkiTrustSet, "", master, body, ct).ConfigureAwait(false);
            try
            {
                Store.WriteTrustAnchors(next.ToPem());
                _credentials.Files.Reload();
                if (!pins.All(pin => _credentials.TrustAnchors.Any(a => Spki.Matches(a, pin))) ||
                    _credentials.TrustAnchors.Count != next.Anchors.Count)
                {
                    throw new IOException("The new trust anchors did not load.");
                }
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                return await FailAsync(op, ex).ConfigureAwait(false);
            }

            _listener?.SetMasterTrust(next);
            AgentPkiLog.TrustReplaced(_logger, pins);
            return await CompleteAsync(op, GetTrust()).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>The <see cref="PkiRotationStatus"/> of an entry (also used by recovery).</summary>
    internal PkiRotationStatus StatusOf(AgentRotationEntry entry) => new()
    {
        RotationId = entry.RotationId,
        Stage = entry.Stage,
        CurrentSpkiSha256 = entry.Stage == PkiRotationStage.Committed && !entry.Finalized
            ? entry.PendingSpkiSha256
            : _credentials.CurrentSpkiSha256,
        PendingSpkiSha256 = entry.Stage is PkiRotationStage.Pending or PkiRotationStage.CertInstalled ? entry.PendingSpkiSha256 : null,
        CertNotAfter = entry.CertNotAfter,
        UpdatedAt = entry.UpdatedAt,
    };

    private AgentRotationEntry CreatePendingKey(string rotationId)
    {
        using ECDsa key = PkiDefaults.CreateKey();
        string csr = CertificateSigningRequests.CreatePem(key, _credentials.NodeId);
        string spki = Spki.Sha256(key);
        Store.WritePendingKey(key.ExportPkcs8PrivateKeyPem());
        Store.FaultInjection?.Invoke("start:key-written");
        DateTimeOffset now = _time.GetUtcNow();
        AgentRotationEntry entry = new()
        {
            RotationId = rotationId,
            Stage = PkiRotationStage.Pending,
            CsrPem = csr,
            PendingSpkiSha256 = spki,
            PreviousSpkiSha256 = _credentials.CurrentSpkiSha256,
            StartedAt = now,
            UpdatedAt = now,
        };
        Store.Save(Store.Document with { Active = entry });
        AgentPkiLog.RotationStage(_logger, rotationId, entry.Stage, spki);
        return entry;
    }

    private (string LeafPem, DateTimeOffset NotAfter) CheckIssuedCertificate(string certificatePem, AgentRotationEntry entry)
    {
        if (string.IsNullOrWhiteSpace(certificatePem) || certificatePem.Length > 64 * 1024)
        {
            throw Refuse(ErrorCodes.InvalidRequest, "certPem must hold the issued certificate (PEM).");
        }

        try
        {
            using X509Certificate2 certificate = X509Certificate2.CreateFromPem(certificatePem);
            if (!Spki.Matches(certificate, entry.PendingSpkiSha256))
            {
                throw Refuse(ErrorCodes.InvalidRequest, "The certificate is not for the pending key of this rotation.");
            }

            using TrustAnchorSet trust = TrustAnchorSet.Create(_credentials.TrustAnchors);
            PeerValidationResult validation = PeerValidator.ForAgent(_credentials.NodeId, trust, [entry.PendingSpkiSha256], _time).Validate(certificate);
            if (!validation.IsValid)
            {
                throw Refuse(ErrorCodes.InvalidRequest, $"The certificate is not a valid agent certificate for {_credentials.NodeId}: {validation.Failure}.");
            }

            return (certificate.ExportCertificatePem(), new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero));
        }
        catch (Exception ex) when (ex is CryptographicException or PkiException or ArgumentException)
        {
            throw Refuse(ErrorCodes.InvalidRequest, "The certificate could not be read.");
        }
    }

    private static TrustAnchorSet ParseAnchors(IReadOnlyList<string>? anchors)
    {
        if (anchors is null || anchors.Count == 0 || anchors.Any(string.IsNullOrWhiteSpace))
        {
            throw Refuse(ErrorCodes.InvalidRequest, "anchors must hold 1 to 2 CA certificates (PEM).");
        }

        try
        {
            return TrustAnchorSet.FromPem(anchors);
        }
        catch (Exception ex) when (ex is PkiException or CryptographicException or ArgumentException)
        {
            throw Refuse(ErrorCodes.InvalidRequest, "The anchors are not 1 to 2 CA certificates: " + ex.Message);
        }
    }

    /// <summary>
    /// A known operation id: the stored answer (or 409 <c>operation_id_conflict</c>), without re-validating against the
    /// current state. Null for a new id.
    /// </summary>
    private async Task<OperationStatus?> ReplayAsync(AgentMutation request, string kind, string target, MasterTriple master, JsonElement body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId) || _log.Find(request.OperationId) is null)
        {
            return null;
        }

        OperationStatus status = await AcceptAsync(request, kind, target, master, body, ct, replay: true).ConfigureAwait(false);
        return status.State == OperationState.Failed && status.Error is { } error
            ? throw new AgentException(ErrorMappings.AgentStatus(error.Code), error)
            : status;
    }

    private async Task<OperationStatus> AcceptAsync(AgentMutation request, string kind, string target, MasterTriple master, JsonElement body, CancellationToken ct, bool replay = false)
    {
        OperationAcceptance acceptance;
        try
        {
            acceptance = await _log.AcceptAsync(new OperationSubmission
            {
                OperationId = request.OperationId,
                Kind = kind,
                Target = target,
                IssuedAt = request.IssuedAt,
                ExpiresAt = request.ExpiresAt,
                Master = master,
                Payload = body,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or AgentException))
        {
            AgentPkiLog.StateUnavailable(_logger, ex, request.OperationId, kind);
            throw Refuse(ErrorCodes.StateUnavailable, "The agent could not durably record the operation; nothing was done.");
        }

        return acceptance.Outcome switch
        {
            OperationAcceptOutcome.Accepted when !replay && acceptance.Status is { } accepted => accepted,
            OperationAcceptOutcome.Duplicate when acceptance.Status is { } stored => stored,
            OperationAcceptOutcome.Conflict => throw Refuse(ErrorCodes.OperationIdConflict,
                $"Operation id {request.OperationId} was already used for another action, target or payload."),
            OperationAcceptOutcome.Expired => throw Refuse(ErrorCodes.OperationExpired,
                $"Operation {request.OperationId} expired at {request.ExpiresAt:O}; nothing was done."),
            OperationAcceptOutcome.IssuedInFuture => throw Refuse(ErrorCodes.ClockSkew,
                "The operation's issuedAt is more than 5 minutes ahead of the agent clock."),
            _ => throw Refuse(ErrorCodes.InternalError, "The operation log returned no status."),
        };
    }

    private async Task StageAsync(OperationStatus op, string stage)
    {
        try
        {
            await _log.RecordStageAsync(op.Id, new OperationStageUpdate { Stage = stage }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The rotation files are authoritative; the terminal record below (or recovery) settles the operation.
            AgentPkiLog.StateUnavailable(_logger, ex, op.Id, op.Kind);
        }
    }

    private async Task<OperationStatus> CompleteAsync<T>(OperationStatus op, T result)
    {
        OperationStatus final;
        try
        {
            final = await _log.CompleteAsync(op.Id, OperationState.Succeeded, result: AgentJson.ToElement(result), ct: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AgentPkiLog.StateUnavailable(_logger, ex, op.Id, op.Kind);
            throw Refuse(ErrorCodes.StateUnavailable,
                "The step was done but the agent could not record the operation; read the rotation (GET /pki/rotations/{id}) before acting again.");
        }

        _events?.Publish(new OperationEvent { Operation = final, At = _time.GetUtcNow() });
        return final;
    }

    private async Task<OperationStatus> FailAsync(OperationStatus op, Exception cause)
    {
        AgentPkiLog.StateUnavailable(_logger, cause, op.Id, op.Kind);
        AgentError error = AgentErrorResults.Create(ErrorCodes.StateUnavailable, "The agent could not write its PKI state; nothing was changed. Retry the step.");
        try
        {
            OperationStatus final = await _log.CompleteAsync(op.Id, OperationState.Failed, error, ct: CancellationToken.None).ConfigureAwait(false);
            _events?.Publish(new OperationEvent { Operation = final, At = _time.GetUtcNow() });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AgentPkiLog.StateUnavailable(_logger, ex, op.Id, op.Kind);
        }

        throw new AgentException(ErrorMappings.AgentStatus(error.Code), error);
    }

    private void RequireEnrolled()
    {
        if (!_credentials.IsEnrolled)
        {
            throw Refuse(ErrorCodes.InvalidState, "The agent is not enrolled.");
        }
    }

    private static void RequireRotationId(string? rotationId)
    {
        if (rotationId is null || !RotationIdRegex().IsMatch(rotationId))
        {
            throw Refuse(ErrorCodes.InvalidRequest, "rotationId must be 1-64 characters of letters, digits, '.', '_' or '-'.");
        }
    }

    private static void RequireSameRotation(string? bodyId, string pathId)
    {
        if (!string.Equals(bodyId, pathId, StringComparison.Ordinal))
        {
            throw Refuse(ErrorCodes.InvalidRequest, $"The body names rotation '{bodyId}' but the path names '{pathId}'.");
        }
    }

    private static bool IsStorageFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or CryptographicException or PkiException or InvalidDataException;

    private static AgentException NotFound(string rotationId) =>
        Refuse(ErrorCodes.NotFound, $"No rotation {rotationId} on this agent.");

    private static AgentException Refuse(string code, string message, object? details = null) =>
        new(ErrorMappings.AgentStatus(code), AgentErrorResults.Create(code, message, details));

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex RotationIdRegex();
}
