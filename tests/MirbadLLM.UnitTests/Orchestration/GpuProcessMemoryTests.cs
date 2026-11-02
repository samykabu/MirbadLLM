using System.Net;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707, xUnit1051 // underscore test names; hand-written fakes ignore cancellation.

namespace MirbadLLM.UnitTests.Orchestration;

/// <summary>
/// Per-model memory from the host agent's <c>GET /gpu-processes</c>: each model's container processes summed, named
/// host services kept apart, and a budget fallback whenever the agent is missing, older (404) or failing
/// (<see cref="ModelMemoryAttribution"/>; the collector's use of it is in <c>TelemetryCollectorTests</c>).
/// </summary>
public sealed class GpuProcessMemoryTests
{
    private const string Qwen35Container = "960769d2c75febb6efba9d52da72b559ec2d1165c8bfb0410b3052ec6b63ff91";
    private const string EmbedContainer = "1111111111111111111111111111111111111111111111111111111111111111";

    /// <summary>The agent's answer for the DGX fixture (see the host agent's GpuProcessesTests).</summary>
    private const string AgentJson = $$"""
        [
          {"pid":2945172,"name":"/home/dgxuser/comfyui-env/bin/python","usedMiB":12327,"containerId":null,"unit":"comfyui.service","displayName":"ComfyUI"},
          {"pid":3062106,"name":"VLLM::EngineCore","usedMiB":3646,"containerId":null,"unit":null,"displayName":null},
          {"pid":2780671,"name":"VLLM::EngineCore","usedMiB":5759,"containerId":null,"unit":"lmstudio-server.service","displayName":null},
          {"pid":3414913,"name":"VLLM::EngineCore","usedMiB":58098,"containerId":"{{Qwen35Container}}","unit":null,"displayName":null},
          {"pid":3419104,"name":"VLLM::EngineCore","usedMiB":17037,"containerId":"{{EmbedContainer}}","unit":null,"displayName":null}
        ]
        """;

    private static HostAgentClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond, bool installed = true) =>
        new(new HttpClient(new FakeHttpMessageHandler { Handler = respond }) { BaseAddress = new Uri("http://hostd/") }, () => installed);

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    // ---------------------------------------------------------------- client

    [Fact]
    public async Task Client_ReadsTheProcesses()
    {
        var client = Client(request =>
        {
            Assert.Equal("/gpu-processes", request.RequestUri!.AbsolutePath);
            return Json(HttpStatusCode.OK, AgentJson);
        });

        var processes = await client.GetGpuProcessesAsync();

        Assert.Equal(5, processes!.Count);
        Assert.Equal(new GpuProcess(3414913, "VLLM::EngineCore", 58098, Qwen35Container, null), processes[3]);
        Assert.Equal("ComfyUI", processes[0].DisplayName);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)] // an agent installed before the endpoint existed
    [InlineData(HttpStatusCode.ServiceUnavailable)] // nvidia-smi failed on the host
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Client_OlderOrFailingAgent_IsNull_NotAnException(HttpStatusCode code)
    {
        var client = Client(_ => Json(code, "{\"error\":\"x\"}"));

        Assert.Null(await client.GetGpuProcessesAsync());
    }

    [Fact]
    public async Task Client_NoAgentSocket_OrNoAnswer_OrGarbage_IsNull()
    {
        Assert.Null(await Client(_ => throw new InvalidOperationException("must not be called"), installed: false).GetGpuProcessesAsync());
        Assert.Null(await Client(_ => throw new HttpRequestException("connection refused")).GetGpuProcessesAsync());
        Assert.Null(await Client(_ => Json(HttpStatusCode.OK, "<html>")).GetGpuProcessesAsync());
    }

    // ---------------------------------------------------------------- mapping

    private static readonly System.Text.Json.JsonSerializerOptions WebJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static HostSample Host() => new(DateTimeOffset.UnixEpoch, new MemorySnapshot(121.7, 10), null);

    private static Dictionary<string, ModelMemoryAttribution.RunningModel> Running() => new(StringComparer.Ordinal)
    {
        ["qwen36-35b-a3b-nvfp4"] = new(Qwen35Container, 48),
        ["qwen3-embedding-4b"] = new(EmbedContainer[..12], 10), // a short id still matches
        ["starting-model"] = new("2222222222222222222222222222222222222222222222222222222222222222", 20), // no GPU process yet
    };

    [Fact]
    public void Measured_SumsEachModelsContainerProcesses_AndNamesHostServices()
    {
        var processes = System.Text.Json.JsonSerializer.Deserialize<List<GpuProcess>>(AgentJson, WebJson)!;

        var sample = ModelMemoryAttribution.WithModelMemory(Host(), Running(), processes);

        Assert.Equal(ModelMemorySource.Measured, sample.MemorySource);
        Assert.Equal(58098 / 1024.0, sample.ModelMemoryGiB!["qwen36-35b-a3b-nvfp4"], 6);
        Assert.Equal(17037 / 1024.0, sample.ModelMemoryGiB["qwen3-embedding-4b"], 6);
        Assert.Equal(0, sample.ModelMemoryGiB["starting-model"]); // measured: nothing allocated yet
        Assert.Equal(12327 / 1024.0, sample.OtherGpuGiB!["ComfyUI"], 6); // the whitelist's display name
        Assert.Equal(5759 / 1024.0, sample.OtherGpuGiB["lmstudio-server"], 6); // an unmanaged unit by its name
        Assert.Equal(2, sample.OtherGpuGiB.Count); // the unowned process stays in "other processes"
    }

    [Fact]
    public void NoProcessReadout_FallsBackToBudgets()
    {
        var sample = ModelMemoryAttribution.WithModelMemory(Host(), Running(), processes: null);

        Assert.Equal(ModelMemorySource.Budget, sample.MemorySource);
        Assert.Equal(48, sample.ModelMemoryGiB!["qwen36-35b-a3b-nvfp4"]);
        Assert.Equal(10, sample.ModelMemoryGiB["qwen3-embedding-4b"]);
        Assert.Null(sample.OtherGpuGiB);
    }

    [Fact]
    public void AnotherContainersProcess_IsNotAttributedToAModel()
    {
        var processes = new List<GpuProcess> { new(1, "python", 4096, "3333333333333333333333333333333333333333333333333333333333333333", null) };

        var sample = ModelMemoryAttribution.WithModelMemory(Host(), Running(), processes);

        Assert.All(sample.ModelMemoryGiB!.Values, v => Assert.Equal(0, v));
        Assert.Empty(sample.OtherGpuGiB!);
    }
}
