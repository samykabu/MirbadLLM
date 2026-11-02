using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>
/// Ambient facts about one model-registry write that the registry's validators (<see cref="OwnershipValidator"/>,
/// <see cref="PlacementValidator"/>, MN-108) cannot read from the <see cref="ModelDefinitionChange"/> alone. Both flow with
/// the async call (an <see cref="AsyncLocal{T}"/>) from the caller into <c>JsonModelRegistry.UpsertAsync</c>/<c>DeleteAsync</c>,
/// where the validators run under the registry lock; outside a scope every write is an ordinary write by no holder.
/// <list type="bullet">
/// <item><see cref="AsOwner"/>: the write is made by the holder of an ownership record (a move committing its placement,
/// a restore placing a model; design §6.7). Only the holder may delete the model, change its placement or place it
/// while the record exists, and only a holder may place an unplaced model.</item>
/// <item><see cref="ExpectStoredPlacement"/>: the write merged its placement from an earlier read
/// (<c>PUT /api/models/{id}</c> without <c>placement</c>, design §5.4) and must be refused with
/// <see cref="PlacementConflictException"/> (409 <c>placement_changed</c>, D75) when the stored placement is no longer
/// the one read, or the model was deleted meanwhile (the write would re-create it). That covers the cases the
/// placement generation cannot see: a model unplaced, placed or deleted meanwhile.</item>
/// </list>
/// </summary>
public static class ModelWriteScope
{
    private static readonly AsyncLocal<string?> OwnerRecordId = new();
    private static readonly AsyncLocal<ExpectedPlacement?> Expected = new();

    /// <summary>The ownership record id the current write is made under, or null.</summary>
    public static string? CurrentOwnerRecordId => OwnerRecordId.Value;

    /// <summary>The placement the current write expects to find stored, or null when it expects nothing.</summary>
    public static ExpectedPlacement? CurrentExpectation => Expected.Value;

    /// <summary>Writes inside the returned scope are made by the holder of ownership record <paramref name="recordId"/>.</summary>
    public static IDisposable AsOwner(string recordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        string? previous = OwnerRecordId.Value;
        OwnerRecordId.Value = recordId;
        return new Restore(() => OwnerRecordId.Value = previous);
    }

    /// <summary>
    /// Writes inside the returned scope expect the stored placement to be <paramref name="placement"/> (same node, same
    /// ordered GPU UUIDs and same generation; or both unplaced).
    /// </summary>
    public static IDisposable ExpectStoredPlacement(ModelPlacement? placement)
    {
        var previous = Expected.Value;
        Expected.Value = new ExpectedPlacement(placement);
        return new Restore(() => Expected.Value = previous);
    }

    /// <summary>What a write expects the stored placement to be.</summary>
    /// <param name="Placement">The placement read before the write; null = the model was unplaced.</param>
    public sealed record ExpectedPlacement(ModelPlacement? Placement)
    {
        /// <summary>True when <paramref name="stored"/> is still the placement that was read.</summary>
        public bool Matches(ModelPlacement? stored) =>
            Placement is null
                ? stored is null
                : stored is not null && Placement.SameLocation(stored) && Placement.Generation == stored.Generation;
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        private Action? _restore = restore;

        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
