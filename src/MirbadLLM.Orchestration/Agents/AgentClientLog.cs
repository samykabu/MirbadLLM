using Microsoft.Extensions.Logging;

namespace MirbadLLM.Orchestration.Agents;

/// <summary>Source-generated log messages for the master's agent clients.</summary>
internal static partial class AgentClientLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Agent handlers for node {NodeId}: generation {Generation} created ({Reason}).")]
    public static partial void GenerationCreated(ILogger logger, string nodeId, long generation, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Agent handlers for node {NodeId}: generation {Generation} disposed ({InFlight} request(s) still in flight).")]
    public static partial void GenerationDisposed(ILogger logger, string nodeId, long generation, int inFlight);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {NodeId} has no usable agent endpoint: {Reason}")]
    public static partial void NodeUnusable(ILogger logger, string nodeId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent client for node {NodeId} removed ({Reason}); its handlers drain.")]
    public static partial void NodeRemoved(ILogger logger, string nodeId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not observe agent operation {OperationId} on node {NodeId}: {Reason}")]
    public static partial void ObserveFailed(ILogger logger, string operationId, string nodeId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent operation {OperationId} on node {NodeId} closed as {Outcome}.")]
    public static partial void ReferenceClosed(ILogger logger, string operationId, string nodeId, AgentOperationOutcome outcome);
}
