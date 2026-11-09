using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Audit;

/// <summary>One audited pre-P2 route: method, the full route pattern as mapped, the audit action and how the line is filled.</summary>
/// <param name="Method">HTTP method.</param>
/// <param name="Pattern">The endpoint's full route pattern (<c>RoutePattern.RawText</c>), e.g. <c>/api/models/{id}/start</c>.</param>
/// <param name="Action">The <see cref="AuditActions"/> value.</param>
/// <param name="ModelRouteValue">Route value holding the model id, when any.</param>
/// <param name="TargetRouteValue">Route value holding the target (folder, unit, key id, run id), when any.</param>
/// <param name="Detail">Fixed non-secret detail (the verb when the action covers several), when any.</param>
public sealed record LegacyAuditRoute(string Method, string Pattern, string Action, string? ModelRouteValue = null, string? TargetRouteValue = null, string? Detail = null);

/// <summary>
/// The audit endpoint filter for the <b>pre-P2</b> mutating <c>/api</c> routes (design §10.5, owner decision for MN-116:
/// every P2 API task writes its own lines through <see cref="IAuditLog"/>; the older endpoints are covered here). It acts
/// only on the routes listed in <see cref="Routes"/> (method + full pattern), so it can wrap a whole route group without
/// double-auditing a P2 route added to the same group later.
/// <para>Per call: the actor is resolved (<see cref="AuditActorResolver"/>), a request <see cref="AuditScope"/> collects the
/// agent operations the handler submits, and after the handler one <see cref="AuditRecord"/> is written: model id and
/// target from the <b>route values</b> only (request bodies are never read: some carry passwords), the result from the
/// handler's <see cref="IResult"/> (2xx succeeded, 202 accepted, 4xx refused, 5xx or an exception failed) with the
/// ProblemDetails <c>code</c> as error code, and the first agent operation id. Writing never changes the response.</para>
/// <para>Composition (MN-130): map the pre-P2 APIs inside one group carrying the filter, e.g.
/// <c>var audited = app.MapGroup("").AddEndpointFilter(new LegacyApiAuditFilter()); audited.MapAdminApi(); audited.MapJobApi(); …</c>,
/// and register an <see cref="IAuditLog"/>; without one the filter does nothing.</para>
/// </summary>
public sealed class LegacyApiAuditFilter : IEndpointFilter
{
    /// <summary>Every pre-P2 mutating <c>/api</c> route of <c>MirbadLLM.Web</c> (checked against the real app by a test).</summary>
    public static IReadOnlyList<LegacyAuditRoute> Routes { get; } =
    [
        new("POST", "/api/models", AuditActions.ModelCreate),
        new("PUT", "/api/models/{id}", AuditActions.ModelUpdate, ModelRouteValue: "id"),
        new("DELETE", "/api/models/{id}", AuditActions.ModelDelete, ModelRouteValue: "id"),
        new("POST", "/api/models/{id}/start", AuditActions.ModelStart, ModelRouteValue: "id"),
        new("POST", "/api/models/{id}/restart", AuditActions.ModelRestart, ModelRouteValue: "id"),
        new("POST", "/api/models/{id}/stop", AuditActions.ModelStop, ModelRouteValue: "id"),
        new("POST", "/api/downloads", AuditActions.Download, Detail: "start"),
        new("DELETE", "/api/downloads/{folder}", AuditActions.Download, TargetRouteValue: "folder", Detail: "cancel"),
        new("POST", "/api/models/{id}/benchmark", AuditActions.Job, ModelRouteValue: "id", Detail: "benchmark start"),
        new("DELETE", "/api/benchmark", AuditActions.Job, Detail: "benchmark cancel"),
        new("POST", "/api/models/{id}/tune", AuditActions.Job, ModelRouteValue: "id", Detail: "tuning start"),
        new("DELETE", "/api/tuning", AuditActions.Job, Detail: "tuning cancel"),
        new("POST", "/api/models/{id}/tuning/{runId}/candidates/{index:int}/apply", AuditActions.ModelUpdate, ModelRouteValue: "id", TargetRouteValue: "runId", Detail: "tuning candidate applied"),
        new("POST", "/api/keys/", AuditActions.ApiKey, Detail: "create"),
        new("POST", "/api/keys/{id}/revoke", AuditActions.ApiKey, TargetRouteValue: "id", Detail: "revoke"),
        new("PUT", "/api/keys/{id}/scopes", AuditActions.ApiKey, TargetRouteValue: "id", Detail: "change scopes"),
        new("DELETE", "/api/keys/{id}", AuditActions.ApiKey, TargetRouteValue: "id", Detail: "delete"),
        new("POST", "/api/services/whitelist", AuditActions.HostService, Detail: "whitelist add"),
        new("PATCH", "/api/services/whitelist/{name}", AuditActions.HostService, TargetRouteValue: "name", Detail: "whitelist update"),
        new("DELETE", "/api/services/whitelist/{name}", AuditActions.HostService, TargetRouteValue: "name", Detail: "whitelist remove"),
        new("POST", "/api/services/{name}/autostart", AuditActions.HostService, TargetRouteValue: "name", Detail: "autostart"),
        new("POST", "/api/services/{name}/{action}", AuditActions.HostService, TargetRouteValue: "name"),
    ];

    /// <summary>The audited route of an endpoint, or null.</summary>
    public static LegacyAuditRoute? Find(string method, string? pattern)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (pattern is null)
        {
            return null;
        }

        foreach (LegacyAuditRoute route in Routes)
        {
            if (string.Equals(route.Method, method, StringComparison.OrdinalIgnoreCase) && string.Equals(route.Pattern, pattern, StringComparison.Ordinal))
            {
                return route;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        HttpContext http = context.HttpContext;
        LegacyAuditRoute? route = Find(http.Request.Method, (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText);
        IAuditLog? log = route is null ? null : http.RequestServices.GetService<IAuditLog>();
        if (route is null || log is null or NullAuditLog)
        {
            return await next(context).ConfigureAwait(false);
        }

        TimeProvider time = http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
        string actor = AuditActorResolver.Resolve(http);
        using AuditScope scope = AuditScope.BeginRequest(actor);
        object? result;
        try
        {
            result = await next(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteAsync(http, log, Build(route, context, actor, scope, time, AuditResults.Failed, errorCode: null,
                http.RequestAborted.IsCancellationRequested ? "request aborted" : "unhandled " + ex.GetType().Name)).ConfigureAwait(false);
            throw;
        }

        (int status, string? code) = Inspect(result, http);
        string outcome = status switch
        {
            StatusCodes.Status202Accepted => AuditResults.Accepted,
            >= 200 and < 300 => AuditResults.Succeeded,
            >= 400 and < 500 => AuditResults.Refused,
            >= 500 => AuditResults.Failed,
            _ => AuditResults.Succeeded,
        };
        string? detail = status is >= 200 and < 300 ? null : $"HTTP {status}";
        await WriteAsync(http, log, Build(route, context, actor, scope, time, outcome, code, detail)).ConfigureAwait(false);
        return result;
    }

    private static AuditRecord Build(LegacyAuditRoute route, EndpointFilterInvocationContext context, string actor, AuditScope scope, TimeProvider time, string result, string? errorCode, string? extraDetail)
    {
        HttpContext http = context.HttpContext;
        string? modelId = RouteValue(http, route.ModelRouteValue);
        if (modelId is null && route.Action == AuditActions.ModelCreate)
        {
            // The created model's id (a bound ModelDefinition); nothing else of the body is read.
            modelId = context.Arguments.OfType<ModelDefinition>().FirstOrDefault()?.Id;
        }

        string? target = RouteValue(http, route.TargetRouteValue);
        if (route.TargetRouteValue == "runId" && RouteValue(http, "index") is { } index)
        {
            target = $"{target}#{index}";
        }

        string? verb = route.Detail ?? RouteValue(http, "action");
        IReadOnlyList<string> operations = scope.OperationIds;
        string? detail = string.Join("; ", new[]
        {
            verb,
            extraDetail,
            operations.Count > 1 ? $"{operations.Count} agent operations" : null,
        }.Where(s => !string.IsNullOrEmpty(s)));
        return new AuditRecord
        {
            Time = time.GetUtcNow(),
            Actor = actor,
            Action = route.Action,
            ModelId = modelId,
            // The node a per-node call named (?node=, MN-229); absent on P2's calls, which act on the only node.
            NodeId = http.Request.Query.TryGetValue("node", out var node) && node.Count == 1 && MirbadLLM.Agent.Contracts.Protocol.IsValidNodeId(node[0]) ? node[0] : null,
            OperationId = operations.Count > 0 ? operations[0] : null,
            Target = target,
            Result = result,
            ErrorCode = errorCode,
            Detail = string.IsNullOrEmpty(detail) ? null : detail,
        };
    }

    private static async Task WriteAsync(HttpContext http, IAuditLog log, AuditRecord record)
    {
        try
        {
            await log.WriteAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // IAuditLog never throws for I/O; anything else must still not fail the request it records.
            ILogger logger = http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger<LegacyApiAuditFilter>() ?? NullLogger<LegacyApiAuditFilter>.Instance;
            AuditLog.FilterFailed(logger, record.Action, ex.Message);
        }
    }

    /// <summary>Status and ProblemDetails code of the handler's result (200 for a plain value).</summary>
    internal static (int Status, string? Code) Inspect(object? result, HttpContext http)
    {
        string? code = null;
        if (result is ProblemHttpResult problem &&
            problem.ProblemDetails.Extensions.TryGetValue("code", out object? value) && value is not null)
        {
            code = value.ToString();
        }

        int status = result switch
        {
            IStatusCodeHttpResult { StatusCode: { } s } => s,
            IResult => http.Response.StatusCode,
            _ => StatusCodes.Status200OK,
        };
        return (status, code);
    }

    private static string? RouteValue(HttpContext http, string? name) =>
        name is not null && http.Request.RouteValues.TryGetValue(name, out object? value) && value is not null
            ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
            : null;
}
