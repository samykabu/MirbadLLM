using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Agent.HostServices;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Updates;

/// <summary>
/// The helper's report (docs/multinode/agent-updates.md U7): <c>key=value</c> lines (<c>operation</c>, <c>exit</c>,
/// <c>version</c>), then <c>log:</c> and the last lines of the installer's output.
/// </summary>
public sealed record HelperResult(string? OperationId, int? ExitCode, string? Version, string Log)
{
    public static HelperResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string? operation = null;
        string? version = null;
        int? exit = null;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int logStart = lines.Length;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line == "log:")
            {
                logStart = i + 1;
                break;
            }

            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            string value = line[(eq + 1)..].Trim();
            switch (line[..eq])
            {
                case "operation":
                    operation = value;
                    break;
                case "version":
                    version = value;
                    break;
                case "exit" when int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int code):
                    exit = code;
                    break;
            }
        }

        string log = string.Join('\n', lines[logStart..]).Trim();
        return new HelperResult(operation, exit, version, log.Length > 4000 ? log[^4000..] : log);
    }
}

/// <summary>
/// Closes pushed updates (docs/multinode/agent-updates.md U7). Every few seconds, for an active <c>agentUpdate</c>
/// operation: when this agent already runs the ordered build, Succeeded; when the helper's result names it with a
/// failure (signature refused, installer rolled back), Failed with the installer's last lines; when neither happens
/// within <see cref="AgentUpdateOptions.ResultWait"/> of the hand-over, Failed. The installer restarts the agent, so a
/// success is usually seen by the new agent right after its start.
/// </summary>
public sealed partial class AgentUpdateWatcher(
    AgentUpdateService updates,
    IOperationLog log,
    TimeProvider time,
    ILogger<AgentUpdateWatcher> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await CheckAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCheckFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>One pass over the active updates.</summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        foreach (var operation in log.ListActive().Where(o => o.Kind == OperationKinds.AgentUpdate))
        {
            string target = operation.Target;
            var spool = updates.Options.Spool;
            string resultFile = Path.Combine(spool, AgentUpdateOptions.ResultFile);
            var result = File.Exists(resultFile) ? HelperResult.Parse(await File.ReadAllTextAsync(resultFile, ct).ConfigureAwait(false)) : null;
            if (result is not null && result.OperationId != operation.Id)
            {
                result = null; // a report of an older update
            }

            if (Matches(updates.RunningRevision, target))
            {
                await CompleteAsync(operation.Id, OperationState.Succeeded, null, result?.Log, ct).ConfigureAwait(false);
                continue;
            }

            if (result is { ExitCode: not null and not 0 })
            {
                await CompleteAsync(operation.Id, OperationState.Failed,
                    $"The update helper reported exit code {result.ExitCode}: {LastLine(result.Log)}", result.Log, ct).ConfigureAwait(false);
                continue;
            }

            if (operation.Stage == OperationStages.Dispatched && time.GetUtcNow() - operation.StartedAt > updates.Options.ResultWait)
            {
                await CompleteAsync(operation.Id, OperationState.Failed, result is { ExitCode: 0 }
                    ? "The installer finished, but this agent still runs its old build."
                    : $"The update helper did not report within {updates.Options.ResultWait.TotalMinutes:0} minutes; check that it is installed (install-agent upgrade).", result?.Log, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>True when <paramref name="running"/> is the ordered revision (a short and a long form match).</summary>
    public static bool Matches(string? running, string? target) =>
        !string.IsNullOrEmpty(running) && !string.IsNullOrEmpty(target)
        && (running.StartsWith(target, StringComparison.OrdinalIgnoreCase) || target.StartsWith(running, StringComparison.OrdinalIgnoreCase));

    private async Task CompleteAsync(string id, OperationState state, string? error, string? helperLog, CancellationToken ct)
    {
        var result = AgentJson.ToElement(new AgentUpdateResult { AgentVersion = updates.RunningVersion, Log = string.IsNullOrEmpty(helperLog) ? null : helperLog });
        await log.CompleteAsync(id, state, error is null ? null : AgentErrorResults.Create(ErrorCodes.InternalError, error), result, ct).ConfigureAwait(false);
        foreach (string name in new[] { AgentUpdateOptions.OrderFile, AgentUpdateOptions.SignatureFile, AgentUpdateOptions.OperationFile, "bundle.tar.gz", "bundle.zip" })
        {
            try
            {
                File.Delete(Path.Combine(updates.Options.Spool, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The next update overwrites it.
            }
        }

        LogCompleted(logger, id, state, error ?? "");
    }

    private static string LastLine(string log) =>
        log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no output";

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent update {OperationId} completed: {State} {Error}")]
    private static partial void LogCompleted(ILogger logger, string operationId, OperationState state, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking agent updates failed.")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);
}

/// <summary>
/// An update a restart left open (the installer restarted the agent, or it crashed): one interrupted before the hand-over
/// is Failed; a dispatched one is left to <see cref="AgentUpdateWatcher"/>, which settles it from the running build and
/// the helper's report.
/// </summary>
public sealed class AgentUpdateRecoveryHandler(IOperationLog log) : IOperationRecoveryHandler
{
    public IReadOnlyList<string> Kinds { get; } = [OperationKinds.AgentUpdate];

    public async Task RecoverAsync(RecoverableOperation operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.Status.Stage != OperationStages.Dispatched)
        {
            await log.CompleteAsync(operation.Status.Id, OperationState.Failed,
                AgentErrorResults.Create(ErrorCodes.InternalError, "The agent restarted before the update was handed to the helper; nothing was installed. Run it again."),
                null, ct).ConfigureAwait(false);
        }
    }
}

/// <summary><c>agent.update</c> in the capability report while the update helper is installed.</summary>
public sealed class AgentUpdateContributor(AgentUpdateOptions options) : ICapabilityContributor
{
    public Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        return Task.FromResult(options.HelperInstalled && !current.Features.Contains(AgentFeatures.AgentUpdate, StringComparer.Ordinal)
            ? current with { Features = [.. current.Features, AgentFeatures.AgentUpdate] }
            : current);
    }
}

/// <summary>Registration and the route of the pushed agent update.</summary>
public static class AgentUpdateServiceCollectionExtensions
{
    public const string HttpClientName = "mirbadllm-agent.update";

    public static IServiceCollection AddAgentUpdates(this IServiceCollection services, string stateDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(new AgentUpdateOptions
        {
            Spool = Path.Combine(stateDirectory, "update"),
            HelperMarker = AgentUpdateOptions.DefaultHelperMarker(),
        });
        services.AddHttpClient(HttpClientName, client => client.Timeout = TimeSpan.FromMinutes(15));
        services.AddSingleton(sp => new AgentUpdateService(
            sp.GetRequiredService<AgentUpdateOptions>(),
            sp.GetRequiredService<IOperationLog>(),
            sp.GetRequiredService<IFencingState>(),
            sp.GetRequiredService<IAgentCredentials>(),
            sp.GetRequiredService<AgentBootInfo>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
            sp.GetRequiredService<ILogger<AgentUpdateService>>()));
        services.AddSingleton<AgentUpdateWatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<AgentUpdateWatcher>());
        services.AddSingleton<IOperationRecoveryHandler, AgentUpdateRecoveryHandler>();
        services.AddSingleton<ICapabilityContributor, AgentUpdateContributor>();
        return services;
    }

    public static IEndpointRouteBuilder MapAgentUpdateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var route = AgentRoutes.AgentUpdate;
        endpoints.MapMethods(route.Path, [route.Method], async (HttpContext http) =>
        {
            HostCallResult result;
            JsonElement body;
            try
            {
                using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted).ConfigureAwait(false);
                body = document.RootElement.Clone();
                var request = body.Deserialize<AgentUpdateRequest>(AgentJson.Options);
                result = request is null
                    ? HostCallResult.Fail(ErrorCodes.InvalidRequest, "An AgentUpdateRequest body is required.")
                    : await http.RequestServices.GetRequiredService<AgentUpdateService>().SubmitAsync(
                        request, http.Request.Headers[Protocol.MasterHeader].ToString(), body, http.Connection.RemoteIpAddress?.MapToIPv4IfMapped(), http.RequestAborted).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                result = HostCallResult.Fail(ErrorCodes.InvalidRequest, $"The body does not match AgentUpdateRequest: {ex.Message}");
            }

            return Results.Json(result.Body, AgentJson.Options, AgentJson.JsonContentType, result.StatusCode);
        }).WithName("agent." + route.Name);
        return endpoints;
    }

    private static System.Net.IPAddress MapToIPv4IfMapped(this System.Net.IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
