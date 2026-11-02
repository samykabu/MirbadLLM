using Microsoft.Extensions.Logging;

namespace MirbadLLM.Orchestration.Fencing;

/// <summary>Source-generated log messages for master fencing.</summary>
internal static partial class FencingLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Master identity {InstanceId}: boot {BootId} at epoch {Epoch} (data path {DataPath}).")]
    public static partial void Booted(ILogger logger, string instanceId, string bootId, long epoch, string dataPath);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created a new master identity {InstanceId} in {DataPath}.")]
    public static partial void Created(ILogger logger, string instanceId, string dataPath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Takeover requested ({Source}). It runs only after the operator confirms the previous master is stopped.")]
    public static partial void TakeoverRequested(ILogger logger, string source);

    [LoggerMessage(Level = LogLevel.Information, Message = "The data directory lock {LockPath} is held by another master; waiting up to {Wait} for it to be released.")]
    public static partial void LockBusy(ILogger logger, string lockPath, TimeSpan wait);

    [LoggerMessage(Level = LogLevel.Information, Message = "Master epoch raised {Previous} -> {Epoch}.")]
    public static partial void EpochRaised(ILogger logger, long previous, long epoch);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Takeover {TakeoverId} persisted at epoch {Epoch} ({Answered} agents answered, {Unreachable} unreachable).")]
    public static partial void TakeoverCompleted(ILogger logger, string takeoverId, long epoch, int answered, int unreachable);

    [LoggerMessage(Level = LogLevel.Error, Message = "Takeover was persisted but the flag file {FlagPath} could not be deleted; delete it by hand.")]
    public static partial void FlagDeleteFailed(ILogger logger, Exception exception, string flagPath);
}
