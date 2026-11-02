namespace MirbadLLM.NodeRuntime.Downloads.Native;

/// <summary>
/// A native download failed (design §6.4). Messages are built by this code only: they name files, hosts and HTTP statuses,
/// never a token or a header value. Deliberately not an <see cref="IOException"/>, <see cref="ArgumentException"/> or
/// <see cref="InvalidOperationException"/>, so a caller's catch for those (Docker, no-match, already running) never
/// misreads it.
/// </summary>
public class NativeDownloadException : Exception
{
    /// <summary>Creates the exception.</summary>
    public NativeDownloadException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public NativeDownloadException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    public NativeDownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The volume ran out of space while writing (design §6.4 "Disk": the download fails as resumable, its files and manifest
/// are kept).
/// </summary>
public sealed class DownloadDiskFullException : NativeDownloadException
{
    /// <summary>Creates the exception.</summary>
    public DownloadDiskFullException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public DownloadDiskFullException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the I/O error.</summary>
    public DownloadDiskFullException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A file failed its integrity check again after the one retry (design §6.4 "Integrity").</summary>
public sealed class DownloadIntegrityException : NativeDownloadException
{
    /// <summary>Creates the exception.</summary>
    public DownloadIntegrityException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public DownloadIntegrityException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    public DownloadIntegrityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The server refused the request for good (401/403/404/410, a non-HTTPS redirect, too many redirects, a protocol
/// violation): retrying does not help.
/// </summary>
public sealed class DownloadRefusedException : NativeDownloadException
{
    /// <summary>Creates the exception.</summary>
    public DownloadRefusedException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public DownloadRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    public DownloadRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for an HTTP status.</summary>
    public DownloadRefusedException(string message, int statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>The HTTP status, when the refusal came with one.</summary>
    public int? StatusCode { get; }

    /// <summary>True for 401/403: the token is missing, wrong or has no access to the repo.</summary>
    public bool IsAuthorization => StatusCode is 401 or 403;
}

/// <summary>A transient failure (5xx, 429, a body cut short, an idle body): the file is resumed by range on the next attempt.</summary>
public sealed class DownloadTransientException : NativeDownloadException
{
    /// <summary>Creates the exception.</summary>
    public DownloadTransientException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public DownloadTransientException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    public DownloadTransientException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A repo file name or a path under the download folder breaks the confinement rules (design §6.4 "Path confinement",
/// design-p3p4 §A.2.3).
/// </summary>
public sealed class DownloadPathException : NativeDownloadException
{
    /// <summary>Creates the exception.</summary>
    public DownloadPathException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public DownloadPathException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    public DownloadPathException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The final folder appeared while the download ran, so the atomic rename cannot happen (design §6.4 "Finalize").</summary>
public sealed class DownloadDestinationExistsException : NativeDownloadException
{
    /// <summary>Creates the exception.</summary>
    public DownloadDestinationExistsException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public DownloadDestinationExistsException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    public DownloadDestinationExistsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
