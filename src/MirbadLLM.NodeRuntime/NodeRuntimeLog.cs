using Microsoft.Extensions.Logging;

namespace MirbadLLM.NodeRuntime;

/// <summary>
/// Source-generated <see cref="LoggerMessage"/> delegates for node-side code (avoids CA1848/CA1873). Minimal internal
/// copy of the entries moved code used from Orchestration's <c>Log</c> class (MN-002); messages are unchanged.
/// </summary>
internal static partial class NodeRuntimeLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "nvidia-smi is unavailable; GPU telemetry will be omitted from samples.")]
    public static partial void GpuProbeUnavailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "nvidia-smi is unavailable; GPU telemetry will be omitted from samples.")]
    public static partial void GpuProbeUnavailableWithException(ILogger logger, Exception exception);
}
