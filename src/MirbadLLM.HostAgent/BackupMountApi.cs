using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MirbadLLM.HostAgent;

/// <summary>
/// The backup-mount endpoints. The mount target is fixed (<see cref="BackupMountPaths.Target"/>); callers only
/// describe the share. Errors use the agent's usual <c>{"error": "...", "code": "..."}</c> shape:
/// 400 <c>invalid</c>, 409 <c>conflict</c> (a hand-made mount is in the way), 422 <c>failed</c> (the test or the
/// switch failed; nothing changed).
/// </summary>
internal static class BackupMountApi
{
    public static IEndpointRouteBuilder MapBackupMountApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/backup-mount", async (BackupMountManager manager, CancellationToken ct) =>
            Results.Ok(await manager.GetStatusAsync(ct).ConfigureAwait(false)));

        endpoints.MapPut("/backup-mount", async (BackupMountRequest? body, BackupMountManager manager, CancellationToken ct) =>
            ToResult(await manager.ApplyAsync(body, ct).ConfigureAwait(false)));

        endpoints.MapDelete("/backup-mount", async (BackupMountManager manager, CancellationToken ct) =>
            ToResult(await manager.RemoveAsync(ct).ConfigureAwait(false)));

        // A failed connection test is an answer, not an error: 200 with ok=false and the reason.
        endpoints.MapPost("/backup-mount/test", async (BackupMountRequest? body, BackupMountManager manager, CancellationToken ct) =>
        {
            var result = await manager.TestAsync(body, ct).ConfigureAwait(false);
            return result.Outcome switch
            {
                BackupMountOutcome.Ok => Results.Ok(new { ok = true, message = result.Message }),
                BackupMountOutcome.Failed => Results.Ok(new { ok = false, message = result.Message }),
                _ => ToResult(result),
            };
        });

        return endpoints;
    }

    private static IResult ToResult(BackupMountResult result) => result.Outcome switch
    {
        BackupMountOutcome.Ok => Results.Ok(result.Status),
        BackupMountOutcome.Invalid => Error(StatusCodes.Status400BadRequest, "invalid", result.Message ?? "Invalid request."),
        BackupMountOutcome.Conflict => Error(StatusCodes.Status409Conflict, "conflict", result.Message ?? "Conflict."),
        _ => Error(StatusCodes.Status422UnprocessableEntity, "failed", result.Message ?? "The backup mount failed."),
    };

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { error = message, code }, statusCode: status);
}
