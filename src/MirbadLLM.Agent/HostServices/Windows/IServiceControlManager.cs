namespace MirbadLLM.Agent.HostServices.Windows;

/// <summary>A Windows service's current state (<c>SERVICE_STATUS_PROCESS.dwCurrentState</c>).</summary>
public enum ScmServiceState
{
    /// <summary>The SCM reported a value this agent does not know.</summary>
    Unknown = 0,

    /// <summary><c>SERVICE_STOPPED</c>.</summary>
    Stopped = 1,

    /// <summary><c>SERVICE_START_PENDING</c>.</summary>
    StartPending = 2,

    /// <summary><c>SERVICE_STOP_PENDING</c>.</summary>
    StopPending = 3,

    /// <summary><c>SERVICE_RUNNING</c>.</summary>
    Running = 4,

    /// <summary><c>SERVICE_CONTINUE_PENDING</c>.</summary>
    ContinuePending = 5,

    /// <summary><c>SERVICE_PAUSE_PENDING</c>.</summary>
    PausePending = 6,

    /// <summary><c>SERVICE_PAUSED</c>.</summary>
    Paused = 7,
}

/// <summary>A Windows service's start type (<c>QUERY_SERVICE_CONFIG.dwStartType</c>, plus the delayed flag).</summary>
public enum ScmStartType
{
    /// <summary><c>SERVICE_BOOT_START</c> (drivers only).</summary>
    Boot = 0,

    /// <summary><c>SERVICE_SYSTEM_START</c> (drivers only).</summary>
    System = 1,

    /// <summary><c>SERVICE_AUTO_START</c>.</summary>
    Automatic = 2,

    /// <summary><c>SERVICE_DEMAND_START</c>.</summary>
    Manual = 3,

    /// <summary><c>SERVICE_DISABLED</c>.</summary>
    Disabled = 4,

    /// <summary><c>SERVICE_AUTO_START</c> with <c>SERVICE_CONFIG_DELAYED_AUTO_START_INFO</c> set.</summary>
    AutomaticDelayed = 5,
}

/// <summary>The rights this process holds on one service (found by opening it, which changes nothing).</summary>
[Flags]
public enum ScmRights
{
    /// <summary>None of the rights below.</summary>
    None = 0,

    /// <summary><c>SERVICE_START</c>.</summary>
    Start = 1,

    /// <summary><c>SERVICE_STOP</c>.</summary>
    Stop = 2,

    /// <summary><c>SERVICE_CHANGE_CONFIG</c> (needed to change the start type).</summary>
    ChangeConfig = 4,
}

/// <summary>One Win32 service as the SCM reports it.</summary>
/// <param name="Name">The service key name (case-insensitive in Windows).</param>
/// <param name="DisplayName">Display name.</param>
/// <param name="State">Current state.</param>
/// <param name="ProcessId">The service process id; null when stopped.</param>
/// <param name="OwnProcess">True for <c>SERVICE_WIN32_OWN_PROCESS</c> (the process is the service's alone).</param>
/// <param name="StartType">Start type; null when the configuration could not be read.</param>
/// <param name="Description">Description; empty when there is none or it could not be read.</param>
public sealed record ScmService(
    string Name,
    string DisplayName,
    ScmServiceState State,
    int? ProcessId,
    bool OwnProcess,
    ScmStartType? StartType,
    string Description);

/// <summary>A failed SCM call, with its Win32 error code.</summary>
public sealed class ScmException : Exception
{
    /// <summary><c>ERROR_ACCESS_DENIED</c>.</summary>
    public const int AccessDenied = 5;

    /// <summary><c>ERROR_DEPENDENT_SERVICES_RUNNING</c>.</summary>
    public const int DependentServicesRunning = 1051;

    /// <summary><c>ERROR_INVALID_SERVICE_CONTROL</c> (the service does not accept the control now).</summary>
    public const int InvalidServiceControl = 1052;

    /// <summary><c>ERROR_SERVICE_REQUEST_TIMEOUT</c>.</summary>
    public const int ServiceRequestTimeout = 1053;

    /// <summary><c>ERROR_SERVICE_ALREADY_RUNNING</c>.</summary>
    public const int ServiceAlreadyRunning = 1056;

    /// <summary><c>ERROR_SERVICE_DISABLED</c>.</summary>
    public const int ServiceDisabled = 1058;

    /// <summary><c>ERROR_SERVICE_DOES_NOT_EXIST</c>.</summary>
    public const int ServiceDoesNotExist = 1060;

    /// <summary><c>ERROR_SERVICE_CANNOT_ACCEPT_CTRL</c>.</summary>
    public const int ServiceCannotAcceptControl = 1061;

    /// <summary><c>ERROR_SERVICE_NOT_ACTIVE</c>.</summary>
    public const int ServiceNotActive = 1062;

    /// <summary><c>ERROR_SERVICE_MARKED_FOR_DELETE</c>.</summary>
    public const int ServiceMarkedForDelete = 1072;

    /// <summary>Creates the exception.</summary>
    public ScmException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public ScmException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public ScmException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with the Win32 error code.</summary>
    public ScmException(int error, string message)
        : base(message)
    {
        Error = error;
    }

    /// <summary>The Win32 error code (0 when not a Win32 failure).</summary>
    public int Error { get; }
}

/// <summary>
/// The Service Control Manager calls the SCM host services need (MN-228, design-p3p4 §A.4.3). The real one is
/// <see cref="WindowsServiceControlManager"/> (<c>advapi32</c>); tests use a fake, so no test ever starts, stops or
/// reconfigures a service of the machine it runs on. Every call is synchronous and returns as soon as the SCM answers:
/// waiting for a state is the caller's job (<see cref="ScmHostClient"/>). Failures throw <see cref="ScmException"/>.
/// </summary>
public interface IServiceControlManager
{
    /// <summary>True when the SCM can be opened for enumeration.</summary>
    bool IsAvailable { get; }

    /// <summary>Every Win32 service (no drivers) with its state, process, start type and description.</summary>
    IReadOnlyList<ScmService> List();

    /// <summary>One service by key name (case-insensitive); null when there is no such Win32 service.</summary>
    ScmService? Find(string serviceName);

    /// <summary>The rights this process holds on the service (read-only: it opens the service and closes it again).</summary>
    ScmRights RightsOn(string serviceName);

    /// <summary><c>StartService</c>.</summary>
    void StartService(string serviceName);

    /// <summary><c>ControlService(SERVICE_CONTROL_STOP)</c>.</summary>
    void StopService(string serviceName);

    /// <summary><c>ChangeServiceConfig</c> (start type) and the delayed-auto-start flag.</summary>
    void SetStartType(string serviceName, ScmStartType startType);

    /// <summary>The working set of a process in bytes; null when it cannot be read.</summary>
    long? WorkingSetBytes(int processId);

    /// <summary>When a process started; null when it cannot be read.</summary>
    DateTimeOffset? ProcessStartedAt(int processId);
}
