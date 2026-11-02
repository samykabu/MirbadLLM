namespace MirbadLLM.Core;

/// <summary>
/// One change the model registry is about to write (P2 extension point, frozen by MN-101, D73): a create
/// (<see cref="Stored"/> null), an update, or a delete (<see cref="Proposed"/> null).
/// </summary>
/// <param name="Stored">The definition stored now, read under the registry lock; null for a create.</param>
/// <param name="Proposed">The normalized, built-in-validated definition to write; null for a delete.</param>
public sealed record ModelDefinitionChange(ModelDefinition? Stored, ModelDefinition? Proposed)
{
    /// <summary>The model id.</summary>
    public string ModelId => Proposed?.Id ?? Stored?.Id ?? "";

    /// <summary>True for a delete.</summary>
    public bool IsDelete => Proposed is null;

    /// <summary>True when the node or the ordered GPU UUIDs change (or the model is placed/unplaced).</summary>
    public bool PlacementChanges =>
        Proposed is not null && Stored is not null &&
        !(Proposed.Placement is null ? Stored.Placement is null : Proposed.Placement.SameLocation(Stored.Placement));
}

/// <summary>
/// A validator the model registry runs on every write, after its own checks, <b>under its lock</b> with the stored
/// definition (design §5.1 validation; P2 extension point of <c>JsonModelRegistry</c>, frozen by MN-101, D73). MN-108
/// registers the placement validator (node exists and is not revoked, GPU UUIDs exist and are unique, equal-capacity TP,
/// runtime available, placement generation) and the ownership check (409 <c>model_busy</c>) through it, so no Wave 7
/// task edits the registry file. No validator is registered by default: P1 behaviour is unchanged.
/// <para>Rules: a validator may read other stores (node registry, ownership store, capabilities) but must never call
/// <see cref="IModelRegistry"/> (the lock is held); it refuses by throwing (<see cref="PlacementValidationException"/> or
/// another <see cref="ArgumentException"/> → 400, <see cref="ModelBusyException"/> → 409, a
/// <see cref="PlacementConflictException"/> → 409 <c>placement_changed</c>); validators run in registration order, each
/// receiving the previous one's result.</para>
/// </summary>
public interface IModelDefinitionValidator
{
    /// <summary>
    /// Checks <paramref name="change"/> and returns the definition to write: <see cref="ModelDefinitionChange.Proposed"/>
    /// unchanged, or a copy with server-owned fields stamped (the placement generation). For a delete the return value is
    /// ignored (return null).
    /// </summary>
    Task<ModelDefinition?> ValidateAsync(ModelDefinitionChange change, CancellationToken ct = default);
}

/// <summary>
/// A save carried a stale placement (its generation is older than the stored one): a move, unplace or placement edit landed
/// between the caller's read and its write. The admin API answers 409 <c>placement_changed</c> (MN-108). An
/// <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class PlacementConflictException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public PlacementConflictException()
        : this("The model's placement changed meanwhile; reload it and try again.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public PlacementConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public PlacementConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>What the model registry writes when <c>models.json</c> does not exist (design §9.4 recovery mode).</summary>
public enum ModelSeedPolicy
{
    /// <summary>Today's behaviour: write the seed model (<c>ornith-9b-fp8</c>) placed on the bootstrap node.</summary>
    Seed,

    /// <summary>Write nothing: a missing file reads as an empty registry and is created on the first save (recovery mode, fleet DR).</summary>
    None,
}

/// <summary>
/// Options of <c>JsonModelRegistry</c> beyond the v1 migration (P2 extension point, frozen by MN-101, D73). Not bound from
/// configuration: the composition (MN-130) sets <see cref="SeedPolicy"/> to <see cref="ModelSeedPolicy.None"/> when the
/// master starts in recovery mode (<see cref="RecoveryOptions.IsActive"/>), which MN-113's fleet recovery relies on.
/// </summary>
public sealed class ModelRegistryOptions
{
    /// <summary>Seeding on a missing file. Default: <see cref="ModelSeedPolicy.Seed"/> (P1 behaviour).</summary>
    public ModelSeedPolicy SeedPolicy { get; set; } = ModelSeedPolicy.Seed;
}
