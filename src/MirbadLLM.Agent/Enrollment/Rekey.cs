using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Core;
using MirbadLLM.Pki;

namespace MirbadLLM.Agent.Enrollment;

/// <summary>
/// <c>mirbadllm-agent join --rekey</c> (design §9.4 step 5, §10.2 item 6; MN-114): what the operator copied from the
/// Nodes page's re-pair action. <see cref="ToString"/> never prints the token.
/// </summary>
public sealed record RekeyParameters
{
    /// <summary>Enroll listener base address, e.g. <c>https://mirbad.example.com:8443</c> (https only).</summary>
    public required Uri EnrollUrl { get; init; }

    /// <summary>Single-use join token of purpose rekey. Secret.</summary>
    public required string Token { get; init; }

    /// <summary>The CA's SPKI hash, <c>sha256:{base64}</c>: the only trust anchor, as for <c>join</c>.</summary>
    public required string CaHash { get; init; }

    /// <summary>The enrolled node's id.</summary>
    public required string NodeId { get; init; }

    /// <summary>
    /// The node uid (<c>--node-uid</c>), required when the state directory lost its <c>node-uid</c> file; when both exist
    /// they must agree. Null = take it from the state directory.
    /// </summary>
    public string? NodeUid { get; init; }

    /// <summary>The agent's state directory; its identity files are replaced, everything else in it is kept.</summary>
    public required string StateDirectory { get; init; }

    /// <summary>Addresses the master can reach this agent at.</summary>
    public IReadOnlyList<string> Addresses { get; init; } = [];

    /// <summary>The capability report sent with the request (optional).</summary>
    public NodeCapabilities? Capabilities { get; init; }

    /// <summary>Redacts the token.</summary>
    public override string ToString() =>
        $"RekeyParameters {{ NodeId = {NodeId}, NodeUid = {NodeUid ?? "(state)"}, EnrollUrl = {EnrollUrl}, CaHash = {CaHash}, Token = {Protocol.RedactedValue} }}";
}

/// <summary>
/// The agent side of <c>POST /enroll/rekey</c>: re-pairs an enrolled node whose certificate expired (it cannot do mTLS
/// any more) or whose state directory lost its identity, keeping its node id and uid. Everything outside the identity
/// files (operation log, accepted master triple, downloads, the runtimes and their containers) is left alone; the running
/// agent serves the new certificate after a restart.
/// <list type="number">
/// <item>Local checks before anything is sent: the uid comes from <c>--node-uid</c> or the state directory's <c>node-uid</c>
/// (both present = they must agree), and a certificate left in the state directory must name the same node id.</item>
/// <item>A fresh P-256 key; the request carries only its CSR, the token and the uid, over the same TLS connection as
/// <c>join</c> (<see cref="AgentEnrollment.SendAsync{TRequest}"/>: the listener must chain to the CA whose hash came with
/// the command, or the token is never sent).</item>
/// <item>The answer gets <c>join</c>'s checks (CA hash, certificate for this node and this key) and must carry the same
/// uid; then <see cref="EnrollmentStateFiles"/> replaces the identity: the old identity's rotation state first (D48, W3
/// review MN-094), then the key, CA, uid and certificate last.</item>
/// </list>
/// </summary>
public static class AgentRekey
{
    private const string RefusedMessage =
        "The master refused the re-pair (rekey token unknown, expired, already used, for another node, or not a rekey token; " +
        "or the node is unknown, revoked or has another uid). Ask for a new re-pair command.";

    /// <summary>Re-pairs the node. Throws <see cref="AgentEnrollmentException"/>; nothing is stored on failure.</summary>
    public static async Task<EnrollmentOutcome> RekeyAsync(this AgentEnrollment enrollment, RekeyParameters parameters, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        ArgumentNullException.ThrowIfNull(parameters);
        if (!Spki.TryParseJoinHash(parameters.CaHash, out string caPin))
        {
            throw new AgentEnrollmentException(EnrollmentFailure.InvalidArguments, "The CA hash is not a SHA-256 value (expected sha256:<base64>).");
        }

        AgentEnrollment.CheckCommon(parameters.EnrollUrl, parameters.Token, parameters.NodeId, parameters.StateDirectory);
        string nodeUid = ResolveUid(parameters);
        CheckStoredNodeId(parameters.StateDirectory, parameters.NodeId);

        RekeyLog.Rekeying(enrollment.Logger, parameters.NodeId, nodeUid, parameters.EnrollUrl, caPin);
        using ECDsa key = PkiDefaults.CreateKey();
        string keyPin = Spki.Sha256(key);
        RekeyRequest request = new()
        {
            Token = parameters.Token,
            NodeId = parameters.NodeId,
            NodeUid = nodeUid,
            CsrPem = CertificateSigningRequests.CreatePem(key, parameters.NodeId),
            Addresses = parameters.Addresses ?? [],
            Capabilities = parameters.Capabilities,
        };

        EnrollResponse response = await enrollment.SendAsync(parameters.EnrollUrl, AgentRoutes.Rekey.Path, RefusedMessage, caPin, knownCa: null, request, ct)
            .ConfigureAwait(false);
        (string caPem, string certificatePem, DateTimeOffset notAfter) = enrollment.CheckResponse(response, parameters.NodeId, caPin, keyPin);
        if (!string.Equals(response.NodeUid, nodeUid, StringComparison.OrdinalIgnoreCase))
        {
            throw AgentEnrollment.Invalid("it names another node uid (a re-pair keeps the uid)");
        }

        EnrollmentStateFiles.Write(parameters.StateDirectory, key.ExportPkcs8PrivateKeyPem(), caPem, nodeUid, certificatePem);
        RekeyLog.Rekeyed(enrollment.Logger, parameters.NodeId, nodeUid, keyPin, notAfter, response.PendingApproval);
        return new EnrollmentOutcome(parameters.NodeId, nodeUid, keyPin, notAfter, response.PendingApproval);
    }

    private static string ResolveUid(RekeyParameters parameters)
    {
        string? stored = ReadStoredUid(parameters.StateDirectory);
        string? given = string.IsNullOrWhiteSpace(parameters.NodeUid) ? null : parameters.NodeUid.Trim();
        if (given is not null && !AgentEnrollment.IsWellFormedUid(given))
        {
            throw new AgentEnrollmentException(EnrollmentFailure.InvalidArguments, "--node-uid is not a node uid (letters, digits and '-', at most 64).");
        }

        if (given is not null && stored is not null && !string.Equals(given, stored, StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentEnrollmentException(EnrollmentFailure.InvalidArguments,
                $"--node-uid {given} differs from the uid {stored} in '{parameters.StateDirectory}'; this state directory belongs to another enrollment.");
        }

        string? uid = given ?? stored;
        if (uid is null)
        {
            throw new AgentEnrollmentException(EnrollmentFailure.InvalidArguments,
                $"'{parameters.StateDirectory}' holds no node uid; pass --node-uid (shown on the master's Nodes page).");
        }

        if (!AgentEnrollment.IsWellFormedUid(uid))
        {
            throw new AgentEnrollmentException(EnrollmentFailure.InvalidArguments,
                $"The node uid in '{parameters.StateDirectory}' is malformed; pass --node-uid (shown on the master's Nodes page).");
        }

        return uid;
    }

    private static string? ReadStoredUid(string directory)
    {
        try
        {
            string? uid = PkiFiles.ReadIfExists(Path.Combine(directory, FileAgentCredentials.NodeUidFileName))?.Trim();
            return string.IsNullOrEmpty(uid) ? null : uid;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AgentEnrollmentException(EnrollmentFailure.InvalidArguments, $"The node uid in '{directory}' cannot be read: {ex.Message}", ex);
        }
    }

    /// <summary>A certificate left in the state directory (expired or not) must be this node's: rekey never re-pairs another node's state.</summary>
    private static void CheckStoredNodeId(string directory, string nodeId)
    {
        string? pem;
        try
        {
            pem = PkiFiles.ReadIfExists(Path.Combine(directory, FileAgentCredentials.CertificateFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // An unreadable certificate is lost state; the uid check still applies.
        }

        if (string.IsNullOrWhiteSpace(pem))
        {
            return;
        }

        string? stored = null;
        try
        {
            using X509Certificate2 certificate = X509Certificate2.CreateFromPem(pem);
            if (Protocol.TryParseNodeSpiffeId(CertificateSans.GetSingleUri(certificate), out string id))
            {
                stored = id;
            }
        }
        catch (Exception ex) when (ex is CryptographicException or PkiException or ArgumentException)
        {
            return; // A corrupt certificate is lost state.
        }

        if (stored is not null && !string.Equals(stored, nodeId, StringComparison.Ordinal))
        {
            throw new AgentEnrollmentException(EnrollmentFailure.InvalidArguments,
                $"'{directory}' holds the identity of node '{stored}', not '{nodeId}'; rekey re-pairs only the node it belongs to.");
        }
    }
}

/// <summary>Source-generated log messages of <c>join --rekey</c>. Tokens and keys are never logged.</summary>
internal static partial class RekeyLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Re-pairing node {NodeId} (uid {NodeUid}) at {Endpoint}, expecting CA sha256:{CaHash}.")]
    public static partial void Rekeying(ILogger logger, string nodeId, string nodeUid, Uri endpoint, string caHash);

    [LoggerMessage(Level = LogLevel.Information, Message = "Node {NodeId} re-paired: uid {NodeUid} kept, new SPKI {Spki}, certificate valid until {NotAfter:O}; pending approval: {PendingApproval}.")]
    public static partial void Rekeyed(ILogger logger, string nodeId, string nodeUid, string spki, DateTimeOffset notAfter, bool pendingApproval);
}
