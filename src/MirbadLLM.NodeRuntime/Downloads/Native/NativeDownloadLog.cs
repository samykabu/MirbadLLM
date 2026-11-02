using Microsoft.Extensions.Logging;

namespace MirbadLLM.NodeRuntime.Downloads.Native;

/// <summary>Log lines of the native downloader. None takes a request, header or URL query, so no token can reach a log.</summary>
internal static partial class NativeDownloadLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Native download {Folder}: {RepoId} pinned at {CommitSha}, {FileCount} file(s), {TotalBytes} bytes.")]
    public static partial void Pinned(ILogger logger, string folder, string repoId, string commitSha, int fileCount, long totalBytes);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Native download {Folder}: {File} verified ({Bytes} bytes, {Response}).")]
    public static partial void FileVerified(ILogger logger, string folder, string file, long bytes, RangeResponseKind response);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Native download {Folder}: {File} failed its integrity check; deleted, retrying once.")]
    public static partial void FileMismatchRetry(ILogger logger, string folder, string file);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Native download {Folder}: {File} attempt {Attempt} failed ({Reason}); resuming.")]
    public static partial void FileTransientRetry(ILogger logger, string folder, string file, int attempt, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Native download {Folder}: finalized.")]
    public static partial void Finalized(ILogger logger, string folder);
}
