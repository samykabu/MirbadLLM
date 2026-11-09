using MirbadLLM.Orchestration;

#pragma warning disable CA1707 // underscore test names.

namespace MirbadLLM.UnitTests.Orchestration;

public sealed class ModelKpiCalculatorTests
{
    private static string Sglang(double running, double throughput, double generated, double prompts, double cached, double requests, double accept) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture, $$"""
        sglang:num_running_reqs{model_name="m1",engine_type="unified"} {{running}}
        sglang:num_queue_reqs{model_name="m1",engine_type="unified"} 1
        sglang:token_usage{model_name="m1",engine_type="unified"} 0.25
        sglang:gen_throughput{model_name="m1",engine_type="unified"} {{throughput}}
        sglang:generation_tokens_total{model_name="m1",is_streaming="true"} {{generated}}
        sglang:prompt_tokens_total{model_name="m1",is_streaming="true"} {{prompts}}
        sglang:cached_tokens_total{model_name="m1",cache_source="device"} {{cached}}
        sglang:num_requests_total{model_name="m1",is_streaming="true"} {{requests}}
        sglang:spec_accept_rate{model_name="m1",engine_type="unified"} {{accept}}
        sglang:time_to_first_token_seconds_bucket{model_name="m1",le="0.5"} {{requests}}
        sglang:time_to_first_token_seconds_bucket{model_name="m1",le="+Inf"} {{requests}}
        sglang:time_to_first_token_seconds_sum{model_name="m1"} 1
        sglang:time_to_first_token_seconds_count{model_name="m1"} {{requests}}
        """);

    [Fact]
    public void Sglang_UsesTheLiveThroughput_WhileItMoves_AndReadsItsOtherFamilies()
    {
        var before = PrometheusText.Parse(Sglang(2, 100, 1000, 500, 100, 10, 0.5));
        var after = PrometheusText.Parse(Sglang(2, 140, 1000, 700, 200, 12, 0.75));

        var kpis = ModelKpiCalculator.Compute(after, before, 10);

        Assert.Equal(140, kpis.Load.GenerationTokensPerSecond);
        Assert.Equal(20, kpis.Load.PromptTokensPerSecond);
        Assert.Equal((2d, 1d, 0.25), (kpis.Load.RunningRequests, kpis.Load.WaitingRequests, kpis.Load.KvCacheUsage));
        Assert.Equal(12, kpis.RequestsPerMinute);
        Assert.Equal(0.5, kpis.PrefixCacheHitRate);
        Assert.Equal(0.75, kpis.SpecDecodeAcceptanceRate);
        Assert.NotNull(kpis.TimeToFirstToken);
        Assert.True(double.IsNaN(kpis.PreemptionsPerMinute));
    }

    [Fact]
    public void Sglang_AFrozenThroughput_ReadsAsTheCounterRate_SoAStalledModelIsNotHidden()
    {
        var before = PrometheusText.Parse(Sglang(2, 100, 1000, 500, 0, 10, 0));
        var after = PrometheusText.Parse(Sglang(2, 100, 1000, 500, 0, 10, 0));

        var kpis = ModelKpiCalculator.Compute(after, before, 10);

        Assert.Equal(0, kpis.Load.GenerationTokensPerSecond);
        Assert.Null(kpis.SpecDecodeAcceptanceRate);
    }

    [Fact]
    public void Nim_ReadsVllmsFamiliesWithoutThePrefix()
    {
        var before = PrometheusText.Parse("num_requests_running 1\nnum_requests_waiting 0\ngpu_cache_usage_perc 0.1\ngeneration_tokens_total 100\nprompt_tokens_total 10\n");
        var after = PrometheusText.Parse("num_requests_running 3\nnum_requests_waiting 2\ngpu_cache_usage_perc 0.4\ngeneration_tokens_total 300\nprompt_tokens_total 30\n");

        var kpis = ModelKpiCalculator.Compute(after, before, 10);

        Assert.Equal((3d, 2d, 0.4), (kpis.Load.RunningRequests, kpis.Load.WaitingRequests, kpis.Load.KvCacheUsage));
        Assert.Equal(20, kpis.Load.GenerationTokensPerSecond);
    }

    private const string Previous = """
        vllm:num_requests_running{model_name="m1"} 2.0
        vllm:num_requests_waiting{model_name="m1"} 1.0
        vllm:kv_cache_usage_perc{model_name="m1"} 0.2
        vllm:generation_tokens_total{model_name="m1"} 1000.0
        vllm:prompt_tokens_total{model_name="m1"} 500.0
        vllm:request_success_total{model_name="m1",finished_reason="stop"} 10.0
        vllm:request_success_total{model_name="m1",finished_reason="length"} 2.0
        vllm:prefix_cache_hits_total{model_name="m1"} 40.0
        vllm:prefix_cache_queries_total{model_name="m1"} 50.0
        vllm:num_preemptions_total{model_name="m1"} 3.0
        vllm:tool_call_parser_invocations_total{model_name="m1",outcome="tool_call"} 5.0
        vllm:tool_call_parser_invocations_total{model_name="m1",outcome="no_tool_call"} 15.0
        vllm:time_to_first_token_seconds_bucket{model_name="m1",le="0.1"} 1.0
        vllm:time_to_first_token_seconds_bucket{model_name="m1",le="0.5"} 4.0
        vllm:time_to_first_token_seconds_bucket{model_name="m1",le="1.0"} 9.0
        vllm:time_to_first_token_seconds_bucket{model_name="m1",le="+Inf"} 10.0
        vllm:time_to_first_token_seconds_sum{model_name="m1"} 3.0
        vllm:time_to_first_token_seconds_count{model_name="m1"} 10.0
        """;

    private const string Current = """
        vllm:num_requests_running{model_name="m1"} 5.0
        vllm:num_requests_waiting{model_name="m1"} 0.0
        vllm:kv_cache_usage_perc{model_name="m1"} 0.35
        vllm:generation_tokens_total{model_name="m1"} 2200.0
        vllm:prompt_tokens_total{model_name="m1"} 1100.0
        vllm:request_success_total{model_name="m1",finished_reason="stop"} 15.0
        vllm:request_success_total{model_name="m1",finished_reason="length"} 3.0
        vllm:prefix_cache_hits_total{model_name="m1"} 48.0
        vllm:prefix_cache_queries_total{model_name="m1"} 60.0
        vllm:num_preemptions_total{model_name="m1"} 4.0
        vllm:tool_call_parser_invocations_total{model_name="m1",outcome="tool_call"} 8.0
        vllm:tool_call_parser_invocations_total{model_name="m1",outcome="no_tool_call"} 22.0
        vllm:time_to_first_token_seconds_bucket{model_name="m1",le="0.1"} 1.0
        vllm:time_to_first_token_seconds_bucket{model_name="m1",le="0.5"} 6.0
        vllm:time_to_first_token_seconds_bucket{model_name="m1",le="1.0"} 17.0
        vllm:time_to_first_token_seconds_bucket{model_name="m1",le="+Inf"} 20.0
        vllm:time_to_first_token_seconds_sum{model_name="m1"} 8.0
        vllm:time_to_first_token_seconds_count{model_name="m1"} 20.0
        """;

    private const double ElapsedSeconds = 60.0;

    [Fact]
    public void Compute_Load_ReadsGaugesDirectly_AndRatesFromCounterDeltas()
    {
        var kpis = ModelKpiCalculator.Compute(Parse(Current), Parse(Previous), ElapsedSeconds);

        Assert.Equal(5.0, kpis.Load.RunningRequests);
        Assert.Equal(0.0, kpis.Load.WaitingRequests);
        Assert.Equal(0.35, kpis.Load.KvCacheUsage);
        Assert.Equal(20.0, kpis.Load.GenerationTokensPerSecond); // (2200-1000)/60
        Assert.Equal(10.0, kpis.Load.PromptTokensPerSecond); // (1100-500)/60
    }

    [Fact]
    public void Compute_RequestsPerMinute_FromDeltaRequestSuccessAcrossAllReasons()
    {
        var kpis = ModelKpiCalculator.Compute(Parse(Current), Parse(Previous), ElapsedSeconds);

        // stop: 15-10=5, length: 3-2=1 -> total delta 6 over 60s -> 6/min.
        Assert.Equal(6.0, kpis.RequestsPerMinute);
    }

    [Fact]
    public void Compute_FinishedByReasonPerMinute_PerReason()
    {
        var kpis = ModelKpiCalculator.Compute(Parse(Current), Parse(Previous), ElapsedSeconds);

        Assert.Equal(5.0, kpis.FinishedByReasonPerMinute["stop"]);
        Assert.Equal(1.0, kpis.FinishedByReasonPerMinute["length"]);
    }

    [Fact]
    public void Compute_PrefixCacheHitRate_FromDeltaHitsOverDeltaQueries()
    {
        var kpis = ModelKpiCalculator.Compute(Parse(Current), Parse(Previous), ElapsedSeconds);

        // hits delta 8, queries delta 10 -> 0.8.
        Assert.Equal(0.8, kpis.PrefixCacheHitRate);
    }

    [Fact]
    public void Compute_PrefixCacheHitRate_Null_WhenNoQueryDelta()
    {
        const string previous = "vllm:prefix_cache_hits_total 10.0\nvllm:prefix_cache_queries_total 20.0\n";
        const string current = "vllm:prefix_cache_hits_total 10.0\nvllm:prefix_cache_queries_total 20.0\n";

        var kpis = ModelKpiCalculator.Compute(Parse(current), Parse(previous), ElapsedSeconds);

        Assert.Null(kpis.PrefixCacheHitRate);
    }

    [Fact]
    public void Compute_SpecDecodeAcceptanceRate_Null_WhenMetricAbsent()
    {
        var kpis = ModelKpiCalculator.Compute(Parse(Current), Parse(Previous), ElapsedSeconds);

        Assert.Null(kpis.SpecDecodeAcceptanceRate);
    }

    [Fact]
    public void Compute_SpecDecodeAcceptanceRate_FromDeltaAcceptedOverDeltaDraft()
    {
        const string previous = "vllm:spec_decode_num_accepted_tokens_total 30.0\nvllm:spec_decode_num_draft_tokens_total 100.0\n";
        const string current = "vllm:spec_decode_num_accepted_tokens_total 90.0\nvllm:spec_decode_num_draft_tokens_total 300.0\n";

        var kpis = ModelKpiCalculator.Compute(Parse(current), Parse(previous), ElapsedSeconds);

        Assert.Equal(0.3, kpis.SpecDecodeAcceptanceRate); // (90-30)/(300-100) = 60/200
    }

    [Fact]
    public void Compute_SpecDecodeAcceptanceRate_Null_WhenDeltaDraftIsZero()
    {
        const string previous = "vllm:spec_decode_num_accepted_tokens_total 30.0\nvllm:spec_decode_num_draft_tokens_total 100.0\n";
        const string current = "vllm:spec_decode_num_accepted_tokens_total 30.0\nvllm:spec_decode_num_draft_tokens_total 100.0\n";

        var kpis = ModelKpiCalculator.Compute(Parse(current), Parse(previous), ElapsedSeconds);

        Assert.Null(kpis.SpecDecodeAcceptanceRate);
    }

    [Fact]
    public void Compute_PreemptionsAndToolCallsPerMinute_FromCounterDeltas()
    {
        var kpis = ModelKpiCalculator.Compute(Parse(Current), Parse(Previous), ElapsedSeconds);

        Assert.Equal(1.0, kpis.PreemptionsPerMinute); // (4-3)/60*60
        Assert.Equal(10.0, kpis.ToolCallsPerMinute); // (30-20)/60*60, summed over outcome labels
    }

    [Fact]
    public void Compute_TimeToFirstToken_QuantilesFromDeltaHistogram()
    {
        var kpis = ModelKpiCalculator.Compute(Parse(Current), Parse(Previous), ElapsedSeconds);

        Assert.NotNull(kpis.TimeToFirstToken);
        Assert.Equal(0.75, kpis.TimeToFirstToken!.P50);
        Assert.Equal(1.0, kpis.TimeToFirstToken.P95);
    }

    [Fact]
    public void Compute_Latency_Null_WhenNoPreviousScrape()
    {
        var kpis = ModelKpiCalculator.Compute(Parse(Current), previous: null, elapsedSeconds: 0);

        Assert.Null(kpis.TimeToFirstToken);
        Assert.Null(kpis.InterTokenLatency);
        Assert.Null(kpis.EndToEndLatency);
        Assert.Null(kpis.QueueTime);
        Assert.Null(kpis.PrefixCacheHitRate);
        Assert.Null(kpis.SpecDecodeAcceptanceRate);
        Assert.Equal(0.0, kpis.RequestsPerMinute);
        Assert.Equal(0.0, kpis.Load.GenerationTokensPerSecond);
        Assert.Empty(kpis.FinishedByReasonPerMinute);
    }

    [Fact]
    public void Compute_Latency_Null_WhenDeltaCountIsZero()
    {
        const string text = """
            vllm:time_to_first_token_seconds_bucket{le="1.0"} 5.0
            vllm:time_to_first_token_seconds_bucket{le="+Inf"} 5.0
            vllm:time_to_first_token_seconds_sum 1.0
            vllm:time_to_first_token_seconds_count 5.0
            """;

        var kpis = ModelKpiCalculator.Compute(Parse(text), Parse(text), ElapsedSeconds); // identical scrapes -> no new observations

        Assert.Null(kpis.TimeToFirstToken);
    }

    [Fact]
    public void Compute_CounterReset_TreatsCurrentValueAsTheDelta_NotNegative()
    {
        const string previous = "vllm:generation_tokens_total 1000.0\nvllm:prompt_tokens_total 500.0\n";
        const string current = "vllm:generation_tokens_total 200.0\nvllm:prompt_tokens_total 50.0\n"; // container restarted

        var kpis = ModelKpiCalculator.Compute(Parse(current), Parse(previous), ElapsedSeconds);

        Assert.Equal(200.0 / ElapsedSeconds, kpis.Load.GenerationTokensPerSecond);
        Assert.Equal(50.0 / ElapsedSeconds, kpis.Load.PromptTokensPerSecond);
        Assert.True(kpis.Load.GenerationTokensPerSecond >= 0);
    }

    [Fact]
    public void Compute_KvCache_FallsBackToGpuCacheUsage_WhenPrimaryMetricAbsent()
    {
        const string text = "vllm:gpu_cache_usage_perc 0.6\n";

        var kpis = ModelKpiCalculator.Compute(Parse(text), previous: null, elapsedSeconds: 0);

        Assert.Equal(0.6, kpis.Load.KvCacheUsage);
    }

    private static List<PrometheusSample> Parse(string text) => PrometheusText.Parse(text);
}
