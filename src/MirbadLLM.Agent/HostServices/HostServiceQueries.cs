using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;

namespace MirbadLLM.Agent.HostServices;

/// <summary>
/// The read-only host-service routes (design §4.3; not operations, §4.4 item 8): units and containers, one container,
/// GPU processes, the backup mount and its test. hostd answers in its own shape; failures become the agent error shape.
/// <para>The words name the node's service manager (<see cref="AgentHostServicesOptions.Manager"/>,
/// <see cref="HostServiceWording"/>, MN-239 item 1): hostd on Linux, the SCM on the Windows PC, which serves these very
/// routes. The codes are the same on both.</para>
/// </summary>
public sealed class HostServiceQueries(IHostdClient hostd, HostContainers? containers, AgentHostServicesOptions options)
{
    /// <summary>
    /// <c>GET /host/services[?include=all]</c>: hostd's units (plus discovery with <c>include=all</c>) and the node's
    /// non-denied containers. hostd or Docker being down is reported in the body, as the Services page shows it today.
    /// </summary>
    public async Task<HostServicesResponse> ListAsync(string? include, CancellationToken ct = default)
    {
        bool all = string.Equals(include, HostServicesQuery.IncludeAll, StringComparison.OrdinalIgnoreCase);
        var unitsTask = options.Systemd ? ReadUnitsAsync(all, ct) : Task.FromResult(new UnitPart(false, "This node's agent does not manage systemd units.", [], null));
        var containersTask = ReadContainersAsync(ct);
        var units = await unitsTask.ConfigureAwait(false);
        var (containersOk, containersError, rows) = await containersTask.ConfigureAwait(false);

        return new HostServicesResponse
        {
            HostdAvailable = units.Available,
            HostdError = units.Error,
            Units = units.Units,
            Discovered = all ? units.Discovery?.Discovered ?? [] : null,
            DiscoveryErrors = all ? units.Discovery?.DiscoveryErrors ?? [] : null,
            DenyPatterns = all ? units.Discovery?.DenyPatterns ?? [] : null,
            ContainersAvailable = containersOk,
            ContainersError = containersError,
            Containers = rows,
        };
    }

    /// <summary><c>GET /host/containers/{name}</c>.</summary>
    public async Task<HostCallResult> GetContainerAsync(string name, CancellationToken ct = default)
    {
        if (!options.Containers || containers is null)
        {
            return NotSupported("non-model containers");
        }

        try
        {
            var info = await containers.GetAsync(name, ct).ConfigureAwait(false);
            return info is null
                ? HostCallResult.Fail(ErrorCodes.NotFound, $"No container named '{name}'.")
                : HostCallResult.Ok(info);
        }
        catch (HostServiceDeniedException ex)
        {
            return HostCallResult.Fail(ErrorCodes.Denied, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HostCallResult.Fail(HostServiceErrors.FromDocker(ex, "read", name).Error);
        }
    }

    /// <summary><c>GET /host/gpu-processes</c>: hostd's list; 503 without hostd, 501 when hostd cannot report it.</summary>
    public async Task<HostCallResult> GetGpuProcessesAsync(CancellationToken ct = default)
    {
        if (!hostd.IsInstalled)
        {
            return HostdMissing();
        }

        var processes = await hostd.GetGpuProcessesAsync(ct).ConfigureAwait(false);
        return processes is null
            ? HostCallResult.Fail(ErrorCodes.NotSupported, HostServiceWording.NoGpuProcesses(options.Manager))
            : HostCallResult.Ok(processes);
    }

    /// <summary><c>GET /host/backup-mount</c>.</summary>
    public Task<HostCallResult> GetBackupMountAsync(CancellationToken ct = default) =>
        BackupAsync(async () => HostCallResult.Ok(await hostd.GetBackupMountAsync(ct).ConfigureAwait(false)));

    /// <summary><c>POST /host/backup-mount/test</c>: read-only, not an operation; the password passes through to hostd only.</summary>
    public Task<HostCallResult> TestBackupMountAsync(BackupMountTestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Settings is null)
        {
            return Task.FromResult(HostCallResult.Fail(ErrorCodes.InvalidRequest, "settings are required."));
        }

        return BackupAsync(async () => HostCallResult.Ok(await hostd.TestBackupMountAsync(request.Settings, ct).ConfigureAwait(false)));
    }

    private async Task<HostCallResult> BackupAsync(Func<Task<HostCallResult>> call)
    {
        if (!options.BackupMount)
        {
            return NotSupported("the backup mount");
        }

        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (HostServiceException ex)
        {
            // hostd's message is kept: the master turns hostd_unavailable back into HostServiceException(message), as today.
            return HostCallResult.Fail(HostdClient.IsTimeout(ex) ? ErrorCodes.Timeout : ErrorCodes.HostdUnavailable, ex.Message);
        }
    }

    private async Task<UnitPart> ReadUnitsAsync(bool all, CancellationToken ct)
    {
        try
        {
            if (all)
            {
                var discovery = await hostd.DiscoverAsync(ct).ConfigureAwait(false);
                return new UnitPart(true, null, discovery.Services, discovery);
            }

            return new UnitPart(true, null, await hostd.ListAsync(ct).ConfigureAwait(false), null);
        }
        catch (HostServiceException ex)
        {
            return new UnitPart(false, ex.Message, [], null);
        }
    }

    private async Task<(bool Ok, string? Error, IReadOnlyList<HostContainerInfo> Rows)> ReadContainersAsync(CancellationToken ct)
    {
        if (!options.Containers || containers is null)
        {
            return (false, "This node's agent does not manage containers.", []);
        }

        try
        {
            return (true, null, await containers.ListAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TaskCanceledException)
        {
            return (false, $"Could not list containers: {ex.Message}", []);
        }
    }

    private HostCallResult HostdMissing() =>
        HostCallResult.Fail(ErrorCodes.HostdUnavailable, HostServiceWording.NotInstalled(options.Manager));

    private static HostCallResult NotSupported(string what) =>
        HostCallResult.Fail(ErrorCodes.NotSupported, $"This node's agent does not manage {what}.");

    private sealed record UnitPart(bool Available, string? Error, IReadOnlyList<HostUnit> Units, HostUnitDiscovery? Discovery);
}
