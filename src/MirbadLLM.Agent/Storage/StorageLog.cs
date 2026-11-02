using Microsoft.Extensions.Logging;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Storage;

/// <summary>Log lines of the storage package. None of them takes a request body, so no token can reach a log.</summary>
internal static partial class StorageLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Download {OperationId}: started {RepoId} into {Folder}.")]
    public static partial void DownloadStarted(ILogger logger, string operationId, string repoId, string folder);

    [LoggerMessage(Level = LogLevel.Information, Message = "Download {OperationId}: resumed monitoring {Folder} after an agent restart.")]
    public static partial void DownloadResumed(ILogger logger, string operationId, string folder);

    [LoggerMessage(Level = LogLevel.Information, Message = "Download {OperationId}: cancelled {Folder}.")]
    public static partial void DownloadCancelled(ILogger logger, string operationId, string folder);

    [LoggerMessage(Level = LogLevel.Information, Message = "Download {OperationId}: {Folder} ended {State}.")]
    public static partial void DownloadFinished(ILogger logger, string operationId, string folder, DownloadState state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Polling the download helper for {Folder} failed; retrying on the next poll.")]
    public static partial void DownloadPollFailed(ILogger logger, Exception exception, string folder);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} finished download helper container(s) at agent start.")]
    public static partial void FinishedHelpersRemoved(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removing finished download helpers at agent start failed.")]
    public static partial void FinishedHelpersCleanupFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Image pull {OperationId}: pulling {Reference}.")]
    public static partial void ImagePullStarted(ILogger logger, string operationId, string reference);

    [LoggerMessage(Level = LogLevel.Information, Message = "Image pull {OperationId}: {Reference} pulled.")]
    public static partial void ImagePullFinished(ILogger logger, string operationId, string reference);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Image pull {OperationId} failed ({Code}).")]
    public static partial void ImagePullFailed(ILogger logger, string operationId, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Folder delete {OperationId}: removed {Folder}.")]
    public static partial void FolderDeleted(ILogger logger, string operationId, string folder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Folder delete {OperationId}: removing {Folder} failed.")]
    public static partial void FolderDeleteFailed(ILogger logger, Exception exception, string operationId, string folder);
}
