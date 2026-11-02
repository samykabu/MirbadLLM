using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>
/// Design §6.7: "a model with an ownership record refuses other owners … plain start/stop requests are refused too, except
/// the holder's own" (composed by MN-130). Checked at the operator's entry points — <c>POST /api/models/{id}/start|stop|restart</c>
/// and the UI's start/stop/restart — so the holders' own background work (a job's stops and launches, a move's target
/// start, a restore's auto-start) and the master's boot auto-start are never refused by it. A caller inside
/// <see cref="ModelWriteScope.AsOwner"/> with the holder's record id is the holder and passes.
/// </summary>
public static class HeldModelGuard
{
    /// <summary>
    /// Throws <see cref="ModelBusyException"/> (409 <c>model_busy</c>, naming the holder) when an ownership record other than
    /// the current scope's (<see cref="ModelWriteScope.CurrentOwnerRecordId"/>) holds <paramref name="modelId"/>. No store
    /// (the single-box wiring) = nothing is held.
    /// </summary>
    public static async Task EnsureNotHeldAsync(IOwnershipStore? ownership, string modelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        if (ownership is null)
        {
            return;
        }

        var holder = await ownership.GetForModelAsync(modelId, ct).ConfigureAwait(false);
        if (holder is not null && !string.Equals(holder.Id, ModelWriteScope.CurrentOwnerRecordId, StringComparison.Ordinal))
        {
            throw new ModelBusyException(modelId, holder);
        }
    }
}
