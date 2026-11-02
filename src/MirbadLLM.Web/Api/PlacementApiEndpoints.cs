using Microsoft.AspNetCore.Mvc;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Multinode;

namespace MirbadLLM.Web.Api;

/// <summary>
/// Placement endpoints of the admin API (design §5.6, §6.2; MN-108): the fit preview
/// <c>GET /api/nodes/{id}/fit?modelId=&amp;gpu=</c> and <c>POST /api/nodes/{id}/fit</c> with a <see cref="NodeFitRequest"/>,
/// plus the mapping of the registry validators' refusals used by the model POST/PUT/DELETE handlers. Admin-cookie
/// protected like the rest of <c>/api</c> (the fallback policy). Mapped by <see cref="AdminApiEndpoints.MapAdminApi"/>.
/// </summary>
public static class PlacementApiEndpoints
{
    /// <summary>Maps the fit preview on <paramref name="api"/> (the <c>/api</c> group).</summary>
    public static IEndpointRouteBuilder MapPlacementApi(this IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        // ?modelId= checks a stored model; ?gpu=<uuid> (repeatable) checks those GPUs instead of the model's own.
        api.MapGet("/nodes/{id}/fit", async (string id, string? modelId, string[]? gpu, IServiceProvider services, CancellationToken ct) =>
            await FitAsync(id, new NodeFitRequest { ModelId = modelId, GpuUuids = gpu is { Length: > 0 } ? gpu : null }, services, ct));

        api.MapPost("/nodes/{id}/fit", async (string id, [FromBody] NodeFitRequest request, IServiceProvider services, CancellationToken ct) =>
            await FitAsync(id, request, services, ct));

        return api;
    }

    private static async Task<IResult> FitAsync(string id, NodeFitRequest? request, IServiceProvider services, CancellationToken ct)
    {
        if (request is null)
        {
            return Results.Problem(detail: "The body must be a fit request object.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (services.GetService<INodeRegistry>() is not { } nodes)
        {
            return Results.Problem(detail: "The node registry is not available.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var fit = services.GetService<PlacementFit>() ?? new PlacementFit(
            nodes,
            services.GetRequiredService<IModelRegistry>(),
            services.GetService<INodeTelemetry>(),
            services.GetService<INodeStatusCache>(),
            services.GetService<TimeProvider>());
        try
        {
            var result = await fit.CheckAsync(id, request, ct);
            return result is null
                ? Results.Problem(detail: $"Node '{id}' does not exist.", statusCode: StatusCodes.Status404NotFound)
                : Results.Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>
    /// The answer for a model-registry refusal by the P2 validators (design D76, MN-108): 400 <c>placement_invalid</c> with
    /// the <c>problems</c> list, 409 <c>placement_changed</c>, 409 <c>model_busy</c> naming the holder. Null for any other
    /// exception (the caller keeps its own handling; a plain <see cref="ArgumentException"/> stays a 400 without a code).
    /// </summary>
    internal static IResult? RegistryRefusal(Exception ex) => ex switch
    {
        PlacementValidationException invalid => Results.Problem(
            detail: invalid.Message,
            statusCode: ErrorCodes.ApiStatus(ErrorCodes.PlacementInvalid),
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.PlacementInvalid, ["problems"] = invalid.Problems }),
        PlacementConflictException => Results.Problem(
            detail: ex.Message,
            statusCode: ErrorCodes.ApiStatus(ErrorCodes.PlacementChanged),
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.PlacementChanged }),
        ModelBusyException busy => Results.Problem(
            detail: busy.Message,
            statusCode: ErrorCodes.ApiStatus(ErrorCodes.ModelBusy),
            extensions: new Dictionary<string, object?>
            {
                ["code"] = ErrorCodes.ModelBusy,
                ["holder"] = busy.Holder is { } holder
                    ? new Dictionary<string, object?>
                    {
                        ["id"] = holder.Id,
                        ["kind"] = holder.Kind.ToString(),
                        ["description"] = ModelBusyException.DescribeHolder(holder),
                        ["stage"] = holder.Stage,
                    }
                    : null,
            }),
        _ => null,
    };
}
