using Microsoft.Extensions.Logging;

namespace MirbadLLM.Agent.HostServices;

/// <summary>
/// Source-generated log lines of the host-service package. Never carries a request body (the backup-share password
/// passes through once and is never logged, design §10.4).
/// </summary>
internal static partial class HostServicesLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Host service operation {OperationId}: {Kind} {Target} -> {State}")]
    public static partial void Completed(ILogger logger, string operationId, string kind, string target, MirbadLLM.Core.OperationState state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Host service operation {OperationId}: {Kind} {Target} failed ({Code}): {Message}")]
    public static partial void Failed(ILogger logger, string operationId, string kind, string target, string code, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Host service operation {OperationId}: {Kind} {Target} outcome unknown ({Code}): {Message}")]
    public static partial void Unknown(ILogger logger, string operationId, string kind, string target, string code, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Host service operation {OperationId}: the operation log could not record it ({Step})")]
    public static partial void StateUnavailable(ILogger logger, string operationId, string step, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Host service operation {OperationId}: recovering {Kind} {Target} at stage {Stage}")]
    public static partial void Recovering(ILogger logger, string operationId, string kind, string target, string stage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Host service probe failed: {Message}")]
    public static partial void ProbeFailed(ILogger logger, string message);
}
