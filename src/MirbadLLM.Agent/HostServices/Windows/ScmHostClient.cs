using System.Globalization;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;

namespace MirbadLLM.Agent.HostServices.Windows;

/// <summary>
/// The agent's <c>/host/services</c> backend on Windows (MN-228, design §9.1, design-p3p4 §A.4.3): the
/// <see cref="IHostdClient"/> contract served by the Service Control Manager instead of hostd, so the host-service
/// operations (<see cref="HostServiceOperations"/>: durable accept, the <c>dispatched</c> stage, Unknown outcomes),
/// their recovery (<see cref="HostServiceRecoveryHandler"/>: desired-state actions reconciled, restarts after a crash end
/// Unknown, D42) and the read routes (<see cref="HostServiceQueries"/>) run unchanged.
/// <para>What hostd does on Linux this class does here: the whitelist (<see cref="ScmWhitelist"/>, empty by default),
/// the compiled deny list (<see cref="ScmDenyList"/>), checked before every action and every whitelist add, also for a
/// whitelisted name; the agent's own service, refused by name and by process id; the typed confirmations of protected
/// services (stop, restart, autostart off, unprotect, whitelist add). Windows services map onto <see cref="HostUnit"/> /
/// <see cref="HostDiscoveredUnit"/> exactly as <see cref="HostServiceManagers"/> documents.</para>
/// <para>Rights: the agent runs as <c>NT SERVICE\mirbadllm-agent</c> without admin rights; <c>install-agent.ps1
/// -AllowService &lt;name&gt;</c> grants it start/stop on that one service (<c>sc sdset</c>), and only
/// <c>-AllowServiceAutostart</c> adds the right to change its start type. A missing right is reported on the unit
/// (<see cref="HostUnit.Error"/>) and refuses the action with 403 <c>denied</c> before anything is sent.</para>
/// <para>Not offered: GPU processes (<c>gpu-processes</c> comes from the Windows GPU process probe) and the backup mount.</para>
/// </summary>
public sealed class ScmHostClient : IHostdClient
{
    /// <summary><see cref="HostDiscoveredUnit.DeniedBy"/> for the service the agent runs in.</summary>
    public const string OwnServiceMarker = "(the agent's own service)";

    private readonly IServiceControlManager _scm;
    private readonly ScmWhitelist _whitelist;
    private readonly ScmDenyList _deny;
    private readonly ScmHostServicesOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly int _ownProcessId;

    /// <summary>Creates the client. <paramref name="ownProcessId"/> defaults to this process (tests pass another).</summary>
    public ScmHostClient(
        IServiceControlManager scm,
        ScmWhitelist whitelist,
        ScmDenyList deny,
        ScmHostServicesOptions options,
        TimeProvider time,
        ILogger<ScmHostClient> logger,
        int? ownProcessId = null)
    {
        ArgumentNullException.ThrowIfNull(scm);
        ArgumentNullException.ThrowIfNull(whitelist);
        ArgumentNullException.ThrowIfNull(deny);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _scm = scm;
        _whitelist = whitelist;
        _deny = deny;
        _options = options;
        _time = time;
        _logger = logger;
        _ownProcessId = ownProcessId ?? Environment.ProcessId;
    }

    /// <summary>True when the SCM can be opened.</summary>
    public bool IsInstalled
    {
        get
        {
            try
            {
                return _scm.IsAvailable;
            }
            catch (ScmException)
            {
                return false;
            }
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<HostUnit>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<HostUnit>>(List());

    /// <inheritdoc/>
    public Task<HostUnitDiscovery> DiscoverAsync(CancellationToken ct = default)
    {
        var managed = List();
        var entries = _whitelist.Read();
        var errors = new List<string>();
        var discovered = new List<HostDiscoveredUnit>();
        try
        {
            foreach (var service in _scm.List().OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                var entry = entries.FirstOrDefault(e => string.Equals(e.Service, service.Name, StringComparison.OrdinalIgnoreCase));
                string? deniedBy = DeniedBy(service.Name, service);
                bool active = IsActive(service.State);
                discovered.Add(new HostDiscoveredUnit
                {
                    Name = entry?.Name ?? ScmWhitelist.DeriveName(service.Name),
                    Unit = service.Name,
                    Scope = HostServiceManagers.ScmScope,
                    Description = service.Description,
                    ActiveState = ActiveStateOf(service.State),
                    SubState = SubStateOf(service.State),
                    UnitFileState = UnitFileStateOf(service.StartType),
                    ActiveSinceUnix = active ? StartedAt(service) : null,
                    MemoryBytes = active ? MemoryOf(service) : null,
                    Managed = entry is not null && deniedBy is null,
                    Denied = deniedBy is not null,
                    DeniedBy = deniedBy,
                });
            }
        }
        catch (ScmException ex)
        {
            errors.Add($"Could not list Windows services: {ex.Message}");
        }

        return Task.FromResult(new HostUnitDiscovery
        {
            Services = managed,
            Discovered = discovered,
            DiscoveryErrors = errors,
            DenyPatterns = _deny.Patterns,
        });
    }

    /// <inheritdoc/>
    public Task<HostUnit> AddToWhitelistAsync(string unit, string scope, string? displayName, bool isProtected, string? confirm, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        string service = unit.Trim();
        if (DeniedBy(service, null) is { } pattern)
        {
            ScmHostServicesLog.Refused(_logger, "whitelist", service, service, pattern);
            throw new HostServiceDeniedException(DeniedMessage(service, pattern));
        }

        if (!ScmWhitelist.IsValidService(service))
        {
            throw new HostServiceNotFoundException($"'{service}' is not a valid Windows service name.");
        }

        if (!string.Equals(scope, HostServiceManagers.ScmScope, StringComparison.Ordinal))
        {
            throw new HostServiceNotFoundException($"Windows services have no '{scope}' scope; use '{HostServiceManagers.ScmScope}'.");
        }

        string name = _whitelist.ProposeName(service);
        if (!string.Equals(confirm?.Trim(), name, StringComparison.Ordinal))
        {
            throw new HostServiceConfirmationRequiredException($"Confirm with the service name '{name}' to manage it.");
        }

        var found = FindService(service) ?? throw new HostServiceNotFoundException($"{service} is not a Windows service on this PC.");
        if (DeniedBy(found.Name, found) is { } own)
        {
            // The canonical name or the process (the agent's own service under another name) is refused too.
            ScmHostServicesLog.Refused(_logger, "whitelist", found.Name, name, own);
            throw new HostServiceDeniedException(DeniedMessage(found.Name, own));
        }

        var entry = _whitelist.Add(found.Name, string.IsNullOrWhiteSpace(displayName) ? found.DisplayName : displayName, isProtected);
        return Task.FromResult(ToUnit(entry));
    }

    /// <inheritdoc/>
    public Task<HostUnit> UpdateWhitelistAsync(string name, string? displayName, bool? isProtected, string? confirm, CancellationToken ct = default)
    {
        var entry = Managed(name, "update");
        var updated = _whitelist.Update(entry.Name, displayName, isProtected, confirm?.Trim());
        return Task.FromResult(ToUnit(updated));
    }

    /// <inheritdoc/>
    public Task RemoveFromWhitelistAsync(string name, CancellationToken ct = default)
    {
        // Allowed for every entry, a denied one included: removing never touches the service.
        _whitelist.Remove(name);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<HostUnit> RunAsync(string name, HostServiceAction action, string? confirm, CancellationToken ct = default)
    {
        string verb = action switch
        {
            HostServiceAction.Start => "start",
            HostServiceAction.Stop => "stop",
            _ => "restart",
        };
        var entry = Managed(name, verb);
        if (entry.Protected && action != HostServiceAction.Start && !string.Equals(confirm?.Trim(), entry.Name, StringComparison.Ordinal))
        {
            ScmHostServicesLog.Refused(_logger, verb, entry.Service, entry.Name, "protected, not confirmed");
            throw new HostServiceConfirmationRequiredException($"'{entry.Name}' is protected: confirm with its name to {verb} it.");
        }

        var current = RequireService(entry);
        RequireRights(entry, current, action == HostServiceAction.Start ? ScmRights.Start
            : action == HostServiceAction.Stop ? ScmRights.Stop : ScmRights.Start | ScmRights.Stop, verb);

        if ((action is HostServiceAction.Stop or HostServiceAction.Restart) && current.State != ScmServiceState.Stopped)
        {
            Act(entry, verb, () => _scm.StopService(entry.Service), ScmException.ServiceNotActive);
            current = await WaitForAsync(entry, verb, ScmServiceState.Stopped, ct).ConfigureAwait(false);
        }

        if (action is HostServiceAction.Start or HostServiceAction.Restart)
        {
            Act(entry, verb, () => _scm.StartService(entry.Service), ScmException.ServiceAlreadyRunning);
            current = await WaitForAsync(entry, verb, ScmServiceState.Running, ct).ConfigureAwait(false);
        }

        string state = SubStateOf(current.State);
        ScmHostServicesLog.ActionDone(_logger, entry.Service, entry.Name, verb, state);
        return ToUnit(entry, current);
    }

    /// <inheritdoc/>
    public Task<HostUnit> SetAutostartAsync(string name, bool enabled, string? confirm, CancellationToken ct = default)
    {
        string verb = enabled ? "turn autostart on for" : "turn autostart off for";
        var entry = Managed(name, verb);
        if (entry.Protected && !enabled && !string.Equals(confirm?.Trim(), entry.Name, StringComparison.Ordinal))
        {
            ScmHostServicesLog.Refused(_logger, verb, entry.Service, entry.Name, "protected, not confirmed");
            throw new HostServiceConfirmationRequiredException($"'{entry.Name}' is protected: confirm with its name to disable it at boot.");
        }

        var current = RequireService(entry);
        ScmStartType? target = enabled
            ? current.StartType is ScmStartType.Automatic or ScmStartType.AutomaticDelayed ? null : ScmStartType.Automatic
            : current.StartType is ScmStartType.Manual or ScmStartType.Disabled ? null : ScmStartType.Manual;
        if (target is { } startType)
        {
            RequireRights(entry, current, ScmRights.ChangeConfig, verb);
            Act(entry, verb, () => _scm.SetStartType(entry.Service, startType), alreadyDone: null);
            current = RequireService(entry);
        }

        string autostart = UnitFileStateOf(current.StartType);
        ScmHostServicesLog.ActionDone(_logger, entry.Service, entry.Name, verb, autostart);
        return Task.FromResult(ToUnit(entry, current));
    }

    /// <summary>Not on Windows through this client (the Windows GPU process probe reports them): null.</summary>
    public Task<IReadOnlyList<GpuProcess>?> GetGpuProcessesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GpuProcess>?>(null);

    /// <summary>No backup mount on Windows (the route is not offered).</summary>
    public Task<BackupMountInfo> GetBackupMountAsync(CancellationToken ct = default) => throw NoBackupMount();

    /// <summary>No backup mount on Windows (the route is not offered).</summary>
    public Task<BackupMountTestResult> TestBackupMountAsync(BackupMountSettings settings, CancellationToken ct = default) => throw NoBackupMount();

    /// <summary>No backup mount on Windows (the route is not offered).</summary>
    public Task<BackupMountInfo> SetBackupMountAsync(BackupMountSettings settings, CancellationToken ct = default) => throw NoBackupMount();

    /// <summary>No backup mount on Windows (the route is not offered).</summary>
    public Task<BackupMountInfo> RemoveBackupMountAsync(CancellationToken ct = default) => throw NoBackupMount();

    // ---------------------------------------------------------------- mapping (HostServiceManagers, frozen by MN-202)

    /// <summary><c>ActiveState</c> of an SCM state.</summary>
    public static string ActiveStateOf(ScmServiceState state) => state switch
    {
        ScmServiceState.Running => "active",
        ScmServiceState.Stopped or ScmServiceState.Paused => "inactive",
        ScmServiceState.StartPending or ScmServiceState.ContinuePending => "activating",
        ScmServiceState.StopPending or ScmServiceState.PausePending => "deactivating",
        _ => "unknown",
    };

    /// <summary><c>SubState</c>: the SCM state in snake case.</summary>
    public static string SubStateOf(ScmServiceState state) => state switch
    {
        ScmServiceState.Running => "running",
        ScmServiceState.Stopped => "stopped",
        ScmServiceState.StartPending => "start_pending",
        ScmServiceState.StopPending => "stop_pending",
        ScmServiceState.ContinuePending => "continue_pending",
        ScmServiceState.PausePending => "pause_pending",
        ScmServiceState.Paused => "paused",
        _ => "",
    };

    /// <summary><c>UnitFileState</c> of a start type (empty when it could not be read).</summary>
    public static string UnitFileStateOf(ScmStartType? startType) => startType switch
    {
        ScmStartType.Automatic or ScmStartType.AutomaticDelayed or ScmStartType.Boot or ScmStartType.System => "enabled",
        ScmStartType.Manual => "disabled",
        ScmStartType.Disabled => "masked",
        _ => "",
    };

    // ---------------------------------------------------------------- helpers

    private List<HostUnit> List()
    {
        var units = new List<HostUnit>();
        foreach (var entry in _whitelist.Read())
        {
            ScmService? service = null;
            string? error = null;
            try
            {
                service = FindService(entry.Service);
            }
            catch (ScmException ex)
            {
                error = $"Could not read the service ({ex.Message}).";
            }

            if (DeniedBy(entry.Service, service) is { } pattern)
            {
                // Never managed, whatever the file says (hostd drops such entries the same way).
                ScmHostServicesLog.WhitelistedButDenied(_logger, entry.Service, entry.Name, pattern);
                continue;
            }

            units.Add(service is null
                ? Unknown(entry, error ?? $"{entry.Service} is not a Windows service on this PC (removed or renamed).")
                : ToUnit(entry, service));
        }

        return units;
    }

    private HostUnit ToUnit(ScmWhitelistEntry entry)
    {
        try
        {
            return FindService(entry.Service) is { } service ? ToUnit(entry, service) : Unknown(entry, $"{entry.Service} is not a Windows service on this PC.");
        }
        catch (ScmException ex)
        {
            return Unknown(entry, $"Could not read the service ({ex.Message}).");
        }
    }

    private HostUnit ToUnit(ScmWhitelistEntry entry, ScmService service)
    {
        bool active = IsActive(service.State);
        return new HostUnit
        {
            Name = entry.Name,
            Unit = service.Name,
            Scope = HostServiceManagers.ScmScope,
            User = null,
            DisplayName = entry.DisplayName,
            Description = service.Description,
            Protected = entry.Protected,
            Risk = entry.Risk,
            ActiveState = ActiveStateOf(service.State),
            SubState = SubStateOf(service.State),
            UnitFileState = UnitFileStateOf(service.StartType),
            MainPid = service.ProcessId is > 0 ? service.ProcessId : null,
            ActiveSinceUnix = active ? StartedAt(service) : null,
            MemoryBytes = active ? MemoryOf(service) : null,
            GpuMemoryBytes = null,
            Error = MissingRightsError(service),
        };
    }

    private static HostUnit Unknown(ScmWhitelistEntry entry, string error) => new()
    {
        Name = entry.Name,
        Unit = entry.Service,
        Scope = HostServiceManagers.ScmScope,
        DisplayName = entry.DisplayName,
        Protected = entry.Protected,
        Risk = entry.Risk,
        ActiveState = "unknown",
        Error = error,
    };

    /// <summary>The whitelist entry for an action, after the deny-list and own-service checks.</summary>
    private ScmWhitelistEntry Managed(string name, string verb)
    {
        var entry = _whitelist.Find(name) ?? throw new HostServiceNotFoundException($"'{name}' is not in the whitelist.");
        ScmService? service = null;
        try
        {
            service = FindService(entry.Service);
        }
        catch (ScmException)
        {
            // The name check below still runs; the action itself reports the SCM failure.
        }

        if ((DeniedBy(entry.Service, service) ?? _deny.Match(entry.Name)) is { } pattern)
        {
            ScmHostServicesLog.Refused(_logger, verb, entry.Service, entry.Name, pattern);
            throw new HostServiceDeniedException(DeniedMessage(entry.Service, pattern));
        }

        return entry;
    }

    /// <summary>The deny pattern, or <see cref="OwnServiceMarker"/>, that refuses a service; null when it may be managed.</summary>
    private string? DeniedBy(string serviceName, ScmService? service)
    {
        if (_deny.Match(serviceName) is { } pattern)
        {
            return pattern;
        }

        if (string.Equals(serviceName, _options.AgentServiceName, StringComparison.OrdinalIgnoreCase)
            || (service?.ProcessId is { } pid && pid == _ownProcessId))
        {
            return OwnServiceMarker;
        }

        return null;
    }

    private static string DeniedMessage(string service, string pattern) =>
        pattern == OwnServiceMarker
            ? $"{service} is the agent's own service and cannot be managed through the agent."
            : $"{service} is a system service (matches '{pattern}') and cannot be managed.";

    private ScmService? FindService(string service) => _scm.Find(service);

    private ScmService RequireService(ScmWhitelistEntry entry)
    {
        try
        {
            return FindService(entry.Service) ?? throw new HostServiceNotFoundException($"{entry.Service} is not a Windows service on this PC.");
        }
        catch (ScmException ex)
        {
            throw Translate(ex, entry, "read");
        }
    }

    private void RequireRights(ScmWhitelistEntry entry, ScmService service, ScmRights needed, string verb)
    {
        ScmRights held;
        try
        {
            held = _scm.RightsOn(service.Name);
        }
        catch (ScmException ex)
        {
            throw Translate(ex, entry, verb);
        }

        if ((held & needed) != needed)
        {
            ScmHostServicesLog.Refused(_logger, verb, entry.Service, entry.Name, "missing rights");
            throw new HostServiceDeniedException(needed.HasFlag(ScmRights.ChangeConfig)
                ? $"The agent may not change the start type of {entry.Service}: run install-agent.ps1 -AllowService {entry.Service} -AllowServiceAutostart as an administrator to allow it (that right also lets the agent change the service's program, so it is not granted by default)."
                : $"The agent may not {verb} {entry.Service}: run install-agent.ps1 -AllowService {entry.Service} as an administrator to grant it.");
        }
    }

    private string? MissingRightsError(ScmService service)
    {
        try
        {
            var held = _scm.RightsOn(service.Name);
            return (held & (ScmRights.Start | ScmRights.Stop)) == (ScmRights.Start | ScmRights.Stop)
                ? null
                : $"The agent has no start/stop rights on {service.Name}: run install-agent.ps1 -AllowService {service.Name} as an administrator.";
        }
        catch (ScmException ex)
        {
            return $"Could not check the agent's rights on {service.Name} ({ex.Message}).";
        }
    }

    /// <summary>Runs one SCM call; <paramref name="alreadyDone"/> is the Win32 error that means "already in that state".</summary>
    private void Act(ScmWhitelistEntry entry, string verb, Action call, int? alreadyDone)
    {
        try
        {
            call();
        }
        catch (ScmException ex) when (alreadyDone is { } done && ex.Error == done)
        {
            // Desired state already reached (start of a running service, stop of a stopped one).
        }
        catch (ScmException ex)
        {
            ScmHostServicesLog.ActionFailed(_logger, entry.Service, entry.Name, verb, ex.Message);
            throw Translate(ex, entry, verb);
        }
    }

    /// <summary>
    /// Waits until the service reaches <paramref name="target"/>. A start that ends Stopped failed (the service exited);
    /// still pending after <see cref="ScmHostServicesOptions.ActionTimeout"/> is ambiguous: the request was sent, so the
    /// operation ends Unknown with the observed state (a <see cref="HostAgentUnavailableException"/> over the SCM failure,
    /// which <see cref="HostServiceErrors.FromHostd"/> treats as "sent, outcome unknown").
    /// </summary>
    private async Task<ScmService> WaitForAsync(ScmWhitelistEntry entry, string verb, ScmServiceState target, CancellationToken ct)
    {
        long started = _time.GetTimestamp();
        while (true)
        {
            var service = RequireService(entry);
            if (service.State == target)
            {
                return service;
            }

            if (target == ScmServiceState.Running && service.State == ScmServiceState.Stopped)
            {
                ScmHostServicesLog.ActionFailed(_logger, entry.Service, entry.Name, verb, "stopped while starting");
                throw new HostServiceException($"{entry.Service} stopped again right after it was started (it may have failed: see the System event log).");
            }

            if (_time.GetElapsedTime(started) >= _options.ActionTimeout)
            {
                string seconds = _options.ActionTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
                throw new HostAgentUnavailableException(
                    $"Windows did not report {entry.Service} {SubStateOf(target)} within {seconds} s after the {verb} (now {SubStateOf(service.State)}).",
                    new ScmException(ScmException.ServiceRequestTimeout, "wait timed out"));
            }

            await Task.Delay(_options.PollInterval, _time, ct).ConfigureAwait(false);
        }
    }

    private static HostServiceException Translate(ScmException ex, ScmWhitelistEntry entry, string verb) => ex.Error switch
    {
        ScmException.AccessDenied => new HostServiceDeniedException(
            $"Windows refused to {verb} {entry.Service} for the agent (access denied): run install-agent.ps1 -AllowService {entry.Service} as an administrator.", ex),
        ScmException.ServiceDoesNotExist or ScmException.ServiceMarkedForDelete =>
            new HostServiceNotFoundException($"{entry.Service} is not a Windows service on this PC.", ex),
        ScmException.ServiceDisabled => new HostServiceConflictException($"{entry.Service} is disabled: turn its autostart on (or set it to Manual) before starting it.", ex),
        ScmException.DependentServicesRunning => new HostServiceConflictException($"Other running services depend on {entry.Service}: stop them first.", ex),
        ScmException.InvalidServiceControl or ScmException.ServiceCannotAcceptControl =>
            new HostServiceConflictException($"{entry.Service} does not accept a {verb} right now ({ex.Message}).", ex),
        _ => new HostServiceException($"Windows could not {verb} {entry.Service}: {ex.Message}", ex),
    };

    private long? StartedAt(ScmService service) =>
        service is { OwnProcess: true, ProcessId: > 0 } ? _scm.ProcessStartedAt(service.ProcessId.Value)?.ToUnixTimeSeconds() : null;

    private long? MemoryOf(ScmService service) =>
        service is { OwnProcess: true, ProcessId: > 0 } ? _scm.WorkingSetBytes(service.ProcessId.Value) : null;

    private static bool IsActive(ScmServiceState state) =>
        state is ScmServiceState.Running or ScmServiceState.StartPending or ScmServiceState.ContinuePending
            or ScmServiceState.StopPending or ScmServiceState.PausePending;

    private static HostServiceException NoBackupMount() =>
        new("This node's agent does not manage a backup mount.");
}
