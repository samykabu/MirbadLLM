using Microsoft.Extensions.Logging;

namespace MirbadLLM.NodeRuntime.Runtimes.Process;

/// <summary>Source-generated log delegates of <see cref="ProcessRuntimeDriver"/>. Arguments may be logged; environment values never are.</summary>
internal static partial class ProcessRuntimeLog
{
    [LoggerMessage(EventId = 2101, Level = LogLevel.Information, Message = "Model {ModelId}: started {Executable} {Version} as PID {Pid} on port {Port} (operation {OperationId}).")]
    public static partial void Started(ILogger logger, string modelId, string executable, string version, int pid, int port, string operationId);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Information, Message = "Model {ModelId} is Ready (PID {Pid}, port {Port}).")]
    public static partial void Ready(ILogger logger, string modelId, int pid, int port);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Warning, Message = "Model {ModelId}: PID {Pid} exited with code {ExitCode}; restart {Restart} of {Budget} on port {Port}.")]
    public static partial void Restarting(ILogger logger, string modelId, int pid, int? exitCode, int restart, int budget, int port);

    [LoggerMessage(EventId = 2104, Level = LogLevel.Error, Message = "Model {ModelId} failed: {Reason}")]
    public static partial void Failed(ILogger logger, string modelId, string reason);

    [LoggerMessage(EventId = 2105, Level = LogLevel.Information, Message = "Model {ModelId}: adopted PID {Pid} (started {StartTime:o}) into job {JobName}.")]
    public static partial void Adopted(ILogger logger, string modelId, int pid, DateTimeOffset startTime, string jobName);

    [LoggerMessage(EventId = 2106, Level = LogLevel.Information, Message = "Model {ModelId}: recorded PID {Pid} is gone (exited, rebooted or reused); the instance is Stopped.")]
    public static partial void RecordGone(ILogger logger, string modelId, int pid);

    [LoggerMessage(EventId = 2107, Level = LogLevel.Warning, Message = "Orphan sweep: killed PID {Pid} ({ImagePath}); its arguments match {Kind} of model {ModelId}.")]
    public static partial void OrphanKilled(ILogger logger, int pid, string imagePath, string kind, string modelId);

    [LoggerMessage(EventId = 2108, Level = LogLevel.Information, Message = "Orphan sweep: PID {Pid} ({ImagePath}) is not this agent's ({Reason}); left alone.")]
    public static partial void Foreign(ILogger logger, int pid, string imagePath, string reason);

    [LoggerMessage(EventId = 2109, Level = LogLevel.Warning, Message = "Model {ModelId}: port {Preferred} is in use by another program; using {Port}.")]
    public static partial void PortSkipped(ILogger logger, string modelId, int preferred, int port);

    [LoggerMessage(EventId = 2110, Level = LogLevel.Warning, Message = "Model {ModelId}: /health is 200 on port {Port} but /v1/models answers {Result}; another process may hold the port.")]
    public static partial void ProbeMismatch(ILogger logger, string modelId, int port, ProcessProbeResult result);

    [LoggerMessage(EventId = 2111, Level = LogLevel.Warning, Message = "Orphan sweep of {Folder} failed.")]
    public static partial void SweepFailed(ILogger logger, Exception exception, string folder);

    [LoggerMessage(EventId = 2112, Level = LogLevel.Error, Message = "Model {ModelId}: supervision failed.")]
    public static partial void SupervisionFailed(ILogger logger, Exception exception, string modelId);

    [LoggerMessage(EventId = 2113, Level = LogLevel.Warning, Message = "Model {ModelId}: GPU memory could not be measured.")]
    public static partial void MeasureFailed(ILogger logger, Exception exception, string modelId);

    [LoggerMessage(EventId = 2114, Level = LogLevel.Warning, Message = "Model {ModelId}: recorded PID {Pid} is ours but could not be adopted; its record is dropped and the orphan sweep ends it.")]
    public static partial void AdoptFailed(ILogger logger, Exception exception, string modelId, int pid);

    [LoggerMessage(EventId = 2115, Level = LogLevel.Warning, Message = "Orphan sweep: PID {Pid} ({ImagePath}) matches model {ModelId} but could not be killed.")]
    public static partial void OrphanKillFailed(ILogger logger, Exception exception, int pid, string imagePath, string modelId);

    [LoggerMessage(EventId = 2116, Level = LogLevel.Warning,
        Message = "Model {ModelId}: its pending start is kept, because the orphan sweep could not list {Folder}; " +
                  "the next recovery looks for it again.")]
    public static partial void PendingKept(ILogger logger, string modelId, string folder);

    [LoggerMessage(EventId = 2117, Level = LogLevel.Warning,
        Message = "Model {ModelId} is Ready on port {Port}, but its record could not be written: {Reason}. The instance " +
                  "serves; an agent restart will not find it and the orphan sweep will end it.")]
    public static partial void ReadyNotRecorded(ILogger logger, string modelId, int port, string reason);
}
