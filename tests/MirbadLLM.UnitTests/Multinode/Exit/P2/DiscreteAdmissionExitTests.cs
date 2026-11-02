using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Multinode.P1Exit;
using static MirbadLLM.UnitTests.Multinode.P2Exit.P2Env;

namespace MirbadLLM.UnitTests.Multinode.P2Exit;

/// <summary>
/// MN-123, design §13.6 "concurrent admission" on a discrete node (§4.8, §6.1, MN-105, D96): N parallel starts through the
/// master's admin API on a real discrete PC agent admit exactly one start per GPU (two reservations, one per card, the host
/// part checked node-wide); every other start answers 409 naming the node and the GPU that is busy; once the first two are
/// Ready the rest start, every instance on its own card and its own port. The DGX beside it keeps one start at a time.
/// </summary>
public sealed class DiscreteAdmissionExitTests
{
    private static readonly (string Id, int Gpu)[] PcModels = [("g0-a", 0), ("g0-b", 0), ("g1-a", 1), ("g1-b", 1)];

    [Fact]
    public async Task ParallelStartsOnADiscreteNodeAdmitOnePerGpuAndRefuseTheOthersNamingTheBusyGpu()
    {
        await using var fleet = await P2Fleet.StartAsync();
        var pc = await fleet.JoinAsync("pc", PcGpus);
        foreach ((string id, int gpu) in PcModels)
        {
            await fleet.Master.CreateModelAsync(ModelBody(id, "pc", [PcGpus[gpu]], budgetGiB: 4));
        }

        pc.Node.StartsHeld = true; // every instance stays Starting: its reservation is held
        var answers = await Task.WhenAll(PcModels.Select(async m =>
            (m.Id, m.Gpu, Answer: await SendAsync(fleet.Master.Admin.PostAsync($"/api/models/{m.Id}/start", content: null, Ct)))));

        // Exactly one start per GPU.
        var admitted = answers.Where(a => a.Answer.Status == HttpStatusCode.OK).ToList();
        Assert.True(admitted.Count == 2, string.Join(" | ", answers.Select(a => $"{a.Id}: {(int)a.Answer.Status} {a.Answer.Body}")));
        Assert.Equal([0, 1], admitted.Select(a => a.Gpu).Order());
        foreach (var refused in answers.Where(a => a.Answer.Status != HttpStatusCode.OK))
        {
            Assert.True(refused.Answer.Status == HttpStatusCode.Conflict, $"{refused.Id}: {(int)refused.Answer.Status} {refused.Answer.Body}");
            string starting = admitted.Single(a => a.Gpu == refused.Gpu).Id;
            string detail = Detail(refused.Answer.Body);
            Assert.Contains($"Model '{starting}' is still starting. Start '{refused.Id}' once it is Ready", detail, StringComparison.Ordinal);
            Assert.EndsWith($"on node pc (GPU {refused.Gpu}).", detail, StringComparison.Ordinal);
        }

        // The agent holds two reservations, one per card; only the admitted models have an instance.
        var client = fleet.Master.Client("pc");
        var reservations = (await client.GetStatusAsync(Ct)).Reservations;
        Assert.Equal(admitted.Select(a => a.Id).Order(StringComparer.Ordinal), reservations.Select(r => r.ModelId).Order(StringComparer.Ordinal));
        Assert.Equal(PcGpus.Order(StringComparer.Ordinal), reservations.SelectMany(r => r.Gpus).Order(StringComparer.Ordinal));
        Assert.Equal(admitted.Select(a => a.Id).Order(StringComparer.Ordinal),
            (await client.ListRuntimesAsync(new RuntimesQuery(), Ct)).Select(r => r.ModelId).Order(StringComparer.Ordinal));

        // Ready releases them; the other two then start side by side (again one per GPU).
        pc.Node.StartsHeld = false;
        foreach (var a in admitted)
        {
            await fleet.Master.WaitForModelAsync(a.Id, ModelState.Ready);
        }

        await ExitEnv.UntilAsync(async () => (await client.GetStatusAsync(Ct)).Reservations.Count == 0, "the reservations to be released on Ready");
        var rest = await Task.WhenAll(answers.Where(a => a.Answer.Status != HttpStatusCode.OK).Select(async a =>
            (a.Id, Answer: await SendAsync(fleet.Master.Admin.PostAsync($"/api/models/{a.Id}/start", content: null, Ct)))));
        Assert.All(rest, r => Assert.True(r.Answer.Status == HttpStatusCode.OK, $"{r.Id}: {(int)r.Answer.Status} {r.Answer.Body}"));
        foreach (var r in rest)
        {
            await fleet.Master.WaitForModelAsync(r.Id, ModelState.Ready);
        }

        // Four instances on their own cards and ports, one container each.
        var all = await client.ListRuntimesAsync(new RuntimesQuery(), Ct);
        Assert.Equal(PcModels.Select(m => m.Id).Order(StringComparer.Ordinal), all.Select(r => r.ModelId).Order(StringComparer.Ordinal));
        Assert.Equal(all.Count, all.Select(r => r.HostPort).Distinct().Count());
        Assert.Equal(4, pc.Docker.ModelContainers.Count);
        foreach ((string id, int gpu) in PcModels)
        {
            JsonNode create = JsonNode.Parse(Assert.Single(pc.CreateBodiesOf(id)))!;
            Assert.Equal([PcGpus[gpu]], create["HostConfig"]!["DeviceRequests"]![0]!["DeviceIDs"]!.AsArray().Select(d => d!.GetValue<string>()));
        }

        Assert.Empty(fleet.Dgx.Docker.ModelContainers);
    }

    private static string Detail(string problemJson)
    {
        using var doc = JsonDocument.Parse(problemJson);
        return doc.RootElement.GetProperty("detail").GetString() ?? "";
    }
}
