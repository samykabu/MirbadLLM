using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration;

public static class MetricsEndpointExtensions
{
    private const string ContentType = "text/plain; version=0.0.4";
    private const double BytesPerGiB = 1024.0 * 1024 * 1024;

    // Retention defaults to 60 min; a day is comfortably larger than any configured retention window.
    private static readonly TimeSpan HostLookbackWindow = TimeSpan.FromDays(1);

    /// <summary>Maps <c>GET /metrics</c> (Prometheus text exposition of host, model and router KPIs), gated
    /// by an Inference or Metrics API key once one exists (see <see cref="ApiKeyAccess.CheckMetricsAsync"/>).
    /// Marked anonymous at the ASP.NET Core authorization level — it authenticates itself via API key, not
    /// the admin cookie — and is therefore unaffected by the app's cookie-authorization fallback policy.</summary>
    public static IEndpointRouteBuilder MapMirbadLLMMetrics(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/metrics", HandleAsync)
            .AllowAnonymous()
            .AddEndpointFilter(MetricsApiKeyFilterAsync);
        return endpoints;
    }

    private static async ValueTask<object?> MetricsApiKeyFilterAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var services = http.RequestServices;
        var store = services.GetRequiredService<IApiKeyStore>();
        var legacyApiKey = services.GetRequiredService<IOptions<MirbadLLMOptions>>().Value.ApiKey;

        var presentedKey = TryGetBearerToken(http.Request);
        var result = await ApiKeyAccess.CheckMetricsAsync(store, presentedKey, legacyApiKey, http.RequestAborted).ConfigureAwait(false);

        if (result == ApiKeyAccessResult.Denied)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await http.Response.WriteAsync("Missing or invalid API key.", http.RequestAborted).ConfigureAwait(false);
            return Results.Empty;
        }

        return await next(context).ConfigureAwait(false);
    }

    private static string? TryGetBearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..] : null;
    }

    /// <summary>The route handler; internal so tests can drive it directly against a fabricated <see cref="HttpContext"/>.</summary>
    internal static async Task HandleAsync(HttpContext context, IModelRegistry registry, IModelRuntime runtime, IMetricsHistory history)
    {
        var ct = context.RequestAborted;
        var routerStats = context.RequestServices.GetService<IRouterStats>();

        var models = await registry.ListAsync(ct).ConfigureAwait(false);
        var statuses = await runtime.ListStatusAsync(ct).ConfigureAwait(false);
        var statusById = statuses.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var latestKpis = history.Latest();
        var hostSamples = history.GetHost(HostLookbackWindow);
        var host = hostSamples.Count > 0 ? hostSamples[^1] : null;
        var routerSnapshot = routerStats?.Snapshot();

        string text = Render(models, statusById, latestKpis, host, routerSnapshot);

        context.Response.ContentType = ContentType;
        await context.Response.WriteAsync(text, ct).ConfigureAwait(false);
    }

    /// <summary>Builds the Prometheus text-exposition body. Internal so tests can check it without a live server.</summary>
    internal static string Render(
        IReadOnlyList<ModelDefinition> models,
        IReadOnlyDictionary<string, ModelStatus> statusById,
        IReadOnlyDictionary<string, ModelKpis> latestKpis,
        HostSample? host,
        IReadOnlyList<RouterModelStats>? routerStats)
    {
        var sb = new StringBuilder();

        if (host is not null)
        {
            WriteHeader(sb, "mirbadllm_memory_total_bytes", "gauge", "Total host memory, in bytes.");
            WriteSample(sb, "mirbadllm_memory_total_bytes", null, host.Memory.TotalGiB * BytesPerGiB);

            WriteHeader(sb, "mirbadllm_memory_available_bytes", "gauge", "Available host memory, in bytes.");
            WriteSample(sb, "mirbadllm_memory_available_bytes", null, host.Memory.AvailableGiB * BytesPerGiB);

            if (host.Gpu?.UtilizationPercent is { } utilization)
            {
                WriteHeader(sb, "mirbadllm_gpu_utilization_percent", "gauge", "GPU utilization, percent.");
                WriteSample(sb, "mirbadllm_gpu_utilization_percent", null, utilization);
            }

            if (host.Gpu?.PowerWatts is { } power)
            {
                WriteHeader(sb, "mirbadllm_gpu_power_watts", "gauge", "GPU power draw, watts.");
                WriteSample(sb, "mirbadllm_gpu_power_watts", null, power);
            }

            if (host.Gpu?.TemperatureC is { } temperature)
            {
                WriteHeader(sb, "mirbadllm_gpu_temperature_celsius", "gauge", "GPU temperature, degrees Celsius.");
                WriteSample(sb, "mirbadllm_gpu_temperature_celsius", null, temperature);
            }
        }

        WriteHeader(sb, "mirbadllm_model_up", "gauge", "1 when the model is Ready, 0 otherwise.");
        foreach (var model in models)
        {
            double up = StateOf(statusById, model.Id) == ModelState.Ready ? 1 : 0;
            WriteSample(sb, "mirbadllm_model_up", [("model", model.Id), ("served_name", model.ServedName)], up);
        }

        WriteHeader(sb, "mirbadllm_model_state", "gauge", "1, labelled with the model's current state.");
        foreach (var model in models)
        {
            string state = StateOf(statusById, model.Id).ToString();
            WriteSample(sb, "mirbadllm_model_state", [("model", model.Id), ("state", state)], 1);
        }

        WriteModelGauge(sb, "mirbadllm_model_generation_tokens_per_second", "gauge", "Generation throughput, tokens/second.", models, latestKpis, k => k.Load.GenerationTokensPerSecond);
        WriteModelGauge(sb, "mirbadllm_model_prompt_tokens_per_second", "gauge", "Prompt throughput, tokens/second.", models, latestKpis, k => k.Load.PromptTokensPerSecond);
        WriteModelGauge(sb, "mirbadllm_model_requests_running", "gauge", "Requests currently running.", models, latestKpis, k => k.Load.RunningRequests);
        WriteModelGauge(sb, "mirbadllm_model_requests_waiting", "gauge", "Requests currently waiting.", models, latestKpis, k => k.Load.WaitingRequests);
        WriteModelGauge(sb, "mirbadllm_model_kv_cache_usage_ratio", "gauge", "KV cache usage ratio, 0..1.", models, latestKpis, k => k.Load.KvCacheUsage);

        WriteQuantileGauge(sb, "mirbadllm_model_ttft_seconds", "Time to first token, seconds.", models, latestKpis, k => k.TimeToFirstToken);
        WriteQuantileGauge(sb, "mirbadllm_model_e2e_latency_seconds", "End-to-end request latency, seconds.", models, latestKpis, k => k.EndToEndLatency);

        WriteModelGaugeNullable(sb, "mirbadllm_model_prefix_cache_hit_ratio", "gauge", "Prefix cache hit ratio, 0..1.", models, latestKpis, k => k.PrefixCacheHitRate);

        if (routerStats is not null)
        {
            WriteHeader(sb, "mirbadllm_router_requests_total", "counter", "Total requests routed, per served model.");
            foreach (var stat in routerStats)
            {
                WriteSample(sb, "mirbadllm_router_requests_total", [("served_name", stat.ServedName)], stat.TotalRequests);
            }

            WriteHeader(sb, "mirbadllm_router_requests_failed_total", "counter", "Failed requests, per served model.");
            foreach (var stat in routerStats)
            {
                WriteSample(sb, "mirbadllm_router_requests_failed_total", [("served_name", stat.ServedName)], stat.FailedRequests);
            }

            WriteHeader(sb, "mirbadllm_router_requests_in_flight", "gauge", "Requests currently in flight, per served model.");
            foreach (var stat in routerStats)
            {
                WriteSample(sb, "mirbadllm_router_requests_in_flight", [("served_name", stat.ServedName)], stat.InFlight);
            }
        }

        return sb.ToString();
    }

    private static ModelState StateOf(IReadOnlyDictionary<string, ModelStatus> statusById, string modelId)
        => statusById.TryGetValue(modelId, out var status) ? status.State : ModelState.Stopped;

    private static void WriteModelGauge(
        StringBuilder sb, string name, string type, string help,
        IReadOnlyList<ModelDefinition> models, IReadOnlyDictionary<string, ModelKpis> latestKpis, Func<ModelKpis, double> selector)
    {
        WriteHeader(sb, name, type, help);
        foreach (var model in models)
        {
            if (!latestKpis.TryGetValue(model.Id, out var kpis))
            {
                continue;
            }

            WriteSample(sb, name, [("model", model.Id), ("served_name", model.ServedName)], selector(kpis));
        }
    }

    private static void WriteModelGaugeNullable(
        StringBuilder sb, string name, string type, string help,
        IReadOnlyList<ModelDefinition> models, IReadOnlyDictionary<string, ModelKpis> latestKpis, Func<ModelKpis, double?> selector)
    {
        WriteHeader(sb, name, type, help);
        foreach (var model in models)
        {
            if (!latestKpis.TryGetValue(model.Id, out var kpis) || selector(kpis) is not { } value)
            {
                continue;
            }

            WriteSample(sb, name, [("model", model.Id), ("served_name", model.ServedName)], value);
        }
    }

    private static void WriteQuantileGauge(
        StringBuilder sb, string name, string help,
        IReadOnlyList<ModelDefinition> models, IReadOnlyDictionary<string, ModelKpis> latestKpis, Func<ModelKpis, LatencyStats?> selector)
    {
        WriteHeader(sb, name, "gauge", help);
        foreach (var model in models)
        {
            if (!latestKpis.TryGetValue(model.Id, out var kpis) || selector(kpis) is not { } latency)
            {
                continue;
            }

            WriteSample(sb, name, [("model", model.Id), ("served_name", model.ServedName), ("quantile", "0.5")], latency.P50);
            WriteSample(sb, name, [("model", model.Id), ("served_name", model.ServedName), ("quantile", "0.95")], latency.P95);
        }
    }

    private static void WriteHeader(StringBuilder sb, string name, string type, string help)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
    }

    private static void WriteSample(StringBuilder sb, string name, IReadOnlyList<(string Key, string Value)>? labels, double value)
    {
        if (double.IsNaN(value))
        {
            return;
        }

        sb.Append(name);
        if (labels is { Count: > 0 })
        {
            sb.Append('{');
            for (int i = 0; i < labels.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append(labels[i].Key).Append("=\"").Append(EscapeLabelValue(labels[i].Value)).Append('"');
            }

            sb.Append('}');
        }

        // No format specifier: since .NET Core 3.0 this yields the shortest string that round-trips back
        // to the same double (e.g. "12.3", not "12.300000000000001"), which is what Prometheus consumers expect.
        sb.Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private static string EscapeLabelValue(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
