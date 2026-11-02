using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.HostServices;

namespace MirbadLLM.Agent.Api;

/// <summary>
/// The agent's <c>/host/*</c> routes (design §4.3 host services, catalogue <see cref="AgentRoutes"/>): reads forward to
/// hostd and Docker; every mutation is an operation (<see cref="HostServiceOperations"/>) answered synchronously — 200
/// with the <see cref="OperationStatus"/> when it succeeded, the agent error otherwise (Unknown outcomes included).
/// Paths are relative: map them on the agent's <see cref="Protocol.BasePath"/> group
/// (<c>app.MapGroup(Protocol.BasePath).MapHostServiceEndpoints()</c>). Protocol-header and request-time checks belong to
/// the agent host (MN-014); the master triple and the envelope are checked here.
/// </summary>
public static class HostServiceEndpoints
{
    private const string NameParameter = "name";

    /// <summary>Maps every host-service route of <see cref="AgentRoutes"/>.</summary>
    public static IEndpointRouteBuilder MapHostServiceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        Map(endpoints, AgentRoutes.HostServicesList, async (http, ct) =>
            HostCallResult.Ok(await Queries(http).ListAsync(http.Request.Query["include"].ToString(), ct).ConfigureAwait(false)));
        Map(endpoints, AgentRoutes.HostGpuProcesses, (http, ct) => Queries(http).GetGpuProcessesAsync(ct));
        Map(endpoints, AgentRoutes.HostContainerGet, (http, ct) => Queries(http).GetContainerAsync(RouteName(http), ct));
        Map(endpoints, AgentRoutes.BackupMountGet, (http, ct) => Queries(http).GetBackupMountAsync(ct));
        Map(endpoints, AgentRoutes.BackupMountTest, async (http, ct) =>
        {
            var (request, error) = await ReadAsync<BackupMountTestRequest>(http, ct).ConfigureAwait(false);
            return request is null ? error! : await Queries(http).TestBackupMountAsync(request, ct).ConfigureAwait(false);
        });

        MapMutation(endpoints, AgentRoutes.HostServiceStart, RouteName);
        MapMutation(endpoints, AgentRoutes.HostServiceStop, RouteName);
        MapMutation(endpoints, AgentRoutes.HostServiceRestart, RouteName);
        MapMutation(endpoints, AgentRoutes.HostServiceAutostart, RouteName);
        MapMutation(endpoints, AgentRoutes.HostWhitelistAdd, null);
        MapMutation(endpoints, AgentRoutes.HostWhitelistUpdate, RouteName);
        MapMutation(endpoints, AgentRoutes.HostWhitelistRemove, RouteName);
        MapMutation(endpoints, AgentRoutes.HostContainerStart, RouteName);
        MapMutation(endpoints, AgentRoutes.HostContainerStop, RouteName);
        MapMutation(endpoints, AgentRoutes.HostContainerRestart, RouteName);
        MapMutation(endpoints, AgentRoutes.HostContainerAutostart, RouteName);
        MapMutation(endpoints, AgentRoutes.BackupMountSet, _ => HostMutation.BackupMountTarget);
        MapMutation(endpoints, AgentRoutes.BackupMountRemove, _ => HostMutation.BackupMountTarget);
        return endpoints;
    }

    private static void MapMutation(IEndpointRouteBuilder endpoints, AgentRoute route, Func<HttpContext, string>? target)
    {
        string kind = route.OperationKind ?? throw new InvalidOperationException($"{route.Name} is not a mutation.");
        Map(endpoints, route, async (http, ct) =>
        {
            JsonElement body;
            try
            {
                using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct).ConfigureAwait(false);
                body = document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                return HostCallResult.Fail(ErrorCodes.InvalidRequest, $"The body is not valid JSON: {ex.Message}");
            }

            AgentMutation? request;
            try
            {
                request = body.Deserialize(route.RequestType, AgentJson.Options) as AgentMutation;
            }
            catch (JsonException ex)
            {
                return HostCallResult.Fail(ErrorCodes.InvalidRequest, $"The body does not match {route.RequestType.Name}: {ex.Message}");
            }

            if (request is null)
            {
                return HostCallResult.Fail(ErrorCodes.InvalidRequest, $"A {route.RequestType.Name} body is required.");
            }

            string name = target?.Invoke(http) ?? (request is HostWhitelistAddRequest add ? add.Unit.Trim() : "");
            var operations = http.RequestServices.GetRequiredService<HostServiceOperations>();
            return await operations.SubmitAsync(new HostMutation(kind, name, request), http.Request.Headers[Protocol.MasterHeader].ToString(), body, ct)
                .ConfigureAwait(false);
        });
    }

    private static void Map(IEndpointRouteBuilder endpoints, AgentRoute route, Func<HttpContext, CancellationToken, Task<HostCallResult>> handler) =>
        endpoints.MapMethods(route.Path, [route.Method], async (HttpContext http) =>
        {
            var result = await handler(http, http.RequestAborted).ConfigureAwait(false);
            return Results.Json(result.Body, AgentJson.Options, AgentJson.JsonContentType, result.StatusCode);
        }).WithName("agent." + route.Name);

    private static async Task<(T? Request, HostCallResult? Error)> ReadAsync<T>(HttpContext http, CancellationToken ct)
        where T : class
    {
        try
        {
            var request = await JsonSerializer.DeserializeAsync<T>(http.Request.Body, AgentJson.Options, ct).ConfigureAwait(false);
            return request is null
                ? (null, HostCallResult.Fail(ErrorCodes.InvalidRequest, $"A {typeof(T).Name} body is required."))
                : (request, null);
        }
        catch (JsonException ex)
        {
            return (null, HostCallResult.Fail(ErrorCodes.InvalidRequest, $"The body does not match {typeof(T).Name}: {ex.Message}"));
        }
    }

    private static HostServiceQueries Queries(HttpContext http) => http.RequestServices.GetRequiredService<HostServiceQueries>();

    private static string RouteName(HttpContext http) => http.Request.RouteValues[NameParameter] as string ?? "";
}
