using Microsoft.AspNetCore.Mvc;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Audit;
using MirbadLLM.Orchestration.Benchmarking;

namespace MirbadLLM.Web.Api;

/// <summary>Body of <c>POST /api/models/{id}/tune</c>. Both optional: goal defaults to Balanced, steps to all four.</summary>
/// <summary>Body of <c>POST /api/models/{id}/tune</c>.</summary>
/// <param name="Search">Quick or Full (workload presets); explicit <paramref name="Steps"/> win when both are given.</param>
public sealed record TuneRequest(TuningGoal? Goal = null, IReadOnlyList<TuningDimension>? Steps = null, TuningSearch? Search = null);

/// <summary>The search presets (D2): Quick = max num seqs + speculative × FULL_AND_PIECEWISE; Full = every phase.</summary>
public enum TuningSearch
{
    Quick,
    Full,
}

/// <summary>Shape of <c>GET /api/benchmark</c> and <c>GET /api/tuning</c>: the running job of that kind (or null) and how the last one ended.</summary>
public sealed record JobStatusResponse(JobSnapshot? Current, JobOutcome? Last);

/// <summary>
/// Benchmark and tuning jobs (<c>/api/*</c>, admin-cookie protected like the rest of the admin API). One job runs per
/// node, at most <c>MirbadLLM:Jobs:MaxConcurrent</c> (default 2) across nodes (MN-111): starting one on a busy node is a
/// 409 <c>job_conflict</c> naming the node and the running job; above the limit a 409 <c>job_limit_reached</c> naming the
/// limit. <c>/api/benchmark</c> and <c>/api/tuning</c> keep their shape and report/cancel the job on the bootstrap node;
/// multi-node callers use <c>GET /api/jobs</c> (always an array of <see cref="NodeJobSummary"/>) and
/// <c>GET/DELETE /api/nodes/{id}/job</c> (today's <see cref="JobSnapshot"/>, 404 when the node has none).
/// </summary>
public static class JobApiEndpoints
{
    public static IEndpointRouteBuilder MapJobApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        // ?preset=InteractiveChat|AgentsTools|Throughput|LongDocuments|Embeddings runs that workload preset; without it the classic suite.
        api.MapPost("/models/{id}/benchmark", async (string id, TuningGoal? preset, [FromServices] ModelJobService jobs, [FromServices] IModelRegistry registry, CancellationToken ct) =>
        {
            if (await registry.GetAsync(id, ct) is null)
            {
                return Results.NotFound();
            }

            try
            {
                var job = await jobs.StartBenchmarkAsync(id, preset, ct);
                return Results.Accepted("/api/benchmark", job);
            }
            catch (JobConflictException ex)
            {
                return JobConflict(ex);
            }
            catch (ModelBusyException ex)
            {
                return ModelBusy(ex);
            }
            catch (InvalidOperationException ex)
            {
                // "Not Ready" is a conflict with the current state too.
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        api.MapGet("/models/{id}/benchmarks", async (string id, [FromServices] BenchmarkStore store, [FromServices] IModelRegistry registry, CancellationToken ct) =>
            await registry.GetAsync(id, ct) is null ? Results.NotFound() : Results.Ok(await store.ListBenchmarksAsync(id, ct)));

        api.MapGet("/benchmark", ([FromServices] ModelJobService jobs) => Results.Ok(StatusOf(jobs, JobKind.Benchmark)));

        api.MapDelete("/benchmark", ([FromServices] ModelJobService jobs) =>
            jobs.CancelOnNode(jobs.BootstrapNodeId, JobKind.Benchmark) ? Results.Accepted("/api/benchmark") : Results.NotFound());

        api.MapPost("/models/{id}/tune", async (
            string id,
            TuningGoal? goal,
            [FromBody] TuneRequest? request,
            [FromServices] ModelJobService jobs,
            [FromServices] IModelRegistry registry,
            CancellationToken ct) =>
        {
            if (await registry.GetAsync(id, ct) is null)
            {
                return Results.NotFound();
            }

            try
            {
                var steps = request?.Steps ?? (request?.Search == TuningSearch.Quick ? SearchPlanner.Quick : request?.Search == TuningSearch.Full ? SearchPlanner.Full : null);
                var job = await jobs.StartTuningAsync(id, request?.Goal ?? goal ?? TuningGoal.Balanced, steps?.ToList(), ct);
                return Results.Accepted("/api/tuning", job);
            }
            catch (JobConflictException ex)
            {
                return JobConflict(ex);
            }
            catch (ModelBusyException ex)
            {
                return ModelBusy(ex);
            }
            catch (InvalidOperationException ex)
            {
                // E.g. a reranker: no benchmark exists for it yet.
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        api.MapGet("/tuning", ([FromServices] ModelJobService jobs) => Results.Ok(StatusOf(jobs, JobKind.Tuning)));

        api.MapDelete("/tuning", ([FromServices] ModelJobService jobs) =>
            jobs.CancelOnNode(jobs.BootstrapNodeId, JobKind.Tuning) ? Results.Accepted("/api/tuning") : Results.NotFound());

        api.MapGet("/models/{id}/tuning", async (string id, [FromServices] BenchmarkStore store, [FromServices] IModelRegistry registry, CancellationToken ct) =>
            await registry.GetAsync(id, ct) is null ? Results.NotFound() : Results.Ok(await store.ListTuningAsync(id, ct)));

        api.MapPost("/models/{id}/tuning/{runId}/candidates/{index:int}/apply", async (
            string id, string runId, int index, [FromServices] ModelJobService jobs, CancellationToken ct) =>
        {
            try
            {
                var merged = await jobs.ApplyTuningCandidateAsync(id, runId, index, ct);
                return Results.Ok(merged);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (JobConflictException ex)
            {
                return JobConflict(ex);
            }
            catch (InvalidOperationException ex)
            {
                // "Not accepted" and "wrong model" are 409s too.
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        // ---- P2 multi-node job API (design §5.6, D34, MN-111) ----

        api.MapGet("/jobs", async ([FromServices] ModelJobService jobs, CancellationToken ct) =>
            Results.Ok(await jobs.ListJobSummariesAsync(ct)));

        api.MapGet("/nodes/{id}/job", (string id, [FromServices] ModelJobService jobs) =>
            jobs.JobOnNode(id) is { } job ? Results.Ok(job) : Results.NotFound());

        api.MapDelete("/nodes/{id}/job", async (string id, HttpContext context, [FromServices] ModelJobService jobs, CancellationToken ct) =>
        {
            var job = jobs.JobOnNode(id);
            bool cancelled = job is not null && jobs.CancelOnNode(id);
            var audit = context.RequestServices.GetService<IAuditLog>() ?? NullAuditLog.Instance;
            await audit.WriteAsync(new AuditRecord
            {
                Time = DateTimeOffset.UtcNow,
                Actor = ActorOf(context),
                Action = AuditActions.Job,
                NodeId = id,
                ModelId = job?.ModelId,
                Target = job?.JobId,
                Result = cancelled ? AuditResults.Accepted : AuditResults.Refused,
                Detail = job is null ? "no job on the node" : "cancel " + KindName(job.Kind),
            }, ct);
            return cancelled ? Results.Accepted($"/api/nodes/{Uri.EscapeDataString(id)}/job") : Results.NotFound();
        });

        return endpoints;
    }

    /// <summary>Today's shape for the bootstrap node's job (D34): the running job of <paramref name="kind"/> there and how its last one ended.</summary>
    private static JobStatusResponse StatusOf(ModelJobService jobs, JobKind kind)
    {
        var current = jobs.JobOnNode(jobs.BootstrapNodeId);
        var last = jobs.LastOutcomeOnNode(jobs.BootstrapNodeId);
        return new JobStatusResponse(current?.Kind == kind ? current : null, last?.Kind == kind ? last : null);
    }

    private static string KindName(JobKind kind) => kind == JobKind.Tuning ? MaintenanceJobKinds.Tuning : MaintenanceJobKinds.Benchmark;

    /// <summary>409 with <c>code</c> <c>job_conflict</c> (plus <c>nodeId</c>) or <c>job_limit_reached</c> (plus <c>limit</c>).</summary>
    private static IResult JobConflict(JobConflictException ex)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = ex.Code };
        if (ex.NodeId is { } nodeId)
        {
            extensions["nodeId"] = nodeId;
        }

        if (ex.Limit is { } limit)
        {
            extensions["limit"] = limit;
        }

        return Results.Problem(detail: ex.Message, statusCode: ErrorCodes.ApiStatus(ex.Code), extensions: extensions);
    }

    private static IResult ModelBusy(ModelBusyException ex) =>
        Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.ModelBusy });

    /// <summary>The audit actor (design §10.5): <see cref="AuditActorResolver"/>, the one rule since the W7 review (MN-126).</summary>
    private static string ActorOf(HttpContext context) => AuditActorResolver.Resolve(context);
}
