using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Audit;

namespace MirbadLLM.Orchestration;

/// <summary>
/// The master's <see cref="IRoomMaker"/>. Given the agent's admission refusal for a start on request, it stops models on
/// the same node until their budgets cover what the refusal says is missing (need − free):
/// <list type="bullet">
/// <item>Candidates carry <see cref="ModelDefinition.StopToMakeRoom"/>, are Ready, have no request in flight or waiting
/// (<see cref="IRouterStats.InUse"/>) and are not held by a job, move or restore (<see cref="AutoStopGuard"/>). When the
/// refusal names a GPU, only models placed on that GPU count, each with its share of the budget there.</item>
/// <item>Least recently used first (<see cref="IRouterStats.LastRequestAt"/>; never requested = oldest), and only as many
/// as the missing memory needs. When all candidates together would not cover it, nothing is stopped.</item>
/// <item>Nothing is stopped without the agent's numbers, or for a refusal about host RAM on a discrete-GPU node: what a
/// stop would free is unknown there.</item>
/// <item>One round at a time per node. A request that waited for another request's round stops nothing itself and asks
/// admission again, so two requests for two stopped models do not empty the node between them.</item>
/// </list>
/// Stops run under the <see cref="AuditActors.SystemMakeRoom"/> actor and are ordinary stops for recovery.
/// </summary>
/// <param name="stats">The router's view of use; null (no router composed) = nothing is ever stopped.</param>
public sealed partial class RoomMaker(
    IModelRegistry registry,
    IModelRuntime runtime,
    IRouterStats? stats,
    ILogger<RoomMaker> logger,
    IMaintenanceMode? maintenance = null,
    IOwnershipStore? ownership = null) : IRoomMaker
{
    private readonly ConcurrentDictionary<string, NodeGate> _gates = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<bool> TryMakeRoomAsync(string modelId, ModelMemoryAdmissionException refusal, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ArgumentNullException.ThrowIfNull(refusal);
        var details = AgentErrorMapper.AdmissionDetails(refusal);
        var wanted = await registry.GetAsync(modelId, ct).ConfigureAwait(false);
        if (stats is null || details is null || details.Host == true || wanted?.Placement?.NodeId is not { } nodeId)
        {
            return false;
        }

        var gate = _gates.GetOrAdd(nodeId, static _ => new NodeGate());
        int roundsSeen = Volatile.Read(ref gate.Rounds);
        await gate.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref gate.Rounds) != roundsSeen)
            {
                return true; // Another request made room here while this one waited: ask admission again before stopping more.
            }

            double missing = details.NeedGiB - details.FreeGiB;
            var models = await registry.ListAsync(ct).ConfigureAwait(false);
            var ready = (await runtime.ListStatusAsync(ct).ConfigureAwait(false))
                .Where(s => s.State == ModelState.Ready).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var victims = new List<ModelDefinition>();
            double freed = 0;
            foreach (var candidate in models
                .Where(m => m.StopToMakeRoom && m.Id != modelId && m.Placement?.NodeId == nodeId && ready.Contains(m.Id) &&
                    HeldGiB(m, details.Gpu) > 0 && !stats.InUse(m.ServedName))
                .OrderBy(m => stats.LastRequestAt(m.ServedName) ?? DateTimeOffset.MinValue)
                .ThenBy(m => m.Id, StringComparer.Ordinal))
            {
                if (freed >= missing)
                {
                    break;
                }

                if (!await AutoStopGuard.HoldsAsync(candidate, maintenance, ownership, ct).ConfigureAwait(false))
                {
                    victims.Add(candidate);
                    freed += HeldGiB(candidate, details.Gpu);
                }
            }

            if (freed < missing)
            {
                NotEnough(logger, modelId, nodeId, missing, freed);
                return false;
            }

            int stopped = 0;
            using var audit = AuditScope.Begin(AuditActors.SystemMakeRoom);
            foreach (var victim in victims.Where(v => !stats.InUse(v.ServedName))) // a request may have arrived since it was picked
            {
                try
                {
                    await runtime.StopAsync(victim.Id, ct).ConfigureAwait(false);
                    stopped++;
                    Stopped(logger, victim.Id, nodeId, modelId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    StopFailed(logger, ex, victim.Id, modelId);
                }
            }

            if (stopped == 0)
            {
                return false;
            }

            Interlocked.Increment(ref gate.Rounds);
            return true;
        }
        finally
        {
            gate.Lock.Release();
        }
    }

    /// <summary>The budget a model holds on <paramref name="gpu"/>, or on its node's one pool when the refusal names no GPU.</summary>
    private static double HeldGiB(ModelDefinition model, string? gpu)
    {
        if (gpu is null)
        {
            return model.MemoryBudgetGiB;
        }

        var uuids = model.Placement?.GpuUuids ?? [];
        int index = uuids.ToList().IndexOf(gpu);
        if (index < 0)
        {
            return 0;
        }

        // shortcut: without explicit per-GPU budgets this is an even split, also for llama.cpp's proportional split on unequal cards, so it can stop one model too few or too many there; use PlacementFit.LlamaCppGpuBudgets if that shows up.
        return model.LlamaCpp?.GpuBudgetsGiB is { } budgets && budgets.Count == uuids.Count ? budgets[index] : model.MemoryBudgetGiB / uuids.Count;
    }

    private sealed class NodeGate
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);

        /// <summary>Rounds on this node that stopped at least one model.</summary>
        public int Rounds;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopped model {VictimId} on node {NodeId} to make room for {ModelId}, which a request asked for.")]
    private static partial void Stopped(ILogger logger, string victimId, string nodeId, string modelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "No room made for {ModelId} on node {NodeId}: it is {MissingGiB:0.##} GiB short and the idle models that may be stopped hold {FreedGiB:0.##} GiB.")]
    private static partial void NotEnough(ILogger logger, string modelId, string nodeId, double missingGiB, double freedGiB);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not stop model {VictimId} to make room for {ModelId}.")]
    private static partial void StopFailed(ILogger logger, Exception exception, string victimId, string modelId);
}
