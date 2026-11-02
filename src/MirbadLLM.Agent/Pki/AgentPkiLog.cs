using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Pki;

/// <summary>Source-generated log messages of the agent's PKI package. Only ids, stages and SPKI pins: never key material.</summary>
internal static partial class AgentPkiLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Certificate rotation {RotationId}: {Stage} (pending SPKI {PendingSpki}).")]
    public static partial void RotationStage(ILogger logger, string rotationId, PkiRotationStage stage, string pendingSpki);

    [LoggerMessage(Level = LogLevel.Information, Message = "Certificate rotation {RotationId} finalized: agent.key/agent.crt now hold SPKI {Spki}; the old key is deleted.")]
    public static partial void RotationFinalized(ILogger logger, string rotationId, string spki);

    [LoggerMessage(Level = LogLevel.Information, Message = "Certificate rotation {RotationId}: recovering stage {Stage} at agent start.")]
    public static partial void RotationRecovering(ILogger logger, string rotationId, PkiRotationStage stage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Certificate rotation {RotationId} abandoned at agent start: {Reason}.")]
    public static partial void RotationAbandoned(ILogger logger, string rotationId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Certificate rotation {RotationId}: the installed certificate could not be loaded; back to Pending (the master installs it again).")]
    public static partial void InstalledCertificateDropped(ILogger logger, Exception exception, string rotationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Certificate rotation {RotationId}: recovery at agent start failed; the agent keeps its committed credentials.")]
    public static partial void RecoveryFailed(ILogger logger, Exception exception, string rotationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Certificate rotation {RotationId} is committed but its promotion failed; it is finished at the next commit retry or agent start.")]
    public static partial void PromotionFailed(ILogger logger, Exception exception, string rotationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Master trust anchors replaced: {Anchors}.")]
    public static partial void TrustReplaced(ILogger logger, IReadOnlyList<string> anchors);

    [LoggerMessage(Level = LogLevel.Error, Message = "PKI operation {OperationId} ({Kind}) could not be completed durably.")]
    public static partial void StateUnavailable(ILogger logger, Exception exception, string operationId, string kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "PKI operation {OperationId} ({Kind}) left {State} by a restart; recovered as {Outcome}.")]
    public static partial void OperationRecovered(ILogger logger, string operationId, string kind, OperationState state, OperationState outcome);
}
