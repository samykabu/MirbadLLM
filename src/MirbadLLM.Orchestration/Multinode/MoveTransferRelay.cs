using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Downloads.Native;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Audit;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>Checkpoint bytes travel directly between the two pinned client streams through bounded master buffers.</summary>
internal sealed class MoveTransferRelay(IAgentClientFactory clients, AgentOperationRunner runner, TimeProvider time,
    MoveServiceOptions options)
{
    private static readonly TimeSpan RenewalLead = TimeSpan.FromMinutes(2.5);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _slots = new(StringComparer.Ordinal);

    public async Task<MoveFolderWork> CopyAsync(OwnershipRecord record, MoveFolderWork work,
        Func<MoveFolderWork, CancellationToken, Task> save, Func<CancellationToken, Task> check,
        Func<MoveFolderWork, MoveFolderManifest, CancellationToken, Task<MoveFolderWork>> classify, CancellationToken ct)
    {
        var move = record.Move!;
        var source = clients.GetClient(move.SourceNodeId);
        var target = clients.GetClient(move.TargetNodeId);
        if (work.ExportOperationId is null)
        {
            work = work with { ExportOperationId = runner.CreateEnvelope().OperationId, Route = "source", State = DownloadState.Queued };
            await save(work, ct).ConfigureAwait(false);
        }

        await check(ct).ConfigureAwait(false);
        var export = await FindOrOpenAsync(source, record, work, MoveTransferRole.Export, null, ct).ConfigureAwait(false);
        work = work with { ExportId = export.TransferId };
        await save(work, ct).ConfigureAwait(false);
        var sourceLease = await LeaseAsync(source, export.TransferId, null, check, ct).ConfigureAwait(false);
        export = await WaitReadyAsync(source, export, sourceLease, check, ct).ConfigureAwait(false);
        sourceLease = await LeaseAsync(source, export.TransferId, sourceLease, check, ct).ConfigureAwait(false);
        var manifest = await source.GetTransferManifestAsync(export.TransferId, sourceLease.Grant, ct).ConfigureAwait(false);
        long expectedBytes = MoveManifestRules.Validate(manifest);
        var fileSet = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        if (work.ManifestId is { } expected && expected != manifest.ManifestId ||
            work.RequiredFiles.Any(required => !fileSet.Contains(required)))
        {
            throw new InvalidDataException("The source snapshot changed or does not contain a required model file.");
        }

        work = work with
        {
            ManifestId = manifest.ManifestId, ImportOperationId = work.ImportOperationId ?? runner.CreateEnvelope().OperationId,
            TotalBytes = expectedBytes,
        };
        work = await classify(work, manifest, ct).ConfigureAwait(false);
        await save(work, ct).ConfigureAwait(false);
        await check(ct).ConfigureAwait(false);
        var import = await FindOrOpenAsync(target, record, work, MoveTransferRole.Import, manifest, ct).ConfigureAwait(false);
        work = work with { ImportId = import.TransferId };
        await save(work, ct).ConfigureAwait(false);
        MoveTransferLease? targetLease = null;
        if (import.Phase != MoveTransferPhase.Completed)
        {
            targetLease = await LeaseAsync(target, import.TransferId, null, check, ct).ConfigureAwait(false);
            import = await WaitReadyAsync(target, import, targetLease, check, ct).ConfigureAwait(false);
        }

        work = work with { Route = import.BytesDownloaded == expectedBytes && work.BytesDownloaded == 0 ? "existing" : "source", State = DownloadState.Downloading,
            BytesDownloaded = import.BytesDownloaded };
        await save(work, ct).ConfigureAwait(false);

        var nodes = new[] { move.SourceNodeId, move.TargetNodeId };
        Array.Sort(nodes, StringComparer.Ordinal);
        var first = _slots.GetOrAdd(nodes[0], static _ => new SemaphoreSlim(1));
        var second = _slots.GetOrAdd(nodes[1], static _ => new SemaphoreSlim(1));
        int failures = 0;
        bool renew = false;
        while (import.FileIndex is { } index && import.NextOffset is { } offset)
        {
            if (index < 0 || index >= manifest.Files.Count || offset < 0 || offset >= manifest.Files[index].Size ||
                import.BytesDownloaded < 0 || import.BytesDownloaded > expectedBytes)
            {
                throw new InvalidDataException("The receiving node reported an invalid checkpoint offset.");
            }

            await first.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await second.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var member = manifest.Files[index];
                    long end = offset + Math.Min(Protocol.MoveRangeBytes, member.Size - offset) - 1;
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2), time);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
                    var token = linked.Token;
                    sourceLease = await LeaseAsync(source, export.TransferId, renew ? null : sourceLease, check, token).ConfigureAwait(false);
                    targetLease = await LeaseAsync(target, import.TransferId, renew ? null : targetLease, check, token).ConfigureAwait(false);
                    renew = false;
                    await check(token).ConfigureAwait(false);
                    using var response = await source.Data.ReadTransferRangeAsync(export.TransferId, sourceLease.Grant, index,
                        offset, end, member.Sha256, token).ConfigureAwait(false);
                    long length = end - offset + 1;
                    if (response.StatusCode != HttpStatusCode.PartialContent ||
                        response.Content.Headers.ContentRange is not { Unit: "bytes" } range ||
                        range.From != offset || range.To != end || range.Length != member.Size ||
                        response.Content.Headers.ContentLength != length ||
                        response.Headers.ETag is not { IsWeak: false } tag || tag.Tag != $"\"sha256-{member.Sha256}\"")
                    {
                        throw new InvalidDataException("The source answered with a different checkpoint range.");
                    }

                    await check(token).ConfigureAwait(false);
                    using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    using var content = new RangeContent(stream, length, options.MaxTransferMiBPerSecond, time, check);
                    var next = await target.Data.WriteTransferRangeAsync(import.TransferId, targetLease.Grant, index, offset,
                        end, member.Size, manifest.ManifestId, content, token).ConfigureAwait(false);
                    if (next.TransferId != import.TransferId || next.Phase != MoveTransferPhase.Ready ||
                        next.BytesDownloaded <= import.BytesDownloaded || next.BytesDownloaded > expectedBytes)
                    {
                        throw new InvalidDataException("The receiving node did not acknowledge the checkpoint range.");
                    }

                    import = next;
                    failures = 0;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && RetriableRange(ex) && ++failures < 3)
                {
                    renew = true;
                    await check(ct).ConfigureAwait(false);
                    var recovered = await target.GetTransferAsync(import.TransferId, ct).ConfigureAwait(false);
                    if (recovered is { Phase: MoveTransferPhase.Ready })
                    {
                        import = recovered;
                    }

                    await Task.Delay(options.RetryInterval, time, ct).ConfigureAwait(false);
                }
                finally
                {
                    second.Release();
                }
            }
            finally
            {
                first.Release();
            }

            work = work with { BytesDownloaded = import.BytesDownloaded };
            await save(work, ct).ConfigureAwait(false);
        }

        if (import.BytesDownloaded != expectedBytes)
        {
            throw new InvalidDataException("The receiving node has not acknowledged the complete checkpoint.");
        }

        if (import.Phase != MoveTransferPhase.Completed)
        {
            await FinishAndWaitAsync(target, import, record, check, ct).ConfigureAwait(false);
        }

        if (export.Phase != MoveTransferPhase.Completed)
        {
            await FinishAndWaitAsync(source, export, record, check, ct).ConfigureAwait(false);
        }

        return work with { State = DownloadState.Completed, BytesDownloaded = expectedBytes, Error = null };
    }

    private async Task<MoveTransferStatus> FindOrOpenAsync(IAgentClient agent, OwnershipRecord record, MoveFolderWork work,
        MoveTransferRole role, MoveFolderManifest? manifest, CancellationToken ct)
    {
        bool export = role == MoveTransferRole.Export;
        if ((export ? work.ExportId : work.ImportId) is { } id &&
            await agent.GetTransferAsync(id, ct).ConfigureAwait(false) is { } known)
        {
            return known;
        }

        var move = record.Move!;
        var body = new MoveTransferOpenRequest
        {
            OperationId = (export ? work.ExportOperationId : work.ImportOperationId)!,
            IssuedAt = record.StartedAt, ExpiresAt = record.StartedAt.AddHours(24),
            MoveId = record.Id, ModelId = record.ModelId, PlacementGeneration = record.PlacementGeneration, Role = role,
            SourceUid = move.SourceNodeUid, TargetUid = move.TargetNodeUid, Folder = work.Folder,
            ManifestId = work.ManifestId, Manifest = manifest,
        };
        return await runner.SubmitAsync(agent.NodeId, AgentRoutes.MoveTransferOpen, work.Folder, body,
            static (client, request, token) => client.OpenTransferAsync(request, token),
            StateOf, status => status.Error?.Code, AuditOwners.Move(record.Id), ct).ConfigureAwait(false);
    }

    private async Task<MoveTransferStatus> WaitReadyAsync(IAgentClient agent, MoveTransferStatus status, MoveTransferLease lease,
        Func<CancellationToken, Task> check, CancellationToken ct)
    {
        while (status.Phase is MoveTransferPhase.Preparing or MoveTransferPhase.Verifying)
        {
            lease = await LeaseAsync(agent, status.TransferId, lease, check, ct).ConfigureAwait(false);
            await Task.Delay(options.PollInterval, time, ct).ConfigureAwait(false);
            status = await agent.GetTransferAsync(status.TransferId, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("The agent lost its checkpoint transfer journal.");
        }

        if (status.Phase is not (MoveTransferPhase.Ready or MoveTransferPhase.Completed))
        {
            var error = status.Error ?? new AgentError { Code = ErrorCodes.FolderConflict, Message = "The checkpoint transfer ended." };
            throw new AgentException(ErrorMappings.AgentStatus(error.Code), error);
        }

        return status;
    }

    private async Task<MoveTransferLease> LeaseAsync(IAgentClient agent, string id, MoveTransferLease? lease,
        Func<CancellationToken, Task> check, CancellationToken ct)
    {
        await check(ct).ConfigureAwait(false);
        return lease is not null && lease.ExpiresAt - time.GetUtcNow() > RenewalLead
            ? lease : await agent.LeaseTransferAsync(id, Action(), ct).ConfigureAwait(false);
    }

    private async Task FinishAndWaitAsync(IAgentClient agent, MoveTransferStatus transfer, OwnershipRecord record,
        Func<CancellationToken, Task> check, CancellationToken ct)
    {
        string id = transfer.TransferId;
        var lease = await LeaseAsync(agent, id, null, check, ct).ConfigureAwait(false);
        var status = await runner.SubmitAsync(agent.NodeId, AgentRoutes.MoveTransferFinish, id, Action(),
            (client, request, token) => client.FinishTransferAsync(id, request, token),
            StateOf, value => value.Error?.Code, AuditOwners.Move(record.Id), ct).ConfigureAwait(false);
        status = await WaitReadyAsync(agent, status, lease, check, ct).ConfigureAwait(false);
        if (status.Phase != MoveTransferPhase.Completed)
        {
            throw new InvalidDataException("The agent did not finalize the verified checkpoint.");
        }
    }

    public Task<MoveTransferStatus> CancelAsync(string node, string id, CancellationToken ct) =>
        clients.GetClient(node).CancelTransferAsync(id, Action(), ct);

    private MoveTransferActionRequest Action()
    {
        var envelope = runner.CreateEnvelope();
        return new MoveTransferActionRequest
        {
            OperationId = envelope.OperationId, IssuedAt = envelope.IssuedAt, ExpiresAt = envelope.ExpiresAt,
        };
    }

    private static bool RetriableRange(Exception error) =>
        error is AgentUnreachableException or IOException or HttpRequestException or OperationCanceledException ||
        error is AgentException { StatusCode: 503 } or
            AgentException { Code: ErrorCodes.FolderConflict or ErrorCodes.TransferLeaseRequired };

    private static OperationState? StateOf(MoveTransferStatus status) => status.Phase switch
    {
        MoveTransferPhase.Completed => OperationState.Succeeded,
        MoveTransferPhase.Cancelled => OperationState.Cancelled,
        MoveTransferPhase.Failed => OperationState.Failed,
        _ => null,
    };

    private sealed class RangeContent(Stream source, long length, int limit, TimeProvider time, Func<CancellationToken, Task> check)
        : HttpContent
    {
        protected override bool TryComputeLength(out long size)
        {
            size = length;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
            try
            {
                long sent = 0;
                long started = time.GetTimestamp();
                while (sent < length)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(256 * 1024, length - sent)), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("The source checkpoint range ended early.");
                    }

                    sent += read;
                    var due = TimeSpan.FromSeconds(2.0 * sent / (limit * 1048576.0)) - time.GetElapsedTime(started);
                    if (due > TimeSpan.Zero)
                    {
                        await Task.Delay(due, time, cancellationToken).ConfigureAwait(false);
                    }

                    await check(cancellationToken).ConfigureAwait(false);
                    await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
