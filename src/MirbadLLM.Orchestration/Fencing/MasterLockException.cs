namespace MirbadLLM.Orchestration.Fencing;

/// <summary>
/// Thrown at startup when another master process already holds <c>{DataPath}/master.lock</c> (design §4.2 layer 1).
/// The message says what to do; the process must not continue.
/// </summary>
public sealed class MasterLockException : InvalidOperationException
{
    /// <summary>Creates the exception with a default message.</summary>
    public MasterLockException()
        : base("Another mirbadllm master already holds the data directory lock.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public MasterLockException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the I/O error that caused it.</summary>
    public MasterLockException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a held lock file.</summary>
    /// <param name="lockPath">The lock file that could not be opened exclusively.</param>
    /// <param name="innerException">The I/O error from the exclusive open.</param>
    public MasterLockException(string lockPath, IOException innerException)
        : base(
            $"Another mirbadllm master is already using this data directory: '{lockPath}' is locked by another " +
            "process (or could not be opened exclusively; see the inner error). Only one master may run per data " +
            "directory. Stop the other master and start this one again (in Kubernetes: one replica, Deployment strategy " +
            "Recreate).",
            innerException)
    {
        LockPath = lockPath;
    }

    /// <summary>The lock file that is held by another process, when known.</summary>
    public string? LockPath { get; }
}
