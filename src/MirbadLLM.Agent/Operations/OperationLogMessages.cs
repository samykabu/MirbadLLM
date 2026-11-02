using Microsoft.Extensions.Logging;

namespace MirbadLLM.Agent.Operations;

/// <summary>Log lines of the operation log (high-performance logging).</summary>
internal static partial class OperationLogMessages
{
    [LoggerMessage(EventId = 1301, Level = LogLevel.Warning, Message = "operation log: ignored {Count} unparseable line(s) in {Path} (torn write after a crash or corruption)")]
    public static partial void TornLinesIgnored(ILogger logger, int count, string path);

    [LoggerMessage(EventId = 1302, Level = LogLevel.Warning, Message = "operation log: ignored {Count} line(s) for operations without an accepted record in {Path}")]
    public static partial void OrphanLinesIgnored(ILogger logger, int count, string path);

    [LoggerMessage(EventId = 1303, Level = LogLevel.Error, Message = "operation log: write failed, operations are refused (state_unavailable): {Reason}")]
    public static partial void WriteFailed(ILogger logger, string reason, Exception exception);

    [LoggerMessage(EventId = 1304, Level = LogLevel.Information, Message = "operation log: writable again")]
    public static partial void Recovered(ILogger logger);

    [LoggerMessage(EventId = 1305, Level = LogLevel.Information, Message = "operation log: compacted, kept {Kept} operation(s) ({Active} active), pruned {Pruned}")]
    public static partial void Compacted(ILogger logger, int kept, int active, int pruned);

    [LoggerMessage(EventId = 1306, Level = LogLevel.Warning, Message = "operation log: compaction failed")]
    public static partial void CompactionFailed(ILogger logger, Exception exception);
}
