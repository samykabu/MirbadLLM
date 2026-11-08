using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Nodes;

namespace MirbadLLM.Web.Api;

/// <summary>The admin REST API (<c>/api/*</c>): model CRUD, start/stop, live logs and the status snapshot.</summary>
public static class AdminApiEndpoints
{
    public static IEndpointRouteBuilder MapAdminApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        // ?tag=embedding&tag=vision returns only the models that carry every listed tag.
        api.MapGet("/models", async (string[]? tag, IModelRegistry registry, CancellationToken ct) =>
        {
            var models = await registry.ListAsync(ct);
            var required = (tag ?? []).Select(ModelTags.Normalize).OfType<string>().ToList();
            return Results.Ok(required.Count == 0 ? models : models.Where(m => ModelTags.HasAll(m, required)).ToList());
        });

        api.MapGet("/models/{id}", async (string id, IModelRegistry registry, CancellationToken ct) =>
        {
            var model = await registry.GetAsync(id, ct);
            return model is null ? Results.NotFound() : Results.Ok(model);
        });

        // Design §5.6: without a placement the model goes to the only node; with several nodes the caller must choose.
        api.MapPost("/models", async (
            ModelDefinition model,
            IModelRegistry registry,
            IServiceProvider services,
            IOptions<RegistryMigrationOptions> migration,
            CancellationToken ct) =>
        {
            var placed = await DefaultPlacement.ApplyAsync(model, services.GetService<INodeRegistry>(), migration.Value.EffectiveBootstrapNodeId, ct);
            if (placed is null)
            {
                return Results.Problem(
                    detail: "More than one node exists; give the model a placement (\"placement\": {\"nodeId\": \"…\"}).",
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.PlacementRequired });
            }

            model = placed;
            try
            {
                await registry.UpsertAsync(model, ct);

                // The registry normalizes (e.g. tags to lowercase slugs); answer with what it stored.
                return Results.Created($"/api/models/{model.Id}", await registry.GetAsync(model.Id, ct) ?? model);
            }
            catch (Exception ex) when (PlacementApiEndpoints.RegistryRefusal(ex) is { } refusal)
            {
                return refusal; // the P2 validators (MN-108): placement_invalid, placement_changed, model_busy
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // Design §5.4: placement, runtime, llamaCpp, hostMemoryBudgetGiB and keepServingDuringJobs are merge-on-absent
        // (an old client that does not know them keeps the stored values); an explicit "placement": null is refused
        // (use /unplace), and a placement on another node is refused (use /move), so a PUT never erases or reassigns
        // a placement. The body is read raw because a bound record cannot tell "absent" from "default".
        api.MapPut("/models/{id}", async (string id, HttpRequest request, IModelRegistry registry, IOptions<JsonOptions> jsonOptions, CancellationToken ct) =>
        {
            if (!request.HasJsonContentType())
            {
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            }

            ModelDefinition? model;
            HashSet<string> present;
            try
            {
                using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
                if (body.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return Results.Problem(detail: "The body must be a model definition object.", statusCode: StatusCodes.Status400BadRequest);
                }

                present = body.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (body.RootElement.EnumerateObject().Any(p =>
                        string.Equals(p.Name, PlacementField, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Null))
                {
                    return Results.Problem(
                        detail: $"\"placement\": null would unplace the model; use POST /api/models/{id}/unplace.",
                        statusCode: StatusCodes.Status400BadRequest,
                        extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.UseUnplace });
                }

                model = body.RootElement.Deserialize<ModelDefinition>(jsonOptions.Value.SerializerOptions);
            }
            catch (JsonException ex)
            {
                return Results.Problem(detail: "Invalid model definition JSON: " + ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }

            if (model is null)
            {
                return Results.Problem(detail: "The body must be a model definition object.", statusCode: StatusCodes.Status400BadRequest);
            }

            var existing = await registry.GetAsync(id, ct);
            if (existing is null)
            {
                return Results.NotFound();
            }

            if (present.Contains(PlacementField) && !string.Equals(model.Placement?.NodeId, existing.Placement?.NodeId, StringComparison.Ordinal))
            {
                return Results.Problem(
                    detail: existing.Placement is null
                        ? $"Model '{id}' is unplaced; place it with POST /api/models/{id}/move."
                        : $"Model '{id}' is placed on node '{existing.Placement.NodeId}'; changing the node is a move (POST /api/models/{id}/move).",
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.UseMove });
            }

            var toSave = MergeAbsentFields(model.Id == id ? model : model with { Id = id }, existing, present);
            try
            {
                // The read above and the write are not atomic (W1 review MN-090). With the P2 validators composed (MN-108,
                // D75) the write is refused with 409 placement_changed when the stored placement is no longer the one
                // read here (moved, unplaced, placed, GPUs changed or the model deleted meanwhile), or when the body
                // carries an older placement generation. Without validators the scope has no effect.
                using (ModelWriteScope.ExpectStoredPlacement(existing.Placement))
                {
                    await registry.UpsertAsync(toSave, ct);
                }

                return Results.Ok(await registry.GetAsync(id, ct) ?? toSave);
            }
            catch (Exception ex) when (PlacementApiEndpoints.RegistryRefusal(ex) is { } refusal)
            {
                return refusal;
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        api.MapDelete("/models/{id}", async (string id, IModelRegistry registry, CancellationToken ct) =>
        {
            try
            {
                return await registry.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound();
            }
            catch (ModelBusyException ex)
            {
                return PlacementApiEndpoints.RegistryRefusal(ex)!; // an ownership record holds the model (MN-108)
            }
        });

        api.MapPost("/models/{id}/start", async (string id, bool? force, IModelRegistry registry, IModelRuntime runtime, IServiceProvider services, CancellationToken ct) =>
        {
            if (await registry.GetAsync(id, ct) is null)
            {
                return Results.NotFound();
            }

            try
            {
                // A model a move, job or restore holds refuses plain starts (design §6.7, MN-130).
                await HeldModelGuard.EnsureNotHeldAsync(services.GetService<IOwnershipStore>(), id, ct);
                var status = await runtime.StartAsync(id, force ?? false, ct);
                return Results.Ok(status);
            }
            catch (ModelBusyException ex)
            {
                return PlacementApiEndpoints.RegistryRefusal(ex)!;
            }
            catch (ModelMemoryAdmissionException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
            catch (ModelStartConflictException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
            catch (Exception ex) when (NodeProblem(ex) is { } problem)
            {
                return problem;
            }
        });

        api.MapPost("/models/{id}/restart", async (string id, bool? force, IModelRegistry registry, IModelRuntime runtime, IServiceProvider services, CancellationToken ct) =>
        {
            if (await registry.GetAsync(id, ct) is null)
            {
                return Results.NotFound();
            }

            try
            {
                // A model a move, job or restore holds refuses plain starts (design §6.7, MN-130).
                await HeldModelGuard.EnsureNotHeldAsync(services.GetService<IOwnershipStore>(), id, ct);
                return Results.Ok(await runtime.RestartAsync(id, force ?? false, ct));
            }
            catch (ModelBusyException ex)
            {
                return PlacementApiEndpoints.RegistryRefusal(ex)!;
            }
            catch (ModelMemoryAdmissionException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
            catch (ModelStartConflictException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
            catch (Exception ex) when (NodeProblem(ex) is { } problem)
            {
                return problem;
            }
        });

        api.MapPost("/models/{id}/stop", async (string id, IModelRegistry registry, IModelRuntime runtime, IServiceProvider services, CancellationToken ct) =>
        {
            if (await registry.GetAsync(id, ct) is null)
            {
                return Results.NotFound();
            }

            try
            {
                // A model a move, job or restore holds refuses plain stops (design §6.7, MN-130).
                await HeldModelGuard.EnsureNotHeldAsync(services.GetService<IOwnershipStore>(), id, ct);
                services.GetService<ModelStartService>()?.Cancel(id);
                await runtime.StopAsync(id, ct);
            }
            catch (ModelBusyException ex)
            {
                return PlacementApiEndpoints.RegistryRefusal(ex)!;
            }
            catch (Exception ex) when (NodeProblem(ex) is { } problem)
            {
                return problem;
            }

            return Results.NoContent();
        });

        api.MapGet("/models/{id}/logs", async (string id, int? tail, HttpContext http, IModelRegistry registry, IModelRuntime runtime) =>
        {
            if (await registry.GetAsync(id, http.RequestAborted) is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            await foreach (var line in runtime.StreamLogsAsync(id, tail ?? 200, follow: true, http.RequestAborted))
            {
                await http.Response.WriteAsync($"data: {line}\n\n", http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
            }
        });

        api.MapGet("/status", async (
            IModelRegistry registry,
            IModelRuntime runtime,
            IMemoryProbe memoryProbe,
            IModelMetricsReader metricsReader,
            IRouterStats routerStats,
            IServiceProvider services,
            CancellationToken ct) =>
        {
            var definitions = await registry.ListAsync(ct);
            var statuses = await runtime.ListStatusAsync(ct);
            var statusById = statuses.ToDictionary(s => s.Id);

            var models = new List<ModelStatusView>(definitions.Count);
            foreach (var definition in definitions)
            {
                var status = statusById.TryGetValue(definition.Id, out var s) ? s : new ModelStatus(definition.Id, ModelState.Stopped);
                var metrics = await metricsReader.GetAsync(definition.Id, ct);
                models.Add(new ModelStatusView(definition, status, metrics));
            }

            MemorySnapshot memory;
            try
            {
                memory = await memoryProbe.GetAsync(ct);
            }
            catch (MirbadLLM.Orchestration.Multinode.NodeTelemetryUnavailableException ex)
            {
                // Multi-node wiring only: the node has not reported memory recently (W3 review MN-094). Today's probe
                // reads /proc/meminfo and never throws this.
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(new StatusResponse(memory, models, routerStats.Snapshot()) { Nodes = await StatusNodesAsync(services, ct) });
        });

        MapMoveRoutes(api);

        api.MapDownloadApi();
        api.MapCatalogApi();
        api.MapPlacementApi();

        return endpoints;
    }

    private const string PlacementField = "placement";

    /// <summary>
    /// <c>nodes</c> of <c>GET /api/status</c> (design §5.6, D106 follow-up, MN-229): every non-revoked registry record, in
    /// id order, with its live state from the status cache. Null when no registry is composed (the field is then absent).
    /// </summary>
    private static async Task<IReadOnlyList<StatusNodeView>?> StatusNodesAsync(IServiceProvider services, CancellationToken ct)
    {
        if (services.GetService<INodeRegistry>() is not { } registry)
        {
            return null;
        }

        var cache = services.GetService<INodeStatusCache>();
        var records = await registry.ListAsync(ct);
        return
        [
            .. records
                .Where(r => !r.Revoked)
                .OrderBy(r => r.Id, StringComparer.Ordinal)
                .Select(r => cache?.GetNode(r.Id) is { } live
                    ? new StatusNodeView(r.Id, r.DisplayName, r.Approved, r.Draining, live.State, live.Reasons, live.Memory)
                    : new StatusNodeView(r.Id, r.DisplayName, r.Approved, r.Draining, null, [], null)),
        ];
    }

    /// <summary>Largest move/unplace body read (a request with a password and a GPU list is well under 8 KiB).</summary>
    private const int MaxMoveBodyBytes = 64 * 1024;

    private static readonly JsonSerializerOptions MoveBodyJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Move and unplace (design §5.6, §6.7; MN-109), over <see cref="MoveService"/>:
    /// <list type="table">
    /// <item><term><c>POST /api/models/{id}/move</c></term><description><see cref="ModelMoveRequest"/> → 202 with the move
    /// record (<see cref="OwnershipRecord"/>, stage <c>downloading</c>); the stages run in the background.</description></item>
    /// <item><term><c>GET /api/models/{id}/move</c></term><description><see cref="MoveStatusView"/> (live record or the last
    /// finished one, downloads, force-commit offer); 404 when none.</description></item>
    /// <item><term><c>POST /api/models/{id}/move/force-commit</c></term><description><see cref="ModelMoveForceCommitRequest"/>
    /// → the record at <c>source-stopped</c>.</description></item>
    /// <item><term><c>POST /api/models/{id}/move/cancel</c></term><description>Cancels a move before the source stop is
    /// confirmed → the record at <c>failed</c>.</description></item>
    /// <item><term><c>POST /api/models/{id}/unplace</c></term><description>Optional <see cref="ModelUnplaceRequest"/> → the
    /// unplaced model.</description></item>
    /// </list>
    /// Refusals are ProblemDetails with <c>code</c>: 400 <c>invalid_request</c>/<c>placement_invalid</c> (with
    /// <c>problems</c>), 403 <c>reauthentication_failed</c>, 409 <c>model_not_movable</c>/<c>model_busy</c> (with
    /// <c>holder</c>)/<c>node_draining</c>/<c>node_revoked</c>/<c>invalid_state</c>/<c>confirmation_required</c>, 429
    /// <c>rate_limited</c>, 503 <c>node_offline</c>; 404 for an unknown model; 504 for an unplace whose stop did not finish.
    /// Each route writes its own audit line (<c>model.move</c>, <c>model.move.force-commit</c>, <c>model.unplace</c>).
    /// </summary>
    private static void MapMoveRoutes(RouteGroupBuilder api)
    {
        api.MapPost("/models/{id}/move", (string id, HttpContext http, CancellationToken ct) => MoveRouteAsync(http, async moves =>
        {
            var request = await ReadMoveBodyAsync<ModelMoveRequest>(http, required: true, ct);
            var record = await moves.StartMoveAsync(id, request!, MoveCallerOf(http), ct);
            return Results.Accepted($"/api/models/{id}/move", record);
        }));

        api.MapGet("/models/{id}/move", (string id, HttpContext http, CancellationToken ct) => MoveRouteAsync(http, async moves =>
            await moves.GetAsync(id, ct) is { } view
                ? Results.Ok(view)
                : Results.Problem(detail: $"Model '{id}' has no move.", statusCode: StatusCodes.Status404NotFound)));

        api.MapPost("/models/{id}/move/force-commit", (string id, HttpContext http, CancellationToken ct) => MoveRouteAsync(http, async moves =>
        {
            var request = await ReadMoveBodyAsync<ModelMoveForceCommitRequest>(http, required: true, ct);
            return Results.Ok(await moves.ForceCommitAsync(id, request!, MoveCallerOf(http), ct));
        }));

        api.MapPost("/models/{id}/move/cancel", (string id, HttpContext http, CancellationToken ct) => MoveRouteAsync(http, async moves =>
            Results.Ok(await moves.CancelAsync(id, MoveCallerOf(http), ct))));

        api.MapPost("/models/{id}/unplace", (string id, HttpContext http, CancellationToken ct) => MoveRouteAsync(http, async moves =>
        {
            var request = await ReadMoveBodyAsync<ModelUnplaceRequest>(http, required: false, ct);
            return Results.Ok(await moves.UnplaceAsync(id, request, MoveCallerOf(http), ct));
        }));
    }

    private static MoveCaller MoveCallerOf(HttpContext http) =>
        new(MirbadLLM.Orchestration.Audit.AuditActorResolver.Resolve(http), AdminPasswordGuard.ClientKey(http)) { User = http.User };

    private static async Task<IResult> MoveRouteAsync(HttpContext http, Func<MoveService, Task<IResult>> run)
    {
        if (http.RequestServices.GetService<MoveService>() is not { } moves)
        {
            return Results.Problem(detail: "Moves are not available on this master.", statusCode: StatusCodes.Status501NotImplemented);
        }

        try
        {
            return await run(moves);
        }
        catch (KeyNotFoundException ex)
        {
            // A body, so the status-code pages do not re-execute a POST into the not-found page.
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (MoveRefusedException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: ex.StatusCode, extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
        }
        catch (Exception ex) when (PlacementApiEndpoints.RegistryRefusal(ex) is { } refusal)
        {
            return refusal; // placement_invalid (target), placement_changed, model_busy naming the holder
        }
        catch (Exception ex) when (NodeProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    /// <summary>Reads a move/unplace body; a missing (when required) or malformed body is 400 <c>invalid_request</c> (never echoing the body: it holds a password).</summary>
    private static async Task<T?> ReadMoveBodyAsync<T>(HttpContext http, bool required, CancellationToken ct)
        where T : class
    {
        var options = http.RequestServices.GetService<IOptions<JsonOptions>>()?.Value.SerializerOptions ?? MoveBodyJson;
        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length > MaxMoveBodyBytes)
        {
            throw new MoveRefusedException(ErrorCodes.InvalidRequest, "The body is too large.", StatusCodes.Status400BadRequest);
        }

        T? body;
        try
        {
            buffer.Position = 0;
            body = buffer.Length == 0 ? null : await JsonSerializer.DeserializeAsync<T>(buffer, options, ct);
        }
        catch (JsonException)
        {
            throw new MoveRefusedException(ErrorCodes.InvalidRequest, $"The body is not a valid {typeof(T).Name}.", StatusCodes.Status400BadRequest);
        }

        return body is null && required
            ? throw new MoveRefusedException(ErrorCodes.InvalidRequest, $"A {typeof(T).Name} body is required.", StatusCodes.Status400BadRequest)
            : body;
    }

    /// <summary>
    /// The answer for a start/restart/stop whose node cannot take it (multi-node wiring, W3 review MN-094, MN-047): 409 when
    /// the model's placement or the node's draining flag stands in the way or another action ended the start before its
    /// container existed; 503 when the node is not serving (incl. a fencing refusal, the node then being Pending takeover),
    /// could not be reached, or something on the node is down (<c>docker_unavailable</c>, <c>hostd_unavailable</c>,
    /// <c>state_unavailable</c>, <c>server_busy</c>); 504 when the node's dependency timed out or the outcome is unknown, or
    /// a stop did not finish in time (it continues); any other agent refusal with the agent's own 409/410/422 status.
    /// Null for any other exception (which keeps today's handling).
    /// </summary>
    private static IResult? NodeProblem(Exception ex) => ex switch
    {
        NodeUnavailableException { IsTransient: true } => Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable),
        NodeUnavailableException => Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict),
        ModelStartInterruptedException => Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict),
        AgentUnreachableException => Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable),
        { InnerException: AgentException agent } when NodeSideStatus(agent.Code) is int status =>
            Results.Problem(detail: ex.Message, statusCode: status, extensions: ApiCodeExtension(agent.Code)),
        TimeoutException => Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status504GatewayTimeout),
        _ => null,
    };

    /// <summary>
    /// The <c>code</c> extension for an agent refusal whose code is also a master API code (<see cref="ErrorCodes.ApiCodes"/>,
    /// e.g. <c>gpu_missing</c>, D73/D95): the API documents those codes, so the body carries them (MN-124). Other agent codes
    /// keep P1's message-only body.
    /// </summary>
    private static Dictionary<string, object?>? ApiCodeExtension(string code) =>
        ErrorCodes.ApiCodes.Contains(code) ? new Dictionary<string, object?> { ["code"] = code } : null;

    /// <summary>
    /// The agent's own status (<see cref="ErrorMappings.AgentStatus"/>) when it says something the operator can act on:
    /// 409 (a conflict on the node), 410 (an expired operation), 422 (a spec the node refuses), 503 (Docker or hostd down,
    /// op log unavailable, busy) or 504 (timeout, unknown outcome); else null, which keeps 500 for what is the master's own
    /// fault (a malformed request, an authentication problem) (MN-047).
    /// </summary>
    internal static int? NodeSideStatus(string code) => ErrorMappings.AgentStatus(code) switch
    {
        StatusCodes.Status409Conflict or StatusCodes.Status410Gone or StatusCodes.Status422UnprocessableEntity
            or StatusCodes.Status503ServiceUnavailable or StatusCodes.Status504GatewayTimeout => ErrorMappings.AgentStatus(code),
        _ => null,
    };

    /// <summary>
    /// The PUT merge rule (design §5.4): each multi-node field the body does not name keeps the stored value.
    /// <paramref name="present"/> holds the body's property names (case-insensitive, like the serializer).
    /// </summary>
    public static ModelDefinition MergeAbsentFields(ModelDefinition model, ModelDefinition stored, IReadOnlySet<string> present) =>
        model with
        {
            Placement = present.Contains(PlacementField) ? model.Placement : stored.Placement,
            Runtime = present.Contains("runtime") ? model.Runtime : stored.Runtime,
            LlamaCpp = present.Contains("llamaCpp") ? model.LlamaCpp : stored.LlamaCpp,
            Mlx = present.Contains("mlx") ? model.Mlx : stored.Mlx,
            Container = present.Contains("container") ? model.Container : stored.Container,
            // Leaving it out must not silently turn speculative decoding off; send null to do that.
            Speculative = present.Contains("speculative") ? model.Speculative : stored.Speculative,
            HostMemoryBudgetGiB = present.Contains("hostMemoryBudgetGiB") ? model.HostMemoryBudgetGiB : stored.HostMemoryBudgetGiB,
            KeepServingDuringJobs = present.Contains("keepServingDuringJobs") ? model.KeepServingDuringJobs : stored.KeepServingDuringJobs,
            // A digest pins one image: kept when absent only while the image stays the same (W7 review MN-126).
            ImageDigest = present.Contains("imageDigest")
                ? model.ImageDigest
                : string.Equals(model.Image, stored.Image, StringComparison.Ordinal) ? stored.ImageDigest : null,
        };
}
