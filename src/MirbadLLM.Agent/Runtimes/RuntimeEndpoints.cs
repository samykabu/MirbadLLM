using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Runtimes;

/// <summary>
/// The agent's <c>/runtimes/*</c> routes (design §4.3, <see cref="AgentRoutes"/>): list, get, launch, stop, launch-config
/// and logs. Protocol header, request-time skew and the mTLS peer are checked by the host (MN-014) before these run;
/// fencing, idempotency and admission are applied here through <see cref="RuntimeOperations"/>.
/// </summary>
public static class RuntimeEndpoints
{
    /// <summary>Maps the runtime routes under <see cref="Protocol.BasePath"/>.</summary>
    public static IEndpointRouteBuilder MapAgentRuntimeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(Protocol.BasePath);

        group.MapGet(AgentRoutes.RuntimesList.Path, async (string? label, RuntimeOperations runtimes, CancellationToken ct) =>
        {
            if (label is not null && !string.Equals(label, RuntimesQuery.TuningLabel, StringComparison.Ordinal))
            {
                return AgentHttp.Error(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, "Unknown label filter '" + label + "'.");
            }

            return await ReadAsync(async () =>
            {
                var all = await runtimes.ListAsync(ct).ConfigureAwait(false);
                IReadOnlyList<RuntimeStatus> result = label is null ? all : [.. all.Where(r => r.Tuning)];
                return AgentHttp.Json(result);
            }).ConfigureAwait(false);
        });

        group.MapGet(AgentRoutes.RuntimeGet.Path, (string modelId, RuntimeOperations runtimes, CancellationToken ct) =>
            ReadAsync(async () =>
            {
                var instance = await runtimes.ObserveAsync(modelId, ct).ConfigureAwait(false);
                return instance is null ? NotRunning(modelId) : AgentHttp.Json(instance);
            }));

        group.MapPost(AgentRoutes.RuntimeLaunch.Path, async (string modelId, HttpContext http, RuntimeOperations runtimes) =>
        {
            if (!AgentHttp.TryReadMaster(http.Request, out var master, out var headerError))
            {
                return AgentHttp.Error(headerError!);
            }

            var body = await AgentHttp.ReadMutationAsync<LaunchRequest>(http.Request, http.RequestAborted).ConfigureAwait(false);
            if (body.Error is not null)
            {
                return AgentHttp.Error(body.Error);
            }

            return await MutationResult.GuardAsync(() => runtimes.LaunchAsync(modelId, body.Value!, body.Payload, master, http.RequestAborted)).ConfigureAwait(false);
        });

        group.MapPost(AgentRoutes.RuntimeStop.Path, async (string modelId, HttpContext http, RuntimeOperations runtimes) =>
        {
            if (!AgentHttp.TryReadMaster(http.Request, out var master, out var headerError))
            {
                return AgentHttp.Error(headerError!);
            }

            var body = await AgentHttp.ReadMutationAsync<StopRequest>(http.Request, http.RequestAborted).ConfigureAwait(false);
            if (body.Error is not null)
            {
                return AgentHttp.Error(body.Error);
            }

            return await MutationResult.GuardAsync(() => runtimes.StopAsync(modelId, body.Value!, body.Payload, master, http.RequestAborted)).ConfigureAwait(false);
        });

        group.MapGet(AgentRoutes.RuntimeLaunchConfig.Path, (string modelId, RuntimeOperations runtimes, CancellationToken ct) =>
            ReadAsync(async () =>
            {
                var (driver, instance) = await runtimes.FindAsync(modelId, ct).ConfigureAwait(false);
                if (driver is null || instance is null)
                {
                    return NotRunning(modelId);
                }

                var config = await driver.GetLaunchConfigAsync(modelId, ct).ConfigureAwait(false);
                return config is null ? NotRunning(modelId) : AgentHttp.Json(config);
            }));

        group.MapGet(AgentRoutes.RuntimeLogs.Path, async (string modelId, int? tail, bool? follow, HttpContext http, RuntimeOperations runtimes) =>
        {
            var ct = http.RequestAborted;
            NodeRuntime.Abstractions.IRuntimeDriver? driver;
            try
            {
                (driver, _) = await runtimes.FindAsync(modelId, ct).ConfigureAwait(false);
            }
            catch (AgentException ex)
            {
                await AgentHttp.Error(ex).ExecuteAsync(http).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (RuntimeOperations.IsDriverUnavailable(ex))
            {
                await AgentHttp.Error(StatusCodes.Status503ServiceUnavailable, RuntimeOperations.DriverError(ex)).ExecuteAsync(http).ConfigureAwait(false);
                return;
            }

            if (driver is null)
            {
                await NotRunning(modelId).ExecuteAsync(http).ConfigureAwait(false);
                return;
            }

            var defaults = new LogsQuery();
            http.Response.ContentType = AgentJson.EventStreamContentType;
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            try
            {
                await foreach (string line in driver.StreamLogsAsync(modelId, tail ?? defaults.Tail, follow ?? defaults.Follow, ct).ConfigureAwait(false))
                {
                    await http.Response.WriteAsync("data: " + line.TrimEnd('\r', '\n') + "\n\n", ct).ConfigureAwait(false);
                    await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The client went away.
            }
        });

        return endpoints;
    }

    private static IResult NotRunning(string modelId) =>
        AgentHttp.Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Model " + modelId + " has no instance on this node (Stopped).");

    private static async Task<IResult> ReadAsync(Func<Task<IResult>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (AgentException ex)
        {
            return AgentHttp.Error(ex);
        }
        catch (Exception ex) when (RuntimeOperations.IsDriverUnavailable(ex))
        {
            return AgentHttp.Error(StatusCodes.Status503ServiceUnavailable, RuntimeOperations.DriverError(ex));
        }
    }
}
