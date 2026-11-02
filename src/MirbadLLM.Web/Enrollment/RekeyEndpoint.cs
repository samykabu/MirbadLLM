using System.Net;
using System.Security.Cryptography;
using System.Text;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Orchestration.Pki;
using MirbadLLM.Pki;

namespace MirbadLLM.Web.Enrollment;

/// <summary>
/// The master side of <c>POST /enroll/rekey</c> (<see cref="AgentRoutes.Rekey"/>; design §9.4 step 5, §10.2 item 6,
/// D73/D78; MN-114): re-pairs an <b>enrolled</b> node whose certificate expired or whose state was lost, keeping its node
/// id, node uid, approval, addresses, settings and placed models. Only the key, the certificate and the pins change.
/// <list type="number">
/// <item>Shape checks (node id, token and uid fields, addresses) and the CSR's proof of possession: nothing is consumed.</item>
/// <item>The token must be of purpose <see cref="JoinTokenPurpose.Rekey"/> (join and bootstrap tokens are refused without
/// being consumed: D48's pre-approval rule stays with <c>/enroll</c>), and the node must be one a rekey token may be issued
/// for (<see cref="JsonNodeRegistry.RekeyRefusal"/>: it exists, is not revoked and holds a current or pending pin) and have
/// the uid the agent presents (constant-time). Failures here consume
/// nothing either, so an operator's rekey token survives a wrong <c>--node-uid</c>.</item>
/// <item>Atomic token consumption (<see cref="INodeRegistry.TryConsumeJoinTokenAsync"/>); the consumed record is checked
/// again for purpose rekey.</item>
/// <item>The certificate, built by the master from the CSR's public key only (SPIFFE SAN only, as at enrollment, D48).</item>
/// <item>One atomic registry update (<see cref="INodeRegistry.UpdateAsync"/>) that re-checks revoked/uid/enrolled under the
/// registry lock and replaces the pins: an approved node gets the new key as its current pin and no pending pin (the old
/// current and pending pins are gone in the same write); a node still pending approval gets the new key as its pending pin
/// and stays pending. The node's open certificate rotation (MN-036) is aborted by removing the record: the rotation
/// service persists every step only while the record still holds its rotation id, so an in-flight step can never promote
/// the old identity's pending key over the new pin.</item>
/// </list>
/// Every refusal is the one generic 403 <c>enrollment_refused</c> answer of <c>/enroll</c> (no oracle for token, node or
/// uid); the reason goes to the log and the audit line only. Rate limit: <c>/enroll</c>'s budget, shared (one 5/min/IP).
/// <para>Issued by the <b>current</b> CA (<see cref="MasterCertificates.Authority"/>, read once per request): a node re-paired
/// after a CA rotation's step 3 (an early close leaves the unrotated nodes for re-pairing) gets a certificate under, and is
/// pinned to, the new CA (MN-126).</para>
/// </summary>
public sealed class RekeyService
{
    private const int MaxNodeUidLength = 64;

    private readonly INodeRegistry _registry;
    private readonly CertificateAuthority _ca;
    private readonly MasterCertificates? _master;
    private readonly CaRotationService? _caRotation;
    private readonly TimeProvider _time;
    private readonly ILogger<RekeyService> _logger;
    private readonly IAuditLog _audit;

    /// <summary>Creates the service. <paramref name="audit"/> defaults to <see cref="NullAuditLog"/> until MN-130 registers MN-116's store.</summary>
    /// <param name="masterCertificates">The master certificates whose <see cref="MasterCertificates.Authority"/> issues; null = <paramref name="ca"/>.</param>
    public RekeyService(INodeRegistry registry, CertificateAuthority ca, TimeProvider time, ILogger<RekeyService> logger, IAuditLog? audit = null, MasterCertificates? masterCertificates = null,
        CaRotationService? caRotation = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(ca);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _ca = ca;
        _master = masterCertificates;
        _caRotation = caRotation;
        _time = time;
        _logger = logger;
        _audit = audit ?? NullAuditLog.Instance;
    }

    /// <summary>The CA that issues now (see the class remarks).</summary>
    public CertificateAuthority IssuingAuthority => _master?.Authority ?? _ca;

    /// <summary>Handles one rekey request from <paramref name="client"/>.</summary>
    public async Task<EnrollmentResult> RekeyAsync(RekeyRequest? request, IPAddress? client, CancellationToken ct = default)
    {
        (EnrollmentResult result, string? abortedRotation) = await DecideAsync(request, client, ct).ConfigureAwait(false);
        await EnrollmentAudit.WriteAsync(_audit, _time, client, request?.NodeId, result, rekey: true, abortedRotation).ConfigureAwait(false);
        return result;
    }

    private async Task<(EnrollmentResult Result, string? AbortedRotation)> DecideAsync(RekeyRequest? request, IPAddress? client, CancellationToken ct)
    {
        string clientText = client?.ToString() ?? "unknown";
        if (request is null)
        {
            return (Invalid(clientText, "no body"), null);
        }

        if (!Protocol.IsValidNodeId(request.NodeId))
        {
            return (Invalid(clientText, "invalid node id"), null);
        }

        if (string.IsNullOrEmpty(request.Token) || request.Token.Length > EnrollmentService.MaxTokenLength)
        {
            return (Invalid(clientText, "invalid token field"), null);
        }

        if (!IsWellFormedUid(request.NodeUid))
        {
            return (Invalid(clientText, "invalid node uid field"), null);
        }

        if (request.Addresses is { Count: > PkiDefaults.MaxSanAddresses }
            || !EnrollmentService.TryNormalizeAddresses(request.Addresses ?? [], out _))
        {
            return (Invalid(clientText, "invalid addresses"), null);
        }

        // Proof of possession before the token is touched: a broken CSR must not burn a valid token.
        try
        {
            _ = CertificateSigningRequests.VerifyAndExtractPublicKey(request.CsrPem);
        }
        catch (PkiException ex)
        {
            return (Refuse(request.NodeId, clientText, $"CSR {ex.Kind}"), null);
        }

        // Only a rekey token, and only for a node it can re-pair; checked before consumption so a mistake burns nothing.
        JoinTokenPurpose? purpose = await JoinTokenLookup.FindPurposeAsync(_registry, request.Token, ct).ConfigureAwait(false);
        if (purpose != JoinTokenPurpose.Rekey)
        {
            return (Refuse(request.NodeId, clientText, purpose is null ? "unknown token (not consumed)" : $"{purpose} token (valid only on /enroll; not consumed)"), null);
        }

        NodeRecord? existing = await _registry.GetAsync(request.NodeId, ct).ConfigureAwait(false);
        if (WhyNotRekeyable(existing, request.NodeUid) is { } problem)
        {
            return (Refuse(request.NodeId, clientText, problem + " (not consumed)"), null);
        }

        var consumption = await _registry.TryConsumeJoinTokenAsync(request.Token, request.NodeId, ct).ConfigureAwait(false);
        if (consumption.Outcome != JoinTokenConsumeOutcome.Consumed || consumption.Record is null)
        {
            return (Refuse(request.NodeId, clientText, $"token {consumption.Outcome}"), null);
        }

        if (consumption.Record.Purpose != JoinTokenPurpose.Rekey)
        {
            return (Refuse(request.NodeId, clientText, $"{consumption.Record.Purpose} token (consumed)"), null);
        }

        // From here on the token is burnt whatever happens.
        try
        {
            CertificateAuthority ca = IssuingAuthority;
            using IssuedCertificate issued = ca.IssueAgentCertificate(request.CsrPem, request.NodeId, addresses: null);
            DateTimeOffset now = _time.GetUtcNow();
            string? abortedRotation = null;
            string? previousPin = null;
            NodeRecord stored = await _registry.UpdateAsync(request.NodeId, node =>
            {
                // Re-checked under the registry lock: the node may have been revoked, deleted and re-created, or
                // re-enrolled with a new uid since the check above.
                if (WhyNotRekeyable(node, request.NodeUid) is { } changed)
                {
                    throw new InvalidOperationException($"Node '{request.NodeId}' can no longer be re-paired: {changed}.");
                }

                abortedRotation = node.Rotation is { } open && open.Stage is not (CertificateRotationStage.Committed or CertificateRotationStage.Aborted)
                    ? open.RotationId
                    : null;
                previousPin = node.CertSpkiSha256 ?? node.PendingCertSpkiSha256;
                return node with
                {
                    CertSpkiSha256 = node.Approved ? issued.SpkiSha256 : null,
                    PendingCertSpkiSha256 = node.Approved ? null : issued.SpkiSha256,
                    CertNotAfter = issued.NotAfter,
                    EnrolledAt = now,
                    EnrolledFrom = client?.ToString(),
                    LastCapabilities = request.Capabilities ?? node.LastCapabilities,
                    Rotation = null,
                };
            }, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"Node '{request.NodeId}' was deleted.");
            _caRotation?.NodeEnrolled(request.NodeId, ca.SpkiSha256); // under an open rotation's new CA: no previous client (MN-124)

            RekeyLog.Rekeyed(_logger, request.NodeId, clientText, stored.Uid, previousPin ?? "-", issued.SpkiSha256, issued.NotAfter,
                stored.Approved ? "approval kept" : "still pending approval", abortedRotation ?? "none");
            return (new EnrollmentResult(EnrollmentStatus.Enrolled, new EnrollResponse
            {
                CaCertPem = ca.CertificatePem,
                AgentCertPem = issued.CertificatePem,
                NodeUid = stored.Uid,
                MasterSpiffeId = Protocol.IssuedMasterSpiffeId,
                PendingApproval = !stored.Approved,
            }, null), abortedRotation);
        }
        catch (Exception ex) when (ex is PkiException or InvalidOperationException or ArgumentException or IOException)
        {
            RekeyLog.FailedAfterConsume(_logger, ex, request.NodeId, clientText);
            return (EnrollmentResult.Refused("failed after token consumption"), null);
        }
    }

    /// <summary>Why <paramref name="node"/> cannot be re-paired by an agent presenting <paramref name="presentedUid"/>; null when it can.</summary>
    /// <remarks>The node part is the rule that also gates issuing the token (<see cref="JsonNodeRegistry.RekeyRefusal"/>).</remarks>
    private static string? WhyNotRekeyable(NodeRecord? node, string presentedUid) =>
        JsonNodeRegistry.RekeyRefusal(node) ?? (UidEquals(node!.Uid, presentedUid) ? null : "node uid mismatch");

    private static bool UidEquals(string? stored, string presented)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return false;
        }

        byte[] a = Encoding.UTF8.GetBytes(stored.ToLowerInvariant());
        byte[] b = Encoding.UTF8.GetBytes(presented.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static bool IsWellFormedUid(string? uid) =>
        !string.IsNullOrEmpty(uid) && uid.Length <= MaxNodeUidLength && uid.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private EnrollmentResult Invalid(string client, string reason)
    {
        EnrollmentLog.Malformed(_logger, client, "rekey: " + reason);
        return EnrollmentResult.Invalid(reason);
    }

    private EnrollmentResult Refuse(string nodeId, string client, string reason)
    {
        RekeyLog.Refused(_logger, nodeId, client, reason);
        return EnrollmentResult.Refused(reason);
    }
}

/// <summary>Maps <c>POST /enroll/rekey</c> next to <c>POST /enroll</c> (called by <see cref="EnrollEndpoints.MapEnrollEndpoint"/>).</summary>
public static class RekeyEndpoint
{
    /// <summary>
    /// Maps <see cref="AgentRoutes.Rekey"/>, anonymous (the rekey token is the credential), with exactly <c>/enroll</c>'s
    /// handling: enroll-listener connections only (404 elsewhere), the shared per-IP rate limit before the body is read,
    /// the body limit, and the answers 200 <see cref="EnrollResponse"/>, 400 <c>invalid_request</c>, 403
    /// <c>enrollment_refused</c> (the same body as <c>/enroll</c>'s) and 429 <c>rate_limited</c>.
    /// </summary>
    internal static RouteHandlerBuilder MapRekeyEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapPost(AgentRoutes.Rekey.Path, (HttpContext http, RekeyService service, EnrollRateLimiter limiter, ILoggerFactory loggers, CancellationToken ct) =>
                EnrollEndpoints.HandleEnrollListenerAsync<RekeyRequest>(http, limiter, loggers, (request, client, token) => service.RekeyAsync(request, client, token), ct))
            .AllowAnonymous()
            .DisableAntiforgery();
    }
}

/// <summary>
/// Finds a join token's purpose without consuming it (D78: <c>/enroll</c> refuses a rekey token and <c>/enroll/rekey</c>
/// refuses the others, both before consumption, so a token sent to the wrong route stays usable on its own). Compares the
/// presented hash with every stored one in constant time, without stopping early. A record's purpose never changes, so the
/// answer cannot be invalidated by a concurrent consumption.
/// </summary>
internal static class JoinTokenLookup
{
    private const int HashBytes = 32;

    /// <summary>The purpose of the stored token matching <paramref name="token"/>, or null when there is none.</summary>
    public static async Task<JoinTokenPurpose?> FindPurposeAsync(INodeRegistry registry, string token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        byte[] presented = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        JoinTokenPurpose? found = null;
        foreach (JoinTokenRecord record in await registry.ListJoinTokensAsync(ct).ConfigureAwait(false))
        {
            if (TryDecode(record.Sha256, out byte[] stored) && CryptographicOperations.FixedTimeEquals(stored, presented) && found is null)
            {
                found = record.Purpose;
            }
        }

        return found;
    }

    private static bool TryDecode(string? base64Url, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrEmpty(base64Url) || base64Url.Length > 64)
        {
            return false;
        }

        string base64 = base64Url.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
        byte[] buffer = new byte[HashBytes + 3];
        if (!Convert.TryFromBase64String(base64, buffer, out int written) || written != HashBytes)
        {
            return false;
        }

        hash = buffer[..HashBytes];
        return true;
    }
}

/// <summary>The <c>node.enroll</c> audit line of one enroll or rekey request (design §10.5). Never holds the token or a key.</summary>
internal static class EnrollmentAudit
{
    public static Task WriteAsync(IAuditLog audit, TimeProvider time, IPAddress? client, string? nodeId, EnrollmentResult result, bool rekey, string? target)
    {
        string ip = client is null ? "unknown" : (client.IsIPv4MappedToIPv6 ? client.MapToIPv4() : client).ToString();
        string route = rekey ? "rekey" : "enroll";
        string detail = result.Status switch
        {
            EnrollmentStatus.Enrolled when rekey => target is null
                ? "rekey: key and certificate replaced, uid kept"
                : $"rekey: key and certificate replaced, uid kept, rotation {target} aborted",
            EnrollmentStatus.Enrolled => result.Response?.PendingApproval == false ? "enroll: pre-approved" : "enroll: pending approval",
            _ => $"{route}: {result.Reason}",
        };

        AuditRecord record = new()
        {
            Time = time.GetUtcNow(),
            Actor = AuditActors.ApiCaller(ip),
            Action = AuditActions.NodeEnroll,
            // An untrusted value: recorded only when it is a well-formed node id.
            NodeId = Protocol.IsValidNodeId(nodeId) ? nodeId : null,
            Target = target,
            Result = result.Status == EnrollmentStatus.Enrolled ? AuditResults.Succeeded : AuditResults.Refused,
            ErrorCode = result.Status switch
            {
                EnrollmentStatus.Enrolled => null,
                EnrollmentStatus.Invalid => ErrorCodes.InvalidRequest,
                _ => ErrorCodes.EnrollmentRefused,
            },
            Detail = detail,
        };

        // The request is decided; recording it does not depend on the caller's cancellation.
        return audit.WriteAsync(record, CancellationToken.None);
    }
}

/// <summary>Source-generated log messages of <c>/enroll/rekey</c>. Tokens, keys and request bodies are never logged.</summary>
internal static partial class RekeyLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Rekey of node {NodeId} from {Client} refused: {Reason}.")]
    public static partial void Refused(ILogger logger, string nodeId, string client, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Rekey of node {NodeId} from {Client} failed after its rekey token was consumed; the token is burnt.")]
    public static partial void FailedAfterConsume(ILogger logger, Exception exception, string nodeId, string client);

    [LoggerMessage(Level = LogLevel.Information, Message = "Node {NodeId} re-paired from {Client}: uid {NodeUid} kept, pin {OldSpki} replaced by {Spki}, certificate valid until {NotAfter:O}, {Approval}; open rotation aborted: {Rotation}.")]
    public static partial void Rekeyed(ILogger logger, string nodeId, string client, string nodeUid, string oldSpki, string spki, DateTimeOffset notAfter, string approval, string rotation);
}
