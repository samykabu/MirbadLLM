using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Benchmarking;
using FakeModelRegistry = MirbadLLM.UnitTests.Orchestration.Fakes.FakeModelRegistry;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Benchmarking;

/// <summary>
/// The llama.cpp benchmark (design-p3p4 §A.6, MN-225) against a fake <c>llama-server</c>: chat levels 1/2/4/8 capped at the
/// slots, <c>ignore_eos</c>, the server-side counters from <c>llamacpp:*</c> (tokens per second from the counter deltas, the
/// server's own prefill/decode speeds) next to the client numbers, the outside-client noise from those counters, the
/// embeddings suite with counters; vLLM targets measured exactly as before (no <c>/metrics</c> read, no new fields); the
/// target of a llama.cpp model; and the provenance's llama.cpp version with its comparison label.
/// </summary>
public sealed class LlamaCppBenchmarkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BenchmarkTarget Llama(int parallel = 4, PoolingMode pooling = PoolingMode.None) =>
        new("qwen3-8b-win", "qwen3-8b", new Uri("http://127.0.0.1:8101/"), 8192, pooling, parallel) { Runtime = RuntimeKind.LlamaCpp };

    private static readonly BenchmarkTarget Vllm = new("qwen", "Qwen", new Uri("http://ss-qwen:8000/"), 8192, MaxNumSeqs: 16);

    private static BenchmarkSuiteOptions Options() => new()
    {
        Levels = [1, 2],
        Runs = 2,
        MaxTokens = 8,
        IncludePrefill = false,
        EmbeddingLevels = [1, 4],
        EmbeddingInputWords = [4, 32],
    };

    /// <summary>
    /// A llama-server stand-in: streams <c>max_tokens</c> tokens (usage in the last chunk), answers tools, questions and
    /// embeddings, and keeps the <c>llamacpp:*</c> counters a real server would (prompt tokens 1 per 4 characters, 20 ms per
    /// prompt, 10 ms per generated token). <see cref="OnMetrics"/> can add work of another client before a read is answered.
    /// </summary>
    private sealed class FakeLlamaServer : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private double _prompt;
        private double _promptSeconds;
        private double _predicted;
        private double _predictedSeconds;

        public List<string> Paths { get; } = [];

        public List<string> Bodies { get; } = [];

        public int MetricsReads { get; private set; }

        /// <summary>Called with the read number (1-based) before a <c>/metrics</c> answer; returns requests processing.</summary>
        public Func<int, FakeLlamaServer, int> OnMetrics { get; set; } = (_, _) => 0;

        public bool MetricsEnabled { get; set; } = true;

        public void AddWork(double prompt, double predicted)
        {
            lock (_gate)
            {
                _prompt += prompt;
                _promptSeconds += prompt / 1000;
                _predicted += predicted;
                _predictedSeconds += predicted * 0.01;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_gate)
            {
                Paths.Add(path);
                Bodies.Add(body);
            }

            if (path == "/metrics")
            {
                return Metrics();
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (path == "/v1/embeddings")
            {
                var inputs = root.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToList();
                int tokens = inputs.Sum(i => i.Length / 4);
                AddWork(tokens, 0);
                var data = inputs.Select((_, i) => new { index = i, embedding = new[] { 0.6, 0.8, 0 } }).ToList();
                return Json(JsonSerializer.Serialize(new { data, usage = new { prompt_tokens = tokens } }));
            }

            int promptTokens = root.GetProperty("messages").EnumerateArray().Sum(m => (m.GetProperty("content").GetString() ?? "").Length / 4);
            if (root.TryGetProperty("stream", out var stream) && stream.GetBoolean())
            {
                int max = root.GetProperty("max_tokens").GetInt32();
                AddWork(promptTokens, max);
                var sse = new StringBuilder();
                for (int i = 0; i < max; i++)
                {
                    sse.Append("data: {\"choices\":[{\"delta\":{\"content\":\"t\"}}]}\n\n");
                }

                sse.Append(CultureInfo.InvariantCulture, $"data: {{\"choices\":[],\"usage\":{{\"prompt_tokens\":{promptTokens},\"completion_tokens\":{max}}}}}\n\n");
                sse.Append("data: [DONE]\n\n");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream") };
            }

            AddWork(promptTokens, 4);
            if (root.TryGetProperty("tools", out _))
            {
                return Json("""{"choices":[{"message":{"content":null,"tool_calls":[{"function":{"name":"get_time","arguments":"{\"city\":\"Riyadh\"}"}}]}}]}""");
            }

            string question = root.GetProperty("messages")[0].GetProperty("content").GetString()!;
            var match = BenchmarkPrompts.Questions.FirstOrDefault(q => q.Question == question);
            return Json(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = match?.Expected ?? "ok" } } } }));
        }

        private HttpResponseMessage Metrics()
        {
            int read;
            lock (_gate)
            {
                read = ++MetricsReads;
            }

            int processing = OnMetrics(read, this);
            if (!MetricsEnabled)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            string text;
            lock (_gate)
            {
                text = string.Create(CultureInfo.InvariantCulture, $"""
                    # HELP llamacpp:prompt_tokens_total Number of prompt tokens processed.
                    # TYPE llamacpp:prompt_tokens_total counter
                    {LlamaCppMetrics.PromptTokensTotal} {_prompt}
                    {LlamaCppMetrics.PromptSecondsTotal} {_promptSeconds}
                    {LlamaCppMetrics.TokensPredictedTotal} {_predicted}
                    {LlamaCppMetrics.TokensPredictedSecondsTotal} {_predictedSeconds}
                    {LlamaCppMetrics.RequestsProcessing} {processing}
                    {LlamaCppMetrics.RequestsDeferred} 0

                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    // ------------------------------------------------------------ levels and request shape

    [Theory]
    [InlineData(1, new[] { 1 })]
    [InlineData(2, new[] { 1, 2 })]
    [InlineData(3, new[] { 1, 2, 3 })]
    [InlineData(4, new[] { 1, 2, 4 })]
    [InlineData(8, new[] { 1, 2, 4, 8 })]
    [InlineData(16, new[] { 1, 2, 4, 8 })]
    public void ChatLevels_Are1248_CappedAtTheSlots(int parallel, int[] expected)
    {
        var suite = new BenchmarkSuite(new HttpClient(new FakeLlamaServer()), null, Options());
        Assert.Equal(expected, suite.ChatLevels(Llama(parallel)));
        Assert.Equal([1, 2], suite.ChatLevels(Vllm)); // vLLM: the configured levels, whatever max num seqs is
        Assert.Equal([1, 2], suite.ChatLevels(Vllm with { MaxNumSeqs = 1 }));
    }

    [Fact]
    public async Task ChatSuite_RunsTheCappedLevels_WithIgnoreEos_AndReportsTheServersCountersNextToTheClients()
    {
        var server = new FakeLlamaServer();
        var suite = new BenchmarkSuite(new HttpClient(server), null, Options());
        var progress = new List<BenchmarkProgress>();

        var result = await suite.RunAsync(Llama(parallel: 3), new SyncProgress(progress.Add), Ct);

        Assert.Equal([1, 2, 3], result.Levels.Select(l => l.Concurrency));
        Assert.Equal(3 + (3 * 2), progress[^1].TotalSteps);
        Assert.Equal(progress.Count, progress[^1].Step);

        var streamed = server.Bodies.Where(b => b.Contains("\"stream\":true", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(streamed);
        Assert.All(streamed, b => Assert.Contains("\"ignore_eos\":true", b, StringComparison.Ordinal));

        // Two reads around each timed run: 3 levels × 2 runs.
        Assert.Equal(3 * 2 * 2, server.MetricsReads);
        foreach (var run in result.Levels.SelectMany(l => l.Runs))
        {
            var c = Assert.IsType<ServerCounters>(run.Server);
            Assert.Equal(run.CompletionTokens, c.PredictedTokens); // the server generated exactly what the client received
            Assert.Equal(c.PredictedTokens / run.WallSeconds, c.PredictedTokS, 6);
            Assert.True(c.PromptTokens > 0);
            Assert.Equal(1000, c.PrefillTokS!.Value, 6); // the fake's 1 ms per prompt token
            Assert.Equal(100, c.DecodeTokS!.Value, 6); // and 10 ms per generated token
            Assert.False(c.OutsideClients);
            Assert.Equal(0, c.ExcessTokens);
        }

        var level = result.Levels[2];
        Assert.Equal(BenchmarkSuite.Median(level.Runs.Select(r => r.Server!.PredictedTokS)), level.Server!.PredictedTokS);
        Assert.Equal(100, level.Server.DecodeTokS!.Value, 6);
        Assert.False(result.Noise.Noisy);
        Assert.All(result.Levels, l => Assert.False(l.Noisy));
    }

    [Fact]
    public async Task AnotherClientsTokens_DuringARun_MakeItNoisy_FromTheServersCounters()
    {
        var server = new FakeLlamaServer
        {
            // Before the 4th read (the end of the second timed run) another client generated 500 tokens.
            OnMetrics = (read, s) =>
            {
                if (read == 4)
                {
                    s.AddWork(0, 500);
                }

                return 0;
            },
        };
        var suite = new BenchmarkSuite(new HttpClient(server), null, Options());

        var result = await suite.RunAsync(Llama(parallel: 2), null, Ct);

        var runs = result.Levels.SelectMany(l => l.Runs).ToList();
        Assert.False(runs[0].Server!.OutsideClients);
        Assert.True(runs[1].Server!.OutsideClients);
        Assert.Equal(500, runs[1].Server!.ExcessTokens);
        Assert.True(result.Levels[0].Noisy);
        Assert.False(result.Levels[1].Noisy);
        Assert.True(result.Noise.Noisy);
        Assert.Contains(result.Noise.Reasons, r => r.StartsWith("noisy: llama-server's own counters show other clients on this model in 1 run(s)", StringComparison.Ordinal) &&
            r.Contains("up to 500 tokens the benchmark did not send", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RequestsAlreadyProcessing_WhenARunStarts_MakeItNoisy()
    {
        // The first read (before the first timed run) sees one request in flight that is not the benchmark's.
        var server = new FakeLlamaServer { OnMetrics = (read, _) => read == 1 ? 1 : 0 };
        var suite = new BenchmarkSuite(new HttpClient(server), null, Options());

        var result = await suite.RunAsync(Llama(parallel: 1), null, Ct);

        var first = result.Levels[0].Runs[0].Server!;
        Assert.Equal(1, first.ProcessingAtStart);
        Assert.True(first.OutsideClients);
        Assert.True(result.Noise.Noisy);
        Assert.Contains("up to 1 request(s) already processing", string.Join(" ", result.Noise.Reasons), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutMetrics_TheRunIsMeasuredAsBefore_WithoutServerCounters()
    {
        var server = new FakeLlamaServer { MetricsEnabled = false };
        var suite = new BenchmarkSuite(new HttpClient(server), null, Options());

        var result = await suite.RunAsync(Llama(parallel: 2), null, Ct);

        Assert.Equal([1, 2], result.Levels.Select(l => l.Concurrency));
        Assert.All(result.Levels.SelectMany(l => l.Runs), r => Assert.Null(r.Server));
        Assert.All(result.Levels, l => Assert.Null(l.Server));
        Assert.False(result.Noise.Noisy);
    }

    [Fact]
    public async Task VllmTargets_AreMeasuredExactlyAsBefore_NoMetricsRead_NoNewFields()
    {
        var server = new FakeLlamaServer();
        var suite = new BenchmarkSuite(new HttpClient(server), null, Options());

        var result = await suite.RunAsync(Vllm, null, Ct);

        Assert.DoesNotContain("/metrics", server.Paths);
        Assert.Equal(0, server.MetricsReads);
        Assert.All(server.Bodies.Where(b => b.Length > 0), b => Assert.DoesNotContain("ignore_eos", b, StringComparison.Ordinal));
        Assert.All(result.Levels.SelectMany(l => l.Runs), r => Assert.Null(r.Server));
        string json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("\"Server\"", json, StringComparison.Ordinal);
        Assert.Equal(3 + (2 * 2), suite.StepCount);
    }

    // ------------------------------------------------------------ embeddings

    [Fact]
    public async Task EmbeddingsSuite_KeepsItsLevels_AndReadsTheCounters()
    {
        var server = new FakeLlamaServer();
        var suite = new BenchmarkSuite(new HttpClient(server), null, Options());

        var result = await suite.RunAsync(Llama(parallel: 8, pooling: PoolingMode.Embedding), null, Ct);

        var embeddings = Assert.IsType<EmbeddingResult>(result.Embeddings);
        Assert.Equal([1, 4, 8], embeddings.Levels.Select(l => l.Concurrency)); // the existing levels + the slots (capped)
        Assert.True(embeddings.Check.Valid);
        Assert.All(server.Bodies.Where(b => b.Contains("\"input\"", StringComparison.Ordinal)), b => Assert.DoesNotContain("ignore_eos", b, StringComparison.Ordinal));
        foreach (var run in embeddings.Levels.SelectMany(l => l.Runs))
        {
            var c = Assert.IsType<ServerCounters>(run.Server);
            Assert.Equal(run.InputTokens, c.PromptTokens);
            Assert.Equal(0, c.PredictedTokens);
            Assert.Equal(1000, c.PrefillTokS!.Value, 6);
            Assert.Null(c.DecodeTokS);
            Assert.False(c.OutsideClients);
        }

        Assert.All(embeddings.Levels, l => Assert.NotNull(l.Server));
        Assert.False(result.Noise.Noisy);
    }

    [Fact]
    public async Task EmbeddingsSuite_FlagsGeneratedTokens_AsAnotherClient()
    {
        var server = new FakeLlamaServer { OnMetrics = (read, s) => { if (read == 2) { s.AddWork(10, 64); } return 0; } };
        var suite = new BenchmarkSuite(new HttpClient(server), null, Options());

        var result = await suite.RunAsync(Llama(parallel: 1, pooling: PoolingMode.Embedding), null, Ct);

        var first = result.Embeddings!.Levels[0];
        Assert.True(first.Runs[0].Server!.OutsideClients);
        Assert.True(first.Noisy);
        Assert.True(result.Noise.Noisy);
    }

    // ------------------------------------------------------------ the counters

    [Fact]
    public void Counters_ADeltaGoingBackwards_IsNoMeasurement_AndUnknownClientTokensAreNotJudged()
    {
        var before = new ServerSnapshot(1000, 1, 500, 5, 0);
        Assert.Null(ServerCounters.Between(before, before with { PredictedTokens = 10 }, 1, null, null, 1)); // restarted
        Assert.Null(ServerCounters.Between(null, before, 1, null, null, 1));
        Assert.Null(ServerCounters.Between(before, null, 1, null, null, 1));

        // The client did not report its tokens (no usage block): 400 generated tokens are not called someone else's ...
        var unknown = ServerCounters.Between(before, before with { PredictedTokens = 900, PredictedSeconds = 9 }, 2, null, null, 4)!;
        Assert.False(unknown.OutsideClients);
        // ... but when the benchmark knows it generated none (embeddings), they are.
        Assert.True(ServerCounters.Between(before, before with { PredictedTokens = 900, PredictedSeconds = 9 }, 2, null, 0, 4)!.OutsideClients);
        Assert.Equal(200, unknown.PredictedTokS);
        Assert.Equal(100, unknown.DecodeTokS);

        // Within the tolerance (2 per request, or 2 %): not noise; above it: noise.
        var after = before with { PromptTokens = 1100, PromptSeconds = 1.1, PredictedTokens = 900 + 8, PredictedSeconds = 9 };
        Assert.False(ServerCounters.Between(before, after, 2, 100, 400, 4)!.OutsideClients);
        Assert.True(ServerCounters.Between(before, after with { PredictedTokens = 900 + 9 }, 2, 100, 400, 4)!.OutsideClients);
        Assert.True(ServerCounters.Between(before, after with { PromptTokens = 1300 }, 2, 100, 400, 4)!.OutsideClients);
        Assert.Equal(1000, ServerCounters.Between(before, after, 2, 100, 400, 4)!.PrefillTokS!.Value, 6);
    }

    [Fact]
    public void Snapshot_NeedsTheLlamaCppFamilies()
    {
        Assert.Null(ServerSnapshot.Parse("vllm:generation_tokens_total 5\n"));
        var s = ServerSnapshot.Parse($"{LlamaCppMetrics.PromptTokensTotal} 10\n{LlamaCppMetrics.TokensPredictedTotal} 20\n{LlamaCppMetrics.RequestsProcessing} 2\n")!;
        Assert.Equal(new ServerSnapshot(10, 0, 20, 0, 2), s);
    }

    // ------------------------------------------------------------ the target

    [Fact]
    public void TargetFor_ALlamaCppModel_UsesItsSlotsAndPerSlotContext_AndVllmIsUnchanged()
    {
        var uri = new Uri("http://127.0.0.1:8101/");
        var llama = BenchmarkSuite.TargetFor("e", LlamaCppTuningTests.Embedder, uri, TuningGoal.Embeddings, 3);
        Assert.Equal(new BenchmarkTarget("e", "qwen3-embedding-4b", uri, 8192, PoolingMode.Embedding, 1, TuningGoal.Embeddings, 3) { Runtime = RuntimeKind.LlamaCpp }, llama);

        var chat = BenchmarkSuite.TargetFor("c", LlamaCppTuningTests.Chat, uri, null, null);
        Assert.Equal((8192, 4, PoolingMode.None, RuntimeKind.LlamaCpp), (chat.MaxModelLen, chat.MaxNumSeqs, chat.Pooling, chat.Runtime));

        var qwen = new ModelDefinition { Id = "qwen", ServedName = "Qwen", ModelPath = "Qwen", MaxModelLen = 262_144, MaxNumSeqs = 16 };
        Assert.Equal(new BenchmarkTarget("qwen", "Qwen", uri, 262_144, PoolingMode.None, 16, TuningGoal.Throughput, 2),
            BenchmarkSuite.TargetFor("qwen", qwen, uri, TuningGoal.Throughput, 2));
    }

    // ------------------------------------------------------------ provenance

    private static NodeLiveStatus WindowsNode(params RuntimeVersion[] versions) => new()
    {
        NodeId = "win-1",
        NodeUid = "uid-win",
        State = NodeState.Online,
        Capabilities = new NodeCapabilities
        {
            Protocol = new ProtocolRange(1, 1),
            AgentVersion = "1.0.0",
            Os = new NodeOsInfo("windows", "Windows 11"),
            Arch = "amd64",
            Memory = new NodeMemoryInfo(MemoryModel.Discrete, 64),
            Gpus = [new GpuCapability { Index = 0, Uuid = "GPU-4090", Name = "NVIDIA GeForce RTX 4090" }],
            Runtimes = [new RuntimeCapability { Kind = RuntimeKind.LlamaCpp, Delivery = LaunchDelivery.Process, Available = true, Versions = versions }],
        },
    };

    [Fact]
    public async Task Provenance_RecordsTheLlamaCppVersion_PinnedElseTheNodesVerifiedDefault()
    {
        var pinned = LlamaCppTuningTests.Chat; // pins b11476
        var unpinned = pinned with { Id = "unpinned", LlamaCpp = pinned.LlamaCpp! with { ExecutableVersion = null } };
        var container = pinned with { Id = "container", LlamaCpp = pinned.LlamaCpp! with { Delivery = LaunchDelivery.Container, ExecutableVersion = null } };
        var registry = new FakeModelRegistry().Add(pinned).Add(unpinned).Add(container);
        var node = WindowsNode(new RuntimeVersion("b11400", "12.4", Verified: true), new RuntimeVersion("b11502", "12.4", Verified: true, Default: true));
        var source = new NodeBenchmarkProvenanceSource(new StatusCache(node), registry);
        var endpoint = new BenchmarkEndpoint(new Uri("https://win-1/"), BenchmarkDataPaths.AgentLan, "win-1");

        var described = (await source.DescribeAsync(pinned.Id, endpoint, Ct)).Provenance;
        Assert.Equal("b11476", described.ExecutableVersion);
        Assert.Equal(RuntimeKind.LlamaCpp, described.Runtime);
        Assert.Equal(LaunchDelivery.Process, described.Delivery);
        Assert.Equal("uid-win", described.NodeUid);
        Assert.Equal(["GPU-4090"], described.Gpus.Select(g => g.Uuid));

        Assert.Equal("b11502", (await source.DescribeAsync(unpinned.Id, endpoint, Ct)).Provenance.ExecutableVersion);
        Assert.Equal("b11502", source.Expected(unpinned).ExecutableVersion);
        Assert.Equal("b11476", source.Expected(pinned).ExecutableVersion);
        Assert.Null(source.Expected(container).ExecutableVersion);

        // An unverified default is not what a launch would use: none recorded.
        var unverified = new NodeBenchmarkProvenanceSource(new StatusCache(WindowsNode(new RuntimeVersion("b11502", null, Verified: false, Default: true))), registry);
        Assert.Null(unverified.Expected(unpinned).ExecutableVersion);

        // vLLM records none, and the field stays out of the JSON.
        var qwen = new ModelDefinition { Id = "qwen", ServedName = "Qwen", ModelPath = "Qwen", Placement = new ModelPlacement("win-1") };
        Assert.Null(source.Expected(qwen).ExecutableVersion);
        Assert.DoesNotContain("ExecutableVersion", JsonSerializer.Serialize(source.Expected(qwen)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AVersionChange_StaysComparable_AndIsLabelled()
    {
        var a = new BenchmarkProvenance { NodeId = "win-1", NodeUid = "uid-win", Runtime = RuntimeKind.LlamaCpp, Delivery = LaunchDelivery.Process, ExecutableVersion = "b11476" };
        var b = a with { ExecutableVersion = "b11502" };

        Assert.True(BenchmarkComparisons.Comparable(a, b, "dgx"));
        Assert.Null(BenchmarkComparisons.Label(a, b, "dgx"));
        Assert.Equal("llama.cpp b11476 → b11502", BenchmarkComparisons.VersionLabel(a, b, "dgx"));
        Assert.Null(BenchmarkComparisons.VersionLabel(a, a, "dgx"));
        Assert.Null(BenchmarkComparisons.VersionLabel(a, a with { ExecutableVersion = null }, "dgx"));

        // Other hardware is labelled as such, not as a version change.
        var elsewhere = b with { NodeUid = "uid-other" };
        Assert.Equal(BenchmarkComparisons.DifferentHardware, BenchmarkComparisons.Label(a, elsewhere, "dgx"));
        Assert.Null(BenchmarkComparisons.VersionLabel(a, elsewhere, "dgx"));
    }

    private sealed class StatusCache(NodeLiveStatus node) : INodeStatusCache
    {
        public long Version => 0;

        public IReadOnlyList<NodeLiveStatus> ListNodes() => [node];

        public NodeLiveStatus? GetNode(string nodeId) => nodeId == node.NodeId ? node : null;

        public NodeModelRuntime? GetRuntime(string modelId) => null;

        public IReadOnlyList<NodeModelRuntime> ListRuntimes(string? nodeId = null) => [];

        public ServedModelRoute? ResolveServedName(string servedName) => null;

        public Task RefreshModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RefreshNodeAsync(string nodeId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class SyncProgress(Action<BenchmarkProgress> report) : IProgress<BenchmarkProgress>
    {
        public void Report(BenchmarkProgress value) => report(value);
    }
}
