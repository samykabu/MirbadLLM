using Microsoft.Extensions.Logging;

namespace MirbadLLM.HostAgent;

/// <summary>
/// Every action and refusal is logged as one line; under systemd (<c>AddSystemdConsole</c>) these land in the
/// journal: <c>journalctl -u mirbadllm-hostd</c>.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "action service={Name} unit={Unit} action={Action} result=ok")]
    public static partial void ActionDone(ILogger logger, string name, string unit, string action);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "action service={Name} unit={Unit} action={Action} result=failed reason={Reason}")]
    public static partial void ActionFailed(ILogger logger, string name, string unit, string action, string reason);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "action service={Name} unit={Unit} action={Action} result=refused reason=protected-needs-confirmation")]
    public static partial void ActionRefused(ILogger logger, string name, string unit, string action);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "request rejected: unknown service {Name}")]
    public static partial void UnknownService(ILogger logger, string name);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "status service={Name} unit={Unit} failed reason={Reason}")]
    public static partial void StatusFailed(ILogger logger, string name, string unit, string reason);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "mirbadllm-hostd listening on {Socket} with {Count} whitelisted units and {DenyCount} deny patterns")]
    public static partial void Started(ILogger logger, string socket, int count, int denyCount);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "request refused: service={Name} action={Action} result=refused reason=denied pattern={Pattern}")]
    public static partial void DeniedRequest(ILogger logger, string name, string action, string pattern);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "whitelist {Change} service={Name} before={Before} after={After}")]
    public static partial void WhitelistChanged(ILogger logger, string change, string name, string before, string after);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "whitelist add refused: unit={Unit} reason=denied pattern={Pattern}")]
    public static partial void WhitelistRefusedDenied(ILogger logger, string unit, string pattern);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "whitelist entry dropped on load: service={Name} unit={Unit} reason=denied pattern={Pattern}")]
    public static partial void WhitelistDroppedDenied(ILogger logger, string name, string unit, string pattern);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "whitelist {Path} loaded with {Count} units ({Merged} merged from hostd.json)")]
    public static partial void WhitelistLoaded(ILogger logger, string path, int count, int merged);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "whitelist change refused: service={Name} change={Change} reason={Reason}")]
    public static partial void WhitelistRefused(ILogger logger, string name, string change, string reason);

    [LoggerMessage(EventId = 13, Level = LogLevel.Warning, Message = "discovery scope={Scope} failed reason={Reason}")]
    public static partial void DiscoveryFailed(ILogger logger, string scope, string reason);

    // Backup mount. Never the password: only the type, the source (//server/share or server:/export) and a summary.

    [LoggerMessage(EventId = 14, Level = LogLevel.Information, Message = "backup-mount test type={Type} source={Source} result={Result} {Reason}")]
    public static partial void BackupMountTested(ILogger logger, string type, string source, string result, string reason);

    [LoggerMessage(EventId = 15, Level = LogLevel.Information, Message = "backup-mount applied type={Type} source={Source} options=\"{Summary}\" target={Target}")]
    public static partial void BackupMountApplied(ILogger logger, string type, string source, string summary, string target);

    [LoggerMessage(EventId = 16, Level = LogLevel.Warning, Message = "backup-mount refused type={Type} source={Source} reason={Reason} detail={Detail}")]
    public static partial void BackupMountRefused(ILogger logger, string type, string source, string reason, string detail);

    [LoggerMessage(EventId = 17, Level = LogLevel.Information, Message = "backup-mount removed target={Target}: backups go to the local disk")]
    public static partial void BackupMountRemoved(ILogger logger, string target);
}
