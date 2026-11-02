namespace MirbadLLM.Agent.Enrollment;

/// <summary>Why an enrollment attempt ended without credentials. Stable values: the <c>join</c> command maps them to messages.</summary>
public enum EnrollmentFailure
{
    /// <summary>The parameters are unusable (node id, URL, CA hash, empty token).</summary>
    InvalidArguments,

    /// <summary>The state directory already holds an identity and <c>--force</c> was not given.</summary>
    AlreadyEnrolled,

    /// <summary>
    /// The enroll listener's certificate was refused (its CA's SPKI hash is not the expected one, wrong SAN, expired, …).
    /// The token was <b>not</b> sent: the TLS handshake failed before any request byte.
    /// </summary>
    ServerNotTrusted,

    /// <summary>The master could not be reached (connect, DNS, timeout, connection lost).</summary>
    Unreachable,

    /// <summary>403 <c>enrollment_refused</c>: token unknown, expired, used or bound to another node, or the CSR was refused.</summary>
    Refused,

    /// <summary>429 <c>rate_limited</c>.</summary>
    RateLimited,

    /// <summary>400 <c>invalid_request</c> or another unexpected HTTP status.</summary>
    Rejected,

    /// <summary>The master answered 200 but the answer fails the agent's checks (CA hash, certificate, uid); nothing was stored.</summary>
    InvalidResponse,

    /// <summary>The bootstrap bundle is unreadable or inconsistent (its CA does not match its hash, wrong node id, …).</summary>
    BundleInvalid,
}

/// <summary>An enrollment attempt that failed. Messages never contain the token or key material.</summary>
public sealed class AgentEnrollmentException : Exception
{
    /// <summary>Creates the exception.</summary>
    public AgentEnrollmentException(EnrollmentFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    /// <summary>Unused; present for the standard exception constructors.</summary>
    public AgentEnrollmentException()
        : this(EnrollmentFailure.Rejected, "Enrollment failed.")
    {
    }

    /// <summary>Unused; present for the standard exception constructors.</summary>
    public AgentEnrollmentException(string message)
        : this(EnrollmentFailure.Rejected, message)
    {
    }

    /// <summary>Unused; present for the standard exception constructors.</summary>
    public AgentEnrollmentException(string message, Exception innerException)
        : this(EnrollmentFailure.Rejected, message, innerException)
    {
    }

    /// <summary>Why it failed.</summary>
    public EnrollmentFailure Failure { get; }
}
