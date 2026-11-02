using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Runtimes.Process;
using MirbadLLM.UnitTests.Agent.Fakes;
using FakeServer = MirbadLLM.FakeLlamaServer.FakeLlamaServer;
using RecordingStageSink = MirbadLLM.UnitTests.Agent.ContainerRuntimeDriverTests.RecordingStageSink;

#pragma warning disable CA1707, xUnit1051 // underscore test names; hand-written fakes ignore the token.

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// <see cref="ProcessRuntimeDriver"/> (MN-211; design-p3p4 §A.1.3–§A.1.4, plan-p3p4 §4.1 driver list) against the in-memory
/// <see cref="FakeProcessHost"/>, whose "processes" are the real fake llama-server running in-process on loopback ports:
/// launch and Ready only after <c>/v1/models</c> lists the alias with the per-instance key, stop with the fencing re-check,
/// crash before Ready (Failed, no restart) and after Ready (2 restarts, then Failed with the log tail), agent restart →
/// adoption by PID + start time into a new job, PID reuse, the orphan sweep (a matching pending start killed; an unknown
/// command line, an unreadable one, another owner or another folder never touched), port taken on 0.0.0.0 → next port,
/// reboot, a torn <c>processes.json</c>, and the <c>CheckSpec</c> guardrails. Nothing here starts or touches an OS process.
/// </summary>
public sealed class ProcessRuntimeDriverTests
{
    private const string NodeId = "win-1";
    private const string Version = "b11476";
    private const string Gpu = "GPU-7c1e9a52-3f60-4d2b-9a8e-5b0f2c6d1e44";
    private const string Alias = "qwen3-embedding-4b-win";
    private const string ModelFile = "Qwen3-Embedding-4B-GGUF/Qwen3-Embedding-4B-Q8_0.gguf";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    // ------------------------------------------------------------ harness

    private sealed class Harness : IAsyncDisposable
    {
        private readonly HttpClient _http;
        private readonly Dictionary<string, int> _modelPorts = new(StringComparer.Ordinal);

        public Harness(int? port = null, HttpMessageHandler? upstream = null, Action<ProcessRuntimeOptions>? configure = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "mirbadllm-mn211", Guid.NewGuid().ToString("N"));
            ModelsRoot = Path.Combine(Root, "models");
            Directory.CreateDirectory(Path.Combine(ModelsRoot, Path.GetDirectoryName(ModelFile)!));
            File.WriteAllBytes(Path.Combine(ModelsRoot, ModelFile), [0x47, 0x47, 0x55, 0x46]);
            Options = new ProcessRuntimeOptions
            {
                NodeId = NodeId,
                ModelsRoot = ModelsRoot,
                CacheRoot = Path.Combine(Root, "cache"),
                StateDirectory = Path.Combine(Root, "state"),
                HealthPollInterval = TimeSpan.FromMilliseconds(40),
                StartTimeout = TimeSpan.FromSeconds(15),
                ProbeTimeout = TimeSpan.FromSeconds(2),
                SystemRoot = OperatingSystem.IsWindows() ? null : "/fake/SystemRoot",
            };
            configure?.Invoke(Options);
            Catalog = new FakeExecutableCatalog(Path.Combine(Root, "runtimes"));
            Executable = Catalog.Add(Version);
            Ports.Next = port ?? TestPorts.Reserve();
            _http = new HttpClient(upstream ?? new SocketsHttpHandler());
            Host = new FakeProcessHost(Table);
            Store = new ProcessRecordStore(Options.RecordsPath);
            Driver = NewDriver();
        }

        public string Root { get; }

        public string ModelsRoot { get; }

        public ProcessRuntimeOptions Options { get; }

        public FakeProcessTable Table { get; } = new();

        public FakeProcessHost Host { get; private set; }

        public ProcessRecordStore Store { get; private set; }

        public FakeExecutableCatalog Catalog { get; }

        public ResolvedExecutable Executable { get; }

        public FakeInstanceKeyProtector Keys { get; } = new();

        public ContainerRuntimeDriverTests.FakePortAllocator Ports { get; } = new();

        public RecordingStageSink Stages { get; } = new();

        public FakeGpuInventory? Gpus { get; set; }

        public FakeGpuStartMeter? Meter { get; set; }

        public ProcessRuntimeDriver Driver { get; private set; }

        public int Port => Ports.Next;

        /// <summary>
        /// The host port a launch of <paramref name="modelId"/> asks for: <see cref="Port"/> for <c>m1</c>, and a port of
        /// its own (reserved through <see cref="TestPorts"/>) for every other model. Two models given the same port made
        /// the driver walk to the next one up, which <see cref="TestPorts"/> had handed to another test (MN-230: "address
        /// already in use" in <c>RekeyTests</c> and in this class's adoption test on CI).
        /// </summary>
        public int PortFor(string modelId)
        {
            if (modelId == "m1")
            {
                return Port;
            }

            lock (_modelPorts)
            {
                if (!_modelPorts.TryGetValue(modelId, out int port))
                {
                    port = TestPorts.Reserve();
                    _modelPorts[modelId] = port;
                }

                return port;
            }
        }

        /// <summary>The proxy's upstream-cache signal (MN-239 bug B); every driver this harness builds shares it.</summary>
        public UpstreamChanges Upstreams { get; } = new();

        public ProcessRuntimeDriver NewDriver(IProcessRecordStore? store = null) =>
            new(Host, store ?? Store, Catalog, Keys, Ports, _http, Microsoft.Extensions.Options.Options.Create(Options),
                NullLogger<ProcessRuntimeDriver>.Instance, Meter, Gpus, timeProvider: null, Upstreams);

        /// <summary>The agent dies (its job handles close, the children keep running) and starts again on the same state.</summary>
        public async Task RestartAgentAsync(Func<Task>? whileDown = null)
        {
            await Driver.DisposeAsync();
            Host.SimulateAgentDeath();
            if (whileDown is not null)
            {
                await whileDown();
            }

            Host = new FakeProcessHost(Table);
            Store = new ProcessRecordStore(Options.RecordsPath);
            Driver = NewDriver();
        }

        public Task<RuntimeLaunchResult> LaunchAsync(LaunchSpec? spec = null, LaunchMode mode = LaunchMode.EnsureStarted, string modelId = "m1", string operationId = "op-1") =>
            Driver.LaunchAsync(
                new RuntimeLaunch { ModelId = modelId, OperationId = operationId, Mode = mode, Spec = spec ?? Spec(), HostPort = PortFor(modelId) },
                Stages);

        public async Task<RuntimeStatus> WaitForAsync(string modelId, Func<RuntimeStatus?, bool> condition, string what)
        {
            var watch = Stopwatch.StartNew();
            RuntimeStatus? status = null;
            while (watch.Elapsed < Wait)
            {
                status = await Driver.GetAsync(modelId);
                if (condition(status))
                {
                    return status!;
                }

                await Task.Delay(25);
            }

            throw new TimeoutException($"{what}: last status {(status is null ? "none" : $"{status.State} ({status.Error})")}.");
        }

        public Task<RuntimeStatus> WaitForStateAsync(string modelId, ModelState state) =>
            WaitForAsync(modelId, s => s?.State == state, $"waiting for {state}");

        public string ExecutableFolder => Executable.Folder;

        public async ValueTask DisposeAsync()
        {
            await Driver.DisposeAsync();
            await Table.RebootAsync();
            _http.Dispose();
            Store.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The spec MN-210's builder emits for the OpenViking embedding model (P3 fixture shape), plus test knobs.</summary>
    private static LaunchSpec Spec(
        IReadOnlyDictionary<string, string>? env = null, IReadOnlyList<string>? argv = null, IReadOnlyList<string>? secretEnvNames = null,
        GpuSelection? gpus = null, string? executable = ExecutableNames.LlamaServer, string? version = Version) => new()
    {
        Kind = LaunchDelivery.Process,
        Runtime = RuntimeKind.LlamaCpp,
        Executable = executable,
        ExecutableVersion = version,
        Argv = argv ?? Argv(),
        Env = env ?? Knobs(load: 100),
        SecretEnvNames = secretEnvNames ?? [],
        Gpus = gpus ?? new GpuSelection { Mode = GpuSelectionMode.Uuids, Uuids = [Gpu] },
        Labels = new Dictionary<string, string>
        {
            [ContainerLabels.ServedName] = Alias,
            [ContainerLabels.ExecutionFingerprint] = "5e0c7a91d2b34f68",
            [ContainerLabels.BudgetGiB] = "8.9",
        },
    };

    private static List<string> Argv(string alias = Alias, IEnumerable<string>? extra = null) =>
    [
        "-m", "{models}/" + ModelFile, "--alias", alias, "-c", "16384", "-np", "2", "-ngl", "999", "-fa", "auto",
        "-b", "2048", "-ub", "2048", "--embedding", "--pooling", "last", "--split-mode", "none", "--device", "CUDA0",
        .. LlamaCppFlags.AlwaysArgs, .. extra ?? [],
    ];

    private static Dictionary<string, string> Knobs(int load = 100, int? exitAfter = null, int? exitAfterReady = null, int? exitCode = null)
    {
        var env = new Dictionary<string, string> { ["FAKE_LLAMA_LOAD_MS"] = load.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (exitAfter is { } a)
        {
            env["FAKE_LLAMA_EXIT_AFTER_MS"] = a.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (exitAfterReady is { } r)
        {
            env["FAKE_LLAMA_EXIT_AFTER_READY_MS"] = r.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (exitCode is { } c)
        {
            env["FAKE_LLAMA_EXIT_CODE"] = c.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return env;
    }

    private static string ExpectedDigest(IReadOnlyList<string> arguments) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', arguments))))[..16];

    private static void AssertRejected(Action action, string code = ErrorCodes.SpecRejected)
    {
        var ex = Assert.Throws<AgentException>(action);
        Assert.Equal(code, ex.Code);
        Assert.Equal(422, ex.StatusCode);
    }

    // ------------------------------------------------------------ launch, Ready, launch-config

    [Fact]
    public async Task Launch_StartsSuspendedSpecWithAgentEnvironment_RecordsIt_AndIsReadyOnlyAfterModelsListsTheAlias()
    {
        await using var h = new Harness();
        h.Gpus = new FakeGpuInventory([new GpuCapability { Index = 1, Uuid = Gpu, Name = "RTX 5060 Ti", MemoryTotalGiB = 15.93 }]);
        h.Meter = new FakeGpuStartMeter(7.6);
        await h.Driver.DisposeAsync();
        var driver = h.NewDriver();
        await using var _ = driver;

        var result = await driver.LaunchAsync(
            new RuntimeLaunch { ModelId = "m1", OperationId = "op-1", Mode = LaunchMode.EnsureStarted, Spec = Spec(Knobs(load: 600)), HostPort = h.Port },
            h.Stages);

        Assert.Equal(RuntimeLaunchOutcome.Created, result.Outcome);
        Assert.Equal(ModelState.Starting, result.Instance.State); // 503 "Loading model" for 600 ms
        Assert.Equal(["before:created", "before:started", "reached:created", "reached:started"], h.Stages.Events);

        var start = Assert.Single(h.Host.Starts);
        Assert.Equal(h.Executable.Path, start.ExecutablePath);
        Assert.Equal(h.Executable.Folder, start.WorkingDirectory);
        Assert.Equal("mirbadllm-ss-m1", start.JobName);
        Assert.Equal(Path.Combine(h.Options.StateDirectory, "logs", "ss-m1.log"), start.LogPath);
        string port = h.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(Path.Combine(h.ModelsRoot, ModelFile.Replace('/', Path.DirectorySeparatorChar)), start.Arguments[1]);
        Assert.Equal(port, start.Arguments[start.Arguments.ToList().IndexOf("--port") + 1]);
        Assert.DoesNotContain(start.Arguments, a => a.Contains('{', StringComparison.Ordinal));

        // The complete environment block: the spec's variables plus the agent's names, nothing inherited.
        string key = start.Environment[ProcessEnvironment.LlamaApiKey];
        Assert.Matches("^[0-9a-f]{64}$", key);
        Assert.Equal(Path.Combine(h.Options.CacheRoot, "tmp"), start.Environment[ProcessEnvironment.Temp]);
        Assert.Equal(Path.Combine(h.Options.CacheRoot, "tmp"), start.Environment[ProcessEnvironment.Tmp]);
        Assert.True(start.Environment.ContainsKey(ProcessEnvironment.SystemRoot));
        if (OperatingSystem.IsMacOS())
        {
            // Metal, no CUDA (docs/macos/implementation-plan.md, Phase 2): TMPDIR and the system tool folders instead.
            Assert.Equal(h.Executable.Folder + ":/usr/bin:/bin", start.Environment[ProcessEnvironment.Path]);
            Assert.Equal(Path.Combine(h.Options.CacheRoot, "tmp"), start.Environment[ProcessEnvironment.TmpDir]);
            Assert.Equal(
                new[] { ProcessEnvironment.LlamaApiKey, ProcessEnvironment.Path, ProcessEnvironment.SystemRoot, ProcessEnvironment.Temp,
                    ProcessEnvironment.Tmp, ProcessEnvironment.TmpDir, "FAKE_LLAMA_LOAD_MS" }
                    .Order(StringComparer.OrdinalIgnoreCase),
                start.Environment.Keys.Order(StringComparer.OrdinalIgnoreCase));
        }
        else
        {
            Assert.Equal(h.Executable.Folder, start.Environment[ProcessEnvironment.Path]);
            Assert.Equal(Gpu, start.Environment[ProcessEnvironment.CudaVisibleDevices]);
            Assert.Equal(Path.Combine(h.Options.CacheRoot, "cuda"), start.Environment[ProcessEnvironment.CudaCachePath]);
            Assert.Equal("4294967296", start.Environment[ProcessEnvironment.CudaCacheMaxSize]);
            Assert.Equal(
                ProcessEnvironment.Reserved.Append("FAKE_LLAMA_LOAD_MS").Order(StringComparer.Ordinal),
                start.Environment.Keys.Order(StringComparer.Ordinal));
        }

        // Durable record; the pending start was replaced by it; the key is stored protected only.
        var document = ProcessRecordsDocumentOnDisk(h);
        Assert.Empty(document.Pending);
        var record = Assert.Single(document.Processes);
        Assert.Equal(ExpectedDigest(start.Arguments), record.ArgvDigest);
        Assert.Equal(h.Port, record.Port);
        Assert.Equal(Version, record.ExecutableVersion);
        Assert.Equal(h.Keys.Protect(key), record.ProtectedApiKey);
        Assert.DoesNotContain(key, File.ReadAllText(h.Options.RecordsPath), StringComparison.Ordinal);
        Assert.Equal(Argv(), record.Spec.Argv); // tokens kept
        Assert.Null(record.ReadyAt);

        var ready = await WaitForStateAsync(driver, "m1", ModelState.Ready);
        Assert.Equal(record.Process.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture), ready.InstanceId);
        Assert.Equal(Alias, ready.ServedName);
        Assert.Equal("5e0c7a91d2b34f68", ready.ExecutionFingerprint);
        Assert.Equal(RuntimeKind.LlamaCpp, ready.Runtime);
        Assert.Equal(LaunchDelivery.Process, ready.Delivery);
        Assert.Equal([new GpuRef(1, Gpu)], ready.Gpus);
        Assert.Equal(NodeId, ready.Labels[ContainerLabels.Node]);
        Assert.Equal("op-1", ready.OperationId);
        Assert.Equal(7.6, ready.MeasuredGpuGiB);
        Assert.Equal([Gpu], h.Meter.Marked);
        Assert.NotNull(ProcessRecordsDocumentOnDisk(h).Processes[0].ReadyAt);

        var config = await driver.GetLaunchConfigAsync("m1");
        Assert.NotNull(config);
        Assert.Equal(LaunchDelivery.Process, config.Kind);
        Assert.Equal(ExecutableNames.LlamaServer, config.Executable);
        Assert.Equal(Version, config.ExecutableVersion);
        Assert.Equal(Argv(), config.Argv);
        Assert.Equal("op-1", config.Labels[ContainerLabels.Operation]);
        Assert.Equal(new Uri($"http://127.0.0.1:{port}/"), await driver.GetUpstreamAsync("m1"));
        Assert.Equal(key, await driver.GetUpstreamApiKeyAsync("m1"));
    }

    [Fact]
    public async Task Ready_IsNotReachedWhenModelsDoesNotListTheAlias_OrRefusesTheKey_AndTheStartTimesOut()
    {
        // A server answering /health 200 but listing another name (or not knowing the key: a foreign process on the port)
        // never makes the instance Ready; at the start timeout it is stopped and Failed.
        foreach (var tamper in new[] { "alias", "key" })
        {
            await using var h = new Harness(configure: o => o.StartTimeout = TimeSpan.FromMilliseconds(1200));
            if (tamper == "alias")
            {
                h.Host.TamperArguments = args => [.. args.Select(a => a == Alias ? "someone-else" : a)];
            }
            else
            {
                h.Host.TamperEnvironment = env => new Dictionary<string, string>(env) { [ProcessEnvironment.LlamaApiKey] = "not-the-instance-key" };
            }

            await h.LaunchAsync();
            var failed = await h.WaitForStateAsync("m1", ModelState.Failed);
            Assert.StartsWith("Cause: ", failed.Error, StringComparison.Ordinal);
            Assert.Contains("did not become Ready", failed.Error, StringComparison.Ordinal);
            Assert.True(h.Table.Killed.Count == 1, tamper);
            Assert.Empty(h.Table.Running);
        }
    }

    [Fact]
    public async Task UpstreamEndpoints_NeedThePerInstanceKey_OnlyHealthIsOpen()
    {
        await using var h = new Harness();
        await h.LaunchAsync();
        await h.WaitForStateAsync("m1", ModelState.Ready);
        var upstream = await h.Driver.GetUpstreamAsync("m1");
        string? key = await h.Driver.GetUpstreamApiKeyAsync("m1");
        Assert.NotNull(upstream);
        Assert.NotNull(key);
        using var http = new HttpClient { BaseAddress = upstream };

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("v1/models")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("metrics")).StatusCode);

        using var models = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        models.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var modelsResponse = await http.SendAsync(models);
        Assert.Equal(HttpStatusCode.OK, modelsResponse.StatusCode);
        using var list = JsonDocument.Parse(await modelsResponse.Content.ReadAsStringAsync());
        Assert.Equal(Alias, list.RootElement.GetProperty("data")[0].GetProperty("id").GetString());

        using var metrics = new HttpRequestMessage(HttpMethod.Get, "metrics");
        metrics.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var metricsResponse = await http.SendAsync(metrics);
        Assert.Equal(HttpStatusCode.OK, metricsResponse.StatusCode);
        string text = await metricsResponse.Content.ReadAsStringAsync();
        Assert.All(LlamaCppMetrics.Families, family => Assert.Contains("\n" + family + " ", "\n" + text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnsureStarted_ReturnsARunningInstance_Replace_StopsAndStartsANewProcess()
    {
        await using var h = new Harness();
        await h.LaunchAsync();
        var first = await h.WaitForStateAsync("m1", ModelState.Ready);

        var again = await h.LaunchAsync(operationId: "op-2");
        Assert.Equal(RuntimeLaunchOutcome.AlreadyRunning, again.Outcome);
        Assert.Single(h.Host.Starts);

        h.Stages.Events.Clear();
        var replaced = await h.LaunchAsync(mode: LaunchMode.Replace, operationId: "op-3");
        Assert.Equal(RuntimeLaunchOutcome.Created, replaced.Outcome);
        Assert.Equal(
            ["before:stopping", "reached:stopping", "before:removed", "reached:removed", "before:created", "before:started", "reached:created", "reached:started"],
            h.Stages.Events);
        Assert.NotEqual(first.InstanceId, replaced.Instance.InstanceId);
        Assert.Equal("op-3", replaced.Instance.OperationId);
        Assert.Single(h.Table.Killed);
        Assert.Single(h.Table.Running);
        Assert.Equal(ModelState.Ready, (await h.WaitForStateAsync("m1", ModelState.Ready)).State);
    }

    // ------------------------------------------------------------ stop and fencing

    [Fact]
    public async Task Stop_RechecksFencingBeforeTheKill_KillsTheTree_AndRemovesTheRecord()
    {
        await using var h = new Harness();
        await h.LaunchAsync();
        await h.WaitForStateAsync("m1", ModelState.Ready);

        // A superseded master: the check before the destructive step refuses and nothing is ended.
        var refused = new RecordingStageSink { FailBefore = OperationStages.Stopping };
        var ex = await Assert.ThrowsAsync<AgentException>(() => h.Driver.StopAsync("m1", refused));
        Assert.Equal(ErrorCodes.Superseded, ex.Code);
        Assert.Single(h.Table.Running);
        Assert.Equal(ModelState.Ready, (await h.Driver.GetAsync("m1"))!.State);

        var sink = new RecordingStageSink();
        await h.Driver.StopAsync("m1", sink);
        Assert.Equal(["before:stopping", "reached:stopping", "before:removed", "reached:removed"], sink.Events);
        Assert.Empty(h.Table.Running);
        Assert.Single(h.Table.Killed);
        Assert.Null(await h.Driver.GetAsync("m1"));
        Assert.Null(await h.Driver.GetLaunchConfigAsync("m1"));
        Assert.Null(await h.Driver.GetUpstreamAsync("m1"));
        Assert.Empty(ProcessRecordsDocumentOnDisk(h).Processes);

        // Nothing to stop → no-op.
        var none = new RecordingStageSink();
        await h.Driver.StopAsync("m1", none);
        Assert.Empty(none.Events);
    }

    [Fact]
    public async Task EveryInstanceChange_SignalsTheProxysUpstreamCache_SoAReplacedPortIsNeverProxiedTo()
    {
        // MN-239 bug B. The agent proxy caches a positive upstream for AgentProxyOptions.UpstreamCacheDuration (1 s).
        // Nothing invalidated it when a driver replaced an instance, so a tuning relaunch that moved llama-server to a
        // new port inside the TTL was proxied to the dead port and answered 503 model_not_running, which recorded the
        // candidate Failed. The driver now bumps UpstreamChanges on every create, adoption and removal, and the
        // resolver discards an entry whose generation moved (DriverResolver_ForgetsACachedUpstream_… in AgentProxyTests
        // is the other half).
        await using var h = new Harness();
        Assert.Equal(0, h.Upstreams.Generation("m1"));

        await h.LaunchAsync();
        await h.WaitForStateAsync("m1", ModelState.Ready);
        long afterLaunch = h.Upstreams.Generation("m1");
        Assert.True(afterLaunch > 0, "a launch did not signal the proxy's upstream cache");

        await h.Driver.StopAsync("m1", new RecordingStageSink());
        long afterStop = h.Upstreams.Generation("m1");
        Assert.True(afterStop > afterLaunch, "a stop did not signal the proxy's upstream cache");

        // The relaunch a tune does: a different port, and the signal that makes the proxy go and find it.
        h.Ports.Next = TestPorts.Reserve();
        await h.LaunchAsync(operationId: "op-2");
        var ready = await h.WaitForStateAsync("m1", ModelState.Ready);
        Assert.True(h.Upstreams.Generation("m1") > afterStop, "a relaunch did not signal the proxy's upstream cache");
        Assert.Equal(h.Ports.Next, (await h.Driver.GetUpstreamAsync("m1"))!.Port);
        Assert.Equal(ModelState.Ready, ready.State);

        // An adoption after the agent died is a change too: the same model, a driver that has never seen it.
        long beforeRestart = h.Upstreams.Generation("m1");
        await h.RestartAgentAsync();
        await h.Driver.RecoverAsync();
        Assert.True(h.Upstreams.Generation("m1") > beforeRestart, "an adoption did not signal the proxy's upstream cache");
    }

    // ------------------------------------------------------------ crashes and the restart budget

    [Fact]
    public async Task CrashBeforeReady_IsFailedAtOnce_WithTheExitCodeAndTheLogTail_AndNoRestart()
    {
        await using var h = new Harness();
        await h.LaunchAsync(Spec(Knobs(load: 5000, exitAfter: 150, exitCode: 3)));

        var failed = await h.WaitForStateAsync("m1", ModelState.Failed);
        Assert.Single(h.Host.Starts);
        Assert.NotNull(failed.Error);
        Assert.StartsWith("Cause: RuntimeError: simulated failure", failed.Error, StringComparison.Ordinal);
        Assert.Contains("exited with code 3 before it was Ready", failed.Error, StringComparison.Ordinal);
        Assert.Contains("simulated crash before the model was loaded", failed.Error, StringComparison.Ordinal);
        Assert.Equal(0, failed.RestartCount);
        Assert.Null(await h.Driver.GetUpstreamAsync("m1"));

        // Failed is not "running": ensure-started removes the record and starts again.
        h.Stages.Events.Clear();
        var again = await h.LaunchAsync(Spec(), operationId: "op-2");
        Assert.Equal(RuntimeLaunchOutcome.Created, again.Outcome);
        Assert.Equal("before:removed", h.Stages.Events[0]);
        await h.WaitForStateAsync("m1", ModelState.Ready);
    }

    [Fact]
    public async Task CrashAfterReady_IsRestartedTwiceOnTheSamePort_ThenFailedWithTheLogTail()
    {
        await using var h = new Harness();
        await h.LaunchAsync(Spec(Knobs(load: 80, exitAfterReady: 250, exitCode: 9)));

        var failed = await h.WaitForAsync("m1", s => s?.State == ModelState.Failed, "waiting for the third exit");
        Assert.Equal(3, h.Host.Starts.Count);
        Assert.All(h.Host.Starts, s => Assert.Equal(h.Host.Starts[0].Arguments, s.Arguments)); // same port, same argv
        Assert.All(h.Host.Starts, s => Assert.Equal(h.Host.Starts[0].Environment[ProcessEnvironment.LlamaApiKey], s.Environment[ProcessEnvironment.LlamaApiKey]));
        Assert.Equal(2, failed.RestartCount);
        Assert.StartsWith("Cause: RuntimeError: simulated failure", failed.Error, StringComparison.Ordinal);
        Assert.Contains("exited with code 9 after it was Ready; the restart budget (2 in 10 min) is used up", failed.Error, StringComparison.Ordinal);
        Assert.Contains("simulated crash after the model was loaded", failed.Error, StringComparison.Ordinal);

        var record = Assert.Single(ProcessRecordsDocumentOnDisk(h).Processes);
        Assert.Equal(2, record.RestartCount);
        Assert.Equal(2, record.RestartTimes.Count);
        Assert.Equal("op-1", record.OperationId); // restarts are not new operations
        Assert.Empty(ProcessRecordsDocumentOnDisk(h).Pending);
    }

    [Fact]
    public void RestartBudget_AllowsTwoRestartsInTheWindow_NeverBeforeReady()
    {
        var budget = new RestartBudget(2, TimeSpan.FromMinutes(10));
        var t0 = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        Assert.Null(budget.TryRestart(wasReady: false, [], t0));
        var one = budget.TryRestart(true, [], t0);
        Assert.Equal([t0], one);
        var two = budget.TryRestart(true, one!, t0.AddMinutes(1));
        Assert.Equal([t0, t0.AddMinutes(1)], two);
        Assert.Null(budget.TryRestart(true, two!, t0.AddMinutes(2)));

        // Restarts older than the window no longer count.
        Assert.Equal([t0.AddMinutes(1), t0.AddMinutes(10).AddSeconds(1)], budget.TryRestart(true, two!, t0.AddMinutes(10).AddSeconds(1)));
        Assert.Null(new RestartBudget(0, TimeSpan.FromMinutes(10)).TryRestart(true, [], t0));
    }

    // ------------------------------------------------------------ agent restart, adoption, PID reuse, reboot

    [Fact]
    public async Task AgentRestart_AdoptsTheProcessByPidAndStartTimeIntoANewJob_ProbingInTheBackground()
    {
        var gate = new GatedHandler();
        await using var h = new Harness(upstream: gate);
        gate.Open();
        await h.LaunchAsync();
        var ready = await h.WaitForStateAsync("m1", ModelState.Ready);
        string? key = await h.Driver.GetUpstreamApiKeyAsync("m1");
        var process = Assert.Single(h.Table.Running);

        gate.Close(); // the new agent's probe hangs: recovery must not wait for it
        await h.RestartAgentAsync(whileDown: () =>
        {
            Assert.False(process.HasExited); // the job handle closed; the child lives on (no kill-on-close)
            return Task.CompletedTask;
        });

        var report = await h.Driver.RecoverAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["m1"], report.Adopted);
        Assert.Empty(report.OrphansKilled);
        var adopted = await h.Driver.GetAsync("m1");
        Assert.Equal(ModelState.Starting, adopted!.State);
        Assert.Equal(ready.InstanceId, adopted.InstanceId);
        Assert.Equal(["mirbadllm-ss-m1", "mirbadllm-ss-m1"], process.Jobs); // the start's job, then a new one
        Assert.Equal(key, await h.Driver.GetUpstreamApiKeyAsync("m1"));

        gate.Open();
        await h.WaitForStateAsync("m1", ModelState.Ready);
        Assert.Empty(h.Table.Killed);
        Assert.Empty(h.Host.KillRequests);

        // The adopted instance is supervised as before: a stop kills it through the new job.
        await h.Driver.StopAsync("m1", new RecordingStageSink());
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task AgentRestart_AdoptedProcessThatCrashes_IsRestartedWithItsStoredKey()
    {
        await using var h = new Harness();
        await h.LaunchAsync(Spec(Knobs(load: 80, exitAfterReady: 900)));
        await h.WaitForStateAsync("m1", ModelState.Ready);
        await h.RestartAgentAsync();
        string? key = await h.Driver.GetUpstreamApiKeyAsync("m1");

        await h.WaitForAsync("m1", s => s?.RestartCount == 1, "waiting for the restart after adoption");
        var restart = Assert.Single(h.Host.Starts); // the new agent's host made it
        Assert.Equal(key, restart.Environment[ProcessEnvironment.LlamaApiKey]);
    }

    [Fact]
    public async Task AgentRestart_RecordWhosePidWasReused_IsNotAdopted_AndTheOtherProcessIsLeftAlone()
    {
        await using var h = new Harness();
        await h.LaunchAsync();
        await h.WaitForStateAsync("m1", ModelState.Ready);
        var original = Assert.Single(h.Table.Running);
        FakeOsProcess? reused = null;

        await h.RestartAgentAsync(async () =>
        {
            // llama-server died while the agent was down and its PID went to another program in the same folder.
            await original.ExitAsync(null);
            reused = h.Table.Spawn(original.ImagePath, ["--version"], pid: original.Pid, startTime: original.StartTime.AddSeconds(30));
        });

        var report = await h.Driver.RecoverAsync();
        Assert.Equal(["m1"], report.Gone);
        Assert.Empty(report.Adopted);
        Assert.Null(await h.Driver.GetAsync("m1"));
        Assert.Empty(ProcessRecordsDocumentOnDisk(h).Processes);
        Assert.False(reused!.HasExited);
        Assert.Contains(reused.Pid, report.Foreign);
        Assert.Empty(h.Host.KillRequests);
    }

    [Fact]
    public async Task AgentRestart_AnAdoptionThatThrows_DoesNotAbortRecovery_TheProcessIsSweptAndTheOthersAreAdopted()
    {
        // MN-219: WindowsProcessHost.OpenAsync throws Win32Exception when the new job cannot be made or assigned. Before
        // the fix that escaped RecoverAsync (so the agent's pre-RunAsync reconciliation failed and the agent did not
        // start); now the record is dropped and the process, provably ours, is ended by the sweep so the master can
        // start it again under supervision.
        await using var h = new Harness();
        await h.LaunchAsync();
        await h.LaunchAsync(Spec(argv: Argv("second")), modelId: "m2", operationId: "op-2");
        await h.WaitForStateAsync("m1", ModelState.Ready);
        await h.WaitForStateAsync("m2", ModelState.Ready);
        var second = h.Table.Running.Single(p => p.Arguments!.Contains("second"));
        var first = h.Table.Running.Single(p => p != second);

        await h.RestartAgentAsync();
        h.Host.FailNextOpen = new System.ComponentModel.Win32Exception(5, "AssignProcessToJobObject");

        var report = await h.Driver.RecoverAsync();
        string failed = report.Gone.Single();
        var unadopted = failed == "m1" ? first : second;
        var kept = failed == "m1" ? second : first;
        Assert.Equal([failed == "m1" ? "m2" : "m1"], report.Adopted);
        Assert.Equal([unadopted.Pid], report.OrphansKilled);
        Assert.True(unadopted.HasExited);
        Assert.False(kept.HasExited);
        Assert.Null(await h.Driver.GetAsync(failed));
        Assert.DoesNotContain(ProcessRecordsDocumentOnDisk(h).Processes, r => r.ModelId == failed);
    }

    [Fact]
    public async Task OrphanSweep_AKillThatThrows_DoesNotAbortRecovery_AndTheNextOrphanIsStillKilled()
    {
        // MN-219: one failing kill (or command-line read) used to escape RecoverAsync after adoption had run; the next
        // ListAsync then ran recovery again and adopted every record a second time (one more job, a leaked supervisor).
        await using var h = new Harness();
        await h.LaunchAsync();
        await h.WaitForStateAsync("m1", ModelState.Ready);
        var adopted = Assert.Single(h.Table.Running);
        FakeOsProcess? first = null, second = null;
        await h.RestartAgentAsync(() =>
        {
            first = h.Table.Spawn(adopted.ImagePath, adopted.Arguments, startTime: DateTimeOffset.UtcNow);
            second = h.Table.Spawn(adopted.ImagePath, adopted.Arguments, startTime: DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        });
        h.Host.FailNextKill = new TimeoutException("did not exit after TerminateProcess");

        var report = await h.Driver.RecoverAsync();

        Assert.Equal(["m1"], report.Adopted);
        Assert.Equal([second!.Pid], report.OrphansKilled);
        Assert.False(first!.HasExited);
        Assert.Equal(1, h.Host.OpenHandles);
        await h.Driver.ListAsync();
        await h.Driver.ListAsync();
        Assert.Equal(1, h.Host.OpenHandles); // recovery ran once: the record was adopted once
        Assert.Equal(["mirbadllm-ss-m1", "mirbadllm-ss-m1"], adopted.Jobs);
    }

    [Fact]
    public async Task OrphanSweep_NeverKillsTheProcessTheDriverRestartedWhileItRan()
    {
        // MN-219: supervision of an adopted instance starts before the sweep. When it crashes and is restarted in that
        // window, the new process has the record's digest and a later start time: the sweep must see it as the
        // instance's own process, not an orphan.
        await using var h = new Harness();
        await h.LaunchAsync();
        await h.WaitForStateAsync("m1", ModelState.Ready);
        await h.WaitForAsync("m1", _ => ProcessRecordsDocumentOnDisk(h).Processes[0].ReadyAt is not null, "the Ready record");
        var original = Assert.Single(h.Table.Running);
        await h.RestartAgentAsync();
        h.Host.BeforeListCandidates = async () =>
        {
            h.Host.BeforeListCandidates = null;
            await original.ExitAsync(1);
            var watch = Stopwatch.StartNew();
            while ((h.Host.Starts.Count == 0 || h.Table.Running.Count == 0) && watch.Elapsed < Wait)
            {
                await Task.Delay(10);
            }

            // MN-239 bug F. This used to be `await Task.Delay(50)`, which is not the condition the sweep depends on and
            // failed once on the Linux leg under load. The sweep matches a candidate against the *records*, so what has
            // to have happened is the restart's commit: processes.json naming the new PID with RestartCount 1. A start
            // having been requested (the loop above) is strictly earlier than that.
            while (watch.Elapsed < Wait)
            {
                var records = ProcessRecordsDocumentOnDisk(h).Processes;
                if (records.Count == 1 && records[0].RestartCount == 1 && records[0].Process.Pid != original.Identity.Pid)
                {
                    return;
                }

                await Task.Delay(10);
            }

            Assert.Fail($"the restart did not commit its new process within {Wait}.");
        };

        var report = await h.Driver.RecoverAsync();

        Assert.Equal(1, ProcessRecordsDocumentOnDisk(h).Processes[0].RestartCount);
        Assert.Single(h.Host.Starts);
        var restarted = Assert.Single(h.Table.Running);
        Assert.Empty(report.OrphansKilled);
        Assert.False(restarted.HasExited);
        Assert.DoesNotContain(restarted.Identity, h.Host.KillRequests);
    }

    [Fact]
    public async Task OrphanSweep_InTheWindowBetweenARestartsCommitAndItsSwap_StillDoesNotKillThatProcess()
    {
        // MN-239 bug F, the deterministic half. The MN-219 test above drives the same situation but cannot pin the
        // interleaving, which is why it failed once on the Linux leg under load and passed alone: RestartAsync commits
        // the new record *before* it swaps the instance's in-memory process, so for an instant processes.json names the
        // restarted PID while _instances still names the one that exited — and the sweep's live check
        // (_instances.Values.Any(…)) misses it and ends a process the driver has just started. Here the restart is
        // parked inside its commit, so the sweep decides exactly in that window, every run.
        await using var h = new Harness();
        await h.LaunchAsync();
        await h.WaitForStateAsync("m1", ModelState.Ready);
        await h.WaitForAsync("m1", _ => ProcessRecordsDocumentOnDisk(h).Processes[0].ReadyAt is not null, "the Ready record");
        var original = Assert.Single(h.Table.Running);

        await h.Driver.DisposeAsync();
        h.Host.SimulateAgentDeath();
        var parking = new ParkingRestartStore(new ProcessRecordStore(h.Options.RecordsPath));
        await using var driver = h.NewDriver(parking);
        h.Host.BeforeListCandidates = async () =>
        {
            h.Host.BeforeListCandidates = null;
            await original.ExitAsync(1);
            // Parked between the commit and the swap: the record names the new PID, _instances does not.
            await parking.Committed.Task.WaitAsync(Wait);
        };

        var report = await driver.RecoverAsync();
        parking.Release.TrySetResult();

        var restarted = Assert.Single(h.Table.Running);
        Assert.Empty(report.OrphansKilled);
        Assert.False(restarted.HasExited);
        Assert.DoesNotContain(restarted.Identity, h.Host.KillRequests);
        Assert.Equal(ModelState.Ready, (await WaitForStateAsync(driver, "m1", ModelState.Ready)).State);
    }

    [Fact]
    public async Task AnInterruptedStop_LeavesTheInstanceFailed_SoEnsureStartedStartsItAgain()
    {
        // MN-219: a kill that threw (Win32Exception, or the caller's token on Windows) left the instance in Stopping with
        // nothing supervising it, and every EnsureStarted answered AlreadyRunning.
        await using var h = new Harness();
        await h.LaunchAsync();
        await h.WaitForStateAsync("m1", ModelState.Ready);
        var first = Assert.Single(h.Table.Running);
        h.Host.FailNextKillTree = new System.ComponentModel.Win32Exception(5, "TerminateJobObject");

        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => h.Driver.StopAsync("m1", new RecordingStageSink()));
        Assert.Equal(ModelState.Failed, (await h.Driver.GetAsync("m1"))!.State);

        var again = await h.LaunchAsync(operationId: "op-2");

        Assert.Equal(RuntimeLaunchOutcome.Created, again.Outcome);
        Assert.True(first.HasExited);
        await h.WaitForStateAsync("m1", ModelState.Ready);
    }

    [Fact]
    public async Task Launch_WhenTheKeyCannotBeProtected_StartsNothing_AndLeavesNoPendingStart()
    {
        // MN-219: the key used to be protected after the create, outside the commit's cleanup, so a DPAPI failure left a
        // running llama-server with no record and its pending start in processes.json.
        await using var h = new Harness();
        h.Keys.FailNextProtect = new CryptographicException("DPAPI failed");

        await Assert.ThrowsAsync<CryptographicException>(() => h.LaunchAsync());

        Assert.Empty(h.Host.Starts);
        Assert.Empty(h.Table.Running);
        Assert.Empty(await h.Store.ListPendingAsync());
        Assert.Empty(await h.Store.ListAsync());
    }

    [Fact]
    public async Task Reboot_RecordsWithDeadPids_ReadStopped_AndAreRemoved()
    {
        await using var h = new Harness();
        await h.LaunchAsync();
        await h.LaunchAsync(Spec(argv: Argv("second")), modelId: "m2", operationId: "op-2");
        await h.WaitForStateAsync("m1", ModelState.Ready);
        Assert.Equal(2, ProcessRecordsDocumentOnDisk(h).Processes.Count);

        await h.RestartAgentAsync(() => h.Table.RebootAsync());

        Assert.Empty(await h.Driver.ListAsync());
        Assert.Equal(["m1", "m2"], h.Driver.LastRecovery!.Gone.Order(StringComparer.Ordinal));
        Assert.Empty(ProcessRecordsDocumentOnDisk(h).Processes);
        Assert.Null(await h.Driver.GetAsync("m1"));
    }

    // ------------------------------------------------------------ orphan sweep

    [Fact]
    public async Task OrphanSweep_KillsAProcessMatchingAPendingStart_AndRemovesThePendingStart()
    {
        await using var h = new Harness();
        var arguments = new[] { "-m", "x.gguf", "--alias", "orphan", "--port", "8123" };
        var at = DateTimeOffset.UtcNow.AddSeconds(-5);
        await h.Store.AddPendingAsync(new PendingProcessStart
        {
            ModelId = "orphaned",
            OperationId = "op-9",
            ExecutablePath = h.Executable.Path,
            Port = 8123,
            ArgvDigest = ProcessArgvDigest.Compute(arguments),
            At = at,
        });

        // The agent crashed between create and record: the process runs, no record names it.
        var orphan = h.Table.Spawn(h.Executable.Path, arguments, startTime: at.AddSeconds(1));
        // Same command line, but already running before the pending start was written: not provably ours.
        var older = h.Table.Spawn(h.Executable.Path, arguments, startTime: at.AddSeconds(-60));

        var report = await h.Driver.RecoverAsync();
        Assert.Equal([orphan.Pid], report.OrphansKilled);
        Assert.True(orphan.HasExited);
        Assert.False(older.HasExited);
        Assert.Contains(older.Pid, report.Foreign);
        Assert.Equal(["orphaned"], report.PendingResolved);
        Assert.Empty(await h.Store.ListPendingAsync());
        Assert.Empty(ProcessRecordsDocumentOnDisk(h).Pending);
    }

    [Fact]
    public async Task OrphanSweep_KillsADuplicateOfARecordedInstance_ButNotTheAdoptedOne()
    {
        await using var h = new Harness();
        await h.LaunchAsync();
        await h.WaitForStateAsync("m1", ModelState.Ready);
        var adopted = Assert.Single(h.Table.Running);
        FakeOsProcess? duplicate = null;
        await h.RestartAgentAsync(() =>
        {
            duplicate = h.Table.Spawn(adopted.ImagePath, adopted.Arguments, startTime: DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        });

        var report = await h.Driver.RecoverAsync();
        Assert.Equal([duplicate!.Pid], report.OrphansKilled);
        Assert.False(adopted.HasExited);
        Assert.True(duplicate.HasExited);
    }

    [Fact]
    public async Task OrphanSweep_NeverTouchesAnUnknownCommandLine_AnUnreadableOne_AnotherOwner_OrAnotherFolder()
    {
        await using var h = new Harness();
        var arguments = new[] { "-m", "x.gguf", "--alias", "orphan", "--port", "8123" };
        var at = DateTimeOffset.UtcNow.AddSeconds(-5);
        await h.Store.AddPendingAsync(new PendingProcessStart
        {
            ModelId = "orphaned",
            OperationId = "op-9",
            ExecutablePath = h.Executable.Path,
            Port = 8123,
            ArgvDigest = ProcessArgvDigest.Compute(arguments),
            At = at,
        });
        var later = at.AddSeconds(1);

        // Same image folder and account, but a command line the agent never built.
        var unknown = h.Table.Spawn(h.Executable.Path, ["-m", "y.gguf", "--port", "8000"], startTime: later);
        // Same image folder and account, command line unreadable: never killed (default answer of the host is null).
        var unreadable = h.Table.Spawn(h.Executable.Path, arguments: null, startTime: later);
        // The matching command line, but another account (the owner's own llama-server, spike S1 / C11).
        var otherOwner = h.Table.Spawn(h.Executable.Path, arguments, owner: @"DESKTOP\owner", startTime: later);
        // The matching command line and account, but outside the runtimes folder (D:\AI\llama.cpp\llama-server.exe).
        string elsewhere = Path.Combine(h.Root, "AI", "llama.cpp", "llama-server.exe");
        var outside = h.Table.Spawn(elsewhere, arguments, startTime: later);

        var report = await h.Driver.RecoverAsync();
        Assert.Empty(report.OrphansKilled);
        Assert.Equal([unknown.Pid, unreadable.Pid], report.Foreign.Order());
        Assert.All(new[] { unknown, unreadable, otherOwner, outside }, p => Assert.False(p.HasExited));
        Assert.Empty(h.Host.KillRequests);
        Assert.Empty(h.Table.Killed);
        Assert.Empty(await h.Store.ListPendingAsync()); // resolved: no process of the agent's was found for it
    }

    [Fact]
    public async Task ReadArgumentsDefault_AnswersNull_SoAHostThatCannotReadCommandLinesNeverLetsTheSweepKill()
    {
        IProcessHost host = new MinimalHost();
        Assert.Null(await host.ReadArgumentsAsync(new ProcessIdentity(1, DateTimeOffset.UtcNow, "x")));
    }

    // ------------------------------------------------------------ failed creates and torn state

    [Fact]
    public async Task FailedCreate_RemovesThePendingStart_AndRecordsNothing()
    {
        await using var h = new Harness();
        h.Host.FailNextStart = new IOException("CreateProcessW failed: 2");

        await Assert.ThrowsAsync<IOException>(() => h.LaunchAsync());
        var document = ProcessRecordsDocumentOnDisk(h);
        Assert.Empty(document.Pending);
        Assert.Empty(document.Processes);
        Assert.Null(await h.Driver.GetAsync("m1"));
        Assert.Empty(h.Table.Running);
    }

    [Fact]
    public async Task PendingStart_IsDurableBeforeTheCreate()
    {
        await using var h = new Harness();
        PendingProcessStart? seen = null;
        h.Host.TamperArguments = args =>
        {
            // Called inside StartAsync, i.e. during the create: the pending start must already be on disk.
            seen = Assert.Single(ProcessRecordStore.Load(h.Options.RecordsPath).Pending);
            return args;
        };

        await h.LaunchAsync();
        Assert.NotNull(seen);
        var record = Assert.Single(ProcessRecordsDocumentOnDisk(h).Processes);
        Assert.Equal(record.ArgvDigest, seen.ArgvDigest);
        Assert.Equal(h.Executable.Path, seen.ExecutablePath);
        Assert.Equal(h.Port, seen.Port);
        Assert.Equal("op-1", seen.OperationId);
    }

    [Fact]
    public async Task AFailedReadyWrite_LeavesTheInstanceReadyAndServing_NotFailed()
    {
        // MN-239, D129 (7e). The Ready write is a processes.json rewrite. It used to throw out of MarkReadyAsync past
        // its finally into SuperviseAsync's catch-all, which called SetFailed — in memory only. llama-server was alive,
        // had answered /health and /v1/models, and was serving; the master was told it had failed, and the supervision
        // loop had already exited, so nothing would ever correct it. A disk that cannot take the record costs an
        // adoption after a restart, not the instance.
        await using var h = new Harness();
        var store = new FailingReadyWriteStore(h.Store);
        await h.Driver.DisposeAsync();
        await using var driver = h.NewDriver(store);

        await driver.LaunchAsync(
            new RuntimeLaunch { ModelId = "m1", OperationId = "op-1", Mode = LaunchMode.EnsureStarted, Spec = Spec(), HostPort = h.Port },
            h.Stages);
        var status = await WaitForStateAsync(driver, "m1", ModelState.Ready);

        Assert.Equal(ModelState.Ready, status.State);
        Assert.Null(status.Error);
        Assert.True(store.Refused > 0, "the Ready write was never attempted, so this test proves nothing");
        Assert.Single(h.Table.Running);
        Assert.NotNull(await driver.GetUpstreamAsync("m1"));

        // And the record on disk is the pre-Ready one, which is the real cost: an agent restart will not adopt it.
        Assert.Null(Assert.Single(ProcessRecordsDocumentOnDisk(h).Processes).ReadyAt);
    }

    [Fact]
    public async Task APendingStart_WhoseFolderTheSweepCouldNotList_IsKept_NotMarkedResolved()
    {
        // MN-239, D129 (7f). Recovery removed every pending start unconditionally, while the sweep skipped a folder
        // whose listing had failed. The orphan the entry existed to find was then forgotten for good — holding its
        // port and its VRAM until an operator noticed. Recovery runs once per agent lifetime, so "forgotten" means
        // until someone deletes the process by hand.
        await using var h = new Harness();
        await h.LaunchAsync();
        await h.WaitForStateAsync("m1", ModelState.Ready);

        // The agent died between a second model's create and its commit: a pending start with no record, which is
        // exactly what the sweep exists for.
        await h.RestartAgentAsync(() => h.Store.AddPendingAsync(new PendingProcessStart
        {
            ModelId = "m2",
            OperationId = "op-2",
            ExecutablePath = h.Executable.Path,
            ArgvDigest = new string('0', 64),
            Port = h.PortFor("m2"),
            At = DateTimeOffset.UtcNow,
        }));
        h.Host.FailListCandidates = new IOException("the version folder cannot be listed");

        var report = await h.Driver.RecoverAsync();

        Assert.DoesNotContain("m2", report.PendingResolved);
        Assert.Contains("m2", ProcessRecordStore.Load(h.Options.RecordsPath).Pending.Select(p => p.ModelId));

        // Negative control: a sweep that could list the folder resolves it, exactly as before.
        h.Host.FailListCandidates = null;
        await h.Driver.DisposeAsync();
        await using var retry = h.NewDriver();
        var second = await retry.RecoverAsync();

        Assert.Contains("m2", second.PendingResolved);
        Assert.Empty(ProcessRecordStore.Load(h.Options.RecordsPath).Pending);
    }

    [Fact]
    public async Task FailedCommit_EndsTheProcess_SoNothingRunsUnrecorded()
    {
        await using var h = new Harness();
        var failing = new FailingCommitStore(h.Store);
        await h.Driver.DisposeAsync();
        var driver = h.NewDriver(failing);
        await using var _ = driver;

        await Assert.ThrowsAsync<IOException>(() => driver.LaunchAsync(
            new RuntimeLaunch { ModelId = "m1", OperationId = "op-1", Mode = LaunchMode.EnsureStarted, Spec = Spec(), HostPort = h.Port },
            h.Stages));
        Assert.Empty(h.Table.Running);
        Assert.Single(h.Table.Killed);
        Assert.Empty(await h.Store.ListPendingAsync());
        Assert.Null(await driver.GetAsync("m1"));
    }

    [Fact]
    public async Task TornProcessesJson_IsReported_AndLeftUntouched_NeverSilentlyEmptied()
    {
        await using var h = new Harness();
        await h.LaunchAsync();
        await h.WaitForStateAsync("m1", ModelState.Ready);
        byte[] valid = File.ReadAllBytes(h.Options.RecordsPath);

        // A torn temp file from a crash during a write is ignored: the last complete document stays in force.
        File.WriteAllBytes(h.Options.RecordsPath + ".tmp", valid.AsSpan(0, valid.Length / 2).ToArray());
        await h.RestartAgentAsync();
        Assert.Equal(["m1"], (await h.Driver.RecoverAsync()).Adopted);

        // A torn document itself is an error the agent reports; nothing is adopted, killed or rewritten.
        byte[] torn = valid.AsSpan(0, valid.Length / 2).ToArray();
        await h.RestartAgentAsync(() =>
        {
            File.WriteAllBytes(h.Options.RecordsPath, torn);
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => h.Driver.RecoverAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => h.Driver.ListAsync());
        Assert.Equal(torn, File.ReadAllBytes(h.Options.RecordsPath));
        Assert.Single(h.Table.Running);
        Assert.Empty(h.Table.Killed);
    }

    [Fact]
    public async Task RecordStore_RoundTripsTheFrozenFixture_AndRefusesDuplicateModels()
    {
        string root = Path.Combine(Path.GetTempPath(), "mirbadllm-mn211", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, ProcessRecordsDocument.FileName);
            File.Copy(Path.Combine(SourceDirectory(), "..", "Contracts", "Fixtures", "P3", "ProcessRecordsDocument.json"), path);
            using var store = new ProcessRecordStore(path);
            var record = Assert.Single(await store.ListAsync());
            Assert.Equal(18244, record.Process.Pid);
            Assert.Equal("9c4e21b07a5d3f18", Assert.Single(await store.ListPendingAsync()).ArgvDigest);

            await store.CommitPendingAsync(record with { ModelId = "bge-reranker", OperationId = "op-302" });
            using var reread = new ProcessRecordStore(path);
            Assert.Equal(["bge-reranker", "qwen3-embedding-4b"], (await reread.ListAsync()).Select(r => r.ModelId).Order(StringComparer.Ordinal));
            Assert.Empty(await reread.ListPendingAsync());
            Assert.True(await reread.RemoveAsync("bge-reranker"));
            Assert.False(await reread.RemoveAsync("bge-reranker"));
            Assert.False(await reread.RemovePendingAsync("nothing"));

            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            node["processes"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(record, AgentJson.Options)));
            File.WriteAllText(path, node.ToJsonString());
            Assert.Equal(2, ((System.Text.Json.Nodes.JsonArray)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["processes"]!).Count);
            Assert.Throws<InvalidDataException>(() => ProcessRecordStore.Load(path));
            File.WriteAllText(path, """{"schemaVersion": 2, "processes": [], "pending": []}""");
            Assert.Throws<InvalidDataException>(() => ProcessRecordStore.Load(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ------------------------------------------------------------ ports

    [Fact]
    public async Task PortTakenOnAnyAddress_TheNextPortIsUsed()
    {
        int first = TestPorts.ReserveBlock(2);
        using var holder = new TcpListener(IPAddress.Any, first);
        holder.Start();
        await using var h = new Harness(port: first);

        var result = await h.LaunchAsync();
        Assert.Equal(first + 1, result.Instance.HostPort);
        Assert.Contains((first + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Assert.Single(h.Host.Starts).Arguments);
        await h.WaitForStateAsync("m1", ModelState.Ready);
        Assert.Equal(first + 1, Assert.Single(ProcessRecordsDocumentOnDisk(h).Processes).Port);
    }

    [Fact]
    public async Task TwoModelsInOneHarness_ListenOnlyOnPortsReservedForThisProcess()
    {
        // MN-230: with one port for every model the second launch walked to "port + 1", a port TestPorts hands to the
        // next test that reserves one (reservations are adjacent), which then failed with "address already in use".
        await using var h = new Harness();
        await h.LaunchAsync();
        await h.LaunchAsync(Spec(argv: Argv("second")), modelId: "m2", operationId: "op-2");
        await h.WaitForStateAsync("m1", ModelState.Ready);
        await h.WaitForStateAsync("m2", ModelState.Ready);

        var ports = ProcessRecordsDocumentOnDisk(h).Processes.Select(r => r.Port).ToList();
        Assert.Equal(2, ports.Distinct().Count());
        Assert.All(ports, p => Assert.True(TestPorts.IsReserved(p), $"port {p} was not reserved through TestPorts"));
    }

    [Fact]
    public void ProcessPorts_SkipTakenAndAvoidedPorts()
    {
        Assert.Equal(8101, ProcessPorts.Choose(8101, new HashSet<int>(), _ => false));
        Assert.Equal(8103, ProcessPorts.Choose(8101, new HashSet<int> { 8102 }, p => p == 8101));
        Assert.Throws<InvalidOperationException>(() => ProcessPorts.Choose(65535, new HashSet<int>(), _ => true));

        int port = TestPorts.Reserve();
        Assert.False(ProcessPorts.IsTaken(port));
        using var loopback = new TcpListener(IPAddress.Loopback, port);
        loopback.Start();
        Assert.True(ProcessPorts.IsTaken(port));
    }

    // ------------------------------------------------------------ CheckSpec guardrails

    [Fact]
    public async Task CheckSpec_AcceptsTheBuildersSpec_AndRefusesExecutablesNotInTheCatalogue()
    {
        await using var h = new Harness();
        h.Driver.CheckSpec("m1", Spec());
        h.Driver.CheckSpec("m1", Spec(version: null)); // the node default

        AssertRejected(() => h.Driver.CheckSpec("m1", Spec(executable: "llama-cli")), ErrorCodes.ExecutableMissing);
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec(version: "b9999")), ErrorCodes.ExecutableMissing);
        h.Catalog.Add("b11500", verified: false, isDefault: false);
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec(version: "b11500")), ErrorCodes.ExecutableUnverified);
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec(executable: null)));
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec(executable: h.Executable.Path)), ErrorCodes.ExecutableMissing); // never a path

        // A refused spec starts nothing.
        await Assert.ThrowsAsync<AgentException>(() => h.LaunchAsync(Spec(version: "b11500")));
        Assert.Empty(h.Host.Starts);
    }

    [Fact]
    public async Task CheckSpec_RefusesReservedEnvironmentNames_InAnyCase()
    {
        await using var h = new Harness();
        foreach (string name in ProcessEnvironment.Reserved.Concat(["path", "llama_api_key", "Cuda_Visible_Devices", "NVIDIA_VISIBLE_DEVICES"]))
        {
            AssertRejected(() => h.Driver.CheckSpec("m1", Spec(env: new Dictionary<string, string> { [name] = "x" })));
        }

        AssertRejected(() => h.Driver.CheckSpec("m1", Spec(env: new Dictionary<string, string> { ["1BAD"] = "x" })));
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec(env: new Dictionary<string, string> { ["A"] = "x" }, secretEnvNames: ["B"])));
        h.Driver.CheckSpec("m1", Spec(env: new Dictionary<string, string> { ["HF_HUB_OFFLINE"] = "1" }));
    }

    [Theory]
    [InlineData("--log-file", "{models}/x.log")]
    [InlineData("--lora", "{models}/" + ModelFile)]
    [InlineData("-hf", "Qwen/Qwen3-Embedding-4B-GGUF")]
    [InlineData("--hf-token", "hf_secret")]
    [InlineData("--slot-save-path", "C:\\temp")]
    [InlineData("--model-url", "https://example.invalid/x.gguf")]
    [InlineData("--path", "/var/www")]
    [InlineData("--api-key", "k")]
    [InlineData("--api-key-file", "k.txt")]
    [InlineData("--webui", null)]
    [InlineData("--ui", null)]
    [InlineData("--log-file={models}/x.log", null)]
    [InlineData("--chat-template-file", "C:\\Windows\\win.ini")]
    [InlineData("--chat-template-file={models}/" + ModelFile, null)]
    [InlineData("--rpc", "10.0.0.1:50052")]
    public async Task CheckSpec_RefusesPathValuedAndOwnedFlagsInExtraArgs(string flag, string? value)
    {
        await using var h = new Harness();
        var extra = value is null ? new[] { flag } : [flag, value];
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec(argv: Argv(extra: extra))));
    }

    [Fact]
    public async Task CheckSpec_RefusesModelPathsThatEscape_AndArgvThatBreaksTheBuildersShape()
    {
        await using var h = new Harness();
        List<string> With(string flag, string value)
        {
            var argv = Argv();
            argv[argv.IndexOf(flag) + 1] = value;
            return argv;
        }

        File.WriteAllText(Path.Combine(h.Root, "outside.gguf"), "x");
        foreach (string path in new[]
        {
            "{models}/../outside.gguf", "{models}/./" + ModelFile, "{models}/missing.gguf", "{models}//" + ModelFile,
            "{cache}/x.gguf", Path.Combine(h.ModelsRoot, ModelFile), "{models}/C:/x.gguf", "{models}/a\\..\\..\\outside.gguf",
        })
        {
            AssertRejected(() => h.Driver.CheckSpec("m1", Spec(argv: With("-m", path))));
        }

        // A symlink inside the models folder pointing outside it (skipped where the OS refuses to create one).
        string link = Path.Combine(h.ModelsRoot, "escape");
        try
        {
            Directory.CreateSymbolicLink(link, h.Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            link = "";
        }

        if (link.Length > 0)
        {
            AssertRejected(() => h.Driver.CheckSpec("m1", Spec(argv: With("-m", "{models}/escape/outside.gguf"))));
        }

        AssertRejected(() => h.Driver.CheckSpec("m1", Spec(argv: With("--host", "0.0.0.0"))));
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec(argv: With("--port", "9000"))));
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec(argv: With("--fit", "on"))));
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec(argv: With("--cache-ram", "8192"))));
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec(argv: Argv(extra: ["--port", "{port}"]))));
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec(argv: Argv(extra: ["--alias", "two"]))));
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec(argv: Argv(extra: ["-m", "{models}/" + ModelFile]))));
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec(argv: Argv(extra: ["--device", "CUDA1"]))));
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec(argv: Argv(extra: ["--host=0.0.0.0"]))));
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec(argv: Argv(extra: ["{port}"]))));
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec(argv: Argv(extra: ["--override-kv", "x={models}"]))));
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec(argv: [.. Argv().Where((_, i) => i is not 2 and not 3)]))); // no --alias
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec(argv: Argv(extra: ["--threads", "4\n--host 0.0.0.0"]))));

        // Allowed builder extras: a chat template file inside the model folder, typed flags in ExtraArgs.
        h.Driver.CheckSpec("m1", Spec(argv: Argv(extra: ["--chat-template-file", "{models}/" + ModelFile, "--threads", "8", "-lv", "4"])));
    }

    [Fact]
    public async Task CheckSpec_RefusesContainerShapedSpecs_AndUnknownGpus()
    {
        await using var h = new Harness();
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec() with { Kind = LaunchDelivery.Container }));
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec() with { Runtime = RuntimeKind.Vllm }));
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec() with { Image = "vllm/vllm-openai:latest" }));
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec() with { Mounts = [new MountSpec(LogicalVolumes.Models, null, LogicalVolumes.ModelsTarget, true)] }));
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec() with { IpcHost = true }));
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec() with { Labels = new Dictionary<string, string> { [ContainerLabels.Node] = "dgx" } }));
        AssertRejected(() => h.Driver.CheckSpec("m1", Spec(gpus: new GpuSelection { Mode = GpuSelectionMode.Uuids, Uuids = ["not-a-gpu"] })));
        Assert.Equal(400, Assert.Throws<AgentException>(() => h.Driver.CheckSpec("../m1", Spec())).StatusCode);

        h.Gpus = new FakeGpuInventory([new GpuCapability { Index = 0, Uuid = "GPU-00000000-0000-0000-0000-000000000000", Name = "RTX 5070", MemoryTotalGiB = 11.94 }]);
        await h.Driver.DisposeAsync();
        await using var driver = h.NewDriver();
        AssertRejected(() => driver.CheckSpec("m1", Spec()), ErrorCodes.GpuMissing);
    }

    // ------------------------------------------------------------ secrets, logs, folders

    [Fact]
    public async Task SecretEnvironmentValues_ReachTheProcessOnly_NeverTheRecordOrLaunchConfig()
    {
        await using var h = new Harness();
        var env = Knobs();
        env["HF_TOKEN_FOR_TEST"] = "s3cr3t-value";
        env["PLAIN"] = "visible";
        await h.LaunchAsync(Spec(env: env, secretEnvNames: ["HF_TOKEN_FOR_TEST"]));

        Assert.Equal("s3cr3t-value", Assert.Single(h.Host.Starts).Environment["HF_TOKEN_FOR_TEST"]);
        Assert.DoesNotContain("s3cr3t-value", File.ReadAllText(h.Options.RecordsPath), StringComparison.Ordinal);
        var config = await h.Driver.GetLaunchConfigAsync("m1");
        Assert.Null(config!.Env["HF_TOKEN_FOR_TEST"]);
        Assert.Equal("visible", config.Env["PLAIN"]);
    }

    [Fact]
    public async Task Logs_StreamTheProcessLogFile_AndFolderInUseFollowsTheArgv()
    {
        await using var h = new Harness();
        await h.LaunchAsync();
        await h.WaitForStateAsync("m1", ModelState.Ready);

        var lines = new List<string>();
        await foreach (string line in h.Driver.StreamLogsAsync("m1", tail: 50, follow: false))
        {
            lines.Add(line);
        }

        Assert.Contains(lines, l => l.Contains($"server is listening on http://127.0.0.1:{h.Port}", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("model loaded", StringComparison.Ordinal));

        Assert.True(await h.Driver.IsFolderInUseAsync("Qwen3-Embedding-4B-GGUF"));
        Assert.False(await h.Driver.IsFolderInUseAsync("Qwen3-Embedding-4B"));
        await Assert.ThrowsAsync<AgentException>(() => h.Driver.IsFolderInUseAsync("../models"));
    }

    [Fact]
    public async Task LogFiles_OfARunningProcess_AreRotatedByCopyAndTruncate_AndTheChildKeepsWriting()
    {
        // MN-219 (D128): logs rotated only before a start, so a long-running llama-server's file grew without bound. While
        // it runs the driver copies the file to .1 and truncates it in place (the child appends: its next write lands at
        // the new end); the process is never restarted for it.
        await using var h = new Harness(configure: o => { o.LogMaxBytes = 4096; o.LogFilesKept = 2; });
        await h.LaunchAsync();
        await h.WaitForStateAsync("m1", ModelState.Ready);
        string path = ProcessRecordsDocumentOnDisk(h).Processes[0].LogPath;
        var process = Assert.Single(h.Table.Running);

        await using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            await writer.WriteAsync(Encoding.UTF8.GetBytes(new string('x', 8000) + "\n"));
        }

        var watch = Stopwatch.StartNew();
        while (!File.Exists(path + ".1") && watch.Elapsed < Wait)
        {
            await Task.Delay(20);
        }

        Assert.True(File.Exists(path + ".1"), "the running process's log was not rotated");
        Assert.True(new FileInfo(path + ".1").Length >= 8000);
        Assert.True(new FileInfo(path).Length < 4096);
        Assert.False(process.HasExited);
        Assert.Equal(ModelState.Ready, (await h.Driver.GetAsync("m1"))!.State);
    }

    [Fact]
    public void LogFiles_RotateBeforeAStart_KeepingTwo()
    {
        string root = Path.Combine(Path.GetTempPath(), "mirbadllm-mn211", Guid.NewGuid().ToString("N"));
        try
        {
            var logs = new ProcessLogFiles(new ProcessRuntimeOptions { StateDirectory = root, LogMaxBytes = 10, LogFilesKept = 2 });
            string path = logs.PrepareForStart("m1");
            for (int i = 1; i <= 3; i++)
            {
                File.WriteAllText(path, $"run {i} ............");
                Assert.Equal(path, logs.PrepareForStart("m1"));
            }

            Assert.False(File.Exists(path));
            Assert.StartsWith("run 3", File.ReadAllText(path + ".1"), StringComparison.Ordinal);
            Assert.StartsWith("run 2", File.ReadAllText(path + ".2"), StringComparison.Ordinal);
            Assert.False(File.Exists(path + ".3"));

            File.WriteAllText(path, "small");
            logs.PrepareForStart("m1");
            Assert.Equal("small", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ArgvDigest_IsSixteenHexOfSha256OverTheArgumentsJoinedWithNul()
    {
        string[] args = ["-m", "C:\\models\\a b.gguf", "--alias", "guoxuter/ov_intent_analysis_sft:v7_q8", "--port", "8101"];
        Assert.Equal(ExpectedDigest(args), ProcessArgvDigest.Compute(args));
        Assert.Matches("^[0-9a-f]{16}$", ProcessArgvDigest.Compute(args));
        Assert.NotEqual(ProcessArgvDigest.Compute(["a b"]), ProcessArgvDigest.Compute(["a", "b"]));
        Assert.NotEqual(ProcessArgvDigest.Compute(["--port", "8101"]), ProcessArgvDigest.Compute(["--port", "8102"]));
    }

    [Fact]
    public void FailureText_PutsTheRootCauseFirst_ThenTheHeadlineAndTheTail()
    {
        string text = ProcessRuntimeDriver.BuildFailure(
            "llama-server exited with code 1 before it was Ready.",
            ["I build: 11476", "error while handling argument \"--device\": invalid device: CUDA1", "usage: ..."]);
        Assert.StartsWith("Cause: error while handling argument \"--device\": invalid device: CUDA1\n\nllama-server exited with code 1", text, StringComparison.Ordinal);
        Assert.EndsWith("usage: ...", text, StringComparison.Ordinal);
        Assert.Equal("Cause: headline\n\nheadline", ProcessRuntimeDriver.BuildFailure("headline", []));
    }

    [Fact]
    public async Task GpuOutOfMemoryAtWarmUp_IsFailedWithTheCause_NotReady_AndTheProcessIsStopped()
    {
        // llama-server on a Mac whose context does not fit Metal: the warm-up fails, /health still answers 200.
        await using var h = new Harness();
        var env = Knobs(load: 100);
        env[MirbadLLM.FakeLlamaServer.FakeLlamaOptions.GpuOutOfMemoryVariable] = "1";
        await h.LaunchAsync(Spec(env));

        var failed = await h.WaitForStateAsync("m1", ModelState.Failed);
        Assert.StartsWith(
            "Cause: error: Insufficient Memory (00000008:kIOGPUCommandBufferCallbackErrorOutOfMemory)\n\nThe GPU ran out of memory",
            failed.Error, StringComparison.Ordinal);
        Assert.Contains("Lower the context size or the parallel slots", failed.Error, StringComparison.Ordinal);
        Assert.Single(h.Table.Killed);
        Assert.Empty(h.Table.Running);
        Assert.Null(await h.Driver.GetUpstreamAsync("m1"));

        // The next start appends to the same log: only what it wrote counts, so a start that fits is Ready.
        await h.LaunchAsync(Spec(), operationId: "op-2");
        await h.WaitForStateAsync("m1", ModelState.Ready);
    }

    [Fact]
    public void BackendFailure_NamesTheOutOfMemoryLine_AndIgnoresAHealthyLog()
    {
        string? text = ProcessRuntimeDriver.BackendFailure(
        [
            "0.24.312.384 E ggml_metal_synchronize: error: command buffer 0 failed with status 5",
            "0.24.312.385 E error: Insufficient Memory (00000008:kIOGPUCommandBufferCallbackErrorOutOfMemory)",
            "0.24.312.413 E ggml_metal_graph_compute: backend is in error state from a previous command buffer failure - recreate the backend to recover",
            "0.30.860.113 I srv    load_model: initializing, n_slots = 2, n_ctx_slot = 40960, kv_unified = 'false'",
        ]);
        Assert.NotNull(text);
        Assert.StartsWith("Cause: error: Insufficient Memory (00000008:kIOGPUCommandBufferCallbackErrorOutOfMemory)\n\n", text, StringComparison.Ordinal);
        Assert.EndsWith("kv_unified = 'false'", text, StringComparison.Ordinal);

        string? generic = ProcessRuntimeDriver.BackendFailure(["E graph_compute: ggml_backend_sched_graph_compute_async failed with error -1"]);
        Assert.StartsWith("Cause: llama-server's GPU backend failed", generic, StringComparison.Ordinal);

        Assert.Null(ProcessRuntimeDriver.BackendFailure(["I main: model loaded", "I srv  update_slots: all slots are idle"]));
        Assert.Null(ProcessRuntimeDriver.BackendFailure([]));
    }

    // ------------------------------------------------------------ the fake llama-server itself

    [Fact]
    public async Task FakeServer_LoadsThenServes_AsLlamaServerB11476Does()
    {
        int port = TestPorts.Reserve();
        var log = new StringWriter();
        var args = Argv().Select(a => a switch
        {
            "{port}" => port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "{models}/" + ModelFile => "C:/models/x.gguf",
            _ => a,
        }).ToList();
        await using var server = await FakeServer.StartAsync(
            args, new Dictionary<string, string> { ["LLAMA_API_KEY"] = "k", ["FAKE_LLAMA_LOAD_MS"] = "400", ["FAKE_LLAMA_N_EMBD"] = "8" }, log);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "k");

        using (var loading = await http.GetAsync("health"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, loading.StatusCode);
            Assert.Equal("""{"error":{"message":"Loading model","type":"unavailable_error","code":503}}""", await loading.Content.ReadAsStringAsync());
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("v1/models")).StatusCode);
        while (!server.IsLoaded)
        {
            await Task.Delay(20);
        }

        Assert.Equal("""{"status":"ok"}""", await http.GetStringAsync("health"));
        Assert.Equal(16384 / 2, server.Options.SlotContext);

        // dimensions is ignored and the request model echoed unvalidated (S1 / C8).
        using var embed = await http.PostAsync("v1/embeddings", Json("""{"model":"no-such-model","input":["hello world","x"],"dimensions":4}"""));
        using var embedding = JsonDocument.Parse(await embed.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, embed.StatusCode);
        Assert.Equal("no-such-model", embedding.RootElement.GetProperty("model").GetString());
        Assert.Equal(2, embedding.RootElement.GetProperty("data").GetArrayLength());
        var vector = embedding.RootElement.GetProperty("data")[0].GetProperty("embedding").EnumerateArray().Select(e => e.GetDouble()).ToList();
        Assert.Equal(8, vector.Count);
        Assert.InRange(Math.Sqrt(vector.Sum(v => v * v)), 0.9999, 1.0001);

        using var base64 = await http.PostAsync("v1/embeddings", Json("""{"input":"hello","encoding_format":"base64"}"""));
        using var b64 = JsonDocument.Parse(await base64.Content.ReadAsStringAsync());
        Assert.Equal(32, Convert.FromBase64String(b64.RootElement.GetProperty("data")[0].GetProperty("embedding").GetString()!).Length);

        string tooLong = string.Join(' ', Enumerable.Repeat("word", 8193));
        using var exceed = await http.PostAsync("v1/embeddings", Json(JsonSerializer.Serialize(new { input = tooLong })));
        Assert.Equal(HttpStatusCode.BadRequest, exceed.StatusCode);
        using var exceedBody = JsonDocument.Parse(await exceed.Content.ReadAsStringAsync());
        Assert.Equal("exceed_context_size_error", exceedBody.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal(8193, exceedBody.RootElement.GetProperty("error").GetProperty("n_prompt_tokens").GetInt32());
        Assert.Equal(8192, exceedBody.RootElement.GetProperty("error").GetProperty("n_ctx").GetInt32());

        using var chat = await http.PostAsync("v1/chat/completions", Json("""{"model":"m","stream":true,"messages":[{"role":"user","content":"hi there"}]}"""));
        Assert.Equal("text/event-stream", chat.Content.Headers.ContentType?.MediaType);
        string sse = await chat.Content.ReadAsStringAsync();
        Assert.EndsWith("data: [DONE]\n\n", sse, StringComparison.Ordinal);
        Assert.Contains("\"delta\":{\"content\":\" fake\"}", sse, StringComparison.Ordinal);

        string metrics = await http.GetStringAsync("metrics");
        Assert.Contains("llamacpp:prompt_tokens_total ", metrics, StringComparison.Ordinal);
        Assert.Equal(LlamaCppMetrics.Families, FakeServer.MetricFamilies); // the frozen names

        Assert.Contains("server is listening", log.ToString(), StringComparison.Ordinal);
        Assert.False(server.Exited.IsCompleted);
    }

    [Fact]
    public async Task FakeServer_ArgumentErrorExitsWithOne_AndANonEmbeddingServerRefusesEmbeddings()
    {
        var log = new StringWriter();
        await using var broken = await FakeServer.StartAsync(["--port", "x"], new Dictionary<string, string>(), log);
        Assert.Equal(1, await broken.Exited.WaitAsync(Wait));
        Assert.Contains("error while handling argument \"--port\"", log.ToString(), StringComparison.Ordinal);

        int port = TestPorts.Reserve();
        await using var chatOnly = await FakeServer.StartAsync(
            ["--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--alias", "chat", "--fit", "off", "--no-ui", "--cache-ram", "0", "--metrics"],
            new Dictionary<string, string> { ["FAKE_LLAMA_LOAD_MS"] = "0" }, new StringWriter());
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        while (!chatOnly.IsLoaded)
        {
            await Task.Delay(10);
        }

        Assert.Equal(HttpStatusCode.NotImplemented, (await http.PostAsync("v1/embeddings", Json("""{"input":"x"}"""))).StatusCode);
        Assert.Contains("\"id\":\"chat\"", await http.GetStringAsync("v1/models"), StringComparison.Ordinal); // no key set: open
    }

    // ------------------------------------------------------------ Windows: the real fake server through MN-212's host

    [Fact]
    [Trait("Os", "Windows")]
    public async Task Windows_RealFakeServerThroughTheWindowsProcessHost_LaunchReadyStop()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only: the Windows process host (MN-212) runs fake-llama-server.exe as a real child process.");
            return;
        }

        // MN-219: MN-212 is merged, so the host is referenced directly (a reflection lookup could skip silently).
        IProcessHost windowsHost = new MirbadLLM.NodeRuntime.Runtimes.Process.Windows.WindowsProcessHost();

        // Copy the fake server into its own version folder, so the sweep's candidate folder holds nothing else.
        string root = Path.Combine(Path.GetTempPath(), "mirbadllm-mn211", Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, "runtimes", "llama.cpp", "b0000");
        Directory.CreateDirectory(folder);
        foreach (string file in Directory.GetFiles(AppContext.BaseDirectory, "fake-llama-server*"))
        {
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        }

        string models = Path.Combine(root, "models");
        Directory.CreateDirectory(Path.Combine(models, Path.GetDirectoryName(ModelFile)!));
        File.WriteAllBytes(Path.Combine(models, ModelFile), [1]);
        var catalog = new SingleExecutableCatalog(new ResolvedExecutable(ExecutableNames.LlamaServer, "b0000", Path.Combine(folder, "fake-llama-server.exe"), folder, null));
        var options = new ProcessRuntimeOptions
        {
            NodeId = NodeId,
            ModelsRoot = models,
            CacheRoot = Path.Combine(root, "cache"),
            StateDirectory = Path.Combine(root, "state"),
            HealthPollInterval = TimeSpan.FromMilliseconds(100),
        };
        using var http = new HttpClient();
        using var store = new ProcessRecordStore(options.RecordsPath);
        await using var driver = new ProcessRuntimeDriver(
            windowsHost, store, catalog, new FakeInstanceKeyProtector(), new ContainerRuntimeDriverTests.FakePortAllocator { Next = TestPorts.Reserve() },
            http, Microsoft.Extensions.Options.Options.Create(options), NullLogger<ProcessRuntimeDriver>.Instance);
        try
        {
            await driver.LaunchAsync(
                new RuntimeLaunch { ModelId = "m1", OperationId = "op-1", Mode = LaunchMode.EnsureStarted, Spec = Spec(version: "b0000", gpus: new GpuSelection { Mode = GpuSelectionMode.None }), HostPort = 0 },
                new RecordingStageSink());
            var watch = Stopwatch.StartNew();
            while ((await driver.GetAsync("m1"))?.State != ModelState.Ready && watch.Elapsed < TimeSpan.FromSeconds(60))
            {
                await Task.Delay(100);
            }

            Assert.Equal(ModelState.Ready, (await driver.GetAsync("m1"))?.State);
        }
        finally
        {
            await driver.StopAsync("m1", new RecordingStageSink());
            Directory.Delete(root, recursive: true);
        }
    }


    // ------------------------------------------------------------ helpers

    private static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "") =>
        Path.GetDirectoryName(sourceFile)!;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static ProcessRecordsDocument ProcessRecordsDocumentOnDisk(Harness h) => ProcessRecordStore.Load(h.Options.RecordsPath);

    private static async Task<RuntimeStatus> WaitForStateAsync(ProcessRuntimeDriver driver, string modelId, ModelState state)
    {
        var watch = Stopwatch.StartNew();
        RuntimeStatus? status = null;
        while (watch.Elapsed < Wait)
        {
            status = await driver.GetAsync(modelId);
            if (status?.State == state)
            {
                return status;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"waiting for {state}: last {(status is null ? "none" : $"{status.State} ({status.Error})")}.");
    }

    internal sealed class FakeGpuInventory(IReadOnlyList<GpuCapability> last) : IGpuInventory
    {
        public IReadOnlyList<GpuCapability>? Last { get; } = last;

        public Task<IReadOnlyList<GpuCapability>> RefreshAsync(CancellationToken ct = default) => Task.FromResult(Last!);
    }

    internal sealed class FakeGpuStartMeter(double measuredGiB) : IGpuStartMeter
    {
        public List<string> Marked { get; } = [];

        public Task<GpuStartMark?> MarkAsync(IReadOnlyList<string> gpuUuids, CancellationToken ct = default)
        {
            Marked.AddRange(gpuUuids);
            return Task.FromResult<GpuStartMark?>(new GpuStartMark(DateTimeOffset.UtcNow, gpuUuids.ToDictionary(u => u, _ => 3.1)));
        }

        public Task<double?> MeasureAsync(GpuStartMark mark, CancellationToken ct = default) => Task.FromResult<double?>(measuredGiB);
    }

    /// <summary>Lets loopback probes through only while open (an adopted instance's probe that hangs).</summary>
    private sealed class GatedHandler() : DelegatingHandler(new SocketsHttpHandler())
    {
        private volatile TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => _open.TrySetResult();

        public void Close() => _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await _open.Task.WaitAsync(cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// <paramref name="inner"/>, but the write that marks an instance Ready (<c>ReadyAt</c> set) fails as a full or
    /// read-only disk would (MN-239, D129 (7e)).
    /// </summary>
    /// <summary>
    /// <paramref name="inner"/>, but the commit of a <b>restart</b> (<c>RestartCount 1</c>) parks the restarting task
    /// after the write until <see cref="Release"/> is set — the window between a restart's commit and its swap of the
    /// instance's in-memory process (MN-239 bug F).
    /// </summary>
    private sealed class ParkingRestartStore(ProcessRecordStore inner) : IProcessRecordStore, IDisposable
    {
        public TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ProcessRecord>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);

        public Task<ProcessRecord?> GetAsync(string modelId, CancellationToken ct = default) => inner.GetAsync(modelId, ct);

        public async Task UpsertAsync(ProcessRecord record, CancellationToken ct = default)
        {
            await inner.UpsertAsync(record, ct);
            await ParkAsync(record);
        }

        public Task<bool> RemoveAsync(string modelId, CancellationToken ct = default) => inner.RemoveAsync(modelId, ct);

        public Task<IReadOnlyList<PendingProcessStart>> ListPendingAsync(CancellationToken ct = default) => inner.ListPendingAsync(ct);

        public Task AddPendingAsync(PendingProcessStart pending, CancellationToken ct = default) => inner.AddPendingAsync(pending, ct);

        public async Task CommitPendingAsync(ProcessRecord record, CancellationToken ct = default)
        {
            await inner.CommitPendingAsync(record, ct);
            await ParkAsync(record);
        }

        public Task<bool> RemovePendingAsync(string modelId, CancellationToken ct = default) => inner.RemovePendingAsync(modelId, ct);

        public void Dispose() => inner.Dispose();

        private async Task ParkAsync(ProcessRecord record)
        {
            if (record.RestartCount != 1)
            {
                return;
            }

            Committed.TrySetResult();
            await Release.Task;
        }
    }

    private sealed class FailingReadyWriteStore(IProcessRecordStore inner) : IProcessRecordStore
    {
        public int Refused { get; private set; }

        public Task<IReadOnlyList<ProcessRecord>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);

        public Task<ProcessRecord?> GetAsync(string modelId, CancellationToken ct = default) => inner.GetAsync(modelId, ct);

        public Task UpsertAsync(ProcessRecord record, CancellationToken ct = default)
        {
            if (record.ReadyAt is not null)
            {
                Refused++;
                throw new IOException("There is not enough space on the disk.");
            }

            return inner.UpsertAsync(record, ct);
        }

        public Task<bool> RemoveAsync(string modelId, CancellationToken ct = default) => inner.RemoveAsync(modelId, ct);

        public Task<IReadOnlyList<PendingProcessStart>> ListPendingAsync(CancellationToken ct = default) => inner.ListPendingAsync(ct);

        public Task AddPendingAsync(PendingProcessStart pending, CancellationToken ct = default) => inner.AddPendingAsync(pending, ct);

        public Task CommitPendingAsync(ProcessRecord record, CancellationToken ct = default) => inner.CommitPendingAsync(record, ct);

        public Task<bool> RemovePendingAsync(string modelId, CancellationToken ct = default) => inner.RemovePendingAsync(modelId, ct);
    }

    private sealed class FailingCommitStore(IProcessRecordStore inner) : IProcessRecordStore
    {
        public Task<IReadOnlyList<ProcessRecord>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);

        public Task<ProcessRecord?> GetAsync(string modelId, CancellationToken ct = default) => inner.GetAsync(modelId, ct);

        public Task UpsertAsync(ProcessRecord record, CancellationToken ct = default) => inner.UpsertAsync(record, ct);

        public Task<bool> RemoveAsync(string modelId, CancellationToken ct = default) => inner.RemoveAsync(modelId, ct);

        public Task<IReadOnlyList<PendingProcessStart>> ListPendingAsync(CancellationToken ct = default) => inner.ListPendingAsync(ct);

        public Task AddPendingAsync(PendingProcessStart pending, CancellationToken ct = default) => inner.AddPendingAsync(pending, ct);

        public Task CommitPendingAsync(ProcessRecord record, CancellationToken ct = default) => throw new IOException("disk full");

        public Task<bool> RemovePendingAsync(string modelId, CancellationToken ct = default) => inner.RemovePendingAsync(modelId, ct);
    }

    private sealed class SingleExecutableCatalog(ResolvedExecutable executable) : IExecutableCatalog
    {
        public IReadOnlyList<ExecutableVersionState> List() =>
            [new ExecutableVersionState(executable.Name, executable.Version, null, Verified: true, IsDefault: true, Problem: null)];

        public ResolvedExecutable Resolve(string name, string? version) =>
            name == executable.Name && (version is null || version == executable.Version)
                ? executable
                : throw new AgentException(422, new AgentError { Code = ErrorCodes.ExecutableMissing, Message = "missing" });

        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class MinimalHost : IProcessHost
    {
        public Task<IHostedProcess> StartAsync(ProcessHostStart start, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IHostedProcess?> OpenAsync(ProcessIdentity expected, string jobName, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcessIdentity>> ListCandidatesAsync(string imageFolder, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> KillAsync(ProcessIdentity identity, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
