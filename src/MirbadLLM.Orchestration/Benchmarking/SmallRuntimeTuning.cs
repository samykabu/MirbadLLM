using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Multinode;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>
/// The small tune of an MLX or SGLang model, one dimension at a time from the saved settings, like
/// <see cref="LlamaCppTuning"/>. Only settings that change what the server does are tried:
/// <list type="bullet">
/// <item>MLX: parallel requests (not with a quantized KV cache, which makes the server serial), the KV cache bits, draft
/// tokens when the model has a drafter, and the prompt cache.</item>
/// <item>SGLang: <c>--max-running-requests</c>, <c>--schedule-policy</c>, and a shallower draft depth when speculative
/// decoding is on, never a deeper one, because a drafter's block size limits it.</item>
/// </list>
/// Memory limits such as <c>--mem-fraction-static</c>, the image, the drafter itself and the context are never varied.
/// SGLang's values live in <see cref="ContainerRuntimeSettings.ExtraArgs"/>, so Apply writes real launch arguments.
/// </summary>
public static class SmallRuntimeTuning
{
    /// <summary>Configurations measured at most, the baseline included.</summary>
    public const int MaxCandidates = 10;

    public const string MaxRunningRequestsFlag = "--max-running-requests";

    public const string SchedulePolicyFlag = "--schedule-policy";

    private static readonly int[] MlxParallelValues = [1, 2, 4, 8];

    private static readonly int[] MlxDraftTokenValues = [2, 3, 5];

    private static readonly string[] MlxPromptCacheValues = ["0", "0.5", "2"];

    private static readonly int[] SglangRunningValues = [4, 8, 16, 32];

    private static readonly string[] TunedFlags = [MaxRunningRequestsFlag, SchedulePolicyFlag];

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static readonly IReadOnlyList<TuningDimension> MlxDimensions =
        [TuningDimension.MlxParallel, TuningDimension.MlxKvBits, TuningDimension.MlxDraftTokens, TuningDimension.MlxPromptCache];

    public static readonly IReadOnlyList<TuningDimension> SglangDimensions =
        [TuningDimension.SglangMaxRunningRequests, TuningDimension.SglangSchedulePolicy, TuningDimension.SglangDraftDepth];

    /// <summary>True when <paramref name="model"/> is tuned by this class.</summary>
    public static bool Applies(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return (model.Runtime == RuntimeKind.Mlx && model.Mlx is not null) || (model.Runtime == RuntimeKind.Sglang && model.Container is not null);
    }

    public static IReadOnlyList<TuningDimension> DimensionsFor(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Runtime == RuntimeKind.Mlx ? MlxDimensions : SglangDimensions;
    }

    public static bool IsSmallDimension(TuningDimension dimension) => MlxDimensions.Contains(dimension) || SglangDimensions.Contains(dimension);

    public static string StepTitle(TuningDimension dimension) => dimension switch
    {
        TuningDimension.MlxParallel => "Parallel requests",
        TuningDimension.MlxKvBits => "KV cache bits",
        TuningDimension.MlxDraftTokens => "Draft tokens",
        TuningDimension.MlxPromptCache => "Prompt cache",
        TuningDimension.SglangMaxRunningRequests => "Max running requests",
        TuningDimension.SglangSchedulePolicy => "Schedule policy",
        TuningDimension.SglangDraftDepth => "Draft depth",
        _ => dimension.ToString(),
    };

    public static string CandidateLabel(TuningDimension dimension, TuningValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return $"{StepTitle(dimension)} {value.Label}";
    }

    public static TuningPlan Plan(
        ModelDefinition baseline, ModelFolderInspection? inspection, IReadOnlyCollection<TuningDimension>? dimensions = null,
        IReadOnlyCollection<TuningValueRef>? excluded = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (!Applies(baseline))
        {
            throw new ArgumentException($"Model '{baseline.Id}' is not tuned by the small tune.", nameof(baseline));
        }

        var all = DimensionsFor(baseline);
        bool Included(TuningDimension d) => dimensions is null || !dimensions.Any(IsSmallDimension) || dimensions.Contains(d);
        var steps = new List<TuningStepPlan>(all.Count);
        int budget = MaxCandidates - 1;
        foreach (var dimension in all)
        {
            var description = TuningDescriptions.Dimension(dimension).Text;
            if (!Included(dimension))
            {
                steps.Add(new TuningStepPlan(dimension, StepTitle(dimension), [], LlamaCppTuning.NotSelected, null, Description: description));
                continue;
            }

            var notes = new List<string>();
            var (candidates, skip) = Values(dimension, baseline);
            var values = new List<TuningValue>();
            foreach (var value in candidates)
            {
                var candidate = Apply(dimension, value, baseline);
                if (Fingerprint(candidate) == Fingerprint(baseline))
                {
                    continue; // the baseline's own value: already measured
                }

                if (Refusal(candidate, inspection) is { } refused)
                {
                    notes.Add($"{CandidateLabel(dimension, value)} skipped: {refused}");
                    continue;
                }

                values.Add(value);
            }

            if (values.Count > budget)
            {
                notes.Add(string.Create(Culture, $"Left out to stay within {MaxCandidates} candidates, the baseline included: {string.Join(", ", values.Skip(budget).Select(v => v.Label))}."));
                values = [.. values.Take(budget)];
            }

            budget -= values.Count;
            skip ??= values.Count == 0 ? "Skipped: no other value to try." : null;
            steps.Add(new TuningStepPlan(dimension, StepTitle(dimension), values, skip, notes.Count == 0 ? null : string.Join(" ", notes), Description: description));
        }

        steps = TuningPlanner.Without(steps, excluded);

        var preview = new List<TuningCandidatePlan> { new("Baseline", "Baseline (saved settings)", baseline, "Measure the saved settings as the reference every candidate is compared with.") };
        var seen = new HashSet<string>(StringComparer.Ordinal) { Fingerprint(baseline) };
        foreach (var step in steps.Where(s => s.SkipReason is null))
        {
            preview.AddRange(CandidatesFor(step, baseline, inspection).Where(c => seen.Add(Fingerprint(c.Settings))));
        }

        int max = 1 + steps.Where(s => s.SkipReason is null).Sum(s => s.Values.Count);
        var search = new SearchContext(TuningGates.NotApplicable, baseline, [.. all.Where(Included)]) { SmallRuntime = true, Inspection = inspection };
        return new TuningPlan(steps, preview, max, search);
    }

    /// <summary>The candidates of <paramref name="step"/> on top of <paramref name="best"/>, without its own values and those it refuses.</summary>
    public static IReadOnlyList<TuningCandidatePlan> CandidatesFor(
        TuningStepPlan step, ModelDefinition best, ModelFolderInspection? inspection, ICollection<string>? skipped = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(best);
        if (step.SkipReason is not null || !Applies(best))
        {
            return [];
        }

        var result = new List<TuningCandidatePlan>(step.Values.Count);
        foreach (var value in step.Values)
        {
            var candidate = Apply(step.Dimension, value, best);
            if (Fingerprint(candidate) == Fingerprint(best))
            {
                continue;
            }

            string label = CandidateLabel(step.Dimension, value);
            if (Refusal(candidate, inspection) is { } refused)
            {
                skipped?.Add($"{label} skipped: {refused}");
                continue;
            }

            result.Add(new TuningCandidatePlan(step.Title, label, candidate, $"{TuningDescriptions.Dimension(step.Dimension).Text} Try {value.Label}."));
        }

        return result;
    }

    /// <summary>Applies one value to <paramref name="best"/>; nothing else changes.</summary>
    public static ModelDefinition Apply(TuningDimension dimension, TuningValue value, ModelDefinition best)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(best);
        return dimension switch
        {
            TuningDimension.MlxParallel when best.Mlx is { } m && value.Number is { } n => best with { Mlx = m with { Parallel = n } },
            TuningDimension.MlxKvBits when best.Mlx is { } m => best with { Mlx = m with { KvBits = value.Number } },
            TuningDimension.MlxDraftTokens when best.Mlx is { } m && value.Number is { } n => best with { Mlx = m with { NumDraftTokens = n } },
            TuningDimension.MlxPromptCache when best.Mlx is { } m && double.TryParse(value.Text, NumberStyles.Float, Culture, out double cache) =>
                best with { Mlx = m with { PromptCacheGiB = cache } },
            TuningDimension.SglangMaxRunningRequests when best.Container is { } c && value.Number is { } n =>
                best with { Container = c with { ExtraArgs = SetFlag(c.ExtraArgs, MaxRunningRequestsFlag, n.ToString(Culture)) } },
            TuningDimension.SglangSchedulePolicy when best.Container is { } c && value.Text is { } policy =>
                best with { Container = c with { ExtraArgs = SetFlag(c.ExtraArgs, SchedulePolicyFlag, policy) } },
            TuningDimension.SglangDraftDepth when best.Speculative is { } s && value.Number is { } n =>
                best with { Speculative = s with { NumSpeculativeTokens = n } },
            _ => best,
        };
    }

    /// <summary>The identity of a launch: what each candidate's server would really be started with.</summary>
    public static string Fingerprint(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var text = new StringBuilder(model.Runtime.ToString());
        if (model.Runtime == RuntimeKind.Mlx)
        {
            text.Append('\0').Append(model.Mlx?.ExecutableVersion);
            foreach (string arg in MlxArgsBuilder.Build(model))
            {
                text.Append('\0').Append(arg);
            }
        }
        else
        {
            text.Append('\0').Append(model.Container?.Image).Append('\0').Append(model.Container?.ImageDigest)
                .Append('\0').Append(model.Speculative?.Method).Append('\0').Append(model.Speculative?.NumSpeculativeTokens).Append('\0').Append(model.Speculative?.DraftModel);
            foreach (string arg in model.Container?.ExtraArgs ?? [])
            {
                text.Append('\0').Append(arg);
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())).AsSpan(0, 8));
    }

    /// <summary>
    /// <paramref name="saved"/> with a candidate's tuned settings laid over it: only what this tune varies, so anything the
    /// operator changed in the extra arguments since the run stays.
    /// </summary>
    public static ModelDefinition MergeTuned(ModelDefinition saved, ModelDefinition candidate)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(candidate);
        if (saved.Mlx is { } m && candidate.Mlx is { } tuned)
        {
            return saved with
            {
                Mlx = m with { Parallel = tuned.Parallel, KvBits = tuned.KvBits, NumDraftTokens = tuned.NumDraftTokens, PromptCacheGiB = tuned.PromptCacheGiB },
            };
        }

        if (saved.Container is not { } c || candidate.Container is not { } t)
        {
            return saved;
        }

        var args = c.ExtraArgs;
        foreach (string flag in TunedFlags)
        {
            args = FlagValue(t.ExtraArgs, flag) is { } value ? SetFlag(args, flag, value) : RemoveFlag(args, flag);
        }

        return saved with
        {
            Container = c with { ExtraArgs = args },
            Speculative = saved.Speculative is { } s && candidate.Speculative is { } ts ? s with { NumSpeculativeTokens = ts.NumSpeculativeTokens } : saved.Speculative,
        };
    }

    /// <summary><paramref name="args"/> with <paramref name="flag"/> set once, whichever form it was written in.</summary>
    public static IReadOnlyList<string> SetFlag(IReadOnlyList<string> args, string flag, string value) => [.. RemoveFlag(args, flag), flag, value];

    private static List<string> RemoveFlag(IReadOnlyList<string> args, string flag)
    {
        var kept = new List<string>(args.Count);
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == flag)
            {
                i++; // and its value
            }
            else if (!args[i].StartsWith(flag + "=", StringComparison.Ordinal) && !args[i].StartsWith(flag + " ", StringComparison.Ordinal))
            {
                kept.Add(args[i]);
            }
        }

        return kept;
    }

    private static string? FlagValue(IReadOnlyList<string> args, string flag)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == flag && i + 1 < args.Count)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith(flag + "=", StringComparison.Ordinal))
            {
                return args[i][(flag.Length + 1)..];
            }
        }

        return null;
    }

    private static (List<TuningValue> Values, string? Skip) Values(TuningDimension dimension, ModelDefinition baseline)
    {
        switch (dimension)
        {
            case TuningDimension.MlxParallel when baseline.Mlx?.KvBits is not null:
                return ([], "Skipped: a quantized KV cache makes mlx_lm.server serve one request at a time.");
            case TuningDimension.MlxParallel:
                return ([.. MlxParallelValues.Select(n => new TuningValue(n.ToString(Culture), Number: n))], null);
            case TuningDimension.MlxKvBits:
                return ([new TuningValue("off"), new TuningValue("8", Number: 8), new TuningValue("4", Number: 4)], null);
            case TuningDimension.MlxDraftTokens when baseline.Mlx?.DraftModelFolder is null:
                return ([], "Skipped: this model has no draft model.");
            case TuningDimension.MlxDraftTokens:
                return ([.. MlxDraftTokenValues.Select(n => new TuningValue(n.ToString(Culture), Number: n))], null);
            case TuningDimension.MlxPromptCache:
                return ([.. MlxPromptCacheValues.Select(gib => new TuningValue(gib + " GiB", Text: gib))], null);
            case TuningDimension.SglangMaxRunningRequests:
                // Compared by value, because the saved flag may be written --flag=8, which hashes differently from the same setting.
                string? running = FlagValue(baseline.Container?.ExtraArgs ?? [], MaxRunningRequestsFlag);
                return ([.. SglangRunningValues.Where(n => n.ToString(Culture) != running).Select(n => new TuningValue(n.ToString(Culture), Number: n))], null);
            case TuningDimension.SglangSchedulePolicy:
                // SGLang's default is fcfs, so a model without the flag only has lpm left to try.
                string policy = FlagValue(baseline.Container?.ExtraArgs ?? [], SchedulePolicyFlag) ?? "fcfs";
                return ([new TuningValue(policy == "lpm" ? "fcfs" : "lpm", Text: policy == "lpm" ? "fcfs" : "lpm")], null);
            case TuningDimension.SglangDraftDepth when baseline.Speculative is not { NumSpeculativeTokens: > 1 }:
                return ([], "Skipped: this model has no speculative decoding to adjust.");
            case TuningDimension.SglangDraftDepth:
                int depth = baseline.Speculative!.NumSpeculativeTokens;
                return ([.. new[] { depth / 2, depth * 3 / 4 }.Where(n => n >= 1 && n < depth).Distinct().Select(n => new TuningValue(n.ToString(Culture), Number: n))], null);
            default:
                return ([], null);
        }
    }

    /// <summary>Why <paramref name="candidate"/> can't run: MLX's own rules, or an estimate above the budget admission reserves.</summary>
    private static string? Refusal(ModelDefinition candidate, ModelFolderInspection? inspection)
    {
        if (candidate.Mlx is not { } settings)
        {
            return null;
        }

        if (MlxArgsBuilder.Problems(candidate) is { Count: > 0 } problems)
        {
            return problems[0].Message;
        }

        if (inspection is null)
        {
            return null;
        }

        double needs = MlxArgsBuilder.Estimate(settings, inspection).BudgetGiB;
        return needs > candidate.MemoryBudgetGiB + 1e-9
            ? string.Create(Culture, $"needs {needs:0.00} GiB, more than the {candidate.MemoryBudgetGiB:0.##} GiB budget")
            : null;
    }
}
