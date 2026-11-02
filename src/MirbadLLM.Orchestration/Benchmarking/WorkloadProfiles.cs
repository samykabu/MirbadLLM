using System.Globalization;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>A measured quantity a workload preset can score on.</summary>
public enum ScoreComponent
{
    /// <summary>Per-request decode tok/s at N=1 (mean over the short-chat output lengths). Higher is better.</summary>
    ChatN1,

    /// <summary>Aggregate tok/s at N=16 (mean over output lengths). Higher is better.</summary>
    ChatN16,

    /// <summary>Aggregate tok/s at the configuration's max num seqs (only when that level is above 16). Higher is better.</summary>
    ChatNMax,

    /// <summary>Cold 16K-token prompt TTFT. Lower is better (scored as baseline ÷ candidate).</summary>
    LongPrompt16K,

    /// <summary>Cold 64K-token prompt TTFT. Lower is better.</summary>
    LongPrompt64K,

    /// <summary>Shared-prefix workload TTFT. Lower is better.</summary>
    SharedPrefix,

    /// <summary>Embeddings input tok/s at N=1. Higher is better.</summary>
    EmbeddingN1,

    /// <summary>Embeddings input tok/s at N=16. Higher is better.</summary>
    EmbeddingN16,

    /// <summary>Short-chat TTFT p95 while a cold long request is in flight (mixed workload). Lower is better.</summary>
    MixedShortTtft,
}

/// <summary>
/// SLO guards a candidate must pass (null disables one). Defaults are the documented ones: short-prompt TTFT p95
/// ≤ 1 s at N=1 and ≤ 3 s at N=16 (and ≤ 3 s under a long request, the mixed workload), long-prompt TTFT no worse
/// than baseline +25 %, tool validity 100 % (when the preset uses tools), error rate 0, no vLLM preemptions, and
/// objective answers no worse than the baseline (generation presets).
/// </summary>
public sealed record SloGuards(
    double? ShortTtftP95AtN1Seconds = 1.0,
    double? ShortTtftP95AtN16Seconds = 3.0,
    double? LongTtftMaxRatio = 1.25,
    bool RequireToolValidity = false,
    double MaxErrorRate = 0,
    bool ObjectiveAnswers = true,
    double? LatencyP95MaxRatio = null,
    double? MixedShortTtftP95Seconds = 3.0,
    int? MaxPreemptions = 0);

/// <summary>
/// A workload preset: which workloads the benchmark runs, which score components it weighs (renormalized over the
/// components actually measured on both sides) and which guards apply.
/// </summary>
public sealed record WorkloadProfile(
    TuningGoal Goal,
    string Name,
    string Description,
    IReadOnlyDictionary<ScoreComponent, double> Weights,
    SloGuards Guards,
    IReadOnlyList<int> LongPromptTokens,
    bool SharedPrefix,
    bool Tools,
    bool Embeddings)
{
    public bool Chat => !Embeddings;

    public string WeightsText => string.Join(", ", Weights.Select(kv =>
        string.Create(CultureInfo.InvariantCulture, $"{WorkloadProfiles.ComponentName(kv.Key)} {kv.Value:0.##}")));
}

/// <summary>The five workload presets (R2-5).</summary>
public static class WorkloadProfiles
{
    public const int Long16K = 16_384;
    public const int Long64K = 65_536;

    public static readonly IReadOnlyList<TuningGoal> Presets =
        [TuningGoal.InteractiveChat, TuningGoal.AgentsTools, TuningGoal.Throughput, TuningGoal.LongDocuments, TuningGoal.Embeddings];

    public static bool IsPreset(TuningGoal goal) => goal >= TuningGoal.InteractiveChat;

    /// <summary>The preset's profile, with <paramref name="guards"/> replacing the default guards when given.</summary>
    public static WorkloadProfile For(TuningGoal goal, SloGuards? guards = null)
    {
        var profile = goal switch
        {
            TuningGoal.AgentsTools => new WorkloadProfile(goal, "Agents & tools",
                "Agent loops: tool calls that must validate against their schema, a long shared system prompt reused across questions, and decent concurrency.",
                Weights((ScoreComponent.ChatN1, 0.25), (ScoreComponent.ChatN16, 0.2), (ScoreComponent.LongPrompt16K, 0.15), (ScoreComponent.SharedPrefix, 0.3), (ScoreComponent.MixedShortTtft, 0.1)),
                new SloGuards(RequireToolValidity: true), [Long16K], SharedPrefix: true, Tools: true, Embeddings: false),
            TuningGoal.Throughput => new WorkloadProfile(goal, "Throughput / batch",
                "Most tokens per second under load: aggregate tok/s at N=16 and at the configuration's max num seqs. Latency guards are off.",
                Weights((ScoreComponent.ChatN16, 0.4), (ScoreComponent.ChatNMax, 0.6)),
                new SloGuards(ShortTtftP95AtN1Seconds: null, ShortTtftP95AtN16Seconds: null, LongTtftMaxRatio: null, MixedShortTtftP95Seconds: null, MaxPreemptions: null), [], SharedPrefix: false, Tools: false, Embeddings: false),
            TuningGoal.LongDocuments => new WorkloadProfile(goal, "Long documents",
                "Large prompts: cold 16K and 64K-token time to first token, plus single-stream decode speed.",
                Weights((ScoreComponent.ChatN1, 0.3), (ScoreComponent.LongPrompt16K, 0.35), (ScoreComponent.LongPrompt64K, 0.35)),
                new SloGuards(MixedShortTtftP95Seconds: null), [Long16K, Long64K], SharedPrefix: false, Tools: false, Embeddings: false),
            TuningGoal.Embeddings => new WorkloadProfile(goal, "Embeddings",
                "Embedding models: input tokens embedded per second at N=1 and N=16, with valid vectors and latency p95 at N=16 at most 2× the baseline's.",
                Weights((ScoreComponent.EmbeddingN1, 0.4), (ScoreComponent.EmbeddingN16, 0.6)),
                new SloGuards(null, null, null, false, 0, false, 2.0, null, null), [], SharedPrefix: false, Tools: false, Embeddings: true),
            _ => new WorkloadProfile(TuningGoal.InteractiveChat, "Interactive chat",
                "One person chatting: fast single-stream output, quick first token on short and 16K-token prompts, some concurrency.",
                Weights((ScoreComponent.ChatN1, 0.5), (ScoreComponent.ChatN16, 0.2), (ScoreComponent.LongPrompt16K, 0.2), (ScoreComponent.MixedShortTtft, 0.1)),
                new SloGuards(), [Long16K], SharedPrefix: false, Tools: false, Embeddings: false),
        };

        return guards is null ? profile : profile with { Guards = guards };
    }

    /// <summary>The mixed workload runs when the preset scores or guards it (Interactive chat, Agents &amp; tools).</summary>
    public static bool Mixed(WorkloadProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Weights.ContainsKey(ScoreComponent.MixedShortTtft) || profile.Guards.MixedShortTtftP95Seconds is not null;
    }

    /// <summary>The preset a classic goal (or a pooling model) maps to when a preset is needed.</summary>
    public static TuningGoal PresetFor(TuningGoal goal, bool pooling) =>
        pooling ? TuningGoal.Embeddings
        : IsPreset(goal) && goal != TuningGoal.Embeddings ? goal
        : goal == TuningGoal.ManyAgents ? TuningGoal.Throughput
        : TuningGoal.InteractiveChat;

    public static bool HigherIsBetter(ScoreComponent component) =>
        component is not (ScoreComponent.LongPrompt16K or ScoreComponent.LongPrompt64K or ScoreComponent.SharedPrefix or ScoreComponent.MixedShortTtft);

    public static string ComponentName(ScoreComponent component) => component switch
    {
        ScoreComponent.ChatN1 => "N=1 tok/s",
        ScoreComponent.ChatN16 => "N=16 aggregate",
        ScoreComponent.ChatNMax => "max-seqs aggregate",
        ScoreComponent.LongPrompt16K => "16K TTFT",
        ScoreComponent.LongPrompt64K => "64K TTFT",
        ScoreComponent.SharedPrefix => "shared-prefix TTFT",
        ScoreComponent.EmbeddingN1 => "N=1 input tok/s",
        ScoreComponent.MixedShortTtft => "mixed short TTFT p95",
        _ => "N=16 input tok/s",
    };

    private static Dictionary<ScoreComponent, double> Weights(params (ScoreComponent Component, double Weight)[] weights) =>
        weights.ToDictionary(w => w.Component, w => w.Weight);
}
