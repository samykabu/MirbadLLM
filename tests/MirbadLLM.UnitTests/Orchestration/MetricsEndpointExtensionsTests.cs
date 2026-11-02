using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

namespace MirbadLLM.UnitTests.Orchestration;

public sealed class MetricsEndpointExtensionsTests
{
    private static ModelKpis Kpis(double running = 1, double? prefixHit = 0.5, LatencyStats? ttft = null)
        => new(
            new ModelMetrics(running, 0, 0.4, 12.5, 3.5),
            RequestsPerMinute: 6,
            TimeToFirstToken: ttft ?? new LatencyStats(0.2, 0.8),
            InterTokenLatency: null,
            EndToEndLatency: new LatencyStats(1.0, 2.0),
            QueueTime: null,
            PrefixCacheHitRate: prefixHit,
            SpecDecodeAcceptanceRate: null,
            PreemptionsPerMinute: 0,
            FinishedByReasonPerMinute: new Dictionary<string, double>(),
            ToolCallsPerMinute: 0);

    [Fact]
    public void Render_EmitsHostMemoryAndGpuGauges()
    {
        var host = new HostSample(DateTimeOffset.UtcNow, new MemorySnapshot(TotalGiB: 128, AvailableGiB: 64), new GpuSnapshot(50, 12.3, 45, 2000));

        string text = MetricsEndpointExtensions.Render([], new Dictionary<string, ModelStatus>(), new Dictionary<string, ModelKpis>(), host, null);

        Assert.Contains("# HELP mirbadllm_memory_total_bytes", text);
        Assert.Contains("# TYPE mirbadllm_memory_total_bytes gauge", text);
        Assert.Contains($"mirbadllm_memory_total_bytes {128.0 * 1024 * 1024 * 1024}", text);
        Assert.Contains("mirbadllm_gpu_utilization_percent 50", text);
        Assert.Contains("mirbadllm_gpu_power_watts 12.3", text);
        Assert.Contains("mirbadllm_gpu_temperature_celsius 45", text);
    }

    [Fact]
    public void Render_NoHostSample_OmitsHostAndGpuGauges()
    {
        string text = MetricsEndpointExtensions.Render([], new Dictionary<string, ModelStatus>(), new Dictionary<string, ModelKpis>(), null, null);

        Assert.DoesNotContain("mirbadllm_memory_total_bytes", text);
        Assert.DoesNotContain("mirbadllm_gpu_utilization_percent", text);
    }

    [Fact]
    public void Render_ModelUp_IsOneForReady_ZeroOtherwise()
    {
        var models = new List<ModelDefinition>
        {
            new() { Id = "m1", ServedName = "M1", ModelPath = "p" },
            new() { Id = "m2", ServedName = "M2", ModelPath = "p" },
        };
        var statuses = new Dictionary<string, ModelStatus>
        {
            ["m1"] = new ModelStatus("m1", ModelState.Ready),
            ["m2"] = new ModelStatus("m2", ModelState.Stopped),
        };

        string text = MetricsEndpointExtensions.Render(models, statuses, new Dictionary<string, ModelKpis>(), null, null);

        Assert.Contains("""mirbadllm_model_up{model="m1",served_name="M1"} 1""", text);
        Assert.Contains("""mirbadllm_model_up{model="m2",served_name="M2"} 0""", text);
        Assert.Contains("""mirbadllm_model_state{model="m1",state="Ready"} 1""", text);
        Assert.Contains("""mirbadllm_model_state{model="m2",state="Stopped"} 1""", text);
    }

    [Fact]
    public void Render_ModelWithoutLatestKpis_SkipsKpiGauges_ButKeepsUpAndState()
    {
        var models = new List<ModelDefinition> { new() { Id = "m1", ServedName = "M1", ModelPath = "p" } };
        var statuses = new Dictionary<string, ModelStatus> { ["m1"] = new ModelStatus("m1", ModelState.Starting) };

        string text = MetricsEndpointExtensions.Render(models, statuses, new Dictionary<string, ModelKpis>(), null, null);

        Assert.Contains("mirbadllm_model_up", text);
        // No KPI sample line for m1 since it has no history yet.
        var lines = text.Split('\n');
        Assert.DoesNotContain(lines, l => l.StartsWith("mirbadllm_model_generation_tokens_per_second{", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_ModelKpiGauges_UseModelAndServedNameLabels()
    {
        var models = new List<ModelDefinition> { new() { Id = "m1", ServedName = "M1", ModelPath = "p" } };
        var statuses = new Dictionary<string, ModelStatus> { ["m1"] = new ModelStatus("m1", ModelState.Ready) };
        var kpis = new Dictionary<string, ModelKpis> { ["m1"] = Kpis() };

        string text = MetricsEndpointExtensions.Render(models, statuses, kpis, null, null);

        Assert.Contains("""mirbadllm_model_generation_tokens_per_second{model="m1",served_name="M1"} 12.5""", text);
        Assert.Contains("""mirbadllm_model_requests_running{model="m1",served_name="M1"} 1""", text);
        Assert.Contains("""mirbadllm_model_kv_cache_usage_ratio{model="m1",served_name="M1"} 0.4""", text);
        Assert.Contains("""mirbadllm_model_prefix_cache_hit_ratio{model="m1",served_name="M1"} 0.5""", text);
    }

    [Fact]
    public void Render_LatencyQuantiles_EmitP50AndP95Labels()
    {
        var models = new List<ModelDefinition> { new() { Id = "m1", ServedName = "M1", ModelPath = "p" } };
        var statuses = new Dictionary<string, ModelStatus> { ["m1"] = new ModelStatus("m1", ModelState.Ready) };
        var kpis = new Dictionary<string, ModelKpis> { ["m1"] = Kpis(ttft: new LatencyStats(0.1, 0.9)) };

        string text = MetricsEndpointExtensions.Render(models, statuses, kpis, null, null);

        Assert.Contains("""mirbadllm_model_ttft_seconds{model="m1",served_name="M1",quantile="0.5"} 0.1""", text);
        Assert.Contains("""mirbadllm_model_ttft_seconds{model="m1",served_name="M1",quantile="0.95"} 0.9""", text);
    }

    [Fact]
    public void Render_PrefixCacheHitRateNull_SkipsThatSample()
    {
        var models = new List<ModelDefinition> { new() { Id = "m1", ServedName = "M1", ModelPath = "p" } };
        var statuses = new Dictionary<string, ModelStatus> { ["m1"] = new ModelStatus("m1", ModelState.Ready) };
        var kpis = new Dictionary<string, ModelKpis> { ["m1"] = Kpis(prefixHit: null) };

        string text = MetricsEndpointExtensions.Render(models, statuses, kpis, null, null);

        // The HELP/TYPE header for the family is always emitted, but there must be no data line for m1.
        Assert.DoesNotContain("mirbadllm_model_prefix_cache_hit_ratio{", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_RouterStatsAbsent_OmitsRouterGauges()
    {
        string text = MetricsEndpointExtensions.Render([], new Dictionary<string, ModelStatus>(), new Dictionary<string, ModelKpis>(), null, null);

        Assert.DoesNotContain("mirbadllm_router_requests_total", text);
    }

    [Fact]
    public void Render_RouterStatsPresent_EmitsPerServedNameGauges()
    {
        var routerStats = new List<RouterModelStats> { new("M1", TotalRequests: 100, FailedRequests: 2, InFlight: 3) };

        string text = MetricsEndpointExtensions.Render([], new Dictionary<string, ModelStatus>(), new Dictionary<string, ModelKpis>(), null, routerStats);

        Assert.Contains("""mirbadllm_router_requests_total{served_name="M1"} 100""", text);
        Assert.Contains("""mirbadllm_router_requests_failed_total{served_name="M1"} 2""", text);
        Assert.Contains("""mirbadllm_router_requests_in_flight{served_name="M1"} 3""", text);
    }

    [Fact]
    public void Render_EscapesQuotesAndBackslashesInLabelValues()
    {
        var models = new List<ModelDefinition> { new() { Id = "m1", ServedName = """weird"name\here""", ModelPath = "p" } };
        var statuses = new Dictionary<string, ModelStatus> { ["m1"] = new ModelStatus("m1", ModelState.Ready) };

        string text = MetricsEndpointExtensions.Render(models, statuses, new Dictionary<string, ModelKpis>(), null, null);

        // Source value is weird"name\here; backslash and quote must both be backslash-escaped in the label.
        Assert.Contains("served_name=\"weird\\\"name\\\\here\"", text);
    }

    [Fact]
    public void Render_NeverContainsNaN()
    {
        var models = new List<ModelDefinition> { new() { Id = "m1", ServedName = "M1", ModelPath = "p" } };
        var statuses = new Dictionary<string, ModelStatus> { ["m1"] = new ModelStatus("m1", ModelState.Ready) };
        var kpis = new Dictionary<string, ModelKpis> { ["m1"] = Kpis(prefixHit: null) };
        var host = new HostSample(DateTimeOffset.UtcNow, new MemorySnapshot(1, 0), new GpuSnapshot(null, null, null, null));

        string text = MetricsEndpointExtensions.Render(models, statuses, kpis, host, [new RouterModelStats("M1", 0, 0, 0)]);

        Assert.DoesNotContain("NaN", text);
    }

    [Fact]
    public async Task HandleAsync_SetsPrometheusContentType_AndWritesBody()
    {
        var registry = new FakeModelRegistry().Add(new ModelDefinition { Id = "m1", ServedName = "M1", ModelPath = "p" });
        var runtime = new FakeModelRuntime().Set(new ModelStatus("m1", ModelState.Ready));
        var history = new InMemoryMetricsHistory(
            Microsoft.Extensions.Options.Options.Create(new MetricsOptions()),
            new ManualTimeProvider());

        var services = new ServiceCollection();
        services.AddSingleton<IRouterStats>(new FakeRouterStats([new RouterModelStats("M1", 5, 0, 1)]));
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };

        await MetricsEndpointExtensions.HandleAsync(context, registry, runtime, history);

        Assert.Equal("text/plain; version=0.0.4", context.Response.ContentType);

        context.Response.Body.Position = 0;
        string body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("# HELP mirbadllm_model_up", body);
        Assert.Contains("mirbadllm_router_requests_total", body);
    }
}
