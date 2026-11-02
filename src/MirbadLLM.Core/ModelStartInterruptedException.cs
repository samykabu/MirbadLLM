namespace MirbadLLM.Core;

/// <summary>
/// Thrown by the multi-node <see cref="IModelRuntime"/> when a model's start ended (cancelled, superseded or expired)
/// before its container existed: another action (a stop, a replacing start, a takeover) won, nothing runs and the
/// start can be asked for again. Derives from <see cref="InvalidOperationException"/> so callers that already handle
/// that keep working; the admin API answers 409 instead of 500 (W4 review MN-096, completing D51). Never thrown with
/// today's single-host wiring.
/// </summary>
public sealed class ModelStartInterruptedException : InvalidOperationException
{
    public ModelStartInterruptedException()
    {
    }

    public ModelStartInterruptedException(string message)
        : base(message)
    {
    }

    public ModelStartInterruptedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The model whose start ended.</summary>
    public string? ModelId { get; init; }

    /// <summary>The node it was to start on.</summary>
    public string? NodeId { get; init; }
}
