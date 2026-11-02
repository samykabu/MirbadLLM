using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.UnitTests.Multinode;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707, xUnit1051 // underscore test names; hand-written fakes ignore cancellation.

namespace MirbadLLM.UnitTests.Orchestration;

/// <summary>
/// MN-223 (design-p3p4 §A.1.9, D129 (1)): the agent's filter forwards exactly <see cref="LlamaCppMetrics.Families"/>, the
/// master's <see cref="ModelKpiCalculator"/> maps two consecutive llama.cpp scrapes onto <see cref="ModelKpis"/> (missing
/// KPIs null, or NaN for the non-nullable ones, both shown as "—"), and a process instance's memory is its measured-at-start
/// figure, never the WDDM GPU process rows (0 MiB). The scrapes are written in llama-server <c>b11476</c>'s exposition
/// format (spike S1 confirmed every family name; it kept no raw scrape): <c># HELP</c>/<c># TYPE</c> lines, no labels,
/// counters with their <c>_total</c> name, plus the families mirbadllm does not read.
/// </summary>
public sealed class LlamaCppKpiTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>The llama.cpp families of the agent filter that are read; S1 also found these, which nothing reads.</summary>
    private static readonly string[] UnreadFamilies =
    [
        "llamacpp:n_decode_total", "llamacpp:n_tokens_max", "llamacpp:spec_decode_num_drafts_total",
        "llamacpp:prompt_tokens_seconds", "llamacpp:predicted_tokens_seconds", "llamacpp:n_busy_slots_per_decode",
    ];

    /// <summary>A b11476-format scrape: every S1 family, with the given values for the ones mirbadllm reads.</summary>
    private static string Scrape(
        double processing = 0,
        double deferred = 0,
        double promptTokens = 0,
        double predictedTokens = 0,
        double cachedTokens = 0,
        double accepted = 0,
        double draft = 0,
        params string[] omit)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [LlamaCppMetrics.RequestsProcessing] = processing,
            [LlamaCppMetrics.RequestsDeferred] = deferred,
            [LlamaCppMetrics.PromptTokensTotal] = promptTokens,
            [LlamaCppMetrics.PromptSecondsTotal] = promptTokens / 1000,
            [LlamaCppMetrics.TokensPredictedTotal] = predictedTokens,
            [LlamaCppMetrics.TokensPredictedSecondsTotal] = predictedTokens / 50,
            [LlamaCppMetrics.PromptTokensCachedTotal] = cachedTokens,
            [LlamaCppMetrics.SpecDecodeAcceptedTotal] = accepted,
            [LlamaCppMetrics.SpecDecodeDraftTotal] = draft,
            ["llamacpp:n_decode_total"] = 17,
            ["llamacpp:n_tokens_max"] = 8192,
            ["llamacpp:spec_decode_num_drafts_total"] = 0,
            ["llamacpp:prompt_tokens_seconds"] = 812.5,
            ["llamacpp:predicted_tokens_seconds"] = 48.25,
            ["llamacpp:n_busy_slots_per_decode"] = 1,
        };

        var text = new StringBuilder();
        foreach (var (family, value) in values)
        {
            if (omit.Contains(family))
            {
                continue;
            }

            bool counter = family.EndsWith("_total", StringComparison.Ordinal) || family == "llamacpp:n_tokens_max";
            text.Append(CultureInfo.InvariantCulture, $"# HELP {family} {family[LlamaCppMetrics.Prefix.Length..]} (b11476)\n");
            text.Append(CultureInfo.InvariantCulture, $"# TYPE {family} {(counter ? "counter" : "gauge")}\n");
            text.Append(CultureInfo.InvariantCulture, $"{family} {value}\n");
        }

        return text.ToString();
    }

    /// <summary>What the master computes from two scrapes as the agent forwards them (filtered), <paramref name="elapsedSeconds"/> apart.</summary>
    private static ModelKpis Kpis(string current, string? previous, double elapsedSeconds = 10) =>
        ModelKpiCalculator.Compute(
            PrometheusText.Parse(MetricFamilyFilter.Filter(current)),
            previous is null ? null : PrometheusText.Parse(MetricFamilyFilter.Filter(previous)),
            previous is null ? 0 : elapsedSeconds);

    // ---- The agent filter ----

    [Fact]
    public void Filter_ForwardsExactlyTheFrozenFamilies_WithTheirHelpAndTypeLines()
    {
        string filtered = MetricFamilyFilter.Filter(Scrape(processing: 2, promptTokens: 40));
        string[] lines = filtered.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var sampleNames = lines.Where(l => l[0] != '#').Select(l => l[..l.IndexOf(' ', StringComparison.Ordinal)]).ToList();
        Assert.Equal(LlamaCppMetrics.Families.Order(StringComparer.Ordinal), sampleNames.Order(StringComparer.Ordinal));
        Assert.Equal(LlamaCppMetrics.Families.Count * 3, lines.Length); // HELP, TYPE, sample for each
        Assert.All(UnreadFamilies, family => Assert.DoesNotContain(family, filtered, StringComparison.Ordinal));
        Assert.Contains("# TYPE llamacpp:prompt_tokens_total counter", lines);
        Assert.Contains("llamacpp:requests_processing 2", lines);
    }

    [Fact]
    public void Filter_KeepsNoLlamaCppNameOutsideTheFrozenList()
    {
        var llama = MetricFamilyFilter.Metrics.Where(n => n.StartsWith(LlamaCppMetrics.Prefix, StringComparison.Ordinal));
        Assert.Equal(LlamaCppMetrics.Families.Order(StringComparer.Ordinal), llama.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("llamacpp:kv_cache_usage_ratio", MetricFamilyFilter.Metrics); // removed from llama.cpp [R7]
    }

    [Fact]
    public void Calculator_ReadsOnlyFrozenLlamaCppNames()
    {
        var names = typeof(ModelKpiCalculator)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(n => n.StartsWith(LlamaCppMetrics.Prefix, StringComparison.Ordinal))
            .ToList();

        Assert.Equal(7, names.Count);
        Assert.All(names, n => Assert.Contains(n, LlamaCppMetrics.Families));
        Assert.All(names, n => Assert.True(MetricFamilyFilter.KeepsSample(n), n));
    }

    // ---- KPIs from two consecutive scrapes ----

    [Fact]
    public void TwoConsecutiveScrapes_GiveTheMappedKpis()
    {
        string previous = Scrape(processing: 1, deferred: 0, promptTokens: 1_000, predictedTokens: 400, cachedTokens: 200, accepted: 30, draft: 50);
        string current = Scrape(processing: 3, deferred: 2, promptTokens: 1_800, predictedTokens: 900, cachedTokens: 400, accepted: 60, draft: 90);

        var kpis = Kpis(current, previous, elapsedSeconds: 10);

        Assert.Equal(3, kpis.Load.RunningRequests);                 // requests_processing
        Assert.Equal(2, kpis.Load.WaitingRequests);                 // requests_deferred
        Assert.Equal(80, kpis.Load.PromptTokensPerSecond);          // (1800-1000)/10
        Assert.Equal(50, kpis.Load.GenerationTokensPerSecond);      // (900-400)/10
        Assert.Equal(0.25, kpis.PrefixCacheHitRate);                // Δcached 200 / Δprompt 800
        Assert.Equal(0.75, kpis.SpecDecodeAcceptanceRate);          // Δaccepted 30 / Δdraft 40
    }

    [Fact]
    public void ARatioAboveOne_IsCappedAtOneHundredPercent()
    {
        // MN-230 (D130): the two llama.cpp ratios assume the denominator counts the numerator too (cached prompt tokens
        // inside prompt_tokens_total). A build that counts only the tokens it really processed — 100 processed next to
        // 900 served from the cache — gave 9.0, which the Monitoring page printed as "900%". The assumption itself is
        // on the live checklist (W16).
        string previous = Scrape(promptTokens: 1_000, cachedTokens: 0, accepted: 0, draft: 0);
        string current = Scrape(promptTokens: 1_100, cachedTokens: 900, accepted: 90, draft: 10);

        var kpis = Kpis(current, previous, elapsedSeconds: 10);

        Assert.Equal(1, kpis.PrefixCacheHitRate);
        Assert.Equal(1, kpis.SpecDecodeAcceptanceRate);
    }

    [Fact]
    public void KpisWithoutALlamaCppSource_AreMissing()
    {
        var kpis = Kpis(Scrape(processing: 1, promptTokens: 900, predictedTokens: 90), Scrape(promptTokens: 100, predictedTokens: 10));

        Assert.True(double.IsNaN(kpis.Load.KvCacheUsage));          // no KV gauge in current llama.cpp: "—"
        Assert.True(double.IsNaN(kpis.RequestsPerMinute));          // counted by the router's stats instead
        Assert.True(double.IsNaN(kpis.PreemptionsPerMinute));
        Assert.True(double.IsNaN(kpis.ToolCallsPerMinute));
        Assert.Null(kpis.TimeToFirstToken);
        Assert.Null(kpis.InterTokenLatency);
        Assert.Null(kpis.EndToEndLatency);
        Assert.Null(kpis.QueueTime);
        Assert.Empty(kpis.FinishedByReasonPerMinute);
        Assert.Equal(0, kpis.PrefixCacheHitRate);                   // prompt traffic without cache hits: a real 0 %
    }

    [Fact]
    public void NoPromptCacheHits_IsZero_ButNoPromptTraffic_OrNoDraftTraffic_IsNull()
    {
        var hitsNone = Kpis(Scrape(promptTokens: 900, cachedTokens: 5), Scrape(promptTokens: 100, cachedTokens: 5));
        Assert.Equal(0, hitsNone.PrefixCacheHitRate);               // traffic, no cache hits: a real 0 %
        Assert.Null(hitsNone.SpecDecodeAcceptanceRate);              // no drafting in the window: "—", not 0 %

        var idle = Kpis(Scrape(promptTokens: 100, cachedTokens: 5), Scrape(promptTokens: 100, cachedTokens: 5));
        Assert.Null(idle.PrefixCacheHitRate);
        Assert.Equal(0, idle.Load.PromptTokensPerSecond);
        Assert.Equal(0, idle.Load.GenerationTokensPerSecond);
    }

    [Fact]
    public void AMissingFamily_IsNull_NotZero()
    {
        string[] noCache = [LlamaCppMetrics.PromptTokensCachedTotal, LlamaCppMetrics.SpecDecodeAcceptedTotal, LlamaCppMetrics.SpecDecodeDraftTotal];
        var kpis = Kpis(Scrape(promptTokens: 900, predictedTokens: 50, omit: noCache), Scrape(promptTokens: 100, predictedTokens: 10, omit: noCache));

        Assert.Null(kpis.PrefixCacheHitRate);
        Assert.Null(kpis.SpecDecodeAcceptanceRate);
        Assert.Equal(80, kpis.Load.PromptTokensPerSecond);
    }

    [Fact]
    public void TheFirstScrape_HasNoRatesOrRatios()
    {
        var kpis = Kpis(Scrape(processing: 2, deferred: 1, promptTokens: 900, predictedTokens: 300, cachedTokens: 100, accepted: 3, draft: 4), previous: null);

        Assert.Equal(2, kpis.Load.RunningRequests);
        Assert.Equal(1, kpis.Load.WaitingRequests);
        Assert.Equal(0, kpis.Load.PromptTokensPerSecond);
        Assert.Equal(0, kpis.Load.GenerationTokensPerSecond);
        Assert.Null(kpis.PrefixCacheHitRate);
        Assert.Null(kpis.SpecDecodeAcceptanceRate);
        Assert.True(double.IsNaN(kpis.Load.KvCacheUsage));
    }

    [Fact]
    public void ARestartedServer_ResetsItsCounters_WithoutANegativeRate()
    {
        var kpis = Kpis(Scrape(promptTokens: 50, predictedTokens: 20, cachedTokens: 10), Scrape(promptTokens: 5_000, predictedTokens: 2_000, cachedTokens: 900));

        Assert.Equal(5, kpis.Load.PromptTokensPerSecond);           // counted from zero again: 50/10
        Assert.Equal(2, kpis.Load.GenerationTokensPerSecond);
        Assert.Equal(0.2, kpis.PrefixCacheHitRate);                 // 10 / 50
    }

    [Fact]
    public void AVllmScrape_TakesTheVllmPath_EvenWithAStrayLlamaCppLine()
    {
        string vllm = "vllm:num_requests_running 4\nvllm:kv_cache_usage_perc 0.3\nllamacpp:requests_processing 9\n";
        var kpis = ModelKpiCalculator.Compute(PrometheusText.Parse(vllm), null, 0);

        Assert.Equal(4, kpis.Load.RunningRequests);
        Assert.Equal(0.3, kpis.Load.KvCacheUsage);
        Assert.Equal(0, kpis.RequestsPerMinute);
        Assert.False(ModelKpiCalculator.IsLlamaCpp(PrometheusText.Parse(vllm)));
        Assert.True(ModelKpiCalculator.IsLlamaCpp(PrometheusText.Parse(Scrape())));
        Assert.False(ModelKpiCalculator.IsLlamaCpp(PrometheusText.Parse("process_resident_memory_bytes 1\n")));
    }

    [Fact]
    public void TheMastersMetricsEndpoint_LeavesOutTheMissingKpis()
    {
        var kpis = Kpis(Scrape(processing: 1, promptTokens: 900), Scrape(promptTokens: 100));
        var model = LlamaModel("emb", budgetGiB: 9);
        string text = MetricsEndpointExtensions.Render([model], new Dictionary<string, ModelStatus>(), new Dictionary<string, ModelKpis> { ["emb"] = kpis }, null, null);

        Assert.Contains("mirbadllm_model_requests_running{model=\"emb\",served_name=\"Emb\"} 1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("mirbadllm_model_kv_cache_usage_ratio{", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", text, StringComparison.Ordinal);
    }

    // ---- Through the master's collector ----

    [Fact]
    public async Task TheCollector_ComputesLlamaCppKpis_FromTwoForwardedScrapes()
    {
        var time = new ManualTimeProvider();
        var env = new CollectorEnv(time, new FakeModelRegistry().Add(LlamaModel("chat", budgetGiB: 12)));
        env.Agents.Add("win");
        env.Nodes.Records["win"] = Node("win");

        env.Agents.Nodes["win"].Append(time.GetUtcNow(), models: [("chat", MetricFamilyFilter.Filter(Scrape(promptTokens: 100, predictedTokens: 40)))]);
        await env.Collector.CollectOnceAsync();
        time.Advance(TimeSpan.FromSeconds(5));
        env.Agents.Nodes["win"].Append(time.GetUtcNow(), models: [("chat", MetricFamilyFilter.Filter(Scrape(processing: 1, promptTokens: 600, predictedTokens: 290, cachedTokens: 100)))]);
        await env.Collector.CollectOnceAsync();

        var samples = env.History.GetModel("chat", Hour);
        Assert.Equal([false, true], samples.Select(s => s.HasRates));
        var kpis = samples[1].Kpis;
        Assert.Equal(1, kpis.Load.RunningRequests);
        Assert.Equal(100, kpis.Load.PromptTokensPerSecond);
        Assert.Equal(50, kpis.Load.GenerationTokensPerSecond);
        Assert.Equal(0.2, kpis.PrefixCacheHitRate);
        Assert.True(double.IsNaN(kpis.Load.KvCacheUsage));
    }

    // ---- Memory by model for process instances (D129 (1)) ----

    [Fact]
    public async Task AWindowsProcessInstance_WhoseRowsRead0MiB_ShowsItsMeasuredValue()
    {
        const string container = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var registry = new FakeModelRegistry()
            .Add(LlamaModel("emb", budgetGiB: 13.5))
            .Add(LlamaModel("chat", budgetGiB: 20))
            .Add(new ModelDefinition { Id = "vllm", ServedName = "Vllm", ModelPath = "p", MemoryBudgetGiB = 30 });
        var cache = new FakeStatusCache();
        cache.Runtimes.Add(new NodeModelRuntime { ModelId = "emb", NodeId = "win", State = ModelState.Ready, InstanceId = "4242", MeasuredGpuGiB = 8.9 });
        cache.Runtimes.Add(new NodeModelRuntime { ModelId = "chat", NodeId = "win", State = ModelState.Starting, InstanceId = "5151" });
        cache.Runtimes.Add(new NodeModelRuntime { ModelId = "vllm", NodeId = "win", State = ModelState.Ready, InstanceId = container });
        var time = new ManualTimeProvider();
        var env = new CollectorEnv(time, registry, cache);
        env.Agents.Add("win");
        env.Nodes.Records["win"] = Node("win");
        GpuProcess[] rows =
        [
            new(4242, @"D:\mirbadllm\llama.cpp\b11476\llama-server.exe", 0, null, null), // WDDM: [N/A] memory
            new(5151, @"D:\mirbadllm\llama.cpp\b11476\llama-server.exe", 0, null, null),
            new(100, "python3", 20 * 1024, container, null),
        ];

        env.Agents.Nodes["win"].Append(time.GetUtcNow(), models: [("emb", Scrape()), ("vllm", "vllm:num_requests_running 0\n")], processes: rows);
        await env.Collector.CollectOnceAsync();
        time.Advance(TimeSpan.FromSeconds(5));
        env.Agents.Nodes["win"].Append(time.GetUtcNow(), models: [("emb", Scrape()), ("vllm", "vllm:num_requests_running 0\n")], processes: null);
        await env.Collector.CollectOnceAsync();

        var host = env.History.GetHost("win", Hour);
        Assert.Equal(ModelMemorySource.Measured, host[0].MemorySource);
        Assert.Equal(new Dictionary<string, double> { ["emb"] = 8.9, ["chat"] = 20, ["vllm"] = 20 }, host[0].ModelMemoryGiB);
        Assert.Equal(ModelMemorySource.Budget, host[1].MemorySource);
        Assert.Equal(new Dictionary<string, double> { ["emb"] = 8.9, ["chat"] = 20, ["vllm"] = 30 }, host[1].ModelMemoryGiB);
    }

    [Fact]
    public async Task AProcessInstanceDoesNotBlockMeasuringContainers_AndNeverTakesAContainersRows()
    {
        // The PID of a process instance is not a container id: before MN-223 it counted as a "known container" and could
        // never match a row; now process instances are left out of the container rule entirely.
        const string container = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
        var registry = new FakeModelRegistry()
            .Add(LlamaModel("emb", budgetGiB: 9))
            .Add(new ModelDefinition { Id = "vllm", ServedName = "Vllm", ModelPath = "p", MemoryBudgetGiB = 30 });
        var cache = new FakeStatusCache();
        cache.Runtimes.Add(new NodeModelRuntime { ModelId = "emb", NodeId = "pc", State = ModelState.Ready, InstanceId = container[..12] }); // a hostile id
        cache.Runtimes.Add(new NodeModelRuntime { ModelId = "vllm", NodeId = "pc", State = ModelState.Ready, InstanceId = container });
        var env = new CollectorEnv(new ManualTimeProvider(), registry, cache);
        env.Agents.Add("pc");
        env.Nodes.Records["pc"] = Node("pc");

        env.Agents.Nodes["pc"].Append(env.Time.GetUtcNow(), models: [("emb", Scrape()), ("vllm", "vllm:num_requests_running 0\n")],
            processes: [new GpuProcess(100, "python3", 12 * 1024, container, null)]);
        await env.Collector.CollectOnceAsync();

        var host = Assert.Single(env.History.GetHost("pc", Hour));
        Assert.Equal(ModelMemorySource.Measured, host.MemorySource);
        Assert.Equal(new Dictionary<string, double> { ["emb"] = 9, ["vllm"] = 12 }, host.ModelMemoryGiB);
    }

    [Fact]
    public void WithModelMemory_ContainersExactlyAsBefore_ProcessesFromTheirMeasuredValue()
    {
        const string container = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var sample = new HostSample(DateTimeOffset.UnixEpoch, new MemorySnapshot(64, 32), null);
        var running = new Dictionary<string, ModelMemoryAttribution.RunningModel>(StringComparer.Ordinal)
        {
            ["vllm"] = new(container, 30),
            ["emb"] = ModelMemoryAttribution.Running(LlamaModel("emb", 9), new NodeModelRuntime { ModelId = "emb", NodeId = "n", State = ModelState.Ready, InstanceId = "77", MeasuredGpuGiB = 6.5 }),
            ["unmeasured"] = ModelMemoryAttribution.Running(LlamaModel("unmeasured", 4), null),
        };

        var measured = ModelMemoryAttribution.WithModelMemory(sample, running,
            [new GpuProcess(77, "llama-server.exe", 0, null, null), new GpuProcess(1, "python3", 10 * 1024, container, null), new GpuProcess(2, "ollama", 1024, null, "ollama.service")]);
        Assert.Equal(new Dictionary<string, double> { ["vllm"] = 10, ["emb"] = 6.5, ["unmeasured"] = 4 }, measured.ModelMemoryGiB);
        Assert.Equal(new Dictionary<string, double> { ["ollama"] = 1 }, measured.OtherGpuGiB);

        var budgeted = ModelMemoryAttribution.WithModelMemory(sample, running, null);
        Assert.Equal(new Dictionary<string, double> { ["vllm"] = 30, ["emb"] = 6.5, ["unmeasured"] = 4 }, budgeted.ModelMemoryGiB);

        Assert.True(ModelMemoryAttribution.ContainersKnown(running));
        Assert.False(ModelMemoryAttribution.ContainersKnown(new Dictionary<string, ModelMemoryAttribution.RunningModel> { ["c"] = new(null, 1) }));
    }

    [Fact]
    public void OnlyALlamaCppProcessDelivery_IsAProcessInstance()
    {
        Assert.True(ModelMemoryAttribution.IsProcessDelivery(LlamaModel("a", 1)));
        Assert.False(ModelMemoryAttribution.IsProcessDelivery(new ModelDefinition { Id = "v", ServedName = "V", ModelPath = "p" }));
        Assert.False(ModelMemoryAttribution.IsProcessDelivery(LlamaModel("c", 1) with { LlamaCpp = new LlamaCppSettings { ModelFile = "m.gguf" } })); // container (P3b)

        var container = ModelMemoryAttribution.Running(new ModelDefinition { Id = "v", ServedName = "V", ModelPath = "p", MemoryBudgetGiB = 5 },
            new NodeModelRuntime { ModelId = "v", NodeId = "n", State = ModelState.Ready, InstanceId = "abc", MeasuredGpuGiB = 2 });
        Assert.Equal(new ModelMemoryAttribution.RunningModel("abc", 5), container); // exactly the P1/P2 entry
    }

    [Fact]
    public void PerGpuMemory_OfAProcessInstance_IsItsMeasuredValueOnItsPlacedGpu()
    {
        var model = LlamaModel("emb", 13.5) with { Placement = new ModelPlacement("win", GpuUuids: ["GPU-b"]) };
        var sample = new NodeSample
        {
            Seq = 1,
            At = DateTimeOffset.UnixEpoch,
            Memory = new NodeMemorySnapshot
            {
                Model = MemoryModel.Discrete,
                HostTotalGiB = 64,
                HostAvailableGiB = 40,
                Gpus = [new GpuMemorySnapshot(0, "GPU-a", 12, 1), new GpuMemorySnapshot(1, "GPU-b", 16, 10)],
            },
            Gpus = [],
            GpuProcesses = [new GpuProcess(4242, "llama-server.exe", 0, null, null, GpuUuid: "GPU-b")],
            Models = [],
        };
        var running = new Dictionary<string, ModelMemoryAttribution.RunningModel>(StringComparer.Ordinal)
        {
            ["emb"] = ModelMemoryAttribution.Running(model, new NodeModelRuntime { ModelId = "emb", NodeId = "win", State = ModelState.Ready, InstanceId = "4242", MeasuredGpuGiB = 8.9 }),
        };
        var host = ModelMemoryAttribution.WithModelMemory(new HostSample(sample.At, new MemorySnapshot(64, 40), null), running, sample.GpuProcesses);

        var gpus = RemoteTelemetryCollector.ToHostGpus(sample, host.ModelMemoryGiB, new Dictionary<string, ModelDefinition> { ["emb"] = model }, sample.GpuProcesses, running)!;

        Assert.Empty(gpus[0].ModelMemoryGiB!);
        Assert.Equal(new Dictionary<string, double> { ["emb"] = 8.9 }, gpus[1].ModelMemoryGiB);
    }

    // ---- Helpers ----

    private static ModelDefinition LlamaModel(string id, double budgetGiB) => new()
    {
        Id = id,
        ServedName = char.ToUpperInvariant(id[0]) + id[1..],
        ModelPath = "gguf/" + id,
        MemoryBudgetGiB = budgetGiB,
        Runtime = RuntimeKind.LlamaCpp,
        LlamaCpp = new LlamaCppSettings { ModelFile = id + ".gguf", Delivery = LaunchDelivery.Process },
    };

    private static NodeRecord Node(string id) =>
        new() { Id = id, Uid = "uid-" + id, Address = "https://127.0.0.1:9443", Approved = true };

    /// <summary>The collector over scripted agents (MN-038's fakes), one manual clock.</summary>
    private sealed class CollectorEnv
    {
        public CollectorEnv(ManualTimeProvider time, FakeModelRegistry registry, FakeStatusCache? cache = null)
        {
            Time = time;
            Agents = new ScriptedAgents(time, 1000);
            History = new InMemoryMetricsHistory(Options.Create(new MetricsOptions()), time);
            Collector = new RemoteTelemetryCollector(Nodes, Agents, registry, History, Options.Create(new MetricsOptions()), time,
                NullLogger<RemoteTelemetryCollector>.Instance, cache);
        }

        public ManualTimeProvider Time { get; }

        public FakeNodeRegistry Nodes { get; } = new();

        public ScriptedAgents Agents { get; }

        public InMemoryMetricsHistory History { get; }

        public RemoteTelemetryCollector Collector { get; }
    }
}
