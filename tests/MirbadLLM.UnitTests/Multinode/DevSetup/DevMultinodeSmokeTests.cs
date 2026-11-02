using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Enrollment;
using MirbadLLM.Agent.Fakes;
using MirbadLLM.Core;
using MirbadLLM.Web.Enrollment;
using AgentProgram = MirbadLLM.Agent.Program;
using MirbadLLM.Agent.Storage;
using MirbadLLM.UnitTests.Agent;

namespace MirbadLLM.UnitTests.Multinode.DevSetup;

/// <summary>
/// MN-042 / MN-122: the two-agent development setup, in process and CI-safe (no Docker, no GPU, loopback only). The real
/// master (Development, Kestrel, enroll listener, the dev join-token helper) and two real agents with the fake runtime:
/// <c>dgx</c> (unified, as the DGX) enrolls in bootstrap mode; <c>dev-b</c> (discrete, two 16 GiB GPUs) joins with the
/// helper's token and is approved through the node API (<c>POST /api/nodes/dev-b/approve</c>, MN-102). dev-b reports a
/// discrete memory model, its GPUs and <c>gpus.uuids</c>; a fake model is placed on its GPU 1 by UUID (the master's
/// placement validation runs against that report), one starts on each node, and <c>/v1</c> chat (buffered and streamed)
/// reaches both through the router and the agents' mTLS proxies. The scripts in <c>tools/dev-multinode</c> run the same
/// composition as three processes.
/// </summary>
public sealed class DevMultinodeSmokeTests
{
    /// <summary>Host ports each fake agent hands its models (<c>PortRangeStart..PortRangeEnd</c>).</summary>
    private const int ModelPortsPerAgent = 20;

    private static readonly string[] FakeModels = ["fake-dgx", "fake-devb"];

    /// <summary>dev-b's GPUs, as the dev scripts configure them (placeholder UUIDs, W7 review MN-126).</summary>
    private static readonly string[] DevBGpus = ["GPU-00000000-0000-0000-0000-00000000de00", "GPU-00000000-0000-0000-0000-00000000de01"];

    /// <summary>A UUID that is not on dev-b.</summary>
    private static readonly string[] UnknownGpu = ["GPU-00000000-0000-0000-0000-0000000dead0"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MasterDgxAndDevBServeV1ThroughBothNodesWithTheFakeRuntime()
    {
        string root = Path.Combine(Path.GetTempPath(), "ss-dev-multinode-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            // Every fixed port of the composition from one block, as the dev scripts lay them out: the listeners, then
            // one model-port range per agent. Each agent hands out its range from the start, unaware of the other, so
            // the ranges must not overlap. Ports from bind(0) do not work here: the OS hands them out nearly sequentially,
            // so two agents' ranges taken that way overlap, and the ports right after one are soon taken by outgoing
            // connections and other tests' listeners (a fake model whose port is taken fails at once, yet its start still
            // answers 200 and it never shows in /v1/models). TestPorts reserves a block below every ephemeral range.
            int ports = TestPorts.ReserveBlock(3 + 2 * ModelPortsPerAgent);
            int enrollPort = ports;
            int dgxPort = ports + 1;
            int devBPort = ports + 2;
            int dgxModelPorts = ports + 3;
            int devBModelPorts = dgxModelPorts + ModelPortsPerAgent;
            string bootstrapDir = Path.Combine(root, "bootstrap");
            string joinDir = Path.Combine(root, "join");
            await using var master = new DevMasterFactory(new Dictionary<string, string?>
            {
                ["MirbadLLM:DataPath"] = Path.Combine(root, "master-data"),
                ["MirbadLLM:Backup:Directory"] = Path.Combine(root, "backups"),
                ["MirbadLLM:HostAgentSocket"] = Path.Combine(root, "no-hostd.sock"),
                ["MirbadLLM:DockerEndpoint"] = "unix:///nonexistent/mirbadllm-tests/docker.sock",
                ["MirbadLLM:Enrollment:Port"] = enrollPort.ToString(CultureInfo.InvariantCulture),
                ["MirbadLLM:Enrollment:BootstrapDirectory"] = bootstrapDir,
                ["MirbadLLM:Enrollment:BootstrapEnrollEndpoint"] = $"https://127.0.0.1:{enrollPort}",
                ["MirbadLLM:Enrollment:BootstrapAgentAddress"] = $"https://127.0.0.1:{dgxPort}",
                ["MirbadLLM:DevJoinTokens:Nodes:0"] = "dev-b",
                ["MirbadLLM:DevJoinTokens:OutputDirectory"] = joinDir,
                ["MirbadLLM:DevJoinTokens:AutoApprove"] = "false", // approved through the node API below, as the scripts do
            });
            master.UseKestrel();
            master.StartServer();
            using var http = new HttpClient { BaseAddress = master.HttpAddress(), Timeout = TimeSpan.FromSeconds(60) };

            // dgx: bootstrap mode, as on the DGX.
            await using WebApplication dgx = await StartAgentAsync(root, "dgx", dgxPort, dgxModelPorts, o =>
            {
                o.BootstrapDirectory = bootstrapDir;
                o.BootstrapWait = TimeSpan.FromSeconds(60);
                o.Addresses.Add("127.0.0.1");
            });

            // dev-b: the helper's join file, `mirbadllm-agent join`, then the agent.
            string joinFile = Path.Combine(joinDir, DevJoinFile.FileName("dev-b"));
            await WaitUntilAsync(() => Task.FromResult(File.Exists(joinFile)), "the dev join-token helper wrote no join file for dev-b");
            var join = JsonSerializer.Deserialize<DevJoinFile>(await File.ReadAllTextAsync(joinFile, Ct), JsonSerializerOptions.Web)!;
            Assert.Equal("dev-b", join.NodeId);
            using (var output = new StringWriter())
            using (var error = new StringWriter())
            {
                int exit = await JoinCommand.RunAsync(
                    ["--enroll-url", join.EnrollUrl.ToString(), "--token", join.Token, "--ca-hash", join.CaHash, "--node-id", "dev-b",
                     "--state-dir", Path.Combine(root, "dev-b", "state"), "--address", $"https://127.0.0.1:{devBPort}"],
                    output, error, ct: Ct);
                Assert.True(exit == JoinCommand.ExitOk, $"join exited {exit}: {output}{error}");
            }

            // Enrolled, not approved: the helper only mints tokens; the operator's approval is the node API (MN-102).
            var registry = master.Services.GetRequiredService<INodeRegistry>();
            Assert.False((await registry.GetAsync("dev-b", Ct))!.Approved);
            using (var approve = await http.PostAsync("/api/nodes/dev-b/approve", content: null, Ct))
            {
                Assert.True(approve.StatusCode == HttpStatusCode.OK, $"approve dev-b: {(int)approve.StatusCode} {await approve.Content.ReadAsStringAsync(Ct)}");
            }

            await using WebApplication devB = await StartAgentAsync(root, "dev-b", devBPort, devBModelPorts, configure: null, memory: "discrete:2x16", gpuUuids: DevBGpus);

            // Both nodes Online: dgx pre-approved by its bootstrap token, dev-b approved above.
            var cache = master.Services.GetRequiredService<INodeStatusCache>();
            await WaitUntilAsync(() => Task.FromResult(cache.GetNode("dgx")?.State == NodeState.Online), "dgx never came Online");
            await WaitUntilAsync(() => Task.FromResult(cache.GetNode("dev-b")?.State == NodeState.Online), "dev-b never came Online");
            var devBRecord = await registry.GetAsync("dev-b", Ct);
            Assert.NotNull(devBRecord);
            Assert.True(devBRecord.Approved);
            Assert.NotNull(devBRecord.CertSpkiSha256);

            // dev-b reports a discrete node with two 16 GiB GPUs and the P2 feature its fake implements; dgx stays P1's
            // unified node. Placement validation reads the stored report, so wait until it is written.
            await WaitUntilAsync(async () => (await registry.GetAsync("dev-b", Ct))?.LastCapabilities?.Memory.Model == MemoryModel.Discrete,
                "dev-b's capability report never reached the registry as discrete");
            NodeCapabilities devBCaps = (await registry.GetAsync("dev-b", Ct))!.LastCapabilities!;
            Assert.Equal(DevBGpus, devBCaps.Gpus.OrderBy(g => g.Index).Select(g => g.Uuid));
            Assert.All(devBCaps.Gpus, g =>
            {
                Assert.Equal(16, g.MemoryTotalGiB);
                Assert.False(g.SharesHostMemory);
            });
            Assert.Contains(AgentFeatures.GpuSelectionUuids, devBCaps.Features);
            Assert.DoesNotContain(AgentFeatures.LaunchEnv, devBCaps.Features); // the container driver's, not the fake's
            await WaitUntilAsync(async () => (await registry.GetAsync("dgx", Ct))?.LastCapabilities is not null,
                "dgx's capability report never reached the registry");
            NodeCapabilities dgxCaps = (await registry.GetAsync("dgx", Ct))!.LastCapabilities!;
            Assert.Equal(MemoryModel.Unified, dgxCaps.Memory.Model);
            Assert.Empty(dgxCaps.Gpus);
            Assert.Empty(dgxCaps.Features.Intersect(AgentFeatures.P2));

            // /status of dev-b carries the per-GPU memory (the discrete reader on the fake node).
            await WaitUntilAsync(() => Task.FromResult(cache.GetNode("dev-b")?.Memory is { Model: MemoryModel.Discrete, Gpus.Count: 2 }),
                "dev-b's /status memory never showed two GPUs");

            // A discrete node needs a GPU, and only its own: both refused by the master's placement validation.
            await AssertPlacementRefusedAsync(http, new { nodeId = "dev-b" }, PlacementProblemCodes.GpusRequired);
            await AssertPlacementRefusedAsync(http, new { nodeId = "dev-b", gpuUuids = UnknownGpu }, PlacementProblemCodes.GpuUnknown);

            // One fake model placed on each node (fake-devb on dev-b's GPU 1, by UUID), started through the admin API.
            foreach ((string id, object placement) in new[] { ("fake-dgx", (object)new { nodeId = "dgx" }), ("fake-devb", new { nodeId = "dev-b", gpuUuids = new[] { DevBGpus[1] } }) })
            {
                using var created = await http.PostAsJsonAsync("/api/models", ModelBody(id, placement), Ct);
                Assert.True(created.StatusCode == HttpStatusCode.Created, $"{(int)created.StatusCode} {await created.Content.ReadAsStringAsync(Ct)}");
                // 409 while another start holds the node's start slot (the master auto-starts its seeded model on the
                // bootstrap node), 503 while the node settles: retry, as the scripts do.
                string last = "";
                await WaitUntilAsync(async () =>
                {
                    using var start = await http.PostAsync($"/api/models/{id}/start", content: null, Ct);
                    last = $"{(int)start.StatusCode} {await start.Content.ReadAsStringAsync(Ct)}";
                    Assert.True(start.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable, $"start {id}: {last}");
                    return start.StatusCode == HttpStatusCode.OK;
                }, $"start {id} kept answering 409/503 for 60 s");
            }

            // The placement was saved by UUID, with the index of that card in dev-b's report.
            ModelPlacement stored = (await master.Services.GetRequiredService<IModelRegistry>().GetAsync("fake-devb", Ct))!.Placement!;
            Assert.Equal("dev-b", stored.NodeId);
            Assert.Equal([DevBGpus[1]], stored.GpuUuids!);
            Assert.Equal([1], stored.GpuIndices!);

            await WaitUntilAsync(async () =>
            {
                // A fake model that failed (e.g. its port was taken) never becomes Ready: say why now, not after 60 s.
                foreach (string id in FakeModels)
                {
                    if (cache.GetRuntime(id) is { State: ModelState.Failed } failed)
                    {
                        Assert.Fail($"{id} failed on {failed.NodeId} (port {failed.HostPort}): {failed.Error}");
                    }
                }

                using var models = await http.GetAsync("/v1/models", Ct);
                string body = await models.Content.ReadAsStringAsync(Ct);
                return models.StatusCode == HttpStatusCode.OK && body.Contains("\"fake-dgx\"", StringComparison.Ordinal) && body.Contains("\"fake-devb\"", StringComparison.Ordinal);
            }, "both fake models never appeared in /v1/models");

            // fake-devb's 4 GiB budget shows as VRAM used on dev-b's GPUs (before MN-106 the master launches with every GPU
            // visible, so the fake puts it on GPU 0 as vLLM would; with MN-106 it lands on the placed GPU 1).
            // The cache takes /status at each synchronization; refresh to read it now.
            await WaitUntilAsync(async () =>
            {
                await cache.RefreshNodeAsync("dev-b", Ct);
                return cache.GetNode("dev-b")?.Memory?.Gpus is { Count: 2 } gpus
                    && Math.Abs(gpus.Sum(g => g.UsedGiB ?? 0) - (2 * DevFakeRuntimeOptions.GpuBaselineUsedGiB + 4)) < 0.01;
            },
                "fake-devb's budget never showed as VRAM used on dev-b");
            if (cache.GetRuntime("fake-devb") is { GpuUuids.Count: > 0 } placedRuntime)
            {
                Assert.Equal([DevBGpus[1]], placedRuntime.GpuUuids);
            }

            // /v1 through the master's router and each node's agent proxy, buffered and streamed.
            foreach (string model in new[] { "fake-dgx", "fake-devb" })
            {
                using (var chat = await http.PostAsJsonAsync("/v1/chat/completions",
                    new { model, messages = new[] { new { role = "user", content = "hi" } }, max_tokens = 4 }, Ct))
                {
                    string body = await chat.Content.ReadAsStringAsync(Ct);
                    Assert.True(chat.StatusCode == HttpStatusCode.OK, $"{model}: {(int)chat.StatusCode} {body}");
                    using var json = JsonDocument.Parse(body);
                    Assert.Equal(model, json.RootElement.GetProperty("model").GetString());
                    Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()));
                }

                using (var stream = await http.PostAsJsonAsync("/v1/chat/completions",
                    new { model, messages = new[] { new { role = "user", content = "hi" } }, max_tokens = 4, stream = true }, Ct))
                {
                    string body = await stream.Content.ReadAsStringAsync(Ct);
                    Assert.True(stream.StatusCode == HttpStatusCode.OK, $"{model} stream: {(int)stream.StatusCode} {body}");
                    Assert.Equal("text/event-stream", stream.Content.Headers.ContentType?.MediaType);
                    Assert.Contains("data: [DONE]", body, StringComparison.Ordinal);
                }
            }

            // Stops go through the agents too.
            foreach (string id in new[] { "fake-dgx", "fake-devb" })
            {
                using var stop = await http.PostAsync($"/api/models/{id}/stop", content: null, Ct);
                Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
            }

            await StopAsync(devB);
            await StopAsync(dgx);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task TheFakeRuntimeRefusesToStartOutsideDevelopment()
    {
        string root = Path.Combine(Path.GetTempPath(), "ss-dev-guard-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var ex = await Assert.ThrowsAsync<AgentStartupException>(() => BuildAgentAsync(root, "dev-x", 0, Environments.Production, configure: null));
            Assert.Contains("development-only", ex.Message, StringComparison.Ordinal);

            // Development, but a listener on every address would expose a fake node on the network.
            ex = await Assert.ThrowsAsync<AgentStartupException>(() => BuildAgentAsync(root, "dev-x", 0, Environments.Development, o => o.ListenAddress = "0.0.0.0"));
            Assert.Contains("loopback", ex.Message, StringComparison.Ordinal);

            // A discrete fake node (MN-122) is development-only too, and malformed settings stop the agent naming the problem.
            ex = await Assert.ThrowsAsync<AgentStartupException>(() => BuildAgentAsync(root, "dev-x", 0, Environments.Production, configure: null, memory: "discrete:2x16"));
            Assert.Contains("development-only", ex.Message, StringComparison.Ordinal);
            ex = await Assert.ThrowsAsync<AgentStartupException>(() => BuildAgentAsync(root, "dev-x", 0, Environments.Development, configure: null, memory: "discrete:2x0"));
            Assert.Contains("discrete:<n>x<GiB>", ex.Message, StringComparison.Ordinal);
            ex = await Assert.ThrowsAsync<AgentStartupException>(() => BuildAgentAsync(
                root, "dev-x", 0, Environments.Development, configure: null, memory: "discrete:2x16", gpuUuids: ["GPU-00000000-0000-0000-0000-00000000de00"]));
            Assert.Contains("1 UUID(s) for 2 GPU(s)", ex.Message, StringComparison.Ordinal);

            // Nothing was enrolled or written: the guard runs before enrollment.
            Assert.False(Directory.Exists(Path.Combine(root, "dev-x", "state")) && Directory.EnumerateFiles(Path.Combine(root, "dev-x", "state")).Any());
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void TheDevJoinTokenHelperIsRefusedOutsideDevelopment()
    {
        DevJoinTokenOptions Options(string? output, params string[] nodes) => new() { Nodes = [.. nodes], OutputDirectory = output };

        var ex = Assert.Throws<InvalidOperationException>(() => DevJoinTokens.Validate(Options("join", "dev-b"), new Env(Environments.Production), "dgx"));
        Assert.Contains("development-only", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => DevJoinTokens.Validate(Options("join", "dev-b"), new Env("Staging"), "dgx"));

        // The bootstrap node enrolls with its bundle, never with a dev token; ids and the output directory are checked.
        Assert.Throws<InvalidOperationException>(() => DevJoinTokens.Validate(Options("join", "dgx"), new Env(Environments.Development), "dgx"));
        Assert.Throws<InvalidOperationException>(() => DevJoinTokens.Validate(Options("join", "Dev_B"), new Env(Environments.Development), "dgx"));
        Assert.Throws<InvalidOperationException>(() => DevJoinTokens.Validate(Options(null, "dev-b"), new Env(Environments.Development), "dgx"));

        // Not configured: off, in any environment; configured in Development: accepted.
        DevJoinTokens.Validate(Options(null), new Env(Environments.Production), "dgx");
        Assert.False(DevJoinTokens.IsEnabled(Options("join")));
        DevJoinTokens.Validate(Options("join", "dev-b"), new Env(Environments.Development), "dgx");
    }

    [Fact]
    public async Task TheMasterDoesNotStartWithTheDevJoinTokenHelperInProduction()
    {
        string root = Path.Combine(Path.GetTempPath(), "ss-dev-master-guard-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            await using var master = new DevMasterFactory(new Dictionary<string, string?>
            {
                ["MirbadLLM:DataPath"] = Path.Combine(root, "master-data"),
                ["MirbadLLM:Backup:Directory"] = Path.Combine(root, "backups"),
                ["MirbadLLM:Enrollment:Port"] = "0",
                ["MirbadLLM:Enrollment:BootstrapEnabled"] = "false",
                ["MirbadLLM:DevJoinTokens:Nodes:0"] = "dev-b",
                ["MirbadLLM:DevJoinTokens:OutputDirectory"] = Path.Combine(root, "join"),
            }, Environments.Production);
            var ex = Assert.ThrowsAny<Exception>(() => master.Services);
            Assert.Contains("development-only", Flatten(ex), StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(root, "join")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string Flatten(Exception ex) =>
        ex is AggregateException aggregate
            ? string.Join(" | ", aggregate.InnerExceptions.Select(Flatten))
            : ex.Message + (ex.InnerException is null ? "" : " | " + Flatten(ex.InnerException));

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("[::1]", true)]
    [InlineData("localhost", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("*", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("192.0.2.191", false)]
    public void OnlyALoopbackListenerCountsAsLoopback(string? address, bool loopback) =>
        Assert.Equal(loopback, DevFakeRuntime.IsLoopbackOnly(address));

    [Fact]
    public void TheDevFakeRuntimeSwitchMapsToItsSetting()
    {
        Assert.True(AgentProgram.TryParseArguments(["--config", "a.json", DevFakeRuntime.Switch], out string? path, out string[] rest, out _));
        Assert.Equal("a.json", path);
        Assert.Equal(["--DevFakeRuntime:Enabled=true"], rest);
    }

    private static object ModelBody(string id, object placement) => new
    {
        id,
        servedName = id,
        modelPath = "fake-model",
        memoryBudgetGiB = 4,
        maxModelLen = 8192,
        maxNumSeqs = 4,
        placement,
    };

    /// <summary>Creating a model with <paramref name="placement"/> answers 400 <c>placement_invalid</c> with <paramref name="problem"/>.</summary>
    private static async Task AssertPlacementRefusedAsync(HttpClient http, object placement, string problem)
    {
        using var created = await http.PostAsJsonAsync("/api/models", ModelBody("fake-refused", placement), Ct);
        string body = await created.Content.ReadAsStringAsync(Ct);
        Assert.True(created.StatusCode == HttpStatusCode.BadRequest, $"{(int)created.StatusCode} {body}");
        Assert.Contains(ErrorCodes.PlacementInvalid, body, StringComparison.Ordinal);
        Assert.Contains($"\"{problem}\"", body, StringComparison.Ordinal);
    }

    private static async Task<WebApplication> StartAgentAsync(
        string root, string nodeId, int port, int modelPorts, Action<AgentOptions>? configure, string memory = "unified:64", string[]? gpuUuids = null)
    {
        WebApplication app = await BuildAgentAsync(root, nodeId, port, Environments.Development, configure, memory, modelPorts, gpuUuids);
        try
        {
            await app.StartAsync(Ct);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    /// <summary>The agent as the dev scripts configure it (see <c>tools/dev-multinode</c>), on loopback with the fake runtime.</summary>
    private static Task<WebApplication> BuildAgentAsync(
        string root, string nodeId, int port, string environment, Action<AgentOptions>? configure, string memory = "unified:64", int modelPorts = 18100,
        string[]? gpuUuids = null)
    {
        string node = Path.Combine(root, nodeId);
        string models = Path.Combine(node, "models");
        Directory.CreateDirectory(Path.Combine(models, "fake-model"));
        File.WriteAllText(Path.Combine(models, "fake-model", "config.json"), """{"architectures":["LlamaForCausalLM"],"model_type":"llama"}""");
        var options = new AgentOptions
        {
            NodeId = nodeId,
            StateDirectory = Path.Combine(node, "state"),
            ListenAddress = "127.0.0.1",
            Port = port,
            ModelsRoot = models,
            ModelsHostPath = models,
            CacheRoot = Path.Combine(node, "cache"),
            CacheHostPath = Path.Combine(node, "cache"),
            UseContainerNetwork = false,
            DockerStartupTimeout = TimeSpan.FromSeconds(5),
            HostdSocket = Path.Combine(node, "no-hostd.sock"),
            HostdExpected = false,
            ManageSystemdUnits = false,
            ManageContainers = false,
            ManageBackupMount = false,
            AllowTagPulls = true,
            PortRangeStart = modelPorts,
            PortRangeEnd = modelPorts + ModelPortsPerAgent - 1,
            ShutdownTimeout = TimeSpan.FromSeconds(10),
        };
        configure?.Invoke(options);
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = node, EnvironmentName = environment });
        builder.Logging.ClearProviders();
        var fake = new Dictionary<string, string?>
        {
            ["DevFakeRuntime:Enabled"] = "true",
            ["DevFakeRuntime:Memory"] = memory,
            ["DevFakeRuntime:HostMemoryGiB"] = "32",
            ["DevFakeRuntime:StartDelay"] = "00:00:00.300",
            ["DevFakeRuntime:TokenDelay"] = "00:00:00.002",
        };
        for (int i = 0; i < (gpuUuids?.Length ?? 0); i++)
        {
            fake[$"DevFakeRuntime:GpuUuids:{i}"] = gpuUuids![i];
        }

        builder.Configuration.AddInMemoryCollection(fake);
        builder.Services.AddSingleton<IDiskSpaceProbe>(new ContractNode.FixedDiskProbe());
        return AgentProgram.CreateAsync(builder, options, startupLoggers: null, configureServices: null, Ct);
    }

    private static async Task StopAsync(WebApplication app)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await app.StopAsync(timeout.Token);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string message)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, message);
            await Task.Delay(100, Ct);
        }
    }

    private static void TryDelete(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>The master's <c>Program</c> on Kestrel in the Development environment (as the dev scripts run it).</summary>
    private sealed class DevMasterFactory(Dictionary<string, string?> settings, string environment = "Development") : WebApplicationFactory<Program>
    {
        public Uri HttpAddress()
        {
            var addresses = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
            string address = addresses.First(a => a.StartsWith("http://", StringComparison.Ordinal));
            return new Uri(address.Replace("://[::]", "://127.0.0.1", StringComparison.Ordinal).Replace("://0.0.0.0", "://127.0.0.1", StringComparison.Ordinal));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("urls", "http://127.0.0.1:0");
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var all = new Dictionary<string, string?>(settings)
                {
                    ["urls"] = "http://127.0.0.1:0",
                    ["MirbadLLM:AdminPassword"] = null,
                    ["Logging:LogLevel:Default"] = "Warning", // appsettings.Development.json says Information
                };
                config.AddInMemoryCollection(all);
            });
        }
    }
}
