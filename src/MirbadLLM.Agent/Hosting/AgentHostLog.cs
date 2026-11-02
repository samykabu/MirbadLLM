using Microsoft.Extensions.Logging;
using MirbadLLM.Pki;

namespace MirbadLLM.Agent.Hosting;

/// <summary>Source-generated log messages of the agent host (listener, credentials, request checks).</summary>
internal static partial class AgentHostLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a master client certificate: {Failure} ({Detail}); subject {Subject}, SPKI {Spki}.")]
    public static partial void MasterCertificateRefused(ILogger logger, PeerValidationFailure failure, string detail, string subject, string spki);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A TLS connection arrived but the agent is not enrolled; refusing it.")]
    public static partial void HandshakeWithoutCredentials(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "mTLS listener uses credentials version {Version}: certificate SPKI {Spki}, {AnchorCount} trust anchor(s).")]
    public static partial void ListenerCredentials(ILogger logger, long version, string spki, int anchorCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded agent credentials for node {NodeId}: SPKI {Spki}, valid until {NotAfter:O}, {AnchorCount} trust anchor(s).")]
    public static partial void CredentialsLoaded(ILogger logger, string nodeId, string spki, DateTime notAfter, int anchorCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No agent certificate, key, CA or node-uid in {Directory}; the agent is not enrolled.")]
    public static partial void CredentialsMissing(ILogger logger, string directory);

    [LoggerMessage(Level = LogLevel.Error, Message = "The agent credentials in {Directory} could not be loaded; the agent is not enrolled.")]
    public static partial void CredentialsInvalid(ILogger logger, Exception exception, string directory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused {Method} {Path}: {Code} ({Message}).")]
    public static partial void RequestRefused(ILogger logger, string method, string path, string code, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled error in {Method} {Path}.")]
    public static partial void UnhandledError(ILogger logger, Exception exception, string method, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Capability contributor {Contributor} failed; its part is left out of the report.")]
    public static partial void ContributorFailed(ILogger logger, Exception exception, string contributor);
}
