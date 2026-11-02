using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Audit;

namespace MirbadLLM.Web.Api;

/// <summary>
/// Fencing admin API (design §4.2 step 3, §5.6; MN-115), behind the same admin-login fallback policy as the rest of
/// <c>/api</c>:
/// <list type="bullet">
/// <item><c>GET /api/fencing</c> → <see cref="FencingStatus"/> (the takeover banner's source, MN-121).</item>
/// <item><c>POST /api/fencing/takeover</c> <see cref="TakeoverRequest"/> → the persisted <see cref="TakeoverRecord"/>.
/// <c>confirm</c> must be <see cref="TakeoverConfirmations.PreviousMasterStopped"/> (409 <c>confirmation_required</c>),
/// then the signed-in session is checked (403 <c>reauthentication_failed</c>; refused
/// while admin login is disabled, where the flag file <c>{DataPath}/takeover</c> remains).</item>
/// <item><c>POST /api/fencing/takeover/continue</c> <see cref="TakeoverContinueRequest"/> → the updated record;
/// <c>confirm</c> is the node id; 409 <c>invalid_state</c> when the node does not need a continuation, the takeover is not
/// the latest, or the agent now reports another epoch.</item>
/// </list>
/// Refusals are ProblemDetails with the <see cref="ErrorCodes"/> value in <c>code</c> and write a <c>refused</c> audit line;
/// the coordinator writes the others. Mapped by the P2 composition (MN-130).
/// </summary>
public static class FencingApiEndpoints
{
    public static IEndpointRouteBuilder MapFencingApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var api = endpoints.MapGroup("/api/fencing");

        api.MapGet("", (IFencingCoordinator coordinator) => Results.Ok(coordinator.GetStatus()));

        api.MapPost("/takeover", async (TakeoverRequest? body, HttpContext http, IFencingCoordinator coordinator, CancellationToken ct) =>
        {
            if (body is null)
            {
                return Results.Problem(detail: "Body must be {\"confirm\"}.", statusCode: StatusCodes.Status400BadRequest);
            }

            AdminCaller caller = AdminCallers.FromRequest(http);
            if ((FencingConfirmation.CheckTakeoverConfirmation(body.Confirm)
                 ?? FencingConfirmation.Reauthenticate(http.RequestServices.GetService<IAdminReauthenticator>(), http.User)) is { } refusal)
            {
                return await RefuseAsync(http, caller.Actor, AuditActions.FencingTakeover, null, null, refusal);
            }

            string actor = caller.Actor;

            try
            {
                return Results.Ok(await coordinator.TakeOverAsync(actor, ct));
            }
            catch (InvalidOperationException ex)
            {
                return Problem(ErrorCodes.InvalidState, ex.Message);
            }
        });

        api.MapPost("/takeover/continue", async (TakeoverContinueRequest? body, HttpContext http, IFencingCoordinator coordinator, CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.NodeId) || string.IsNullOrWhiteSpace(body.TakeoverId))
            {
                return Results.Problem(
                    detail: "Body must be {\"nodeId\", \"takeoverId\", \"agentEpoch\", \"confirm\"}.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            AdminCaller caller = AdminCallers.FromRequest(http);
            if ((FencingConfirmation.CheckContinueConfirmation(body.Confirm, body.NodeId)
                 ?? FencingConfirmation.Reauthenticate(http.RequestServices.GetService<IAdminReauthenticator>(), http.User)) is { } refusal)
            {
                return await RefuseAsync(http, caller.Actor, AuditActions.FencingContinue, body.NodeId, body.TakeoverId, refusal);
            }

            string actor = caller.Actor;

            try
            {
                return Results.Ok(await coordinator.ContinueAsync(body.NodeId, body.TakeoverId, body.AgentEpoch, actor, ct));
            }
            catch (InvalidOperationException ex)
            {
                return Problem(ErrorCodes.InvalidState, ex.Message);
            }
        });

        return endpoints;
    }

    /// <summary>The audit actor of a request (<see cref="AuditActorResolver"/>, the one rule since the W7 review MN-126).</summary>
    internal static string Actor(HttpContext http) => AuditActorResolver.Resolve(http);

    private static async Task<IResult> RefuseAsync(
        HttpContext http, string actor, string action, string? nodeId, string? target, FencingRefusal refusal)
    {
        await FencingConfirmation.AuditRefusedAsync(
            http.RequestServices.GetService<IAuditLog>(), http.RequestServices.GetService<TimeProvider>(), actor, action, nodeId, target, refusal);
        return Problem(refusal.Code, refusal.Message, refusal.Status);
    }

    private static IResult Problem(string code, string message, int? status = null) =>
        Results.Problem(
            detail: message,
            statusCode: status ?? ErrorCodes.ApiStatus(code),
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
