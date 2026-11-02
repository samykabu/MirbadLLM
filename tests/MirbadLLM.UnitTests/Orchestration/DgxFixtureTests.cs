using System.Runtime.CompilerServices;
using MirbadLLM.Orchestration;

#pragma warning disable CA1707 // underscore test names.

namespace MirbadLLM.UnitTests.Orchestration;

/// <summary>
/// Parses a trimmed, real <c>/metrics</c> scrape captured from the running <c>ornith-9b-nvfp4</c> vLLM
/// container on the DGX Spark (<c>ssh ollama-box 'curl -s localhost:8101/metrics'</c>), to shape the
/// parser against real label names rather than only hand-written fixtures.
/// </summary>
public sealed class DgxFixtureTests
{
    private static readonly string FixtureText = File.ReadAllText(FixturePath());

    [Fact]
    public void RealScrape_ParsesLoadGauges()
    {
        var samples = PrometheusText.Parse(FixtureText);

        Assert.Equal(0.0, PrometheusText.Sum(samples, "vllm:num_requests_running"));
        Assert.Equal(0.0, PrometheusText.Sum(samples, "vllm:num_requests_waiting"));
        Assert.True(PrometheusText.HasMetric(samples, "vllm:kv_cache_usage_perc"));
    }

    [Fact]
    public void RealScrape_ParsesRequestSuccessByFinishedReason()
    {
        var samples = PrometheusText.Parse(FixtureText);

        var byReason = PrometheusText.SumByLabel(samples, "vllm:request_success_total", "finished_reason");

        Assert.Contains("stop", byReason.Keys);
        Assert.Contains("length", byReason.Keys);
        Assert.Contains("abort", byReason.Keys);
    }

    [Fact]
    public void RealScrape_ParsesAllFourLatencyHistograms()
    {
        var samples = PrometheusText.Parse(FixtureText);

        foreach (string baseName in new[]
        {
            "vllm:time_to_first_token_seconds",
            "vllm:inter_token_latency_seconds",
            "vllm:e2e_request_latency_seconds",
            "vllm:request_queue_time_seconds",
        })
        {
            var histogram = PrometheusText.Histogram(samples, baseName);
            Assert.NotNull(histogram);
            Assert.True(double.IsPositiveInfinity(histogram!.Buckets[^1].UpperBound));
        }
    }

    [Fact]
    public void RealScrape_SpecDecodeMetricsAbsent_NotConfiguredOnThisModel()
    {
        var samples = PrometheusText.Parse(FixtureText);

        Assert.False(PrometheusText.HasMetric(samples, "vllm:spec_decode_num_draft_tokens_total"));
    }

    [Fact]
    public void RealScrape_ToolCallParserInvocations_SummedAcrossModeAndOutcomeLabels()
    {
        var samples = PrometheusText.Parse(FixtureText);

        Assert.True(PrometheusText.HasMetric(samples, "vllm:tool_call_parser_invocations_total"));
        // Real scrape has 8 label combinations (mode x outcome x request_type), all zero at capture time.
        Assert.Equal(0.0, PrometheusText.Sum(samples, "vllm:tool_call_parser_invocations_total"));
    }

    [Fact]
    public void RealScrape_ComputesKpisWithoutThrowing_OnFirstAndSecondSample()
    {
        var samples = PrometheusText.Parse(FixtureText);

        var first = ModelKpiCalculator.Compute(samples, previous: null, elapsedSeconds: 0);
        var second = ModelKpiCalculator.Compute(samples, previous: samples, elapsedSeconds: 5);

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    private static string FixturePath([CallerFilePath] string sourceFile = "")
        => Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures", "vllm-ornith-9b-nvfp4.metrics.txt");
}
