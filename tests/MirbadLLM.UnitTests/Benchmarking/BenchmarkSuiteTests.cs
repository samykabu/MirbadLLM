using System.Net;
using System.Text;
using System.Text.Json;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.UnitTests.Benchmarking.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Benchmarking;

/// <summary>The suite against a fake vLLM: request shape, medians, guardrails, prefill and the noisy flag.</summary>
public sealed class BenchmarkSuiteTests
{
    private static readonly BenchmarkTarget Target = new("qwen", "Qwen", new Uri("http://ss-qwen:8000/"), 8192);

    private static BenchmarkSuiteOptions SmallOptions() => new() { Levels = [1, 2], Runs = 3, MaxTokens = 8, PrefillTokens = 4096 };

    /// <summary>A vLLM stand-in: streams <c>max_tokens</c> tokens, answers tools and the objective questions.</summary>
    private static LockedHandler FakeVllm(bool toolCall = true, int wrongAnswers = 0)
    {
        int wrongLeft = wrongAnswers;
        return new LockedHandler
        {
            Handler = request =>
            {
                string path = request.RequestUri!.AbsolutePath;
                string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (path == "/tokenize")
                {
                    return Json("""{"count": 4400, "max_model_len": 8192}""");
                }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("stream", out var stream) && stream.GetBoolean())
                {
                    int tokens = root.GetProperty("max_tokens").GetInt32();
                    int promptTokens = root.GetProperty("messages")[0].GetProperty("content").GetString()!.Length / 4;
                    var sse = new StringBuilder();
                    for (int i = 0; i < tokens; i++)
                    {
                        sse.Append("data: {\"choices\":[{\"delta\":{\"content\":\"t\"}}]}\n\n");
                    }

                    sse.Append($"data: {{\"choices\":[],\"usage\":{{\"prompt_tokens\":{promptTokens},\"completion_tokens\":{tokens}}}}}\n\n");
                    sse.Append("data: [DONE]\n\n");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream") };
                }

                if (root.TryGetProperty("tools", out _))
                {
                    return toolCall
                        ? Json("""{"choices":[{"message":{"content":null,"tool_calls":[{"function":{"name":"get_time","arguments":"{\"city\":\"Riyadh\"}"}}]}}]}""")
                        : Json("""{"choices":[{"message":{"content":"It is noon."}}]}""");
                }

                string question = root.GetProperty("messages")[0].GetProperty("content").GetString()!;
                var match = BenchmarkPrompts.Questions.First(q => q.Question == question);
                string answer = wrongLeft-- > 0 ? "I am not sure." : match.Expected;
                return Json(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = answer } } } }));
            },
        };
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Runs_EveryLevel_ThreeTimes_WithDeterministicRequests()
    {
        var handler = FakeVllm();
        var suite = new BenchmarkSuite(new HttpClient(handler), history: null, SmallOptions());
        var progress = new List<BenchmarkProgress>();

        var result = await suite.RunAsync(Target, new SyncProgress(progress.Add), TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], result.Levels.Select(l => l.Concurrency));
        Assert.All(result.Levels, l => Assert.Equal(3, l.Runs.Count));
        Assert.All(result.Levels.SelectMany(l => l.Runs), r => Assert.Equal(8 * r.Concurrency, r.CompletionTokens));
        Assert.Equal(suite.StepCount, progress.Count);
        Assert.Equal(progress.Count, progress[^1].Step);

        var streamed = handler.RequestBodies.Where(b => b.Contains("\"stream\":true", StringComparison.Ordinal)).ToList();
        using var first = JsonDocument.Parse(streamed[0]);
        var r = first.RootElement;
        Assert.Equal(8, r.GetProperty("max_tokens").GetInt32());
        Assert.Equal(8, r.GetProperty("min_tokens").GetInt32());
        Assert.Equal(0, r.GetProperty("temperature").GetInt32());
        Assert.False(r.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.Equal("Qwen", r.GetProperty("model").GetString());
        Assert.All(handler.Requests, req => Assert.Equal("ss-qwen", req.RequestUri!.Host));
    }

    /// <summary>A reasoning model: streams 30 thinking characters, then the answer, for each objective question.</summary>
    private static LockedHandler FakeThinker(int cutOff = 0, int failures = 0)
    {
        var inner = FakeVllm();
        int cutLeft = cutOff, failLeft = failures;
        return new LockedHandler
        {
            Handler = request =>
            {
                string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!body.Contains("\"enable_thinking\":true", StringComparison.Ordinal))
                {
                    return inner.Handler(request);
                }

                if (failLeft-- > 0)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") };
                }

                using var doc = JsonDocument.Parse(body);
                string question = doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
                string answer = BenchmarkPrompts.Questions.First(q => q.Question == question).Expected;
                var sse = new StringBuilder();
                for (int i = 0; i < 3; i++)
                {
                    sse.Append("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"0123456789\"}}]}\n\n");
                }

                if (cutLeft-- <= 0)
                {
                    sse.Append(JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = answer } } } }).Insert(0, "data: ")).Append("\n\n");
                }

                sse.Append("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":40}}\n\n");
                sse.Append("data: [DONE]\n\n");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream") };
            },
        };
    }

    [Fact]
    public async Task AReasoningModel_IsAskedTheQuestionsAgainWithThinkingOn()
    {
        var handler = FakeThinker(cutOff: 1, failures: 1);
        var suite = new BenchmarkSuite(new HttpClient(handler), null, SmallOptions());
        var progress = new List<BenchmarkProgress>();

        var result = await suite.RunAsync(Target with { Thinks = true }, new SyncProgress(progress.Add), TestContext.Current.CancellationToken);

        var thinking = Assert.IsType<ThinkingResult>(result.Thinking);
        Assert.Equal((8, 6, 1, 1), (thinking.Questions, thinking.Correct, thinking.Unanswered, thinking.Errors));
        Assert.True(thinking.ThinkingTokensMedian > 30); // 30 of the reply's characters are thinking, out of 40 tokens
        Assert.Equal(BenchmarkSuite.ThinkingMaxTokens, thinking.MaxTokens);
        Assert.Contains(progress, p => p.Phase == "Objective questions, thinking on");
        Assert.Equal(progress.Count, progress[^1].TotalSteps);

        // Everything else still runs with thinking off.
        Assert.Equal(8, handler.RequestBodies.Count(b => b.Contains("\"enable_thinking\":true", StringComparison.Ordinal)));
        Assert.Null((await new BenchmarkSuite(new HttpClient(FakeVllm()), null, SmallOptions()).RunAsync(Target, null, TestContext.Current.CancellationToken)).Thinking);
    }

    [Fact]
    public async Task Guardrails_ValidToolCall_AndAllAnswersRight()
    {
        var suite = new BenchmarkSuite(new HttpClient(FakeVllm()), null, SmallOptions());
        var result = await suite.RunAsync(Target, null, TestContext.Current.CancellationToken);

        Assert.True(result.Guardrails.ToolCallValid);
        Assert.Equal("get_time({\"city\":\"Riyadh\"})", result.Guardrails.ToolCallDetail);
        Assert.Equal(8, result.Guardrails.QaTotal);
        Assert.Equal(8, result.Guardrails.QaCorrect);
    }

    [Fact]
    public async Task Guardrails_NoToolCall_AndWrongAnswers_AreReported()
    {
        var suite = new BenchmarkSuite(new HttpClient(FakeVllm(toolCall: false, wrongAnswers: 2)), null, SmallOptions());
        var result = await suite.RunAsync(Target, null, TestContext.Current.CancellationToken);

        Assert.False(result.Guardrails.ToolCallValid);
        Assert.StartsWith("No tool call", result.Guardrails.ToolCallDetail, StringComparison.Ordinal);
        Assert.Equal(6, result.Guardrails.QaCorrect);
        Assert.Equal(2, result.Guardrails.Answers.Count(a => !a.Correct));
    }

    [Fact]
    public async Task Prefill_UsesMinOfContextAndCap_ColdThenCached()
    {
        var handler = FakeVllm();
        var suite = new BenchmarkSuite(new HttpClient(handler), null, SmallOptions());

        var result = await suite.RunAsync(Target, null, TestContext.Current.CancellationToken);

        Assert.NotNull(result.Prefill);
        var longPrompts = handler.RequestBodies.Where(b => b.Contains("Below is a sensor log", StringComparison.Ordinal) && b.Contains("\"stream\":true", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, longPrompts.Count);
        Assert.Equal(longPrompts[0], longPrompts[1]); // the repeat is the identical prompt (prefix-cache hit)
        Assert.Contains("\"max_tokens\":1", longPrompts[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Median_IsTheMiddleOfThreeRuns_PerMetric()
    {
        var runs = new[]
        {
            new RunMeasurement(4, 1024, 10, 30, 300, 0.3, 0.9, 0, 4),
            new RunMeasurement(4, 1024, 10, 10, 100, 0.1, 0.5, 0, 4),
            new RunMeasurement(4, 1024, 10, 20, 250, 0.2, 0.7, 0, 4),
        };

        var level = BenchmarkSuite.Summarize(4, runs);

        Assert.Equal(20, level.PerRequestTokS);
        Assert.Equal(250, level.AggregateTokS);
        Assert.Equal(0.2, level.TtftP50);
        Assert.Equal(0.7, level.TtftP95);
        Assert.False(level.Noisy);
    }

    [Fact]
    public void Percentile_IsNearestRank()
    {
        double[] values = [5, 1, 4, 2, 3];
        Assert.Equal(3, BenchmarkSuite.Percentile(values, 50));
        Assert.Equal(5, BenchmarkSuite.Percentile(values, 95));
        Assert.Equal(7, BenchmarkSuite.Percentile([7], 95));
    }

    [Fact]
    public async Task OtherBusyModels_MarkTheResultNoisy()
    {
        var history = new FakeMetricsHistory
        {
            LatestKpis = new Dictionary<string, ModelKpis> { ["ornith"] = Results.Kpis(3), ["qwen"] = Results.Kpis(1) },
        };
        history.ModelSamplesById["ornith"] = [new ModelSample(DateTimeOffset.UtcNow.AddSeconds(30), Results.Kpis(3))];
        var suite = new BenchmarkSuite(new HttpClient(FakeVllm()), history, SmallOptions());

        var result = await suite.RunAsync(Target, null, TestContext.Current.CancellationToken);

        Assert.True(result.Noise.Noisy);
        Assert.Equal("noisy: other models were busy (ornith up to 3 running)", Assert.Single(result.Noise.Reasons));
        Assert.Equal(3, result.Noise.OtherModelsMaxRunning["ornith"]);
        Assert.All(result.Levels, l => Assert.True(l.Noisy));
    }

    [Fact]
    public async Task QuietBox_IsNotNoisy()
    {
        var history = new FakeMetricsHistory { LatestKpis = new Dictionary<string, ModelKpis> { ["ornith"] = Results.Kpis(0) } };
        history.ModelSamplesById["ornith"] = [new ModelSample(DateTimeOffset.UtcNow.AddSeconds(30), Results.Kpis(0))];
        var suite = new BenchmarkSuite(new HttpClient(FakeVllm()), history, SmallOptions());

        var result = await suite.RunAsync(Target, null, TestContext.Current.CancellationToken);

        Assert.False(result.Noise.Noisy);
        Assert.Empty(result.Noise.Reasons);
    }

    [Fact]
    public async Task OutsideTrafficOnTheMeasuredModel_IsNoisyToo()
    {
        var history = new FakeMetricsHistory { LatestKpis = new Dictionary<string, ModelKpis> { ["qwen"] = Results.Kpis(5) } };
        history.ModelSamplesById["qwen"] = [new ModelSample(DateTimeOffset.UtcNow.AddSeconds(30), Results.Kpis(5))];
        var suite = new BenchmarkSuite(new HttpClient(FakeVllm()), history, SmallOptions());

        var result = await suite.RunAsync(Target, null, TestContext.Current.CancellationToken);

        Assert.True(result.Noise.Noisy);
        Assert.Contains("other clients ran on this model", Assert.Single(result.Noise.Reasons), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpstreamError_FailsTheBenchmark()
    {
        var handler = new LockedHandler { Handler = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") } };
        var suite = new BenchmarkSuite(new HttpClient(handler), null, SmallOptions());

        var ex = await Assert.ThrowsAsync<BenchmarkRequestException>(() => suite.RunAsync(Target, null, TestContext.Current.CancellationToken));
        Assert.Equal(500, ex.StatusCode);
    }

    /// <summary>Thread-safe request capture (the suite fires concurrent requests).</summary>
    private sealed class LockedHandler : HttpMessageHandler
    {
        private readonly Lock _gate = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> RequestBodies { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_gate)
            {
                Requests.Add(request);
                RequestBodies.Add(body);
            }

            return Handler(request);
        }
    }

    private sealed class SyncProgress(Action<BenchmarkProgress> report) : IProgress<BenchmarkProgress>
    {
        public void Report(BenchmarkProgress value) => report(value);
    }
}
