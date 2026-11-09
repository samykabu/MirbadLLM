using System.Globalization;
using MirbadLLM.Core;

namespace MirbadLLM.Web;

/// <summary>One setting that differs, in the editor's words: "Speculative decoding: off → MTP × 2".</summary>
public sealed record SettingChange(string Label, string Old, string New)
{
    public override string ToString() => $"{Label}: {Old} → {New}";
}

/// <summary>
/// Which settings differ between two definitions and need a container restart to take effect. Every vLLM
/// setting is a launch argument, so everything except <see cref="ModelDefinition.AutoStart"/> needs one — the
/// served name included (it is <c>--served-model-name</c>) — and so do the image digest and the placement's GPUs
/// (P2, MN-118); tags, sources and keep-serving do not.
/// </summary>
public static class SettingChanges
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>Everything that differs and needs a restart, on whichever runtime the model uses.</summary>
    public static IReadOnlyList<SettingChange> AllRestartNeeded(ModelDefinition before, ModelDefinition after) =>
        [.. RestartNeeded(before, after), .. LlamaCppEditing.RestartNeeded(before, after), .. MlxEditing.RestartNeeded(before, after)];

    public static IReadOnlyList<SettingChange> RestartNeeded(ModelDefinition before, ModelDefinition after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changes = new List<SettingChange>();
        void Add(string label, string old, string @new)
        {
            if (!string.Equals(old, @new, StringComparison.Ordinal))
            {
                changes.Add(new SettingChange(label, old, @new));
            }
        }

        Add("Served name", before.ServedName, after.ServedName);
        Add("Model path", before.ModelPath, after.ModelPath);
        Add("Memory budget", UiFormat.GiB(before.MemoryBudgetGiB), UiFormat.GiB(after.MemoryBudgetGiB));
        Add("Max model len", Tokens(before.MaxModelLen), Tokens(after.MaxModelLen));
        Add("Max num seqs", before.MaxNumSeqs.ToString(Culture), after.MaxNumSeqs.ToString(Culture));
        Add("Max batched tokens", Batched(before.MaxNumBatchedTokens), Batched(after.MaxNumBatchedTokens));
        Add("KV cache dtype", before.KvCacheDtype, after.KvCacheDtype);
        Add("Quantization", before.Quantization ?? "none", after.Quantization ?? "none");
        Add("Tool call parser", before.ToolCallParser ?? "none", after.ToolCallParser ?? "none");
        Add("Reasoning parser", before.ReasoningParser ?? "none", after.ReasoningParser ?? "none");
        Add("Prefix caching", OnOff(before.EnablePrefixCaching), OnOff(after.EnablePrefixCaching));
        Add("Speculative decoding", SpeculativeDetail(before.Speculative), SpeculativeDetail(after.Speculative));
        Add("CUDA graph mode", before.CudaGraphMode ?? "default", after.CudaGraphMode ?? "default");
        Add("Compilation config", Json(before.CompilationConfig), Json(after.CompilationConfig));
        Add("Async scheduling", OnOff(before.AsyncScheduling), OnOff(after.AsyncScheduling));
        Add("Mamba cache mode", before.MambaCacheMode ?? "default", after.MambaCacheMode ?? "default");
        Add("Image", before.Image ?? "default", after.Image ?? "default");
        Add("Image digest", before.ImageDigest ?? "none", after.ImageDigest ?? "none");
        Add("Extra args", ExtraArgs(before.ExtraArgs), ExtraArgs(after.ExtraArgs));
        Add("Container image", before.Container?.Image ?? "none", after.Container?.Image ?? "none");
        Add("Container image digest", before.Container?.ImageDigest ?? "none", after.Container?.ImageDigest ?? "none");
        Add("Container extra args", ExtraArgs(before.Container?.ExtraArgs ?? []), ExtraArgs(after.Container?.ExtraArgs ?? []));
        Add("Environment variables", Variables(before.Container?.Env), Variables(after.Container?.Env));
        // Token names only: the values never reach this page.
        Add("Secret variables", Variables(before.Container?.SecretEnv), Variables(after.Container?.SecretEnv));
        Add("Registry login", Login(before.Container?.RegistryLogin), Login(after.Container?.RegistryLogin));

        // Placement GPUs (design §5.1, §6.3; MN-118): another card or another count is a launch change (device selection,
        // --tensor-parallel-size, the per-GPU utilization). Compared by UUID, the GPU's identity, so a renumbering that
        // keeps the cards is no change; the node itself changes only through a move, which restarts anyway.
        if (!(before.Placement?.GpuUuids ?? []).SequenceEqual(after.Placement?.GpuUuids ?? [], StringComparer.Ordinal))
        {
            changes.Add(new SettingChange("GPUs", Gpus(before.Placement), Gpus(after.Placement)));
        }

        return changes;
    }

    /// <summary>"GPU 0 (GPU-5967…), GPU 1 (GPU-77ab…)" by the placement's display indices, the UUIDs when it has none, "none".</summary>
    public static string Gpus(ModelPlacement? placement)
    {
        var uuids = placement?.GpuUuids ?? [];
        if (uuids.Count == 0)
        {
            return "none";
        }

        var indices = placement!.GpuIndices is { } shown && shown.Count == uuids.Count ? shown : null;
        return string.Join(", ", uuids.Select((uuid, i) => indices is null
            ? uuid
            : string.Create(Culture, $"GPU {indices[i]} ({uuid})")));
    }

    /// <summary>"MTP × 2", "n-gram × 3", "draft × 3" (a draft model without a method), "off".</summary>
    public static string Speculative(SpeculativeDecoding? speculative) => speculative switch
    {
        null => "off",
        { Method: "mtp" } s => string.Create(Culture, $"MTP × {s.NumSpeculativeTokens}"),
        { Method: "ngram" } s => string.Create(Culture, $"n-gram × {s.NumSpeculativeTokens}"),
        { Method: "" } s => string.Create(Culture, $"draft × {s.NumSpeculativeTokens}"),
        var s => string.Create(Culture, $"{s.Method} × {s.NumSpeculativeTokens}"),
    };

    /// <summary><see cref="Speculative"/> plus the draft model and any other keys, so a change to those shows too.</summary>
    public static string SpeculativeDetail(SpeculativeDecoding? speculative)
    {
        if (speculative is null)
        {
            return "off";
        }

        var parts = new List<string> { Speculative(speculative) };
        if (!string.IsNullOrEmpty(speculative.DraftModel))
        {
            parts.Add("draft " + speculative.DraftModel);
        }

        if (!speculative.Extra.IsEmpty)
        {
            parts.Add(speculative.Extra.ToJson());
        }

        return string.Join(", ", parts);
    }

    public static string Batched(int? tokens) => tokens is { } t ? t.ToString("N0", Culture) : "default";

    /// <summary>The tuned settings in one line, e.g. "MTP × 2 · PIECEWISE · seqs 16 · batched default · KV fp8".</summary>
    public static string Summary(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return string.Join(" · ",
            Speculative(model.Speculative) == "off" ? "speculative off" : Speculative(model.Speculative),
            "graphs " + (model.CudaGraphMode ?? "default"),
            string.Create(Culture, $"seqs {model.MaxNumSeqs}"),
            "batched " + Batched(model.MaxNumBatchedTokens),
            "KV " + model.KvCacheDtype);
    }

    /// <summary>
    /// "Restarting takes about 5 min (last start took 4m 12s); clients get 503 'starting' until it is Ready."
    /// </summary>
    public static string RestartEstimate(TimeSpan? lastStart)
    {
        const string Tail = "clients get 503 'starting' until it is Ready.";
        if (lastStart is not { } last)
        {
            return $"Restarting usually takes 2–5 min (no start of this model has been measured yet); {Tail}";
        }

        int minutes = Math.Max(1, (int)Math.Ceiling(last.TotalMinutes));
        return string.Create(Culture, $"Restarting takes about {minutes} min (last start took {UiFormat.Duration(last)}); {Tail}");
    }

    private static string Tokens(int value) => value.ToString("N0", Culture);

    private static string OnOff(bool? value) => value switch
    {
        true => "on",
        false => "off",
        null => "default",
    };

    private static string Json(JsonFields? fields) => fields is null || fields.IsEmpty ? "none" : fields.ToJson();

    private static string Variables(IReadOnlyDictionary<string, string>? variables) =>
        variables is not { Count: > 0 } ? "none" : ContainerEditing.Format(variables).Replace('\n', ' ');

    private static string Login(ContainerRegistryLogin? login) =>
        login is null ? "none" : $"{login.Username} at {login.Host}, token {login.TokenName}";

    private static string ExtraArgs(IReadOnlyList<string> args) => args.Count == 0 ? "none" : string.Join(' ', args);
}
