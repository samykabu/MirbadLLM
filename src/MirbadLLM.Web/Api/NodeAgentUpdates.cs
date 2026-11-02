using System.Text;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;

namespace MirbadLLM.Web.Api;

/// <summary><c>POST /api/nodes/{id}/agent-update</c>: the signed-in session (the action replaces the node's agent).</summary>
public sealed record AgentUpdateStartRequest(string? Password = null);

/// <summary>A pushed update that the agent accepted.</summary>
public sealed record AgentUpdateStartResult(string NodeId, string OperationId, string? FromVersion, string ToVersion);

/// <summary>
/// Pushed agent updates (docs/multinode/agent-updates.md U3–U5): the master signs an order for one node's bundle with its
/// CA and sends it over the node's mTLS connection; the agent downloads the bundle from this master and its root helper
/// verifies the order and runs the bundle's installer. Only for a reachable node that reports
/// <see cref="AgentFeatures.AgentUpdate"/> and runs another build than the bundle.
/// </summary>
public sealed partial class NodeAdminService
{
    /// <summary>How long a signed order stays valid for the helper.</summary>
    public static readonly TimeSpan AgentUpdateOrderLifetime = TimeSpan.FromHours(1);

    /// <summary>The admin action: checks the session, audits, then <see cref="PushAgentUpdateAsync"/>.</summary>
    public Task<AgentUpdateStartResult> StartAgentUpdateAsync(
        string nodeId, AgentUpdateStartRequest request, NodeApiCaller caller, AgentOperationRunner runner, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        return AuditedAsync(AuditActions.NodeAgentUpdate, nodeId, caller, async () =>
        {
            Reauthenticate(caller);
            var started = await PushAgentUpdateAsync(nodeId, runner, ct);
            return (started, $"agent {started.FromVersion ?? "unknown"} -> {started.ToVersion}, operation {started.OperationId}");
        }, ct);
    }

    /// <summary>Why a node's agent cannot be updated from here now, or null when it can.</summary>
    public string? AgentUpdateBlocker(NodeLiveStatus? live)
    {
        if (live?.IsServing != true)
        {
            return "The node is not reachable for updates. Wait for it to reconnect, or use the manual steps below.";
        }

        if (live.OpenOperations.Any(o => o.Kind == OperationKinds.AgentUpdate))
        {
            return "An update is already running.";
        }

        if (live.Capabilities is not { } caps || !caps.Features.Contains(AgentFeatures.AgentUpdate, StringComparer.Ordinal))
        {
            return "This node has no update helper yet: update its agent once by hand (the steps below install the helper).";
        }

        if (AgentUpdatePlan.RidFor(caps) is not { } rid || _agentBundles?.Find(rid) is not { } bundle)
        {
            return "This master has no agent bundle for this node's OS.";
        }

        return ProductVersion.SameBuild(live.AgentVersion, "+" + bundle.Version) == true ? "The agent already runs this build." : null;
    }

    /// <summary>Signs and sends the order (no password: the caller decided, e.g. the fleet's automatic update).</summary>
    public async Task<AgentUpdateStartResult> PushAgentUpdateAsync(string nodeId, AgentOperationRunner runner, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        NodeRecord record = await RequireAsync(nodeId, ct);
        RefuseRevoked(record);
        NodeLiveStatus? live = _cache?.GetNode(nodeId);
        if (AgentUpdateBlocker(live) is { } blocker)
        {
            throw new NodeApiException(ErrorCodes.InvalidState, blocker);
        }

        string rid = AgentUpdatePlan.RidFor(live!.Capabilities)!;
        var bundle = _agentBundles!.Find(rid)!;
        var now = _time.GetUtcNow();
        var order = new AgentUpdateOrder(live.NodeUid ?? record.Uid, rid, bundle.Version, bundle.Sha256, bundle.Size, now, now + AgentUpdateOrderLifetime);
        string text = order.Format();
        var authority = _master?.Authority ?? _ca;
        var envelope = runner.CreateEnvelope();
        var request = new AgentUpdateRequest
        {
            OperationId = envelope.OperationId,
            IssuedAt = envelope.IssuedAt,
            ExpiresAt = envelope.ExpiresAt,
            Rid = rid,
            Version = bundle.Version,
            Sha256 = bundle.Sha256,
            Size = bundle.Size,
            BundlePath = AgentBundleCatalog.BundleUrl(new Uri("http://master/"), rid).AbsolutePath,
            BundlePort = _options.AgentBundleUrl?.Port ?? 8080,
            Order = text,
            Signature = Convert.ToBase64String(authority.SignData(Encoding.UTF8.GetBytes(text))),
        };

        try
        {
            await runner.SubmitAsync(nodeId, AgentRoutes.AgentUpdate, bundle.Version, request, (c, r, t) => c.UpdateAgentAsync(r, t), owner: null, ct);
        }
        catch (AgentException ex)
        {
            throw new NodeApiException(ex.Error.Code, $"The agent refused the update: {ex.Error.Message}", StatusCodes.Status502BadGateway);
        }

        LogAgentUpdatePushed(_logger, nodeId, live.AgentVersion ?? "unknown", bundle.Version, envelope.OperationId);
        return new AgentUpdateStartResult(nodeId, envelope.OperationId, live.AgentVersion, bundle.Version);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Node {NodeId}: agent update {From} -> {To} sent (operation {OperationId}).")]
    private static partial void LogAgentUpdatePushed(ILogger logger, string nodeId, string from, string to, string operationId);
}
