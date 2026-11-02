using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Pki;

namespace MirbadLLM.Agent.Pki;

/// <summary>
/// The agent's <see cref="IAgentCredentials"/> with two-phase rotation (design §10.2): the committed identity comes from
/// <see cref="FileAgentCredentials"/> (<c>agent.crt</c>/<c>agent.key</c>/<c>ca.crt</c>/<c>node-uid</c>); while a rotation
/// is <see cref="PkiRotationStage.CertInstalled"/> the pending certificate is offered to <b>new</b> connections instead
/// (step 3), and established connections keep the certificate they negotiated. <see cref="Version"/> changes whenever
/// either does, so <see cref="AgentListenerTls"/> swaps its cached certificate context.
/// <para>Construction recovers the rotation files before the listener can use them (agent restart, design §4.4 item 5
/// "rotation pending → keep"): a committed rotation whose promotion was cut short is finished, an installed certificate
/// is presented again, and pending material that no longer matches its stage is dropped (the master then sees the stage
/// and repeats or abandons the step).</para>
/// <para>Certificates replaced here are not disposed: established TLS connections may still use them.</para>
/// </summary>
public sealed class RotatingAgentCredentials : IAgentCredentials
{
    private readonly FileAgentCredentials _files;
    private readonly TimeProvider _time;
    private readonly ILogger<RotatingAgentCredentials> _logger;
    private X509Certificate2? _pending;
    private long _localVersion;

    /// <summary>Wraps <paramref name="files"/>, opens the rotation store in <paramref name="options"/>' state directory and recovers it.</summary>
    public RotatingAgentCredentials(FileAgentCredentials files, AgentHostOptions options, TimeProvider time, ILogger<RotatingAgentCredentials> logger)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _files = files;
        _time = time;
        _logger = logger;
        Store = new AgentRotationStore(options.StateDirectory);
        Recover();
    }

    /// <summary>The committed credentials (state directory files).</summary>
    public FileAgentCredentials Files => _files;

    /// <summary>The rotation files.</summary>
    public AgentRotationStore Store { get; }

    /// <inheritdoc />
    public bool IsEnrolled => _files.IsEnrolled;

    /// <inheritdoc />
    public string NodeId => _files.NodeId;

    /// <inheritdoc />
    public string NodeUid => _files.NodeUid;

    /// <inheritdoc />
    public X509Certificate2 ServerCertificate => Volatile.Read(ref _pending) ?? _files.ServerCertificate;

    /// <inheritdoc />
    public IReadOnlyList<X509Certificate2> TrustAnchors => _files.TrustAnchors;

    /// <inheritdoc />
    public long Version => _files.Version + Interlocked.Read(ref _localVersion);

    /// <summary>SPKI pin of the committed certificate (what established connections use), or null when not enrolled.</summary>
    public string? CurrentSpkiSha256 => _files.IsEnrolled ? Spki.Sha256(_files.ServerCertificate) : null;

    /// <summary>
    /// Presents <paramref name="certificate"/> (with its key, TLS-ready) on new connections from now on, or the committed
    /// certificate again when null.
    /// </summary>
    internal void SetPending(X509Certificate2? certificate)
    {
        Volatile.Write(ref _pending, certificate);
        Interlocked.Increment(ref _localVersion);
    }

    /// <summary>
    /// Finishes a committed rotation (design §10.2 step 4, idempotent): promotes the pending key and certificate over
    /// <c>agent.key</c>/<c>agent.crt</c> (the old key is gone from here on), reloads the committed credentials and checks
    /// they carry the new key, stops presenting the pending copy, deletes the pending files and records the rotation as
    /// finalized. Throws <see cref="IOException"/> when a step fails; the rotation stays committed and the next call (commit
    /// retry or agent start) continues.
    /// </summary>
    internal void FinishCommit(AgentRotationEntry committed)
    {
        ArgumentNullException.ThrowIfNull(committed);
        if (Store.HasPendingKey && Store.HasPendingCertificate)
        {
            Store.PromotePending();
        }

        Store.FaultInjection?.Invoke("commit:promoted");
        _files.Reload();
        if (!_files.IsEnrolled || !Spki.Matches(_files.ServerCertificate, committed.PendingSpkiSha256))
        {
            throw new IOException("The promoted agent certificate did not load; the rotation stays committed and is finished at the next attempt.");
        }

        SetPending(null);
        Store.DeletePendingFiles();
        Store.FaultInjection?.Invoke("commit:pending-deleted");
        Store.Save(Store.Document.Finish(committed with { Finalized = true, UpdatedAt = _time.GetUtcNow() }));
        AgentPkiLog.RotationFinalized(_logger, committed.RotationId, committed.PendingSpkiSha256);
    }

    /// <summary>Loads the pending certificate + key as a TLS-usable certificate, checking it carries the expected key.</summary>
    internal X509Certificate2 LoadPending(string certificatePem, string expectedSpki)
    {
        string keyPem = Store.ReadPendingKey() ?? throw new IOException("The pending key is missing.");
        X509Certificate2 certificate = TlsCredentials.FromPem(certificatePem, keyPem);
        if (!Spki.Matches(certificate, expectedSpki))
        {
            certificate.Dispose();
            throw new PkiException(PkiErrorKind.KeyMismatch, "The pending certificate is not for the pending key.");
        }

        return certificate;
    }

    private void Recover()
    {
        AgentRotationEntry? active = Store.Document.Active;
        try
        {
            switch (active?.Stage)
            {
                case null:
                    // Pending material without an open rotation is left over from an abort or a start cut short.
                    Store.DeletePendingFiles();
                    break;
                case PkiRotationStage.Pending:
                    if (!Store.HasPendingKey)
                    {
                        Abandon(active, "the pending key is missing");
                    }

                    break;
                case PkiRotationStage.CertInstalled:
                    RecoverInstalled(active);
                    break;
                case PkiRotationStage.Committed:
                    AgentPkiLog.RotationRecovering(_logger, active.RotationId, active.Stage);
                    FinishCommit(active);
                    break;
                default:
                    Store.Save(Store.Document.Finish(active));
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or PkiException)
        {
            // The agent still starts with whatever committed credentials load; the master sees the stage and retries.
            AgentPkiLog.RecoveryFailed(_logger, ex, active?.RotationId ?? "-");
        }
    }

    private void RecoverInstalled(AgentRotationEntry active)
    {
        AgentPkiLog.RotationRecovering(_logger, active.RotationId, active.Stage);
        if (!Store.HasPendingKey)
        {
            Abandon(active, "the pending key is missing");
            return;
        }

        string? certificatePem = Store.ReadPendingCertificate();
        try
        {
            if (certificatePem is null)
            {
                throw new IOException("The pending certificate is missing.");
            }

            SetPending(LoadPending(certificatePem, active.PendingSpkiSha256));
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or PkiException)
        {
            // The key is fine: go back to Pending so the master installs the certificate again (same CSR).
            AgentPkiLog.InstalledCertificateDropped(_logger, ex, active.RotationId);
            Store.DeletePendingCertificate();
            Store.Save(Store.Document with { Active = active with { Stage = PkiRotationStage.Pending, CertNotAfter = null, UpdatedAt = _time.GetUtcNow() } });
        }
    }

    private void Abandon(AgentRotationEntry active, string reason)
    {
        AgentPkiLog.RotationAbandoned(_logger, active.RotationId, reason);
        Store.DeletePendingFiles();
        Store.Save(Store.Document.Finish(active with { Stage = PkiRotationStage.Aborted, UpdatedAt = _time.GetUtcNow() }));
    }
}
