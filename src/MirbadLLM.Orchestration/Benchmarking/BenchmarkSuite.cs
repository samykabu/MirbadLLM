using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>
/// The deterministic benchmark suite, a C# port of the Phase 0 <c>bench.py</c> semantics: streaming chat
/// completions of exactly <see cref="BenchmarkSuiteOptions.MaxTokens"/> tokens (max_tokens = min_tokens),
/// temperature 0, thinking off; a warm-up request; each concurrency level run
/// <see cref="BenchmarkSuiteOptions.Runs"/> times with the median reported; a long-prompt prefill (cold, then
/// a prefix-cache hit); a tool-call check and objective questions as correctness guardrails. The noise check reads
/// <see cref="IMetricsHistory"/> over each timed run, only for the models that share the measured model's node or GPUs
/// (<see cref="BenchmarkTarget.Scope"/>, MN-112; no scope = every model in the history): their running requests and their
/// request/token counters flag noisy results, and incomplete telemetry reads "noise unknown" (design §6.5 item 4).
/// Pooling (embedding) models get the embeddings benchmark instead (see <see cref="RunEmbeddingsAsync"/>).
/// <para>llama.cpp targets (<see cref="BenchmarkTarget.Runtime"/>, design-p3p4 §A.6, MN-225): the classic chat levels are
/// <see cref="LlamaCppChatLevels"/> capped at the slots (<see cref="ChatLevels"/>); each chat request also sends
/// <c>ignore_eos</c> (llama-server has no <c>min_tokens</c>, so a request could stop early); and llama-server's own
/// <c>llamacpp:*</c> counters (<see cref="LlamaCppMetrics"/>) are read just before and just after every timed run, giving
/// the server's tokens and speeds next to the client numbers (<see cref="ServerCounters"/>) and a direct outside-client
/// check on the measured model (requests already processing, or tokens the benchmark did not send). The workload presets
/// keep their levels (1, 16 and the slots), so tuning candidates with different slot counts are compared at the same
/// N=16; the embeddings suite keeps its levels too. vLLM targets are measured exactly as before (nothing extra is read).</para>
/// Requests go to <see cref="BenchmarkTarget.BaseUri"/> through <paramref name="http"/>, or through the client
/// <paramref name="targets"/> returns for that address (the node's data client to the agent proxy, MN-033).
/// </summary>
public sealed class BenchmarkSuite(
    HttpClient http,
    IMetricsHistory? history,
    BenchmarkSuiteOptions options,
    TimeProvider? timeProvider = null,
    Random? random = null,
    IBenchmarkTargetResolver? targets = null) : IBenchmarkSuite
{
    private readonly Random _random = random ?? Random.Shared;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Fallback when <c>/tokenize</c> is unavailable: measured ~22 tokens per filler line on Qwen tokenizers.</summary>
    private const double DefaultTokensPerLine = 22;

    /// <summary>The client for <paramref name="target"/>: the resolver's for its address, else the suite's own.</summary>
    private HttpClient Http(BenchmarkTarget target) => targets?.GetClient(target.BaseUri) ?? http;

    public int StepCount => 3 + (options.Levels.Count * options.Runs) + (options.IncludePrefill ? 2 : 0);

    /// <summary>The llama.cpp chat levels before the slot cap (design-p3p4 §A.6).</summary>
    public static readonly IReadOnlyList<int> LlamaCppChatLevels = [1, 2, 4, 8];

    /// <summary>
    /// The classic chat levels of <paramref name="target"/>: <see cref="BenchmarkSuiteOptions.Levels"/> for vLLM; for
    /// llama.cpp <see cref="LlamaCppChatLevels"/> below the slots (<see cref="BenchmarkTarget.MaxNumSeqs"/>, i.e.
    /// <c>Parallel</c>) plus the slot count itself, at most 8 (<c>Parallel</c> 3 → 1, 2, 3; 16 → 1, 2, 4, 8).
    /// </summary>
    public IReadOnlyList<int> ChatLevels(BenchmarkTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        // A runtime with a fixed number of slots gets levels up to them; SGLang and NIM queue the rest, and that load is worth measuring.
        if (target.Runtime is not (RuntimeKind.LlamaCpp or RuntimeKind.Mlx))
        {
            return options.Levels;
        }

        int cap = Math.Min(Math.Max(1, target.MaxNumSeqs), LlamaCppChatLevels[^1]);
        return [.. LlamaCppChatLevels.Where(l => l < cap).Append(cap)];
    }

    /// <summary>
    /// The suite's target for <paramref name="settings"/> (the definition the instance runs) behind <paramref name="baseUri"/>:
    /// vLLM exactly as before (max model len, max num seqs); a llama.cpp model with its per-slot context
    /// (<see cref="LlamaCppSettings.ContextSize"/>), its slots (<see cref="LlamaCppSettings.Parallel"/>) and
    /// <see cref="BenchmarkTarget.Runtime"/> set (MN-225).
    /// </summary>
    public static BenchmarkTarget TargetFor(string modelId, ModelDefinition settings, Uri baseUri, TuningGoal? preset, int? repetitions)
    {
        ArgumentNullException.ThrowIfNull(settings);
        int slots = settings.Runtime switch
        {
            RuntimeKind.LlamaCpp when settings.LlamaCpp is { } llama => llama.Parallel,
            RuntimeKind.Mlx when settings.Mlx is { } mlx => mlx.KvBits is null ? mlx.Parallel : 1, // a quantized KV cache makes it serial
            _ => settings.MaxNumSeqs,
        };
        return new BenchmarkTarget(modelId, settings.ServedName, baseUri, ModelCapabilities.MaxModelLen(settings), PoolingDetection.Detect(settings), slots, preset, repetitions)
        {
            Runtime = settings.Runtime,
            Thinks = NoThinking.Offered(settings),
        };
    }

    public async Task<BenchmarkResult> RunAsync(BenchmarkTarget target, IProgress<BenchmarkProgress>? progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        switch (target.Pooling)
        {
            case PoolingMode.Reranker:
                throw new NotSupportedException(RerankerUnsupported);
            case PoolingMode.Embedding:
                return await RunEmbeddingsAsync(target, progress, ct).ConfigureAwait(false);
        }

        if (target.Preset == TuningGoal.Embeddings)
        {
            return await RunEmbeddingsAsync(target, progress, ct).ConfigureAwait(false);
        }

        if (target.Preset is { } preset && WorkloadProfiles.IsPreset(preset))
        {
            return await RunWorkloadsAsync(target, WorkloadProfiles.For(preset), progress, ct).ConfigureAwait(false);
        }

        var chatLevels = ChatLevels(target);
        int totalSteps = 3 + (chatLevels.Count * options.Runs) + (options.IncludePrefill ? 2 : 0) + (target.Thinks ? 1 : 0);
        int step = 0;
        void Report(string phase) => progress?.Report(new BenchmarkProgress(phase, ++step, totalSteps));

        Report("Tool-call check");
        var (toolValid, toolDetail) = await ToolCheckAsync(target, ct).ConfigureAwait(false);

        Report("Warm-up request");
        await StreamOnceAsync(target, BenchmarkPrompts.Stories[0], options.MaxTokens, ct).ConfigureAwait(false);

        var windows = new List<RunWindow>();
        var levels = new List<LevelResult>(chatLevels.Count);
        foreach (int concurrency in chatLevels)
        {
            var runs = new List<RunMeasurement>(options.Runs);
            for (int run = 1; run <= options.Runs; run++)
            {
                Report(string.Create(CultureInfo.InvariantCulture, $"N={concurrency}, run {run} of {options.Runs}"));
                runs.Add(await RunLevelAsync(target, concurrency, windows, ct).ConfigureAwait(false));
            }

            levels.Add(Summarize(concurrency, runs));
        }

        PrefillResult? prefill = null;
        if (options.IncludePrefill)
        {
            prefill = await PrefillAsync(target, () => Report("Long prompt, cold"), () => Report("Long prompt, prefix-cache hit"), ct)
                .ConfigureAwait(false);
        }

        Report("Objective questions");
        var answers = await QaAsync(target, ct).ConfigureAwait(false);
        var thinking = await ThinkingIfReasoningAsync(target, Report, ct).ConfigureAwait(false);

        var guardrails = new GuardrailResult(toolValid, toolDetail, answers.Count(a => a.Correct), answers.Count, answers);
        var noise = await EvaluateNoiseAsync(target, windows, ct).ConfigureAwait(false);
        var measured = ApplyNoise(levels, noise);
        return new BenchmarkResult(measured, prefill, guardrails, BuildNoise(target, measured.SelectMany(l => l.Runs).Select(r => r.SameModelRunningMax - r.Concurrency), noise,
            measured.SelectMany(l => l.Runs).Select(r => r.Server))) { SpecAcceptanceRate = AcceptanceDuring(target, windows), Thinking = thinking };
    }

    /// <summary>Per-metric median across repetitions; the level is noisy when any repetition was.</summary>
    public static LevelResult Summarize(int concurrency, IReadOnlyList<RunMeasurement> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        return new LevelResult(
            concurrency,
            runs,
            Median(runs.Select(r => r.PerRequestTokS)),
            Median(runs.Select(r => r.AggregateTokS)),
            Median(runs.Select(r => r.TtftP50)),
            Median(runs.Select(r => r.TtftP95)),
            runs.Any(r => IsNoisy(r)))
        {
            Server = ServerCounters.Median(runs.Select(r => r.Server)),
        };
    }

    /// <summary>
    /// A noisy run: another sharing model was busy or served, outside clients ran on the measured model (its running count
    /// above the run's own), or llama-server's own counters saw another client (<see cref="ServerCounters.OutsideClients"/>).
    /// </summary>
    public static bool IsNoisy(RunMeasurement run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.OtherModelsRunningMax > 0 || run.OtherModelsServed || run.SameModelRunningMax > run.Concurrency || run.Server?.OutsideClients == true;
    }

    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
        {
            return double.NaN;
        }

        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>Nearest-rank percentile (p in 0..100) of an already-populated list.</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return double.NaN;
        }

        var sorted = values.Order().ToList();
        int rank = (int)Math.Ceiling(p / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    /// <summary>
    /// The noisy and unknown reasons of a run (design §6.5 item 4): other models sharing the node (or the GPUs) were busy
    /// (running requests sampled) or served requests (their counters grew), outside clients used the measured model, and
    /// the telemetry gaps that make the verdict "noise unknown".
    /// </summary>
    /// <param name="sameExtra">Per timed run, the measured model's running requests above what the run itself sent.</param>
    /// <param name="server">Per timed run, llama-server's own counters (llama.cpp); null entries for vLLM.</param>
    private static NoiseReport BuildNoise(BenchmarkTarget target, IEnumerable<int> sameExtra, NoiseEvaluation noise, IEnumerable<ServerCounters?>? server = null)
    {
        var reasons = new List<string>();
        var busy = noise.OtherMax.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        if (busy.Count > 0)
        {
            reasons.Add("noisy: other models were busy (" +
                string.Join(", ", busy.Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key} up to {kv.Value} running"))) + ")");
        }

        var served = noise.Served.Where(id => noise.OtherMax.GetValueOrDefault(id) <= 0).Order(StringComparer.Ordinal).ToList();
        if (served.Count > 0)
        {
            reasons.Add($"noisy: {Sharing(target.Scope)} served requests during the runs ({string.Join(", ", served)})");
        }

        int extra = sameExtra.DefaultIfEmpty(0).Max();
        if (extra > 0)
        {
            reasons.Add(string.Create(CultureInfo.InvariantCulture, $"noisy: up to {extra} requests from other clients ran on this model"));
        }

        var outside = (server ?? []).OfType<ServerCounters>().Where(c => c.OutsideClients).ToList();
        if (outside.Count > 0)
        {
            int processing = outside.Max(c => c.ProcessingAtStart);
            double excess = outside.Max(c => c.ExcessTokens);
            reasons.Add(string.Create(CultureInfo.InvariantCulture,
                $"noisy: llama-server's own counters show other clients on this model in {outside.Count} run(s) (up to {processing} request(s) already processing when a run started, up to {excess:0} tokens the benchmark did not send)"));
        }

        return new NoiseReport(reasons.Count > 0, reasons, null, noise.OtherMax)
        {
            Unknown = noise.Unknown.Count > 0 ? noise.Unknown : null,
            Considered = noise.Considered is { Count: > 0 } considered ? considered : null,
        };
    }

    private static string Sharing(NoiseScope? scope) =>
        scope is null ? "other models" : scope.PerGpu ? "other models sharing its GPUs" : $"other models on node {scope.NodeId}";

    private async Task<RunMeasurement> RunLevelAsync(BenchmarkTarget target, int concurrency, List<RunWindow> windows, CancellationToken ct)
    {
        var before = await ReadServerAsync(target, ct).ConfigureAwait(false);
        var startedAt = _time.GetUtcNow();
        long start = Stopwatch.GetTimestamp();
        var tasks = Enumerable.Range(0, concurrency)
            .Select(i => StreamOnceAsync(target, BenchmarkPrompts.Stories[i % BenchmarkPrompts.Stories.Count], options.MaxTokens, ct))
            .ToList();
        var samples = await Task.WhenAll(tasks).ConfigureAwait(false);
        double wall = Stopwatch.GetElapsedTime(start).TotalSeconds;
        windows.Add(new RunWindow(startedAt, _time.GetUtcNow()));

        int tokens = samples.Sum(s => s.CompletionTokens);
        var ttfts = samples.Select(s => s.TtftSeconds).ToList();
        var server = ServerCounters.Between(before, await ReadServerAsync(target, ct).ConfigureAwait(false), wall,
            Reported(samples.Sum(s => s.PromptTokens)), Reported(tokens), concurrency);

        // Contention is filled in by ApplyNoise once the telemetry of every run is in.
        return new RunMeasurement(
            concurrency,
            tokens,
            wall,
            samples.Average(s => s.CompletionTokens / Math.Max(s.TotalSeconds - s.TtftSeconds, 1e-6)),
            wall > 0 ? tokens / wall : 0,
            Percentile(ttfts, 50),
            Percentile(ttfts, 95),
            0,
            0)
        {
            Server = server,
        };
    }

    // ---------------------------------------------------------------- noise (design §6.5 item 4, MN-112)

    /// <summary>The wall-clock span of one timed run (requests fired together until the last one finished).</summary>
    /// <param name="PerRun">False for a span that counts for the verdict only (the long-prompt and shared-prefix workloads).</param>
    private readonly record struct RunWindow(DateTimeOffset Start, DateTimeOffset End, bool PerRun = true);

    /// <summary>What the telemetry says about one timed run.</summary>
    /// <param name="OtherMax">Highest running requests of another sharing model.</param>
    /// <param name="SameMax">Highest running requests of the measured model.</param>
    /// <param name="OtherServed">Another sharing model's request or token counters grew.</param>
    private readonly record struct WindowNoise(int OtherMax, int SameMax, bool OtherServed);

    /// <summary>The noise of every timed run, in run order, and what it adds up to.</summary>
    private sealed record NoiseEvaluation(
        IReadOnlyList<WindowNoise> Windows,
        Dictionary<string, int> OtherMax,
        IReadOnlyCollection<string> Served,
        IReadOnlyList<string> Unknown,
        IReadOnlyList<string>? Considered);

    /// <summary>
    /// Reads the metrics history over every timed run (design §6.5 item 4). Only the models of
    /// <see cref="BenchmarkTarget.Scope"/> are looked at (no scope: every model in the history, the P1 rule). Per run and
    /// model: the highest sampled <c>num_requests_running</c>, and whether its request/token counters grew (the rates of
    /// the scrapes covering the run are counter deltas, so a burst between two samples is seen). Telemetry is complete for
    /// a run when the model has a scrape at or before its start and one at or after its end, no gap above
    /// <see cref="BenchmarkSuiteOptions.TelemetryMaxGap"/> between them, and each of them carries rates; the measured model
    /// must be sampled through every level run too (its running count shows outside clients). Incomplete telemetry is
    /// "noise unknown", never quiet.
    /// With a scope, the suite first waits up to <see cref="BenchmarkSuiteOptions.TelemetrySettleTimeout"/> for the next
    /// scrape of each live model after the last run, so the tail of that run is covered.
    /// </summary>
    private async Task<NoiseEvaluation> EvaluateNoiseAsync(BenchmarkTarget target, List<RunWindow> windows, CancellationToken ct)
    {
        var scope = target.Scope;
        var considered = scope?.SharedWith.Where(id => !string.Equals(id, target.ModelId, StringComparison.Ordinal)).ToList();
        if (windows.Count == 0)
        {
            return new NoiseEvaluation([], new Dictionary<string, int>(StringComparer.Ordinal), [], [], considered);
        }

        if (history is null)
        {
            return new NoiseEvaluation([.. windows.Where(w => w.PerRun).Select(_ => default(WindowNoise))], new Dictionary<string, int>(StringComparer.Ordinal), [],
                ["noise unknown: no metrics history to check the runs against"], considered);
        }

        var first = windows.Min(w => w.Start);
        var last = windows.Max(w => w.End);
        var latest = history.Latest();
        var others = (considered ?? [.. latest.Keys.Where(id => !string.Equals(id, target.ModelId, StringComparison.Ordinal))])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (scope is not null)
        {
            await SettleAsync([target.ModelId, .. others], last, ct).ConfigureAwait(false);
        }

        var span = _time.GetUtcNow() - first + options.TelemetryMaxGap + TimeSpan.FromSeconds(6);
        var self = Ordered(history.GetModel(target.ModelId, span));
        var samples = others.ToDictionary(id => id, id => Ordered(history.GetModel(id, span)), StringComparer.Ordinal);

        // A sharing model counts when it was serving when the scope was taken or shows up in the history around the runs;
        // one that never ran is not noise. Without a scope every model in the history counts (the P1 rule).
        var relevant = others.Where(id =>
            scope is null ||
            scope.Running.Contains(id, StringComparer.Ordinal) ||
            latest.ContainsKey(id) ||
            samples[id].Any(s => s.At >= first - options.TelemetryMaxGap)).ToList();

        var otherMax = relevant.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var served = new HashSet<string>(StringComparer.Ordinal);
        var incomplete = new SortedSet<string>(StringComparer.Ordinal);
        bool selfIncomplete = false;
        var result = new List<WindowNoise>(windows.Count);
        foreach (var window in windows)
        {
            // Outside clients on the measured model are judged on the level runs only (the other workloads vary its load).
            int same = window.PerRun ? RunningMax(self, window) : 0;
            selfIncomplete |= window.PerRun && !Sampled(self, window);
            int other = 0;
            bool otherServed = false;
            foreach (string id in relevant)
            {
                var series = samples[id];
                int max = RunningMax(series, window);
                other = Math.Max(other, max);
                otherMax[id] = Math.Max(otherMax[id], max);
                if (Served(series, window))
                {
                    served.Add(id);
                    otherServed = true;
                }

                if (!Covered(series, window))
                {
                    incomplete.Add(id);
                }
            }

            if (window.PerRun)
            {
                result.Add(new WindowNoise(other, same, otherServed));
            }
        }

        var unknown = new List<string>();
        if (selfIncomplete)
        {
            unknown.Add(target.Runtime == RuntimeKind.Mlx
                ? "noise unknown: MLX has no metrics, so requests from other clients can't be seen"
                : $"noise unknown: the metrics of {target.ModelId} do not cover every run");
        }

        if (incomplete.Count > 0)
        {
            unknown.Add($"noise unknown: incomplete telemetry for {string.Join(", ", incomplete)} during the runs");
        }

        return new NoiseEvaluation(result, otherMax, served, unknown, considered);
    }

    /// <summary>
    /// Waits (bounded) until every model in <paramref name="ids"/> whose sampler is live (a scrape within
    /// <see cref="BenchmarkSuiteOptions.TelemetryMaxGap"/> of <paramref name="lastEnd"/>) has a scrape at or after it. A model
    /// with stale or no telemetry is not waited for: it reads "noise unknown" at once.
    /// </summary>
    private async Task SettleAsync(IReadOnlyList<string> ids, DateTimeOffset lastEnd, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + options.TelemetrySettleTimeout;
        while (_time.GetUtcNow() < deadline)
        {
            var window = _time.GetUtcNow() - lastEnd + options.TelemetryMaxGap + TimeSpan.FromSeconds(1);
            bool waiting = ids.Any(id =>
                history!.GetModel(id, window).Select(s => s.At).DefaultIfEmpty(DateTimeOffset.MinValue).Max() is var newest &&
                newest < lastEnd && lastEnd - newest <= options.TelemetryMaxGap);
            if (!waiting)
            {
                return;
            }

            await Task.Delay(options.TelemetrySettlePoll, _time, ct).ConfigureAwait(false);
        }
    }

    private static List<ModelSample> Ordered(IReadOnlyList<ModelSample> samples) => [.. samples.OrderBy(s => s.At)];

    /// <summary>The scrape after the run ended (the one whose rates cover its tail); null when there is none yet.</summary>
    private static ModelSample? After(List<ModelSample> samples, RunWindow window) => samples.FirstOrDefault(s => s.At >= window.End);

    /// <summary>Highest sampled running requests from 5 s before the run until the scrape after it (or now).</summary>
    private static int RunningMax(List<ModelSample> samples, RunWindow window)
    {
        var until = After(samples, window)?.At ?? DateTimeOffset.MaxValue;
        return samples
            .Where(s => s.At >= window.Start - TimeSpan.FromSeconds(5) && s.At <= until)
            .Select(s => (int)Math.Round(s.Kpis.Load.RunningRequests))
            .DefaultIfEmpty(0)
            .Max();
    }

    /// <summary>
    /// True when a scrape whose rates overlap the run (after its start, up to the scrape after its end) saw finished
    /// requests or prompt/generation tokens: the counters grew while the run was measured.
    /// </summary>
    private static bool Served(List<ModelSample> samples, RunWindow window)
    {
        var until = After(samples, window)?.At ?? DateTimeOffset.MaxValue;
        return samples.Any(s =>
            s.At > window.Start && s.At <= until && s.HasRates &&
            (s.Kpis.RequestsPerMinute > 0 || s.Kpis.Load.GenerationTokensPerSecond > 0 || s.Kpis.Load.PromptTokensPerSecond > 0));
    }

    /// <summary>
    /// True when the scrapes cover the whole run: one at or before its start, one at or after its end, no gap above
    /// <see cref="BenchmarkSuiteOptions.TelemetryMaxGap"/> in between, and every scrape after the first carrying rates
    /// (their counter deltas span the run).
    /// </summary>
    private bool Covered(List<ModelSample> samples, RunWindow window)
    {
        int from = samples.FindLastIndex(s => s.At <= window.Start);
        int to = samples.FindIndex(s => s.At >= window.End);
        if (from < 0 || to < 0 || window.Start - samples[from].At > options.TelemetryMaxGap)
        {
            return false;
        }

        for (int i = from + 1; i <= to; i++)
        {
            if (!samples[i].HasRates || samples[i].At - samples[i - 1].At > options.TelemetryMaxGap)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// For the measured model only its running count matters (outside clients): true when its scrapes sample the whole
    /// run, the first within <see cref="BenchmarkSuiteOptions.TelemetryMaxGap"/> of the start, one at or after the end, no
    /// longer gap between. A scrape before the start is not needed (a tuning candidate may have just started).
    /// </summary>
    private bool Sampled(List<ModelSample> samples, RunWindow window)
    {
        int from = samples.FindIndex(s => s.At >= window.Start - options.TelemetryMaxGap);
        int to = samples.FindIndex(s => s.At >= window.End);
        if (from < 0 || to < 0 || samples[from].At - window.Start > options.TelemetryMaxGap)
        {
            return false;
        }

        for (int i = from + 1; i <= to; i++)
        {
            if (samples[i].At - samples[i - 1].At > options.TelemetryMaxGap)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Puts each run's contention (in run order) on the levels, and marks a level noisy when one of its runs was.</summary>
    private static List<LevelResult> ApplyNoise(List<LevelResult> levels, NoiseEvaluation noise)
    {
        int index = 0;
        var result = new List<LevelResult>(levels.Count);
        foreach (var level in levels)
        {
            var runs = new List<RunMeasurement>(level.Runs.Count);
            foreach (var run in level.Runs)
            {
                var w = index < noise.Windows.Count ? noise.Windows[index] : default;
                index++;
                runs.Add(run with { OtherModelsRunningMax = w.OtherMax, SameModelRunningMax = w.SameMax, OtherModelsServed = w.OtherServed });
            }

            result.Add(level with { Runs = runs, Noisy = runs.Any(IsNoisy) });
        }

        return result;
    }

    /// <summary>As <see cref="ApplyNoise(List{LevelResult}, NoiseEvaluation)"/> for the embeddings levels.</summary>
    private static List<EmbeddingLevelResult> ApplyNoise(List<EmbeddingLevelResult> levels, NoiseEvaluation noise)
    {
        int index = 0;
        var result = new List<EmbeddingLevelResult>(levels.Count);
        foreach (var level in levels)
        {
            var runs = new List<EmbeddingRunMeasurement>(level.Runs.Count);
            foreach (var run in level.Runs)
            {
                var w = index < noise.Windows.Count ? noise.Windows[index] : default;
                index++;
                runs.Add(run with { OtherModelsRunningMax = w.OtherMax, SameModelRunningMax = w.SameMax, OtherModelsServed = w.OtherServed });
            }

            result.Add(level with { Runs = runs, Noisy = runs.Any(IsNoisy) });
        }

        return result;
    }

    private static bool IsNoisy(EmbeddingRunMeasurement run) =>
        run.OtherModelsRunningMax > 0 || run.OtherModelsServed || run.SameModelRunningMax > run.Concurrency * run.Inputs || run.Server?.OutsideClients == true;

    private async Task<PrefillResult?> PrefillAsync(BenchmarkTarget target, Action reportCold, Action reportCached, CancellationToken ct)
    {
        // Room for the chat template and the one generated token.
        int targetTokens = Math.Min(options.PrefillTokens, target.MaxModelLen - 64);
        if (targetTokens < 1024)
        {
            reportCold();
            reportCached();
            return null;
        }

        double tokensPerLine = await CalibrateTokensPerLineAsync(target, ct).ConfigureAwait(false);
        int lines = (int)Math.Floor(targetTokens * 0.98 / tokensPerLine);

        // Later lines carry longer numbers than the calibration sample: count the real prompt once and rescale.
        string unique = Guid.NewGuid().ToString("N");
        if (await CountTokensAsync(target, BenchmarkPrompts.LongPrompt(unique, lines), ct).ConfigureAwait(false) is { } counted && counted > 0)
        {
            lines = (int)Math.Floor(lines * (targetTokens - 32.0) / counted);
        }

        reportCold();
        string prompt = BenchmarkPrompts.LongPrompt(unique, lines);
        RequestSample cold;
        try
        {
            cold = await StreamOnceAsync(target, prompt, 1, ct).ConfigureAwait(false);
        }
        catch (BenchmarkRequestException ex) when (ex.StatusCode == 400)
        {
            // Calibration overshot the context window: shrink by 10 % once.
            lines = (int)(lines * 0.9);
            prompt = BenchmarkPrompts.LongPrompt(unique, lines);
            cold = await StreamOnceAsync(target, prompt, 1, ct).ConfigureAwait(false);
        }

        reportCached();
        var cached = await StreamOnceAsync(target, prompt, 1, ct).ConfigureAwait(false);
        return new PrefillResult(cold.PromptTokens, cold.TtftSeconds, cached.TtftSeconds);
    }

    private async Task<double> CalibrateTokensPerLineAsync(BenchmarkTarget target, CancellationToken ct)
    {
        int? count = await CountTokensAsync(target, BenchmarkPrompts.LongPrompt("calibration", BenchmarkPrompts.CalibrationLines), ct).ConfigureAwait(false);
        return count is > 0 ? (double)count.Value / BenchmarkPrompts.CalibrationLines : DefaultTokensPerLine;
    }

    /// <summary>Token count of <paramref name="prompt"/> as a chat message, via vLLM's <c>/tokenize</c>; null when unavailable.</summary>
    private async Task<int?> CountTokensAsync(BenchmarkTarget target, string prompt, CancellationToken ct)
    {
        try
        {
            var body = new { model = target.ServedName, messages = new[] { new { role = "user", content = prompt } } };
            using var response = await Http(target).PostAsJsonAsync(new Uri(target.BaseUri, "tokenize"), body, JsonOptions, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("count", out var count) && count.TryGetInt32(out int n) ? n : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return null;
        }
    }

    private async Task<(bool Valid, string Detail)> ToolCheckAsync(BenchmarkTarget target, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = target.ServedName,
            ["messages"] = new[] { new { role = "user", content = BenchmarkPrompts.ToolCallPrompt } },
            ["tools"] = BenchmarkPrompts.ToolDefinitions,
            ["max_tokens"] = 256,
            ["temperature"] = 0,
            ["chat_template_kwargs"] = new { enable_thinking = false },
        };

        using var doc = await PostJsonAsync(target, body, ct).ConfigureAwait(false);
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        if (!message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() == 0)
        {
            string content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
            return (false, "No tool call; replied: " + Truncate(content, 120));
        }

        var call = calls[0].GetProperty("function");
        string name = call.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        string arguments = call.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
        if (!BenchmarkPrompts.ToolNames.Contains(name, StringComparer.Ordinal))
        {
            return (false, $"Called unknown tool '{Truncate(name, 60)}'");
        }

        try
        {
            using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            return args.RootElement.ValueKind == JsonValueKind.Object
                ? (true, $"{name}({Truncate(arguments, 120)})")
                : (false, $"{name} arguments are not a JSON object: {Truncate(arguments, 120)}");
        }
        catch (JsonException)
        {
            return (false, $"{name} arguments are not valid JSON: {Truncate(arguments, 120)}");
        }
    }

    private async Task<List<QaAnswer>> QaAsync(BenchmarkTarget target, CancellationToken ct)
    {
        var answers = new List<QaAnswer>(BenchmarkPrompts.Questions.Count);
        foreach (var question in BenchmarkPrompts.Questions)
        {
            var body = new Dictionary<string, object?>
            {
                ["model"] = target.ServedName,
                ["messages"] = new[] { new { role = "user", content = question.Question } },
                ["max_tokens"] = 64,
                ["temperature"] = 0,
                ["chat_template_kwargs"] = new { enable_thinking = false },
            };

            using var doc = await PostJsonAsync(target, body, ct).ConfigureAwait(false);
            var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
            string content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
            int thinkEnd = content.LastIndexOf("</think>", StringComparison.Ordinal);
            if (thinkEnd >= 0)
            {
                content = content[(thinkEnd + "</think>".Length)..];
            }

            content = content.Trim();
            answers.Add(new QaAnswer(question.Question, question.Expected, Truncate(content, 200), question.Pattern.IsMatch(content)));
        }

        return answers;
    }

    /// <summary>Token limit per thinking request: room to think, and a bound on how long one question can take.</summary>
    public const int ThinkingMaxTokens = 4096;

    private Task<ThinkingResult?> ThinkingIfReasoningAsync(BenchmarkTarget target, Action<string> report, CancellationToken ct)
    {
        if (!target.Thinks)
        {
            return Task.FromResult<ThinkingResult?>(null);
        }

        report("Objective questions, thinking on");
        return ThinkingAsync(target, ct)!;
    }

    /// <summary>
    /// The objective questions again, one at a time, with thinking on: when the thinking starts and ends, when the answer
    /// starts, and whether it is right. A failed request counts as an error; a reply cut off before any answer, as unanswered.
    /// </summary>
    private async Task<ThinkingResult?> ThinkingAsync(BenchmarkTarget target, CancellationToken ct)
    {
        var answered = new List<ThinkingSample>();
        int correct = 0, unanswered = 0, errors = 0;
        foreach (var question in BenchmarkPrompts.Questions)
        {
            ThinkingSample sample;
            try
            {
                sample = await StreamThinkingAsync(target, question.Question, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is BenchmarkRequestException or HttpRequestException or JsonException
                || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                errors++;
                continue;
            }

            if (sample.AnswerAt is null)
            {
                unanswered++;
                continue;
            }

            answered.Add(sample);
            correct += question.Pattern.IsMatch(sample.Answer.Trim()) ? 1 : 0;
        }

        return new ThinkingResult(
            BenchmarkPrompts.Questions.Count,
            correct,
            unanswered,
            errors,
            Median(answered.Select(s => s.AnswerAt!.Value)),
            Median(answered.Select(s => s.AnswerAt!.Value - (s.ThinkingAt ?? s.AnswerAt!.Value))),
            Median(answered.Select(s => s.ThinkingTokens)),
            Median(answered.Where(s => s.Total > s.FirstTokenAt).Select(s => s.CompletionTokens / (s.Total - s.FirstTokenAt))),
            ThinkingMaxTokens);
    }

    private async Task<ThinkingSample> StreamThinkingAsync(BenchmarkTarget target, string question, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = target.ServedName,
            ["messages"] = new[] { new { role = "user", content = question } },
            ["max_tokens"] = ThinkingMaxTokens,
            ["temperature"] = 0,
            ["stream"] = true,
            ["stream_options"] = new { include_usage = true },
            ["chat_template_kwargs"] = new { enable_thinking = true },
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(target.BaseUri, "v1/chat/completions"))
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };

        long start = Stopwatch.GetTimestamp();
        using var response = await Http(target).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            throw new BenchmarkRequestException((int)response.StatusCode, Truncate(text, 300));
        }

        double? thinkingAt = null, answerAt = null;
        int thinkingChars = 0, completionTokens = 0, chunks = 0;
        var answer = new StringBuilder();
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cts.Token).ConfigureAwait(false) is { } line)
        {
            string data = line.StartsWith("data:", StringComparison.Ordinal) ? line[5..].Trim() : "";
            if (data.Length == 0 || data == "[DONE]")
            {
                continue;
            }

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
                usage.TryGetProperty("completion_tokens", out var tokens) && tokens.TryGetInt32(out int n))
            {
                completionTokens = n;
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
                !choices[0].TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            double now = Stopwatch.GetElapsedTime(start).TotalSeconds;
            foreach (string field in (string[])["reasoning_content", "reasoning"])
            {
                if (HasText(delta, field))
                {
                    thinkingAt ??= now;
                    thinkingChars += delta.GetProperty(field).GetString()!.Length;
                    chunks++;
                }
            }

            // shortcut: a server without a reasoning parser puts <think>…</think> in the content, which reads as the answer here.
            if (HasText(delta, "content"))
            {
                answerAt ??= now;
                answer.Append(delta.GetProperty("content").GetString());
                chunks++;
            }
        }

        double total = Stopwatch.GetElapsedTime(start).TotalSeconds;
        int replyTokens = completionTokens > 0 ? completionTokens : chunks;
        double share = thinkingChars + answer.Length == 0 ? 0 : (double)thinkingChars / (thinkingChars + answer.Length);
        return new ThinkingSample(thinkingAt, answerAt, answer.ToString(), replyTokens * share, replyTokens, Math.Min(thinkingAt ?? total, answerAt ?? total), total);
    }

    private sealed record ThinkingSample(double? ThinkingAt, double? AnswerAt, string Answer, double ThinkingTokens, int CompletionTokens, double FirstTokenAt, double Total);

    private async Task<JsonDocument> PostJsonAsync(BenchmarkTarget target, object body, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.RequestTimeout);
        using var response = await Http(target).PostAsJsonAsync(new Uri(target.BaseUri, "v1/chat/completions"), body, JsonOptions, cts.Token)
            .ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new BenchmarkRequestException((int)response.StatusCode, Truncate(text, 300));
        }

        return JsonDocument.Parse(text);
    }

    private async Task<RequestSample> StreamOnceAsync(BenchmarkTarget target, string prompt, int maxTokens, CancellationToken ct, string? system = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = target.ServedName,
            ["messages"] = system is null
                ? new[] { new { role = "user", content = prompt } }
                : new[] { new { role = "system", content = system }, new { role = "user", content = prompt } },
            ["max_tokens"] = maxTokens,
            ["min_tokens"] = maxTokens,
            ["temperature"] = 0,
            ["stream"] = true,
            ["stream_options"] = new { include_usage = true },
            ["chat_template_kwargs"] = new { enable_thinking = false },
        };
        if (target.Runtime == RuntimeKind.LlamaCpp)
        {
            // llama-server has no min_tokens: without this a request may stop at an end-of-sequence token early.
            body["ignore_eos"] = true;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(target.BaseUri, "v1/chat/completions"))
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };

        long start = Stopwatch.GetTimestamp();
        using var response = await Http(target).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            throw new BenchmarkRequestException((int)response.StatusCode, Truncate(text, 300));
        }

        double? ttft = null;
        int completionTokens = 0;
        int chunks = 0;
        int promptTokens = 0;
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cts.Token).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            string data = line[5..].Trim();
            if (data.Length == 0 || data == "[DONE]")
            {
                continue;
            }

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (ttft is null &&
                root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object &&
                (HasText(delta, "content") || HasText(delta, "reasoning_content") || HasText(delta, "reasoning")))
            {
                ttft = Stopwatch.GetElapsedTime(start).TotalSeconds;
            }

            if (root.TryGetProperty("choices", out var deltaChoices) && deltaChoices.ValueKind == JsonValueKind.Array && deltaChoices.GetArrayLength() > 0 &&
                deltaChoices[0].TryGetProperty("delta", out var chunk) && chunk.ValueKind == JsonValueKind.Object &&
                (HasText(chunk, "content") || HasText(chunk, "reasoning_content") || HasText(chunk, "reasoning")))
            {
                chunks++;
            }

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                completionTokens = usage.TryGetProperty("completion_tokens", out var ctok) && ctok.TryGetInt32(out int c) ? c : completionTokens;
                promptTokens = usage.TryGetProperty("prompt_tokens", out var ptok) && ptok.TryGetInt32(out int p) ? p : promptTokens;
            }
        }

        double total = Stopwatch.GetElapsedTime(start).TotalSeconds;
        // shortcut: a server that reports no usage gets one token per stream chunk, which undercounts if it batches tokens; read its usage when it adds it.
        return new RequestSample(ttft ?? total, total, completionTokens > 0 ? completionTokens : chunks, promptTokens);
    }

    /// <summary>The average speculative acceptance the server reported during <paramref name="windows"/>; null when it reported none.</summary>
    private double? AcceptanceDuring(BenchmarkTarget target, List<RunWindow> windows)
    {
        if (history is null || windows.Count == 0)
        {
            return null;
        }

        var first = windows.Min(w => w.Start);
        var last = windows.Max(w => w.End);
        var rates = history.GetModel(target.ModelId, _time.GetUtcNow() - first + options.TelemetryMaxGap)
            .Where(s => s.At >= first && s.At <= last + options.TelemetryMaxGap)
            .Select(s => s.Kpis.SpecDecodeAcceptanceRate)
            .OfType<double>()
            .ToList();
        return rates.Count == 0 ? null : rates.Average();
    }

    private static bool HasText(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString());

    private static string Truncate(string text, int max) => text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");

    private sealed record RequestSample(double TtftSeconds, double TotalSeconds, int CompletionTokens, int PromptTokens);

    // ---------------------------------------------------------------- workload presets (schema v2)

    /// <summary>
    /// Counters the shared-prefix workload reads before and after its timed questions, first match wins: vLLM's prefix
    /// cache hits, then llama.cpp's cached prompt tokens (MN-230; the family is already kept by the agent's filter,
    /// <see cref="LlamaCppMetrics.Families"/>). Without a match the result is labelled "cache metrics unavailable".
    /// </summary>
    private static readonly string[] PrefixCacheHitMetrics =
        ["vllm:prefix_cache_hits_total", "vllm:prefix_cache_hits", "vllm:gpu_prefix_cache_hits_total", "vllm:gpu_prefix_cache_hits",
         LlamaCppMetrics.PromptTokensCachedTotal];

    /// <summary>KV pool pressure (v3): vLLM's preemption counter and KV cache usage gauge, first match wins.</summary>
    private static readonly string[] PreemptionMetrics = ["vllm:num_preemptions_total", "vllm:num_preemptions"];

    private static readonly string[] KvCacheUsageMetrics = ["vllm:kv_cache_usage_perc", "vllm:gpu_cache_usage_perc"];

    private static readonly JsonElement ToolsElement = ParseTools();

    private static JsonElement ParseTools()
    {
        using var doc = JsonDocument.Parse(WorkloadPrompts.ToolsJson);
        return doc.RootElement.Clone();
    }

    /// <summary>The concurrency levels of a v2 benchmark: 1, 16 and the configuration's max num seqs (capped).</summary>
    public IReadOnlyList<int> WorkloadLevels(BenchmarkTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return options.WorkloadLevels
            .Append(Math.Min(target.MaxNumSeqs, options.MaxConcurrency))
            .Where(n => n > 0)
            .Distinct()
            .Order()
            .ToList();
    }

    /// <summary>
    /// A preset's workloads: an untimed warm-up, the tool-call cases (each repetition), short chat at every output
    /// length × level × repetition (distinct prompts from the code/tool/prose sets, errors counted), cold long prompts
    /// (an untimed warm-up, then a fresh random prefix every repetition), the shared-prefix workload (primed, then the
    /// prefix-cache hit counter delta over the timed questions), and the objective questions.
    /// </summary>
    private async Task<BenchmarkResult> RunWorkloadsAsync(BenchmarkTarget target, WorkloadProfile profile, IProgress<BenchmarkProgress>? progress, CancellationToken ct)
    {
        int reps = Math.Max(1, target.Repetitions ?? options.Runs);
        var levels = WorkloadLevels(target);
        var lengths = options.ChatOutputTokens.Count == 0 ? [256] : options.ChatOutputTokens;
        var longTargets = profile.LongPromptTokens.Where(t => t + 256 <= target.MaxModelLen).Order().ToList();
        int sharedTokens = Math.Min(options.SharedPrefixTokens, target.MaxModelLen / 2);
        bool shared = profile.SharedPrefix && sharedTokens >= 512;

        // The mixed workload's long request: 64K when the context holds it plus output, else 16K; 0 = not run.
        int mixedTarget = WorkloadProfiles.Mixed(profile)
            ? new[] { WorkloadProfiles.Long64K, WorkloadProfiles.Long16K }.FirstOrDefault(t => t + 256 <= target.MaxModelLen)
            : 0;
        int mixedConcurrency = Math.Min(16, Math.Min(target.MaxNumSeqs, options.MaxConcurrency));

        int total = 1 + (profile.Tools ? reps : 0) + (lengths.Count * levels.Count * reps) +
            (longTargets.Count > 0 ? 1 + (longTargets.Count * reps) : 0) + (shared ? 1 + reps : 0) + (mixedTarget > 0 ? reps : 0) + 1 + (target.Thinks ? 1 : 0);
        int step = 0;
        void Report(string phase) => progress?.Report(new BenchmarkProgress(phase, ++step, total));
        string tag = WorkloadPrompts.RandomWords(_random, 1);

        // KV pool pressure (v3): the preemption counter is diffed between the first and last sample, the usage gauge's max kept.
        var pool = new List<PoolSample>();
        async Task SamplePoolAsync()
        {
            if (await ReadPoolAsync(target, ct).ConfigureAwait(false) is { } sample)
            {
                pool.Add(sample);
            }
        }

        Report("Warm-up request (not timed)");
        await StreamOnceAsync(target, BenchmarkPrompts.Stories[0], 32, ct).ConfigureAwait(false);
        await SamplePoolAsync().ConfigureAwait(false);

        ToolCheckResult? tools = null;
        if (profile.Tools)
        {
            var cases = new List<ToolCaseResult>();
            for (int rep = 1; rep <= reps; rep++)
            {
                Report(string.Create(CultureInfo.InvariantCulture, $"Tool calls, run {rep} of {reps}"));
                foreach (var toolCase in WorkloadPrompts.ToolCases)
                {
                    cases.Add(await ToolCaseAsync(target, toolCase, ct).ConfigureAwait(false));
                }
            }

            tools = new ToolCheckResult(cases.Count(c => c.Valid), cases.Count, cases);
        }

        var windows = new List<RunWindow>();
        var results = new List<LevelResult>(lengths.Count * levels.Count);
        foreach (int outputTokens in lengths)
        {
            foreach (int concurrency in levels)
            {
                var runs = new List<RunMeasurement>(reps);
                for (int rep = 1; rep <= reps; rep++)
                {
                    Report(string.Create(CultureInfo.InvariantCulture, $"Chat {outputTokens} tokens, N={concurrency}, run {rep} of {reps}"));
                    runs.Add(await RunChatLevelAsync(target, concurrency, outputTokens, $"{tag}-{outputTokens}-{concurrency}-{rep}", windows, ct).ConfigureAwait(false));
                }

                var summary = Summarize(concurrency, runs);
                results.Add(summary with { OutputTokens = outputTokens, Errors = runs.Sum(r => r.Errors) });
                await SamplePoolAsync().ConfigureAwait(false);
            }
        }

        // The long-prompt, shared-prefix and mixed workloads are timed too: their span counts for the verdict (not per level).
        var longStart = _time.GetUtcNow();
        var sizes = await SizeLongPromptsAsync(target, longTargets.Append(mixedTarget).Where(t => t > 0).Distinct(), ct).ConfigureAwait(false);
        var longPrompts = longTargets.Count == 0 ? null : await LongPromptsAsync(target, longTargets, sizes, reps, Report, ct).ConfigureAwait(false);
        await SamplePoolAsync().ConfigureAwait(false);
        var sharedPrefix = shared ? await SharedPrefixAsync(target, sharedTokens, reps, tag, Report, ct).ConfigureAwait(false) : null;
        await SamplePoolAsync().ConfigureAwait(false);
        var mixed = mixedTarget == 0 ? null
            : await MixedAsync(target, mixedTarget, sizes[mixedTarget], mixedConcurrency, lengths.Min(), reps, tag, Report, SamplePoolAsync, ct).ConfigureAwait(false);
        await SamplePoolAsync().ConfigureAwait(false);
        if (longPrompts is not null || sharedPrefix is not null || mixed is not null)
        {
            windows.Add(new RunWindow(longStart, _time.GetUtcNow(), PerRun: false));
        }

        Report("Objective questions");
        var answers = await QaAsync(target, ct).ConfigureAwait(false);
        var thinking = await ThinkingIfReasoningAsync(target, Report, ct).ConfigureAwait(false);

        var guardrails = new GuardrailResult(
            tools is null || tools.Valid == tools.Total,
            tools is null ? "Not part of this preset" : string.Create(CultureInfo.InvariantCulture, $"{tools.Valid}/{tools.Total} tool calls valid"),
            answers.Count(a => a.Correct),
            answers.Count,
            answers);
        var noise = await EvaluateNoiseAsync(target, windows, ct).ConfigureAwait(false);
        var measured = ApplyNoise(results, noise);
        int? preemptions = pool.Count > 1 && pool[0].Preemptions is { } first && pool[^1].Preemptions is { } last ? (int)Math.Max(0, last - first) : null;
        return new BenchmarkResult(measured, null, guardrails,
            BuildNoise(target, measured.SelectMany(l => l.Runs).Select(r => r.SameModelRunningMax - r.Concurrency), noise,
                measured.SelectMany(l => l.Runs).Select(r => r.Server)), null,
            BenchmarkResult.CurrentSchemaVersion, profile.Goal, reps, longPrompts, sharedPrefix, tools,
            mixed, preemptions, pool.Max(s => s.KvUsage)) { SpecAcceptanceRate = AcceptanceDuring(target, windows), Thinking = thinking };
    }

    private async Task<RunMeasurement> RunChatLevelAsync(
        BenchmarkTarget target, int concurrency, int outputTokens, string tag, List<RunWindow> windows, CancellationToken ct)
    {
        var before = await ReadServerAsync(target, ct).ConfigureAwait(false);
        var startedAt = _time.GetUtcNow();
        long start = Stopwatch.GetTimestamp();
        var tasks = Enumerable.Range(0, concurrency)
            .Select(i => TryStreamAsync(target, WorkloadPrompts.ChatPrompt(i, tag), outputTokens, ct))
            .ToList();
        var all = await Task.WhenAll(tasks).ConfigureAwait(false);
        double wall = Stopwatch.GetElapsedTime(start).TotalSeconds;
        windows.Add(new RunWindow(startedAt, _time.GetUtcNow()));

        var samples = all.OfType<RequestSample>().ToList();
        int errors = all.Length - samples.Count;
        int tokens = samples.Sum(s => s.CompletionTokens);
        var ttfts = samples.Select(s => s.TtftSeconds).ToList();

        // A failed request's tokens are unknown, so the excess is judged only on runs without errors.
        var server = ServerCounters.Between(before, await ReadServerAsync(target, ct).ConfigureAwait(false), wall,
            errors == 0 ? Reported(samples.Sum(s => s.PromptTokens)) : null, errors == 0 ? Reported(tokens) : null, concurrency);
        return new RunMeasurement(
            concurrency,
            tokens,
            wall,
            samples.Count == 0 ? 0 : samples.Average(s => s.CompletionTokens / Math.Max(s.TotalSeconds - s.TtftSeconds, 1e-6)),
            wall > 0 ? tokens / wall : 0,
            ttfts.Count == 0 ? 0 : Percentile(ttfts, 50),
            ttfts.Count == 0 ? 0 : Percentile(ttfts, 95),
            0,
            0,
            errors)
        {
            Server = server,
        };
    }

    /// <summary>One timed request; a failure (HTTP error, timeout) is counted, not thrown. Job cancellation propagates.</summary>
    private async Task<RequestSample?> TryStreamAsync(BenchmarkTarget target, string prompt, int maxTokens, CancellationToken ct, string? system = null)
    {
        try
        {
            return await StreamOnceAsync(target, prompt, maxTokens, ct, system).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Lines of <see cref="WorkloadPrompts.ColdLongPrompt"/> per target token count (calibrated once, corrected with /tokenize).</summary>
    private async Task<Dictionary<int, int>> SizeLongPromptsAsync(BenchmarkTarget target, IEnumerable<int> targets, CancellationToken ct)
    {
        var sizes = new Dictionary<int, int>();
        double? tokensPerLine = null;
        foreach (int t in targets)
        {
            tokensPerLine ??= await CalibrateTokensPerLineAsync(target, ct).ConfigureAwait(false);
            int lines = (int)Math.Floor(t * 0.98 / tokensPerLine.Value);
            if (await CountTokensAsync(target, WorkloadPrompts.ColdLongPrompt(_random, lines), ct).ConfigureAwait(false) is { } counted && counted > 0)
            {
                lines = (int)Math.Floor(lines * (t - 32.0) / counted);
            }

            sizes[t] = Math.Max(1, lines);
        }

        return sizes;
    }

    private async Task<List<LongPromptResult>> LongPromptsAsync(BenchmarkTarget target, List<int> targets, Dictionary<int, int> sizes, int reps, Action<string> report, CancellationToken ct)
    {
        report("Long prompt warm-up (not timed)");
        await TryStreamAsync(target, WorkloadPrompts.ColdLongPrompt(_random, sizes[targets[0]]), 1, ct).ConfigureAwait(false);

        var results = new List<LongPromptResult>(targets.Count);
        foreach (int t in targets)
        {
            var ttfts = new List<double>(reps);
            int errors = 0;
            int promptTokens = 0;
            for (int rep = 1; rep <= reps; rep++)
            {
                report(string.Create(CultureInfo.InvariantCulture, $"Cold {t / 1024}K-token prompt, run {rep} of {reps}"));

                // A fresh random prefix every repetition: nothing of this prompt can be in the prefix cache.
                var sample = await TryStreamAsync(target, WorkloadPrompts.ColdLongPrompt(_random, sizes[t]), 1, ct).ConfigureAwait(false);
                if (sample is null)
                {
                    errors++;
                    continue;
                }

                ttfts.Add(sample.TtftSeconds);
                promptTokens = sample.PromptTokens;
            }

            results.Add(new LongPromptResult(t, promptTokens, ttfts, ttfts.Count == 0 ? 0 : Median(ttfts), errors));
        }

        return results;
    }

    /// <summary>
    /// The mixed workload (v3): one cold long request, then <see cref="BenchmarkSuiteOptions.MixedStartDelay"/> later (before
    /// its first token arrives on a real GPU) the short-chat set at <paramref name="concurrency"/> on top of it; a fresh
    /// random prefix every repetition. Records the short requests' TTFT p95 and per-request tok/s while the long one is in
    /// flight, and the long request's own TTFT. The pool is sampled after the short set, while the long request may still run.
    /// </summary>
    private async Task<MixedWorkloadResult> MixedAsync(
        BenchmarkTarget target, int longTarget, int lines, int concurrency, int outputTokens, int reps, string tag, Action<string> report,
        Func<Task> samplePool, CancellationToken ct)
    {
        var shortP95 = new List<double>(reps);
        var shortTokS = new List<double>(reps);
        var longTtfts = new List<double>(reps);
        int errors = 0;
        for (int rep = 1; rep <= reps; rep++)
        {
            report(string.Create(CultureInfo.InvariantCulture, $"Mixed: cold {longTarget / 1024}K-token prompt + chat N={concurrency}, run {rep} of {reps}"));
            var longTask = TryStreamAsync(target, WorkloadPrompts.ColdLongPrompt(_random, lines), 1, ct);
            await Task.Delay(options.MixedStartDelay, ct).ConfigureAwait(false);
            var shorts = await Task.WhenAll(Enumerable.Range(0, concurrency)
                .Select(i => TryStreamAsync(target, WorkloadPrompts.ChatPrompt(i, $"{tag}-mixed-{rep}"), outputTokens, ct))).ConfigureAwait(false);
            await samplePool().ConfigureAwait(false);
            var longSample = await longTask.ConfigureAwait(false);

            var samples = shorts.OfType<RequestSample>().ToList();
            errors += shorts.Length - samples.Count + (longSample is null ? 1 : 0);
            if (samples.Count > 0)
            {
                shortP95.Add(Percentile(samples.Select(s => s.TtftSeconds).ToList(), 95));
                shortTokS.Add(samples.Average(s => s.CompletionTokens / Math.Max(s.TotalSeconds - s.TtftSeconds, 1e-6)));
            }

            if (longSample is not null)
            {
                longTtfts.Add(longSample.TtftSeconds);
            }
        }

        return new MixedWorkloadResult(longTarget, concurrency, shortP95, shortP95.Count == 0 ? 0 : Median(shortP95),
            shortTokS.Count == 0 ? 0 : shortTokS.Average(), longTtfts, longTtfts.Count == 0 ? 0 : Median(longTtfts), errors);
    }

    private async Task<SharedPrefixResult> SharedPrefixAsync(BenchmarkTarget target, int prefixTokens, int reps, string tag, Action<string> report, CancellationToken ct)
    {
        // Size the shared system prompt: ~26 tokens per policy line, corrected once with /tokenize.
        int lines = Math.Max(10, prefixTokens / 26);
        if (await CountTokensAsync(target, WorkloadPrompts.SharedSystemPrompt(tag, lines), ct).ConfigureAwait(false) is { } counted && counted > 0)
        {
            lines = Math.Max(10, (int)Math.Floor(lines * (prefixTokens - 64.0) / counted));
        }

        string system = WorkloadPrompts.SharedSystemPrompt(tag, lines);
        int questions = Math.Max(1, options.SharedPrefixQuestions);

        // Prime deliberately (untimed): the shared prefix is now in the cache if caching is on.
        report("Shared prefix: priming (not timed)");
        var primed = await StreamOnceAsync(target, "Acknowledge the handbook in one word.", 1, ct, system).ConfigureAwait(false);

        double? before = await ReadPrefixCacheHitsAsync(target, ct).ConfigureAwait(false);
        var perRep = new List<double>(reps);
        var all = new List<double>(reps * questions);
        int errors = 0;
        for (int rep = 1; rep <= reps; rep++)
        {
            report(string.Create(CultureInfo.InvariantCulture, $"Shared prefix, run {rep} of {reps}"));
            var ttfts = new List<double>(questions);
            for (int q = 0; q < questions; q++)
            {
                int index = (((rep - 1) * questions) + q) % WorkloadPrompts.SharedPrefixQuestions.Count;
                string question = string.Create(CultureInfo.InvariantCulture, $"{WorkloadPrompts.SharedPrefixQuestions[index]} (Question {rep}-{q}.)");
                var sample = await TryStreamAsync(target, question, 16, ct, system).ConfigureAwait(false);
                if (sample is null)
                {
                    errors++;
                    continue;
                }

                ttfts.Add(sample.TtftSeconds);
            }

            if (ttfts.Count > 0)
            {
                perRep.Add(Median(ttfts));
                all.AddRange(ttfts);
            }
        }

        double? after = before is null ? null : await ReadPrefixCacheHitsAsync(target, ct).ConfigureAwait(false);
        double? delta = before is { } b && after is { } a ? Math.Max(0, a - b) : null;
        bool reuse = delta is > 0;
        string label = reuse ? "shared prefix (cache reuse observed)"
            : delta is null ? "repeated prompt (cache metrics unavailable)"
            : "repeated prompt (no cache reuse observed)";
        return new SharedPrefixResult(
            primed.PromptTokens, questions, perRep, perRep.Count == 0 ? 0 : Median(perRep), all.Count == 0 ? 0 : Percentile(all, 95),
            delta, reuse, label, errors);
    }

    /// <summary>Tokens the client's usage blocks reported; null (not known) when they reported none.</summary>
    private static double? Reported(int tokens) => tokens > 0 ? tokens : null;

    /// <summary>
    /// llama-server's <c>llamacpp:*</c> counters from the measured instance's own <c>/metrics</c> (through the same client
    /// as the requests); null for a vLLM target (nothing is read), and when unreadable or without the llama.cpp families.
    /// </summary>
    private async Task<ServerSnapshot?> ReadServerAsync(BenchmarkTarget target, CancellationToken ct)
    {
        if (target.Runtime != RuntimeKind.LlamaCpp)
        {
            return null;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await Http(target).GetAsync(new Uri(target.BaseUri, "metrics"), cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? ServerSnapshot.Parse(await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false))
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or FormatException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>The model's prefix-cache hit counter (tokens) from its own <c>/metrics</c>; null when unreadable.</summary>
    private async Task<double?> ReadPrefixCacheHitsAsync(BenchmarkTarget target, CancellationToken ct)
    {
        var samples = await ReadMetricsAsync(target, ct).ConfigureAwait(false);
        return samples is null ? null : FirstMetric(samples, PrefixCacheHitMetrics);
    }

    private readonly record struct PoolSample(double? Preemptions, double? KvUsage);

    /// <summary>vLLM's preemption counter and KV cache usage gauge from the model's own <c>/metrics</c>; null when unreadable.</summary>
    private async Task<PoolSample?> ReadPoolAsync(BenchmarkTarget target, CancellationToken ct)
    {
        var samples = await ReadMetricsAsync(target, ct).ConfigureAwait(false);
        return samples is null ? null : new PoolSample(FirstMetric(samples, PreemptionMetrics), FirstMetric(samples, KvCacheUsageMetrics));
    }

    /// <summary>The sum of the first metric of <paramref name="names"/> present in the scrape; null when none is.</summary>
    private static double? FirstMetric(List<PrometheusSample> samples, string[] names)
    {
        string? name = names.FirstOrDefault(n => PrometheusText.HasMetric(samples, n));
        return name is null ? null : PrometheusText.Sum(samples, name);
    }

    private async Task<List<PrometheusSample>?> ReadMetricsAsync(BenchmarkTarget target, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await Http(target).GetAsync(new Uri(target.BaseUri, "metrics"), cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? PrometheusText.Parse(await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false))
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or FormatException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>One tool-call case: the expected tool must be called with arguments that satisfy its JSON schema.</summary>
    private async Task<ToolCaseResult> ToolCaseAsync(BenchmarkTarget target, ToolCase toolCase, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = target.ServedName,
            ["messages"] = new[] { new { role = "user", content = toolCase.Prompt } },
            ["tools"] = ToolsElement,
            ["max_tokens"] = 256,
            ["temperature"] = 0,
            ["chat_template_kwargs"] = new { enable_thinking = false },
        };

        try
        {
            using var doc = await PostJsonAsync(target, body, ct).ConfigureAwait(false);
            var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
            if (!message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() == 0)
            {
                string content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                return new ToolCaseResult(toolCase.ExpectedTool, false, "No tool call; replied: " + Truncate(content, 120));
            }

            var call = calls[0].GetProperty("function");
            string name = call.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string arguments = call.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
            if (!string.Equals(name, toolCase.ExpectedTool, StringComparison.Ordinal))
            {
                return new ToolCaseResult(toolCase.ExpectedTool, false, $"Called '{Truncate(name, 60)}' instead of '{toolCase.ExpectedTool}'");
            }

            using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            string? violation = JsonSchemaValidator.Validate(WorkloadPrompts.ToolSchema(name), args.RootElement);
            return violation is null
                ? new ToolCaseResult(name, true, $"{name}({Truncate(arguments, 120)})")
                : new ToolCaseResult(name, false, $"{name}({Truncate(arguments, 120)}): {violation}");
        }
        catch (JsonException ex)
        {
            return new ToolCaseResult(toolCase.ExpectedTool, false, "Arguments are not valid JSON: " + Truncate(ex.Message, 120));
        }
        catch (Exception ex) when (ex is BenchmarkRequestException or HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new ToolCaseResult(toolCase.ExpectedTool, false, "Request failed: " + Truncate(ex.Message, 160));
        }
    }

    // ---------------------------------------------------------------- embeddings (pooling models)

    public const string RerankerUnsupported =
        "Rerankers (score/classify pooling models) can't be benchmarked or tuned yet; only embedding models get the pooling benchmark.";

    /// <summary>Words the embedding inputs are built from (deterministic, so every run sends comparable text).</summary>
    private static readonly string[] FillerWords =
    [
        "river", "signal", "harbor", "copper", "lantern", "meadow", "engine", "silver", "orchard", "canyon",
        "ledger", "violet", "compass", "thunder", "garden", "pixel", "marble", "falcon", "harvest", "summit",
        "anchor", "velvet", "glacier", "circuit", "willow", "beacon", "quartz", "prairie", "rocket", "saffron",
        "timber", "voyage", "nebula", "cobalt", "ember", "fjord", "galaxy", "hollow", "island", "jasmine",
    ];

    /// <summary>
    /// The embeddings benchmark: a warm-up, then each level (1, 16 and the configuration's max num seqs, capped)
    /// <see cref="BenchmarkSuiteOptions.Runs"/> times: <c>N</c> concurrent <c>/v1/embeddings</c> requests, each a batch
    /// of distinct inputs of mixed lengths (unique per repetition, so nothing is served from a cache). Measures input
    /// tok/s, req/s and latency p50/p95, and checks every response's shape.
    /// </summary>
    private async Task<BenchmarkResult> RunEmbeddingsAsync(BenchmarkTarget target, IProgress<BenchmarkProgress>? progress, CancellationToken ct)
    {
        var levels = options.EmbeddingLevels
            .Append(Math.Min(target.MaxNumSeqs, options.EmbeddingMaxConcurrency))
            .Where(n => n > 0)
            .Distinct()
            .Order()
            .ToList();
        int reps = Math.Max(1, target.Repetitions ?? options.Runs);
        int total = 1 + (levels.Count * reps);
        int step = 0;
        void Report(string phase) => progress?.Report(new BenchmarkProgress(phase, ++step, total));

        var checker = new EmbeddingChecker();
        string tag = Guid.NewGuid().ToString("N")[..8];

        Report("Warm-up request");
        await EmbedOnceAsync(target, EmbeddingInputs(target, $"{tag}-warm", 0), checker, ct).ConfigureAwait(false);

        var windows = new List<RunWindow>();
        var results = new List<EmbeddingLevelResult>(levels.Count);
        foreach (int concurrency in levels)
        {
            var runs = new List<EmbeddingRunMeasurement>(reps);
            for (int run = 1; run <= reps; run++)
            {
                Report(string.Create(CultureInfo.InvariantCulture, $"Embeddings N={concurrency}, run {run} of {reps}"));
                runs.Add(await RunEmbeddingLevelAsync(target, concurrency, $"{tag}-{concurrency}-{run}", checker, windows, ct).ConfigureAwait(false));
            }

            results.Add(SummarizeEmbeddings(concurrency, runs));
        }

        var noise = await EvaluateNoiseAsync(target, windows, ct).ConfigureAwait(false);
        var measured = ApplyNoise(results, noise);
        var report = BuildNoise(target, measured.SelectMany(l => l.Runs).Select(r => r.SameModelRunningMax - (r.Concurrency * r.Inputs)), noise,
            measured.SelectMany(l => l.Runs).Select(r => r.Server));
        var check = checker.Result();
        var embeddings = new EmbeddingResult(options.EmbeddingInputsPerRequest, measured, check);
        var guardrails = new GuardrailResult(check.Valid, check.Detail, 0, 0, []);
        return target.Preset is { } preset && WorkloadProfiles.IsPreset(preset)
            ? new BenchmarkResult([], null, guardrails, report, embeddings, BenchmarkResult.CurrentSchemaVersion, TuningGoal.Embeddings, reps)
            : new BenchmarkResult([], null, guardrails, report, embeddings);
    }

    public static EmbeddingLevelResult SummarizeEmbeddings(int concurrency, IReadOnlyList<EmbeddingRunMeasurement> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        return new EmbeddingLevelResult(
            concurrency,
            runs,
            Median(runs.Select(r => r.InputTokS)),
            Median(runs.Select(r => r.RequestsPerSecond)),
            Median(runs.Select(r => r.LatencyP50)),
            Median(runs.Select(r => r.LatencyP95)),
            runs.Any(IsNoisy))
        {
            Server = ServerCounters.Median(runs.Select(r => r.Server)),
        };
    }

    private async Task<EmbeddingRunMeasurement> RunEmbeddingLevelAsync(
        BenchmarkTarget target, int concurrency, string tag, EmbeddingChecker checker, List<RunWindow> windows, CancellationToken ct)
    {
        var before = await ReadServerAsync(target, ct).ConfigureAwait(false);
        var startedAt = _time.GetUtcNow();
        long start = Stopwatch.GetTimestamp();
        var tasks = Enumerable.Range(0, concurrency)
            .Select(i => EmbedOnceAsync(target, EmbeddingInputs(target, tag, i), checker, ct))
            .ToList();
        var samples = await Task.WhenAll(tasks).ConfigureAwait(false);
        double wall = Stopwatch.GetElapsedTime(start).TotalSeconds;
        windows.Add(new RunWindow(startedAt, _time.GetUtcNow()));

        int tokens = samples.Sum(s => s.InputTokens);
        var latencies = samples.Select(s => s.Seconds).ToList();

        // Input token counts may be estimates (no usage block), so the prompt side is not judged here; generated tokens are
        // (an embedding run generates none) and so are requests already processing.
        var server = ServerCounters.Between(before, await ReadServerAsync(target, ct).ConfigureAwait(false), wall, null, 0, concurrency);
        return new EmbeddingRunMeasurement(
            concurrency,
            options.EmbeddingInputsPerRequest,
            tokens,
            wall,
            wall > 0 ? tokens / wall : 0,
            wall > 0 ? concurrency / wall : 0,
            Percentile(latencies, 50),
            Percentile(latencies, 95),
            0,
            0)
        {
            Server = server,
        };
    }

    /// <summary>
    /// The inputs of request <paramref name="request"/>: <see cref="BenchmarkSuiteOptions.EmbeddingInputsPerRequest"/>
    /// distinct texts whose lengths cycle through <see cref="BenchmarkSuiteOptions.EmbeddingInputWords"/> (capped so
    /// the longest still fits max model len), each starting with a unique tag.
    /// </summary>
    internal IReadOnlyList<string> EmbeddingInputs(BenchmarkTarget target, string tag, int request)
    {
        // About 1.5 tokens per filler word; leave room for the tag.
        int maxWords = Math.Max(8, (int)((target.MaxModelLen - 32) / 1.5));
        IReadOnlyList<int> lengths = options.EmbeddingInputWords.Count == 0 ? [16] : options.EmbeddingInputWords;
        var inputs = new List<string>(options.EmbeddingInputsPerRequest);
        for (int j = 0; j < options.EmbeddingInputsPerRequest; j++)
        {
            int words = Math.Min(lengths[(request + j) % lengths.Count], maxWords);
            var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"[{tag}-{request}-{j}]"));
            for (int k = 0; k < words; k++)
            {
                text.Append(' ').Append(FillerWords[((k * 7) + (request * 13) + (j * 31)) % FillerWords.Length]);
            }

            inputs.Add(text.ToString());
        }

        return inputs;
    }

    private async Task<(double Seconds, int InputTokens)> EmbedOnceAsync(
        BenchmarkTarget target, IReadOnlyList<string> inputs, EmbeddingChecker checker, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = target.ServedName,
            ["input"] = inputs,
            ["encoding_format"] = "float",
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.RequestTimeout);
        long start = Stopwatch.GetTimestamp();
        using var response = await Http(target).PostAsJsonAsync(new Uri(target.BaseUri, "v1/embeddings"), body, JsonOptions, cts.Token).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        if (!response.IsSuccessStatusCode)
        {
            throw new BenchmarkRequestException((int)response.StatusCode, Truncate(text, 300));
        }

        int estimated = inputs.Sum(i => i.Length) / 4;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            // Python's json writes NaN/Infinity literals, which are not JSON: a broken vector, not a broken benchmark.
            checker.Invalid("a response was not valid JSON (non-finite values?)");
            return (seconds, estimated);
        }

        using var _ = doc;
        var root = doc.RootElement;
        checker.Observe(inputs.Count, root);
        int tokens = root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
            usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out int n)
            ? n
            : estimated;
        return (seconds, tokens);
    }

    /// <summary>Collects shape problems across every embeddings response of a run (thread-safe: levels fire requests concurrently).</summary>
    internal sealed class EmbeddingChecker
    {
        private const double UnitTolerance = 0.02;
        private readonly Lock _lock = new();
        private readonly List<string> _problems = [];
        private int _dimension;
        private bool _normalized = true;
        private int _vectors;

        public void Observe(int expected, JsonElement root)
        {
            lock (_lock)
            {
                if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    Problem("a response had no data array");
                    return;
                }

                if (data.GetArrayLength() != expected)
                {
                    Problem(string.Create(CultureInfo.InvariantCulture, $"{data.GetArrayLength()} vectors for {expected} inputs"));
                }

                foreach (var item in data.EnumerateArray())
                {
                    ObserveVector(item);
                }
            }
        }

        public void Invalid(string problem)
        {
            lock (_lock)
            {
                Problem(problem);
            }
        }

        public EmbeddingCheck Result()
        {
            lock (_lock)
            {
                if (_problems.Count > 0)
                {
                    return new EmbeddingCheck(false, "Invalid embeddings: " + string.Join("; ", _problems.Distinct(StringComparer.Ordinal).Take(3)), _dimension, _normalized);
                }

                return new EmbeddingCheck(true, string.Create(CultureInfo.InvariantCulture,
                    $"{_vectors:N0} vectors, dimension {_dimension}, {(_normalized ? "unit length" : "not normalized")}"), _dimension, _normalized);
            }
        }

        private void ObserveVector(JsonElement item)
        {
            if (!item.TryGetProperty("embedding", out var embedding) || embedding.ValueKind != JsonValueKind.Array)
            {
                Problem("a vector was missing or not a float array");
                return;
            }

            int dimension = embedding.GetArrayLength();
            if (dimension == 0)
            {
                Problem("an empty vector");
                return;
            }

            if (_dimension == 0)
            {
                _dimension = dimension;
            }
            else if (dimension != _dimension)
            {
                Problem(string.Create(CultureInfo.InvariantCulture, $"dimension {dimension} after {_dimension}"));
            }

            double sum = 0;
            foreach (var value in embedding.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double x) || !double.IsFinite(x))
                {
                    Problem("a vector had a non-finite value");
                    return;
                }

                sum += x * x;
            }

            double norm = Math.Sqrt(sum);
            if (norm <= 0)
            {
                Problem("an all-zero vector");
                return;
            }

            _normalized &= Math.Abs(norm - 1) <= UnitTolerance;
            _vectors++;
        }

        private void Problem(string text)
        {
            if (_problems.Count < 50)
            {
                _problems.Add(text);
            }
        }
    }
}

/// <summary>A non-2xx reply from the model container during a benchmark.</summary>
public sealed class BenchmarkRequestException : InvalidOperationException
{
    public BenchmarkRequestException()
    {
    }

    public BenchmarkRequestException(string message)
        : base(message)
    {
    }

    public BenchmarkRequestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public BenchmarkRequestException(int statusCode, string body)
        : base($"The model returned HTTP {statusCode}: {body}")
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

/// <summary>One read of llama-server's <c>llamacpp:*</c> families (<see cref="LlamaCppMetrics"/>; design-p3p4 §A.1.9, §A.6).</summary>
/// <param name="PromptTokens"><see cref="LlamaCppMetrics.PromptTokensTotal"/>.</param>
/// <param name="PromptSeconds"><see cref="LlamaCppMetrics.PromptSecondsTotal"/>.</param>
/// <param name="PredictedTokens"><see cref="LlamaCppMetrics.TokensPredictedTotal"/>.</param>
/// <param name="PredictedSeconds"><see cref="LlamaCppMetrics.TokensPredictedSecondsTotal"/>.</param>
/// <param name="Processing"><see cref="LlamaCppMetrics.RequestsProcessing"/>.</param>
public sealed record ServerSnapshot(double PromptTokens, double PromptSeconds, double PredictedTokens, double PredictedSeconds, int Processing)
{
    /// <summary>
    /// The snapshot in a <c>/metrics</c> text; null when it lacks the <see cref="LlamaCppMetrics.PromptTokensTotal"/> or
    /// <see cref="LlamaCppMetrics.TokensPredictedTotal"/> family (not a llama-server, or started without <c>--metrics</c>).
    /// </summary>
    public static ServerSnapshot? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var samples = PrometheusText.Parse(text);
        if (!PrometheusText.HasMetric(samples, LlamaCppMetrics.TokensPredictedTotal) || !PrometheusText.HasMetric(samples, LlamaCppMetrics.PromptTokensTotal))
        {
            return null;
        }

        return new ServerSnapshot(
            PrometheusText.Sum(samples, LlamaCppMetrics.PromptTokensTotal),
            PrometheusText.Sum(samples, LlamaCppMetrics.PromptSecondsTotal),
            PrometheusText.Sum(samples, LlamaCppMetrics.TokensPredictedTotal),
            PrometheusText.Sum(samples, LlamaCppMetrics.TokensPredictedSecondsTotal),
            (int)Math.Round(PrometheusText.Sum(samples, LlamaCppMetrics.RequestsProcessing)));
    }
}

/// <summary>
/// llama-server's own numbers over one timed run (design-p3p4 §A.6, MN-225), from the <c>llamacpp:*</c> counters read just
/// before and just after it: its tokens per second (Δ<c>prompt_tokens_total</c>, Δ<c>tokens_predicted_total</c> over the
/// run's wall time), its own prefill and decode speeds (the token deltas over Δ<c>prompt_seconds_total</c> and
/// Δ<c>tokens_predicted_seconds_total</c>), shown next to the client numbers, and the outside-client check.
/// </summary>
/// <param name="PromptTokens">Δ prompt tokens the server processed (cached prompt tokens are not processed again).</param>
/// <param name="PredictedTokens">Δ tokens the server generated.</param>
/// <param name="PromptTokS"><paramref name="PromptTokens"/> ÷ the run's wall time.</param>
/// <param name="PredictedTokS"><paramref name="PredictedTokens"/> ÷ the run's wall time (the server's view of the aggregate tok/s).</param>
/// <param name="PrefillTokS">The server's prefill speed: <paramref name="PromptTokens"/> ÷ Δ prompt seconds; null when none was counted.</param>
/// <param name="DecodeTokS">The server's decode speed: <paramref name="PredictedTokens"/> ÷ Δ generation seconds; null when none was counted.</param>
/// <param name="ProcessingAtStart">Requests already processing just before the run (none of them the benchmark's).</param>
/// <param name="ExcessTokens">Tokens the server counted beyond the benchmark's own (the larger of the prompt and generated
/// excess; a side counts only when the benchmark knows its own tokens there).</param>
/// <param name="OutsideClients">True when <paramref name="ProcessingAtStart"/> is above 0 or the excess of a judged side is
/// above <see cref="Tolerance"/>: another client used the model during the run (noisy).</param>
public sealed record ServerCounters(
    double PromptTokens,
    double PredictedTokens,
    double PromptTokS,
    double PredictedTokS,
    double? PrefillTokS,
    double? DecodeTokS,
    int ProcessingAtStart,
    double ExcessTokens,
    bool OutsideClients)
{
    /// <summary>
    /// Tokens of slack before an excess counts as another client: 2 per request of the run, or 2 % of what the client
    /// reported, whichever is larger (a stream's usage and the server's counter may differ by an end token).
    /// </summary>
    public static double Tolerance(double clientTokens, int concurrency) => Math.Max(2.0 * Math.Max(1, concurrency), 0.02 * clientTokens);

    /// <summary>
    /// The counters between <paramref name="before"/> and <paramref name="after"/> for a run of <paramref name="wallSeconds"/>
    /// in which the benchmark sent <paramref name="clientPromptTokens"/> and received <paramref name="clientCompletionTokens"/>
    /// (null = not known, e.g. no usage block: that side is not judged; 0 = none, e.g. an embedding run generates nothing).
    /// Null when either read is missing or a counter went backwards (the server restarted during the run).
    /// </summary>
    public static ServerCounters? Between(
        ServerSnapshot? before, ServerSnapshot? after, double wallSeconds, double? clientPromptTokens, double? clientCompletionTokens, int concurrency)
    {
        if (before is null || after is null)
        {
            return null;
        }

        double prompt = after.PromptTokens - before.PromptTokens;
        double predicted = after.PredictedTokens - before.PredictedTokens;
        double promptSeconds = after.PromptSeconds - before.PromptSeconds;
        double predictedSeconds = after.PredictedSeconds - before.PredictedSeconds;
        if (prompt < 0 || predicted < 0 || promptSeconds < 0 || predictedSeconds < 0)
        {
            return null;
        }

        double excessPrompt = clientPromptTokens is { } sentPrompt ? prompt - sentPrompt : 0;
        double excessPredicted = clientCompletionTokens is { } received ? predicted - received : 0;
        bool promptOutside = clientPromptTokens is { } p && excessPrompt > Tolerance(p, concurrency);
        bool predictedOutside = clientCompletionTokens is { } c && excessPredicted > Tolerance(c, concurrency);
        return new ServerCounters(
            prompt,
            predicted,
            wallSeconds > 0 ? prompt / wallSeconds : 0,
            wallSeconds > 0 ? predicted / wallSeconds : 0,
            promptSeconds > 0 && prompt > 0 ? prompt / promptSeconds : null,
            predictedSeconds > 0 && predicted > 0 ? predicted / predictedSeconds : null,
            before.Processing,
            Math.Max(0, Math.Max(excessPrompt, excessPredicted)),
            before.Processing > 0 || promptOutside || predictedOutside);
    }

    /// <summary>Per-field median of the runs that have counters (worst case for the noise fields); null when none has.</summary>
    public static ServerCounters? Median(IEnumerable<ServerCounters?> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var all = runs.OfType<ServerCounters>().ToList();
        if (all.Count == 0)
        {
            return null;
        }

        static double? Optional(IEnumerable<double?> values)
        {
            var known = values.OfType<double>().ToList();
            return known.Count == 0 ? null : BenchmarkSuite.Median(known);
        }

        return new ServerCounters(
            BenchmarkSuite.Median(all.Select(c => c.PromptTokens)),
            BenchmarkSuite.Median(all.Select(c => c.PredictedTokens)),
            BenchmarkSuite.Median(all.Select(c => c.PromptTokS)),
            BenchmarkSuite.Median(all.Select(c => c.PredictedTokS)),
            Optional(all.Select(c => c.PrefillTokS)),
            Optional(all.Select(c => c.DecodeTokS)),
            all.Max(c => c.ProcessingAtStart),
            all.Max(c => c.ExcessTokens),
            all.Any(c => c.OutsideClients));
    }
}
