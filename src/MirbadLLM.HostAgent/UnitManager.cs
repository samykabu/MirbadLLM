using Microsoft.Extensions.Logging;

namespace MirbadLLM.HostAgent;

/// <summary>A unit's status as sent to the controller (camelCase JSON).</summary>
internal sealed record UnitStatusDto(
    string Name,
    string Unit,
    string Scope,
    string? User,
    string DisplayName,
    string Description,
    bool Protected,
    string? Risk,
    string ActiveState,
    string SubState,
    string UnitFileState,
    int? MainPid,
    long? ActiveSinceUnix,
    long? MemoryBytes,
    long? GpuMemoryBytes,
    string? Error);

internal enum ActionOutcome
{
    Done,
    ConfirmationRequired,
    Denied,
    Failed,
}

/// <summary><c>GET /services?include=all</c>: the whitelisted units' status plus everything discovered.</summary>
internal sealed record DiscoveryDto(
    IReadOnlyList<UnitStatusDto> Services,
    IReadOnlyList<DiscoveredUnitDto> Discovered,
    IReadOnlyList<string> DiscoveryErrors,
    IReadOnlyList<string> DenyPatterns);

internal sealed record ActionResult(ActionOutcome Outcome, string? Message, UnitStatusDto? Status);

/// <summary>
/// Everything the agent does to systemd (<see cref="IUnitManager"/> on Linux). Actions only ever run for whitelisted
/// units (callers look a unit up with <see cref="WhitelistStore.Find"/> first) and never for a unit the
/// <see cref="DenyList"/> matches; that is re-checked here, below the API, so no route can skip it.
/// </summary>
internal sealed class UnitManager(
    HostdConfig config,
    WhitelistStore whitelist,
    DenyList deny,
    IProcessRunner runner,
    ICgroupReader cgroups,
    ILogger<UnitManager> logger) : IUnitManager
{
    private const int ShowBatchSize = 120;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan GpuTimeout = TimeSpan.FromSeconds(10);

    public HostdConfig Config => config;

    public WhitelistStore Whitelist => whitelist;

    public DenyList Deny => deny;

    public async Task<IReadOnlyList<UnitStatusDto>> ListAsync(CancellationToken ct)
    {
        var gpu = await ReadGpuAsync(ct).ConfigureAwait(false);
        return await ListAsync(gpu, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Every service unit of the system manager and of each <see cref="HostdConfig.DiscoverUsers"/> user's manager,
    /// with state, memory and GPU memory, flagged managed/denied. Read-only: list-units, list-unit-files, show.
    /// </summary>
    public async Task<DiscoveryDto> DiscoverAsync(CancellationToken ct)
    {
        var gpu = await ReadGpuAsync(ct).ConfigureAwait(false);
        var managed = await ListAsync(gpu, ct).ConfigureAwait(false);
        var discovered = new List<DiscoveredUnitDto>();
        var errors = new List<string>();

        var scopes = new List<(UnitScope Scope, string? User)> { (UnitScope.System, null) };
        scopes.AddRange(config.DiscoverUsers.Select(u => (UnitScope.User, (string?)u)));
        foreach (var (scope, user) in scopes)
        {
            string label = scope == UnitScope.User ? $"user {user}" : "system";
            try
            {
                discovered.AddRange(await DiscoverScopeAsync(scope, user, gpu, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is DiscoveryException or System.Text.Json.JsonException)
            {
                Log.DiscoveryFailed(logger, label, ex.Message);
                errors.Add($"Could not list {label} services: {ex.Message}");
            }
        }

        return new DiscoveryDto(managed, discovered, errors, deny.Patterns);
    }

    /// <summary>Reads one unit for a whitelist-add. Null unless it is loaded and has a unit file.</summary>
    public async Task<DiscoveredUnitDto?> InspectAsync(string unit, UnitScope scope, string? user, CancellationToken ct)
    {
        string output;
        try
        {
            output = await RunQueryAsync(SystemctlCommands.ShowMany(scope, user, [unit]), ct).ConfigureAwait(false);
        }
        catch (DiscoveryException)
        {
            return null;
        }

        var shown = UnitStatusParser.ParseBlocks(output);
        return UnitDiscovery.Build(scope, user, shown, null, cgroups, whitelist, deny)
            .FirstOrDefault(d => string.Equals(d.Unit, unit, StringComparison.Ordinal));
    }

    public async Task<UnitStatusDto> GetAsync(AllowedUnit unit, CancellationToken ct)
    {
        var gpu = await ReadGpuAsync(ct).ConfigureAwait(false);
        return await ReadStatusAsync(unit, gpu, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Start/stop/restart. A protected unit's stop or restart must carry <paramref name="confirm"/> equal to its
    /// name (the controller asks the operator to type it); otherwise nothing runs.
    /// </summary>
    public async Task<ActionResult> RunAsync(AllowedUnit unit, UnitAction action, string? confirm, CancellationToken ct)
    {
        string verb = SystemctlCommands.Verb(action);
        if (RefuseDenied(unit, verb) is { } denied)
        {
            return denied;
        }

        if (unit.Protected && action != UnitAction.Start && !string.Equals(confirm, unit.Name, StringComparison.Ordinal))
        {
            Log.ActionRefused(logger, unit.Name, unit.Unit, verb);
            return new ActionResult(ActionOutcome.ConfirmationRequired, $"'{unit.Name}' is protected: confirm with its name to {verb} it.", null);
        }

        var run = await runner.RunAsync(SystemctlCommands.SystemctlPath, SystemctlCommands.Action(unit, action), ActionTimeout, ct)
            .ConfigureAwait(false);
        return await FinishAsync(unit, verb, run, ct).ConfigureAwait(false);
    }

    /// <summary>Enable/disable at boot. Turning a protected unit's autostart off needs the same typed confirmation.</summary>
    public async Task<ActionResult> SetAutostartAsync(AllowedUnit unit, bool enabled, string? confirm, CancellationToken ct)
    {
        string verb = enabled ? "enable" : "disable";
        if (RefuseDenied(unit, verb) is { } denied)
        {
            return denied;
        }

        if (unit.Protected && !enabled && !string.Equals(confirm, unit.Name, StringComparison.Ordinal))
        {
            Log.ActionRefused(logger, unit.Name, unit.Unit, verb);
            return new ActionResult(ActionOutcome.ConfirmationRequired, $"'{unit.Name}' is protected: confirm with its name to disable it at boot.", null);
        }

        var run = await runner.RunAsync(SystemctlCommands.SystemctlPath, SystemctlCommands.Autostart(unit, enabled), ActionTimeout, ct)
            .ConfigureAwait(false);
        return await FinishAsync(unit, verb, run, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<UnitStatusDto>> ListAsync(IReadOnlyDictionary<int, long>? gpu, CancellationToken ct)
    {
        var units = whitelist.Units;
        var result = new List<UnitStatusDto>(units.Count);
        foreach (var unit in units)
        {
            result.Add(await ReadStatusAsync(unit, gpu, ct).ConfigureAwait(false));
        }

        return result;
    }

    private async Task<List<DiscoveredUnitDto>> DiscoverScopeAsync(UnitScope scope, string? user, IReadOnlyDictionary<int, long>? gpu, CancellationToken ct)
    {
        string units = await RunQueryAsync(SystemctlCommands.ListUnits(scope, user), ct).ConfigureAwait(false);
        string files = await RunQueryAsync(SystemctlCommands.ListUnitFiles(scope, user), ct).ConfigureAwait(false);
        var candidates = UnitDiscovery.Candidates(units, files);
        var shown = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var chunk in candidates.Chunk(ShowBatchSize))
        {
            string output = await RunQueryAsync(SystemctlCommands.ShowMany(scope, user, chunk), ct).ConfigureAwait(false);
            foreach (var (id, values) in UnitStatusParser.ParseBlocks(output))
            {
                shown.TryAdd(id, values);
            }
        }

        return UnitDiscovery.Build(scope, user, shown, gpu, cgroups, whitelist, deny);
    }

    private async Task<string> RunQueryAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        ProcessResult run;
        try
        {
            run = await runner.RunAsync(SystemctlCommands.SystemctlPath, args, QueryTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new DiscoveryException(ex.Message, ex);
        }

        return run.Succeeded ? run.StdOut : throw new DiscoveryException(run.Describe());
    }

    private ActionResult? RefuseDenied(AllowedUnit unit, string verb)
    {
        string? pattern = deny.Match(unit.Unit) ?? deny.Match(unit.Name);
        if (pattern is null)
        {
            return null;
        }

        Log.DeniedRequest(logger, unit.Name, verb, pattern);
        return new ActionResult(ActionOutcome.Denied, $"{unit.Unit} is a system service (matches '{pattern}') and cannot be managed.", null);
    }

    private async Task<ActionResult> FinishAsync(AllowedUnit unit, string verb, ProcessResult run, CancellationToken ct)
    {
        if (!run.Succeeded)
        {
            string reason = run.Describe();
            Log.ActionFailed(logger, unit.Name, unit.Unit, verb, reason);
            return new ActionResult(ActionOutcome.Failed, $"systemctl {verb} {unit.Unit} failed ({reason}).", null);
        }

        Log.ActionDone(logger, unit.Name, unit.Unit, verb);
        return new ActionResult(ActionOutcome.Done, null, await GetAsync(unit, ct).ConfigureAwait(false));
    }

    private async Task<UnitStatusDto> ReadStatusAsync(AllowedUnit unit, IReadOnlyDictionary<int, long>? gpu, CancellationToken ct)
    {
        string scope = unit.Scope == UnitScope.User ? "user" : "system";
        ProcessResult run;
        try
        {
            run = await runner.RunAsync(SystemctlCommands.SystemctlPath, SystemctlCommands.Show(unit), QueryTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            run = new ProcessResult(-1, "", ex.Message);
        }

        if (!run.Succeeded)
        {
            string reason = run.Describe();
            Log.StatusFailed(logger, unit.Name, unit.Unit, reason);
            return new UnitStatusDto(unit.Name, unit.Unit, scope, unit.User, unit.DisplayName, unit.Description, unit.Protected, unit.Risk,
                "unknown", "", "", null, null, null, null, $"Could not read status ({reason}).");
        }

        var props = UnitStatusParser.Parse(run.StdOut);
        bool active = props.ActiveState is "active" or "reloading" or "activating" or "deactivating";
        long? gpuBytes = gpu is null || !active ? null : GpuCgroupMapper.SumForCgroup(props.ControlGroup, gpu, cgroups);

        return new UnitStatusDto(
            unit.Name,
            unit.Unit,
            scope,
            unit.User,
            unit.DisplayName,
            unit.Description,
            unit.Protected,
            unit.Risk,
            props.ActiveState,
            props.SubState,
            props.UnitFileState,
            props.MainPid,
            active ? props.ActiveSince?.ToUnixTimeSeconds() : null,
            active ? props.MemoryBytes : null,
            gpuBytes,
            null);
    }

    /// <summary>PID → GPU bytes, or null when nvidia-smi is missing or fails (GPU then shows as unknown, not 0).</summary>
    private async Task<IReadOnlyDictionary<int, long>?> ReadGpuAsync(CancellationToken ct)
    {
        try
        {
            var run = await runner.RunAsync(SystemctlCommands.NvidiaSmiPath, SystemctlCommands.GpuApps(), GpuTimeout, ct).ConfigureAwait(false);
            return run.Succeeded ? GpuCgroupMapper.ParseComputeApps(run.StdOut) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>A read-only systemctl query failed (non-zero exit, timeout, missing binary).</summary>
internal sealed class DiscoveryException : Exception
{
    public DiscoveryException()
    {
    }

    public DiscoveryException(string message)
        : base(message)
    {
    }

    public DiscoveryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
