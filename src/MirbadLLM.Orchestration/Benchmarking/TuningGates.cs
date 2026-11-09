using System.Globalization;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>
/// Capability gates for the workload-preset search, computed without measuring anything: which speculative methods
/// and depths the model can run, whether prefix caching must be paired with a mamba cache mode, whether an auto
/// (BF16) KV cache fits the budget, and the concurrency / token-budget values worth trying.
/// </summary>
/// <param name="MtpDepths">MTP depths to try; empty when MTP is gated off (<see cref="MtpSkipReason"/>).</param>
/// <param name="DraftDepths">Draft-model depths to try; empty when there is no drafter (<see cref="DraftSkipReason"/>).</param>
/// <param name="MambaHybrid">Prefix caching on pairs with <c>--mamba-cache-mode align</c>, off with <c>none</c>.</param>
/// <param name="KvAutoSkipReason">Why an auto (BF16) KV cache is not tried; null = it is. FP8 is always eligible.</param>
/// <param name="Notes">Why a step was narrowed (e.g. the drafter's depth is unknown).</param>
public sealed record TuningGates(
    IReadOnlyList<int> MtpDepths,
    string? MtpSkipReason,
    IReadOnlyList<int> DraftDepths,
    string? DraftSkipReason,
    bool MambaHybrid,
    string? KvAutoSkipReason,
    IReadOnlyList<int> MaxNumSeqsValues,
    IReadOnlyList<int> BatchedTokenValues,
    IReadOnlyList<string> Notes)
{
    public static readonly IReadOnlyList<int> MtpCandidateDepths = [1, 2, 3, 5];
    public static readonly IReadOnlyList<int> DraftCandidateDepths = [3, 7, 11];
    public static readonly IReadOnlyList<int> SeqsValues = [4, 8, 16, 32, 64];
    public static readonly IReadOnlyList<int> SmallSeqsValues = [1, 2];
    public static readonly IReadOnlyList<int> BatchedValues = [2048, 4096, 8192, 16384, 32768];

    /// <summary>
    /// The gates of a model the vLLM gates do not apply to (a llama.cpp model, MN-225: <see cref="LlamaCppTuning"/> decides
    /// its candidates): nothing to try, every vLLM-only step skipped with this reason.
    /// </summary>
    public static readonly TuningGates NotApplicable = new(
        [], "Not applicable to llama.cpp models.", [], "Not applicable to llama.cpp models.", false, "Not applicable to llama.cpp models.", [], [], []);

    /// <summary>Dense models above this size also try max num seqs 1 and 2 (vLLM's DGX Spark guidance keeps big dense models low).</summary>
    public const double LargeDenseBillions = 20;

    /// <param name="baseline">The normalized saved definition.</param>
    /// <param name="inspection">The checkpoint inspection; null when config.json could not be read.</param>
    /// <param name="inspectFolder">Inspects another folder under the models root (the draft model).</param>
    /// <param name="mtpArchitectures">The image's registered MTP architectures; empty = unknown.</param>
    public static TuningGates Compute(
        ModelDefinition baseline, ModelFolderInspection? inspection, Func<string, ModelFolderInspection?>? inspectFolder,
        IReadOnlyCollection<string> mtpArchitectures)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(mtpArchitectures);
        var notes = new List<string>();

        var (mtpDepths, mtpSkip) = MtpGate(baseline, inspection, mtpArchitectures);
        var (draftDepths, draftSkip, draftNote) = DraftGate(baseline, inspectFolder);
        if (draftNote is not null)
        {
            notes.Add(draftNote);
        }

        bool hybrid = inspection?.IsMambaHybrid == true || !string.IsNullOrEmpty(baseline.MambaCacheMode);

        var seqs = SeqsValues.ToList();
        if (inspection is { IsMoe: false } dense && dense.ParamsBillions > LargeDenseBillions)
        {
            seqs.InsertRange(0, SmallSeqsValues);
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"Max num seqs also tries 1 and 2: a dense model of about {dense.ParamsBillions:0} B parameters."));
        }

        var batched = BatchedValues.ToList();
        if (baseline.MaxNumBatchedTokens is { } saved && !batched.Contains(saved))
        {
            batched.Add(saved);
            batched.Sort();
        }

        bool noChunkedPrefill = (baseline.ExtraArgs ?? []).Any(a => a.Trim().StartsWith("--no-enable-chunked-prefill", StringComparison.Ordinal));
        if (noChunkedPrefill)
        {
            batched = batched.Where(b => b >= baseline.MaxModelLen).ToList();
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"Max batched tokens below max model len ({baseline.MaxModelLen:N0}) are left out: chunked prefill is disabled."));
        }

        return new TuningGates(mtpDepths, mtpSkip, draftDepths, draftSkip, hybrid, TuningPlanner.KvAutoSkipReason(baseline, inspection), seqs, batched, notes);
    }

    private static (IReadOnlyList<int> Depths, string? Skip) MtpGate(ModelDefinition baseline, ModelFolderInspection? inspection, IReadOnlyCollection<string> registry)
    {
        if (inspection is null)
        {
            return ([], "MTP skipped: the checkpoint's config.json could not be read.");
        }

        if (!inspection.MaySupportMtp)
        {
            return ([], "MTP skipped: the checkpoint has no MTP layers (mtp_num_hidden_layers is 0).");
        }

        string architecture = inspection.Architecture ?? "";
        if (registry.Count > 0)
        {
            string? match = registry.FirstOrDefault(r => MtpMatches(architecture, r));
            return match is null
                ? ([], $"MTP skipped: the image registers no MTP architecture for {architecture} (MirbadLLM:MtpArchitectures).")
                : (MtpCandidateDepths, null);
        }

        // The registry is unknown: only the saved configuration running MTP proves the image supports it.
        return baseline.Speculative is { Method: "mtp" }
            ? (MtpCandidateDepths, null)
            : ([], $"MTP skipped: can't confirm the image registers an MTP architecture for {architecture}; list it in MirbadLLM:MtpArchitectures.");
    }

    /// <summary>
    /// <paramref name="registered"/> (e.g. <c>Qwen3_5MoeMTP</c>) serves <paramref name="architecture"/>
    /// (<c>Qwen3_5MoeForConditionalGeneration</c>): its name without "MTP" prefixes the architecture's base name.
    /// </summary>
    public static bool MtpMatches(string architecture, string registered)
    {
        ArgumentNullException.ThrowIfNull(architecture);
        ArgumentNullException.ThrowIfNull(registered);
        if (!registered.EndsWith("MTP", StringComparison.Ordinal) || registered.Length <= 3)
        {
            return false;
        }

        string family = registered[..^3];
        string baseName = architecture;
        foreach (string suffix in new[] { "ForConditionalGeneration", "ForCausalLM" })
        {
            if (baseName.EndsWith(suffix, StringComparison.Ordinal))
            {
                baseName = baseName[..^suffix.Length];
            }
        }

        return baseName.StartsWith(family, StringComparison.Ordinal);
    }

    private static (IReadOnlyList<int> Depths, string? Skip, string? Note) DraftGate(ModelDefinition baseline, Func<string, ModelFolderInspection?>? inspectFolder)
    {
        if (baseline.Speculative is not { DraftModel: { Length: > 0 } draft } speculative)
        {
            return ([], "Draft-model depths skipped: no draft model is configured.", null);
        }

        int saved = speculative.NumSpeculativeTokens;
        string folder = draft.StartsWith("/models/", StringComparison.Ordinal) ? draft["/models/".Length..].TrimEnd('/') : draft;
        int? blockSize = null;
        try
        {
            blockSize = inspectFolder?.Invoke(folder)?.SpeculativeBlockSize;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            blockSize = null;
        }

        if (blockSize is not { } size)
        {
            return (saved > 0 ? [saved] : [], null,
                "Draft model: its trained block size is unknown, so only the saved depth (and off) are tried.");
        }

        var depths = DraftCandidateDepths.Append(saved).Where(d => d > 0 && d <= size).Distinct().Order().ToList();
        return (depths, null, string.Create(CultureInfo.InvariantCulture, $"Draft model: depths up to its trained block size of {size}."));
    }
}

/// <summary>What the adaptive parts of a workload-preset search (follow-ups, refinement) need: the gates and the baseline.</summary>
public sealed record SearchContext(TuningGates Gates, ModelDefinition Baseline, IReadOnlyCollection<TuningDimension> Dimensions)
{
    public bool Includes(TuningDimension dimension) => Dimensions.Contains(dimension);

    /// <summary>
    /// Set on a llama.cpp model's plan (<see cref="LlamaCppTuning.Plan"/>, MN-225): <see cref="SearchPlanner.CandidatesFor"/>
    /// then builds the llama.cpp candidates (with their memory checks) and there are no follow-ups. Null for vLLM.
    /// </summary>
    public LlamaCppTuningContext? LlamaCpp { get; init; }

    /// <summary>True on an MLX or SGLang plan (<see cref="SmallRuntimeTuning.Plan"/>): it builds the candidates, and there are no follow-ups.</summary>
    public bool SmallRuntime { get; init; }

    /// <summary>The checkpoint inspection behind an MLX plan's memory checks.</summary>
    public ModelFolderInspection? Inspection { get; init; }
}
