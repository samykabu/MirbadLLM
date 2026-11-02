using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;

namespace MirbadLLM.Web.Api;

/// <summary>
/// Drives the "add model" dropdowns and folder auto-fill: the vLLM option catalog and checkpoint-folder inspection, on the
/// bootstrap node through its agent in the master composition (<see cref="ModelSourcing"/>). A folder read whose node
/// cannot answer is 503 with the reason (W4 review MN-096), never an empty list or a 404 that would hide the outage.
/// <para><b><c>GET /api/catalog</c> is the exception, and it is deliberate</b> (MN-239 item E):
/// <see cref="MirbadLLM.Orchestration.Multinode.RemoteVllmCatalogService"/> is master-local — the fixed option lists
/// plus the node's images from the status cache or its last capability report — so an approved node that is Offline or
/// unreachable answers <b>200</b> with the images it last reported, which is what lets the editor show them at all.
/// The <see cref="ApiNodeScope"/> refusals (400, 404, 409) still apply: they are decided before any report is read.
/// MN-237 recorded the gap between this and the "unreachable is 503" line in the operations docs; the docs now say so
/// (operations-p3p4 section 6), and <c>CatalogApiTests</c> pins the 200.</para>
/// <para>Per node (design §5.6, D106 follow-up, MN-229): every route takes an optional <c>?node=</c>; without it the only
/// fleet member answers, exactly as in P2, and with two or more members it is 400 <c>node_required</c>
/// (<see cref="ApiNodeScope"/>).</para>
/// <para><c>GET /api/model-folders/{folder}/gguf?file=&amp;node=</c> (design-p3p4 §A.1.10) answers the node's
/// <see cref="GgufSummary"/> through <see cref="IAgentClient.InspectGgufAsync"/>, only for a node that reports
/// <see cref="AgentFeatures.GgufInspect"/> (else 501 <c>not_supported</c>, nothing sent; the DGX reports P2's features
/// only). The agent's refusals keep their status: 400 (<c>file</c> ambiguous or not a plain name), 404 (no such folder or
/// file), 422 <c>gguf_invalid</c>; an unreachable or not serving node is 503.</para>
/// </summary>
public static class CatalogApiEndpoints
{
    public static RouteGroupBuilder MapCatalogApi(this RouteGroupBuilder group)
    {
        group.MapGet("/catalog", async (string? currentImage, string? node, IServiceProvider services, CancellationToken ct) =>
        {
            var scope = await ApiNodeScope.ResolveAsync(services, node, ct);
            if (scope.Refusal is { } refused)
            {
                return refused;
            }

            try
            {
                return Results.Ok(await ModelSourcing.GetCatalogAsync(services, scope.NodeId, currentImage, ct));
            }
            catch (NodeUnavailableException ex)
            {
                // Defensive only: the catalog is master-local (see the class remarks, MN-239 item E), so nothing on this
                // path throws it today. Kept so a future agent-backed catalog answers 503 and not 500, as the folder
                // routes do (MN-230) — not because an unreachable node reaches it now.
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/model-folders", async (string? node, IServiceProvider services, CancellationToken ct) =>
        {
            var scope = await ApiNodeScope.ResolveAsync(services, node, ct);
            if (scope.Refusal is { } refused)
            {
                return refused;
            }

            try
            {
                return Results.Ok(await ModelSourcing.ListFoldersAsync(services, scope.NodeId, ct));
            }
            catch (NodeUnavailableException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/model-folders/{folder}", async (string folder, string? node, IServiceProvider services, CancellationToken ct) =>
        {
            var scope = await ApiNodeScope.ResolveAsync(services, node, ct);
            if (scope.Refusal is { } refused)
            {
                return refused;
            }

            try
            {
                var inspection = await ModelSourcing.InspectAsync(services, scope.NodeId, folder, ct);
                return inspection is null ? Results.NotFound() : Results.Ok(inspection);
            }
            catch (NodeUnavailableException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/model-folders/{folder}/gguf", async (string folder, string? file, string? node, IServiceProvider services, CancellationToken ct) =>
        {
            var scope = await ApiNodeScope.ResolveAsync(services, node, ct);
            return scope.Refusal ?? await InspectGgufAsync(services, scope.NodeId!, folder, string.IsNullOrEmpty(file) ? null : file, ct);
        });

        return group;
    }

    private static async Task<IResult> InspectGgufAsync(IServiceProvider services, string nodeId, string folder, string? file, CancellationToken ct)
    {
        if (services.GetService<INodeStatusCache>()?.GetNode(nodeId) is { IsServing: false } live)
        {
            return Results.Problem(
                detail: $"Node {nodeId} is not serving (state: {live.State}); its GGUF files cannot be read now.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!await ApiNodeScope.ReportsAsync(services, nodeId, AgentFeatures.GgufInspect, ct))
        {
            return Problem(StatusCodes.Status501NotImplemented, ErrorCodes.NotSupported,
                $"Node '{nodeId}' cannot read GGUF headers (feature '{AgentFeatures.GgufInspect}').");
        }

        if (services.GetService<IAgentClientFactory>() is not { } clients)
        {
            return Problem(StatusCodes.Status501NotImplemented, ErrorCodes.NotSupported, "This master has no agent clients.");
        }

        try
        {
            return Results.Ok(await clients.GetClient(nodeId).InspectGgufAsync(folder, file, ct));
        }
        catch (AgentException ex)
        {
            int status = ErrorMappings.AgentStatus(ex.Code);
            return status is StatusCodes.Status400BadRequest or StatusCodes.Status404NotFound or StatusCodes.Status422UnprocessableEntity
                    or StatusCodes.Status501NotImplemented
                ? Problem(status, ex.Code, AgentErrorMapper.WithNode(ex.Error.Message, nodeId))
                : Results.Problem(detail: $"Node {nodeId} could not read the GGUF file: {ex.Error.Message}", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex) when (ex is AgentUnreachableException or KeyNotFoundException)
        {
            return Results.Problem(detail: $"Node {nodeId} could not be reached: {ex.Message}", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(detail: detail, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
