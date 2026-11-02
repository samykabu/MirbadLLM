using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Storage;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Api;

/// <summary>
/// Agent routes for images, model folders and downloads (design §4.3, routes in <see cref="AgentRoutes"/>). Paths are
/// relative: the host (MN-014/MN-026) maps them on its <c>/agent/v1</c> group, after its own TLS, protocol-header and
/// request-time checks. Mutations go through <see cref="StorageOperations"/> (fencing, durable acceptance with the HF
/// token redacted, payload-bound deduplication) and answer with the stored result on a replay.
/// </summary>
public static class StorageEndpoints
{
    /// <summary>Maps the storage routes onto <paramref name="endpoints"/>.</summary>
    public static IEndpointRouteBuilder MapStorageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(AgentRoutes.ImagesList.Path, (Func<HttpContext, Task<IResult>>)ListImagesAsync);
        endpoints.MapPost(AgentRoutes.ImagePull.Path, (Func<HttpContext, Task<IResult>>)PullImageAsync);
        endpoints.MapGet(AgentRoutes.ModelFoldersList.Path, (Func<HttpContext, Task<IResult>>)ListFoldersAsync);
        endpoints.MapGet(AgentRoutes.ModelFolderInspect.Path, InspectFolder);
        endpoints.MapPost(AgentRoutes.ModelFolderDelete.Path, DeleteFolderAsync);
        endpoints.MapPost(AgentRoutes.DownloadStart.Path, (Func<HttpContext, Task<IResult>>)StartDownloadAsync);
        endpoints.MapGet(AgentRoutes.DownloadsList.Path, (Func<HttpContext, Task<IResult>>)ListDownloadsAsync);
        endpoints.MapGet(AgentRoutes.DownloadGet.Path, GetDownloadAsync);
        endpoints.MapPost(AgentRoutes.DownloadCancel.Path, CancelDownloadAsync);
        return endpoints;
    }

    private static async Task<IResult> ListImagesAsync(HttpContext http)
    {
        var images = http.RequestServices.GetRequiredService<AgentImageService>();
        return await Guard(async () => Ok(await images.ListAsync(http.RequestAborted).ConfigureAwait(false))).ConfigureAwait(false);
    }

    private static async Task<IResult> ListFoldersAsync(HttpContext http)
    {
        var folders = http.RequestServices.GetRequiredService<ModelFolderService>();
        return await Guard(async () => Ok(await folders.ListAsync(http.RequestAborted).ConfigureAwait(false))).ConfigureAwait(false);
    }

    private static Task<IResult> InspectFolder(HttpContext http, string folder)
    {
        var folders = http.RequestServices.GetRequiredService<ModelFolderService>();
        return Guard(() => Task.FromResult(Ok(folders.Inspect(folder))));
    }

    private static async Task<IResult> ListDownloadsAsync(HttpContext http)
    {
        var downloads = http.RequestServices.GetRequiredService<AgentDownloadService>();
        return await Guard(async () => Ok(await downloads.ListAsync(http.RequestAborted).ConfigureAwait(false))).ConfigureAwait(false);
    }

    private static async Task<IResult> GetDownloadAsync(HttpContext http, string folder)
    {
        var downloads = http.RequestServices.GetRequiredService<AgentDownloadService>();
        return await Guard(async () =>
        {
            var status = await downloads.GetAsync(folder, http.RequestAborted).ConfigureAwait(false);
            return status is null ? Error(StorageErrors.NotFound($"No download for '{folder}' on this node.")) : Ok(status);
        }).ConfigureAwait(false);
    }

    private static Task<IResult> PullImageAsync(HttpContext http) =>
        MutateAsync<ImagePullRequest>(http, AgentRoutes.ImagePull, request => request.Image,
            async (request, admission, ct) =>
            {
                var images = http.RequestServices.GetRequiredService<AgentImageService>();
                return Json(await images.StartAsync(request, admission.Status!, ct).ConfigureAwait(false), 202);
            },
            stored => Task.FromResult(Json(stored, 202)));

    private static Task<IResult> DeleteFolderAsync(HttpContext http, string folder) =>
        MutateAsync<FolderDeleteRequest>(http, AgentRoutes.ModelFolderDelete, _ => folder,
            async (request, admission, ct) =>
            {
                var folders = http.RequestServices.GetRequiredService<ModelFolderService>();
                return Ok(await folders.DeleteAsync(folder, request, admission.Status!, admission.Master!, ct).ConfigureAwait(false));
            },
            stored => Task.FromResult(Ok(stored)));

    private static Task<IResult> StartDownloadAsync(HttpContext http) =>
        MutateAsync<DownloadStartRequest>(http, AgentRoutes.DownloadStart, request => request.Folder,
            async (request, admission, ct) =>
            {
                var downloads = http.RequestServices.GetRequiredService<AgentDownloadService>();
                return Json(await downloads.StartAsync(request, admission.Status!, ct).ConfigureAwait(false), 202);
            },
            async stored => Json(await ReplayDownloadAsync(http, stored).ConfigureAwait(false), 202));

    private static Task<IResult> CancelDownloadAsync(HttpContext http, string folder) =>
        MutateAsync<DownloadCancelRequest>(http, AgentRoutes.DownloadCancel, _ => folder,
            async (_, admission, ct) =>
            {
                var downloads = http.RequestServices.GetRequiredService<AgentDownloadService>();
                var operations = http.RequestServices.GetRequiredService<StorageOperations>();
                var final = await downloads.CancelFolderAsync(folder, ct).ConfigureAwait(false);
                return Ok(await operations.CompleteAsync(admission.Status!.Id, OperationState.Succeeded,
                    result: AgentJson.ToElement(final), ct: CancellationToken.None).ConfigureAwait(false));
            },
            stored => Task.FromResult(Ok(stored)));

    /// <summary>
    /// The shared mutation flow: parse the body (400 on malformed JSON or a missing envelope field), admit it, execute a
    /// new operation (an <see cref="AgentException"/> before the first stage fails the operation and answers with the
    /// error), or answer a replay from the stored status (a stored refusal answers with its error again).
    /// </summary>
    private static async Task<IResult> MutateAsync<TRequest>(
        HttpContext http,
        AgentRoute route,
        Func<TRequest, string> target,
        Func<TRequest, MutationAdmission, CancellationToken, Task<IResult>> execute,
        Func<OperationStatus, Task<IResult>> replay)
        where TRequest : AgentMutation
    {
        JsonElement body;
        TRequest? request;
        try
        {
            using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted).ConfigureAwait(false);
            body = document.RootElement.Clone();
            request = body.Deserialize<TRequest>(AgentJson.Options);
        }
        catch (JsonException ex)
        {
            return Error(StorageErrors.InvalidRequest($"The request body is not a valid {typeof(TRequest).Name}: {ex.Message}"));
        }

        if (request is null)
        {
            return Error(StorageErrors.InvalidRequest("The request body is empty."));
        }

        var operations = http.RequestServices.GetRequiredService<StorageOperations>();
        string operationTarget = target(request) ?? "";
        var admission = await operations.AdmitAsync(http.Request, request, route.OperationKind!, operationTarget, body, http.RequestAborted)
            .ConfigureAwait(false);
        switch (admission.Outcome)
        {
            case MutationAdmissionOutcome.Refused:
                return Error(admission.Error!);
            case MutationAdmissionOutcome.Replay:
                var stored = admission.Status!;
                return StorageOperations.IsRefusal(stored)
                    ? Error(new AgentException(StorageErrors.StatusFor(stored.Error!.Code), stored.Error))
                    : await replay(stored).ConfigureAwait(false);
        }

        try
        {
            // The operation is durable now: a client abort must not leave it half-done.
            return await execute(request, admission, CancellationToken.None).ConfigureAwait(false);
        }
        catch (AgentException ex)
        {
            await TryFailAsync(operations, admission.Status!.Id, ex.Error).ConfigureAwait(false);
            return Error(ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never leave an accepted operation active because of an unexpected failure (W2 review MN-092): active
            // operations are never pruned and would show as running until the next agent start.
            var error = StorageErrors.Create(ErrorCodes.InternalError, "The operation failed: " + ex.Message);
            await TryFailAsync(operations, admission.Status!.Id, error.Error).ConfigureAwait(false);
            return Error(error);
        }
    }

    private static async Task TryFailAsync(StorageOperations operations, string operationId, AgentError error)
    {
        try
        {
            await operations.CompleteAsync(operationId, OperationState.Failed, error, ct: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The log is unavailable: the operation stays active and recovery at the next start settles it.
        }
    }

    private static async Task<NodeDownloadStatus> ReplayDownloadAsync(HttpContext http, OperationStatus stored)
    {
        if (stored.ReadResult<NodeDownloadStatus>() is { } final)
        {
            return final;
        }

        var downloads = http.RequestServices.GetRequiredService<AgentDownloadService>();
        var current = (await downloads.ListAsync(http.RequestAborted).ConfigureAwait(false))
            .FirstOrDefault(d => d.OperationId == stored.Id);
        return current ?? new NodeDownloadStatus
        {
            Folder = stored.Target,
            RepoId = "",
            State = DownloadState.Queued,
            OperationId = stored.Id,
            StartedAt = stored.StartedAt,
        };
    }

    private static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (AgentException ex)
        {
            return Error(ex);
        }
    }

    private static IResult Ok<T>(T value) => Json(value, 200);

    private static IResult Json<T>(T value, int status) => Results.Json(value, AgentJson.Options, AgentJson.JsonContentType, status);

    private static IResult Error(AgentException ex) =>
        Results.Json(new AgentErrorResponse(ex.Error), AgentJson.Options, AgentJson.JsonContentType, ex.StatusCode);
}
