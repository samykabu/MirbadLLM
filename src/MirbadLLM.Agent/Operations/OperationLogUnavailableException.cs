using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Operations;

/// <summary>
/// The operation log could not be written (or loaded durably). Request handlers answer 503
/// <see cref="ErrorCodes.StateUnavailable"/> and run nothing (design §4.4 item 1); the log reports the
/// <see cref="HealthReasonCodes.StateUnavailable"/> health reason until a write succeeds again.
/// </summary>
public sealed class OperationLogUnavailableException : IOException
{
    /// <summary>Creates the exception.</summary>
    public OperationLogUnavailableException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public OperationLogUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the I/O failure behind it.</summary>
    public OperationLogUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The error code request handlers answer with.</summary>
    public static string ErrorCode => ErrorCodes.StateUnavailable;
}
