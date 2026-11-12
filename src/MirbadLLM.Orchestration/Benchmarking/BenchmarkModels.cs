using System.Text.Json.Serialization;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>Why a benchmark ran: on demand from the UI/API, or as one candidate of a tuning run.</summary>
public enum BenchmarkOrigin
{
    OnDemand,
    Tuning,
}

/// <summary>One repetition of one concurrency level: <see cref="Concurrency"/> streaming requests fired together.</summary>
/// <param name="PerRequestTokS">Mean over requests of completion tokens ÷ (total − TTFT): the decode speed one client sees.</param>
/// <param name="AggregateTokS">All completion tokens ÷ wall time of the run: what the box delivers.</param>
/// <param name="TtftP50">Time to first token, median over the run's requests, seconds.</param>
/// <param name="TtftP95">Time to first token, 95th percentile (nearest rank) over the run's requests, seconds.</param>
/// <param name="OtherModelsRunningMax">Highest <c>vllm:num_requests_running</c> any other model sharing the node (or the
/// GPUs) reported during the run (no scope known: any other Ready model).</param>
/// <param name="SameModelRunningMax">Highest running count the measured model reported; above <see cref="Concurrency"/> means outside traffic.</param>
public sealed record RunMeasurement(
    int Concurrency,
    int CompletionTokens,
    double WallSeconds,
    double PerRequestTokS,
    double AggregateTokS,
    double TtftP50,
    double TtftP95,
    int OtherModelsRunningMax,
    int SameModelRunningMax,
    int Errors = 0)
{
    /// <summary>
    /// llama-server's own counters over the run (design-p3p4 §A.6, MN-225): null for vLLM and for runs stored before.
    /// Omitted when null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServerCounters? Server { get; init; }

    /// <summary>
    /// True when the request counters of another model sharing the node (or the GPUs) grew during the run (design §6.5
    /// item 4: <c>vllm:request_success_total</c> and the prompt/generation token totals, diffed between scrapes), so a
    /// burst that ended between two samples of <c>num_requests_running</c> is seen too. Omitted when false (MN-112).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool OtherModelsServed { get; init; }
}

/// <summary>One concurrency level: every repetition plus the per-metric median across them (the reported number).</summary>
/// <param name="OutputTokens">Tokens each request generated (schema v1: always 256).</param>
/// <param name="Errors">Failed requests over every repetition (schema v2; v1 failed the whole benchmark instead).</param>
public sealed record LevelResult(
    int Concurrency,
    IReadOnlyList<RunMeasurement> Runs,
    double PerRequestTokS,
    double AggregateTokS,
    double TtftP50,
    double TtftP95,
    bool Noisy,
    int OutputTokens = 256,
    int Errors = 0)
{
    /// <summary>Per-field median of the runs' <see cref="RunMeasurement.Server"/> (llama.cpp, MN-225); omitted when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServerCounters? Server { get; init; }
}

/// <summary>Long-prompt prefill: a cold request with a unique prefix, then the same prompt again (prefix-cache hit).</summary>
public sealed record PrefillResult(int PromptTokens, double ColdSeconds, double CachedSeconds)
{
    public double ColdTokensPerSecond => ColdSeconds > 0 ? PromptTokens / ColdSeconds : 0;
}

public sealed record QaAnswer(string Question, string Expected, string Answer, bool Correct);

/// <summary>Correctness checks: a valid tool call, and objective questions scored by exact/regex match.</summary>
public sealed record GuardrailResult(
    bool ToolCallValid,
    string ToolCallDetail,
    int QaCorrect,
    int QaTotal,
    IReadOnlyList<QaAnswer> Answers);

/// <summary>Whether something else competed for the GPU while measuring, and why we think so.</summary>
/// <param name="IdleGpuPercent">GPU utilization sampled before the first request, with mirbadllm traffic paused. Null when unknown.</param>
/// <param name="OtherModelsMaxRunning">Per other model sharing the node (or the GPUs; no scope known: per other Ready
/// model), the highest running-request count seen during any run.</param>
public sealed record NoiseReport(
    bool Noisy,
    IReadOnlyList<string> Reasons,
    double? IdleGpuPercent,
    IReadOnlyDictionary<string, int> OtherModelsMaxRunning)
{
    public static NoiseReport Quiet { get; } = new(false, [], null, new Dictionary<string, int>());

    /// <summary>
    /// Why the telemetry of the runs was incomplete (design §6.5 item 4: a model sharing the node had gaps or no samples
    /// during a run, or there was no metrics history): the result is then "noise unknown", not "quiet". Kept apart from
    /// <see cref="Reasons"/>, which make a result noisy. Null when complete and on results stored before MN-112 (omitted
    /// from JSON then).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Unknown { get; init; }

    /// <summary>
    /// The other models whose telemetry was checked: those sharing the measured model's node (on a discrete node its GPUs,
    /// plus the node's models with no GPU), MN-112. Null when none was, and on results stored before MN-112.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Considered { get; init; }

    /// <summary>
    /// The verdict (design §6.5 item 4): <see cref="BenchmarkNoise.Noisy"/> when <see cref="Noisy"/>, else
    /// <see cref="BenchmarkNoise.Unknown"/> when <see cref="Unknown"/> lists a gap, else <see cref="BenchmarkNoise.Quiet"/>.
    /// Derived, so a caller that adds noisy reasons (the job's drain and GPU idle notes) keeps it right.
    /// </summary>
    [JsonIgnore]
    public BenchmarkNoise Verdict =>
        Noisy ? BenchmarkNoise.Noisy : Unknown is { Count: > 0 } ? BenchmarkNoise.Unknown : BenchmarkNoise.Quiet;
}

/// <summary>Everything one run of the benchmark suite measured.</summary>
/// <param name="Levels">Generation levels (chat models); empty for a pooling model. Schema v2 has one level per
/// (output tokens, concurrency).</param>
/// <param name="Embeddings">The embeddings benchmark (pooling models); null for a generation model.</param>
/// <param name="SchemaVersion">1 = the classic suite (levels 1/4/8/16 × 256 tokens, one long prompt, one tool call);
/// 2 = workload presets (<see cref="Preset"/>, <see cref="LongPrompts"/>, <see cref="SharedPrefix"/>, <see cref="Tools"/>);
/// 3 = the mixed workload (<see cref="Mixed"/>) and KV pool pressure (<see cref="Preemptions"/>, <see cref="KvCacheUsageMax"/>).
/// Files written before the field existed read as 1.</param>
/// <param name="Repetitions">How many times each timed case ran (v2).</param>
/// <param name="Preemptions">vLLM preemptions (<c>vllm:num_preemptions_total</c> delta) over the timed workloads; null when
/// /metrics could not be read (v3).</param>
/// <param name="KvCacheUsageMax">Highest <c>vllm:kv_cache_usage_perc</c> sampled between the timed workloads (0–1); null when unread (v3).</param>
public sealed record BenchmarkResult(
    IReadOnlyList<LevelResult> Levels,
    PrefillResult? Prefill,
    GuardrailResult Guardrails,
    NoiseReport Noise,
    EmbeddingResult? Embeddings = null,
    int SchemaVersion = 1,
    TuningGoal? Preset = null,
    int Repetitions = 0,
    IReadOnlyList<LongPromptResult>? LongPrompts = null,
    SharedPrefixResult? SharedPrefix = null,
    ToolCheckResult? Tools = null,
    MixedWorkloadResult? Mixed = null,
    int? Preemptions = null,
    double? KvCacheUsageMax = null)
{
    /// <summary>Speculative acceptance during the runs, from the server's own metrics; null without speculative decoding or metrics.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? SpecAcceptanceRate { get; init; }

    /// <summary>The objective questions asked again with thinking on, for a model tagged Reasoning; null otherwise.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ThinkingResult? Thinking { get; init; }

    public const int CurrentSchemaVersion = 3;

    /// <summary>The level at <paramref name="concurrency"/> (the shortest output length when there are several).</summary>
    public LevelResult? Level(int concurrency) =>
        Levels.Where(l => l.Concurrency == concurrency).OrderBy(l => l.OutputTokens).FirstOrDefault();

    public LevelResult? Level(int concurrency, int outputTokens) =>
        Levels.FirstOrDefault(l => l.Concurrency == concurrency && l.OutputTokens == outputTokens);

    /// <summary>Failed timed requests over every workload (v2).</summary>
    public int Errors =>
        Levels.Sum(l => l.Errors) + (LongPrompts?.Sum(l => l.Errors) ?? 0) + (SharedPrefix?.Errors ?? 0) + (Mixed?.Errors ?? 0);

    /// <summary>Timed requests sent (v2), for the error rate.</summary>
    public int TimedRequests =>
        Levels.Sum(l => l.Runs.Sum(r => r.Concurrency)) + (LongPrompts?.Sum(l => l.TtftRuns.Count + l.Errors) ?? 0) +
        (SharedPrefix is { } sp ? sp.Questions * Math.Max(1, sp.TtftRuns.Count) : 0) +
        (Mixed is { } m ? (m.Concurrency + 1) * Math.Max(1, m.LongTtftRuns.Count) : 0);
}

/// <summary>
/// The objective questions asked one at a time with thinking on. Every other measurement runs with thinking off, so this is
/// what thinking costs: how long the user waits for the answer, and how many tokens the thinking takes.
/// </summary>
/// <param name="Questions">Questions asked.</param>
/// <param name="Correct">Answers that matched the expected one.</param>
/// <param name="Unanswered">Replies that reached <paramref name="MaxTokens"/> before any answer.</param>
/// <param name="Errors">Requests that failed.</param>
/// <param name="TimeToAnswerMedian">Seconds from sending to the first answer token: what a user waits for.</param>
/// <param name="ThinkingSecondsMedian">Seconds from the first thinking token to the first answer token.</param>
/// <param name="ThinkingTokensMedian">About how many tokens the thinking took: the reply's tokens times thinking's share of its text.</param>
/// <param name="TokensPerSecondMedian">Reply tokens per second, thinking included, from the first token to the last.</param>
/// <param name="MaxTokens">The token limit per request.</param>
public sealed record ThinkingResult(
    int Questions,
    int Correct,
    int Unanswered,
    int Errors,
    double TimeToAnswerMedian,
    double ThinkingSecondsMedian,
    double ThinkingTokensMedian,
    double TokensPerSecondMedian,
    int MaxTokens);

/// <summary>
/// The mixed workload (schema v3): one cold <see cref="LongTargetTokens"/>-token request, then ~1 s later (before its first
/// token) <see cref="Concurrency"/> short-chat requests on top of it; repeated per repetition with a fresh prefix.
/// </summary>
/// <param name="ShortTtftP95Runs">TTFT p95 of the short requests while the long one was in flight, one per repetition.</param>
/// <param name="ShortTtftP95">Median of <see cref="ShortTtftP95Runs"/>.</param>
/// <param name="ShortPerRequestTokS">Mean per-request decode tok/s of the short requests.</param>
/// <param name="LongTtftRuns">The long request's own TTFT, one per successful repetition.</param>
public sealed record MixedWorkloadResult(
    int LongTargetTokens,
    int Concurrency,
    IReadOnlyList<double> ShortTtftP95Runs,
    double ShortTtftP95,
    double ShortPerRequestTokS,
    IReadOnlyList<double> LongTtftRuns,
    double LongTtftMedian,
    int Errors);

/// <summary>
/// A cold long prompt (schema v2): each repetition starts with a unique random token sequence before any reusable
/// content, so no repetition can hit the prefix cache. TTFT measured with one output token.
/// </summary>
/// <param name="TargetTokens">16,384 or 65,536 (the workload's size).</param>
/// <param name="PromptTokens">Measured prompt size (vLLM usage) of the last repetition.</param>
/// <param name="TtftRuns">Seconds to first token, one per successful repetition.</param>
public sealed record LongPromptResult(int TargetTokens, int PromptTokens, IReadOnlyList<double> TtftRuns, double TtftMedian, int Errors);

/// <summary>
/// The shared-prefix workload (schema v2): one ~8K-token system prompt, different questions. Primed once (untimed),
/// then each repetition asks <see cref="Questions"/> new questions; <see cref="CacheHitTokens"/> is the delta of the
/// model's <c>vllm:prefix_cache_hits</c> counter over the timed part (null when /metrics could not be read).
/// </summary>
/// <param name="TtftRuns">Median TTFT of each repetition.</param>
/// <param name="CacheReuseObserved">The hit counter grew during the workload.</param>
/// <param name="Label">"shared prefix (cache reuse observed)" or "repeated prompt (no cache reuse observed)".</param>
public sealed record SharedPrefixResult(
    int PrefixTokens,
    int Questions,
    IReadOnlyList<double> TtftRuns,
    double TtftMedian,
    double TtftP95,
    double? CacheHitTokens,
    bool CacheReuseObserved,
    string Label,
    int Errors);

/// <summary>One tool-call case: the expected tool was called with arguments that satisfy its JSON schema.</summary>
public sealed record ToolCaseResult(string Tool, bool Valid, string Detail);

/// <summary>Every tool-call case of every repetition (schema v2): full JSON-schema validation, required arguments included.</summary>
public sealed record ToolCheckResult(int Valid, int Total, IReadOnlyList<ToolCaseResult> Cases)
{
    public double Validity => Total > 0 ? (double)Valid / Total : 0;
}

/// <summary>
/// One repetition of one embeddings level: <see cref="Concurrency"/> <c>/v1/embeddings</c> requests fired together,
/// each a batch of distinct inputs of mixed lengths.
/// </summary>
/// <param name="InputTokS">All input (prompt) tokens ÷ wall time: what the box embeds per second.</param>
/// <param name="RequestsPerSecond">Requests ÷ wall time.</param>
/// <param name="LatencyP50">Request latency, median, seconds.</param>
/// <param name="LatencyP95">Request latency, 95th percentile (nearest rank), seconds.</param>
public sealed record EmbeddingRunMeasurement(
    int Concurrency,
    int Inputs,
    int InputTokens,
    double WallSeconds,
    double InputTokS,
    double RequestsPerSecond,
    double LatencyP50,
    double LatencyP95,
    int OtherModelsRunningMax,
    int SameModelRunningMax)
{
    /// <summary>As <see cref="RunMeasurement.OtherModelsServed"/>: a sharing model's request counters grew during the run.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool OtherModelsServed { get; init; }

    /// <summary>As <see cref="RunMeasurement.Server"/> (llama.cpp, MN-225); omitted when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServerCounters? Server { get; init; }
}

/// <summary>One embeddings level: every repetition plus the per-metric median across them.</summary>
public sealed record EmbeddingLevelResult(
    int Concurrency,
    IReadOnlyList<EmbeddingRunMeasurement> Runs,
    double InputTokS,
    double RequestsPerSecond,
    double LatencyP50,
    double LatencyP95,
    bool Noisy)
{
    /// <summary>As <see cref="LevelResult.Server"/> (llama.cpp, MN-225); omitted when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServerCounters? Server { get; init; }
}

/// <summary>
/// Shape correctness of every response: one vector per input, one dimension throughout, finite non-zero values.
/// <see cref="Normalized"/> = every vector has unit length (±2 %), which a configuration change must not alter.
/// </summary>
public sealed record EmbeddingCheck(bool Valid, string Detail, int Dimension, bool Normalized);

/// <summary>The embeddings benchmark of a pooling model.</summary>
/// <param name="InputsPerRequest">Inputs batched into each request.</param>
public sealed record EmbeddingResult(int InputsPerRequest, IReadOnlyList<EmbeddingLevelResult> Levels, EmbeddingCheck Check)
{
    public EmbeddingLevelResult? Level(int concurrency) => Levels.FirstOrDefault(l => l.Concurrency == concurrency);
}

/// <summary>A persisted benchmark: what was measured, with which settings, and the outcome.</summary>
/// <param name="Id">Timestamp id (<c>yyyyMMddTHHmmssZ</c>), also the file name.</param>
/// <param name="Settings">The settings the container was actually running with (read back from its command line).</param>
/// <param name="ConfigHash">The container's launch-config hash, for matching runs to configurations.</param>
public sealed record BenchmarkRun(
    string Id,
    string ModelId,
    BenchmarkOrigin Origin,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    ModelDefinition Settings,
    string? ConfigHash,
    BenchmarkResult? Result,
    string? Error,
    bool Cancelled,
    TuningGoal? Preset = null)
{
    /// <summary>
    /// Data path of the load (design §6.5 item 5, <see cref="BenchmarkDataPaths"/>): <c>agent-mtls-loopback</c>,
    /// <c>agent-mtls-lan</c> or <c>direct</c>; null on runs recorded before it existed (direct).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Proxy { get; init; }

    /// <summary>
    /// Where and how the run was measured (design §6.5 item 5, MN-112): node id and uid, GPU UUIDs and names, runtime and
    /// delivery, image digest, config hash, execution fingerprint, data path and the noise verdict. Null on runs stored
    /// before MN-112 (the bootstrap node, direct: <see cref="BenchmarkComparisons.Of"/>) and omitted from JSON then.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BenchmarkProvenance? Provenance { get; init; }

    [JsonIgnore]
    public TimeSpan Duration => FinishedAt - StartedAt;
}

/// <summary>Where and what to benchmark: the container's own upstream (bypasses the router and API keys).</summary>
/// <param name="Pooling">A pooling model gets the embeddings benchmark instead of chat/tool/QA.</param>
/// <param name="MaxNumSeqs">The configuration's max num seqs: the embeddings benchmark also measures at this concurrency.</param>
/// <param name="Preset">Run the preset's workloads (schema v2); null = the classic suite (v1).</param>
/// <param name="Repetitions">Repetitions of each timed case; null = <see cref="BenchmarkSuiteOptions.Runs"/>.</param>
public sealed record BenchmarkTarget(
    string ModelId, string ServedName, Uri BaseUri, int MaxModelLen, PoolingMode Pooling = PoolingMode.None, int MaxNumSeqs = 0,
    TuningGoal? Preset = null, int? Repetitions = null)
{
    /// <summary>
    /// The resources the measured model shares (design §6.5 item 4, MN-112): contention is read only for these models.
    /// Null = no scope known (wiring without a provenance source, tests): every model in the metrics history, as before.
    /// </summary>
    public NoiseScope? Scope { get; init; }

    /// <summary>
    /// The measured runtime (MN-225). <see cref="RuntimeKind.LlamaCpp"/>: chat levels 1/2/4/8 capped at the slots
    /// (<see cref="MaxNumSeqs"/> carries <c>Parallel</c>), <c>ignore_eos</c> on each request, and llama-server's
    /// <c>llamacpp:*</c> counters read around every timed run. vLLM (the default) is measured exactly as before.
    /// </summary>
    public RuntimeKind Runtime { get; init; } = RuntimeKind.Vllm;

    /// <summary>Tagged Reasoning: the run ends with the objective questions asked again with thinking on (<see cref="ThinkingResult"/>).</summary>
    public bool Thinks { get; init; }
}

/// <summary>
/// What a benchmark's contention check looks at (design §6.5 item 4, MN-112): only the models that share a resource with
/// the measured model. On a unified node that is every other model on the node; on a discrete node the models sharing one
/// of its GPUs, plus the node's models with no GPU (host-heavy). Models on other nodes are never this run's noise: they
/// keep serving whether the job's maintenance window is node-scoped or global (D83) and do not load this node.
/// </summary>
/// <param name="NodeId">The node the measured instance runs on.</param>
/// <param name="SharedWith">Model ids sharing the node (or the GPUs), the measured model excluded.</param>
/// <param name="Running">Those of <paramref name="SharedWith"/> whose instance was Ready when the scope was taken: their
/// telemetry must cover every run, else the result is "noise unknown".</param>
/// <param name="GpuUuids">The measured instance's GPUs on a discrete node; empty on a unified node.</param>
public sealed record NoiseScope(string NodeId, IReadOnlyList<string> SharedWith, IReadOnlyList<string> Running, IReadOnlyList<string> GpuUuids)
{
    /// <summary>True when the scope is per GPU (a discrete node).</summary>
    public bool PerGpu => GpuUuids.Count > 0;
}

/// <summary>Live progress of the suite.</summary>
public sealed record BenchmarkProgress(string Phase, int Step, int TotalSteps);

/// <summary>The deterministic benchmark suite (one implementation, shared by on-demand benchmarks and tuning).</summary>
public interface IBenchmarkSuite
{
    /// <summary>Number of progress steps one run reports.</summary>
    int StepCount { get; }

    Task<BenchmarkResult> RunAsync(BenchmarkTarget target, IProgress<BenchmarkProgress>? progress, CancellationToken ct = default);
}

/// <summary>Suite knobs. Defaults are the documented procedure; tests shrink them.</summary>
public sealed class BenchmarkSuiteOptions
{
    public IReadOnlyList<int> Levels { get; set; } = [1, 4, 8, 16];

    public int Runs { get; set; } = 3;

    /// <summary>max_tokens = min_tokens, so every request generates exactly this many tokens.</summary>
    public int MaxTokens { get; set; } = 256;

    /// <summary>Long-prompt size cap; the actual size is min(MaxModelLen, this).</summary>
    public int PrefillTokens { get; set; } = 65_536;

    public bool IncludePrefill { get; set; } = true;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Embeddings benchmark: fixed concurrency levels; the target's max num seqs (capped) is added.</summary>
    public IReadOnlyList<int> EmbeddingLevels { get; set; } = [1, 16];

    /// <summary>Embeddings benchmark: the candidate's max num seqs is measured too, up to this concurrency.</summary>
    public int EmbeddingMaxConcurrency { get; set; } = 64;

    /// <summary>Embeddings benchmark: inputs per request (each of a different length).</summary>
    public int EmbeddingInputsPerRequest { get; set; } = 4;

    /// <summary>Embeddings benchmark: input lengths in words, cycled over a request's inputs (capped to fit max model len).</summary>
    public IReadOnlyList<int> EmbeddingInputWords { get; set; } = [16, 128, 512, 2048];

    /// <summary>Workloads (v2): output lengths of the short-chat workload.</summary>
    public IReadOnlyList<int> ChatOutputTokens { get; set; } = [256, 1024];

    /// <summary>Workloads (v2): fixed concurrency levels; the configuration's max num seqs (capped) is added.</summary>
    public IReadOnlyList<int> WorkloadLevels { get; set; } = [1, 16];

    /// <summary>Workloads (v2): highest concurrency measured (the max-num-seqs level is capped here).</summary>
    public int MaxConcurrency { get; set; } = 64;

    /// <summary>Workloads (v2): shared system-prompt size of the shared-prefix workload (capped to max model len / 2).</summary>
    public int SharedPrefixTokens { get; set; } = 8192;

    /// <summary>Workloads (v2): questions per repetition of the shared-prefix workload.</summary>
    public int SharedPrefixQuestions { get; set; } = 4;

    /// <summary>Workloads (v3): how long after the cold long request the mixed workload's short requests start.</summary>
    public TimeSpan MixedStartDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Noise check (MN-112): the longest gap between two metrics samples of a model that still counts as complete
    /// telemetry (the sampler scrapes every 5 s); a longer gap around a run makes the result "noise unknown".
    /// </summary>
    public TimeSpan TelemetryMaxGap { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Noise check (MN-112, scoped targets only): how long the suite waits after its last timed run for the sampler's next
    /// scrape of every model it checks, so the tail of that run is covered by the counters too. Whatever is still missing
    /// then reads "noise unknown".
    /// </summary>
    public TimeSpan TelemetrySettleTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Noise check: poll interval of the settle wait.</summary>
    public TimeSpan TelemetrySettlePoll { get; set; } = TimeSpan.FromSeconds(1);
}

public enum JobKind
{
    Benchmark,
    Tuning,
}

/// <summary>
/// Thrown when a benchmark or tuning job can't start (design §6.5 item 1, D15): another job holds the model's node
/// (<see cref="Code"/> = <c>job_conflict</c>, <see cref="NodeId"/> names the node), or
/// <see cref="Core.JobOptions.MaxConcurrent"/> jobs already run across nodes (<c>job_limit_reached</c>, <see cref="Limit"/>
/// names the limit). Both are 409 (<c>ErrorCodes.ApiStatus</c>).
/// </summary>
public sealed class JobConflictException : InvalidOperationException
{
    public JobConflictException()
    {
    }

    public JobConflictException(string message)
        : base(message)
    {
    }

    public JobConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary><c>job_conflict</c> (the node is busy) or <c>job_limit_reached</c> (the global limit).</summary>
    public string Code { get; init; } = Agent.Contracts.ErrorCodes.JobConflict;

    /// <summary>The node whose job blocks the request (<c>job_conflict</c>); null for the limit.</summary>
    public string? NodeId { get; init; }

    /// <summary>The limit that was reached (<c>job_limit_reached</c>); null for a node conflict.</summary>
    public int? Limit { get; init; }
}

/// <summary>Live view of the running job, for the UI and <c>GET /api/benchmark</c> / <c>GET /api/tuning</c>.</summary>
/// <param name="Phase">Human phase, e.g. "Waiting for in-flight requests", "Starting MTP × 2", "Benchmarking: N=8, run 2 of 3".</param>
/// <param name="CandidateLabel">Tuning only: the candidate being measured.</param>
/// <param name="Candidates">Tuning only: candidates finished so far.</param>
/// <param name="NodeId">The node the job holds (P2, MN-111): the model's placement, else the bootstrap node.</param>
public sealed record JobSnapshot(
    string JobId,
    JobKind Kind,
    string ModelId,
    DateTimeOffset StartedAt,
    DateTimeOffset EstimatedEnd,
    string Phase,
    int Step,
    int TotalSteps,
    string? CandidateLabel,
    int CandidateNumber,
    int CandidateCount,
    IReadOnlyList<TuningCandidateResult> Candidates,
    TuningGoal? Goal,
    bool Cancelling,
    string? CandidateDescription = null,
    string? NodeId = null);

/// <summary>How the most recent job ended, so a page that was away can show the outcome.</summary>
public sealed record JobOutcome(string JobId, JobKind Kind, string ModelId, string RunId, string Status, DateTimeOffset FinishedAt, string? Error);

/// <summary>GPU utilization sampled over a few seconds with no mirbadllm traffic; above the threshold, other GPU work is active.</summary>
public sealed record GpuIdleReading(double? AveragePercent, double? MaxPercent, int Samples)
{
    public const double BusyThresholdPercent = 10;

    public bool Busy => AveragePercent is > BusyThresholdPercent;
}

/// <summary>
/// What a tuning run optimizes, and which workloads a benchmark runs. The five workload presets
/// (<see cref="InteractiveChat"/> … <see cref="Embeddings"/>) are current; <see cref="Balanced"/>,
/// <see cref="ManyAgents"/> and <see cref="SingleUser"/> are the classic goals, kept so stored runs and API
/// clients keep working (they use the classic suite and scorer, without the confirmation stage).
/// </summary>
public enum TuningGoal
{
    Balanced,
    ManyAgents,
    SingleUser,
    InteractiveChat,
    AgentsTools,
    Throughput,
    LongDocuments,
    Embeddings,
}

/// <summary>
/// The settings the search varies. The classic goals use the first four in this order; the workload presets
/// (<see cref="SearchPlanner"/>) use all of them, grouped into phases: memory &amp; cache (KV dtype × prefix caching),
/// concurrency (max num seqs, then max batched tokens), decode acceleration (speculative × CUDA graph mode, then async
/// scheduling) and a bounded refinement.
/// </summary>
public enum TuningDimension
{
    SpeculativeDecoding,
    MaxNumSeqs,
    MaxNumBatchedTokens,
    KvCacheDtype,
    PrefixCaching,
    CudaGraphMode,
    AsyncScheduling,
    Refinement,

    // ---- llama.cpp small tune (design-p3p4 §A.6, MN-225; LlamaCppTuning). Appended, so the values above keep their numbers.

    /// <summary>llama.cpp <c>-np</c> parallel slots {1, 2, 4, 8}.</summary>
    Parallel,

    /// <summary>llama.cpp <c>-fa</c> {on, off}.</summary>
    FlashAttention,

    /// <summary>llama.cpp KV cache types {f16, q8_0}, K and V together (<c>-ctk</c>/<c>-ctv</c>).</summary>
    KvCacheTypes,

    /// <summary>llama.cpp batch × micro-batch (<c>-b</c>/<c>-ub</c>); causal embedding models <c>ub = b</c>.</summary>
    BatchSizes,

    // ---- MLX and SGLang small tune (SmallRuntimeTuning). Appended, so stored runs keep their meaning.

    /// <summary>MLX decode and prompt concurrency.</summary>
    MlxParallel,

    /// <summary>MLX KV cache bits {off, 8, 4}.</summary>
    MlxKvBits,

    /// <summary>MLX <c>--num-draft-tokens</c>, on a model with a drafter.</summary>
    MlxDraftTokens,

    /// <summary>MLX prompt cache size.</summary>
    MlxPromptCache,

    /// <summary>SGLang <c>--max-running-requests</c>.</summary>
    SglangMaxRunningRequests,

    /// <summary>SGLang <c>--schedule-policy</c>.</summary>
    SglangSchedulePolicy,

    /// <summary>SGLang's draft depth, on a model with speculative decoding.</summary>
    SglangDraftDepth,
}

/// <summary>
/// One value a step tries: a label and the definition change it makes. <see cref="Number"/>: seqs, batched tokens or
/// a speculative depth; <see cref="Text"/>: KV dtype; <see cref="Flag"/>: prefix caching or async scheduling;
/// <see cref="Graph"/>: CUDA graph mode; <see cref="Method"/>: speculative choice (off, saved, mtp, draft).
/// </summary>
public sealed record TuningValue(string Label, int? Number = null, string? Text = null, bool? Flag = null, string? Graph = null, string? Method = null);

/// <summary>One coordinate of the search. <see cref="SkipReason"/> set = the step does not run, and why;
/// <see cref="Note"/> explains a narrowed step (e.g. MTP values left out).</summary>
/// <param name="Phase">The search phase the step belongs to (workload presets), e.g. "Phase 2 · Concurrency".</param>
/// <param name="Description">What the step tries to achieve and its tradeoff (<see cref="TuningDescriptions"/>).</param>
/// <param name="Key">Distinguishes two steps of one dimension (the refinement pass and the final prefix-cache check).</param>
/// <param name="Offered">Every value the step offers, when a custom search left some of them out of <paramref name="Values"/>; null = <paramref name="Values"/>.</param>
public sealed record TuningStepPlan(
    TuningDimension Dimension, string Title, IReadOnlyList<TuningValue> Values, string? SkipReason, string? Note = null,
    string? Phase = null, string? Description = null, string? Key = null, IReadOnlyList<TuningValue>? Offered = null);

/// <summary>One value of one step, named by its label: how a custom search says which values it leaves out.</summary>
public sealed record TuningValueRef(TuningDimension Dimension, string Label);

/// <param name="Description">What this candidate tries to achieve and its tradeoff.</param>
public sealed record TuningCandidatePlan(string Step, string Label, ModelDefinition Settings, string? Description = null);

/// <summary>The exact search: steps with their values, and the candidates as they would be if the baseline stays best.</summary>
/// <param name="PreviewCandidates">Baseline plus every step applied to the baseline, duplicates removed.</param>
/// <param name="MaxCandidates">Upper bound when later steps build on a different best-so-far.</param>
/// <param name="Search">Workload presets: the gates and baseline the adaptive steps (follow-ups, refinement) need; null = classic plan.</param>
/// <param name="EstimatedCandidates">Expected candidates including follow-ups and refinement (for the ETA); 0 = <see cref="PreviewCandidates"/>.</param>
public sealed record TuningPlan(
    IReadOnlyList<TuningStepPlan> Steps,
    IReadOnlyList<TuningCandidatePlan> PreviewCandidates,
    int MaxCandidates,
    SearchContext? Search = null,
    int EstimatedCandidates = 0)
{
    public int ExpectedCandidates => EstimatedCandidates > 0 ? EstimatedCandidates : PreviewCandidates.Count;
}

public enum CandidateOutcome
{
    Accepted,
    Rejected,
    Failed,
}

/// <summary>One measured candidate of a tuning run.</summary>
/// <param name="Score">Goal score (see <see cref="TuningScorer"/>); null when not computable.</param>
/// <param name="StartupSeconds">How long the candidate took to become Ready; null when the running container was reused.</param>
/// <param name="Description">What the candidate tries to achieve and its tradeoff.</param>
/// <param name="EffectiveConfig">The settings it ran with, after normalization, in one line.</param>
/// <param name="RuntimeNotes">What vLLM reported resolving (graph mode, speculative config, async), or "as requested".</param>
/// <param name="BenchmarkSeconds">How long the benchmark took (for later ETAs).</param>
/// <param name="ScoreMin">Workload presets: lowest per-repetition score (the candidate's range).</param>
/// <param name="ScoreMax">Workload presets: highest per-repetition score.</param>
public sealed record TuningCandidateResult(
    int Index,
    string Step,
    string Label,
    ModelDefinition Settings,
    string ConfigHash,
    CandidateOutcome Outcome,
    string? Reason,
    BenchmarkResult? Result,
    double? Score,
    double? StartupSeconds,
    double? ScoreMin = null,
    double? ScoreMax = null,
    string? Description = null,
    string? EffectiveConfig = null,
    IReadOnlyList<string>? RuntimeNotes = null,
    double? BenchmarkSeconds = null,
    string? KvPool = null);

public enum TuningRunStatus
{
    Completed,
    Cancelled,
    Failed,
}

/// <summary>A persisted tuning run. The winner is never applied automatically.</summary>
public sealed record TuningRun(
    string Id,
    string ModelId,
    TuningGoal Goal,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    TuningRunStatus Status,
    ModelDefinition Baseline,
    IReadOnlyList<TuningCandidateResult> Candidates,
    int? WinnerIndex,
    double? WinnerGainPercent,
    string? Error,
    IReadOnlyList<string> Notes,
    int SchemaVersion = 1)
{
    /// <summary>Workload presets with the confirmation stage (search, then top-2 + baseline re-measured).</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// Data path of the load (design §6.5 item 5, <see cref="BenchmarkDataPaths"/>): <c>agent-mtls-loopback</c>,
    /// <c>agent-mtls-lan</c> or <c>direct</c>; null on runs recorded before it existed (direct).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Proxy { get; init; }

    /// <summary>
    /// Where and how the run was measured (design §6.5 item 5, MN-112), as <see cref="BenchmarkRun.Provenance"/>: the
    /// hardware, runtime and data path every candidate shared; image digest, config hash and execution fingerprint of the
    /// first measured instance (each candidate's own hash is <see cref="TuningCandidateResult.ConfigHash"/>); the noise
    /// verdict over every measured candidate (noisy if one was; else unknown if one was). Null on runs stored before
    /// MN-112 and omitted from JSON then.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BenchmarkProvenance? Provenance { get; init; }

    [JsonIgnore]
    public TuningCandidateResult? Winner => WinnerIndex is { } i ? Candidates.FirstOrDefault(c => c.Index == i) : null;
}
