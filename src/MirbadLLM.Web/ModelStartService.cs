using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Multinode;

namespace MirbadLLM.Web;

/// <summary>Manual starts wait in node order; the runtime still owns capacity admission and launch execution.</summary>
public sealed class ModelStartService(
    IModelRegistry registry,
    IModelRuntime runtime,
    IHostApplicationLifetime lifetime,
    IOwnershipStore? ownership = null,
    IOptions<RegistryMigrationOptions>? migration = null,
    OperatorStops? operatorStops = null)
{
    private readonly PerNodeStartLanes _lanes = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _pending = new(StringComparer.Ordinal);

    public bool IsPending(string id)
    {
        lock (_gate)
        {
            return _pending.TryGetValue(id, out var entry) && !entry.Cancellation.IsCancellationRequested;
        }
    }

    public bool IsQueued(string id)
    {
        lock (_gate)
        {
            return _pending.TryGetValue(id, out var entry) && !entry.Dispatched && !entry.Cancellation.IsCancellationRequested;
        }
    }

    /// <summary>
    /// Cancels a queued manual start. Every operator stop calls this first, so it also tells a boot auto-start still waiting
    /// for its node to leave the model stopped (<see cref="OperatorStops"/>).
    /// </summary>
    public bool Cancel(string id)
    {
        operatorStops?.Record(id);
        lock (_gate)
        {
            if (!_pending.TryGetValue(id, out var entry) || entry.Dispatched)
            {
                return false;
            }

            entry.Cancellation.Cancel();
            return true;
        }
    }

    public async Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default)
    {
        var model = await registry.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Unknown model '{id}'.");
        await HeldModelGuard.EnsureNotHeldAsync(ownership, id, ct).ConfigureAwait(false);
        Entry entry;
        lock (_gate)
        {
            if (!_pending.TryGetValue(id, out entry!) || entry.Cancellation.IsCancellationRequested)
            {
                entry = new Entry(CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.ApplicationStopping));
                _pending[id] = entry;
                // Complete a canceled request immediately, even while its predecessor still owns the lane.
                var token = entry.Cancellation.Token;
                token.Register(() => entry.Completion.TrySetCanceled(token));
                _ = _lanes.Enqueue(NodeOf(model), () => RunAsync(model, force, entry));
            }
        }

        return await entry.Completion.Task.ConfigureAwait(false);
    }

    private async Task RunAsync(ModelDefinition requested, bool force, Entry entry)
    {
        await Task.Yield(); // Enqueue under the gate preserves click order; execute outside it.
        var ct = entry.Cancellation.Token;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                // Statuses first, the definition last: a move or delete that happened before this turn is seen by the
                // re-read that decides the launch, not by one taken before the lane freed up.
                var statuses = await runtime.ListStatusAsync(ct).ConfigureAwait(false);
                var definitions = await registry.ListAsync(ct).ConfigureAwait(false);
                var model = definitions.FirstOrDefault(m => m.Id == requested.Id)
                    ?? throw new KeyNotFoundException($"Model '{requested.Id}' was deleted while queued.");
                if (model.Placement != requested.Placement)
                {
                    throw new ModelStartConflictException("The model's placement changed while queued. Start it again on its new node.");
                }

                await HeldModelGuard.EnsureNotHeldAsync(ownership, model.Id, ct).ConfigureAwait(false);
                bool alreadyRunning = statuses.Any(s => s.Id == model.Id && s.State is ModelState.Starting or ModelState.Ready);
                if (alreadyRunning || !statuses.Any(s => s.State == ModelState.Starting &&
                    definitions.Any(m => m.Id == s.Id && NodeOf(m) == NodeOf(model))))
                {
                    lock (_gate)
                    {
                        ct.ThrowIfCancellationRequested();
                        entry.Dispatched = true;
                    }

                    try
                    {
                        entry.Completion.TrySetResult(await runtime.StartAsync(model.Id, force, ct).ConfigureAwait(false));
                        return;
                    }
                    catch (ModelStartConflictException)
                    {
                        // A boot/recovery start may have taken the agent's slot after the status read.
                        lock (_gate)
                        {
                            entry.Dispatched = false;
                        }
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            entry.Completion.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            entry.Completion.TrySetException(ex);
        }
        finally
        {
            lock (_gate)
            {
                if (_pending.GetValueOrDefault(requested.Id) == entry)
                {
                    _pending.Remove(requested.Id);
                }

                entry.Cancellation.Dispose();
            }
        }
    }

    private string NodeOf(ModelDefinition model) => PerNodeStartLanes.NodeOf(model,
        migration?.Value.EffectiveBootstrapNodeId ?? RegistryMigrationOptions.DefaultBootstrapNodeId);

    private sealed class Entry(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource<ModelStatus> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Dispatched { get; set; }
    }
}
