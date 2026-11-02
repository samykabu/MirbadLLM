using MirbadLLM.Core;

namespace MirbadLLM.Web.Enrollment;

/// <summary>Source-generated log messages of enrollment. Tokens, keys and request bodies are never logged.</summary>
internal static partial class EnrollmentLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Enrollment of node {NodeId} from {Client} refused: {Reason}.")]
    public static partial void Refused(ILogger logger, string nodeId, string client, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Enrollment of node {NodeId} from {Client} refused: join token {Outcome}.")]
    public static partial void TokenRefused(ILogger logger, string nodeId, string client, JoinTokenConsumeOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Malformed enroll request from {Client}: {Reason}.")]
    public static partial void Malformed(ILogger logger, string client, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Enroll request from {Client} rate-limited.")]
    public static partial void RateLimited(ILogger logger, string client);

    [LoggerMessage(Level = LogLevel.Error, Message = "Enrollment of node {NodeId} from {Client} failed after its join token was consumed; the token is burnt.")]
    public static partial void FailedAfterConsume(ILogger logger, Exception exception, string nodeId, string client);

    [LoggerMessage(Level = LogLevel.Information, Message = "Node {NodeId} enrolled from {Client}: uid {NodeUid}, SPKI {Spki}, certificate valid until {NotAfter:O}, {Approval}.")]
    public static partial void Enrolled(ILogger logger, string nodeId, string client, string nodeUid, string spki, DateTimeOffset notAfter, string approval);

    [LoggerMessage(Level = LogLevel.Information, Message = "Enroll listener certificate issued: SPKI {Spki}, valid until {NotAfter:O}.")]
    public static partial void ListenerCertificateIssued(ILogger logger, string spki, DateTimeOffset notAfter);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bootstrap bundle for node {NodeId} written to {Directory} (token valid until {ExpiresAt:O}).")]
    public static partial void BundleWritten(ILogger logger, string nodeId, string directory, DateTimeOffset expiresAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bootstrap node {NodeId} is enrolled; removed the leftover bootstrap bundle from {Directory}.")]
    public static partial void BundleRemoved(ILogger logger, string nodeId, string directory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bootstrap node {NodeId} is revoked; no bootstrap bundle is written.")]
    public static partial void BootstrapNodeRevoked(ILogger logger, string nodeId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Checking the bootstrap bundle in {Directory} failed.")]
    public static partial void BundleCheckFailed(ILogger logger, Exception exception, string directory);
}
