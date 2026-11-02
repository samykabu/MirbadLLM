using System.Collections.Concurrent;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Multinode;

namespace MirbadLLM.Orchestration.Recovery;

/// <summary>
/// The master's <see cref="IRestoreNodeResources"/> (design §9.4 configuration import; composed by MN-130): each node's
/// checkpoint folders and downloader through its agent (<see cref="RemoteCheckpointFolders"/>,
/// <see cref="RemoteHuggingFaceDownloader"/>), one instance per node. The bootstrap node keeps the instances the master
/// already registered for it, so a single-DGX restore reads and downloads exactly as before.
/// </summary>
public sealed class AgentRestoreNodeResources : IRestoreNodeResources
{
    private readonly IAgentClientFactory _clients;
    private readonly AgentOperationRunner _runner;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, ICheckpointFolders> _folders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IHuggingFaceDownloader> _downloaders = new(StringComparer.Ordinal);

    /// <param name="bootstrapNodeId">The bootstrap node, served by <paramref name="bootstrapFolders"/> and <paramref name="bootstrapDownloader"/>.</param>
    /// <param name="bootstrapFolders">The bootstrap node's registered checkpoint folders.</param>
    /// <param name="bootstrapDownloader">The bootstrap node's registered downloader.</param>
    /// <param name="clients">Agent clients.</param>
    /// <param name="runner">Operation lifecycle of the downloads.</param>
    /// <param name="timeProvider">Clock of the folder caches.</param>
    public AgentRestoreNodeResources(
        string bootstrapNodeId,
        ICheckpointFolders bootstrapFolders,
        IHuggingFaceDownloader bootstrapDownloader,
        IAgentClientFactory clients,
        AgentOperationRunner runner,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrEmpty(bootstrapNodeId);
        ArgumentNullException.ThrowIfNull(bootstrapFolders);
        ArgumentNullException.ThrowIfNull(bootstrapDownloader);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _clients = clients;
        _runner = runner;
        _time = timeProvider;
        _folders[bootstrapNodeId] = bootstrapFolders;
        _downloaders[bootstrapNodeId] = bootstrapDownloader;
    }

    /// <inheritdoc/>
    public ICheckpointFolders FoldersFor(string nodeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        return _folders.GetOrAdd(nodeId, id => new RemoteCheckpointFolders(id, _clients, _time));
    }

    /// <inheritdoc/>
    public IHuggingFaceDownloader DownloaderFor(string nodeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        return _downloaders.GetOrAdd(nodeId, id => new RemoteHuggingFaceDownloader(id, _clients, _runner));
    }
}
