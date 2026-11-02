using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Runtimes;

namespace MirbadLLM.Agent.Operations;

/// <summary>
/// The agent's <c>/operations/*</c> routes (design §4.3, §4.4): status of one operation, the active (or active + recent)
/// list, and cancellation (<see cref="OperationCancellation"/>).
/// </summary>
public static class OperationEndpoints
{
    /// <summary>Maps the operation routes under <see cref="Protocol.BasePath"/>.</summary>
    public static IEndpointRouteBuilder MapAgentOperationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(Protocol.BasePath);

        group.MapGet(AgentRoutes.OperationGet.Path, (string operationId, IOperationLog log) =>
            log.Find(operationId) is { } status
                ? AgentHttp.Json(status)
                : AgentHttp.Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Operation " + operationId + " is not known on this node."));

        group.MapGet(AgentRoutes.OperationsList.Path, (string? state, IOperationLog log) =>
        {
            if (state is null)
            {
                IReadOnlyList<OperationStatus> all = [.. log.ListActive(), .. log.ListRecent(OperationRetention.StatusRecentCount)];
                return AgentHttp.Json(all);
            }

            return string.Equals(state, OperationsQuery.ActiveState, StringComparison.Ordinal)
                ? AgentHttp.Json(log.ListActive())
                : AgentHttp.Error(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, "Unknown state filter '" + state + "'.");
        });

        group.MapPost(AgentRoutes.OperationCancel.Path, async (string operationId, HttpContext http, OperationCancellation cancellation) =>
        {
            if (!AgentHttp.TryReadMaster(http.Request, out var master, out var headerError))
            {
                return AgentHttp.Error(headerError!);
            }

            var body = await AgentHttp.ReadMutationAsync<OperationCancelRequest>(http.Request, http.RequestAborted).ConfigureAwait(false);
            if (body.Error is not null)
            {
                return AgentHttp.Error(body.Error);
            }

            return await MutationResult.GuardAsync(() => cancellation.CancelAsync(operationId, body.Value!, body.Payload, master, http.RequestAborted)).ConfigureAwait(false);
        });

        return endpoints;
    }
}
