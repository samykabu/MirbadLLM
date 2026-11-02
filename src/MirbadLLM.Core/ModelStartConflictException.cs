namespace MirbadLLM.Core;

/// <summary>
/// Thrown by <see cref="IModelRuntime.StartAsync"/> and restarts when another model is still starting. vLLM
/// sizes its cache from how free memory changes while it loads; on the Spark's unified memory a second model
/// loading (or freeing) memory at the same time corrupts that measurement and both fail. Starts therefore run
/// one at a time.
/// </summary>
public sealed class ModelStartConflictException : Exception
{
    public ModelStartConflictException()
    {
    }

    public ModelStartConflictException(string message)
        : base(message)
    {
    }

    public ModelStartConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The model that is still starting.</summary>
    public string? StartingModelId { get; init; }
}
