using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.UnitTests.Multinode.Contract;
using Xunit;

namespace MirbadLLM.UnitTests.Agent;

public sealed class MoveTransferTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MasterCopiesExactRangesAndDuplicateRangeReportsDurableOffset()
    {
        await using var source = await ContractNode.CreateAsync(enroll: true, Ct);
        await using var target = await ContractNode.CreateAsync(enroll: true, Ct);
        const string folder = "move-checkpoint";
        string sourceFolder = Path.Combine(source.ModelsRoot, folder);
        Directory.CreateDirectory(sourceFolder);
        byte[] data = [21, 55, 0, 7, 8, 11, 3];
        await File.WriteAllBytesAsync(Path.Combine(sourceFolder, "model.gguf"), data, Ct);
        await File.WriteAllBytesAsync(Path.Combine(sourceFolder, ".asset"), [], Ct);
        await using var sourceHost = await RealAgentHarness.StartAsync(source, ct: Ct);
        await using var targetHost = await RealAgentHarness.StartAsync(target, ct: Ct);
        using var sourceClient = new AgentContractClient(sourceHost.Target);
        using var targetClient = new AgentContractClient(targetHost.Target);

        var openSource = await sourceClient.SendAsync(AgentRoutes.MoveTransferOpen, [],
            Open(MoveTransferRole.Export, source.NodeUid, target.NodeUid, folder), ct: Ct);
        Assert.Equal(202, openSource.Status);
        var exported = openSource.Read<MoveTransferStatus>();
        var sourceLease = await LeaseAsync(sourceClient, exported.TransferId);
        _ = await WaitAsync(sourceClient, exported.TransferId, MoveTransferPhase.Ready);
        using var manifestResponse = await sourceClient.SendTransferDataAsync(AgentRoutes.MoveTransferManifest, [exported.TransferId], sourceLease.Grant, ct: Ct);
        Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);
        var manifest = (await manifestResponse.Content.ReadFromJsonAsync<MoveFolderManifest>(AgentJson.Options, Ct))!;
        Assert.Equal(MoveFolderManifest.IdFor(manifest.Files), manifest.ManifestId);
        Assert.Equal([".asset", "model.gguf"], manifest.Files.Select(file => file.Path));
        string digest = Convert.ToHexStringLower(SHA256.HashData(data));
        Assert.Equal(digest, manifest.Files[1].Sha256);
        Assert.Equal(data.Length, manifest.Files[1].Size);

        using var unauthorized = await sourceClient.SendTransferDataAsync(AgentRoutes.MoveTransferRead, [exported.TransferId, "1"],
            new string('0', 64), new RangeHeaderValue(0, 2), $"\"sha256-{digest}\"", ct: Ct);
        Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);
        using var outOfRange = await sourceClient.SendTransferDataAsync(AgentRoutes.MoveTransferRead, [exported.TransferId, "1"],
            sourceLease.Grant, new RangeHeaderValue(0, data.Length), $"\"sha256-{digest}\"", ct: Ct);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, outOfRange.StatusCode);

        var openTarget = await targetClient.SendAsync(AgentRoutes.MoveTransferOpen, [],
            Open(MoveTransferRole.Import, source.NodeUid, target.NodeUid, folder) with { ManifestId = manifest.ManifestId, Manifest = manifest }, ct: Ct);
        Assert.Equal(202, openTarget.Status);
        var imported = openTarget.Read<MoveTransferStatus>();
        var targetLease = await LeaseAsync(targetClient, imported.TransferId);
        var initial = await WaitAsync(targetClient, imported.TransferId, MoveTransferPhase.Ready);
        Assert.Equal(0, initial.NextOffset);
        Assert.False(Directory.Exists(Path.Combine(target.ModelsRoot, folder)));

        for (int start = 0; start < data.Length;)
        {
            int inclusiveEnd = Math.Min(start + 2, data.Length - 1);
            using var read = await sourceClient.SendTransferDataAsync(AgentRoutes.MoveTransferRead, [exported.TransferId, "1"],
                sourceLease.Grant, new RangeHeaderValue(start, inclusiveEnd), $"\"sha256-{digest}\"", ct: Ct);
            Assert.Equal(HttpStatusCode.PartialContent, read.StatusCode);
            Assert.Equal(new ContentRangeHeaderValue(start, inclusiveEnd, data.Length), read.Content.Headers.ContentRange);
            Assert.Equal(inclusiveEnd - start + 1, read.Content.Headers.ContentLength);
            Assert.Equal($"\"sha256-{digest}\"", read.Headers.ETag?.Tag);
            byte[] chunk = await read.Content.ReadAsByteArrayAsync(Ct);
            Assert.Equal(data[start..(inclusiveEnd + 1)], chunk);
            var ack = await WriteAsync(chunk, start, inclusiveEnd);
            Assert.Equal(inclusiveEnd + 1, ack.BytesDownloaded);
            Assert.Equal(ack, await WriteAsync(chunk, start, inclusiveEnd));
            start = inclusiveEnd + 1;
        }

        var finished = await targetClient.SendAsync(AgentRoutes.MoveTransferFinish, [imported.TransferId], Action(), ct: Ct);
        Assert.Equal(200, finished.Status);
        _ = await WaitAsync(targetClient, imported.TransferId, MoveTransferPhase.Completed);
        string moved = Path.Combine(target.ModelsRoot, folder);
        Assert.Empty(await File.ReadAllBytesAsync(Path.Combine(moved, ".asset"), Ct));
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(moved, "model.gguf"), Ct));
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(sourceFolder, "model.gguf"), Ct));
        var history = await targetClient.SendAsync(AgentRoutes.OperationsList, [], ct: Ct);
        Assert.DoesNotContain(targetLease.Grant, history.Body, StringComparison.Ordinal);
        string journal = await File.ReadAllTextAsync(Path.Combine(target.StateDirectory, "move-transfers", imported.TransferId + ".json"), Ct);
        Assert.Contains(manifest.ManifestId, journal, StringComparison.Ordinal);
        Assert.DoesNotContain(targetLease.Grant, journal, StringComparison.Ordinal);

        async Task<MoveTransferStatus> WriteAsync(byte[] chunk, int start, int inclusiveEnd)
        {
            using var body = new ByteArrayContent(chunk);
            body.Headers.ContentRange = new ContentRangeHeaderValue(start, inclusiveEnd, data.Length);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await targetClient.SendTransferDataAsync(AgentRoutes.MoveTransferWrite, [imported.TransferId, "1"],
                targetLease.Grant, etag: $"\"{manifest.ManifestId}\"", content: body, ct: Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<MoveTransferStatus>(AgentJson.Options, Ct))!;
        }
    }

    private static MoveTransferOpenRequest Open(MoveTransferRole role, string source, string target, string folder) => new()
    {
        OperationId = Guid.NewGuid().ToString("N"), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        MoveId = "move-checkpoint", ModelId = "model", Folder = folder, PlacementGeneration = 2,
        Role = role, SourceUid = source, TargetUid = target,
    };

    private static MoveTransferActionRequest Action() => new()
    {
        OperationId = Guid.NewGuid().ToString("N"), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
    };

    private static async Task<MoveTransferLease> LeaseAsync(AgentContractClient client, string id)
    {
        var request = Action();
        var reply = await client.SendAsync(AgentRoutes.MoveTransferLease, [id], request, ct: Ct);
        Assert.Equal(200, reply.Status);
        var lease = reply.Read<MoveTransferLease>();
        var replay = await client.SendAsync(AgentRoutes.MoveTransferLease, [id], request, ct: Ct);
        Assert.Equal(409, replay.Status);
        Assert.DoesNotContain(lease.Grant, replay.Body, StringComparison.Ordinal);
        return lease;
    }

    private static async Task<MoveTransferStatus> WaitAsync(AgentContractClient client, string id, MoveTransferPhase phase)
    {
        for (int retry = 0; retry < 500; retry++)
        {
            var response = await client.SendAsync(AgentRoutes.MoveTransferGet, [id], ct: Ct);
            Assert.Equal(200, response.Status);
            var status = response.Read<MoveTransferStatus>();
            Assert.True(status.Phase is not (MoveTransferPhase.Cancelled or MoveTransferPhase.Failed), status.Error?.Message);
            if (status.Phase == phase)
            {
                return status;
            }

            await Task.Delay(10, Ct);
        }

        throw new TimeoutException($"Transfer did not reach {phase}.");
    }
}
