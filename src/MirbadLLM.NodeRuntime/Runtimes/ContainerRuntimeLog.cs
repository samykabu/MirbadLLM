using Microsoft.Extensions.Logging;

namespace MirbadLLM.NodeRuntime.Runtimes;

/// <summary>Source-generated log delegates of <see cref="ContainerRuntimeDriver"/>; texts match the pre-P1 <c>DockerModelRuntime</c>'s.</summary>
internal static partial class ContainerRuntimeLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Model {ModelId} is crash-looping; stopped its container. See the Failed status for the cause.")]
    public static partial void CrashLoopStopped(ILogger logger, string modelId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not stop crash-looping model {ModelId}.")]
    public static partial void CrashLoopStopFailed(ILogger logger, Exception exception, string modelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read logs for container {ContainerId}.")]
    public static partial void ReadLogsFailed(ILogger logger, Exception exception, string containerId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Model {ModelId}: created container {ContainerId} on port {HostPort} (operation {OperationId}).")]
    public static partial void Created(ILogger logger, string modelId, string containerId, int hostPort, string operationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pulling image {Image} for model {ModelId}.")]
    public static partial void Pulling(ILogger logger, string image, string modelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the command line of container {ContainerId}.")]
    public static partial void InspectFailed(ILogger logger, Exception exception, string containerId);
}
