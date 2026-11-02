using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Benchmarking;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Benchmarking;

/// <summary>The workload presets (schema v2) against a fake vLLM: levels, cold long prompts, shared prefix with the
/// prefix-cache counter, schema-validated tool calls, error counting and repetitions.</summary>
public sealed class WorkloadSuiteTests
{
    private static BenchmarkTarget Target(TuningGoal preset, int maxModelLen = 32_768, int? reps = null) =>
        new("qwen", "Qwen", new Uri("http://ss-qwen:8000/"), maxModelLen, PoolingMode.None, MaxNumSeqs: 3, preset, reps);

    private static BenchmarkSuiteOptions Options() => new()
    {
        Runs = 2,
        ChatOutputTokens = [4, 8],
        WorkloadLevels = [1, 2],
        SharedPrefixTokens = 2048,
        SharedPrefixQuestions = 3,
        MixedStartDelay = TimeSpan.FromMilliseconds(50),
    };

    private static BenchmarkSuite Suite(FakeVllm vllm) => new(new HttpClient(vllm), history: null, Options(), random: new Random(7));

    /// <summary>A fake vLLM: streams max_tokens tokens, counts tokens as chars ÷ 4, keeps a prefix-cache hit counter.</summary>
    private sealed class FakeVllm : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private double _hits;
        private double _preemptions;

        public List<(string Path, string Body)> Requests { get; } = [];

        /// <summary>Tokens added to the hit counter per request that carries a system prompt (0 = no cache reuse).</summary>
        public double HitsPerSharedRequest { get; set; } = 500;

        /// <summary>Time before a long (> 1000-token) one-token request answers: a slow first token on the cold long prompt.</summary>
        public TimeSpan LongPromptDelay { get; set; }

        /// <summary>Added to vllm:num_preemptions_total per streamed request (0 = the pool never preempts).</summary>
        public double PreemptionsPerStream { get; set; }

        public double KvUsage { get; set; } = 0.42;

        public bool MetricsAvailable { get; set; } = true;

        /// <summary>Report the hits under llama.cpp's counter name instead of vLLM's (MN-230).</summary>
        public bool LlamaCppMetricNames { get; set; }

        /// <summary>The counter name this fake reports the hits under.</summary>
        private string Metric => LlamaCppMetricNames ? MirbadLLM.Agent.Contracts.LlamaCppMetrics.PromptTokensCachedTotal : "vllm:prefix_cache_hits_total";

        /// <summary>Arguments returned per tool (JSON text); defaults are valid.</summary>
        public Dictionary<string, string> ToolArguments { get; } = new(StringComparer.Ordinal)
        {
            ["get_time"] = """{"city":"Riyadh"}""",
            ["get_weather"] = """{"city":"Tokyo","unit":"celsius"}""",
            ["create_event"] = """{"title":"Design review","attendees":["Sara","Omar"],"duration_minutes":30}""",
        };

        public Func<string, bool> Fail { get; set; } = _ => false;

        public List<string> Bodies(Func<JsonElement, bool> where)
        {
            lock (_gate)
            {
                return Requests.Where(r => r.Body.Length > 0 && where(JsonDocument.Parse(r.Body).RootElement)).Select(r => r.Body).ToList();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_gate)
            {
                Requests.Add((path, body));
            }

            if (path == "/metrics")
            {
                lock (_gate)
                {
                    return MetricsAvailable
                        ? Text(string.Create(CultureInfo.InvariantCulture,
                            $"# TYPE {Metric} counter\n{Metric}{{model_name=\"Qwen\"}} {_hits}\nvllm:num_preemptions_total{{model_name=\"Qwen\"}} {_preemptions}\nvllm:kv_cache_usage_perc{{model_name=\"Qwen\"}} {KvUsage}\n"))
                        : new HttpResponseMessage(HttpStatusCode.NotFound);
                }
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            int promptChars = root.GetProperty("messages").EnumerateArray().Sum(m => m.GetProperty("content").GetString()!.Length);
            if (path == "/tokenize")
            {
                return Json(JsonSerializer.Serialize(new { count = promptChars / 4 }));
            }

            if (Fail(body))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") };
            }

            if (root.GetProperty("messages")[0].GetProperty("role").GetString() == "system")
            {
                lock (_gate)
                {
                    _hits += HitsPerSharedRequest;
                }
            }

            if (root.TryGetProperty("tools", out _))
            {
                string prompt = root.GetProperty("messages")[0].GetProperty("content").GetString()!;
                string tool = WorkloadPrompts.ToolCases.First(c => c.Prompt == prompt).ExpectedTool;
                string args = JsonSerializer.Serialize(ToolArguments[tool]);
                return Json($$$"""{"choices":[{"message":{"content":null,"tool_calls":[{"function":{"name":"{{{tool}}}","arguments":{{{args}}}}}]}}]}""");
            }

            if (root.TryGetProperty("stream", out var stream) && stream.GetBoolean())
            {
                int tokens = root.GetProperty("max_tokens").GetInt32();
                lock (_gate)
                {
                    _preemptions += PreemptionsPerStream;
                }

                if (tokens == 1 && promptChars / 4 > 1000)
                {
                    await Task.Delay(LongPromptDelay, cancellationToken);
                }

                var sse = new StringBuilder();
                for (int i = 0; i < tokens; i++)
                {
                    sse.Append("data: {\"choices\":[{\"delta\":{\"content\":\"t\"}}]}\n\n");
                }

                sse.Append(CultureInfo.InvariantCulture, $"data: {{\"choices\":[],\"usage\":{{\"prompt_tokens\":{promptChars / 4},\"completion_tokens\":{tokens}}}}}\n\n");
                sse.Append("data: [DONE]\n\n");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream") };
            }

            string question = root.GetProperty("messages")[0].GetProperty("content").GetString()!;
            var match = BenchmarkPrompts.Questions.First(q => q.Question == question);
            return Json(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = match.Expected } } } }));
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };
    }

    private static bool IsStream(JsonElement r) => r.TryGetProperty("stream", out var s) && s.GetBoolean();

    private static string UserContent(JsonElement r) =>
        r.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;

    [Fact]
    public async Task InteractiveChat_RunsEveryLengthAndLevel_ColdLongPrompt_AndQa_AsSchemaV2()
    {
        var vllm = new FakeVllm();
        var progress = new List<BenchmarkProgress>();

        var result = await Suite(vllm).RunAsync(Target(TuningGoal.InteractiveChat), new Sync(progress.Add), TestContext.Current.CancellationToken);

        Assert.Equal(3, result.SchemaVersion);
        Assert.Equal(TuningGoal.InteractiveChat, result.Preset);
        Assert.Equal(2, result.Repetitions);
        Assert.Equal([(4, 1), (4, 2), (4, 3), (8, 1), (8, 2), (8, 3)], result.Levels.Select(l => (l.OutputTokens, l.Concurrency)));
        Assert.All(result.Levels, l =>
        {
            Assert.Equal(2, l.Runs.Count);
            Assert.Equal(0, l.Errors);
            Assert.All(l.Runs, r => Assert.Equal(l.OutputTokens * l.Concurrency, r.CompletionTokens));
        });

        var longPrompt = Assert.Single(result.LongPrompts!);
        Assert.Equal(16_384, longPrompt.TargetTokens);
        Assert.Equal(2, longPrompt.TtftRuns.Count); // the untimed warm-up is not a run
        Assert.Null(result.SharedPrefix);
        Assert.Null(result.Tools);
        Assert.NotNull(result.Mixed);
        Assert.Equal(8, result.Guardrails.QaCorrect);
        Assert.Equal(progress.Count, progress[^1].Step);
        Assert.All(progress, p => Assert.Equal(progress.Count, p.TotalSteps));
    }

    [Theory]
    [InlineData(32_768, 16_384)]
    [InlineData(131_072, 65_536)]
    public async Task Mixed_StartsTheShortSetAfterTheColdLongRequest_AndRecordsBothTtfts(int maxModelLen, int longTarget)
    {
        // The long request's first token takes 300 ms; the short set starts 50 ms after it was sent and answers at once.
        var vllm = new FakeVllm { LongPromptDelay = TimeSpan.FromMilliseconds(300) };
        var result = await Suite(vllm).RunAsync(Target(TuningGoal.InteractiveChat, maxModelLen), null, TestContext.Current.CancellationToken);

        var mixed = result.Mixed!;
        Assert.Equal(longTarget, mixed.LongTargetTokens); // 64K only when the context holds it plus output
        Assert.Equal(3, mixed.Concurrency); // the N=16 set, capped at max num seqs
        Assert.Equal(2, mixed.ShortTtftP95Runs.Count);
        Assert.Equal(2, mixed.LongTtftRuns.Count);
        Assert.All(mixed.LongTtftRuns, t => Assert.True(t >= 0.29, $"long TTFT {t}")); // 10 ms slack: the clock can read the 300 ms delay as 299.8 ms
        Assert.True(mixed.ShortTtftP95 < mixed.LongTtftMedian, $"short p95 {mixed.ShortTtftP95} vs long {mixed.LongTtftMedian}");
        Assert.True(mixed.ShortPerRequestTokS > 0);
        Assert.Equal(0, mixed.Errors);
        Assert.Equal(mixed.ShortTtftP95, WorkloadScorer.Component(result, ScoreComponent.MixedShortTtft, null));

        // The short requests were sent while the long one was in flight: the request before the first of them is the long one.
        var requests = vllm.Requests;
        int firstShort = requests.FindIndex(r => r.Body.Contains("-mixed-1-0.", StringComparison.Ordinal));
        Assert.True(firstShort > 0);
        var before = JsonDocument.Parse(requests[firstShort - 1].Body).RootElement;
        Assert.True(IsStream(before));
        Assert.Equal(1, before.GetProperty("max_tokens").GetInt32());
        Assert.Equal(3, requests.Count(r => r.Body.Contains("-mixed-1-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Mixed_IsNotRun_ForThroughputOrLongDocuments()
    {
        Assert.Null((await Suite(new FakeVllm()).RunAsync(Target(TuningGoal.Throughput), null, TestContext.Current.CancellationToken)).Mixed);
        Assert.Null((await Suite(new FakeVllm()).RunAsync(Target(TuningGoal.LongDocuments), null, TestContext.Current.CancellationToken)).Mixed);
    }

    [Fact]
    public async Task PoolPressure_DiffsThePreemptionCounterOverTheTimedPart_AndKeepsThePeakKvUsage()
    {
        var vllm = new FakeVllm { PreemptionsPerStream = 1, KvUsage = 0.42 };
        var result = await Suite(vllm).RunAsync(Target(TuningGoal.InteractiveChat), null, TestContext.Current.CancellationToken);

        // Between the first sample (after the warm-up) and the last (after the mixed workload): every timed request plus the
        // untimed long-prompt warm-up; the objective questions come after the last sample.
        Assert.Equal(result.TimedRequests + 1, result.Preemptions);
        Assert.Equal(0.42, result.KvCacheUsageMax);

        var quiet = await Suite(new FakeVllm()).RunAsync(Target(TuningGoal.InteractiveChat), null, TestContext.Current.CancellationToken);
        Assert.Equal(0, quiet.Preemptions);

        var unread = await Suite(new FakeVllm { MetricsAvailable = false }).RunAsync(Target(TuningGoal.InteractiveChat), null, TestContext.Current.CancellationToken);
        Assert.Null(unread.Preemptions);
        Assert.Null(unread.KvCacheUsageMax);
    }

    [Fact]
    public async Task ShortChat_EveryRequestOfALevelHasADistinctPrompt_FromTheThreeSets()
    {
        var vllm = new FakeVllm();
        await Suite(vllm).RunAsync(Target(TuningGoal.Throughput), null, TestContext.Current.CancellationToken);

        var chat = vllm.Bodies(r => IsStream(r) && r.GetProperty("max_tokens").GetInt32() == 4).Select(b => UserContent(JsonDocument.Parse(b).RootElement)).ToList();
        Assert.Equal(2 * (1 + 2 + 3), chat.Count);
        Assert.Equal(chat.Count, chat.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(chat, c => c.StartsWith("Write a Python function", StringComparison.Ordinal));
        Assert.Contains(chat, c => c.StartsWith("Plan the steps", StringComparison.Ordinal) || c.StartsWith("As an operations agent", StringComparison.Ordinal));
        Assert.Contains(chat, c => c.StartsWith("Write a long story about a lighthouse", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ColdLongPrompts_StartWithAFreshRandomSequence_BeforeTheReusableContent()
    {
        var vllm = new FakeVllm();
        await Suite(vllm).RunAsync(Target(TuningGoal.LongDocuments, maxModelLen: 32_768), null, TestContext.Current.CancellationToken);

        var longPrompts = vllm.Bodies(r => IsStream(r) && r.GetProperty("max_tokens").GetInt32() == 1)
            .Select(b => UserContent(JsonDocument.Parse(b).RootElement))
            .ToList();
        Assert.Equal(1 + 2, longPrompts.Count); // warm-up + 2 runs; the 64K size does not fit 32K and is skipped
        var heads = longPrompts.Select(p => p[..p.IndexOf('\n', StringComparison.Ordinal)]).ToList();
        Assert.All(heads, h => Assert.Matches(@"^Session [a-z0-9]{5}( [a-z0-9]{5}){23}\.$", h));
        Assert.Equal(heads.Count, heads.Distinct(StringComparer.Ordinal).Count());
        Assert.All(longPrompts, p => Assert.Contains("Record 000001", p, StringComparison.Ordinal));
    }

    [Fact]
    public async Task LongDocuments_MeasuresBothSizes_WhenTheContextFits()
    {
        var vllm = new FakeVllm();
        var result = await Suite(vllm).RunAsync(Target(TuningGoal.LongDocuments, maxModelLen: 131_072), null, TestContext.Current.CancellationToken);

        Assert.Equal([16_384, 65_536], result.LongPrompts!.Select(l => l.TargetTokens));
        Assert.All(result.LongPrompts!, l => Assert.InRange(l.PromptTokens, (int)(l.TargetTokens * 0.9), l.TargetTokens));
    }

    [Fact]
    public async Task AgentsTools_SharedPrefix_PrimesOnce_AndReportsTheCacheHitDelta()
    {
        var vllm = new FakeVllm();
        var result = await Suite(vllm).RunAsync(Target(TuningGoal.AgentsTools), null, TestContext.Current.CancellationToken);

        var sp = result.SharedPrefix!;
        Assert.Equal(3, sp.Questions);
        Assert.Equal(2, sp.TtftRuns.Count);
        Assert.Equal(6 * 500, sp.CacheHitTokens); // timed questions only: the priming request came before the first read
        Assert.True(sp.CacheReuseObserved);
        Assert.Equal("shared prefix (cache reuse observed)", sp.Label);

        var shared = vllm.Bodies(r => r.GetProperty("messages")[0].GetProperty("role").GetString() == "system" && IsStream(r)).ToList();
        Assert.Equal(1 + 6, shared.Count);
        var systems = shared.Select(b => JsonDocument.Parse(b).RootElement.GetProperty("messages")[0].GetProperty("content").GetString()).Distinct().ToList();
        Assert.Single(systems); // one shared prefix
        var questions = shared.Skip(1).Select(b => UserContent(JsonDocument.Parse(b).RootElement)).ToList();
        Assert.Equal(questions.Count, questions.Distinct(StringComparer.Ordinal).Count()); // different questions
    }

    [Fact]
    public async Task SharedPrefix_ReadsLlamaCppsCachedPromptTokens_NotOnlyVllmsHits()
    {
        // MN-230: only the vLLM counter names were read, so a llama.cpp model's shared-prefix result always said
        // "cache metrics unavailable" even though the agent's filter keeps llamacpp:prompt_tokens_cached_total.
        var server = new FakeVllm { LlamaCppMetricNames = true };
        var result = await Suite(server).RunAsync(Target(TuningGoal.AgentsTools), null, TestContext.Current.CancellationToken);

        var sp = result.SharedPrefix!;
        Assert.Equal(6 * 500, sp.CacheHitTokens);
        Assert.True(sp.CacheReuseObserved);
        Assert.Equal("shared prefix (cache reuse observed)", sp.Label);
    }

    [Theory]
    [InlineData(true, "repeated prompt (no cache reuse observed)")]
    [InlineData(false, "repeated prompt (cache metrics unavailable)")]
    public async Task SharedPrefix_WithoutObservedReuse_IsLabelledARepeatedPrompt(bool metrics, string label)
    {
        var vllm = new FakeVllm { HitsPerSharedRequest = 0, MetricsAvailable = metrics };
        var result = await Suite(vllm).RunAsync(Target(TuningGoal.AgentsTools), null, TestContext.Current.CancellationToken);

        Assert.False(result.SharedPrefix!.CacheReuseObserved);
        Assert.Equal(label, result.SharedPrefix.Label);
    }

    [Fact]
    public async Task ToolCalls_AreValidatedAgainstTheirSchemas_EveryRepetition()
    {
        var vllm = new FakeVllm();
        var valid = await Suite(vllm).RunAsync(Target(TuningGoal.AgentsTools), null, TestContext.Current.CancellationToken);
        Assert.Equal(6, valid.Tools!.Total);
        Assert.Equal(6, valid.Tools.Valid);
        Assert.True(valid.Guardrails.ToolCallValid);

        vllm.ToolArguments["get_weather"] = """{"city":"Tokyo"}""";
        vllm.ToolArguments["create_event"] = """{"title":"Design review","attendees":["Sara"],"duration_minutes":"30"}""";
        var invalid = await Suite(vllm).RunAsync(Target(TuningGoal.AgentsTools), null, TestContext.Current.CancellationToken);

        Assert.Equal(2, invalid.Tools!.Valid);
        Assert.False(invalid.Guardrails.ToolCallValid);
        Assert.Contains(invalid.Tools.Cases, c => c.Detail.Contains("$.unit is required but missing", StringComparison.Ordinal));
        Assert.Contains(invalid.Tools.Cases, c => c.Detail.Contains("$.duration_minutes should be integer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedTimedRequests_AreCounted_NotThrown()
    {
        var vllm = new FakeVllm { Fail = body => body.Contains("Variant 2,", StringComparison.Ordinal) };
        var result = await Suite(vllm).RunAsync(Target(TuningGoal.InteractiveChat), null, TestContext.Current.CancellationToken);

        Assert.True(result.Errors > 0);
        Assert.Equal(0, result.Level(1, 4)!.Errors); // N=1 only sends variant 1
        Assert.Equal(2, result.Level(2, 4)!.Errors); // one per repetition
        Assert.True(result.TimedRequests > result.Errors);
    }

    [Fact]
    public async Task Repetitions_FromTheTarget_OverrideTheDefault()
    {
        var vllm = new FakeVllm();
        var result = await Suite(vllm).RunAsync(Target(TuningGoal.InteractiveChat, reps: 5), null, TestContext.Current.CancellationToken);

        Assert.Equal(5, result.Repetitions);
        Assert.All(result.Levels, l => Assert.Equal(5, l.Runs.Count));
        Assert.Equal(5, result.LongPrompts![0].TtftRuns.Count);
    }

    [Fact]
    public async Task NoPreset_RunsTheClassicSuite_SchemaV1()
    {
        var vllm = new FakeVllm();
        var options = Options();
        options.Levels = [1];
        options.IncludePrefill = false;
        var suite = new BenchmarkSuite(new HttpClient(vllm), history: null, options);

        var result = await suite.RunAsync(new BenchmarkTarget("qwen", "Qwen", new Uri("http://ss-qwen:8000/"), 8192), null, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.SchemaVersion);
        Assert.Null(result.Preset);
        Assert.Equal([1], result.Levels.Select(l => l.Concurrency));
    }

    private sealed class Sync(Action<BenchmarkProgress> report) : IProgress<BenchmarkProgress>
    {
        public void Report(BenchmarkProgress value) => report(value);
    }
}
