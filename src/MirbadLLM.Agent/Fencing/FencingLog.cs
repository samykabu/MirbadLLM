using Microsoft.Extensions.Logging;

namespace MirbadLLM.Agent.Fencing;

/// <summary>Source-generated log messages of the agent's fencing state.</summary>
internal static partial class FencingLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Accepted master {InstanceId} epoch {Epoch} boot {BootId} (previous epoch {Previous}).")]
    public static partial void Accepted(ILogger logger, string instanceId, long epoch, string bootId, long? previous);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused master {InstanceId} epoch {Epoch} boot {BootId}: {Decision} (accepted {AcceptedInstanceId} epoch {AcceptedEpoch} boot {AcceptedBootId}).")]
    public static partial void Refused(ILogger logger, string instanceId, long epoch, string bootId, string decision, string acceptedInstanceId, long acceptedEpoch, string acceptedBootId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded fencing state: master {InstanceId} epoch {Epoch} boot {BootId}.")]
    public static partial void Loaded(ILogger logger, string instanceId, long epoch, string bootId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not persist the accepted master triple in {Path}; the request is refused.")]
    public static partial void PersistFailed(ILogger logger, Exception exception, string path);
}
