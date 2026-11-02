using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Admission;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Events;
using MirbadLLM.Agent.Fencing;
using MirbadLLM.Agent.Operations;
using MirbadLLM.Agent.Proxy;
using MirbadLLM.Agent.Runtimes;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Runtimes.Process;
using MirbadLLM.UnitTests.Agent.Fakes;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707, xUnit1051 // underscore test names; hand-written fakes ignore the token.

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// MN-222: the per-instance API key (design-p3p4 §A.8). DPAPI protection at rest (<see cref="InstanceKeyProtector"/>,
/// machine scope + the account SID as entropy, Windows only); the agent proxy and the metrics scrape send it upstream to a
/// native llama-server (which answers 401 on <c>/v1/*</c> and <c>/metrics</c> without it); container upstreams get no
/// header; and the key is never in argv, the logs, <c>/launch-config</c>, <c>processes.json</c> or the operation log. The
/// end-to-end tests run the agent's launch path (<see cref="RuntimeOperations"/>, reservations, the process driver over the
/// in-memory process host running the fake llama-server, the real executable catalogue).
/// </summary>
public sealed class InstanceKeyTests
{
    private const string WindowsOnly = "DPAPI (crypt32 CryptProtectData): runs on Windows only.";

    /// <summary>True on Windows (the <c>SkipUnless</c> condition).</summary>
    public static bool IsWindows => OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------ DPAPI

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(IsWindows))]
    [SupportedOSPlatform("windows")]
    public void Dpapi_RoundTrips_NeverContainsTheKey_AndIsSaltedPerCall()
    {
        var protector = new InstanceKeyProtector();
        string key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        string first = protector.Protect(key);
        string second = protector.Protect(key);

        Assert.Equal(key, protector.Unprotect(first));
        Assert.Equal(key, protector.Unprotect(second));
        Assert.NotEqual(first, second);
        Assert.DoesNotContain(key, first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(key)), first, StringComparison.Ordinal);
        Assert.DoesNotContain(key, Encoding.UTF8.GetString(Convert.FromBase64String(first)), StringComparison.Ordinal);

        // The default account is this process's token user: the same SID given explicitly reads the blob.
        using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(key, new InstanceKeyProtector(identity.User!).Unprotect(first));
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(IsWindows))]
    [SupportedOSPlatform("windows")]
    public void Dpapi_AnotherAccountsEntropy_ATamperedBlob_OrGarbage_CannotBeUnprotected()
    {
        var protector = new InstanceKeyProtector(new SecurityIdentifier("S-1-5-80-1111111111-2222222222-3333333333-4444444444-555555555"));
        string blob = protector.Protect("k");

        var other = new InstanceKeyProtector(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null));
        Assert.Throws<CryptographicException>(() => other.Unprotect(blob));

        byte[] bytes = Convert.FromBase64String(blob);
        bytes[^5] ^= 0x5A;
        Assert.Throws<CryptographicException>(() => protector.Unprotect(Convert.ToBase64String(bytes)));
        Assert.Throws<CryptographicException>(() => protector.Unprotect("not base64!"));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(""));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(FakeInstanceKeyProtector.Marker + "AAAA"));
        Assert.Equal("k", protector.Unprotect(blob));
    }

    // ------------------------------------------------------------ end to end: key upstream, never anywhere else

    [Fact]
    public async Task LaunchedInstance_ProxyAndScrapeSendItsKey_AndTheKeyIsInNoArgvLogLaunchConfigRecordOrOperationLog()
    {
        await using var node = new LlamaCppTestNode();
        await node.Catalog.RefreshAsync(Ct);

        var accepted = await node.LaunchViaOperationsAsync("op-key-1");
        Assert.Equal(202, accepted.StatusCode);
        await node.WaitReadyAsync();
        await node.Runtimes.WhenIdleAsync();

        string? key = await node.Driver.GetUpstreamApiKeyAsync("m1", Ct);
        Assert.NotNull(key);
        Assert.Matches("^[0-9a-f]{64}$", key);

        // The child got it in its environment only.
        var start = Assert.Single(node.Host.Starts);
        Assert.Equal(key, start.Environment[ProcessEnvironment.LlamaApiKey]);
        Assert.DoesNotContain(start.Arguments, a => a.Contains(key, StringComparison.Ordinal));
        Assert.DoesNotContain(key, start.ExecutablePath, StringComparison.Ordinal);

        // Proxy: the client's Authorization is replaced by the instance key (llama-server would answer 401 otherwise).
        var proxied = await node.ProxyAsync("v1/models", [node.Driver], clientAuthorization: "Bearer client-api-key");
        Assert.Equal(StatusCodes.Status200OK, proxied.Status);
        Assert.Contains(LlamaCppTestNode.Alias, proxied.Body, StringComparison.Ordinal);

        // Scrape: /metrics with the key (MetricFamilyFilter decides what is kept; MN-223).
        var sample = await node.SampleAsync([node.Driver]);
        var scrape = Assert.Single(sample.Models);
        Assert.Null(scrape.Error);

        // Nowhere else: launch-config, processes.json, the operation log, the status, every log line.
        var config = await node.Driver.GetLaunchConfigAsync("m1", Ct);
        Assert.DoesNotContain(key, JsonSerializer.Serialize(config, AgentJson.Options), StringComparison.Ordinal);
        Assert.DoesNotContain(key, JsonSerializer.Serialize(await node.Driver.GetAsync("m1", Ct), AgentJson.Options), StringComparison.Ordinal);
        Assert.DoesNotContain(key, File.ReadAllText(node.Options.RecordsPath), StringComparison.Ordinal);
        var record = Assert.Single(ProcessRecordStore.Load(node.Options.RecordsPath).Processes);
        Assert.Equal(key, node.Keys.Unprotect(record.ProtectedApiKey));
        string operationLog = node.ReadAgentStateFiles();
        Assert.Contains("op-key-1", operationLog, StringComparison.Ordinal);
        Assert.DoesNotContain(key, operationLog, StringComparison.Ordinal);
        Assert.NotEmpty(node.Logs.Lines);
        Assert.DoesNotContain(node.Logs.Lines, line => line.Contains(key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutTheKey_LlamaServerAnswers401_ThroughTheProxyAndTheScrape()
    {
        // What the key is for: the same instance reached through a driver view that answers no key (as a container driver).
        await using var node = new LlamaCppTestNode();
        await node.Catalog.RefreshAsync(Ct);
        await node.LaunchViaOperationsAsync("op-key-2");
        await node.WaitReadyAsync();
        var keyless = new KeylessDriver(node.Driver);

        var proxied = await node.ProxyAsync("v1/models", [keyless], resolverDriver: keyless);
        Assert.Equal(StatusCodes.Status401Unauthorized, proxied.Status);

        var sample = await node.SampleAsync([keyless]);
        Assert.Equal("/metrics answered HTTP 401.", Assert.Single(sample.Models).Error);
    }

    [Fact]
    public async Task ContainerUpstreams_GetNoAuthorizationHeader_FromTheProxyOrTheScrape()
    {
        await using var upstream = await HeaderRecordingUpstream.StartAsync(Ct);
        var container = new ContainerDriverStub(upstream.BaseUri);
        var options = Options.Create(new AgentProxyOptions());
        using var proxy = new AgentProxy(
            new RuntimeDriverUpstreamResolver([container], options, TimeProvider.System, new UpstreamChanges()), options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentProxy>.Instance, [container]);

        var context = LlamaCppTestNode.NewContext("v1/models", "Bearer client-api-key");
        await proxy.HandleAsync(context, "m1", "v1/models");
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        var ring = new TelemetryRing("boot-1", Options.Create(new TelemetryOptions()));
        var sampler = new TelemetrySampler(
            ring, new FakeMemoryProbe(64, 60), new NoGpus(), [container], new TelemetryHttpClient(),
            Options.Create(new TelemetryOptions()), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TelemetrySampler>.Instance);
        var sample = await sampler.SampleOnceAsync(Ct);
        Assert.Null(Assert.Single(sample!.Models).Error);

        Assert.Equal(["/v1/models", "/metrics"], upstream.Seen.Select(s => s.Path));
        Assert.All(upstream.Seen, s => Assert.False(s.Headers.ContainsKey("Authorization"), $"{s.Path} carried Authorization."));
    }

    [Fact]
    public async Task MlxInstances_AreNotScraped_ForMetricsTheyDoNotHave()
    {
        // mlx_lm.server answers /metrics with 404 (spike S4): the sampler leaves MLX models out instead of failing each pass.
        await using var upstream = await HeaderRecordingUpstream.StartAsync(Ct);
        var mlx = new ContainerDriverStub(upstream.BaseUri) { Runtime = RuntimeKind.Mlx, Delivery = LaunchDelivery.Process };
        var sampler = new TelemetrySampler(
            new TelemetryRing("boot-1", Options.Create(new TelemetryOptions())), new FakeMemoryProbe(8, 6), new NoGpus(), [mlx],
            new TelemetryHttpClient(), Options.Create(new TelemetryOptions()), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TelemetrySampler>.Instance);

        var sample = await sampler.SampleOnceAsync(Ct);

        Assert.Empty(sample!.Models);
        Assert.Empty(upstream.Seen);
    }

    [Fact]
    public async Task TheProxy_RewritesAnMlxRequestBody_BeforeTheServerSeesIt_AndRefusesAnAmbiguousOne()
    {
        await using var upstream = await HeaderRecordingUpstream.StartAsync(Ct);
        var mlx = new ContainerDriverStub(upstream.BaseUri)
        {
            Runtime = RuntimeKind.Mlx,
            Delivery = LaunchDelivery.Process,
            Rewrite = new UpstreamBodyRewrite(MlxFlags.DefaultModel, MlxFlags.RemovedRequestFields),
        };
        var options = Options.Create(new AgentProxyOptions());
        using var proxy = new AgentProxy(
            new RuntimeDriverUpstreamResolver([mlx], options, TimeProvider.System, new UpstreamChanges()), options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentProxy>.Instance, [mlx]);

        var context = PostContext("""{"model":"qwen3-0.6b-mlx","draft_model":"/Users/Shared","messages":[]}""");
        await proxy.HandleAsync(context, "m1", "v1/chat/completions");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var seen = Assert.Single(upstream.Seen);
        Assert.Equal("/v1/chat/completions", seen.Path);
        Assert.Equal("""{"model":"default_model","messages":[]}""", seen.Body);
        Assert.Equal(seen.Body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), seen.Headers["Content-Length"]);
        Assert.Equal("application/json", seen.Headers["Content-Type"]);

        var ambiguous = PostContext("""{"model":"a","model":"/Users/Shared/other"}""");
        await proxy.HandleAsync(ambiguous, "m1", "v1/chat/completions");
        Assert.Equal(StatusCodes.Status400BadRequest, ambiguous.Response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidRequest, ambiguous.Response.Headers[Protocol.AgentErrorHeader].ToString());
        Assert.Single(upstream.Seen); // nothing more reached the server

        static DefaultHttpContext PostContext(string json)
        {
            var context = LlamaCppTestNode.NewContext("v1/chat/completions", null);
            byte[] body = System.Text.Encoding.UTF8.GetBytes(json);
            context.Request.Method = "POST";
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = body.Length;
            context.Request.Body = new MemoryStream(body);
            return context;
        }
    }

    [Fact]
    public async Task AKeyLookupThatFails_Is503ModelNotRunning_BeforeAnyUpstreamRequest()
    {
        await using var upstream = await HeaderRecordingUpstream.StartAsync(Ct);
        var failing = new ContainerDriverStub(upstream.BaseUri) { KeyFailure = new CryptographicException("cannot unprotect") };
        var options = Options.Create(new AgentProxyOptions());
        using var proxy = new AgentProxy(
            new RuntimeDriverUpstreamResolver([failing], options, TimeProvider.System, new UpstreamChanges()), options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentProxy>.Instance, [failing]);

        var context = LlamaCppTestNode.NewContext("v1/models", null);
        await proxy.HandleAsync(context, "m1", "v1/models");

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal(ErrorCodes.ModelNotRunning, context.Response.Headers[Protocol.AgentErrorHeader].ToString());
        Assert.Empty(upstream.Seen);
    }

    // ------------------------------------------------------------ fakes

    /// <summary>Everything of the inner driver except the key (the interface default: null), like a container driver.</summary>
    private sealed class KeylessDriver(IRuntimeDriver inner) : IRuntimeDriver
    {
        public LaunchDelivery Delivery => inner.Delivery;

        public Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);

        public Task<RuntimeStatus?> GetAsync(string modelId, CancellationToken ct = default) => inner.GetAsync(modelId, ct);

        public Task<RuntimeLaunchResult> LaunchAsync(RuntimeLaunch launch, IRuntimeStageSink stages, CancellationToken ct = default) => inner.LaunchAsync(launch, stages, ct);

        public Task<RuntimeStatus> StartCreatedAsync(string modelId, CancellationToken ct = default) => inner.StartCreatedAsync(modelId, ct);

        public Task StopAsync(string modelId, IRuntimeStageSink stages, CancellationToken ct = default) => inner.StopAsync(modelId, stages, ct);

        public Task<LaunchConfig?> GetLaunchConfigAsync(string modelId, CancellationToken ct = default) => inner.GetLaunchConfigAsync(modelId, ct);

        public Task<Uri?> GetUpstreamAsync(string modelId, CancellationToken ct = default) => inner.GetUpstreamAsync(modelId, ct);

        public IAsyncEnumerable<string> StreamLogsAsync(string modelId, int tail, bool follow, CancellationToken ct = default) => inner.StreamLogsAsync(modelId, tail, follow, ct);

        public Task<bool> IsFolderInUseAsync(string folder, CancellationToken ct = default) => inner.IsFolderInUseAsync(folder, ct);
    }

    /// <summary>A container instance <c>m1</c>, Ready, at <paramref name="upstream"/>; no key (the interface default).</summary>
    private sealed class ContainerDriverStub(Uri upstream) : IRuntimeDriver
    {
        public Exception? KeyFailure { get; init; }

        public RuntimeKind Runtime { get; init; } = RuntimeKind.Vllm;

        public LaunchDelivery Delivery { get; init; } = LaunchDelivery.Container;

        public UpstreamBodyRewrite? Rewrite { get; init; }

        public Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RuntimeStatus>>([new RuntimeStatus
            {
                ModelId = "m1",
                InstanceId = "c-1",
                State = ModelState.Ready,
                Runtime = Runtime,
                Delivery = Delivery,
            }]);

        public Task<UpstreamBodyRewrite?> GetUpstreamBodyRewriteAsync(string modelId, CancellationToken ct = default) =>
            Task.FromResult(Rewrite);

        public Task<string?> GetUpstreamApiKeyAsync(string modelId, CancellationToken ct = default) =>
            KeyFailure is null ? Task.FromResult<string?>(null) : Task.FromException<string?>(KeyFailure);

        public Task<RuntimeStatus?> GetAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RuntimeLaunchResult> LaunchAsync(RuntimeLaunch launch, IRuntimeStageSink stages, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RuntimeStatus> StartCreatedAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task StopAsync(string modelId, IRuntimeStageSink stages, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LaunchConfig?> GetLaunchConfigAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<Uri?> GetUpstreamAsync(string modelId, CancellationToken ct = default) => Task.FromResult<Uri?>(upstream);

        public IAsyncEnumerable<string> StreamLogsAsync(string modelId, int tail, bool follow, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> IsFolderInUseAsync(string folder, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>A loopback server that records each request's path and headers and answers 200 (a container model).</summary>
    private sealed class HeaderRecordingUpstream : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private HeaderRecordingUpstream(WebApplication app) => _app = app;

        public Uri BaseUri { get; private set; } = null!;

        public ConcurrentQueue<(string Path, Dictionary<string, string> Headers, string Body)> Seen { get; } = new();

        public static async Task<HeaderRecordingUpstream> StartAsync(CancellationToken ct)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            var self = new HeaderRecordingUpstream(app);
            app.Run(async context =>
            {
                using var reader = new StreamReader(context.Request.Body);
                string body = await reader.ReadToEndAsync(context.RequestAborted);
                self.Seen.Enqueue((context.Request.Path.Value ?? "", context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase), body));
                await context.Response.WriteAsync("vllm:num_requests_running 0\n", context.RequestAborted);
            });
            await app.StartAsync(ct);
            var address = app.Urls.Select(u => new Uri(u)).First();
            self.BaseUri = new Uri($"http://127.0.0.1:{address.Port}/");
            return self;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class NoGpus : IGpuTelemetryProbe
    {
        public GpuTelemetryReading? Last { get; private set; }

        public Task<GpuTelemetryReading> ReadAsync(CancellationToken ct = default) =>
            Task.FromResult(Last = new GpuTelemetryReading(GpuProbeStatus.NotInstalled, []));
    }
}

/// <summary>
/// A Windows-shaped llama.cpp node for MN-222 tests: the real <see cref="ExecutableCatalog"/> over a temp version folder,
/// the process driver over <see cref="FakeProcessHost"/> (the fake llama-server in-process on a loopback port), and the
/// agent's launch path (<see cref="RuntimeOperations"/>, gate, operation log, fencing, reservations as the port allocator).
/// Every component logs into <see cref="Logs"/>. The key protector is DPAPI on Windows, the reversible fake elsewhere.
/// </summary>
internal sealed class LlamaCppTestNode : IAsyncDisposable
{
    public const string Alias = "qwen3-embedding-4b-win";
    private const string ModelFile = "Qwen3-Embedding-4B-GGUF/Qwen3-Embedding-4B-Q8_0.gguf";
    private const string Gpu = "GPU-7c1e9a52-3f60-4d2b-9a8e-5b0f2c6d1e44";
    private static readonly MasterTriple Master = new("master-a", 1, "boot-a");

    private readonly HttpClient _http = new(new SocketsHttpHandler());
    private readonly ILoggerFactory _loggers;
    private readonly FencingState _fencing;
    private readonly ReservationManager _reservations;
    private readonly string _agentState;
    private readonly int _port = TestPorts.Reserve();

    public LlamaCppTestNode()
    {
        Root = Path.Combine(Path.GetTempPath(), "mn222-node-" + Guid.NewGuid().ToString("N")[..12]);
        Version = LlamaCppVersionFolder.Create("b11476", Root);
        _loggers = LoggerFactory.Create(b => b.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        Catalog = new ExecutableCatalog([LlamaCppVersionFolder.Options(null, Version)], _loggers.CreateLogger<ExecutableCatalog>());
        string models = Path.Combine(Root, "models");
        Directory.CreateDirectory(Path.Combine(models, Path.GetDirectoryName(ModelFile)!));
        File.WriteAllBytes(Path.Combine(models, ModelFile), [0x47, 0x47, 0x55, 0x46]);
        Options = new ProcessRuntimeOptions
        {
            NodeId = "win-1",
            ModelsRoot = models,
            CacheRoot = Path.Combine(Root, "cache"),
            StateDirectory = Path.Combine(Root, "state"),
            HealthPollInterval = TimeSpan.FromMilliseconds(40),
            StartTimeout = TimeSpan.FromSeconds(15),
            ProbeTimeout = TimeSpan.FromSeconds(2),
            SystemRoot = OperatingSystem.IsWindows() ? null : "/fake/SystemRoot",
        };
        Keys = OperatingSystem.IsWindows() ? new InstanceKeyProtector() : new FakeInstanceKeyProtector();
        Host = new FakeProcessHost(Table);
        Store = new ProcessRecordStore(Options.RecordsPath);

        _agentState = Path.Combine(Root, "agent-state");
        Directory.CreateDirectory(_agentState);
        var time = TimeProvider.System;
        Log = new OperationLog(new OperationLogOptions { StateDirectory = _agentState, BootId = "boot-1" }, time, _loggers.CreateLogger<OperationLog>());
        _fencing = new FencingState(_agentState, time);
        var events = new EventBus("boot-1", time: time);
        ProcessRuntimeDriver? driver = null;
        _reservations = new ReservationManager(
            new AdmissionOptions { NodeId = "win-1", MemoryModel = MemoryModel.Discrete, PortRangeStart = _port, PortRangeEnd = _port },
            new FakeMemoryProbe(64, 60), new LateInstances(() => driver!), persistence: null, events, time, _loggers.CreateLogger<ReservationManager>());
        driver = new ProcessRuntimeDriver(
            Host, Store, Catalog, Keys, _reservations, _http, Microsoft.Extensions.Options.Options.Create(Options),
            _loggers.CreateLogger<ProcessRuntimeDriver>(), null, null);
        Driver = driver;
        var gate = new MutationGate(Log, _fencing, events, time);
        Runtimes = new RuntimeOperations(
            [driver], gate, _reservations, new RuntimeStateBoard(events), events, time,
            new AgentRuntimeOptions { WatchInterval = TimeSpan.FromHours(1) }, _loggers.CreateLogger<RuntimeOperations>());
    }

    public string Root { get; }

    public LlamaCppVersionFolder Version { get; }

    public CapturedLogLines Logs { get; } = new();

    public ExecutableCatalog Catalog { get; }

    public ProcessRuntimeOptions Options { get; }

    public IInstanceKeyProtector Keys { get; }

    public FakeProcessTable Table { get; } = new();

    public FakeProcessHost Host { get; }

    public ProcessRecordStore Store { get; }

    public OperationLog Log { get; }

    public ProcessRuntimeDriver Driver { get; }

    public RuntimeOperations Runtimes { get; }

    /// <summary>The spec MN-210's builder emits for the OpenViking embedding model (the P3 fixture shape).</summary>
    public static LaunchSpec Spec() => new()
    {
        Kind = LaunchDelivery.Process,
        Runtime = RuntimeKind.LlamaCpp,
        Executable = ExecutableNames.LlamaServer,
        ExecutableVersion = "b11476",
        Argv =
        [
            "-m", "{models}/" + ModelFile, "--alias", Alias, "-c", "16384", "-np", "2", "-ngl", "999", "-fa", "auto",
            "-b", "2048", "-ub", "2048", "--embedding", "--pooling", "last", "--split-mode", "none", "--device", "CUDA0",
            .. LlamaCppFlags.AlwaysArgs,
        ],
        Env = new Dictionary<string, string> { ["FAKE_LLAMA_LOAD_MS"] = "50" },
        Gpus = new GpuSelection { Mode = GpuSelectionMode.Uuids, Uuids = [Gpu] },
        Labels = new Dictionary<string, string>
        {
            [ContainerLabels.ServedName] = Alias,
            [ContainerLabels.ExecutionFingerprint] = "5e0c7a91d2b34f68",
            [ContainerLabels.BudgetGiB] = "8.9",
        },
    };

    /// <summary>The driver alone (no operation, no reservation).</summary>
    public Task<RuntimeLaunchResult> LaunchDirectAsync() => Driver.LaunchAsync(
        new RuntimeLaunch { ModelId = "m1", OperationId = "op-direct", Mode = LaunchMode.EnsureStarted, Spec = Spec(), HostPort = _port },
        new ContainerRuntimeDriverTests.RecordingStageSink());

    /// <summary><c>POST /runtimes/m1/launch</c> as the master sends it.</summary>
    public Task<MutationResult> LaunchViaOperationsAsync(string operationId)
    {
        var now = DateTimeOffset.UtcNow;
        var request = new LaunchRequest
        {
            OperationId = operationId,
            IssuedAt = now,
            ExpiresAt = now + OperationRetention.EnvelopeLifetime,
            Mode = LaunchMode.EnsureStarted,
            Spec = Spec(),
            Admission = new AdmissionRequest { BudgetGiB = 8.9, ReserveGiB = 2, Force = true },
        };
        return Runtimes.LaunchAsync("m1", request, JsonSerializer.SerializeToElement(request, AgentJson.Options), Master, TestContext.Current.CancellationToken);
    }

    public async Task<RuntimeStatus> WaitReadyAsync()
    {
        var watch = Stopwatch.StartNew();
        RuntimeStatus? status = null;
        while (watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            status = await Driver.GetAsync("m1");
            if (status?.State == ModelState.Ready)
            {
                return status;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"m1 never Ready: last {(status is null ? "none" : $"{status.State} ({status.Error})")}.");
    }

    /// <summary>One proxied GET through <see cref="AgentProxy"/> with the given key sources.</summary>
    public async Task<(int Status, string Body)> ProxyAsync(
        string path, IEnumerable<IRuntimeDriver> keySources, string? clientAuthorization = null, IRuntimeDriver? resolverDriver = null)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AgentProxyOptions());
        using var proxy = new AgentProxy(
            new RuntimeDriverUpstreamResolver([resolverDriver ?? Driver], options, TimeProvider.System, new UpstreamChanges()), options,
            _loggers.CreateLogger<AgentProxy>(), keySources);
        var context = NewContext(path, clientAuthorization);
        await proxy.HandleAsync(context, "m1", path);
        return (context.Response.StatusCode, Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }

    /// <summary>One sampler pass over <paramref name="drivers"/> (memory and GPUs faked).</summary>
    public async Task<NodeSample> SampleAsync(IEnumerable<IRuntimeDriver> drivers)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new TelemetryOptions { ScrapeTimeout = TimeSpan.FromSeconds(5) });
        using var http = new TelemetryHttpClient();
        var sampler = new TelemetrySampler(
            new TelemetryRing("boot-1", options), new FakeMemoryProbe(64, 60), new NoGpuProbe(), drivers, http, options,
            TimeProvider.System, _loggers.CreateLogger<TelemetrySampler>());
        return (await sampler.SampleOnceAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Everything the agent wrote into its own state directory (the operation log and its snapshots, fencing).</summary>
    public string ReadAgentStateFiles()
    {
        var text = new StringBuilder();
        foreach (string file in Directory.EnumerateFiles(_agentState, "*", SearchOption.AllDirectories))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text.Append(reader.ReadToEnd()).Append('\n');
        }

        return text.ToString();
    }

    public static DefaultHttpContext NewContext(string path, string? authorization)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/agent/v1/proxy/m1/" + path;
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        context.Response.Body = new MemoryStream();
        return context;
    }

    public async ValueTask DisposeAsync()
    {
        await Runtimes.WhenIdleAsync();
        Runtimes.Dispose();
        await Driver.DisposeAsync();
        await Table.RebootAsync();
        _reservations.Dispose();
        _fencing.Dispose();
        Log.Dispose();
        Store.Dispose();
        _http.Dispose();
        _loggers.Dispose();
        Version.Dispose();
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

    private sealed class LateInstances(Func<IRuntimeDriver> driver) : IRuntimeInstanceSource
    {
        public Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default) => driver().ListAsync(ct);
    }

    private sealed class NoGpuProbe : IGpuTelemetryProbe
    {
        public GpuTelemetryReading? Last { get; private set; }

        public Task<GpuTelemetryReading> ReadAsync(CancellationToken ct = default) =>
            Task.FromResult(Last = new GpuTelemetryReading(GpuProbeStatus.NotInstalled, []));
    }
}

/// <summary>Every formatted log line (message, state values and exception text) of every category.</summary>
internal sealed class CapturedLogLines : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturedLogLines owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = new StringBuilder().Append(category).Append(": ").Append(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var (name, value) in values)
                {
                    line.Append(" | ").Append(name).Append('=').Append(value);
                }
            }

            if (exception is not null)
            {
                line.Append(" | ").Append(exception);
            }

            owner._lines.Enqueue(line.ToString());
        }
    }
}
