using Microsoft.Extensions.Logging;
using MirbadLLM.Pki;

namespace MirbadLLM.Agent.Enrollment;

/// <summary>Source-generated log messages of agent enrollment. Tokens and keys are never logged.</summary>
internal static partial class EnrollmentLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Enrolling node {NodeId} at {Endpoint}, expecting CA sha256:{CaHash}.")]
    public static partial void Enrolling(ILogger logger, string nodeId, Uri endpoint, string caHash);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused the enroll listener's certificate: {Failure} ({Detail}); subject {Subject}, SPKI {Spki}. The token was not sent.")]
    public static partial void ServerRefused(ILogger logger, PeerValidationFailure failure, string detail, string subject, string spki);

    [LoggerMessage(Level = LogLevel.Information, Message = "Node {NodeId} enrolled: uid {NodeUid}, SPKI {Spki}, certificate valid until {NotAfter:O}; pending approval: {PendingApproval}.")]
    public static partial void Enrolled(ILogger logger, string nodeId, string nodeUid, string spki, DateTimeOffset notAfter, bool pendingApproval);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bootstrap bundle {Path} deleted after enrolling.")]
    public static partial void BundleDeleted(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the bootstrap bundle {Path} (its token is used and cannot enroll again).")]
    public static partial void BundleDeleteFailed(ILogger logger, Exception exception, string path);
}
