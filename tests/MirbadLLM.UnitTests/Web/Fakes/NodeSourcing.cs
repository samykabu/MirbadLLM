using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.UnitTests.Multinode;

namespace MirbadLLM.UnitTests.Web.Fakes;

/// <summary>
/// The master's model sourcing (<see cref="RemoteModelFolders"/>, <see cref="RemoteVllmCatalogService"/>) for UI and API
/// hosts without a real agent: the bootstrap node's agent is <see cref="LocalFoldersAgentClient"/> over a local models
/// root. Replaces the pre-P1 local fallbacks the hosts registered before MN-047 (D60).
/// </summary>
internal static class NodeSourcing
{
    /// <summary>
    /// Registers the remote folder and catalog services, reading folders from <paramref name="modelsRoot"/>; the catalog's
    /// default image comes from <paramref name="settings"/> (default settings when null). The model registry is the host's
    /// (an empty one when it has none).
    /// </summary>
    public static IServiceCollection AddNodeSourcing(this IServiceCollection services, string modelsRoot, IOptions<MirbadLLMOptions>? settings = null)
    {
        // The runner is only used by folder deletion, which these hosts do not exercise; its reference store is never written.
        var options = Options.Create(new MirbadLLMOptions { DataPath = Path.Combine(modelsRoot, ".operations-unused") });
        var nodes = new FakeNodeRegistry();
        var clients = new LocalFoldersAgentClients(new LocalFoldersAgentClient(modelsRoot));
        services.AddSingleton(sp => new RemoteModelFolders(
            clients,
            new AgentOperationRunner(clients, new JsonAgentOperationReferenceStore(options, TimeProvider.System), nodes, TimeProvider.System),
            sp.GetService<IModelRegistry>() ?? new Orchestration.Fakes.FakeModelRegistry()));
        services.AddSingleton(new RemoteVllmCatalogService(nodes, settings ?? Options.Create(new MirbadLLMOptions())));
        return services;
    }
}

/// <summary>Every node id gets the same <see cref="LocalFoldersAgentClient"/>.</summary>
internal sealed class LocalFoldersAgentClients(IAgentClient client) : IAgentClientFactory
{
    public IAgentClient GetClient(string nodeId) => client;

    public Task<IAgentClientLease> CreateFreshClientAsync(string nodeId, CancellationToken ct = default) => throw new NotSupportedException();

    public void RebuildHandlers(string nodeId) => throw new NotSupportedException();

    public void SetNegotiatedProtocol(string nodeId, int version) => throw new NotSupportedException();
}

/// <summary>
/// An agent that answers the folder reads from a local models root with <see cref="ModelFolderInspector"/>, the inspector
/// the real agent uses (its <c>ModelFolderService</c>); every other route is not used by the hosts here.
/// </summary>
internal sealed class LocalFoldersAgentClient(string modelsRoot) : IAgentClient
{
    private readonly ModelFolderInspector _inspector = new(Options.Create(new ModelStorageOptions { ModelsLocalPath = modelsRoot }));

    public string NodeId => "dgx";

    public IAgentDataClient Data => throw new NotSupportedException();

    public Task<IReadOnlyList<ModelFolderEntry>> ListModelFoldersAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(modelsRoot))
        {
            return Task.FromResult<IReadOnlyList<ModelFolderEntry>>([]);
        }

        var entries = new List<ModelFolderEntry>();
        foreach (string dir in Directory.EnumerateDirectories(modelsRoot).Order(StringComparer.OrdinalIgnoreCase))
        {
            string folder = Path.GetFileName(dir);
            if (folder.StartsWith('.'))
            {
                continue; // hidden, as on the agent
            }

            bool hasConfig = File.Exists(Path.Combine(dir, "config.json"));
            ModelFolderInspection? inspection = null;
            try
            {
                inspection = hasConfig ? _inspector.Inspect(folder) : null;
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                // An unreadable config.json: listed without a summary, as the agent does.
            }

            entries.Add(new ModelFolderEntry
            {
                Folder = folder,
                HasConfig = hasConfig,
                WeightsGiB = inspection?.WeightsGiB,
                Architecture = inspection?.Architecture,
                DetectedFormat = inspection?.DetectedFormat,
            });
        }

        return Task.FromResult<IReadOnlyList<ModelFolderEntry>>(entries);
    }

    public Task<ModelFolderInspection?> InspectModelFolderAsync(string folder, CancellationToken ct = default) =>
        Task.FromResult(_inspector.Inspect(folder));

    public Task<IAgentClientLease> CreateFreshClientAsync(string nodeId, CancellationToken ct = default) => throw new NotSupportedException();

    public void RebuildHandlers(string nodeId) => throw new NotSupportedException();

    public void SetNegotiatedProtocol(string nodeId, int version) => throw new NotSupportedException();

    public Task<InfoResponse> GetInfoAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<NodeCapabilities> GetCapabilitiesAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<StatusSnapshot> GetStatusAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public IAsyncEnumerable<AgentEvent> SubscribeEventsAsync(EventsQuery query, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<GpuIdleResponse> SampleGpuIdleAsync(GpuIdleQuery query, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<RuntimeStatus>> ListRuntimesAsync(RuntimesQuery query, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<RuntimeStatus?> GetRuntimeAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> LaunchAsync(string modelId, LaunchRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> StopAsync(string modelId, StopRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<LaunchConfig?> GetLaunchConfigAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

    public IAsyncEnumerable<string> StreamLogsAsync(string modelId, LogsQuery query, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<ImageInfo>> ListImagesAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> PullImageAsync(ImagePullRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> DeleteModelFolderAsync(string folder, FolderDeleteRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<NodeDownloadStatus> StartDownloadAsync(DownloadStartRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<NodeDownloadStatus>> ListDownloadsAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<NodeDownloadStatus?> GetDownloadAsync(string folder, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> CancelDownloadAsync(string folder, DownloadCancelRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus?> GetOperationAsync(string operationId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<OperationStatus>> ListOperationsAsync(OperationsQuery query, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> CancelOperationAsync(string operationId, OperationCancelRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<HostServicesResponse> ListHostServicesAsync(HostServicesQuery query, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> RunHostServiceAsync(string name, HostServiceAction action, HostServiceActionRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> SetHostServiceAutostartAsync(string name, HostServiceAutostartRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> AddToWhitelistAsync(HostWhitelistAddRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> UpdateWhitelistAsync(string name, HostWhitelistUpdateRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> RemoveFromWhitelistAsync(string name, HostWhitelistRemoveRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<GpuProcess>?> GetGpuProcessesAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<HostContainerInfo?> GetContainerAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> RunContainerAsync(string name, HostServiceAction action, HostContainerActionRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> SetContainerAutostartAsync(string name, HostContainerAutostartRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<BackupMountInfo> GetBackupMountAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> SetBackupMountAsync(BackupMountSetRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> RemoveBackupMountAsync(BackupMountRemoveRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<BackupMountTestResult> TestBackupMountAsync(BackupMountTestRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> StartRotationAsync(PkiRotationStartRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> InstallRotationCertificateAsync(PkiRotationCertRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> CommitRotationAsync(PkiRotationCommitRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> AbortRotationAsync(PkiRotationAbortRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<PkiRotationStatus?> GetRotationAsync(string rotationId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<PkiTrustResponse> GetTrustAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> SetTrustAsync(PkiTrustSetRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<TelemetryPage> GetTelemetryAsync(TelemetryQuery query, CancellationToken ct = default) => throw new NotSupportedException();
}
