using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MirbadLLM.Agent.Api;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Pki;

namespace MirbadLLM.UnitTests.Multinode.Fakes;

/// <summary>
/// PKI on the agent (design §4.3 "PKI", §10.2): the listener's certificate is chosen per handshake, so a rotation's
/// pending certificate is presented on new connections only while pooled ones keep the old one; commit promotes it and
/// drops the old key (irreversible, idempotent); abort before commit restores the old one. Trust anchors are swapped
/// per handshake through a <see cref="PeerValidatorSource"/> and must keep one anchor that validates the caller.
/// </summary>
internal sealed partial class FakeAgent
{
    private readonly Dictionary<string, FakeRotation> _rotations = new(StringComparer.Ordinal);
    private TrustAnchorSet _trust = null!;
    private PeerValidatorSource _masterValidator = null!;
    private X509Certificate2 _currentTls = null!;
    private string _currentSpki = "";
    private DateTimeOffset _currentNotAfter;
    private X509Certificate2? _pendingTls;

    /// <summary>SPKI pin of the certificate presented to new connections after the last commit.</summary>
    public string CurrentSpki
    {
        get
        {
            lock (_gate)
            {
                return _currentSpki;
            }
        }
    }

    private void InitializePki()
    {
        using var key = PkiDefaults.CreateKey();
        using var issued = _ca.IssueAgentCertificate(CertificateSigningRequests.CreatePem(key, NodeId), NodeId, ["127.0.0.1"]);
        _currentTls = TlsCredentials.FromPem(issued.CertificatePem, key.ExportPkcs8PrivateKeyPem());
        _currentSpki = issued.SpkiSha256;
        _currentNotAfter = issued.NotAfter;
        _trust = TrustAnchorSet.Create([_ca.Certificate]);
        _masterValidator = new PeerValidatorSource(PeerValidator.ForMasterClient(_trust));
    }

    private X509Certificate2 SelectServerCertificate()
    {
        lock (_gate)
        {
            return _pendingTls ?? _currentTls;
        }
    }

    private void DisposePkiLocked()
    {
        _currentTls.Dispose();
        _pendingTls?.Dispose();
        _trust.Dispose();
        foreach (var rotation in _rotations.Values)
        {
            rotation.Key.Dispose();
        }
    }

    private void Retire(IDisposable? disposable)
    {
        if (disposable is not null)
        {
            _retired.Add(disposable);
        }
    }

    private PkiRotationStatus RotationStatusLocked(FakeRotation rotation) => new()
    {
        RotationId = rotation.Id,
        Stage = rotation.Stage,
        CurrentSpkiSha256 = _currentSpki,
        PendingSpkiSha256 = rotation.Stage is PkiRotationStage.Pending or PkiRotationStage.CertInstalled ? rotation.PendingSpki : null,
        CertNotAfter = rotation.Stage == PkiRotationStage.Committed ? _currentNotAfter : rotation.CertNotAfter,
        UpdatedAt = rotation.UpdatedAt,
    };

    private FakeRotation RotationLocked(string rotationId) =>
        _rotations.TryGetValue(rotationId, out var rotation) ? rotation : throw Fail(ErrorCodes.NotFound, $"No rotation {rotationId} on node {NodeId}.");

    private static void RequireSameRotation(string bodyId, string pathId)
    {
        if (!string.Equals(bodyId, pathId, StringComparison.Ordinal))
        {
            throw Fail(ErrorCodes.InvalidRequest, $"The body names rotation '{bodyId}' but the path names '{pathId}'.");
        }
    }

    private FakeReply CompletedPki(FakeCall call, object result)
    {
        var op = AcceptLocked(call);
        CompleteLocked(op, OperationState.Succeeded, result: result);
        return FakeReply.Json(op.ToStatus(), call.Route.SuccessStatus);
    }

    private FakeReply RotationStart(FakeCall call)
    {
        var request = call.Request<PkiRotationStartRequest>();
        lock (_gate)
        {
            if (TryDuplicateLocked(call, out var duplicate))
            {
                return duplicate;
            }

            if (_rotations.TryGetValue(request.RotationId, out var existing))
            {
                // Repeating the same rotation returns the same CSR (§4.3).
                return existing.Stage is PkiRotationStage.Pending or PkiRotationStage.CertInstalled
                    ? CompletedPki(call, new PkiRotationCsr(existing.Id, existing.CsrPem))
                    : throw Fail(ErrorCodes.InvalidState, $"Rotation {existing.Id} is {existing.Stage}.");
            }

            if (_rotations.Values.FirstOrDefault(r => r.Stage is PkiRotationStage.Pending or PkiRotationStage.CertInstalled) is { } active)
            {
                // As the real agent: the details name the open rotation, so a master without a record of it can abort it.
                throw Fail(ErrorCodes.InvalidState, $"Rotation {active.Id} is still {active.Stage}; commit or abort it first.",
                    new { activeRotationId = active.Id, stage = active.Stage });
            }

            var key = PkiDefaults.CreateKey();
            var rotation = new FakeRotation(request.RotationId, key, CertificateSigningRequests.CreatePem(key, NodeId), Spki.Sha256(key))
            {
                Stage = PkiRotationStage.Pending,
                UpdatedAt = Now,
            };
            _rotations[rotation.Id] = rotation;
            var op = AcceptLocked(call);
            StageLocked(op, OperationStages.KeyPending);
            CompleteLocked(op, OperationState.Succeeded, result: new PkiRotationCsr(rotation.Id, rotation.CsrPem));
            return FakeReply.Json(op.ToStatus(), call.Route.SuccessStatus);
        }
    }

    private FakeReply RotationCert(FakeCall call)
    {
        var request = call.Request<PkiRotationCertRequest>();
        lock (_gate)
        {
            if (TryDuplicateLocked(call, out var duplicate))
            {
                return duplicate;
            }

            RequireSameRotation(request.RotationId, call.Target);
            var rotation = RotationLocked(call.Target);
            if (rotation.Stage is PkiRotationStage.Committed or PkiRotationStage.Aborted)
            {
                throw Fail(ErrorCodes.InvalidState, $"Rotation {rotation.Id} is {rotation.Stage}.");
            }

            X509Certificate2 pending;
            DateTimeOffset notAfter;
            try
            {
                using var certificate = X509Certificate2.CreateFromPem(request.CertPem);
                if (!Spki.Matches(certificate, rotation.PendingSpki))
                {
                    throw Fail(ErrorCodes.InvalidRequest, "The certificate is not for the pending key of this rotation.");
                }

                var validation = PeerValidator.ForAgent(NodeId, _trust, [rotation.PendingSpki]).Validate(certificate);
                if (!validation.IsValid)
                {
                    throw Fail(ErrorCodes.InvalidRequest, $"The certificate is not a valid agent certificate for {NodeId}: {validation.Failure}.");
                }

                notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
                pending = TlsCredentials.FromPem(request.CertPem, rotation.Key.ExportPkcs8PrivateKeyPem());
            }
            catch (Exception ex) when (ex is PkiException or CryptographicException or ArgumentException)
            {
                throw Fail(ErrorCodes.InvalidRequest, "The certificate could not be read: " + ex.Message);
            }

            Retire(_pendingTls);
            _pendingTls = pending;
            rotation.Stage = PkiRotationStage.CertInstalled;
            rotation.CertNotAfter = notAfter;
            rotation.UpdatedAt = Now;
            var op = AcceptLocked(call);
            StageLocked(op, OperationStages.CertInstalled);
            CompleteLocked(op, OperationState.Succeeded, result: RotationStatusLocked(rotation));
            return FakeReply.Json(op.ToStatus(), call.Route.SuccessStatus);
        }
    }

    private FakeReply RotationCommit(FakeCall call)
    {
        var request = call.Request<PkiRotationCommitRequest>();
        lock (_gate)
        {
            if (TryDuplicateLocked(call, out var duplicate))
            {
                return duplicate;
            }

            RequireSameRotation(request.RotationId, call.Target);
            var rotation = RotationLocked(call.Target);
            switch (rotation.Stage)
            {
                case PkiRotationStage.Committed:
                    return CompletedPki(call, RotationStatusLocked(rotation));
                case PkiRotationStage.Pending:
                    throw Fail(ErrorCodes.InvalidState, $"Rotation {rotation.Id} has no installed certificate yet.");
                case PkiRotationStage.Aborted:
                    throw Fail(ErrorCodes.InvalidState, $"Rotation {rotation.Id} was aborted.");
                case PkiRotationStage.CertInstalled when !Spki.PinEquals(PkiEndpoints.ConnectionSpki(call.Http), rotation.PendingSpki):
                    // As the real agent (§10.2 step 3): only a connection that negotiated the new certificate commits.
                    throw Fail(ErrorCodes.InvalidState,
                        $"Rotation {rotation.Id} must be committed over a fresh connection that negotiated the new certificate.");
            }

            Retire(_currentTls);
            _currentTls = _pendingTls!;
            _pendingTls = null;
            _currentSpki = rotation.PendingSpki;
            _currentNotAfter = rotation.CertNotAfter ?? _currentNotAfter;
            rotation.Stage = PkiRotationStage.Committed;
            rotation.UpdatedAt = Now;
            var op = AcceptLocked(call);
            StageLocked(op, OperationStages.Committed);
            CompleteLocked(op, OperationState.Succeeded, result: RotationStatusLocked(rotation));
            return FakeReply.Json(op.ToStatus(), call.Route.SuccessStatus);
        }
    }

    private FakeReply RotationAbort(FakeCall call)
    {
        var request = call.Request<PkiRotationAbortRequest>();
        lock (_gate)
        {
            if (TryDuplicateLocked(call, out var duplicate))
            {
                return duplicate;
            }

            RequireSameRotation(request.RotationId, call.Target);
            var rotation = RotationLocked(call.Target);
            if (rotation.Stage == PkiRotationStage.Committed)
            {
                throw Fail(ErrorCodes.AlreadyCommitted, $"Rotation {rotation.Id} is committed; it cannot be aborted.");
            }

            if (rotation.Stage != PkiRotationStage.Aborted)
            {
                if (rotation.Stage == PkiRotationStage.CertInstalled)
                {
                    Retire(_pendingTls);
                    _pendingTls = null;
                }

                rotation.Stage = PkiRotationStage.Aborted;
                rotation.UpdatedAt = Now;
            }

            var op = AcceptLocked(call);
            StageLocked(op, OperationStages.Aborted);
            CompleteLocked(op, OperationState.Succeeded, result: RotationStatusLocked(rotation));
            return FakeReply.Json(op.ToStatus(), call.Route.SuccessStatus);
        }
    }

    private FakeReply RotationGet(string rotationId)
    {
        lock (_gate)
        {
            return FakeReply.Json(RotationStatusLocked(RotationLocked(rotationId)));
        }
    }

    private PkiTrustResponse TrustLocked() => new() { Anchors = _trust.Describe(), MasterSan = MirbadLLM.Agent.Contracts.Protocol.IssuedMasterSpiffeId };

    private FakeReply TrustGet()
    {
        lock (_gate)
        {
            return FakeReply.Json(TrustLocked());
        }
    }

    private FakeReply TrustSet(FakeCall call)
    {
        var request = call.Request<PkiTrustSetRequest>();
        lock (_gate)
        {
            if (TryDuplicateLocked(call, out var duplicate))
            {
                return duplicate;
            }

            TrustAnchorSet next;
            try
            {
                next = TrustAnchorSet.FromPem(request.Anchors);
            }
            catch (Exception ex) when (ex is PkiException or CryptographicException or ArgumentException)
            {
                throw Fail(ErrorCodes.InvalidRequest, "The anchors are not 1–2 CA certificates: " + ex.Message);
            }

            var caller = call.Http.Connection.ClientCertificate;
            if (caller is null || !PeerValidator.ForMasterClient(next).Validate(caller).IsValid)
            {
                next.Dispose();
                throw Fail(ErrorCodes.InvalidState, "The new anchors must keep one that validates the calling master.");
            }

            Retire(_trust);
            _trust = next;
            _masterValidator.Set(PeerValidator.ForMasterClient(next));
            return CompletedPki(call, TrustLocked());
        }
    }

    private sealed class FakeRotation(string id, ECDsa key, string csrPem, string pendingSpki)
    {
        public string Id { get; } = id;

        public ECDsa Key { get; } = key;

        public string CsrPem { get; } = csrPem;

        public string PendingSpki { get; } = pendingSpki;

        public PkiRotationStage Stage { get; set; }

        public DateTimeOffset? CertNotAfter { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }
}
