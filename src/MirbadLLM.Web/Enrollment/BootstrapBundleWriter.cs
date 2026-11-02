using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Pki;
using MirbadLLM.Pki;

namespace MirbadLLM.Web.Enrollment;

/// <summary>What <see cref="BootstrapBundleWriter.EnsureAsync"/> did.</summary>
public enum BootstrapBundleState
{
    /// <summary><see cref="EnrollmentOptions.BootstrapEnabled"/> is false; nothing touched.</summary>
    Disabled,

    /// <summary>A new bundle (with a new bootstrap token) was written.</summary>
    Written,

    /// <summary>The bundle on disk is still valid (its token unused and far enough from expiry).</summary>
    Current,

    /// <summary>The bootstrap node is enrolled; no bundle is needed and a leftover one was removed.</summary>
    Enrolled,

    /// <summary>The bootstrap node is revoked; no bundle is written (a leftover one was removed).</summary>
    Revoked,
}

/// <summary>
/// Writes the DGX bootstrap bundle (design §3.2): while the bootstrap node (<c>MirbadLLM:Nodes:BootstrapNodeId</c>) is
/// not enrolled, <c>{BootstrapDirectory}/bootstrap.json</c> holds the CA <b>certificate</b>, its SPKI hash, the enroll
/// endpoint and a single-use token of purpose <see cref="JoinTokenPurpose.Bootstrap"/>. The CA key is never written
/// there. The file is written atomically with mode 0600 in a 0700 directory (<see cref="PkiFiles.WriteSecret"/>); the
/// agent deletes it after enrolling, and the writer removes a leftover one once the node is enrolled.
/// <para>The CA is the <b>current</b> issuing CA (<see cref="MasterCertificates.Authority"/>): after a CA rotation's step 3 an
/// unused bundle names a CA the enroll listener no longer chains to, so the next check (<see cref="BootstrapBundleService"/>)
/// sees it as stale, expires its token and writes a bundle for the new CA (MN-126). An enrolled bootstrap node (P1's DGX)
/// has no bundle and is rotated like any other node.</para>
/// </summary>
public sealed partial class BootstrapBundleWriter : IDisposable
{
    private const int MaxBundleBytes = 64 * 1024;

    private readonly INodeRegistry _registry;
    private readonly CertificateAuthority _ca;
    private readonly MasterCertificates? _master;
    private readonly EnrollmentOptions _options;
    private readonly string _nodeId;
    private readonly TimeProvider _time;
    private readonly ILogger<BootstrapBundleWriter> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BootstrapBundleWriter(
        INodeRegistry registry,
        CertificateAuthority ca,
        IOptions<EnrollmentOptions> options,
        IOptions<RegistryMigrationOptions> migration,
        TimeProvider time,
        ILogger<BootstrapBundleWriter> logger,
        MasterCertificates? masterCertificates = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(ca);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(migration);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _ca = ca;
        _master = masterCertificates;
        _options = options.Value;
        _nodeId = migration.Value.EffectiveBootstrapNodeId;
        _time = time;
        _logger = logger;
    }

    public void Dispose() => _lock.Dispose();

    /// <summary>The CA the bundle names: the current issuing CA (see the class remarks).</summary>
    public CertificateAuthority Authority => _master?.Authority ?? _ca;

    /// <summary>Full path of the bundle file.</summary>
    public string BundlePath => Path.Combine(_options.BootstrapDirectory, BootstrapBundle.FileName);

    /// <summary>Brings the bundle in line with the registry (see <see cref="BootstrapBundleState"/>).</summary>
    public async Task<BootstrapBundleState> EnsureAsync(CancellationToken ct = default)
    {
        if (!_options.BootstrapEnabled)
        {
            return BootstrapBundleState.Disabled;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var node = await _registry.GetAsync(_nodeId, ct).ConfigureAwait(false);
            if (node is { Revoked: true })
            {
                DeleteBundle();
                EnrollmentLog.BootstrapNodeRevoked(_logger, _nodeId);
                return BootstrapBundleState.Revoked;
            }

            if (node is not null && (node.CertSpkiSha256 is not null || node.PendingCertSpkiSha256 is not null))
            {
                if (DeleteBundle())
                {
                    EnrollmentLog.BundleRemoved(_logger, _nodeId, _options.BootstrapDirectory);
                }

                return BootstrapBundleState.Enrolled;
            }

            CertificateAuthority ca = Authority;
            if (await IsCurrentAsync(ca, ct).ConfigureAwait(false))
            {
                return BootstrapBundleState.Current;
            }

            // The previous bundle's token (stale, close to expiry or from another CA/endpoint) stops working now instead of
            // staying valid until it expires (W3 review MN-094, MN-102). The node has no enrollment at this point, so the
            // only unused tokens bound to it are bootstrap tokens (an operator join token for it would be expired too).
            int expired = await _registry.ExpireUnusedJoinTokensAsync(_nodeId, ct).ConfigureAwait(false);
            if (expired > 0)
            {
                LogPreviousTokensExpired(_logger, _nodeId, expired);
            }

            var lifetime = _options.BootstrapTokenLifetime;
            var issue = await _registry.CreateJoinTokenAsync(_nodeId, lifetime, JoinTokenPurpose.Bootstrap, ct).ConfigureAwait(false);
            var bundle = new BootstrapBundle
            {
                NodeId = _nodeId,
                Token = issue.Token,
                CaCertPem = ca.CertificatePem,
                CaSpkiSha256 = ca.SpkiSha256,
                EnrollEndpoint = _options.BootstrapEnrollEndpoint,
                CreatedAt = issue.Record.CreatedAt,
            };

            PkiFiles.EnsureDirectory(_options.BootstrapDirectory);
            PkiFiles.WriteSecret(BundlePath, JsonSerializer.Serialize(bundle, AgentJson.Options));
            EnrollmentLog.BundleWritten(_logger, _nodeId, _options.BootstrapDirectory, issue.Record.ExpiresAt);
            return BootstrapBundleState.Written;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>True when the bundle on disk is for this node, CA and endpoint, and its token is unused and not close to expiry.</summary>
    private async Task<bool> IsCurrentAsync(CertificateAuthority ca, CancellationToken ct)
    {
        BootstrapBundle? bundle;
        try
        {
            var info = new FileInfo(BundlePath);
            if (!info.Exists || info.Length > MaxBundleBytes)
            {
                return false;
            }

            bundle = JsonSerializer.Deserialize<BootstrapBundle>(PkiFiles.ReadIfExists(BundlePath) ?? "null", AgentJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (bundle is null
            || bundle.NodeId != _nodeId
            || !Spki.PinEquals(bundle.CaSpkiSha256, ca.SpkiSha256)
            || bundle.EnrollEndpoint != _options.BootstrapEnrollEndpoint
            || string.IsNullOrEmpty(bundle.Token))
        {
            return false;
        }

        string hash = TokenHash(bundle.Token);
        var tokens = await _registry.ListJoinTokensAsync(ct).ConfigureAwait(false);
        var record = tokens.FirstOrDefault(t => t.Sha256 == hash);
        return record is { UsedAt: null, Purpose: JoinTokenPurpose.Bootstrap }
            && record.NodeId == _nodeId
            && record.ExpiresAt - _time.GetUtcNow() > _options.BootstrapRefreshMargin;
    }

    private bool DeleteBundle()
    {
        if (!File.Exists(BundlePath))
        {
            return false;
        }

        File.Delete(BundlePath);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Bootstrap bundle for node {NodeId} is rewritten; expired {Count} unused join token(s) of the previous bundle.")]
    private static partial void LogPreviousTokensExpired(ILogger logger, string nodeId, int count);

    /// <summary>The stored form of a token (<c>JoinTokenRecord.Sha256</c>): base64url SHA-256 of its UTF-8 bytes.</summary>
    private static string TokenHash(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Runs <see cref="BootstrapBundleWriter.EnsureAsync"/> at start and every <see cref="EnrollmentOptions.BootstrapCheckInterval"/>.</summary>
public sealed class BootstrapBundleService(
    BootstrapBundleWriter writer,
    IOptions<EnrollmentOptions> options,
    TimeProvider time,
    ILogger<BootstrapBundleService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.BootstrapEnabled)
        {
            return;
        }

        var interval = options.Value.BootstrapCheckInterval > TimeSpan.Zero ? options.Value.BootstrapCheckInterval : TimeSpan.FromMinutes(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await writer.EnsureAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
            {
                EnrollmentLog.BundleCheckFailed(logger, ex, options.Value.BootstrapDirectory);
            }

            try
            {
                await Task.Delay(interval, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
