using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;

namespace MirbadLLM.Web.Api;

/// <summary>Body of <c>POST /api/services/{name}/autostart</c>; <c>confirm</c> may also come from the query string.</summary>
public sealed record AutostartBody(bool Enabled, string? Confirm = null);

/// <summary>
/// Body of <c>POST /api/services/whitelist</c>: <c>kind</c> is System, User or Container; <c>confirm</c> is the
/// service's name (as discovery reports it) and the signed-in portal session authorizes the action. The legacy password field is ignored.
/// </summary>
public sealed record WhitelistAddBody(HostServiceKind Kind, string Unit, string? DisplayName, bool Protected, string? Confirm, string? Password);

/// <summary>Body of <c>PATCH /api/services/whitelist/{name}</c>. Unprotecting needs <c>confirm</c> and a signed-in session.</summary>
public sealed record WhitelistUpdateBody(string? DisplayName, bool? Protected, string? Confirm, string? Password);

/// <summary>
/// Admin API for the Services page (behind the same admin-login fallback policy as the rest of <c>/api</c>):
/// <c>GET /api/services</c>, <c>POST /api/services/{name}/{start|stop|restart}?confirm=</c>,
/// <c>POST /api/services/{name}/autostart</c>. Protected services need <c>confirm=&lt;name&gt;</c> to stop,
/// restart or turn autostart off; without it the call is rejected with 400 and nothing runs.
/// Whitelist: <c>GET /api/services/discovered</c>, <c>POST /api/services/whitelist</c> (signed-in session + typed name),
/// <c>PATCH /api/services/whitelist/{name}</c>, <c>DELETE /api/services/whitelist/{name}</c>. A unit on the host
/// agent's deny list is 403; a missing session is refused before changing the whitelist.
/// </summary>
public static class HostServiceApiEndpoints
{
    public static IEndpointRouteBuilder MapHostServiceApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/services");

        api.MapGet("", async (IHostServiceManager manager, CancellationToken ct) =>
            Results.Ok(await manager.ListAsync(ct)));

        api.MapGet("/discovered", async (IHostServiceManager manager, CancellationToken ct) =>
            Results.Ok(await manager.DiscoverAsync(ct)));

        api.MapPost("/whitelist", async (WhitelistAddBody? body, HttpContext http, IHostServiceManager manager, CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Unit))
            {
                return Results.Problem(detail: "Body must be {\"kind\", \"unit\", \"confirm\"}.", statusCode: StatusCodes.Status400BadRequest);
            }

            var request = new HostServiceWhitelistAdd(body.Kind, body.Unit, body.DisplayName, body.Protected, body.Confirm, null,
                AdminPasswordGuard.ClientKey(http)) { User = http.User };
            return await GuardAsync(() => manager.AddToWhitelistAsync(request, ct));
        });

        api.MapMethods("/whitelist/{name}", ["PATCH"], async (string name, WhitelistUpdateBody? body, HttpContext http, IHostServiceManager manager, CancellationToken ct) =>
        {
            if (body is null)
            {
                return Results.Problem(detail: "Body must be {\"displayName\"?, \"protected\"?, \"confirm\"?}.", statusCode: StatusCodes.Status400BadRequest);
            }

            var request = new HostServiceWhitelistUpdate(body.DisplayName, body.Protected, body.Confirm, null, AdminPasswordGuard.ClientKey(http)) { User = http.User };
            return await GuardAsync(() => manager.UpdateWhitelistAsync(name, request, ct));
        });

        api.MapDelete("/whitelist/{name}", async (string name, IHostServiceManager manager, CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                await manager.RemoveFromWhitelistAsync(name, ct);
                return new { removed = name };
            }));

        api.MapPost("/{name}/autostart", async (string name, string? confirm, AutostartBody? body, IHostServiceManager manager, CancellationToken ct) =>
        {
            if (body is null)
            {
                return Results.Problem(detail: "Body must be {\"enabled\": true|false}.", statusCode: StatusCodes.Status400BadRequest);
            }

            return await GuardAsync(() => manager.SetAutostartAsync(name, body.Enabled, confirm ?? body.Confirm, ct));
        });

        api.MapPost("/{name}/{action}", async (string name, string action, string? confirm, IHostServiceManager manager, CancellationToken ct) =>
        {
            HostServiceAction? parsed = action switch
            {
                "start" => HostServiceAction.Start,
                "stop" => HostServiceAction.Stop,
                "restart" => HostServiceAction.Restart,
                _ => null,
            };

            if (parsed is null)
            {
                return Results.Problem(detail: "Action must be start, stop or restart.", statusCode: StatusCodes.Status400BadRequest);
            }

            return await GuardAsync(() => manager.RunAsync(name, parsed.Value, confirm, ct));
        });

        return endpoints;
    }

    private static async Task<IResult> GuardAsync<T>(Func<Task<T>> run)
    {
        try
        {
            return Results.Ok(await run());
        }
        catch (AdminReauthenticationException ex)
        {
            return Results.Problem(detail: ex.Message,
                statusCode: ex.Outcome == AdminReauthOutcome.RateLimited ? StatusCodes.Status429TooManyRequests : StatusCodes.Status403Forbidden);
        }
        catch (HostServiceDeniedException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (HostServiceConflictException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (HostServiceNotFoundException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (HostServiceConfirmationRequiredException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (HostAgentUnavailableException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (HostServiceException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
