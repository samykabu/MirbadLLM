using Microsoft.Extensions.Logging;

namespace MirbadLLM.NodeRuntime.Runtimes.Process.Windows;

/// <summary>Source-generated log delegates of <see cref="WindowsProcessHost"/>.</summary>
internal static partial class WindowsProcessHostLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Agent process job membership: in a job = {InJob} (per-instance jobs are created without kill-on-close and without breakaway).")]
    public static partial void AgentJobMembership(ILogger logger, bool inJob);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the agent process's job membership.")]
    public static partial void AgentJobMembershipUnknown(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Started {ImagePath} as pid {Pid} in job {JobName}.")]
    public static partial void Started(ILogger logger, string imagePath, int pid, string jobName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Adopted pid {Pid} ({ImagePath}) into a new job {JobName}.")]
    public static partial void Adopted(ILogger logger, int pid, string imagePath, string jobName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Not adopting pid {Pid}: {Reason}.")]
    public static partial void NotAdopted(ILogger logger, int pid, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Not adopting pid {Pid}: no such process or no access (error {Error}).")]
    public static partial void NotOpened(ILogger logger, int pid, int error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job name {JobName} is already in use; created an unnamed job instead (the name is a label only).")]
    public static partial void JobNameInUse(ILogger logger, string jobName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Killed candidate orphan pid {Pid} ({ImagePath}).")]
    public static partial void KilledCandidate(ILogger logger, int pid, string imagePath);
}
