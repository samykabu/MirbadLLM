using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using static MirbadLLM.UnitTests.Multinode.P1Exit.ExitEnv;

namespace MirbadLLM.UnitTests.Multinode.P1Exit;

/// <summary>
/// Design §13.6 "pool isolation: saturate the data handler → control calls (status, events) keep their latency" (§7.2:
/// separate control and data handlers per node). Through the real master composition: client streams on <c>/v1</c> go through
/// the master's router and data handler to FakeAgent's proxy, where the model holds every stream open after its first chunk
/// until the master's per-node in-flight bound is reached (the next request is refused locally with <c>server_busy</c>).
/// Meanwhile the master's control calls answer, its event stream delivers a model start, and the node stays Online.
/// </summary>
public sealed class PoolIsolationExitTests
{
    [Fact]
    public async Task ControlCallsAndEventsKeepWorkingWhileTheDataChannelIsSaturated()
    {
        await using var node = await FakeNode.StartAsync(models: [Model("qwen"), Model("other")]);
        await using var master = await node.Master().StartAsync();
        var (started, startBody) = await SendAsync(master.Http.PostAsync("/api/models/qwen/start", content: null, Ct));
        Assert.True(started == HttpStatusCode.OK, $"{(int)started} {startBody}");
        await master.WaitForModelAsync("qwen", ModelState.Ready);

        // Baseline control latency.
        var baseline = Stopwatch.StartNew();
        await master.Client.GetStatusAsync(Ct);
        await master.Client.GetInfoAsync(Ct);
        baseline.Stop();

        // Every stream stalls after its first SSE chunk (a silent model) until the client goes away.
        (await node.Agent.GetModelServerAsync()).SetSilentGapMs((int)TimeSpan.FromMinutes(10).TotalMilliseconds);
        using var streams = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        int saturation = Protocol.DefaultMaxInFlightPerNode;
        var open = await Task.WhenAll(Enumerable.Range(0, saturation).Select(_ => OpenStreamAsync(master, streams.Token)));
        try
        {
            Assert.All(open, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

            // The data channel is full: the next request is refused by the master itself.
            using (var excess = await master.Http.PostAsJsonAsync("/v1/chat/completions", new { model = "qwen", stream = true }, Ct))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, excess.StatusCode);
                Assert.Contains(ErrorCodes.ServerBusy, await excess.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
            }

            // Control calls keep answering promptly on their own handler.
            var bound = TimeSpan.FromSeconds(5) + baseline.Elapsed;
            var control = Stopwatch.StartNew();
            var status = await master.Client.GetStatusAsync(Ct).WaitAsync(bound, Ct);
            var info = await master.Client.GetInfoAsync(Ct).WaitAsync(bound, Ct);
            control.Stop();
            Assert.Equal(NodeId, info.NodeId);
            Assert.Contains(status.Runtimes, r => r.ModelId == "qwen" && r.State == ModelState.Ready);
            Assert.True(control.Elapsed < bound, $"control calls took {control.Elapsed} under saturation (baseline {baseline.Elapsed})");

            // A command and its events: the start of another model reaches the master's cache through the event stream.
            var (other, otherBody) = await SendAsync(master.Http.PostAsync("/api/models/other/start", content: null, Ct).WaitAsync(bound, Ct));
            Assert.True(other == HttpStatusCode.OK, $"{(int)other} {otherBody}");
            await ExitEnv.UntilAsync(() => master.Cache.GetRuntime("other")?.State == ModelState.Ready, "other Ready in the master's cache", bound);
            Assert.Equal(NodeState.Online, master.Cache.GetNode(NodeId)?.State);
        }
        finally
        {
            await streams.CancelAsync();
            foreach (var response in open)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>Opens one streaming chat through the master and reads until its first chunk arrived.</summary>
    private static async Task<HttpResponseMessage> OpenStreamAsync(ExitMaster master, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new { model = "qwen", messages = new[] { new { role = "user", content = "hi" } }, stream = true }),
        };
        var response = await master.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var body = await response.Content.ReadAsStreamAsync(ct);
            byte[] first = new byte[1];
            Assert.Equal(1, await body.ReadAsync(first, ct));
        }

        return response;
    }
}
