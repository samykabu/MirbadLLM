using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Pki;
using MirbadLLM.Pki;

namespace MirbadLLM.Web.Enrollment;

/// <summary>What <see cref="EnrollmentService.EnrollAsync"/> decided.</summary>
public enum EnrollmentStatus
{
    /// <summary>Certificate issued; <see cref="EnrollmentResult.Response"/> is set.</summary>
    Enrolled,

    /// <summary>The request is malformed (400 <c>invalid_request</c>). Decided before the token is looked at.</summary>
    Invalid,

    /// <summary>Refused (403 <c>enrollment_refused</c>): one answer for every token, node and CSR problem, so there is no oracle.</summary>
    Refused,
}

/// <summary>Outcome of one enroll request. <see cref="Reason"/> is for the log only and never sent to the client.</summary>
public sealed record EnrollmentResult(EnrollmentStatus Status, EnrollResponse? Response, string? Reason)
{
    internal static EnrollmentResult Invalid(string reason) => new(EnrollmentStatus.Invalid, null, reason);

    internal static EnrollmentResult Refused(string reason) => new(EnrollmentStatus.Refused, null, reason);
}

/// <summary>
/// The master side of <c>POST /enroll</c> (design §3.2), in this order:
/// <list type="number">
/// <item>shape checks (node id, token length, addresses) and the CSR's proof of possession — failures here consume nothing;</item>
/// <item>a rekey-purpose token (D78) is refused here without being consumed: it is valid only on <c>/enroll/rekey</c>
/// (<see cref="RekeyService"/>), and the refusal is the same generic answer as for an unknown token;</item>
/// <item>atomic token consumption under the registry lock (<see cref="INodeRegistry.TryConsumeJoinTokenAsync"/>, persisted
/// before it returns): every outcome other than <see cref="JoinTokenConsumeOutcome.Consumed"/> is one generic refusal;</item>
/// <item>the certificate, built by the master from the CSR's public key only (<see cref="CertificateAuthority.IssueAgentCertificate"/>:
/// subject, SAN <c>spiffe://mirbadllm/node/{id}</c>, EKU, lifetime chosen here; requested extensions ignored);</item>
/// <item>the node record: a fresh uid unique across the registry, the new pin as <i>pending</i> and state
/// PendingApproval — except for the bootstrap node enrolling with a bootstrap token, which is pre-approved with the pin
/// current.</item>
/// </list>
/// If anything fails after the consumption the token stays burnt (design §3.2: a token never issues two certificates).
/// Every outcome is written to the audit log (<see cref="IAuditLog"/>, action <see cref="AuditActions.NodeEnroll"/>, actor
/// <c>api:{ip}</c>; never the token).
/// <para>The certificate is issued by, and the response carries, the <b>current</b> issuing CA
/// (<see cref="MasterCertificates.Authority"/>, read once per request), so an enrollment after a CA rotation's step 3 is
/// pinned to the new CA, never to the one loaded at boot (MN-126).</para>
/// </summary>
public sealed class EnrollmentService : IDisposable
{
    /// <summary>Longest token accepted (a token is 43 base64url characters).</summary>
    public const int MaxTokenLength = 256;

    private readonly INodeRegistry _registry;
    private readonly CertificateAuthority _ca;
    private readonly MasterCertificates? _master;
    private readonly CaRotationService? _caRotation;
    private readonly EnrollmentOptions _options;
    private readonly string _bootstrapNodeId;
    private readonly TimeProvider _time;
    private readonly ILogger<EnrollmentService> _logger;
    private readonly IAuditLog _audit;

    // Serializes the read-check-write of node records between concurrent enrollments (tokens are already single-use).
    private readonly SemaphoreSlim _recordLock = new(1, 1);

    public EnrollmentService(
        INodeRegistry registry,
        CertificateAuthority ca,
        IOptions<EnrollmentOptions> options,
        IOptions<RegistryMigrationOptions> migration,
        TimeProvider time,
        ILogger<EnrollmentService> logger,
        IAuditLog? audit = null,
        MasterCertificates? masterCertificates = null,
        CaRotationService? caRotation = null)
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
        _caRotation = caRotation;
        _options = options.Value;
        _bootstrapNodeId = migration.Value.EffectiveBootstrapNodeId;
        _time = time;
        _logger = logger;
        _audit = audit ?? NullAuditLog.Instance;
    }

    public void Dispose() => _recordLock.Dispose();

    /// <summary>The CA that issues now: the master certificates' authority (the new CA after a CA rotation's step 3), else the boot CA.</summary>
    public CertificateAuthority IssuingAuthority => _master?.Authority ?? _ca;

    /// <summary>Handles one enroll request from <paramref name="client"/> (source IP, recorded for the approval dialog).</summary>
    public async Task<EnrollmentResult> EnrollAsync(EnrollRequest? request, IPAddress? client, CancellationToken ct = default)
    {
        EnrollmentResult result = await DecideAsync(request, client, ct).ConfigureAwait(false);
        await EnrollmentAudit.WriteAsync(_audit, _time, client, request?.NodeId, result, rekey: false, target: null).ConfigureAwait(false);
        return result;
    }

    private async Task<EnrollmentResult> DecideAsync(EnrollRequest? request, IPAddress? client, CancellationToken ct)
    {
        string clientText = client?.ToString() ?? "unknown";
        if (request is null)
        {
            return Invalid(clientText, "no body");
        }

        if (!Protocol.IsValidNodeId(request.NodeId))
        {
            return Invalid(clientText, "invalid node id");
        }

        if (string.IsNullOrEmpty(request.Token) || request.Token.Length > MaxTokenLength)
        {
            return Invalid(clientText, "invalid token field");
        }

        if (request.Addresses is { Count: > PkiDefaults.MaxSanAddresses })
        {
            return Invalid(clientText, "too many addresses");
        }

        if (!TryNormalizeAddresses(request.Addresses ?? [], out List<string> addresses))
        {
            return Invalid(clientText, "an address is not an IP, DNS name or https URL");
        }

        // Proof of possession before the token is touched: a broken CSR must not burn a valid token.
        try
        {
            _ = CertificateSigningRequests.VerifyAndExtractPublicKey(request.CsrPem);
        }
        catch (PkiException ex)
        {
            return Refuse(request.NodeId, clientText, $"CSR {ex.Kind}");
        }

        // A rekey token is valid only on /enroll/rekey (D78): refused before consumption, so it stays usable there. A
        // token's purpose never changes, so this lookup cannot race the consumption below.
        if (await JoinTokenLookup.FindPurposeAsync(_registry, request.Token, ct).ConfigureAwait(false) == JoinTokenPurpose.Rekey)
        {
            return Refuse(request.NodeId, clientText, "rekey token (valid only on /enroll/rekey; not consumed)");
        }

        var consumption = await _registry.TryConsumeJoinTokenAsync(request.Token, request.NodeId, ct).ConfigureAwait(false);
        if (consumption.Outcome != JoinTokenConsumeOutcome.Consumed || consumption.Record is null)
        {
            EnrollmentLog.TokenRefused(_logger, request.NodeId, clientText, consumption.Outcome);
            return EnrollmentResult.Refused($"token {consumption.Outcome}");
        }

        // Defence in depth: the consumed record must not be a rekey token either (it is burnt now).
        if (consumption.Record.Purpose == JoinTokenPurpose.Rekey)
        {
            return Refuse(request.NodeId, clientText, "rekey token (valid only on /enroll/rekey; consumed)");
        }

        // From here on the token is burnt whatever happens.
        try
        {
            bool preApproved = consumption.Record.Purpose == JoinTokenPurpose.Bootstrap
                && string.Equals(request.NodeId, _bootstrapNodeId, StringComparison.Ordinal);
            // One authority for the certificate and the CA returned with it, even if a CA rotation switches it meanwhile.
            CertificateAuthority ca = IssuingAuthority;
            using var issued = ca.IssueAgentCertificate(request.CsrPem, request.NodeId, addresses: null);
            string uid = await RecordAsync(request, addresses, client, issued, preApproved, ct).ConfigureAwait(false);
            _caRotation?.NodeEnrolled(request.NodeId, ca.SpkiSha256); // under an open rotation's new CA: no previous client (MN-124)

            EnrollmentLog.Enrolled(_logger, request.NodeId, clientText, uid, issued.SpkiSha256, issued.NotAfter,
                preApproved ? "pre-approved (bootstrap)" : "pending approval");
            return new EnrollmentResult(EnrollmentStatus.Enrolled, new EnrollResponse
            {
                CaCertPem = ca.CertificatePem,
                AgentCertPem = issued.CertificatePem,
                NodeUid = uid,
                MasterSpiffeId = Protocol.IssuedMasterSpiffeId,
                PendingApproval = !preApproved,
            }, null);
        }
        catch (Exception ex) when (ex is PkiException or InvalidOperationException or ArgumentException or IOException)
        {
            // E.g. the node was revoked between consumption and the write (the registry refuses to un-revoke).
            EnrollmentLog.FailedAfterConsume(_logger, ex, request.NodeId, clientText);
            return EnrollmentResult.Refused("failed after token consumption");
        }
    }

    private async Task<string> RecordAsync(
        EnrollRequest request,
        List<string> addresses,
        IPAddress? client,
        IssuedCertificate issued,
        bool preApproved,
        CancellationToken ct)
    {
        await _recordLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var nodes = await _registry.ListAsync(ct).ConfigureAwait(false);
            var existing = nodes.FirstOrDefault(n => n.Id == request.NodeId);
            if (existing is { Revoked: true })
            {
                throw new InvalidOperationException($"Node '{request.NodeId}' is revoked.");
            }

            // A bootstrap token pre-approves, so it only enrolls a bootstrap node the master has no enrollment for (the
            // same rule as BootstrapBundleWriter). An older bundle's token that is still unexpired cannot take over an
            // enrolled node's identity (W3 review MN-094).
            if (preApproved && existing is not null && (existing.CertSpkiSha256 is not null || existing.PendingCertSpkiSha256 is not null))
            {
                throw new InvalidOperationException($"Node '{request.NodeId}' is already enrolled; a bootstrap token cannot re-enroll it.");
            }

            // A new uid on every enrollment (design §3.1: "same name, different machine"), unique across the registry.
            string uid;
            do
            {
                uid = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            }
            while (nodes.Any(n => string.Equals(n.Uid, uid, StringComparison.OrdinalIgnoreCase)));

            var now = _time.GetUtcNow();
            string address = existing?.Address ?? DefaultAddress(addresses, client, preApproved);
            var record = (existing ?? new NodeRecord
            {
                Id = request.NodeId,
                Uid = uid,
                Address = address,
                CreatedAt = now,
                MemoryReserveGiB = NodeRecord.DefaultMemoryReserveGiB(request.Capabilities?.Memory.TotalGiB),
            }) with
            {
                Uid = uid,
                Address = address,
                AltAddresses = existing is null ? [.. addresses.Select(ToAgentUrl).Where(a => a != address).Distinct(StringComparer.Ordinal)] : existing.AltAddresses,
                Approved = preApproved,
                ApprovedAt = preApproved ? now : null,
                CertSpkiSha256 = preApproved ? issued.SpkiSha256 : null,
                PendingCertSpkiSha256 = preApproved ? null : issued.SpkiSha256,
                CertNotAfter = issued.NotAfter,
                EnrolledAt = now,
                EnrolledFrom = client?.ToString(),
                LastCapabilities = request.Capabilities ?? existing?.LastCapabilities,
                Rotation = null,
            };

            await _registry.UpsertAsync(record, ct).ConfigureAwait(false);
            return uid;
        }
        finally
        {
            _recordLock.Release();
        }
    }

    private string DefaultAddress(List<string> addresses, IPAddress? client, bool preApproved)
    {
        if (preApproved && !string.IsNullOrWhiteSpace(_options.BootstrapAgentAddress))
        {
            return _options.BootstrapAgentAddress.Trim();
        }

        if (addresses.Count > 0)
        {
            return ToAgentUrl(addresses[0]);
        }

        return ToAgentUrl(client is null ? "127.0.0.1" : (client.IsIPv4MappedToIPv6 ? client.MapToIPv4() : client).ToString());
    }

    /// <summary>An address entry as an agent base URL: https URLs as given, hosts on the default agent port.</summary>
    private static string ToAgentUrl(string address)
    {
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            return uri.GetLeftPart(UriPartial.Authority);
        }

        string host = IPAddress.TryParse(address, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{ip}]"
            : address;
        return $"https://{host}:{Protocol.DefaultAgentPort}";
    }

    /// <summary>Validates and normalizes the agent's address list (IP, DNS name or https origin); shared with <see cref="RekeyService"/>.</summary>
    internal static bool TryNormalizeAddresses(IReadOnlyList<string> input, out List<string> addresses)
    {
        addresses = [];
        foreach (string? raw in input)
        {
            string value = raw?.Trim() ?? "";
            if (CertificateSans.TryParseIpLiteral(value, out var ip))
            {
                addresses.Add(ip.ToString());
            }
            else if (CertificateSans.IsValidDnsName(value))
            {
                addresses.Add(value.ToLowerInvariant());
            }
            else if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.UserInfo)
                && (CertificateSans.TryParseIpLiteral(uri.Host.Trim('[', ']'), out _) || CertificateSans.IsValidDnsName(uri.Host)))
            {
                addresses.Add(uri.GetLeftPart(UriPartial.Authority));
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private EnrollmentResult Invalid(string client, string reason)
    {
        EnrollmentLog.Malformed(_logger, client, reason);
        return EnrollmentResult.Invalid(reason);
    }

    private EnrollmentResult Refuse(string nodeId, string client, string reason)
    {
        EnrollmentLog.Refused(_logger, nodeId, client, reason);
        return EnrollmentResult.Refused(reason);
    }
}
