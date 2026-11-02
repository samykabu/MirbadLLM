using Microsoft.Extensions.Logging;

namespace MirbadLLM.Agent.HostServices.Windows;

/// <summary>Source-generated log lines of the SCM host services (MN-228).</summary>
internal static partial class ScmHostServicesLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Service whitelist {Change} {Name}: {Before} -> {After}")]
    public static partial void WhitelistChanged(ILogger logger, string change, string name, string before, string after);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused to {Action} {Service} ({Name}): {Reason}")]
    public static partial void Refused(ILogger logger, string action, string service, string name, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The service whitelist lists {Service} ({Name}), which the deny list refuses ('{Pattern}'): it is not managed")]
    public static partial void WhitelistedButDenied(ILogger logger, string service, string name, string pattern);

    [LoggerMessage(Level = LogLevel.Information, Message = "Service {Service} ({Name}): {Action} done, now {State}")]
    public static partial void ActionDone(ILogger logger, string service, string name, string action, string state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Service {Service} ({Name}): {Action} failed: {Message}")]
    public static partial void ActionFailed(ILogger logger, string service, string name, string action, string message);
}
