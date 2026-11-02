using System.Collections.Concurrent;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>Timings of <see cref="AgentJobTargets"/>; tests shrink them.</summary>
public sealed class AgentJobTargetsOptions
{
    /// <summary>How often boot recovery re-checks whether the nodes' open operations are reconciled.</summary>
    public TimeSpan ReconcilePollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Nodes whose agent runs on the master's own host (topology a: the bootstrap node, reached over the Docker network
    /// by name, never over the LAN). Their runs record <see cref="BenchmarkDataPaths.AgentLoopback"/> like an agent
    /// reached on a loopback address; every other node records <see cref="BenchmarkDataPaths.AgentLan"/> (W4 review
    /// MN-096).
    /// </summary>
    public IReadOnlyCollection<string> CoLocatedNodeIds { get; init; } = [];
}

/// <summary>
/// Benchmarks and tuning over the agents (MN-033, design §6.5): the load goes through the node's <b>data</b> client to the
/// agent's inference proxy (<c>/agent/v1/proxy/{modelId}/…</c>, HTTP/2 over mTLS, the per-node in-flight bound) — the path
/// clients use; the GPU idle check is the node's <c>/gpu/idle</c>; boot recovery of interrupted tuning runs waits until
/// the nodes are reconciled. The model's node is where its instance runs (<see cref="INodeStatusCache"/>), else its
/// placement. Not registered in DI here (MN-040).
/// </summary>
public sealed class AgentJobTargets : IBenchmarkTargetResolver, IGpuIdleSampler, IJobRecoveryGate, IDisposable
{
    /// <summary>Operation kinds that create or remove model instances: recovery waits until none is open.</summary>
    private static readonly HashSet<string> RuntimeKinds =
        new([OperationKinds.Launch, OperationKinds.Stop, OperationKinds.OperationCancel], StringComparer.Ordinal);

    private readonly IAgentClientFactory _agents;
    private readonly INodeStatusCache _nodes;
    private readonly IModelRegistry _registry;
    private readonly IAgentOperationReferenceStore _references;
    private readonly TimeProvider _time;
    private readonly AgentJobTargetsOptions _options;
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _nodeByAuthority = new(StringComparer.OrdinalIgnoreCase);

    public AgentJobTargets(
        IAgentClientFactory agents,
        INodeStatusCache nodes,
        IModelRegistry registry,
        IAgentOperationReferenceStore references,
        TimeProvider? timeProvider = null,
        AgentJobTargetsOptions? options = null)
    {
        _agents = agents;
        _nodes = nodes;
        _registry = registry;
        _references = references;
        _time = timeProvider ?? TimeProvider.System;
        _options = options ?? new AgentJobTargetsOptions();
    }

    // ---------------------------------------------------------------- target

    /// <inheritdoc/>
    public async Task<BenchmarkEndpoint> ResolveAsync(string modelId, ModelStatus status, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        string nodeId = await NodeOfAsync(modelId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No node runs {modelId} (it is not placed).");

        Uri uri;
        try
        {
            uri = _agents.GetClient(nodeId).Data.BuildProxyUri(modelId, "");
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidOperationException($"Node {nodeId} of {modelId} is unknown or revoked.", ex);
        }

        if (!uri.AbsoluteUri.EndsWith('/'))
        {
            uri = new Uri(uri.AbsoluteUri + "/");
        }

        _clients.GetOrAdd(nodeId, id => new HttpClient(new AgentProxyHandler(_agents, id)) { Timeout = Timeout.InfiniteTimeSpan });
        _nodeByAuthority[uri.GetLeftPart(UriPartial.Authority)] = nodeId;
        bool sameHost = uri.IsLoopback || _options.CoLocatedNodeIds.Contains(nodeId, StringComparer.Ordinal);
        return new BenchmarkEndpoint(uri, sameHost ? BenchmarkDataPaths.AgentLoopback : BenchmarkDataPaths.AgentLan, nodeId);
    }

    /// <inheritdoc/>
    public HttpClient? GetClient(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        return _nodeByAuthority.TryGetValue(baseUri.GetLeftPart(UriPartial.Authority), out string? nodeId) &&
            _clients.TryGetValue(nodeId, out var client)
            ? client
            : null;
    }

    // ---------------------------------------------------------------- GPU idle

    /// <inheritdoc/>
    public async Task<GpuIdleReading> SampleAsync(string? modelId, TimeSpan duration, CancellationToken ct = default)
    {
        string? nodeId = modelId is null ? SingleNode() : await NodeOfAsync(modelId, ct).ConfigureAwait(false);
        if (nodeId is null)
        {
            return new GpuIdleReading(null, null, 0);
        }

        int seconds = Math.Clamp((int)Math.Round(duration.TotalSeconds), 1, 60);
        var gpus = modelId is null ? null : await GpuIndicesAsync(modelId, nodeId, ct).ConfigureAwait(false);
        try
        {
            var idle = await _agents.GetClient(nodeId).SampleGpuIdleAsync(new GpuIdleQuery { Seconds = seconds, Gpus = gpus }, ct).ConfigureAwait(false);
            return new GpuIdleReading(idle.AveragePercent, idle.MaxPercent, idle.Samples);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // No reading: the check is advisory, like a failed local sample.
            return new GpuIdleReading(null, null, 0);
        }
    }

    /// <summary>
    /// The current indices of the model's GPUs on a discrete node (MN-112, D80: the node's UUIDs mapped through its last
    /// capability report, since <c>/gpu/idle</c> takes indices); null = every GPU (a unified node, no GPU list, or a UUID
    /// the report does not list: sampling more GPUs is the conservative choice).
    /// </summary>
    private async Task<IReadOnlyList<int>?> GpuIndicesAsync(string modelId, string nodeId, CancellationToken ct)
    {
        if (_nodes.GetNode(nodeId)?.Capabilities is not { Memory.Model: MemoryModel.Discrete } capabilities)
        {
            return null;
        }

        var uuids = _nodes.GetRuntime(modelId)?.GpuUuids is { Count: > 0 } running
            ? running
            : (await _registry.GetAsync(modelId, ct).ConfigureAwait(false))?.Placement?.GpuUuids ?? [];
        var indices = new List<int>(uuids.Count);
        foreach (string uuid in uuids)
        {
            if (capabilities.Gpus.FirstOrDefault(g => string.Equals(g.Uuid, uuid, StringComparison.OrdinalIgnoreCase)) is not { } gpu)
            {
                return null;
            }

            indices.Add(gpu.Index);
        }

        return indices.Count > 0 ? indices : null;
    }

    // ---------------------------------------------------------------- recovery gate

    /// <inheritdoc/>
    public Task WaitUntilReconciledAsync(CancellationToken ct = default) => PollUntilAsync(IsReconciledAsync, ct);

    /// <inheritdoc/>
    /// <remarks>Every node the status cache knows (registered nodes, serving or not).</remarks>
    public IReadOnlyList<string> NodeIds => [.. _nodes.ListNodes().Select(n => n.NodeId)];

    /// <inheritdoc/>
    /// <remarks>Always: the status cache fills after boot (the node monitor's first sync), so the list can start empty.</remarks>
    public bool IsPerNode => true;

    /// <inheritdoc/>
    public Task WaitUntilNodeReconciledAsync(string nodeId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        return PollUntilAsync(token => IsNodeReconciledAsync(nodeId, token), ct);
    }

    /// <summary>
    /// True when every known node is serving with a snapshot applied (its boot is known), the node lists no open
    /// launch/stop/cancel operation, and the master holds no open reference of those kinds for it (design §4.4, §4.6).
    /// </summary>
    public async Task<bool> IsReconciledAsync(CancellationToken ct = default)
    {
        var open = await _references.ListOpenAsync(ct).ConfigureAwait(false);
        return _nodes.ListNodes().All(node => IsReconciled(node, open));
    }

    /// <summary>
    /// As <see cref="IsReconciledAsync"/> for <paramref name="nodeId"/> alone (MN-111): the other nodes' state does not
    /// matter. False for a node the cache does not know (yet).
    /// </summary>
    public async Task<bool> IsNodeReconciledAsync(string nodeId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        var open = await _references.ListOpenAsync(ct).ConfigureAwait(false);
        return _nodes.ListNodes().FirstOrDefault(n => string.Equals(n.NodeId, nodeId, StringComparison.Ordinal)) is { } node &&
            IsReconciled(node, open);
    }

    private static bool IsReconciled(NodeLiveStatus node, IReadOnlyList<AgentOperationReference> open) =>
        node.IsServing && node.BootId is not null &&
        !node.OpenOperations.Any(o => RuntimeKinds.Contains(o.Kind)) &&
        !open.Any(r => string.Equals(r.NodeId, node.NodeId, StringComparison.Ordinal) && RuntimeKinds.Contains(r.Action));

    private async Task PollUntilAsync(Func<CancellationToken, Task<bool>> reconciled, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                if (await reconciled(ct).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // operations.json unreadable for now: not reconciled; check again (a hosted service must not fault).
            }

            await Task.Delay(_options.ReconcilePollInterval, _time, ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        _clients.Clear();
    }

    private async Task<string?> NodeOfAsync(string modelId, CancellationToken ct) =>
        _nodes.GetRuntime(modelId)?.NodeId
        ?? (await _registry.GetAsync(modelId, ct).ConfigureAwait(false))?.Placement?.NodeId;

    private string? SingleNode() => _nodes.ListNodes() is [var only] ? only.NodeId : null;

    /// <summary>
    /// Sends every request through the node's current data client (a handler rebuild between two requests of a long run
    /// is picked up); transport failures surface as <see cref="HttpRequestException"/>, as the suite expects. Agent
    /// refusals (<c>model_not_running</c>, <c>server_busy</c>) stay error responses carrying the agent's message.
    /// </summary>
    private sealed class AgentProxyHandler(IAgentClientFactory agents, string nodeId) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            IAgentDataClient data;
            try
            {
                data = agents.GetClient(nodeId).Data;
            }
            catch (KeyNotFoundException ex)
            {
                throw new HttpRequestException($"Node {nodeId} is unknown or revoked.", ex);
            }

            try
            {
                return await data.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (AgentUnreachableException ex)
            {
                throw new HttpRequestException(ex.Message, ex);
            }
        }
    }
}
