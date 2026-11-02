namespace MirbadLLM.HostAgent;

/// <summary>
/// What the API asks of a service manager: systemd (<see cref="UnitManager"/>) on Linux, launchd
/// (<see cref="LaunchdManager"/>) on macOS. Actions only ever run for whitelisted units (callers look a unit up with
/// <see cref="WhitelistStore.Find"/> first) and never for a unit the <see cref="DenyList"/> matches; each implementation
/// re-checks that below the API, so no route can skip it.
/// </summary>
internal interface IUnitManager
{
    HostdConfig Config { get; }

    WhitelistStore Whitelist { get; }

    DenyList Deny { get; }

    /// <summary>The whitelisted units' status.</summary>
    Task<IReadOnlyList<UnitStatusDto>> ListAsync(CancellationToken ct);

    /// <summary>The whitelisted units plus every service found on the host, flagged managed/denied. Read-only.</summary>
    Task<DiscoveryDto> DiscoverAsync(CancellationToken ct);

    /// <summary>Reads one unit for a whitelist-add. Null unless it exists and has a unit file (a plist on macOS).</summary>
    Task<DiscoveredUnitDto?> InspectAsync(string unit, UnitScope scope, string? user, CancellationToken ct);

    Task<UnitStatusDto> GetAsync(AllowedUnit unit, CancellationToken ct);

    /// <summary>Start/stop/restart. A protected unit's stop or restart must carry <paramref name="confirm"/> equal to its name.</summary>
    Task<ActionResult> RunAsync(AllowedUnit unit, UnitAction action, string? confirm, CancellationToken ct);

    /// <summary>Enable/disable at boot. Turning a protected unit's autostart off needs the same typed confirmation.</summary>
    Task<ActionResult> SetAutostartAsync(AllowedUnit unit, bool enabled, string? confirm, CancellationToken ct);
}
