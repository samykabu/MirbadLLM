using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Benchmarking;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Benchmarking;

/// <summary>The embeddings benchmark for pooling models against a fake <c>/v1/embeddings</c>.</summary>
public sealed class EmbeddingBenchmarkTests
{
    private static readonly BenchmarkTarget Target = new("embed", "Embed", new Uri("http://ss-embed:8000/"), 8192, PoolingMode.Embedding, MaxNumSeqs: 8);

    private static BenchmarkSuiteOptions Options() => new() { Runs = 2, EmbeddingLevels = [1, 4], EmbeddingInputWords = [4, 32, 128, 512] };

    /// <summary>Returns one unit vector per input (or a broken shape when asked), with a prompt-token count.</summary>
    private sealed class FakeEmbeddings : HttpMessageHandler
    {
        private readonly Lock _gate = new();

        public List<string> Paths { get; } = [];

        public List<IReadOnlyList<string>> Inputs { get; } = [];

        public Func<int, int> VectorCount { get; set; } = n => n;

        public double Scale { get; set; } = 1;

        public bool NaN { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var inputs = doc.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToList();
            lock (_gate)
            {
                Paths.Add(request.RequestUri!.AbsolutePath);
                Inputs.Add(inputs);
            }

            int tokens = inputs.Sum(i => i.Split(' ').Length);
            var data = Enumerable.Range(0, VectorCount(inputs.Count))
                .Select(i => new { index = i, embedding = Vector() })
                .ToList();
            string json = JsonSerializer.Serialize(new { data, usage = new { prompt_tokens = tokens } });
            if (NaN)
            {
                json = json.Replace("0.6", "NaN", StringComparison.Ordinal);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        private double[] Vector() => [0.6 * Scale, 0.8 * Scale, 0];
    }

    [Fact]
    public async Task Measures_EveryLevel_WithDistinctMixedLengthBatches_AndValidVectors()
    {
        var handler = new FakeEmbeddings();
        var suite = new BenchmarkSuite(new HttpClient(handler), history: null, Options());
        var progress = new List<BenchmarkProgress>();

        var result = await suite.RunAsync(Target, new Sync(progress.Add), TestContext.Current.CancellationToken);

        Assert.Empty(result.Levels); // no generation numbers
        Assert.Null(result.Prefill);
        var emb = Assert.IsType<EmbeddingResult>(result.Embeddings);
        Assert.Equal([1, 4, 8], emb.Levels.Select(l => l.Concurrency)); // 1, 4 and the configuration's max num seqs
        Assert.All(emb.Levels, l =>
        {
            Assert.Equal(2, l.Runs.Count);
            Assert.True(l.InputTokS > 0);
            Assert.True(l.RequestsPerSecond > 0);
            Assert.True(l.LatencyP95 >= l.LatencyP50);
        });
        Assert.True(emb.Check.Valid);
        Assert.True(emb.Check.Normalized);
        Assert.Equal(3, emb.Check.Dimension);
        Assert.True(result.Guardrails.ToolCallValid);

        Assert.All(handler.Paths, p => Assert.Equal("/v1/embeddings", p));
        Assert.Equal(1 + (2 * (1 + 4 + 8)), handler.Paths.Count); // warm-up + every request
        var all = handler.Inputs.SelectMany(i => i).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count()); // nothing repeats: no cache hits
        Assert.All(handler.Inputs, batch =>
        {
            Assert.Equal(4, batch.Count);
            Assert.Equal(4, batch.Select(i => i.Split(' ').Length).Distinct().Count()); // mixed lengths
        });

        Assert.Equal(1 + (3 * 2), progress.Count);
        Assert.All(progress, p => Assert.Equal(7, p.TotalSteps));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("nan")]
    public async Task BrokenVectors_FailTheCheck(string broken)
    {
        var handler = new FakeEmbeddings();
        if (broken == "count")
        {
            handler.VectorCount = n => n - 1;
        }
        else
        {
            handler.NaN = true;
        }

        var suite = new BenchmarkSuite(new HttpClient(handler), history: null, Options());
        var result = await suite.RunAsync(Target, null, TestContext.Current.CancellationToken);

        Assert.False(result.Embeddings!.Check.Valid);
        Assert.StartsWith("Invalid embeddings:", result.Embeddings.Check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnnormalizedVectors_AreValid_ButReportedAsNotNormalized()
    {
        var handler = new FakeEmbeddings { Scale = 3 };
        var suite = new BenchmarkSuite(new HttpClient(handler), history: null, Options());

        var check = (await suite.RunAsync(Target, null, TestContext.Current.CancellationToken)).Embeddings!.Check;

        Assert.True(check.Valid);
        Assert.False(check.Normalized);
    }

    [Fact]
    public void Inputs_FitMaxModelLen()
    {
        var suite = new BenchmarkSuite(new HttpClient(new FakeEmbeddings()), history: null, new BenchmarkSuiteOptions());
        var inputs = suite.EmbeddingInputs(Target with { MaxModelLen = 512 }, "t", 0);

        Assert.Equal(4, inputs.Count);
        Assert.All(inputs, i => Assert.True(i.Split(' ').Length - 1 <= (int)((512 - 32) / 1.5), i.Length.ToString(CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task Reranker_IsNotSupported()
    {
        var suite = new BenchmarkSuite(new HttpClient(new FakeEmbeddings()), history: null, Options());
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            suite.RunAsync(Target with { Pooling = PoolingMode.Reranker }, null, TestContext.Current.CancellationToken));
        Assert.Equal(BenchmarkSuite.RerankerUnsupported, ex.Message);
    }

    [Fact]
    public void Detection_FromArgsAndTags()
    {
        var model = new ModelDefinition { Id = "e", ServedName = "E", ModelPath = "e" };
        Assert.Equal(PoolingMode.None, PoolingDetection.Detect(model));
        Assert.Equal(PoolingMode.Embedding, PoolingDetection.Detect(model with { ExtraArgs = ["--runner", "pooling"] }));
        Assert.Equal(PoolingMode.Embedding, PoolingDetection.Detect(model with { ExtraArgs = ["--runner=pooling"] }));
        Assert.Equal(PoolingMode.Embedding, PoolingDetection.Detect(model with { ExtraArgs = ["--convert", "embed"] }));
        Assert.Equal(PoolingMode.Embedding, PoolingDetection.Detect(model with { Tags = ["embedding"] }));
        Assert.Equal(PoolingMode.Reranker, PoolingDetection.Detect(model with { ExtraArgs = ["--runner", "pooling", "--convert", "classify"] }));
        Assert.Equal(PoolingMode.Reranker, PoolingDetection.Detect(model with { Tags = ["reranker"] }));
    }

    private sealed class Sync(Action<BenchmarkProgress> report) : IProgress<BenchmarkProgress>
    {
        public void Report(BenchmarkProgress value) => report(value);
    }
}
