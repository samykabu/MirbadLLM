using System.Collections.Concurrent;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>
/// Shared plumbing of the remote host-service backends: every mutation goes through <see cref="AgentOperationRunner"/>
/// (envelope, operation reference written before sending, closed on a terminal status or a 4xx), failures become the
/// <see cref="HostServiceException"/> family (<see cref="RemoteAgentErrors.ToHostServiceException"/>).
/// </summary>
internal static class RemoteHostCalls
{
    /// <summary>The operation target of backup-mount mutations (the agent's own, design §4.3).</summary>
    public const string BackupMountTarget = "backup-mount";

    public static async Task<OperationStatus> SubmitAsync<TRequest>(
        AgentOperationRunner runner,
        string nodeId,
        AgentRoute route,
        string target,
        TRequest request,
        Func<IAgentClient, TRequest, CancellationToken, Task<OperationStatus>> send,
        CancellationToken ct)
        where TRequest : AgentMutation
    {
        try
        {
            return await runner.SubmitAsync(nodeId, route, target, request, send, owner: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (RemoteAgentErrors.IsAgentFailure(ex))
        {
            throw RemoteAgentErrors.ToHostServiceException(ex, nodeId, request.OperationId);
        }
    }

    public static async Task<T> ReadAsync<T>(IAgentClientFactory clients, string nodeId, Func<IAgentClient, Task<T>> read)
    {
        try
        {
            return await read(clients.GetClient(nodeId)).ConfigureAwait(false);
        }
        catch (Exception ex) when (RemoteAgentErrors.IsAgentFailure(ex))
        {
            throw RemoteAgentErrors.ToHostServiceException(ex, nodeId);
        }
    }

    public static T RequireResult<T>(OperationStatus status, string what)
        where T : class =>
        status.ReadResult<T>() ?? throw new HostServiceException($"The agent reported no {what} after operation {status.Id}.");
}

/// <summary>
/// <see cref="IHostUnitBackend"/> over a node's agent (design §4.3 host services, §9.1): reads from
/// <c>GET /host/services</c>, actions and whitelist changes as agent operations that hostd executes. hostd keeps the unit
/// whitelist and deny list on the node; <see cref="HostServiceManager"/> keeps admin re-authentication and the typed
/// confirmations on the master. An <c>Unknown</c> outcome surfaces as <see cref="HostServiceOutcomeUnknownException"/>.
/// </summary>
public sealed class RemoteHostUnitBackend : IHostUnitBackend
{
    private readonly string _nodeId;
    private readonly IAgentClientFactory _clients;
    private readonly AgentOperationRunner _runner;

    public RemoteHostUnitBackend(string nodeId, IAgentClientFactory clients, AgentOperationRunner runner)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(runner);
        _nodeId = nodeId;
        _clients = clients;
        _runner = runner;
    }

    /// <summary>The node this backend drives.</summary>
    public string NodeId => _nodeId;

    public async Task<IReadOnlyList<HostAgentUnit>> ListAsync(CancellationToken ct = default)
    {
        var response = await RemoteHostCalls.ReadAsync(_clients, _nodeId, c => c.ListHostServicesAsync(new HostServicesQuery(), ct)).ConfigureAwait(false);
        RequireHostd(response);
        return [.. response.Units.Select(ToHostAgentUnit)];
    }

    public async Task<HostAgentDiscovery> DiscoverAsync(CancellationToken ct = default)
    {
        var response = await RemoteHostCalls.ReadAsync(_clients, _nodeId,
            c => c.ListHostServicesAsync(new HostServicesQuery { Include = HostServicesQuery.IncludeAll }, ct)).ConfigureAwait(false);
        RequireHostd(response);
        return new HostAgentDiscovery(
            [.. response.Units.Select(ToHostAgentUnit)],
            [.. (response.Discovered ?? []).Select(ToHostAgentDiscoveredUnit)],
            response.DiscoveryErrors ?? [],
            response.DenyPatterns ?? []);
    }

    public async Task<HostAgentUnit> AddToWhitelistAsync(string unit, string scope, string? displayName, bool isProtected, string? confirm, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        var envelope = _runner.CreateEnvelope();
        var request = new HostWhitelistAddRequest
        {
            OperationId = envelope.OperationId,
            IssuedAt = envelope.IssuedAt,
            ExpiresAt = envelope.ExpiresAt,
            Unit = unit,
            Scope = scope,
            DisplayName = displayName,
            Protected = isProtected,
            Confirm = confirm,
        };
        var status = await RemoteHostCalls.SubmitAsync(_runner, _nodeId, AgentRoutes.HostWhitelistAdd, unit.Trim(), request,
            (c, r, t) => c.AddToWhitelistAsync(r, t), ct).ConfigureAwait(false);
        return ToHostAgentUnit(RemoteHostCalls.RequireResult<HostUnit>(status, "unit"));
    }

    public async Task<HostAgentUnit> UpdateWhitelistAsync(string name, string? displayName, bool? isProtected, string? confirm, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var envelope = _runner.CreateEnvelope();
        var request = new HostWhitelistUpdateRequest
        {
            OperationId = envelope.OperationId,
            IssuedAt = envelope.IssuedAt,
            ExpiresAt = envelope.ExpiresAt,
            DisplayName = displayName,
            Protected = isProtected,
            Confirm = confirm,
        };
        var status = await RemoteHostCalls.SubmitAsync(_runner, _nodeId, AgentRoutes.HostWhitelistUpdate, name, request,
            (c, r, t) => c.UpdateWhitelistAsync(name, r, t), ct).ConfigureAwait(false);
        return ToHostAgentUnit(RemoteHostCalls.RequireResult<HostUnit>(status, "unit"));
    }

    public async Task RemoveFromWhitelistAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var envelope = _runner.CreateEnvelope();
        var request = new HostWhitelistRemoveRequest { OperationId = envelope.OperationId, IssuedAt = envelope.IssuedAt, ExpiresAt = envelope.ExpiresAt };
        await RemoteHostCalls.SubmitAsync(_runner, _nodeId, AgentRoutes.HostWhitelistRemove, name, request,
            (c, r, t) => c.RemoveFromWhitelistAsync(name, r, t), ct).ConfigureAwait(false);
    }

    public async Task<HostAgentUnit> RunAsync(string name, HostServiceAction action, string? confirm, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var route = action switch
        {
            HostServiceAction.Start => AgentRoutes.HostServiceStart,
            HostServiceAction.Stop => AgentRoutes.HostServiceStop,
            HostServiceAction.Restart => AgentRoutes.HostServiceRestart,
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        var envelope = _runner.CreateEnvelope();
        var request = new HostServiceActionRequest
        {
            OperationId = envelope.OperationId,
            IssuedAt = envelope.IssuedAt,
            ExpiresAt = envelope.ExpiresAt,
            Confirm = confirm,
        };
        var status = await RemoteHostCalls.SubmitAsync(_runner, _nodeId, route, name, request,
            (c, r, t) => c.RunHostServiceAsync(name, action, r, t), ct).ConfigureAwait(false);
        return ToHostAgentUnit(RemoteHostCalls.RequireResult<HostUnit>(status, "unit"));
    }

    public async Task<HostAgentUnit> SetAutostartAsync(string name, bool enabled, string? confirm, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var envelope = _runner.CreateEnvelope();
        var request = new HostServiceAutostartRequest
        {
            OperationId = envelope.OperationId,
            IssuedAt = envelope.IssuedAt,
            ExpiresAt = envelope.ExpiresAt,
            Enabled = enabled,
            Confirm = confirm,
        };
        var status = await RemoteHostCalls.SubmitAsync(_runner, _nodeId, AgentRoutes.HostServiceAutostart, name, request,
            (c, r, t) => c.SetHostServiceAutostartAsync(name, r, t), ct).ConfigureAwait(false);
        return ToHostAgentUnit(RemoteHostCalls.RequireResult<HostUnit>(status, "unit"));
    }

    /// <summary>hostd's unit as the agent forwards it → today's <see cref="HostAgentUnit"/>.</summary>
    public static HostAgentUnit ToHostAgentUnit(HostUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return new HostAgentUnit(
            unit.Name, unit.Unit, unit.Scope, unit.User, unit.DisplayName, unit.Description, unit.Protected, unit.Risk,
            unit.ActiveState, unit.SubState, unit.UnitFileState, unit.MainPid, unit.ActiveSinceUnix, unit.MemoryBytes,
            unit.GpuMemoryBytes, unit.Error);
    }

    /// <summary>A discovered unit as the agent forwards it → today's <see cref="HostAgentDiscoveredUnit"/>.</summary>
    public static HostAgentDiscoveredUnit ToHostAgentDiscoveredUnit(HostDiscoveredUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return new HostAgentDiscoveredUnit(
            unit.Name, unit.Unit, unit.Scope, unit.User, unit.Description, unit.ActiveState, unit.SubState, unit.UnitFileState,
            unit.ActiveSinceUnix, unit.MemoryBytes, unit.GpuMemoryBytes, unit.Managed, unit.Denied, unit.DeniedBy);
    }

    private void RequireHostd(HostServicesResponse response)
    {
        if (!response.HostdAvailable)
        {
            throw new HostAgentUnavailableException(response.HostdError ?? $"mirbadllm-hostd does not answer on node {_nodeId}.");
        }
    }
}

/// <summary>
/// <see cref="IHostContainerBackend"/> over a node's agent (design §4.3 host containers, §10.3): containers are read from
/// <c>GET /host/services</c> and <c>GET /host/containers/{name}</c>; actions and restart-policy changes are agent
/// operations. The master keeps the container whitelist and the remembered restart policies
/// (<see cref="HostServicesStore"/>); the agent refuses its own container, the master's, model and download containers
/// (403 <c>denied</c>, read here as "no such container").
/// <para>The agent addresses containers by name and reports memory with each container, so this backend remembers the
/// last container it saw per id: <see cref="GetMemoryUsageAsync"/> answers from it, and actions on an id go to that
/// container's name.</para>
/// </summary>
public sealed class RemoteHostContainerBackend : IHostContainerBackend
{
    private const int MaxRemembered = 1_000;

    private readonly string _nodeId;
    private readonly IAgentClientFactory _clients;
    private readonly AgentOperationRunner _runner;
    private readonly ConcurrentDictionary<string, HostContainerInfo> _seen = new(StringComparer.Ordinal);

    public RemoteHostContainerBackend(string nodeId, IAgentClientFactory clients, AgentOperationRunner runner)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(runner);
        _nodeId = nodeId;
        _clients = clients;
        _runner = runner;
    }

    /// <summary>The node this backend drives.</summary>
    public string NodeId => _nodeId;

    public async Task<ContainerDetails?> InspectAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        HostContainerInfo? info;
        try
        {
            info = await _clients.GetClient(_nodeId).GetContainerAsync(name, ct).ConfigureAwait(false);
        }
        catch (AgentException ex) when (ex.Code == ErrorCodes.Denied)
        {
            // The agent never manages it (its own, the master's, a model or download container): not manageable here either.
            return null;
        }
        catch (Exception ex) when (RemoteAgentErrors.IsAgentFailure(ex))
        {
            throw RemoteAgentErrors.ToHostServiceException(ex, _nodeId);
        }

        return info is null ? null : ToDetails(Remember(info));
    }

    public async Task<IReadOnlyList<ServiceContainerSummary>> ListAsync(CancellationToken ct = default)
    {
        var response = await RemoteHostCalls.ReadAsync(_clients, _nodeId, c => c.ListHostServicesAsync(new HostServicesQuery(), ct)).ConfigureAwait(false);
        if (!response.ContainersAvailable)
        {
            throw new HostServiceException(response.ContainersError ?? $"The agent of node {_nodeId} cannot list containers.");
        }

        return [.. response.Containers.Select(c => ToSummary(Remember(c)))];
    }

    public Task<long?> GetMemoryUsageAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_seen.TryGetValue(id, out var info) && IsRunning(info.State) ? info.MemoryBytes : null);

    public Task StartAsync(string id, CancellationToken ct = default) => RunAsync(id, HostServiceAction.Start, 30, ct);

    public Task StopAsync(string id, int timeoutSeconds = 30, CancellationToken ct = default) => RunAsync(id, HostServiceAction.Stop, timeoutSeconds, ct);

    public Task RestartAsync(string id, int timeoutSeconds = 30, CancellationToken ct = default) => RunAsync(id, HostServiceAction.Restart, timeoutSeconds, ct);

    public async Task UpdateRestartPolicyAsync(string id, string policy, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(policy);
        string name = NameOf(id);
        var envelope = _runner.CreateEnvelope();
        var request = new HostContainerAutostartRequest
        {
            OperationId = envelope.OperationId,
            IssuedAt = envelope.IssuedAt,
            ExpiresAt = envelope.ExpiresAt,
            RestartPolicy = policy,
        };
        var status = await RemoteHostCalls.SubmitAsync(_runner, _nodeId, AgentRoutes.HostContainerAutostart, name, request,
            (c, r, t) => c.SetContainerAutostartAsync(name, r, t), ct).ConfigureAwait(false);
        RememberResult(status);
    }

    /// <summary>The agent's container → today's <see cref="ContainerDetails"/>.</summary>
    public static ContainerDetails ToDetails(HostContainerInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new ContainerDetails(
            info.Id, info.Name, info.State, info.ExitCode ?? 0, info.OomKilled, info.Health, info.StartedAt,
            info.RestartPolicy ?? "", info.Labels, info.Image);
    }

    /// <summary>The agent's container → today's <see cref="ServiceContainerSummary"/>.</summary>
    public static ServiceContainerSummary ToSummary(HostContainerInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new ServiceContainerSummary(info.Id, info.Name, info.State, info.Status, info.Image, info.Labels);
    }

    private static bool IsRunning(string state) => state is "running" or "restarting";

    private async Task RunAsync(string id, HostServiceAction action, int timeoutSeconds, CancellationToken ct)
    {
        var route = action switch
        {
            HostServiceAction.Start => AgentRoutes.HostContainerStart,
            HostServiceAction.Stop => AgentRoutes.HostContainerStop,
            HostServiceAction.Restart => AgentRoutes.HostContainerRestart,
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        string name = NameOf(id);
        var envelope = _runner.CreateEnvelope();
        var request = new HostContainerActionRequest
        {
            OperationId = envelope.OperationId,
            IssuedAt = envelope.IssuedAt,
            ExpiresAt = envelope.ExpiresAt,
            TimeoutSeconds = timeoutSeconds,
        };
        var status = await RemoteHostCalls.SubmitAsync(_runner, _nodeId, route, name, request,
            (c, r, t) => c.RunContainerAsync(name, action, r, t), ct).ConfigureAwait(false);
        RememberResult(status);
    }

    private string NameOf(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return _seen.TryGetValue(id, out var info) ? info.Name : id;
    }

    private void RememberResult(OperationStatus status)
    {
        try
        {
            if (status.ReadResult<HostContainerInfo>() is { } info)
            {
                Remember(info);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // The action succeeded; an unreadable result only means the next read refreshes the row.
        }
    }

    private HostContainerInfo Remember(HostContainerInfo info)
    {
        if (_seen.Count >= MaxRemembered)
        {
            _seen.Clear();
        }

        _seen[info.Id] = info;
        return info;
    }
}

/// <summary>
/// <see cref="IHostAgentBackupMountClient"/> over the agent of the node that mounts the backup share (design §9.3
/// topology a, <c>MirbadLLM:Backup:MountNodeId</c>, default <c>dgx</c>): GET and test are reads; PUT and DELETE are
/// agent operations. The share password passes through once, in memory: the agent redacts it from the stored operation
/// and the master's operation reference digests the request without it (D38). A PUT whose outcome the agent cannot verify
/// surfaces as <see cref="HostServiceOutcomeUnknownException"/>.
/// </summary>
public sealed class RemoteBackupMountClient : IHostAgentBackupMountClient
{
    private readonly string _nodeId;
    private readonly IAgentClientFactory _clients;
    private readonly AgentOperationRunner _runner;

    public RemoteBackupMountClient(string nodeId, IAgentClientFactory clients, AgentOperationRunner runner)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(runner);
        _nodeId = nodeId;
        _clients = clients;
        _runner = runner;
    }

    /// <summary>The node whose hostd mounts the share.</summary>
    public string NodeId => _nodeId;

    public async Task<HostAgentBackupMount> GetBackupMountAsync(CancellationToken ct = default) =>
        ToMount(await RemoteHostCalls.ReadAsync(_clients, _nodeId, c => c.GetBackupMountAsync(ct)).ConfigureAwait(false));

    public async Task<HostAgentBackupMountTest> TestBackupMountAsync(HostAgentBackupMountRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await RemoteHostCalls.ReadAsync(_clients, _nodeId,
            c => c.TestBackupMountAsync(new BackupMountTestRequest { Settings = ToSettings(request) }, ct)).ConfigureAwait(false);
        return new HostAgentBackupMountTest(result.Ok, result.Message);
    }

    public async Task<HostAgentBackupMount> SetBackupMountAsync(HostAgentBackupMountRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var envelope = _runner.CreateEnvelope();
        var body = new BackupMountSetRequest
        {
            OperationId = envelope.OperationId,
            IssuedAt = envelope.IssuedAt,
            ExpiresAt = envelope.ExpiresAt,
            Settings = ToSettings(request),
        };
        var status = await RemoteHostCalls.SubmitAsync(_runner, _nodeId, AgentRoutes.BackupMountSet, RemoteHostCalls.BackupMountTarget, body,
            (c, r, t) => c.SetBackupMountAsync(r, t), ct).ConfigureAwait(false);
        return await ResultOrReadAsync(status, ct).ConfigureAwait(false);
    }

    public async Task<HostAgentBackupMount> RemoveBackupMountAsync(CancellationToken ct = default)
    {
        var envelope = _runner.CreateEnvelope();
        var body = new BackupMountRemoveRequest { OperationId = envelope.OperationId, IssuedAt = envelope.IssuedAt, ExpiresAt = envelope.ExpiresAt };
        var status = await RemoteHostCalls.SubmitAsync(_runner, _nodeId, AgentRoutes.BackupMountRemove, RemoteHostCalls.BackupMountTarget, body,
            (c, r, t) => c.RemoveBackupMountAsync(r, t), ct).ConfigureAwait(false);
        return await ResultOrReadAsync(status, ct).ConfigureAwait(false);
    }

    /// <summary>Today's request → the protocol's settings (a class, so the password never reaches a generated <c>ToString</c>).</summary>
    public static BackupMountSettings ToSettings(HostAgentBackupMountRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new BackupMountSettings
        {
            Type = request.Type,
            Server = request.Server,
            Share = request.Share,
            Subfolder = request.Subfolder,
            Username = request.Username,
            Password = request.Password,
            Domain = request.Domain,
            SmbVersion = request.SmbVersion,
            NfsVersion = request.NfsVersion,
        };
    }

    /// <summary>hostd's mount as the agent forwards it → today's <see cref="HostAgentBackupMount"/>.</summary>
    public static HostAgentBackupMount ToMount(BackupMountInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new HostAgentBackupMount(
            info.Target, info.Unit, info.Configured, info.ForeignUnit, info.Type, info.Server, info.Share, info.Subfolder,
            info.Username, info.Domain, info.Version, info.Source, info.Options, info.Mounted, info.MountedSource,
            info.MountedType, info.TotalBytes, info.FreeBytes, info.ConfiguredAt, info.LastError, info.Warning);
    }

    private async Task<HostAgentBackupMount> ResultOrReadAsync(OperationStatus status, CancellationToken ct) =>
        status.ReadResult<BackupMountInfo>() is { } info ? ToMount(info) : await GetBackupMountAsync(ct).ConfigureAwait(false);
}
