using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace MirbadLLM.HostAgent;

internal sealed record AutostartRequest(bool Enabled);

/// <summary><c>POST /whitelist</c>. <c>confirm</c> must equal the name the unit gets (shown in discovery).</summary>
internal sealed record WhitelistAddRequest(string? Unit, string? Scope, string? User, string? DisplayName, bool? Protected, string? Confirm);

/// <summary><c>PATCH /whitelist/{name}</c>. Turning protection off needs <c>confirm</c> equal to the name.</summary>
internal sealed record WhitelistUpdateRequest(string? DisplayName, bool? Protected, string? Confirm);

/// <summary>
/// The agent's JSON API. Order of checks for anything that names a unit: deny list (403, code <c>denied</c>),
/// then whitelist (404), then the protected confirmation (403, code <c>confirm</c>). Nothing runs before all pass.
/// Errors are <c>{"error": "...", "code": "..."}</c>.
/// </summary>
internal static class HostAgentApi
{
    public static IEndpointRouteBuilder MapHostAgentApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        // Read-only: GPU compute processes with the container or unit that owns each (Monitoring's per-model memory).
        endpoints.MapGet("/gpu-processes", async (GpuProcessInventory inventory, IUnitManager manager, CancellationToken ct) =>
        {
            if (await inventory.ListAsync(ct).ConfigureAwait(false) is not { } processes)
            {
                return Error(StatusCodes.Status503ServiceUnavailable, "unavailable", "nvidia-smi is missing, failed or timed out.");
            }

            var units = manager.Whitelist.Units;
            return Results.Ok(processes.Select(p => p.Unit is null ? p : p with
            {
                DisplayName = units.FirstOrDefault(u => string.Equals(u.Unit, p.Unit, StringComparison.Ordinal))?.DisplayName,
            }).ToList());
        });

        endpoints.MapGet("/services", async (string? include, IUnitManager manager, CancellationToken ct) =>
            string.Equals(include, "all", StringComparison.Ordinal)
                ? Results.Ok(await manager.DiscoverAsync(ct).ConfigureAwait(false))
                : Results.Ok(new { services = await manager.ListAsync(ct).ConfigureAwait(false) }));

        endpoints.MapGet("/services/{name}", async (string name, IUnitManager manager, ILogger<UnitManager> logger, CancellationToken ct) =>
        {
            var unit = manager.Whitelist.Find(name);
            if (unit is null)
            {
                Log.UnknownService(logger, name);
                return UnknownService(name);
            }

            return Results.Ok(await manager.GetAsync(unit, ct).ConfigureAwait(false));
        });

        // Literal "autostart" outranks the {action} parameter route below.
        endpoints.MapPost("/services/{name}/autostart", async (
            string name, string? confirm, AutostartRequest? body, IUnitManager manager, ILogger<UnitManager> logger, CancellationToken ct) =>
        {
            if (Denied(manager, logger, name, "autostart") is { } denied)
            {
                return denied;
            }

            var unit = manager.Whitelist.Find(name);
            if (unit is null)
            {
                Log.UnknownService(logger, name);
                return UnknownService(name);
            }

            if (body is null)
            {
                return Error(StatusCodes.Status400BadRequest, "invalid", "Body must be {\"enabled\": true|false}.");
            }

            return ToResult(await manager.SetAutostartAsync(unit, body.Enabled, confirm, ct).ConfigureAwait(false));
        });

        endpoints.MapPost("/services/{name}/{action}", async (
            string name, string action, string? confirm, IUnitManager manager, ILogger<UnitManager> logger, CancellationToken ct) =>
        {
            if (Denied(manager, logger, name, action) is { } denied)
            {
                return denied;
            }

            var unit = manager.Whitelist.Find(name);
            if (unit is null)
            {
                Log.UnknownService(logger, name);
                return UnknownService(name);
            }

            if (!SystemctlCommands.TryParseAction(action, out var parsed))
            {
                return Error(StatusCodes.Status400BadRequest, "invalid", "Action must be start, stop or restart.");
            }

            return ToResult(await manager.RunAsync(unit, parsed, confirm, ct).ConfigureAwait(false));
        });

        endpoints.MapPost("/whitelist", async (WhitelistAddRequest? body, IUnitManager manager, ILogger<UnitManager> logger, CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Unit))
            {
                return Error(StatusCodes.Status400BadRequest, "invalid", "Body must be {\"unit\": \"x.service\", \"scope\": \"system|user\", \"confirm\": \"<name>\"}.");
            }

            string unitName = body.Unit.Trim();
            if (manager.Deny.Match(unitName) is { } pattern)
            {
                Log.WhitelistRefusedDenied(logger, unitName, pattern);
                return Error(StatusCodes.Status403Forbidden, "denied", $"{unitName} is a system service (matches '{pattern}') and cannot be managed.");
            }

            if (!manager.Config.IsValidUnitName(unitName))
            {
                return Error(StatusCodes.Status400BadRequest, "invalid", $"'{unitName}' is not a valid service unit name.");
            }

            UnitScope scope;
            string? user = null;
            switch (body.Scope ?? "system")
            {
                case "system":
                    scope = UnitScope.System;
                    break;
                case "user":
                    scope = UnitScope.User;
                    var users = manager.Config.DiscoverUsers;
                    user = body.User ?? (users.Count == 1 ? users[0] : null);
                    if (user is null || !users.Contains(user, StringComparer.Ordinal))
                    {
                        return Error(StatusCodes.Status400BadRequest, "invalid", $"User units can only be managed for: {string.Join(", ", users)}.");
                    }

                    break;
                default:
                    return Error(StatusCodes.Status400BadRequest, "invalid", "Scope must be 'system' or 'user'.");
            }

            string name = manager.Whitelist.ProposeName(unitName, scope, user);
            if (!string.Equals(body.Confirm?.Trim(), name, StringComparison.Ordinal))
            {
                Log.WhitelistRefused(logger, name, "add", "needs-confirmation");
                return Error(StatusCodes.Status403Forbidden, "confirm", $"Confirm with the service name '{name}' to manage it.");
            }

            var found = await manager.InspectAsync(unitName, scope, user, ct).ConfigureAwait(false);
            if (found is null)
            {
                return Error(StatusCodes.Status404NotFound, "not-found", manager.Config.Flavor == ServiceFlavor.Launchd
                    ? $"{unitName} is not a launchd job with a plist in {(scope == UnitScope.User ? "the user's or the system's LaunchAgents" : "/Library/LaunchDaemons")}."
                    : $"{unitName} is not a loaded service with a unit file.");
            }

            var result = manager.Whitelist.Add(unitName, scope, user, body.DisplayName, found.Description, body.Protected ?? false);
            if (!result.Ok)
            {
                Log.WhitelistRefused(logger, name, "add", result.Error.ToString());
                return WhitelistError(result);
            }

            return Results.Ok(await manager.GetAsync(result.Unit!, ct).ConfigureAwait(false));
        });

        endpoints.MapMethods("/whitelist/{name}", ["PATCH"], async (
            string name, WhitelistUpdateRequest? body, IUnitManager manager, ILogger<UnitManager> logger, CancellationToken ct) =>
        {
            if (body is null)
            {
                return Error(StatusCodes.Status400BadRequest, "invalid", "Body must be {\"displayName\"?: \"...\", \"protected\"?: true|false}.");
            }

            var result = manager.Whitelist.Update(name, body.DisplayName, body.Protected, body.Confirm);
            if (!result.Ok)
            {
                Log.WhitelistRefused(logger, name, "update", result.Error.ToString());
                return WhitelistError(result);
            }

            return Results.Ok(await manager.GetAsync(result.Unit!, ct).ConfigureAwait(false));
        });

        endpoints.MapDelete("/whitelist/{name}", (string name, IUnitManager manager, ILogger<UnitManager> logger) =>
        {
            var result = manager.Whitelist.Remove(name);
            if (!result.Ok)
            {
                Log.WhitelistRefused(logger, name, "remove", result.Error.ToString());
                return WhitelistError(result);
            }

            return Results.Ok(new { removed = name });
        });

        return endpoints;
    }

    private static IResult? Denied(IUnitManager manager, ILogger logger, string name, string action)
    {
        // Checked on the raw name before any lookup, so a denied unit is refused even if someone wrote it into the file.
        string? pattern = manager.Deny.Match(name) ?? (manager.Whitelist.Find(name) is { } unit ? manager.Deny.Match(unit.Unit) : null);
        if (pattern is null)
        {
            return null;
        }

        Log.DeniedRequest(logger, name, action, pattern);
        return Error(StatusCodes.Status403Forbidden, "denied", $"'{name}' is a system service (matches '{pattern}') and cannot be managed.");
    }

    private static IResult ToResult(ActionResult result) => result.Outcome switch
    {
        ActionOutcome.Done => Results.Ok(result.Status),
        ActionOutcome.ConfirmationRequired => Error(StatusCodes.Status403Forbidden, "confirm", result.Message ?? "Confirmation required."),
        ActionOutcome.Denied => Error(StatusCodes.Status403Forbidden, "denied", result.Message ?? "Denied."),
        _ => Error(StatusCodes.Status502BadGateway, "failed", result.Message ?? "systemctl failed."),
    };

    private static IResult WhitelistError(WhitelistResult result) => result.Error switch
    {
        HostAgent.WhitelistError.Denied => Error(StatusCodes.Status403Forbidden, "denied", result.Message!),
        HostAgent.WhitelistError.ConfirmationRequired => Error(StatusCodes.Status403Forbidden, "confirm", result.Message!),
        HostAgent.WhitelistError.Exists => Error(StatusCodes.Status409Conflict, "exists", result.Message!),
        HostAgent.WhitelistError.NotFound => Error(StatusCodes.Status404NotFound, "not-found", result.Message!),
        _ => Error(StatusCodes.Status400BadRequest, "invalid", result.Message ?? "Invalid request."),
    };

    private static IResult UnknownService(string name) =>
        Error(StatusCodes.Status404NotFound, "not-found", $"'{name}' is not in the host agent's whitelist.");

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { error = message, code }, statusCode: status);
}
