using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration;

/// <summary>
/// The controller's Services state in <c>{DataPath}/host-services.json</c>:
/// <list type="bullet">
/// <item><c>restartPolicies</c>: a container's restart policy remembered when its autostart is turned off, so
/// turning it back on restores <c>always</c> rather than flattening it to <c>unless-stopped</c>.</item>
/// <item><c>containers</c>: the container whitelist (only these can be started/stopped/restarted), seeded once from
/// <c>MirbadLLM:ManagedContainers</c> (or the built-in defaults) when the file has no list yet.</item>
/// </list>
/// Written atomically (temp file + rename).
/// </summary>
public sealed class HostServicesStore(string filePath) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string FilePath => filePath;

    public async Task<string?> GetAsync(string container, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await ReadAsync(ct).ConfigureAwait(false);
            return data.RestartPolicies?.GetValueOrDefault(container);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string container, string policy, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await ReadAsync(ct).ConfigureAwait(false);
            data.RestartPolicies ??= new(StringComparer.Ordinal);
            data.RestartPolicies[container] = policy;
            await WriteAsync(data, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The container whitelist; seeded from <paramref name="seed"/> (and persisted) the first time.</summary>
    public async Task<IReadOnlyList<ManagedContainerOptions>> GetContainersAsync(IEnumerable<ManagedContainerOptions> seed, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await ReadAsync(ct).ConfigureAwait(false);
            if (data.Containers is null)
            {
                data.Containers = [.. seed.Where(c => !string.IsNullOrWhiteSpace(c.Name)).Select(Clone)];
                await WriteAsync(data, ct).ConfigureAwait(false);
            }

            return [.. data.Containers.Select(Clone)];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the (seeded) container list and persists it. The callback returns the
    /// new list, or null to leave the file untouched.
    /// </summary>
    public async Task<IReadOnlyList<ManagedContainerOptions>> UpdateContainersAsync(
        IEnumerable<ManagedContainerOptions> seed,
        Func<List<ManagedContainerOptions>, List<ManagedContainerOptions>?> change,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await ReadAsync(ct).ConfigureAwait(false);
            var current = data.Containers ?? [.. seed.Where(c => !string.IsNullOrWhiteSpace(c.Name)).Select(Clone)];
            var updated = change([.. current.Select(Clone)]);
            if (updated is not null)
            {
                data.Containers = updated;
                await WriteAsync(data, ct).ConfigureAwait(false);
                return [.. updated.Select(Clone)];
            }

            return [.. current.Select(Clone)];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Everything in the file, for a backup. Null lists mean the file has none yet (not seeded).</summary>
    public async Task<HostServicesBackup> ExportAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await ReadAsync(ct).ConfigureAwait(false);
            return new HostServicesBackup(
                data.RestartPolicies is null ? null : new Dictionary<string, string>(data.RestartPolicies, StringComparer.Ordinal),
                data.Containers?.Select(Clone).ToList());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Replaces the container whitelist with the backup's and merges its remembered restart policies (the
    /// backup's value wins for a container both know). A backup without a list leaves the current list alone.
    /// </summary>
    public async Task ImportAsync(HostServicesBackup backup, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await ReadAsync(ct).ConfigureAwait(false);
            if (backup.Containers is not null)
            {
                data.Containers = [.. backup.Containers.Where(c => !string.IsNullOrWhiteSpace(c.Name)).Select(Clone)];
            }

            if (backup.RestartPolicies is { Count: > 0 })
            {
                data.RestartPolicies ??= new(StringComparer.Ordinal);
                foreach (var (container, policy) in backup.RestartPolicies)
                {
                    data.RestartPolicies[container] = policy;
                }
            }

            await WriteAsync(data, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ManagedContainerOptions Clone(ManagedContainerOptions c) => new()
    {
        Name = c.Name,
        DisplayName = c.DisplayName,
        Description = c.Description,
        Protected = c.Protected,
        Risk = c.Risk,
    };

    private async Task<FileBody> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            return new FileBody();
        }

        string text = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
        try
        {
            var body = JsonSerializer.Deserialize<FileBody>(text, JsonOptions) ?? new FileBody();
            if (body.RestartPolicies is not null)
            {
                body.RestartPolicies = new(body.RestartPolicies, StringComparer.Ordinal);
            }

            return body;
        }
        catch (JsonException ex)
        {
            // Fail closed: never treat a corrupt file as "no whitelist yet" (that would re-seed the defaults).
            throw new HostServiceException($"{filePath} is not valid JSON ({ex.Message}); fix or delete it.", ex);
        }
    }

    private async Task WriteAsync(FileBody data, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string temp = filePath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(data, JsonOptions), ct).ConfigureAwait(false);
        File.Move(temp, filePath, overwrite: true);
    }

    public void Dispose() => _gate.Dispose();

    private sealed class FileBody
    {
        public Dictionary<string, string>? RestartPolicies { get; set; }

        public List<ManagedContainerOptions>? Containers { get; set; }
    }
}

/// <summary>The contents of <c>host-services.json</c> carried in a backup (see <see cref="HostServicesStore.ExportAsync"/>).</summary>
public sealed record HostServicesBackup(
    IReadOnlyDictionary<string, string>? RestartPolicies,
    IReadOnlyList<ManagedContainerOptions>? Containers)
{
    /// <summary>
    /// Backup version 2 (design §5.5): the container lists and restart policies per node id,
    /// <c>{"nodes": {"dgx": {"containers": [...], "restartPolicies": {...}}}}</c>. Null in version 1, where the two
    /// top-level members hold the single box's lists. Written and read from MN-034 on.
    /// </summary>
    public IReadOnlyDictionary<string, HostServicesBackup>? Nodes { get; init; }
}

/// <summary>
/// The systemd-unit calls <see cref="HostServiceManager"/> makes: mirbadllm-hostd's API. Implemented locally over the
/// hostd socket (<see cref="LocalHostUnitBackend"/>) and remotely through a node's agent
/// (<c>Multinode.RemoteHostUnitBackend</c>, design §9.1). Exceptions are the <see cref="HostServiceException"/> family;
/// "hostd does not answer" is <see cref="HostAgentUnavailableException"/>.
/// </summary>
public interface IHostUnitBackend
{
    /// <summary>Whitelisted units (<c>GET /services</c>).</summary>
    Task<IReadOnlyList<HostAgentUnit>> ListAsync(CancellationToken ct = default);

    /// <summary>Whitelisted plus every discovered unit (<c>GET /services?include=all</c>).</summary>
    Task<HostAgentDiscovery> DiscoverAsync(CancellationToken ct = default);

    /// <summary>Adds a unit to hostd's whitelist; hostd re-checks the deny list and the typed name.</summary>
    Task<HostAgentUnit> AddToWhitelistAsync(string unit, string scope, string? displayName, bool isProtected, string? confirm, CancellationToken ct = default);

    /// <summary>Renames or (un)protects a whitelisted unit.</summary>
    Task<HostAgentUnit> UpdateWhitelistAsync(string name, string? displayName, bool? isProtected, string? confirm, CancellationToken ct = default);

    /// <summary>Removes a unit from the whitelist.</summary>
    Task RemoveFromWhitelistAsync(string name, CancellationToken ct = default);

    /// <summary>Starts, stops or restarts a unit.</summary>
    Task<HostAgentUnit> RunAsync(string name, HostServiceAction action, string? confirm, CancellationToken ct = default);

    /// <summary>Turns a unit's start-at-boot on or off.</summary>
    Task<HostAgentUnit> SetAutostartAsync(string name, bool enabled, string? confirm, CancellationToken ct = default);
}

/// <summary>
/// The container calls <see cref="HostServiceManager"/> makes for non-model containers. Implemented locally over the
/// Docker socket (<see cref="LocalHostContainerBackend"/>) and remotely through a node's agent
/// (<c>Multinode.RemoteHostContainerBackend</c>). The container whitelist and the remembered restart policies stay with
/// the manager (on the master); only these calls move.
/// </summary>
public interface IHostContainerBackend
{
    /// <summary>Null when no container has that name.</summary>
    Task<ContainerDetails?> InspectAsync(string name, CancellationToken ct = default);

    /// <summary>Every container, running or not.</summary>
    Task<IReadOnlyList<ServiceContainerSummary>> ListAsync(CancellationToken ct = default);

    /// <summary>Memory in use (as <c>docker stats</c> reports it); null when not reported.</summary>
    Task<long?> GetMemoryUsageAsync(string id, CancellationToken ct = default);

    /// <summary>Starts the container with this id (as returned by <see cref="InspectAsync"/>).</summary>
    Task StartAsync(string id, CancellationToken ct = default);

    /// <summary>Stops the container.</summary>
    Task StopAsync(string id, int timeoutSeconds = 30, CancellationToken ct = default);

    /// <summary>Restarts the container.</summary>
    Task RestartAsync(string id, int timeoutSeconds = 30, CancellationToken ct = default);

    /// <summary>Sets the restart policy (<c>no</c>, <c>always</c>, <c>unless-stopped</c>, <c>on-failure</c>).</summary>
    Task UpdateRestartPolicyAsync(string id, string policy, CancellationToken ct = default);
}

/// <summary><see cref="IHostUnitBackend"/> over mirbadllm-hostd's socket (today's path).</summary>
public sealed class LocalHostUnitBackend(HostAgentClient agent) : IHostUnitBackend
{
    public Task<IReadOnlyList<HostAgentUnit>> ListAsync(CancellationToken ct = default) => agent.ListAsync(ct);

    public Task<HostAgentDiscovery> DiscoverAsync(CancellationToken ct = default) => agent.DiscoverAsync(ct);

    public Task<HostAgentUnit> AddToWhitelistAsync(string unit, string scope, string? displayName, bool isProtected, string? confirm, CancellationToken ct = default) =>
        agent.AddToWhitelistAsync(unit, scope, displayName, isProtected, confirm, ct);

    public Task<HostAgentUnit> UpdateWhitelistAsync(string name, string? displayName, bool? isProtected, string? confirm, CancellationToken ct = default) =>
        agent.UpdateWhitelistAsync(name, displayName, isProtected, confirm, ct);

    public Task RemoveFromWhitelistAsync(string name, CancellationToken ct = default) => agent.RemoveFromWhitelistAsync(name, ct);

    public Task<HostAgentUnit> RunAsync(string name, HostServiceAction action, string? confirm, CancellationToken ct = default) =>
        agent.RunAsync(name, action, confirm, ct);

    public Task<HostAgentUnit> SetAutostartAsync(string name, bool enabled, string? confirm, CancellationToken ct = default) =>
        agent.SetAutostartAsync(name, enabled, confirm, ct);
}

/// <summary><see cref="IHostContainerBackend"/> over the Docker socket (today's path).</summary>
public sealed class LocalHostContainerBackend(DockerServiceClient docker) : IHostContainerBackend
{
    public Task<ContainerDetails?> InspectAsync(string name, CancellationToken ct = default) => docker.InspectAsync(name, ct);

    public Task<IReadOnlyList<ServiceContainerSummary>> ListAsync(CancellationToken ct = default) => docker.ListAsync(ct);

    public Task<long?> GetMemoryUsageAsync(string id, CancellationToken ct = default) => docker.GetMemoryUsageAsync(id, ct);

    public Task StartAsync(string id, CancellationToken ct = default) => docker.StartAsync(id, ct);

    public Task StopAsync(string id, int timeoutSeconds = 30, CancellationToken ct = default) => docker.StopAsync(id, timeoutSeconds, ct);

    public Task RestartAsync(string id, int timeoutSeconds = 30, CancellationToken ct = default) => docker.RestartAsync(id, timeoutSeconds, ct);

    public Task UpdateRestartPolicyAsync(string id, string policy, CancellationToken ct = default) => docker.UpdateRestartPolicyAsync(id, policy, ct);
}

/// <summary>
/// The Services page's backend: systemd units through mirbadllm-hostd (which owns the unit whitelist and the
/// deny list) and whitelisted non-model containers through Docker. mirbadllm itself, model containers and
/// download jobs are never listed or controlled. Protected services need a typed confirmation (their name) to
/// stop, restart or lose autostart; adding to the whitelist and unprotecting also need a signed-in admin session
/// (<see cref="IAdminReauthenticator"/>). All of that is enforced here, below the UI and the API, and again
/// inside the host agent for units.
/// <para>The unit and container calls go through <see cref="IHostUnitBackend"/> and <see cref="IHostContainerBackend"/>:
/// local sockets (the constructor taking <see cref="HostAgentClient"/> and <see cref="DockerServiceClient"/>) or a node's
/// agent (multi-node, design §9.1). Admin re-authentication, typed confirmations and the container whitelist stay here
/// either way.</para>
/// </summary>
public sealed class HostServiceManager(
    IHostUnitBackend agent,
    IHostContainerBackend docker,
    HostServicesStore store,
    IOptions<HostServicesOptions> options,
    ILogger<HostServiceManager> logger,
    IAdminReauthenticator? reauth = null) : IHostServiceManager
{
    /// <summary>Today's wiring: hostd and Docker over their local sockets.</summary>
    public HostServiceManager(
        HostAgentClient agent,
        DockerServiceClient docker,
        HostServicesStore store,
        IOptions<HostServicesOptions> options,
        ILogger<HostServiceManager> logger,
        IAdminReauthenticator? reauth = null)
        : this(new LocalHostUnitBackend(agent), new LocalHostContainerBackend(docker), store, options, logger, reauth)
    {
    }

    public const string ControllerContainerName = "mirbadllm";
    public const string ModelLabel = "mirbadllm.model";
    public const string DownloadLabel = "mirbadllm.download";

    public const string LoginDisabledMessage =
        "Whitelist changes need admin login. Set MirbadLLM__AdminPassword (and restart mirbadllm) first.";

    private static readonly TimeSpan ContainerStopTimeout = TimeSpan.FromSeconds(30);

    public async Task<HostServicesSnapshot> ListAsync(CancellationToken ct = default)
    {
        var unitsTask = ListUnitsAsync(ct);
        var containersTask = ListContainersAsync(ct);
        var (units, agentError) = await unitsTask.ConfigureAwait(false);
        var containers = await containersTask.ConfigureAwait(false);

        return new HostServicesSnapshot([.. units, .. containers], agentError is null, agentError);
    }

    public async Task<HostServicesDiscovery> DiscoverAsync(CancellationToken ct = default)
    {
        var unitsTask = DiscoverUnitsAsync(ct);
        var containersTask = DiscoverContainersAsync(ct);
        var (units, unitErrors, agentError) = await unitsTask.ConfigureAwait(false);
        var (containers, containerError) = await containersTask.ConfigureAwait(false);

        var errors = new List<string>(unitErrors);
        if (containerError is not null)
        {
            errors.Add(containerError);
        }

        var all = units.Concat(containers)
            .OrderByDescending(s => s.TotalMemoryBytes ?? -1)
            .ThenBy(s => s.Unit, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new HostServicesDiscovery(all, agentError is null, agentError, errors);
    }

    public async Task<HostServiceStatus> AddToWhitelistAsync(HostServiceWhitelistAdd request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string target = request.Unit.Trim();
        if (target.Length == 0)
        {
            throw new HostServiceException("Name the service to add.");
        }

        RequireAdmin(request.User, request.ClientKey, target, "add");

        if (request.Kind != HostServiceKind.Container)
        {
            string scope = request.Kind == HostServiceKind.User ? "user" : "system";
            if (request.Confirm is { Length: > 0 } typed && await FindContainerConfigAsync(typed.Trim(), ct).ConfigureAwait(false) is not null)
            {
                throw new HostServiceConflictException($"'{typed.Trim()}' is already the name of a managed container.");
            }

            var added = await agent.AddToWhitelistAsync(target, scope, request.DisplayName, request.Protected, request.Confirm?.Trim(), ct)
                .ConfigureAwait(false);
            HostServiceLog.Whitelist(logger, added.Name, "unit", "add", request.Protected ? "protected" : "unprotected");
            return MapUnit(added);
        }

        // Containers: the controller owns this whitelist.
        if (!string.Equals(request.Confirm?.Trim(), target, StringComparison.Ordinal))
        {
            throw new HostServiceConfirmationRequiredException($"Type the container name '{target}' to manage it.");
        }

        var details = await docker.InspectAsync(target, ct).ConfigureAwait(false);
        if (details is null || IsExcluded(details))
        {
            throw new HostServiceNotFoundException($"No container named '{target}' that can be managed here (mirbadllm, model and download containers never are).");
        }

        if (await UnitNameTakenAsync(target, ct).ConfigureAwait(false))
        {
            throw new HostServiceConflictException($"'{target}' is already the name of a managed systemd service.");
        }

        string display = string.IsNullOrWhiteSpace(request.DisplayName) ? target : request.DisplayName.Trim();
        bool exists = false;
        await store.UpdateContainersAsync(options.Value.EffectiveContainers, list =>
        {
            if (list.Any(c => string.Equals(c.Name, target, StringComparison.Ordinal)))
            {
                exists = true;
                return null;
            }

            list.Add(new ManagedContainerOptions { Name = target, DisplayName = display, Description = details.Image, Protected = request.Protected });
            return list;
        }, ct).ConfigureAwait(false);

        if (exists)
        {
            throw new HostServiceConflictException($"'{target}' is already managed.");
        }

        HostServiceLog.Whitelist(logger, target, "container", "add", request.Protected ? "protected" : "unprotected");
        return await ReadContainerAsync(await FindContainerConfigAsync(target, ct).ConfigureAwait(false)
                ?? throw new HostServiceNotFoundException($"'{target}' disappeared."), ct).ConfigureAwait(false)
            ?? throw new HostServiceNotFoundException($"Container '{target}' disappeared.");
    }

    public async Task RemoveFromWhitelistAsync(string name, CancellationToken ct = default)
    {
        RequireLoginEnabled();
        var container = await FindContainerConfigAsync(name, ct).ConfigureAwait(false);
        if (container is null)
        {
            await agent.RemoveFromWhitelistAsync(name, ct).ConfigureAwait(false);
            HostServiceLog.Whitelist(logger, name, "unit", "remove", "-");
            return;
        }

        await store.UpdateContainersAsync(options.Value.EffectiveContainers,
            list => [.. list.Where(c => !string.Equals(c.Name, name, StringComparison.Ordinal))], ct).ConfigureAwait(false);
        HostServiceLog.Whitelist(logger, name, "container", "remove", "-");
    }

    public async Task<HostServiceStatus> UpdateWhitelistAsync(string name, HostServiceWhitelistUpdate request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireLoginEnabled();
        var container = await FindContainerConfigAsync(name, ct).ConfigureAwait(false);
        bool currentlyProtected = container?.Protected ?? (await FindUnitAsync(name, ct).ConfigureAwait(false)).Protected;
        bool unprotecting = currentlyProtected && request.Protected == false;
        if (unprotecting)
        {
            if (!string.Equals(request.Confirm?.Trim(), name, StringComparison.Ordinal))
            {
                throw new HostServiceConfirmationRequiredException($"'{name}' is protected: type its name to unprotect it.");
            }

            RequireAdmin(request.User, request.ClientKey, name, "unprotect");
        }

        string change = request.Protected switch
        {
            true => "protect",
            false => "unprotect",
            null => "rename",
        };

        if (container is null)
        {
            var unit = await agent.UpdateWhitelistAsync(name, request.DisplayName, request.Protected, request.Confirm?.Trim(), ct).ConfigureAwait(false);
            HostServiceLog.Whitelist(logger, name, "unit", "update", change);
            return MapUnit(unit);
        }

        await store.UpdateContainersAsync(options.Value.EffectiveContainers, list =>
        {
            foreach (var c in list.Where(c => string.Equals(c.Name, name, StringComparison.Ordinal)))
            {
                c.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? c.DisplayName : request.DisplayName.Trim();
                c.Protected = request.Protected ?? c.Protected;
            }

            return list;
        }, ct).ConfigureAwait(false);
        HostServiceLog.Whitelist(logger, name, "container", "update", change);
        return await ReadContainerAsync((await FindContainerConfigAsync(name, ct).ConfigureAwait(false))!, ct).ConfigureAwait(false)
            ?? throw new HostServiceNotFoundException($"Container '{name}' disappeared.");
    }

    public async Task<HostServiceStatus> RunAsync(string name, HostServiceAction action, string? confirm = null, CancellationToken ct = default)
    {
        var container = await FindContainerConfigAsync(name, ct).ConfigureAwait(false);
        if (container is null)
        {
            // Not a whitelisted container: the host agent decides whether the name exists (404 → HostServiceNotFoundException).
            var unit = await FindUnitAsync(name, ct).ConfigureAwait(false);
            string unitVerb = Verb(action);
            RequireConfirmation(unit.Protected, action != HostServiceAction.Start, unit.Name, confirm, unitVerb);
            var result = await agent.RunAsync(name, action, confirm, ct).ConfigureAwait(false);
            HostServiceLog.Action(logger, name, "unit", unitVerb);
            return MapUnit(result);
        }

        var details = await InspectManagedAsync(container, ct).ConfigureAwait(false);
        RequireConfirmation(container.Protected, action != HostServiceAction.Start, container.Name, confirm, Verb(action));
        try
        {
            switch (action)
            {
                case HostServiceAction.Start:
                    await docker.StartAsync(details.Id, ct).ConfigureAwait(false);
                    break;
                case HostServiceAction.Stop:
                    await docker.StopAsync(details.Id, (int)ContainerStopTimeout.TotalSeconds, ct).ConfigureAwait(false);
                    break;
                case HostServiceAction.Restart:
                    await docker.RestartAsync(details.Id, (int)ContainerStopTimeout.TotalSeconds, ct).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }
        }
        catch (DockerApiException ex)
        {
            throw new HostServiceException($"Docker could not {Verb(action)} {container.Name}: {ex.ResponseBody.Trim()}", ex);
        }

        string containerVerb = Verb(action);
        HostServiceLog.Action(logger, name, "container", containerVerb);
        return await ReadContainerAsync(container, ct).ConfigureAwait(false)
            ?? throw new HostServiceNotFoundException($"Container '{container.Name}' disappeared.");
    }

    public async Task<HostServiceStatus> SetAutostartAsync(string name, bool enabled, string? confirm = null, CancellationToken ct = default)
    {
        var container = await FindContainerConfigAsync(name, ct).ConfigureAwait(false);
        if (container is null)
        {
            var unit = await FindUnitAsync(name, ct).ConfigureAwait(false);
            RequireConfirmation(unit.Protected, !enabled, unit.Name, confirm, "turn off autostart for");
            var result = await agent.SetAutostartAsync(name, enabled, confirm, ct).ConfigureAwait(false);
            string change = enabled ? "autostart-on" : "autostart-off";
            HostServiceLog.Action(logger, name, "unit", change);
            return MapUnit(result);
        }

        var details = await InspectManagedAsync(container, ct).ConfigureAwait(false);
        RequireConfirmation(container.Protected, !enabled, container.Name, confirm, "turn off autostart for");

        string current = NormalizePolicy(details.RestartPolicy);
        string? target = null;
        if (enabled && current == "no")
        {
            string? remembered = await store.GetAsync(container.Name, ct).ConfigureAwait(false);
            target = remembered is "always" or "unless-stopped" or "on-failure" ? remembered : "unless-stopped";
        }
        else if (!enabled && current != "no")
        {
            await store.SetAsync(container.Name, current, ct).ConfigureAwait(false);
            target = "no";
        }

        if (target is not null)
        {
            try
            {
                await docker.UpdateRestartPolicyAsync(details.Id, target, ct).ConfigureAwait(false);
            }
            catch (DockerApiException ex)
            {
                throw new HostServiceException($"Docker could not change the restart policy of {container.Name}: {ex.ResponseBody.Trim()}", ex);
            }

            string change = $"restart-policy {current} -> {target}";
            HostServiceLog.Action(logger, name, "container", change);
        }

        return await ReadContainerAsync(container, ct).ConfigureAwait(false)
            ?? throw new HostServiceNotFoundException($"Container '{container.Name}' disappeared.");
    }

    /// <summary>Maps Docker's state (plus health) onto the page's states.</summary>
    public static HostServiceState MapContainerState(string status, string? health, bool oomKilled) => status switch
    {
        "running" when health == "starting" => HostServiceState.Starting,
        "running" when health == "unhealthy" => HostServiceState.Failed,
        "running" => HostServiceState.Running,
        "restarting" => HostServiceState.Starting,
        "removing" => HostServiceState.Stopping,
        "paused" => HostServiceState.Stopped,
        "created" => HostServiceState.Stopped,
        "exited" when oomKilled => HostServiceState.Failed,
        "exited" => HostServiceState.Stopped,
        "dead" => HostServiceState.Failed,
        _ => HostServiceState.Unknown,
    };

    /// <summary>systemd ActiveState → page state.</summary>
    public static HostServiceState MapUnitState(string activeState) => activeState switch
    {
        "active" or "reloading" => HostServiceState.Running,
        "activating" => HostServiceState.Starting,
        "deactivating" => HostServiceState.Stopping,
        "inactive" => HostServiceState.Stopped,
        "failed" => HostServiceState.Failed,
        _ => HostServiceState.Unknown,
    };

    /// <summary>Unit file state → autostart; null when it cannot be toggled (static, masked, generated…).</summary>
    public static bool? MapUnitAutostart(string unitFileState) => unitFileState switch
    {
        "enabled" or "enabled-runtime" => true,
        "disabled" => false,
        _ => null,
    };

    public static bool ContainerAutostart(string restartPolicy) => NormalizePolicy(restartPolicy) != "no";

    /// <summary>mirbadllm itself, model containers and download jobs: never listed, discovered or controlled here.</summary>
    public static bool IsExcluded(string name, IReadOnlyDictionary<string, string> labels) =>
        string.Equals(name, ControllerContainerName, StringComparison.Ordinal)
        || labels.ContainsKey(ModelLabel)
        || labels.ContainsKey(DownloadLabel);

    private static string NormalizePolicy(string policy) => string.IsNullOrEmpty(policy) ? "no" : policy;

    private void RequireLoginEnabled()
    {
        if (reauth is null || !reauth.LoginEnabled)
        {
            throw new AdminReauthenticationException(AdminReauthOutcome.LoginDisabled, LoginDisabledMessage);
        }
    }

    private void RequireAdmin(System.Security.Claims.ClaimsPrincipal? user, string clientKey, string target, string change)
    {
        RequireLoginEnabled();
        var outcome = reauth!.Verify(user);
        if (outcome == AdminReauthOutcome.Verified)
        {
            return;
        }

        HostServiceLog.ReauthRefused(logger, target, change, outcome.ToString());
        throw outcome switch
        {
            AdminReauthOutcome.RateLimited => new AdminReauthenticationException(outcome, "Too many password attempts. Try again in a minute."),
            AdminReauthOutcome.LoginDisabled => new AdminReauthenticationException(outcome, LoginDisabledMessage),
            _ => new AdminReauthenticationException(outcome, "Sign in to the portal to perform this action."),
        };
    }

    private static HostServiceStatus MapUnit(HostAgentUnit u)
    {
        var state = u.Error is null ? MapUnitState(u.ActiveState) : HostServiceState.Unknown;
        return new HostServiceStatus(
            u.Name,
            u.DisplayName,
            u.Description,
            u.Scope == "user" ? HostServiceKind.User : HostServiceKind.System,
            u.Unit,
            u.Protected,
            u.Risk,
            state,
            u.SubState,
            MapUnitAutostart(u.UnitFileState),
            string.IsNullOrEmpty(u.UnitFileState) ? "unknown" : u.UnitFileState,
            u.ActiveSinceUnix is long s ? DateTimeOffset.FromUnixTimeSeconds(s) : null,
            u.MemoryBytes,
            u.GpuMemoryBytes,
            u.Error);
    }

    private static DiscoveredService MapDiscovered(HostAgentDiscoveredUnit u) => new(
        u.Name,
        u.Unit,
        u.Scope == "user" ? HostServiceKind.User : HostServiceKind.System,
        u.Description,
        MapUnitState(u.ActiveState),
        u.SubState,
        string.IsNullOrEmpty(u.UnitFileState) ? "unknown" : u.UnitFileState,
        u.MemoryBytes,
        u.GpuMemoryBytes,
        u.Managed,
        u.Denied,
        u.DeniedBy);

    private async Task<(IReadOnlyList<HostServiceStatus> Units, string? Error)> ListUnitsAsync(CancellationToken ct)
    {
        try
        {
            var units = await agent.ListAsync(ct).ConfigureAwait(false);
            return (units.Select(MapUnit).ToList(), null);
        }
        catch (HostServiceException ex)
        {
            return ([], ex.Message);
        }
    }

    private async Task<(IReadOnlyList<DiscoveredService> Units, IReadOnlyList<string> Errors, string? AgentError)> DiscoverUnitsAsync(CancellationToken ct)
    {
        try
        {
            var discovery = await agent.DiscoverAsync(ct).ConfigureAwait(false);
            return (discovery.Discovered.Select(MapDiscovered).ToList(), discovery.DiscoveryErrors, null);
        }
        catch (HostServiceException ex)
        {
            return ([], [], ex.Message);
        }
    }

    private async Task<(IReadOnlyList<DiscoveredService> Containers, string? Error)> DiscoverContainersAsync(CancellationToken ct)
    {
        IReadOnlyList<ServiceContainerSummary> all;
        IReadOnlyList<ManagedContainerOptions> managed;
        try
        {
            all = await docker.ListAsync(ct).ConfigureAwait(false);
            managed = await ManagedContainersAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or HostServiceException)
        {
            return ([], $"Could not list containers: {ex.Message}");
        }

        var candidates = all.Where(c => c.Name.Length > 0 && !IsExcluded(c.Name, c.Labels)).ToList();
        var memory = await Task.WhenAll(candidates.Select(async c =>
        {
            if (c.State is not ("running" or "restarting"))
            {
                return (long?)null;
            }

            try
            {
                return await docker.GetMemoryUsageAsync(c.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException)
            {
                return null;
            }
        })).ConfigureAwait(false);

        var result = new List<DiscoveredService>(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            result.Add(new DiscoveredService(
                c.Name,
                c.Name,
                HostServiceKind.Container,
                c.Image,
                MapContainerState(c.State, c.Status.Contains("(unhealthy)", StringComparison.Ordinal) ? "unhealthy" : null, oomKilled: false),
                c.Status,
                "",
                memory[i],
                null,
                managed.Any(m => string.Equals(m.Name, c.Name, StringComparison.Ordinal)),
                Denied: false,
                DeniedBy: null));
        }

        return (result, null);
    }

    private async Task<IReadOnlyList<HostServiceStatus>> ListContainersAsync(CancellationToken ct)
    {
        IReadOnlyList<ManagedContainerOptions> containers;
        try
        {
            containers = await ManagedContainersAsync(ct).ConfigureAwait(false);
        }
        catch (HostServiceException ex)
        {
            HostServiceLog.StoreFailed(logger, ex.Message);
            return [];
        }

        var result = new List<HostServiceStatus>();
        foreach (var container in containers)
        {
            try
            {
                var status = await ReadContainerAsync(container, ct).ConfigureAwait(false);
                if (status is not null)
                {
                    result.Add(status);
                }
            }
            catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or HostServiceException)
            {
                // HostServiceException: a remote container backend's read failure (the local one never throws it here).
                result.Add(new HostServiceStatus(
                    container.Name, container.DisplayName ?? container.Name, container.Description ?? "", HostServiceKind.Container,
                    container.Name, container.Protected, container.Risk, HostServiceState.Unknown, "", null, "unknown",
                    null, null, null, $"Could not read it from Docker: {ex.Message}"));
            }
        }

        return result;
    }

    /// <summary>Null when the container does not exist or must not be managed here (mirbadllm, model/download containers).</summary>
    private async Task<HostServiceStatus?> ReadContainerAsync(ManagedContainerOptions container, CancellationToken ct)
    {
        var details = await docker.InspectAsync(container.Name, ct).ConfigureAwait(false);
        if (details is null || IsExcluded(details))
        {
            return null;
        }

        bool running = details.Status is "running" or "restarting";
        long? memory = running ? await docker.GetMemoryUsageAsync(details.Id, ct).ConfigureAwait(false) : null;
        string detail = details.Status == "exited" ? $"exited ({details.ExitCode})" : details.Status;
        if (details.Health is { Length: > 0 } health && details.Status == "running")
        {
            detail = $"running ({health})";
        }

        return new HostServiceStatus(
            container.Name,
            container.DisplayName ?? container.Name,
            container.Description ?? "",
            HostServiceKind.Container,
            details.Name,
            container.Protected,
            container.Risk,
            MapContainerState(details.Status, details.Health, details.OomKilled),
            detail,
            ContainerAutostart(details.RestartPolicy),
            NormalizePolicy(details.RestartPolicy),
            running ? details.StartedAt : null,
            memory,
            null);
    }

    private async Task<ContainerDetails> InspectManagedAsync(ManagedContainerOptions container, CancellationToken ct)
    {
        var details = await docker.InspectAsync(container.Name, ct).ConfigureAwait(false);
        return details is null || IsExcluded(details)
            ? throw new HostServiceNotFoundException($"No managed container named '{container.Name}'.")
            : details;
    }

    private async Task<HostAgentUnit> FindUnitAsync(string name, CancellationToken ct)
    {
        var units = await agent.ListAsync(ct).ConfigureAwait(false);
        return units.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.Ordinal))
            ?? throw new HostServiceNotFoundException($"'{name}' is not a managed service.");
    }

    private async Task<bool> UnitNameTakenAsync(string name, CancellationToken ct)
    {
        try
        {
            var units = await agent.ListAsync(ct).ConfigureAwait(false);
            return units.Any(u => string.Equals(u.Name, name, StringComparison.Ordinal));
        }
        catch (HostAgentUnavailableException)
        {
            return false;
        }
    }

    private static bool IsExcluded(ContainerDetails details) => IsExcluded(details.Name, details.Labels);

    private async Task<IReadOnlyList<ManagedContainerOptions>> ManagedContainersAsync(CancellationToken ct) =>
        [.. (await store.GetContainersAsync(options.Value.EffectiveContainers, ct).ConfigureAwait(false))
            .Where(c => !string.IsNullOrWhiteSpace(c.Name) && !string.Equals(c.Name, ControllerContainerName, StringComparison.Ordinal))];

    private async Task<ManagedContainerOptions?> FindContainerConfigAsync(string name, CancellationToken ct) =>
        (await ManagedContainersAsync(ct).ConfigureAwait(false)).FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    private static void RequireConfirmation(bool isProtected, bool risky, string name, string? confirm, string verb)
    {
        if (isProtected && risky && !string.Equals(confirm, name, StringComparison.Ordinal))
        {
            throw new HostServiceConfirmationRequiredException($"'{name}' is protected: type its name to {verb} it.");
        }
    }

    private static string Verb(HostServiceAction action) => action switch
    {
        HostServiceAction.Start => "start",
        HostServiceAction.Stop => "stop",
        HostServiceAction.Restart => "restart",
        _ => action.ToString().ToLowerInvariant(),
    };
}

internal static partial class HostServiceLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Services: {Name} ({Kind}) {Action}")]
    public static partial void Action(ILogger logger, string name, string kind, string action);

    [LoggerMessage(Level = LogLevel.Information, Message = "Services whitelist: {Name} ({Kind}) {Change} {Detail}")]
    public static partial void Whitelist(ILogger logger, string name, string kind, string change, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Services whitelist: {Change} of {Name} refused, admin re-authentication {Outcome}")]
    public static partial void ReauthRefused(ILogger logger, string name, string change, string outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Services: could not read the container whitelist: {Reason}")]
    public static partial void StoreFailed(ILogger logger, string reason);
}
