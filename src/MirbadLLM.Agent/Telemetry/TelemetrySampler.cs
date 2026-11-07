using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.Agent.Telemetry;

/// <summary>
/// The agent's 5-second sampler (design §8.1), the node-side half of the pre-P1 <c>MetricsSampler</c>: every
/// <see cref="TelemetryOptions.SampleInterval"/> it reads host memory, per-GPU telemetry, GPU processes with owners
/// (hostd) and the <c>/metrics</c> of every Ready model, filtered to the families the master reads
/// (<see cref="MetricFamilyFilter"/>), and appends one <see cref="NodeSample"/> to the ring. KPI math stays on the
/// master (<c>ModelKpiCalculator</c> on consecutive scrapes).
/// <para>Failure isolation as today: one failing or slow model scrape is recorded as that model's error and does not
/// affect the others; a failing GPU or GPU-process read leaves those parts empty/null. Only a failing memory read skips
/// the sample (a <see cref="NodeSample"/> cannot exist without memory, and nothing is invented: the master sees a gap).</para>
/// <para>Scrape key (design-p3p4 §A.8, MN-222): llama-server answers 401 on <c>/metrics</c> without its per-instance key, so
/// a scrape sends <c>Authorization: Bearer {key}</c> when the instance's driver answers one
/// (<see cref="IRuntimeDriver.GetUpstreamApiKeyAsync"/>). Container instances have none: their scrape is unchanged.</para>
/// <para>S5 (plan §3.6): inside the agent container <c>/proc/meminfo</c> must show the host's values (today's controller
/// container already relies on this on the DGX); <c>ProcMeminfoMemoryProbe</c> is used unchanged. Verified against the
/// existing probe fixture only; a capture from the DGX agent container is left to the deployment check (no ssh here).</para>
/// <para>Measured-delta hook (MN-214, design-p3p4 §A.4.3): every stored sample is handed to the registered
/// <see cref="ITelemetrySampleObserver"/>s (the external-VRAM check, <see cref="ExternalGpuUseContributor"/>), so their
/// hysteresis counts samples at the sampler's cadence. An observer that throws is logged and does not affect the sample
/// or the other observers. Without observers (every node before P4) the sampler behaves exactly as before.</para>
/// </summary>
public sealed partial class TelemetrySampler(
    ITelemetryRing ring,
    IMemoryProbe memoryProbe,
    IGpuTelemetryProbe gpuProbe,
    IEnumerable<IRuntimeDriver> drivers,
    TelemetryHttpClient http,
    IOptions<TelemetryOptions> options,
    TimeProvider timeProvider,
    ILogger<TelemetrySampler> logger,
    IGpuProcessProbe? gpuProcesses = null,
    IEnumerable<ITelemetrySampleObserver>? observers = null) : BackgroundService
{
    private readonly TelemetryOptions _options = options.Value;
    private readonly IReadOnlyList<IRuntimeDriver> _drivers = drivers.ToList();
    private readonly IReadOnlyList<ITelemetrySampleObserver> _observers = observers?.ToList() ?? [];

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.SampleInterval, timeProvider);
        do
        {
            try
            {
                await SampleOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPassFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Takes one sample and appends it to the ring. Returns the stored sample, or null when the memory read failed.
    /// Exposed for tests, which drive the sampler without the background loop.
    /// </summary>
    public async Task<NodeSample?> SampleOnceAsync(CancellationToken ct = default)
    {
        var at = timeProvider.GetUtcNow();

        var memoryTask = ReadMemoryAsync(ct);
        var gpuTask = ReadGpusAsync(ct);
        var processesTask = ReadGpuProcessesAsync(ct);
        var modelsTask = ScrapeModelsAsync(ct);
        await Task.WhenAll(memoryTask, gpuTask, processesTask, modelsTask).ConfigureAwait(false);

        var memory = await memoryTask.ConfigureAwait(false);
        if (memory is null)
        {
            return null;
        }

        var gpus = await gpuTask.ConfigureAwait(false);
        var sample = new NodeSample
        {
            Seq = 0,
            At = at,
            Memory = new NodeMemorySnapshot
            {
                Model = _options.MemoryModel ?? (gpus.Any(g => g.MemoryTotalGiB is not null) ? MemoryModel.Discrete : MemoryModel.Unified),
                HostTotalGiB = memory.TotalGiB,
                HostAvailableGiB = memory.AvailableGiB,
                Gpus = gpus
                    .Where(g => g.Uuid is not null)
                    .Select(g => new GpuMemorySnapshot(g.Index, g.Uuid!, g.MemoryTotalGiB, g.MemoryUsedGiB, DiscreteNodeMemoryReader.FreeOf(g)))
                    .ToList(),
            },
            Gpus = gpus,
            GpuProcesses = await processesTask.ConfigureAwait(false),
            Models = await modelsTask.ConfigureAwait(false),
        };

        var stored = ring.Append(sample);
        await NotifyObserversAsync(stored, ct).ConfigureAwait(false);
        return stored;
    }

    private async Task NotifyObserversAsync(NodeSample sample, CancellationToken ct)
    {
        foreach (var observer in _observers)
        {
            try
            {
                await observer.ObserveAsync(sample, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogObserverFailed(logger, ex, observer.GetType().Name);
            }
        }
    }

    private async Task<MemorySnapshot?> ReadMemoryAsync(CancellationToken ct)
    {
        try
        {
            return await memoryProbe.GetAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogMemoryFailed(logger, ex);
            return null;
        }
    }

    private async Task<IReadOnlyList<GpuSample>> ReadGpusAsync(CancellationToken ct)
    {
        try
        {
            var reading = await gpuProbe.ReadAsync(ct).ConfigureAwait(false);
            return reading.Gpus;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogGpuFailed(logger, ex);
            return [];
        }
    }

    private async Task<IReadOnlyList<GpuProcess>?> ReadGpuProcessesAsync(CancellationToken ct)
    {
        if (gpuProcesses is null)
        {
            return null;
        }

        try
        {
            return await gpuProcesses.GetAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogGpuFailed(logger, ex);
            return null;
        }
    }

    /// <summary>Every Ready instance of every driver, scraped in parallel, in model-id order.</summary>
    private async Task<IReadOnlyList<ModelMetricsScrape>> ScrapeModelsAsync(CancellationToken ct)
    {
        var ready = new Dictionary<string, IRuntimeDriver>(StringComparer.Ordinal);
        foreach (var driver in _drivers)
        {
            IReadOnlyList<RuntimeStatus> instances;
            try
            {
                instances = await driver.ListAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogListFailed(logger, ex);
                continue;
            }

            foreach (var instance in instances)
            {
                // Only vLLM and llama.cpp have confirmed metric-family mappings. Other runtimes intentionally leave
                // engine KPIs absent rather than treating similarly named vendor metrics as equivalent.
                if (instance.State == ModelState.Ready && instance.Runtime is RuntimeKind.Vllm or RuntimeKind.LlamaCpp)
                {
                    ready.TryAdd(instance.ModelId, driver);
                }
            }
        }

        var scrapes = await Task.WhenAll(ready
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => ScrapeAsync(kv.Key, kv.Value, ct))).ConfigureAwait(false);
        return scrapes.OfType<ModelMetricsScrape>().ToList();
    }

    /// <summary>One model's scrape; null when it stopped running in between (no upstream any more).</summary>
    private async Task<ModelMetricsScrape?> ScrapeAsync(string modelId, IRuntimeDriver driver, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.ScrapeTimeout);

            var upstream = await driver.GetUpstreamAsync(modelId, timeoutCts.Token).ConfigureAwait(false);
            if (upstream is null)
            {
                return null;
            }

            string? apiKey = await driver.GetUpstreamApiKeyAsync(modelId, timeoutCts.Token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(upstream, "metrics"));
            if (apiKey is { Length: > 0 })
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await http.Client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogScrapeStatus(logger, modelId, (int)response.StatusCode);
                return Failed(modelId, string.Create(CultureInfo.InvariantCulture, $"/metrics answered HTTP {(int)response.StatusCode}."));
            }

            string text = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            return new ModelMetricsScrape { ModelId = modelId, At = timeProvider.GetUtcNow(), Text = MetricFamilyFilter.Filter(text) };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LogScrapeTimedOut(logger, modelId);
            return Failed(modelId, string.Create(CultureInfo.InvariantCulture,
                $"/metrics did not answer within {_options.ScrapeTimeout.TotalSeconds:0.#} s."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogScrapeException(logger, ex, modelId);
            return Failed(modelId, ex.Message);
        }
    }

    private ModelMetricsScrape Failed(string modelId, string error) =>
        new() { ModelId = modelId, At = timeProvider.GetUtcNow(), Error = error };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telemetry pass failed.")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telemetry sample observer {Observer} failed.")]
    private static partial void LogObserverFailed(ILogger logger, Exception exception, string observer);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Host memory could not be read; no telemetry sample this interval.")]
    private static partial void LogMemoryFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "GPU telemetry or GPU processes could not be read.")]
    private static partial void LogGpuFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Runtime instances could not be listed; no model scrapes this interval.")]
    private static partial void LogListFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Metrics scrape for {ModelId} returned HTTP {StatusCode}.")]
    private static partial void LogScrapeStatus(ILogger logger, string modelId, int statusCode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Metrics scrape for {ModelId} timed out.")]
    private static partial void LogScrapeTimedOut(ILogger logger, string modelId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Metrics scrape for {ModelId} failed.")]
    private static partial void LogScrapeException(ILogger logger, Exception exception, string modelId);
}

/// <summary>
/// Sees every sample the <see cref="TelemetrySampler"/> stores, right after it is appended to the ring (MN-214's
/// measured-delta hook): checks that need consecutive samples (<see cref="ExternalGpuUseContributor"/>) observe here
/// instead of running their own probes. Called on the sampler's loop, so it must be quick.
/// </summary>
public interface ITelemetrySampleObserver
{
    /// <summary>Observes <paramref name="sample"/> (as stored, with its sequence number).</summary>
    Task ObserveAsync(NodeSample sample, CancellationToken ct = default);
}

/// <summary>
/// The sampler's own <see cref="HttpClient"/> for loopback/container-network <c>/metrics</c> scrapes (no proxy, no
/// client-wide timeout: each scrape has its own). Owned by DI, disposed with the container.
/// </summary>
public sealed class TelemetryHttpClient : IDisposable
{
    /// <summary>Creates the default client.</summary>
    public TelemetryHttpClient()
        : this(new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
    }

    /// <summary>Creates a client over <paramref name="handler"/> (tests pass a fake).</summary>
    public TelemetryHttpClient(HttpMessageHandler handler)
    {
        Client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>The client.</summary>
    public HttpClient Client { get; }

    /// <inheritdoc />
    public void Dispose() => Client.Dispose();
}
