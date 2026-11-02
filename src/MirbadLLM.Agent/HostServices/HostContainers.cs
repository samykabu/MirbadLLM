using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;

namespace MirbadLLM.Agent.HostServices;

/// <summary>
/// Non-model containers on the node (design §4.3 host containers) through <see cref="DockerServiceClient"/>, with the
/// agent's deny rules (<see cref="HostContainerRules"/>) applied to every list, read and action.
/// </summary>
public sealed class HostContainers(DockerServiceClient docker, AgentHostServicesOptions options)
{
    /// <summary>Every container except denied ones, with inspect details and current memory (running only).</summary>
    public async Task<IReadOnlyList<HostContainerInfo>> ListAsync(CancellationToken ct = default)
    {
        var all = await docker.ListAsync(ct).ConfigureAwait(false);
        var candidates = all.Where(c => c.Name.Length > 0 && !HostContainerRules.IsDenied(c.Name, c.Labels, options)).ToList();
        var rows = await Task.WhenAll(candidates.Select(async summary =>
        {
            try
            {
                var details = await docker.InspectAsync(summary.Name, ct).ConfigureAwait(false);
                if (details is null || HostContainerRules.IsDenied(details.Name, details.Labels, options))
                {
                    return null;
                }

                return await ToInfoAsync(details, summary.Status, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException)
            {
                // Keep the row from the list so the page still shows it; details stay empty.
                return new HostContainerInfo
                {
                    Id = summary.Id,
                    Name = summary.Name,
                    State = summary.State,
                    Status = summary.Status,
                    Image = summary.Image,
                    Labels = summary.Labels,
                };
            }
        })).ConfigureAwait(false);

        return [.. rows.Where(r => r is not null).Select(r => r!)];
    }

    /// <summary>One container. Null when it does not exist.</summary>
    /// <exception cref="HostServiceDeniedException">The container is the agent's, the master's, a model or a download helper.</exception>
    public async Task<HostContainerInfo?> GetAsync(string name, CancellationToken ct = default)
    {
        var details = await InspectAllowedAsync(name, ct).ConfigureAwait(false);
        return details is null ? null : await ToInfoAsync(details, status: null, ct).ConfigureAwait(false);
    }

    /// <summary>Inspect, refusing denied containers. Null when it does not exist.</summary>
    /// <exception cref="HostServiceDeniedException">Denied by <see cref="HostContainerRules"/>.</exception>
    public async Task<ContainerDetails?> InspectAllowedAsync(string name, CancellationToken ct = default)
    {
        if (HostContainerRules.IsDenied(name, labels: null, options))
        {
            throw Denied(name);
        }

        var details = await docker.InspectAsync(name, ct).ConfigureAwait(false);
        if (details is not null && HostContainerRules.IsDenied(details.Name, details.Labels, options))
        {
            throw Denied(name);
        }

        return details;
    }

    /// <summary>Starts a container (already running is fine).</summary>
    public Task StartAsync(ContainerDetails details, CancellationToken ct = default) => docker.StartAsync(Id(details), ct);

    /// <summary>Stops a container (already stopped is fine).</summary>
    public Task StopAsync(ContainerDetails details, int timeoutSeconds, CancellationToken ct = default) =>
        docker.StopAsync(Id(details), timeoutSeconds, ct);

    /// <summary>Restarts a container.</summary>
    public Task RestartAsync(ContainerDetails details, int timeoutSeconds, CancellationToken ct = default) =>
        docker.RestartAsync(Id(details), timeoutSeconds, ct);

    /// <summary>Sets the restart policy.</summary>
    public Task SetRestartPolicyAsync(ContainerDetails details, string policy, CancellationToken ct = default) =>
        docker.UpdateRestartPolicyAsync(Id(details), policy, ct);

    /// <summary>Restart policies Docker accepts and the Services page uses.</summary>
    public static IReadOnlyList<string> RestartPolicies { get; } = ["no", "always", "unless-stopped", "on-failure"];

    /// <summary>Docker's empty policy means <c>no</c>.</summary>
    public static string NormalizePolicy(string? policy) => string.IsNullOrEmpty(policy) ? "no" : policy;

    /// <summary>True when the container runs (or is restarting).</summary>
    public static bool IsRunning(string? state) => state is "running" or "restarting";

    /// <summary>The contract row for inspect details (+ memory when running).</summary>
    public async Task<HostContainerInfo> ToInfoAsync(ContainerDetails details, string? status, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        long? memory = null;
        if (IsRunning(details.Status))
        {
            try
            {
                memory = await docker.GetMemoryUsageAsync(Id(details), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException)
            {
                memory = null;
            }
        }

        return new HostContainerInfo
        {
            Id = details.Id,
            Name = details.Name,
            State = details.Status,
            Status = status ?? StatusText(details),
            Image = details.Image,
            Labels = details.Labels,
            RestartPolicy = NormalizePolicy(details.RestartPolicy),
            Health = details.Health,
            ExitCode = details.ExitCode,
            OomKilled = details.OomKilled,
            StartedAt = details.StartedAt,
            MemoryBytes = memory,
        };
    }

    /// <summary>Today's Services page detail text: <c>exited (1)</c>, <c>running (healthy)</c>, or Docker's state.</summary>
    public static string StatusText(ContainerDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (details.Status == "exited")
        {
            return $"exited ({details.ExitCode})";
        }

        return details.Health is { Length: > 0 } health && details.Status == "running" ? $"running ({health})" : details.Status;
    }

    private static string Id(ContainerDetails details) => details.Id.Length > 0 ? details.Id : details.Name;

    private static HostServiceDeniedException Denied(string name) =>
        new($"'{name}' cannot be managed here: mirbadllm, the agent, model and download containers never are.");
}
