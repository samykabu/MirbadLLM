using MirbadLLM.Core;

namespace MirbadLLM.Agent.Contracts;

/// <summary>
/// Typed master-side client of one node's agent (design §4, §7.2), one method per route of <see cref="AgentRoutes"/>,
/// implemented by <c>MirbadLLM.Orchestration.Agents</c> (MN-021) and faked by <c>FakeAgent</c> (MN-022).
/// <para>Every call goes over HTTPS with mutual TLS and HTTP/2 (<c>Version20</c> + <c>RequestVersionExact</c> per
/// request; no downgrade). All calls except the proxy use the node's <b>control</b> handler; the proxy uses
/// <see cref="Data"/>. Requests carry <c>MirbadLLM-Protocol</c> (the negotiated version, except <c>/info</c>);
/// mutations also carry <c>MirbadLLM-Master</c> and <c>MirbadLLM-Request-Time</c>.</para>
/// <para>Errors: a non-2xx answer throws <see cref="AgentException"/> (status + <see cref="AgentError"/>); a transport
/// failure before any response throws <see cref="AgentUnreachableException"/>. Reads are retried per
/// <see cref="AgentCallPolicies"/>; a mutation is retried only on transport errors before a response and always with the
/// same request object (same id, envelope and body bytes); an HTTP error response is never retried. GETs whose 404 means
/// "absent" return null instead of throwing.</para>
/// <para>Instances are tied to a handler generation: get one from <see cref="IAgentClientFactory.GetClient"/> per unit
/// of work rather than caching it, so certificate or trust changes take effect.</para>
/// </summary>
public interface IAgentClient
{
    /// <summary>Node id this client talks to.</summary>
    string NodeId { get; }

    /// <summary>The data channel (proxy) of the same handler generation.</summary>
    IAgentDataClient Data { get; }

    // ---- Identity, health, telemetry ----

    /// <summary><see cref="AgentRoutes.Info"/>.</summary>
    Task<InfoResponse> GetInfoAsync(CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.Capabilities"/>.</summary>
    Task<NodeCapabilities> GetCapabilitiesAsync(CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.Status"/>.</summary>
    Task<StatusSnapshot> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// <see cref="AgentRoutes.Events"/>: yields events until the agent closes the stream (after a reset or overflow event),
    /// the connection fails (<see cref="AgentUnreachableException"/>) or <paramref name="ct"/> is cancelled. No reconnect
    /// logic here; <c>NodeMonitor</c> owns it.
    /// </summary>
    IAsyncEnumerable<AgentEvent> SubscribeEventsAsync(EventsQuery query, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.Telemetry"/>.</summary>
    Task<TelemetryPage> GetTelemetryAsync(TelemetryQuery query, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.GpuIdle"/>.</summary>
    Task<GpuIdleResponse> SampleGpuIdleAsync(GpuIdleQuery query, CancellationToken ct = default);

    // ---- Runtimes ----

    /// <summary><see cref="AgentRoutes.RuntimesList"/>.</summary>
    Task<IReadOnlyList<RuntimeStatus>> ListRuntimesAsync(RuntimesQuery query, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.RuntimeGet"/>; null when there is no instance (Stopped).</summary>
    Task<RuntimeStatus?> GetRuntimeAsync(string modelId, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.RuntimeLaunch"/>: the accepted (202) or stored operation.</summary>
    Task<OperationStatus> LaunchAsync(string modelId, LaunchRequest request, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.RuntimeStop"/>.</summary>
    Task<OperationStatus> StopAsync(string modelId, StopRequest request, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.RuntimeLaunchConfig"/>; null when there is no instance.</summary>
    Task<LaunchConfig?> GetLaunchConfigAsync(string modelId, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.RuntimeLogs"/>: one string per log line.</summary>
    IAsyncEnumerable<string> StreamLogsAsync(string modelId, LogsQuery query, CancellationToken ct = default);

    // ---- Images, folders, downloads ----

    /// <summary><see cref="AgentRoutes.ImagesList"/>.</summary>
    Task<IReadOnlyList<ImageInfo>> ListImagesAsync(CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.ImagePull"/>.</summary>
    Task<OperationStatus> PullImageAsync(ImagePullRequest request, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.ModelFoldersList"/>.</summary>
    Task<IReadOnlyList<ModelFolderEntry>> ListModelFoldersAsync(CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.ModelFolderInspect"/>; null when the folder has no readable config.</summary>
    Task<ModelFolderInspection?> InspectModelFolderAsync(string folder, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.ModelFolderDelete"/>.</summary>
    Task<OperationStatus> DeleteModelFolderAsync(string folder, FolderDeleteRequest request, CancellationToken ct = default);

    /// <summary>
    /// <see cref="AgentRoutes.GgufInspect"/> (P3, MN-202, D116): the GGUF header summary of <paramref name="file"/> (null =
    /// the folder's only model GGUF). Check <see cref="AgentFeatures.GgufInspect"/> first. The master's <c>AgentClient</c>
    /// implements it (MN-202); the default member (throws <see cref="NotSupportedException"/>) keeps other implementations
    /// and test doubles compiling.
    /// </summary>
    Task<GgufSummary> InspectGgufAsync(string folder, string? file, CancellationToken ct = default) =>
        throw new NotSupportedException("GGUF inspection is not implemented by this agent client.");

    /// <summary><see cref="AgentRoutes.DownloadStart"/>.</summary>
    Task<NodeDownloadStatus> StartDownloadAsync(DownloadStartRequest request, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.DownloadsList"/>.</summary>
    Task<IReadOnlyList<NodeDownloadStatus>> ListDownloadsAsync(CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.DownloadGet"/>; null when unknown.</summary>
    Task<NodeDownloadStatus?> GetDownloadAsync(string folder, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.DownloadCancel"/>.</summary>
    Task<OperationStatus> CancelDownloadAsync(string folder, DownloadCancelRequest request, CancellationToken ct = default);

    // ---- Operations ----

    /// <summary><see cref="AgentRoutes.OperationGet"/>; null when the agent does not know the id.</summary>
    Task<OperationStatus?> GetOperationAsync(string operationId, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.OperationsList"/>.</summary>
    Task<IReadOnlyList<OperationStatus>> ListOperationsAsync(OperationsQuery query, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.OperationCancel"/>: cancels <paramref name="operationId"/> with a new operation.</summary>
    Task<OperationStatus> CancelOperationAsync(string operationId, OperationCancelRequest request, CancellationToken ct = default);

    // ---- Host services ----

    /// <summary><see cref="AgentRoutes.HostServicesList"/>.</summary>
    Task<HostServicesResponse> ListHostServicesAsync(HostServicesQuery query, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.HostServiceStart"/> / <see cref="AgentRoutes.HostServiceStop"/> / <see cref="AgentRoutes.HostServiceRestart"/> by <paramref name="action"/>.</summary>
    Task<OperationStatus> RunHostServiceAsync(string name, HostServiceAction action, HostServiceActionRequest request, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.HostServiceAutostart"/>.</summary>
    Task<OperationStatus> SetHostServiceAutostartAsync(string name, HostServiceAutostartRequest request, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.HostWhitelistAdd"/>.</summary>
    Task<OperationStatus> AddToWhitelistAsync(HostWhitelistAddRequest request, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.HostWhitelistUpdate"/>.</summary>
    Task<OperationStatus> UpdateWhitelistAsync(string name, HostWhitelistUpdateRequest request, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.HostWhitelistRemove"/>.</summary>
    Task<OperationStatus> RemoveFromWhitelistAsync(string name, HostWhitelistRemoveRequest request, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.HostGpuProcesses"/>; null when unavailable (no hostd, older hostd, no <c>nvidia-smi</c>).</summary>
    Task<IReadOnlyList<GpuProcess>?> GetGpuProcessesAsync(CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.HostContainerGet"/>; null when unknown.</summary>
    Task<HostContainerInfo?> GetContainerAsync(string name, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.HostContainerStart"/> / <see cref="AgentRoutes.HostContainerStop"/> / <see cref="AgentRoutes.HostContainerRestart"/> by <paramref name="action"/>.</summary>
    Task<OperationStatus> RunContainerAsync(string name, HostServiceAction action, HostContainerActionRequest request, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.HostContainerAutostart"/>.</summary>
    Task<OperationStatus> SetContainerAutostartAsync(string name, HostContainerAutostartRequest request, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.BackupMountGet"/>.</summary>
    Task<BackupMountInfo> GetBackupMountAsync(CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.BackupMountSet"/>.</summary>
    Task<OperationStatus> SetBackupMountAsync(BackupMountSetRequest request, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.BackupMountRemove"/>.</summary>
    Task<OperationStatus> RemoveBackupMountAsync(BackupMountRemoveRequest request, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.BackupMountTest"/>.</summary>
    Task<BackupMountTestResult> TestBackupMountAsync(BackupMountTestRequest request, CancellationToken ct = default);

    // ---- PKI ----

    /// <summary><see cref="AgentRoutes.PkiRotationStart"/> (result <see cref="PkiRotationCsr"/>).</summary>
    Task<OperationStatus> StartRotationAsync(PkiRotationStartRequest request, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.PkiRotationCert"/>.</summary>
    Task<OperationStatus> InstallRotationCertificateAsync(PkiRotationCertRequest request, CancellationToken ct = default);

    /// <summary>
    /// <see cref="AgentRoutes.PkiRotationCommit"/>. Must be sent through a client from
    /// <see cref="IAgentClientFactory.CreateFreshClientAsync"/> so the commit travels on a connection that negotiated the
    /// new certificate (design §10.2 step 3).
    /// </summary>
    Task<OperationStatus> CommitRotationAsync(PkiRotationCommitRequest request, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.PkiRotationAbort"/>.</summary>
    Task<OperationStatus> AbortRotationAsync(PkiRotationAbortRequest request, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.PkiRotationGet"/>; null when the agent does not know the rotation.</summary>
    Task<PkiRotationStatus?> GetRotationAsync(string rotationId, CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.PkiTrustGet"/>.</summary>
    Task<PkiTrustResponse> GetTrustAsync(CancellationToken ct = default);

    /// <summary><see cref="AgentRoutes.PkiTrustSet"/>.</summary>
    Task<OperationStatus> SetTrustAsync(PkiTrustSetRequest request, CancellationToken ct = default);

    /// <summary><c>POST /agent/update</c> (feature <see cref="AgentFeatures.AgentUpdate"/>, docs/multinode/agent-updates.md).</summary>
    Task<OperationStatus> UpdateAgentAsync(AgentUpdateRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("This agent client does not send agent updates.");
}

/// <summary>
/// The data channel of one node (design §7.2): the inference proxy over the node's <b>data</b> handler (16 connections,
/// multiple HTTP/2 connections), bounded by the per-node in-flight limit. Used by the router's <c>ProxyHandler</c>,
/// <c>RouterChatClient</c> and benchmark load generation.
/// </summary>
public interface IAgentDataClient
{
    /// <summary>Node id.</summary>
    string NodeId { get; }

    /// <summary>
    /// The absolute proxy URI for a model: <c>https://{agent}/agent/v1/proxy/{modelId}/{pathAndQuery}</c>, e.g.
    /// <c>BuildProxyUri("qwen", "v1/chat/completions")</c>.
    /// </summary>
    Uri BuildProxyUri(string modelId, string pathAndQuery);

    /// <summary>
    /// Sends <paramref name="request"/> (whose URI came from <see cref="BuildProxyUri"/>) with HTTP/2 exact,
    /// <c>ResponseHeadersRead</c> and the protocol header, and returns the response unbuffered. A 503 with the agent
    /// error shape before headers (<c>model_not_running</c>, <c>server_busy</c>) is returned as a response, not thrown,
    /// so the router can translate it (<see cref="ErrorMappings.TranslateProxyError"/>). Transport failures throw
    /// <see cref="AgentUnreachableException"/>.
    /// </summary>
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default);

    /// <summary>
    /// An <see cref="HttpMessageInvoker"/> over the data handler for code that needs one (benchmark HTTP). Requests sent
    /// through it must still set <c>Version20</c> + <c>RequestVersionExact</c>; prefer <see cref="SendAsync"/>.
    /// </summary>
    HttpMessageInvoker Invoker { get; }
}

/// <summary>
/// Builds and owns per-node agent clients (design §7.2, §10.2), implemented in <c>MirbadLLM.Orchestration.Agents</c>
/// (MN-021).
/// <para>Invariants: each node has two independently owned <c>SocketsHttpHandler</c>s (control: 2 connections, no
/// multiple HTTP/2 connections; data: 16 connections, multiple HTTP/2 connections), both with the same authentication
/// policy (master client certificate; custom-root chain to the master CA(s); SAN <c>spiffe://mirbadllm/node/{id}</c>;
/// EKU serverAuth; SPKI pin = current or pending pin) and keep-alive pings (30 s / 10 s, with active requests), connect
/// timeout 5 s. On a certificate or trust change <see cref="RebuildHandlers"/> creates new handlers for both channels;
/// the old ones drain (in-flight requests finish, at most 5 minutes) and are then disposed.</para>
/// </summary>
public interface IAgentClientFactory
{
    /// <summary>The client of the current handler generation for a node. Throws <see cref="KeyNotFoundException"/> for an unknown or revoked node.</summary>
    IAgentClient GetClient(string nodeId);

    /// <summary>
    /// A client over brand-new handlers with no pooled connection (design §10.2 step 3: the rotation commit is sent over a
    /// fresh connection that must negotiate the new certificate and match the pending pin). Dispose it when done.
    /// </summary>
    Task<IAgentClientLease> CreateFreshClientAsync(string nodeId, CancellationToken ct = default);

    /// <summary>Replaces both handlers of a node after its pins, the master certificate or the trust anchors changed; old handlers drain.</summary>
    void RebuildHandlers(string nodeId);

    /// <summary>
    /// Records the protocol version negotiated with a node (design §3.6); sent as <c>MirbadLLM-Protocol</c> from then on.
    /// Until set, <see cref="Protocol.CurrentVersion"/> is sent.
    /// </summary>
    void SetNegotiatedProtocol(string nodeId, int version);
}

/// <summary>A client over dedicated handlers (<see cref="IAgentClientFactory.CreateFreshClientAsync"/>); disposing it disposes them.</summary>
public interface IAgentClientLease : IAsyncDisposable
{
    /// <summary>The client.</summary>
    IAgentClient Client { get; }
}
