using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MirbadLLM.Orchestration.Recovery;

namespace MirbadLLM.Web.Api;

/// <summary>
/// Backup downloads and status (<c>/api/backup</c>), admin-only like the rest of <c>/api</c> (the fallback
/// authorization policy). Creating and restoring backups happens on the Backup &amp; restore page; these endpoints
/// only hand files to the browser.
/// </summary>
public static class BackupApiEndpoints
{
    private const string JsonContentType = "application/json";

    public static IEndpointRouteBuilder MapBackupApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/backup");

        // A backup the page just generated, under a one-time token that expires after two minutes.
        api.MapGet("/download/{token}", (string token, HttpContext http, BackupDownloadTokens tokens) =>
        {
            if (!tokens.TryRedeem(token, out byte[] content, out string fileName))
            {
                return Results.Problem(detail: "This download link has expired or was already used. Create the backup again.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            http.Response.Headers.CacheControl = "no-store";
            return Results.File(content, JsonContentType, fileName);
        });

        // A scheduled backup from the backup directory. Only a bare backup file name is accepted.
        api.MapGet("/files/{name}", (string name, HttpContext http, BackupDirectory directory) =>
        {
            if (!BackupDirectory.IsBackupFileName(name))
            {
                return Results.Problem(detail: "Not a backup file name.", statusCode: StatusCodes.Status400BadRequest);
            }

            string? path = directory.Resolve(name);
            if (path is null)
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "no-store";
            return Results.File(path, JsonContentType, name);
        });

        api.MapGet("/status", (BackupScheduler scheduler, RestoreJobService restores) =>
            Results.Ok(new BackupStatusResponse(scheduler.GetStatus(), restores.Current)));

        return endpoints;
    }
}

/// <summary>Shape returned by <c>GET /api/backup/status</c>. Never includes the passphrase, only whether one is configured.</summary>
public sealed record BackupStatusResponse(BackupScheduleStatus Scheduled, RestoreJobSnapshot? Restore);
