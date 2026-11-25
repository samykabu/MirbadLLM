using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Storage;
using MirbadLLM.UnitTests.Multinode.Contract;
using MirbadLLM.UnitTests.Multinode.Fakes;
using Xunit;

namespace MirbadLLM.UnitTests.Agent;

public sealed class MoveTransferTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnActiveRangeEndsWhenLeaseExpiresOrMasterChanges(bool takeover)
    {
        await using var node = await ContractNode.CreateAsync(enroll: true, Ct);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        node.ExtraServices = services => services.AddSingleton<TimeProvider>(clock);
        const string folder = "clock-checkpoint";
        var source = Directory.CreateDirectory(Path.Combine(node.ModelsRoot, folder));
        byte[] data = new byte[512 * 1024];
        await File.WriteAllBytesAsync(Path.Combine(source.FullName, "model.gguf"), data, Ct);
        await using var host = await RealAgentHarness.StartAsync(node, ct: Ct);
        using var client = new AgentContractClient(host.Target);
        var opened = await client.SendAsync(AgentRoutes.MoveTransferOpen, [],
            Open(MoveTransferRole.Export, node.NodeUid, "receiver", folder), ct: Ct);
        Assert.Equal(202, opened.Status);
        string id = opened.Read<MoveTransferStatus>().TransferId;
        var lease = await LeaseAsync(client, id);
        _ = await WaitAsync(client, id, MoveTransferPhase.Ready);
        var service = host.App.Services.GetRequiredService<MoveTransferService>();
        string etag = $"\"sha256-{Convert.ToHexStringLower(SHA256.HashData(data))}\"";
        using var body = new BlockingRange();
        var inFlight = service.ReadAsync(id, client.Master, lease.Grant, 0, 0, data.Length - 1, etag, _ => body, Ct);
        await body.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.InRange(body.Length, 1, data.Length - 1);

        if (takeover)
        {
            var fence = host.App.Services.GetRequiredService<IFencingState>();
            var changed = client.Master with { Epoch = client.Master.Epoch + 1, BootId = "next-boot" };
            await fence.CheckAsync(changed, Ct);
            Assert.True(fence.IsCurrent(changed));
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        else
        {
            clock.Advance(TimeSpan.FromMinutes(5));
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        using var refused = new MemoryStream();
        var error = await Assert.ThrowsAsync<AgentException>(() =>
            service.ReadAsync(id, client.Master, lease.Grant, 0, 0, 0, etag, _ => refused, Ct));
        Assert.Equal(ErrorCodes.TransferLeaseRequired, error.Code);
        Assert.Empty(refused.ToArray());
    }

    [Fact]
    public async Task DiskHeadroomIsRequiredBeforeCreatingStagingOrWritingBytes()
    {
        await using var node = await ContractNode.CreateAsync(enroll: true, Ct);
        node.ExtraServices = services =>
        {
            services.Configure<AgentStorageOptions>(options => options.DiskHeadroomFraction = 0.1);
            services.AddSingleton<IDiskSpaceProbe>(new LimitedDisk());
        };
        await using var host = await RealAgentHarness.StartAsync(node, ct: Ct);
        using var client = new AgentContractClient(host.Target);
        byte[] data = new byte[100];
        var (id, _) = await OpenImportAsync(node, client, "full-checkpoint", "model.gguf", data);
        var failed = await WaitAsync(client, id, MoveTransferPhase.Failed);
        Assert.Equal(ErrorCodes.InsufficientDisk, failed.Error?.Code);
        Assert.Equal(0, failed.BytesDownloaded);
        Assert.False(Directory.Exists(Path.Combine(node.ModelsRoot, ".move-" + id)));
    }

    [Theory]
    [InlineData("model-00001-of-00002.gguf", "", "model-00002-of-00002.gguf")]
    [InlineData("nested/model.safetensors.index.json", "{\"weight_map\":{\"layer\":\"weights-2.safetensors\"}}", "nested/weights-2.safetensors")]
    public async Task ExportRefusesMissingShardByName(string name, string contents, string missing)
    {
        await using var node = await ContractNode.CreateAsync(enroll: true, Ct);
        const string folder = "incomplete-checkpoint";
        string path = Path.Combine(node.ModelsRoot, folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents, Ct);
        await using var host = await RealAgentHarness.StartAsync(node, ct: Ct);
        using var client = new AgentContractClient(host.Target);
        var opened = await client.SendAsync(AgentRoutes.MoveTransferOpen, [], Open(MoveTransferRole.Export, node.NodeUid, "receiver", folder), ct: Ct);
        Assert.Equal(202, opened.Status);
        string id = opened.Read<MoveTransferStatus>().TransferId;
        _ = await LeaseAsync(client, id);
        var failed = await WaitAsync(client, id, MoveTransferPhase.Failed);
        Assert.Contains($"'{missing}'", failed.Error?.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishRefusesSwappedParentOrNewConflictingFile(bool swapParent)
    {
        await using var node = await ContractNode.CreateAsync(enroll: true, Ct);
        string outside = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(node.ModelsRoot)!, "outside")).FullName;
        const string folder = "existing-checkpoint";
        string parent = Directory.CreateDirectory(Path.Combine(node.ModelsRoot, folder, "nested")).FullName;
        string target = Path.Combine(parent, "model.gguf");
        await using var host = await RealAgentHarness.StartAsync(node, ct: Ct);
        var service = host.App.Services.GetRequiredService<MoveTransferService>();
        service.BeforeMergeForTests = async token =>
        {
            if (!swapParent)
            {
                await File.WriteAllTextAsync(target, "keep this file", token);
                return;
            }

            Directory.Move(parent, parent + "-original");
            if (OperatingSystem.IsWindows())
            {
                var junction = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
                foreach (string arg in new[] { "/c", "mklink", "/J", parent, outside })
                {
                    junction.ArgumentList.Add(arg);
                }
                using var process = Process.Start(junction)!;
                await process.WaitForExitAsync(token);
                Assert.Equal(0, process.ExitCode);
            }
            else
            {
                Directory.CreateSymbolicLink(parent, outside);
            }
        };

        using var client = new AgentContractClient(host.Target);
        byte[] data = [1, 2, 3];
        var (id, manifest) = await OpenImportAsync(node, client, folder, "nested/model.gguf", data);
        _ = await WaitAsync(client, id, MoveTransferPhase.Ready);
        using var body = new MemoryStream(data);
        await service.WriteAsync(id, client.Master, manifest.Grant, 0, 0, data.Length - 1, data.Length,
            $"\"{manifest.ManifestId}\"", body, Ct);
        await service.FinishAsync(id, client.Master, Ct);
        var failed = await WaitAsync(client, id, MoveTransferPhase.Failed);
        Assert.Equal(ErrorCodes.FolderConflict, failed.Error?.Code);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
        if (swapParent)
        {
            Assert.Empty(Directory.GetFileSystemEntries(parent + "-original"));
            Directory.Delete(parent);
        }
        else
        {
            Assert.Equal("keep this file", await File.ReadAllTextAsync(target, Ct));
        }
    }

    [Fact]
    public async Task PublishAddsAMissingNestedFileToAnExistingFolder()
    {
        await using var node = await ContractNode.CreateAsync(enroll: true, Ct);
        const string folder = "existing-checkpoint";
        string destination = Directory.CreateDirectory(Path.Combine(node.ModelsRoot, folder)).FullName;
        await using var host = await RealAgentHarness.StartAsync(node, ct: Ct);
        using var client = new AgentContractClient(host.Target);
        byte[] data = [3, 2, 1];
        var (id, manifest) = await OpenImportAsync(node, client, folder, "nested/model.gguf", data);
        _ = await WaitAsync(client, id, MoveTransferPhase.Ready);
        var service = host.App.Services.GetRequiredService<MoveTransferService>();
        using var body = new MemoryStream(data);
        await service.WriteAsync(id, client.Master, manifest.Grant, 0, 0, data.Length - 1, data.Length,
            $"\"{manifest.ManifestId}\"", body, Ct);
        await service.FinishAsync(id, client.Master, Ct);
        _ = await WaitAsync(client, id, MoveTransferPhase.Completed);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(destination, "nested", "model.gguf"), Ct));
    }

    private static async Task<(string Id, (string ManifestId, string Grant) Lease)> OpenImportAsync(ContractNode node,
        AgentContractClient client, string folder, string path, byte[] data)
    {
        MoveManifestFile[] files = [new(path, data.Length, Convert.ToHexStringLower(SHA256.HashData(data)))];
        var manifest = new MoveFolderManifest(1, files, MoveFolderManifest.IdFor(files));
        var opened = await client.SendAsync(AgentRoutes.MoveTransferOpen, [],
            Open(MoveTransferRole.Import, "sender", node.NodeUid, folder) with { ManifestId = manifest.ManifestId, Manifest = manifest }, ct: Ct);
        Assert.Equal(202, opened.Status);
        string id = opened.Read<MoveTransferStatus>().TransferId;
        var lease = await LeaseAsync(client, id);
        return (id, (manifest.ManifestId, lease.Grant));
    }

    private sealed class BlockingRange : MemoryStream
    {
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Length == 0)
            {
                await base.WriteAsync(buffer, cancellationToken);
                return;
            }

            Blocked.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class LimitedDisk : IDiskSpaceProbe
    {
        public DiskSpace Probe(string path) => new(105, 1000);
    }

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
            if (status.Phase == phase)
            {
                return status;
            }

            Assert.True(status.Phase is not (MoveTransferPhase.Cancelled or MoveTransferPhase.Failed), status.Error?.Message);
            await Task.Delay(10, Ct);
        }

        throw new TimeoutException($"Transfer did not reach {phase}.");
    }
}
