using Microsoft.Extensions.Hosting;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>
/// On controller boot: the maintenance flag is already cleared by <see cref="MaintenanceModeService"/> (no job
/// survives a restart); this puts back the saved settings of any model an interrupted tuning run left running a
/// candidate. On shutdown, cancels every running job so a tuning run restores the saved settings before exit.
/// With a <see cref="IJobRecoveryGate"/> (over agents) the clean-up waits until the nodes' open operations are
/// reconciled (design §6.5 item 7), never acting blindly on reconnect.
/// <para>Per node (P2, MN-111; W3 review MN-094): when the gate lists its nodes, each node is recovered as soon as
/// <em>that</em> node is reconciled, in parallel, so one Offline node no longer holds the clean-up of the others back.
/// A per-node gate (<see cref="IJobRecoveryGate.IsPerNode"/>) is watched every <see cref="NodeListPollInterval"/>: a node
/// that appears after boot (the status cache fills on the node monitor's first sync, or a node enrolls later) is recovered
/// once when it shows up, so a boot that races the first sync never skips a node. A gate that is not per node keeps the
/// single global wait (with a node list read at boot, as before).</para>
/// </summary>
public sealed class ModelJobRecoveryService(ModelJobService jobs, IJobRecoveryGate? gate = null, TimeSpan? nodeListPollInterval = null) : BackgroundService
{
    /// <summary>How often a per-node gate's node list is re-read for nodes that appeared since boot.</summary>
    public TimeSpan NodeListPollInterval { get; } = nodeListPollInterval ?? TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (gate is null)
        {
            await jobs.RecoverAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        if (!gate.IsPerNode)
        {
            var nodes = gate.NodeIds.Distinct(StringComparer.Ordinal).ToList();
            if (nodes.Count == 0)
            {
                await gate.WaitUntilReconciledAsync(stoppingToken).ConfigureAwait(false);
                await jobs.RecoverAsync(stoppingToken).ConfigureAwait(false);
                return;
            }

            await Task.WhenAll(nodes.Select(node => RecoverNodeAsync(gate, node, stoppingToken))).ConfigureAwait(false);
            return;
        }

        await WatchNodesAsync(gate, stoppingToken).ConfigureAwait(false);
    }

    /// <summary>Recovers each node of a per-node gate once, as soon as it is listed and reconciled, until shutdown.</summary>
    private async Task WatchNodesAsync(IJobRecoveryGate recoveryGate, CancellationToken ct)
    {
        var started = new HashSet<string>(StringComparer.Ordinal);
        var running = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (string node in recoveryGate.NodeIds)
                {
                    if (started.Add(node))
                    {
                        running.Add(RecoverNodeAsync(recoveryGate, node, ct));
                    }
                }

                running.RemoveAll(t => t.IsCompleted);
                await Task.Delay(NodeListPollInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }

        try
        {
            await Task.WhenAll(running).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        jobs.Cancel();
        try
        {
            await jobs.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Shutting down anyway; boot recovery handles a candidate left running.
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RecoverNodeAsync(IJobRecoveryGate recoveryGate, string nodeId, CancellationToken ct)
    {
        await recoveryGate.WaitUntilNodeReconciledAsync(nodeId, ct).ConfigureAwait(false);
        await jobs.RecoverNodeAsync(nodeId, ct).ConfigureAwait(false);
    }
}
