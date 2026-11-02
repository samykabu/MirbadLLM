namespace MirbadLLM.Core;

/// <summary>Where a managed service runs: a system unit, a user unit, or a Docker container.</summary>
public enum HostServiceKind
{
    System,
    User,
    Container,
}

/// <summary>A managed service's state, reduced to what the Services page shows.</summary>
public enum HostServiceState
{
    Running,
    Starting,
    Stopping,
    Stopped,
    Failed,
    Unknown,
}

public enum HostServiceAction
{
    Start,
    Stop,
    Restart,
}

/// <summary>
/// One host daemon or container on the Services page. Memory values are measured bytes (null = not
/// measured / not running); <see cref="Autostart"/> is null when it cannot be toggled (e.g. a static unit).
/// </summary>
public sealed record HostServiceStatus(
    string Name,
    string DisplayName,
    string Description,
    HostServiceKind Kind,
    string Unit,
    bool Protected,
    string? Risk,
    HostServiceState State,
    string StateDetail,
    bool? Autostart,
    string AutostartDetail,
    DateTimeOffset? Since,
    long? MemoryBytes,
    long? GpuMemoryBytes,
    string? Error = null)
{
    /// <summary>RAM + GPU, the memory that stopping it gives back (null when neither is known).</summary>
    public long? TotalMemoryBytes => MemoryBytes is null && GpuMemoryBytes is null ? null : (MemoryBytes ?? 0) + (GpuMemoryBytes ?? 0);
}

/// <summary>Everything the Services page lists. Containers are always present; systemd units need the host agent.</summary>
public sealed record HostServicesSnapshot(
    IReadOnlyList<HostServiceStatus> Services,
    bool HostAgentAvailable,
    string? HostAgentError);

/// <summary>
/// A service found by discovery (every systemd service unit of the system and the configured user, every
/// non-mirbadllm container), whether or not it is managed. <see cref="Name"/> is the name it has (or would get)
/// in the whitelist; the add dialog asks the operator to type it. Denied units are listed read-only with the
/// deny-list pattern that matched.
/// </summary>
public sealed record DiscoveredService(
    string Name,
    string Unit,
    HostServiceKind Kind,
    string Description,
    HostServiceState State,
    string StateDetail,
    string AutostartDetail,
    long? MemoryBytes,
    long? GpuMemoryBytes,
    bool Managed,
    bool Denied,
    string? DeniedBy)
{
    public long? TotalMemoryBytes => MemoryBytes is null && GpuMemoryBytes is null ? null : (MemoryBytes ?? 0) + (GpuMemoryBytes ?? 0);
}

/// <summary>Everything discovery found. Containers are always present; systemd units need the host agent.</summary>
public sealed record HostServicesDiscovery(
    IReadOnlyList<DiscoveredService> Services,
    bool HostAgentAvailable,
    string? HostAgentError,
    IReadOnlyList<string> Errors);

/// <summary>
/// Add a discovered service to the whitelist. <see cref="Confirm"/> must equal the service's
/// <see cref="DiscoveredService.Name"/>. The server-resolved <see cref="HostServiceWhitelistAdd.User"/> authorizes it;
/// <see cref="AdminPassword"/> is retained for compatibility and ignored.
/// </summary>
public sealed record HostServiceWhitelistAdd(
    HostServiceKind Kind,
    string Unit,
    string? DisplayName,
    bool Protected,
    string? Confirm,
    string? AdminPassword,
    string ClientKey)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public System.Security.Claims.ClaimsPrincipal? User { get; init; }
}

/// <summary>Rename or (un)protect a managed service. Unprotecting needs the typed name and a signed-in session.</summary>
public sealed record HostServiceWhitelistUpdate(
    string? DisplayName,
    bool? Protected,
    string? Confirm,
    string? AdminPassword,
    string ClientKey)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public System.Security.Claims.ClaimsPrincipal? User { get; init; }
}

public enum AdminReauthOutcome
{
    Verified,
    WrongPassword,
    RateLimited,
    LoginDisabled,
    LoginRequired,
}

/// <summary>Authorizes sensitive changes using the server-resolved portal session.</summary>
public interface IAdminReauthenticator
{
    /// <summary>False when no admin password is configured; whitelist changes are then refused.</summary>
    bool LoginEnabled { get; }

    /// <summary>Rejects missing, anonymous or legacy sessions. Never accepts a password in place of a session.</summary>
    AdminReauthOutcome Verify(System.Security.Claims.ClaimsPrincipal? user);
}

/// <summary>Lists and controls host daemons (through the host agent) and non-model containers (through Docker).</summary>
public interface IHostServiceManager
{
    Task<HostServicesSnapshot> ListAsync(CancellationToken ct = default);

    /// <summary>Every service unit and non-mirbadllm container, managed or not, with state and memory.</summary>
    Task<HostServicesDiscovery> DiscoverAsync(CancellationToken ct = default);

    /// <summary>Adds a discovered service to the whitelist (admin re-authentication + typed name).</summary>
    /// <exception cref="AdminReauthenticationException">Login disabled, wrong password or rate-limited.</exception>
    /// <exception cref="HostServiceDeniedException">The unit is on the host agent's deny list.</exception>
    /// <exception cref="HostServiceConfirmationRequiredException">The typed name does not match.</exception>
    Task<HostServiceStatus> AddToWhitelistAsync(HostServiceWhitelistAdd request, CancellationToken ct = default);

    /// <summary>Removes a service from the whitelist (it keeps running; it just can't be controlled here any more).</summary>
    Task RemoveFromWhitelistAsync(string name, CancellationToken ct = default);

    /// <summary>Renames or (un)protects a managed service.</summary>
    Task<HostServiceStatus> UpdateWhitelistAsync(string name, HostServiceWhitelistUpdate request, CancellationToken ct = default);

    /// <summary>
    /// Starts, stops or restarts a service. Stop and Restart of a protected service require
    /// <paramref name="confirm"/> to equal its name.
    /// </summary>
    /// <exception cref="HostServiceNotFoundException">Unknown name.</exception>
    /// <exception cref="HostServiceConfirmationRequiredException">Protected and not confirmed.</exception>
    /// <exception cref="HostServiceException">The host agent or Docker refused or failed.</exception>
    Task<HostServiceStatus> RunAsync(string name, HostServiceAction action, string? confirm = null, CancellationToken ct = default);

    /// <summary>Turns start-at-boot on or off. Turning a protected service's autostart off requires <paramref name="confirm"/>.</summary>
    Task<HostServiceStatus> SetAutostartAsync(string name, bool enabled, string? confirm = null, CancellationToken ct = default);
}

/// <summary>A managed service operation failed (host agent missing or returned an error, Docker error).</summary>
public class HostServiceException : Exception
{
    public HostServiceException()
    {
    }

    public HostServiceException(string message)
        : base(message)
    {
    }

    public HostServiceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class HostServiceNotFoundException : HostServiceException
{
    public HostServiceNotFoundException()
    {
    }

    public HostServiceNotFoundException(string message)
        : base(message)
    {
    }

    public HostServiceNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class HostServiceConfirmationRequiredException : HostServiceException
{
    public HostServiceConfirmationRequiredException()
    {
    }

    public HostServiceConfirmationRequiredException(string message)
        : base(message)
    {
    }

    public HostServiceConfirmationRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The host agent's deny list refused the unit (a system service that can never be managed here).</summary>
public sealed class HostServiceDeniedException : HostServiceException
{
    public HostServiceDeniedException()
    {
    }

    public HostServiceDeniedException(string message)
        : base(message)
    {
    }

    public HostServiceDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The service is already managed, or its name is taken.</summary>
public sealed class HostServiceConflictException : HostServiceException
{
    public HostServiceConflictException()
    {
    }

    public HostServiceConflictException(string message)
        : base(message)
    {
    }

    public HostServiceConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A whitelist change could not be authorized by the portal session.</summary>
public sealed class AdminReauthenticationException : HostServiceException
{
    public AdminReauthenticationException()
    {
    }

    public AdminReauthenticationException(string message)
        : base(message)
    {
    }

    public AdminReauthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AdminReauthenticationException(AdminReauthOutcome outcome, string message)
        : base(message)
    {
        Outcome = outcome;
    }

    public AdminReauthOutcome Outcome { get; }
}

/// <summary>
/// A container the Services page may manage. <c>MirbadLLM:ManagedContainers</c> only seeds the container
/// whitelist in <c>{DataPath}/host-services.json</c> on first use; after that the page manages it.
/// </summary>
public sealed class ManagedContainerOptions
{
    public string Name { get; set; } = "";

    public string? DisplayName { get; set; }

    public string? Description { get; set; }

    public bool Protected { get; set; }

    /// <summary>What goes wrong when it stops; shown in the confirm dialog of a protected container.</summary>
    public string? Risk { get; set; }
}

/// <summary>Bound from the <c>MirbadLLM</c> section: the host agent socket and the managed containers.</summary>
public sealed class HostServicesOptions
{
    public const string DefaultHostAgentSocket = "/run/mirbadllm-hostd/hostd.sock";

    public string HostAgentSocket { get; set; } = DefaultHostAgentSocket;

    /// <summary>
    /// Seed for the container whitelist (empty = <see cref="DefaultContainers"/>), read once when
    /// <c>host-services.json</c> has no container list yet. mirbadllm itself, model and download containers are never managed.
    /// </summary>
    public List<ManagedContainerOptions> ManagedContainers { get; set; } = [];

    public IReadOnlyList<ManagedContainerOptions> EffectiveContainers => ManagedContainers.Count > 0 ? ManagedContainers : DefaultContainers;

    public static IReadOnlyList<ManagedContainerOptions> DefaultContainers { get; } =
    [
        new()
        {
            Name = "openviking",
            DisplayName = "OpenViking",
            Description = "Context database and memory server for the coding agents.",
            Protected = true,
            Risk = "OpenViking holds the agents' memory and context. Stopping it breaks memory recall for every connected agent until it runs again.",
        },
        new()
        {
            Name = "n8n-compose-n8n-1",
            DisplayName = "n8n",
            Description = "Workflow automation.",
        },
        new()
        {
            Name = "sharp_wing",
            DisplayName = "Pangolin tunnel (newt)",
            Description = "The fosrl/newt client that connects this box to Pangolin.",
            Protected = true,
            Risk = "sharp_wing is the Pangolin tunnel; stopping it can cut remote access to this box, including to this page.",
        },
        new()
        {
            Name = "ss-vllm",
            DisplayName = "Legacy vLLM (ss-vllm)",
            Description = "The hand-made vLLM container from before mirbadllm managed models.",
        },
    ];
}
