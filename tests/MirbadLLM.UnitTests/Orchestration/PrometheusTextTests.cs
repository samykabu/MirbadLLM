using MirbadLLM.Orchestration;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

namespace MirbadLLM.UnitTests.Orchestration;

public sealed class PrometheusTextTests
{
    private const string HistogramFixture = """
        # HELP vllm:time_to_first_token_seconds TTFT.
        # TYPE vllm:time_to_first_token_seconds histogram
        vllm:time_to_first_token_seconds_bucket{engine="0",le="0.1"} 1.0
        vllm:time_to_first_token_seconds_bucket{engine="1",le="0.1"} 2.0
        vllm:time_to_first_token_seconds_bucket{engine="0",le="0.5"} 4.0
        vllm:time_to_first_token_seconds_bucket{engine="1",le="0.5"} 3.0
        vllm:time_to_first_token_seconds_bucket{engine="0",le="+Inf"} 9.0
        vllm:time_to_first_token_seconds_bucket{engine="1",le="+Inf"} 3.0
        vllm:time_to_first_token_seconds_sum{engine="0"} 3.0
        vllm:time_to_first_token_seconds_sum{engine="1"} 1.0
        vllm:time_to_first_token_seconds_count{engine="0"} 9.0
        vllm:time_to_first_token_seconds_count{engine="1"} 3.0
        vllm:some_nan_metric{engine="0"} NaN
        """;

    [Fact]
    public void Parse_SkipsComments_AndNaN()
    {
        var samples = PrometheusText.Parse(HistogramFixture);

        Assert.DoesNotContain(samples, s => s.Name == "vllm:some_nan_metric");
        Assert.Contains(samples, s => s.Name == "vllm:time_to_first_token_seconds_bucket" && s.Labels["engine"] == "1" && s.Labels["le"] == "0.1");
    }

    [Fact]
    public void Sum_AddsAcrossAllLabelSets()
    {
        var samples = PrometheusText.Parse(HistogramFixture);

        // engine="0" count(9) + engine="1" count(3)
        Assert.Equal(12.0, PrometheusText.Sum(samples, "vllm:time_to_first_token_seconds_count"));
    }

    [Fact]
    public void HasMetric_TrueOnlyWhenPresent()
    {
        var samples = PrometheusText.Parse(HistogramFixture);

        Assert.True(PrometheusText.HasMetric(samples, "vllm:time_to_first_token_seconds_count"));
        Assert.False(PrometheusText.HasMetric(samples, "vllm:spec_decode_num_draft_tokens_total"));
    }

    [Fact]
    public void SumByLabel_GroupsByOneLabel_SummingOverOthers()
    {
        const string text = """
            vllm:request_success_total{engine="0",finished_reason="stop"} 3.0
            vllm:request_success_total{engine="1",finished_reason="stop"} 2.0
            vllm:request_success_total{engine="0",finished_reason="length"} 1.0
            """;
        var samples = PrometheusText.Parse(text);

        var byReason = PrometheusText.SumByLabel(samples, "vllm:request_success_total", "finished_reason");

        Assert.Equal(5.0, byReason["stop"]); // 3 + 2, summed across engines
        Assert.Equal(1.0, byReason["length"]);
    }

    [Fact]
    public void Histogram_SumsBucketsAcrossLabelSets_IncludingPlusInf()
    {
        var samples = PrometheusText.Parse(HistogramFixture);

        var histogram = PrometheusText.Histogram(samples, "vllm:time_to_first_token_seconds");

        Assert.NotNull(histogram);
        Assert.Equal(3.0, LookupBucket(histogram!, 0.1)); // 1 + 2
        Assert.Equal(7.0, LookupBucket(histogram!, 0.5)); // 4 + 3
        Assert.Equal(12.0, LookupBucket(histogram!, double.PositiveInfinity)); // 9 + 3
        Assert.Equal(4.0, histogram!.Sum); // 3 + 1
        Assert.Equal(12.0, histogram!.Count); // 9 + 3
    }

    [Fact]
    public void Histogram_ReturnsNull_WhenFamilyAbsent()
    {
        var samples = PrometheusText.Parse("vllm:num_requests_running 1.0");

        Assert.Null(PrometheusText.Histogram(samples, "vllm:time_to_first_token_seconds"));
    }

    [Fact]
    public void Histogram_BucketsAreOrderedAscending_WithPlusInfLast()
    {
        var samples = PrometheusText.Parse(HistogramFixture);

        var histogram = PrometheusText.Histogram(samples, "vllm:time_to_first_token_seconds")!;

        Assert.Equal(0.1, histogram.Buckets[0].UpperBound);
        Assert.Equal(0.5, histogram.Buckets[1].UpperBound);
        Assert.True(double.IsPositiveInfinity(histogram.Buckets[2].UpperBound));
    }

    private static double LookupBucket(PrometheusHistogram histogram, double upperBound)
        => histogram.Buckets.Single(b => b.UpperBound.Equals(upperBound)).CumulativeCount;
}
