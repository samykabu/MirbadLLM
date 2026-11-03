using Microsoft.Extensions.Logging;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration;

/// <summary>Source-generated <see cref="LoggerMessage"/> delegates for the orchestration workstream (avoids CA1848/CA1873).</summary>
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Model {ModelId} is already {State}; skipping auto-start.")]
    public static partial void ModelAlreadyRunning(ILogger logger, string modelId, ModelState state);

    [LoggerMessage(Level = LogLevel.Information, Message = "Auto-started model {ModelId}.")]
    public static partial void AutoStarted(ILogger logger, string modelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not auto-start model {ModelId}: {Reason}")]
    public static partial void AutoStartRefused(ILogger logger, string modelId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Model {ModelId} was stopped by the operator while its auto-start waited; leaving it stopped.")]
    public static partial void AutoStartStoppedByOperator(ILogger logger, string modelId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to auto-start model {ModelId}.")]
    public static partial void AutoStartFailed(ILogger logger, Exception exception, string modelId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to refresh model statuses.")]
    public static partial void RefreshFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Model {ModelId} state {Previous} -> {Current}")]
    public static partial void StateTransition(ILogger logger, string modelId, ModelState previous, ModelState current);

    /// <summary>
    /// The one line MN-239 item 13 asked for: an operator could not tell "old agent" from "unreachable" from "the file
    /// is not addressable by the route" when a tune's candidate memory went unchecked. <see cref="LogLevel.Debug"/>,
    /// because the degradation is designed (D130) and the tuning plan already says the memory was not checked.
    /// </summary>
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Tuning {ModelId}: the GGUF header was not read on node {NodeId} ({Reason}); the candidates' memory is not checked.")]
    public static partial void TuningGgufUnread(ILogger logger, string modelId, string nodeId, string reason);

    /// <summary>The first of item 13's three cases: the master cannot even address the file, so nothing was sent.</summary>
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Tuning {ModelId}: '{ModelFile}' is not a file directly inside a top-level folder, so node {NodeId} was " +
                  "never asked for its GGUF header; the candidates' memory is not checked.")]
    public static partial void TuningGgufNotAddressable(ILogger logger, string modelId, string nodeId, string? modelFile);

    /// <summary>The second: the node accepted the connection and then did not answer in time.</summary>
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Tuning {ModelId}: node {NodeId} did not answer the GGUF header read within {Seconds} s; the candidates' " +
                  "memory is not checked.")]
    public static partial void TuningGgufTimedOut(ILogger logger, string modelId, string nodeId, double seconds);
}
