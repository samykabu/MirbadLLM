using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Storage;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Api;

public static partial class StorageEndpoints
{
    private static void MapMoveTransfers(IEndpointRouteBuilder routes)
    {
        routes.MapPost(AgentRoutes.MoveTransferOpen.Path, (Func<HttpContext, Task<IResult>>)OpenMoveTransferAsync);
        routes.MapGet(AgentRoutes.MoveTransferGet.Path, (HttpContext http, string transferId) =>
            Guard(() => Task.FromResult(Ok(Transfers(http).Get(transferId)))));
        routes.MapGet(AgentRoutes.MoveTransferManifest.Path, (HttpContext http, string transferId) =>
            Guard(() => Task.FromResult(Ok(Transfers(http).Manifest(transferId, DataMaster(http), Grant(http))))));
        routes.MapPost(AgentRoutes.MoveTransferLease.Path, LeaseMoveTransferAsync);
        routes.MapPost(AgentRoutes.MoveTransferFinish.Path, (HttpContext http, string transferId) =>
            CompleteMoveTransferAsync(http, transferId, cancel: false));
        routes.MapPost(AgentRoutes.MoveTransferCancel.Path, (HttpContext http, string transferId) =>
            CompleteMoveTransferAsync(http, transferId, cancel: true));
        routes.MapGet(AgentRoutes.MoveTransferRead.Path, ReadMoveTransferAsync);
        routes.MapPut(AgentRoutes.MoveTransferWrite.Path, WriteMoveTransferAsync);
    }

    private static Task<IResult> OpenMoveTransferAsync(HttpContext http)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = 65L * 1024 * 1024;
        }

        return MutateAsync<MoveTransferOpenRequest>(http, AgentRoutes.MoveTransferOpen, request => request.Folder,
            async (request, accepted, ct) => Json(await Transfers(http).OpenAsync(request, accepted.Status!, accepted.Master!, ct)
                .ConfigureAwait(false), 202),
            stored => Guard(() => Task.FromResult(Json(Transfers(http).Get(MoveTransferService.IdOf(stored.Id)), 202))));
    }

    private static Task<IResult> LeaseMoveTransferAsync(HttpContext http, string transferId) =>
        MutateAsync<MoveTransferActionRequest>(http, AgentRoutes.MoveTransferLease, _ => transferId,
            async (_, accepted, ct) =>
            {
                var lease = await Transfers(http).LeaseAsync(transferId, accepted.Master!, ct).ConfigureAwait(false);
                await http.RequestServices.GetRequiredService<StorageOperations>().CompleteAsync(accepted.Status!.Id,
                    OperationState.Succeeded, ct: ct).ConfigureAwait(false);
                http.Response.Headers.CacheControl = "no-store";
                return Ok(lease);
            },
            _ => Task.FromResult(Error(StorageErrors.Create(ErrorCodes.Conflict, "Use a fresh operation to issue a new transfer lease."))));

    private static Task<IResult> CompleteMoveTransferAsync(HttpContext http, string transferId, bool cancel) =>
        MutateAsync<MoveTransferActionRequest>(http, cancel ? AgentRoutes.MoveTransferCancel : AgentRoutes.MoveTransferFinish, _ => transferId,
            async (_, accepted, ct) =>
            {
                var service = Transfers(http);
                var status = cancel
                    ? await service.CancelAsync(transferId, accepted.Master!, ct).ConfigureAwait(false)
                    : await service.FinishAsync(transferId, accepted.Master!, ct).ConfigureAwait(false);
                await http.RequestServices.GetRequiredService<StorageOperations>().CompleteAsync(accepted.Status!.Id, OperationState.Succeeded,
                    result: AgentJson.ToElement(status), ct: ct).ConfigureAwait(false);
                return Ok(status);
            },
            stored => Guard(() => Task.FromResult(Ok(stored.ReadResult<MoveTransferStatus>() ?? Transfers(http).Get(transferId)))));

    private static Task<IResult> ReadMoveTransferAsync(HttpContext http, string transferId, int index) =>
        DataResponseAsync(http, async () =>
        {
            var master = DataMaster(http);
            if (!RangeHeaderValue.TryParse(http.Request.Headers.Range, out var range) || range.Unit != "bytes" ||
                range.Ranges.Count != 1 || range.Ranges.Single() is not { From: { } start, To: { } end })
            {
                throw StorageErrors.Create(ErrorCodes.InvalidRange, "One explicit checkpoint byte range is required.");
            }

            await Transfers(http).ReadAsync(transferId, master, Grant(http), index, start, end, http.Request.Headers.IfMatch.ToString(),
                member =>
                {
                    http.Response.StatusCode = 206;
                    http.Response.Headers.ContentRange = new ContentRangeHeaderValue(start, end, member.Size).ToString();
                    http.Response.Headers.ETag = $"\"sha256-{member.Sha256}\"";
                    http.Response.Headers.CacheControl = "no-store";
                    http.Response.ContentLength = end - start + 1;
                    http.Response.ContentType = "application/octet-stream";
                    return http.Response.Body;
                }, http.RequestAborted).ConfigureAwait(false);
            return Results.Empty;
        });

    private static Task<IResult> WriteMoveTransferAsync(HttpContext http, string transferId, int index) =>
        DataResponseAsync(http, async () =>
        {
            var master = DataMaster(http);
            if (!ContentRangeHeaderValue.TryParse(http.Request.Headers.ContentRange, out var range) ||
                range is not { Unit: "bytes", From: { } start, To: { } end, Length: { } total } ||
                end - start >= Protocol.MoveRangeBytes || http.Request.ContentLength != end - start + 1)
            {
                throw StorageErrors.Create(ErrorCodes.InvalidRange, "A bounded Content-Range and its exact Content-Length are required.");
            }

            return Ok(await Transfers(http).WriteAsync(transferId, master, Grant(http), index, start, end, total,
                http.Request.Headers.IfMatch.ToString(), http.Request.Body, http.RequestAborted).ConfigureAwait(false));
        });

    private static async Task<IResult> DataResponseAsync(HttpContext http, Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AgentException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            if (http.Response.HasStarted || http.RequestAborted.IsCancellationRequested)
            {
                http.Abort();
                return Results.Empty;
            }

            http.Response.Clear();
            return Error(ex as AgentException ?? StorageErrors.Create(ErrorCodes.TransferChanged, "The checkpoint range did not complete."));
        }
    }

    private static MoveTransferService Transfers(HttpContext http) => http.RequestServices.GetRequiredService<MoveTransferService>();
    private static string Grant(HttpContext http) => http.Request.Headers[Protocol.TransferGrantHeader].ToString();

    private static MasterTriple DataMaster(HttpContext http)
    {
        var time = http.RequestServices.GetRequiredService<TimeProvider>();
        if (!MasterTriple.TryParse(http.Request.Headers[Protocol.MasterHeader], out var master) ||
            !DateTimeOffset.TryParse(http.Request.Headers[Protocol.RequestTimeHeader], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var issued) || (time.GetUtcNow() - issued).Duration() > Protocol.MaxRequestSkew)
        {
            throw StorageErrors.Create(ErrorCodes.Forbidden, "A fresh fenced master request is required for checkpoint data.");
        }

        return master;
    }
}
