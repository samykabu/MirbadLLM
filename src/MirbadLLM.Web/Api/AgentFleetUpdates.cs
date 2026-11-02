using Microsoft.Extensions.Hosting;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;

namespace MirbadLLM.Web.Api;

/// <summary><c>PUT /api/agent-updates/settings</c>. Password is accepted for compatibility; the current admin session authorizes the setting.</summary>
public sealed record AgentUpdateSettingsRequest(bool Automatic, string? Password = null);

/// <summary>What "update all" did for each outdated node.</summary>
public sealed record AgentFleetUpdateResult(IReadOnlyList<AgentUpdateStartResult> Started, IReadOnlyDictionary<string, string> Skipped);

public sealed partial class NodeAdminService
{
    /// <summary>The fleet setting (null when the host registered none: then automatic updates are unavailable).</summary>
    public AgentUpdateSettingsStore? UpdateSettings { get; init; }

    /// <summary>Who the automatic updates are recorded as in the audit log.</summary>
    public static readonly NodeApiCaller AutomaticCaller = new("system:agent-auto-update", "master");

    /// <summary>The nodes whose agent runs another build than MirbadLLM, with why each cannot be pushed now (null: it can).</summary>
    public IReadOnlyList<(NodeLiveStatus Node, string? Blocker)> OutdatedAgents()
    {
        if (_cache is null)
        {
            return [];
        }

        return [.. _cache.ListNodes()
            .Where(n => n.State != NodeState.PendingApproval && ProductVersion.SameBuild(n.AgentVersion) == false)
            .OrderBy(n => n.NodeId, StringComparer.Ordinal)
            .Select(n => (n, n.OpenOperations.Any(o => o.Kind == OperationKinds.AgentUpdate) ? "An update is already running." : AgentUpdateBlocker(n)))];
    }

    /// <summary>"Update all": every outdated node that can be pushed now (signed-in session; one audit line per node).</summary>
    public async Task<AgentFleetUpdateResult> UpdateOutdatedAgentsAsync(AgentUpdateStartRequest request, NodeApiCaller caller, AgentOperationRunner runner, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Reauthenticate(caller);
        var started = new List<AgentUpdateStartResult>();
        var skipped = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (node, blocker) in OutdatedAgents())
        {
            if (blocker is not null)
            {
                skipped[node.NodeId] = blocker;
                continue;
            }

            try
            {
                started.Add(await AuditedAsync(AuditActions.NodeAgentUpdate, node.NodeId, caller, async () =>
                {
                    var result = await PushAgentUpdateAsync(node.NodeId, runner, ct);
                    return (result, $"agent {result.FromVersion ?? "unknown"} -> {result.ToVersion}, operation {result.OperationId} (update all)");
                }, ct));
            }
            catch (NodeApiException ex)
            {
                skipped[node.NodeId] = ex.Message;
            }
        }

        return new AgentFleetUpdateResult(started, skipped);
    }

    /// <summary>Turns the fleet's automatic agent updates on or off under the portal's access policy; audited.</summary>
    public Task<bool> SetAutomaticAgentUpdatesAsync(bool automatic, NodeApiCaller caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return AuditedAsync(AuditActions.AgentUpdateSettings, null, caller, () =>
        {
            var store = UpdateSettings ?? throw new NodeApiException(ErrorCodes.NotSupported, "Automatic agent updates are not available on this master.");
            if (!store.CanPersist)
            {
                throw new NodeApiException(ErrorCodes.NotSupported, "This master has no data folder to keep the setting in.");
            }

            store.SetAutomatic(automatic);
            return Task.FromResult((automatic, automatic ? "automatic agent updates on" : "automatic agent updates off"));
        }, ct);
    }

    /// <summary>One automatic update (no password: the admin turned the setting on), audited as <see cref="AutomaticCaller"/>.</summary>
    public Task<AgentUpdateStartResult> AutoUpdateAgentAsync(string nodeId, AgentOperationRunner runner, CancellationToken ct = default) =>
        AuditedAsync(AuditActions.NodeAgentUpdate, nodeId, AutomaticCaller, async () =>
        {
            var result = await PushAgentUpdateAsync(nodeId, runner, ct);
            return (result, $"agent {result.FromVersion ?? "unknown"} -> {result.ToVersion}, operation {result.OperationId} (automatic)");
        }, ct);
}

/// <summary>
/// Automatic agent updates (docs/multinode/agent-updates.md U8): while the fleet setting is on, every minute, the first
/// outdated node that can be pushed is updated, and nothing else while any update runs, so nodes update one at a time.
/// A node that is offline is picked up once it is back; one whose update failed is retried only after an hour.
/// </summary>
public sealed partial class AgentAutoUpdater(NodeAdminService admin, AgentOperationRunner? runner, TimeProvider time, ILogger<AgentAutoUpdater> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    private readonly Dictionary<string, DateTimeOffset> _tried = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTickFailed(logger, ex);
            }
        }
    }

    /// <summary>One pass: at most one update started.</summary>
    public async Task<string?> TickAsync(CancellationToken ct = default)
    {
        // No agent connections (a master without nodes): nothing to update.
        if (runner is null || admin.UpdateSettings?.Automatic != true)
        {
            return null;
        }

        var outdated = admin.OutdatedAgents();
        if (outdated.Any(o => o.Node.OpenOperations.Any(op => op.Kind == OperationKinds.AgentUpdate)))
        {
            return null; // one at a time
        }

        var now = time.GetUtcNow();
        foreach (var (node, blocker) in outdated)
        {
            if (blocker is not null || (_tried.TryGetValue(node.NodeId, out var at) && now - at < RetryAfter))
            {
                continue;
            }

            _tried[node.NodeId] = now;
            try
            {
                var started = await admin.AutoUpdateAgentAsync(node.NodeId, runner, ct).ConfigureAwait(false);
                LogStarted(logger, node.NodeId, started.ToVersion);
                return node.NodeId;
            }
            catch (NodeApiException ex)
            {
                LogRefused(logger, node.NodeId, ex.Message);
            }
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Automatic agent update: node {NodeId} -> {Version}.")]
    private static partial void LogStarted(ILogger logger, string nodeId, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automatic agent update of node {NodeId} was refused: {Reason}")]
    private static partial void LogRefused(ILogger logger, string nodeId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automatic agent updates: a pass failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);
}
