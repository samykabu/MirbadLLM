using Microsoft.Extensions.Logging;

namespace MirbadLLM.NodeRuntime.Runtimes.Process.Mac;

/// <summary>Source-generated log delegates of <see cref="MacProcessHost"/>.</summary>
internal static partial class MacProcessHostLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Started {ImagePath} as pid {Pid} ({JobName}), in its own session.")]
    public static partial void Started(ILogger logger, string imagePath, int pid, string jobName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Adopted pid {Pid} ({ImagePath}) as {JobName}.")]
    public static partial void Adopted(ILogger logger, int pid, string imagePath, string jobName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Not adopting pid {Pid}: {Reason}.")]
    public static partial void NotAdopted(ILogger logger, int pid, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Killed candidate orphan pid {Pid} ({ImagePath}).")]
    public static partial void KilledCandidate(ILogger logger, int pid, string imagePath);
}
