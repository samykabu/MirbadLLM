namespace MirbadLLM.Core;

/// <summary>
/// Thrown by <see cref="IModelRuntime.StartAsync"/> when a model's memory budget exceeds the available
/// memory minus the configured reserve and the caller did not pass <c>force</c>.
/// </summary>
public sealed class ModelMemoryAdmissionException : Exception
{
    public ModelMemoryAdmissionException()
    {
    }

    public ModelMemoryAdmissionException(string message)
        : base(message)
    {
    }

    public ModelMemoryAdmissionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
