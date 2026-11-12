using System.Globalization;
using System.Text;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Multinode;

namespace MirbadLLM.Web;

/// <summary>
/// The llama.cpp settings as the model editor edits them (design-p3p4 §A.7, MN-226): plain strings and nullable numbers the
/// form binds to, read from and written back into <see cref="LlamaCppSettings"/>. Members the form does not show
/// (<see cref="LlamaCppSettings.Image"/>, <see cref="LlamaCppSettings.SplitMode"/>) are kept from the stored settings.
/// </summary>
public sealed class LlamaCppForm
{
    /// <summary>The GGUF file relative to the model folder (the first part of a split model).</summary>
    public string File { get; set; } = "";

    /// <summary>The vision projector (<c>--mmproj</c>) relative to the model folder; empty = none.</summary>
    public string ProjectorFile { get; set; } = "";

    /// <summary>The draft GGUF (<c>-md</c>) relative to the models root, since a drafter is its own download; empty = none.</summary>
    public string DraftModelFile { get; set; } = "";

    /// <summary>Context per request (per slot).</summary>
    public int ContextSize { get; set; } = 32_768;

    /// <summary>Parallel slots.</summary>
    public int Parallel { get; set; } = 4;

    /// <summary>Layers offloaded to the GPUs (999 = all).</summary>
    public int GpuLayers { get; set; } = 999;

    /// <summary>"" = auto, "on", "off".</summary>
    public string FlashAttention { get; set; } = "";

    /// <summary>K cache type; "" = llama.cpp's default (f16).</summary>
    public string CacheTypeK { get; set; } = "";

    /// <summary>V cache type; "" = llama.cpp's default (f16).</summary>
    public string CacheTypeV { get; set; } = "";

    /// <summary><c>-b</c>; null = default.</summary>
    public int? BatchSize { get; set; }

    /// <summary><c>-ub</c>; null = default (for an embedding model the builder's <see cref="LlamaCppSettings.EmbeddingMicroBatch"/>).</summary>
    public int? MicroBatchSize { get; set; }

    /// <summary>
    /// True while <see cref="MicroBatchSize"/> is the editor's pre-fill for a non-causal embedding GGUF (= the context, D118);
    /// it then follows the context until the operator types a micro-batch of their own.
    /// </summary>
    public bool MicroBatchFollowsContext { get; set; }

    /// <summary>Embedding mode.</summary>
    public bool Embedding { get; set; }

    /// <summary>Pooling; "" = the GGUF's own <c>pooling_type</c>.</summary>
    public string Pooling { get; set; } = "";

    /// <summary>The router's <c>dimensions</c> emulation; null = the default (on for embedding models).</summary>
    public bool? EmulateDimensions { get; set; }

    /// <summary>Chat template: "" = the GGUF's own, "builtin", "file".</summary>
    public string ChatTemplateMode { get; set; } = "";

    /// <summary>Built-in template name (without <c>builtin:</c>).</summary>
    public string ChatTemplateName { get; set; } = "chatml";

    /// <summary>Template file in the model folder.</summary>
    public string ChatTemplateFile { get; set; } = "";

    /// <summary>"" = auto, "on", "off".</summary>
    public string Reasoning { get; set; } = "";

    /// <summary>CPU threads; null = llama.cpp's choice.</summary>
    public int? Threads { get; set; }

    /// <summary>Pinned <c>llama-server</c> version; "" = the node's default.</summary>
    public string ExecutableVersion { get; set; } = "";

    /// <summary>Extra arguments, one flag (with its value) per line.</summary>
    public string ExtraArgsText { get; set; } = "";

    /// <summary>Per-GPU budgets in placement order (null entries = not typed); empty = derived from capacity.</summary>
    public List<double?> GpuBudgets { get; set; } = [];

    /// <summary>The stored settings (kept members), null for a new model.</summary>
    public LlamaCppSettings? Baseline { get; private set; }

    /// <summary>The stored settings in the form; <paramref name="folder"/> is the model's folder (<see cref="ModelDefinition.ModelPath"/>).</summary>
    public static LlamaCppForm From(LlamaCppSettings settings, string folder)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var form = new LlamaCppForm
        {
            Baseline = settings,
            File = Relative(settings.ModelFile, folder),
            ProjectorFile = settings.ProjectorFile is { } projector ? Relative(projector, folder) : "",
            DraftModelFile = settings.DraftModelFile ?? "",
            ContextSize = settings.ContextSize,
            Parallel = settings.Parallel,
            GpuLayers = settings.GpuLayers,
            FlashAttention = TriState(settings.FlashAttention),
            CacheTypeK = settings.CacheTypeK ?? "",
            CacheTypeV = settings.CacheTypeV ?? "",
            BatchSize = settings.BatchSize,
            MicroBatchSize = settings.MicroBatchSize,
            Embedding = settings.Embedding,
            Pooling = settings.Pooling ?? "",
            EmulateDimensions = settings.EmulateDimensions,
            Reasoning = TriState(settings.Reasoning),
            Threads = settings.Threads,
            ExecutableVersion = settings.ExecutableVersion ?? "",
            ExtraArgsText = LlamaCppEditing.FormatExtraArgs(settings.ExtraArgs),
            GpuBudgets = settings.GpuBudgetsGiB is { } budgets ? [.. budgets.Select(b => (double?)b)] : [],
        };

        if (LlamaCppChatTemplate.TryParse(settings.ChatTemplate, out var template))
        {
            form.ChatTemplateMode = template.BuiltinName is not null ? "builtin" : "file";
            form.ChatTemplateName = template.BuiltinName ?? form.ChatTemplateName;
            form.ChatTemplateFile = template.File ?? "";
        }
        else if (settings.ChatTemplate is { } raw)
        {
            // An unparsable stored value is shown as a file name, so a save reports it instead of dropping it.
            form.ChatTemplateMode = "file";
            form.ChatTemplateFile = raw;
        }

        return form;
    }

    /// <summary>The router emulates <c>dimensions</c> (the switch's state).</summary>
    public bool EmulatesDimensions => EmulateDimensions ?? Embedding;

    /// <summary>
    /// The settings this form describes for a model in <paramref name="folder"/> on <paramref name="gpuCount"/> GPUs, delivered
    /// as <paramref name="delivery"/>. Per-GPU budgets are written only on two or more GPUs and only when every one is typed.
    /// </summary>
    public LlamaCppSettings ToSettings(string folder, LaunchDelivery delivery, int gpuCount)
    {
        string root = (folder ?? "").Trim().Trim('/');
        return (Baseline ?? new LlamaCppSettings { ModelFile = "" }) with
        {
            ModelFile = Join(root, File),
            ProjectorFile = string.IsNullOrWhiteSpace(ProjectorFile) ? null : Join(root, ProjectorFile),
            DraftModelFile = Blank(DraftModelFile),
            ContextSize = ContextSize,
            Parallel = Parallel,
            GpuLayers = GpuLayers,
            FlashAttention = FromTriState(FlashAttention),
            CacheTypeK = Blank(CacheTypeK),
            CacheTypeV = Blank(CacheTypeV),
            BatchSize = BatchSize,
            MicroBatchSize = MicroBatchSize,
            Delivery = delivery,
            ExtraArgs = LlamaCppEditing.ParseExtraArgs(ExtraArgsText),
            Embedding = Embedding,
            Pooling = Embedding ? Blank(Pooling) : null,
            EmulateDimensions = EmulateDimensions,
            ChatTemplate = ChatTemplateMode switch
            {
                "builtin" => LlamaCppChatTemplate.BuiltinPrefix + (ChatTemplateName ?? "").Trim(),
                "file" => Blank(ChatTemplateFile),
                _ => null,
            },
            Reasoning = FromTriState(Reasoning),
            Threads = Threads,
            GpuBudgetsGiB = gpuCount >= 2 && GpuBudgets.Count == gpuCount && GpuBudgets.TrueForAll(b => b is not null)
                ? [.. GpuBudgets.Select(b => b!.Value)]
                : null,
            ExecutableVersion = Blank(ExecutableVersion),
        };
    }

    /// <summary>Sets the context; a micro-batch pre-filled from a non-causal GGUF follows it (D118).</summary>
    public void SetContextSize(int value)
    {
        ContextSize = value;
        if (MicroBatchFollowsContext)
        {
            MicroBatchSize = value;
        }
    }

    /// <summary>
    /// Pre-fills the form from a GGUF header (design-p3p4 §A.1.7, D118): the file, embedding mode and pooling from its
    /// <c>pooling_type</c>, the context capped at its training context, and for a non-causal embedding model
    /// <see cref="MicroBatchSize"/> = the context (its whole input must fit one micro-batch); a causal one leaves it empty
    /// (the builder's <c>min(context, 2048)</c> applies).
    /// </summary>
    public void ApplyGguf(GgufSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        File = summary.File;
        if (summary.ContextLength is { } trained && trained > 0 && trained < ContextSize)
        {
            ContextSize = (int)trained;
        }

        Embedding = summary.PoolingType is { } pooling && !string.Equals(pooling, LlamaCppPooling.None, StringComparison.Ordinal);
        Pooling = Embedding && LlamaCppPooling.IsValid(summary.PoolingType) ? summary.PoolingType! : "";
        if (Embedding && summary.CausalAttention == false)
        {
            MicroBatchSize = ContextSize;
            MicroBatchFollowsContext = true;
        }
        else if (MicroBatchFollowsContext)
        {
            MicroBatchSize = null;
            MicroBatchFollowsContext = false;
        }
    }

    private static string Relative(string path, string folder)
    {
        string prefix = (folder ?? "").Trim().Trim('/') + "/";
        return prefix.Length > 1 && path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
    }

    private static string Join(string folder, string file)
    {
        string name = (file ?? "").Trim().TrimStart('/');
        return folder.Length == 0 ? name : folder + "/" + name;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string TriState(bool? value) => value switch
    {
        true => "on",
        false => "off",
        null => "",
    };

    private static bool? FromTriState(string? value) => value switch
    {
        "on" => true,
        "off" => false,
        _ => null,
    };
}

/// <summary>One GPU as the GGUF picker judges a file against it: its label and free memory after its reserve.</summary>
/// <param name="Label">"GPU 0 · NVIDIA GeForce RTX 5060 Ti · 16.0 GiB", or the node's name on a unified node.</param>
/// <param name="FreeGiB">Free after the reserve, from the fit preview; null without a fresh reading.</param>
/// <param name="ReserveGiB">The reserve applied.</param>
public sealed record GgufGpuRoom(string Label, double? FreeGiB, double ReserveGiB);

/// <summary>
/// The model editor's llama.cpp rules and words (design-p3p4 §A.7, MN-226), kept out of the components so they are testable
/// on their own: whether a node runs llama.cpp and how, the version choices, the ExtraArgs text and its refusals, the fit
/// of a GGUF on each GPU, and the settings a restart applies.
/// </summary>
public static class LlamaCppEditing
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>The KV cache types the editor offers (llama-server's <c>-ctk</c>/<c>-ctv</c>, those the estimate knows).</summary>
    public static IReadOnlyList<string> CacheTypes { get; } = ["f16", "bf16", "f32", "q8_0", "q5_1", "q5_0", "q4_1", "q4_0", "iq4_nl"];

    /// <summary>
    /// The node's llama.cpp runtime from its capabilities: the native process one when it reports it (Windows), else a
    /// container one; null when the node runs no llama.cpp (the runtime option is then not offered).
    /// </summary>
    public static RuntimeCapability? Runtime(NodeCapabilities? capabilities)
    {
        var all = (capabilities?.Runtimes ?? []).Where(r => r.Kind == RuntimeKind.LlamaCpp).ToList();
        return all.FirstOrDefault(r => r.Delivery == LaunchDelivery.Process) ?? all.FirstOrDefault();
    }

    /// <summary>The llama.cpp runtime of an editor node (<see cref="Runtime(NodeCapabilities?)"/>).</summary>
    public static RuntimeCapability? Runtime(PlacementNode? node) =>
        node is null ? null : Runtime(node.Record.LastCapabilities ?? node.Live?.Capabilities);

    /// <summary>"b11476 · CUDA 13.4 · node default", "b11400 · not verified".</summary>
    public static string VersionLabel(RuntimeVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var parts = new List<string> { version.Version };
        if (!string.IsNullOrWhiteSpace(version.Cuda))
        {
            parts.Add("CUDA " + version.Cuda);
        }

        if (version.Default)
        {
            parts.Add("node default");
        }

        if (!version.Verified)
        {
            parts.Add("not verified");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>"Node default (b11476)", or "Node default (none verified)".</summary>
    public static string DefaultVersionLabel(IReadOnlyList<RuntimeVersion>? versions) =>
        (versions ?? []).FirstOrDefault(v => v.Default && v.Verified) is { } current
            ? $"Node default ({current.Version})"
            : "Node default (none verified)";

    /// <summary>
    /// The ExtraArgs of a text with one flag (and its value) per line: each line is split on whitespace into argv elements
    /// (no shell: llama-server gets each element as is, so "-sm row" must be two elements, and a refused flag cannot hide
    /// inside a line). Double quotes hold one element together, so an element that contains a space survives
    /// (<c>--override-kv "k=str:a b"</c> is two elements, not three) and a literal quote inside is doubled: with
    /// <see cref="FormatExtraArgs"/> this is a round trip for every stored element, so an unrelated save no longer
    /// splits one in two (MN-239 item 4, D131).
    /// </summary>
    public static IReadOnlyList<string> ParseExtraArgs(string? text)
    {
        string input = text ?? "";
        var args = new List<string>();
        var element = new StringBuilder();
        bool quoted = false;
        bool started = false;
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c == '"')
            {
                if (quoted && i + 1 < input.Length && input[i + 1] == '"')
                {
                    element.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }

                started = true;
            }
            else if (!quoted && char.IsWhiteSpace(c))
            {
                if (started)
                {
                    args.Add(element.ToString());
                    element.Clear();
                    started = false;
                }
            }
            else
            {
                element.Append(c);
                started = true;
            }
        }

        if (started)
        {
            args.Add(element.ToString());
        }

        return args;
    }

    /// <summary>
    /// The text of <paramref name="args"/>: a line per flag, its values on the same line, each element quoted when a plain
    /// split would not give it back (it holds whitespace, a quote, or is empty).
    /// </summary>
    public static string FormatExtraArgs(IReadOnlyList<string>? args)
    {
        var lines = new List<string>();
        foreach (string arg in args ?? [])
        {
            if (lines.Count == 0 || IsFlagToken(arg))
            {
                lines.Add(Quoted(arg));
            }
            else
            {
                lines[^1] += " " + Quoted(arg);
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>The ExtraArgs a save refuses (<see cref="LlamaCppFlags.RefusedExtraArgs"/>: owned, path-valued or typed).</summary>
    public static IReadOnlyList<string> RefusedExtraArgs(string? text) => LlamaCppFlags.RefusedExtraArgs(ParseExtraArgs(text));

    /// <summary>
    /// The refused-flag hint under Extra args (D118, D126), grouped: each typed setting with the field that sets it, then
    /// the flags mirbadllm sets itself (bind, key, served name, model file, <c>--fit</c>, log, device, the web UI and
    /// <c>--cache-ram</c> switches), then "any flag that takes a path or a download source".
    /// </summary>
    public static IReadOnlyList<(string Flags, string Reason)> RefusedHint { get; } =
    [
        ("-c, --ctx-size", "Context per request × Parallel slots"),
        ("-np, --parallel", "Parallel slots"),
        ("-ngl, --gpu-layers, --n-gpu-layers", "GPU layers"),
        ("-b, --batch-size · -ub, --ubatch-size", "Batch / Micro-batch"),
        ("-ctk, --cache-type-k · -ctv, --cache-type-v", "K / V cache type"),
        ("-fa, --flash-attn", "Flash attention"),
        ("--embedding, --embeddings, --pooling", "Embedding mode and Pooling"),
        ("-ts, --tensor-split, -mg, --main-gpu", "Per-GPU budgets (the tensor split) and the GPUs under Placement"),
        ("--host, --port, --api-key, --alias, -m, --model, --fit, --log-file, --device", "set by MirbadLLM for every model"),
        ("-md, --model-draft, --spec-draft-model", "Draft model"),
        ("--ui, --no-ui, --webui, --no-webui, --cache-ram, -cram", "fixed off by MirbadLLM (no web UI, no host-RAM prompt cache)"),
        ("-hf, --hf-token, --lora, --mmproj, --chat-template-file, …", "any flag that takes a path, a URL or a download source"),
    ];

    /// <summary>
    /// Why typed per-GPU budgets would be refused at save (<see cref="PlacementProblemCodes.GpuBudgetsMismatch"/>): some but
    /// not all typed, one not positive, or a sum other than the memory budget (± 0.01 GiB). Null when none is typed (the split
    /// follows capacity) or they are fine.
    /// </summary>
    public static string? GpuBudgetProblem(IReadOnlyList<double?> budgets, int gpuCount, double totalBudgetGiB)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        if (gpuCount < 2 || !budgets.Any(b => b is not null))
        {
            return null;
        }

        if (budgets.Count != gpuCount || budgets.Any(b => b is null))
        {
            return "Type a budget for every ticked GPU, or clear them all.";
        }

        if (budgets.Any(b => b <= 0))
        {
            return "Every per-GPU budget must be more than 0 GiB.";
        }

        double sum = budgets.Sum(b => b!.Value);
        return Math.Abs(sum - totalBudgetGiB) > 0.01
            ? $"They add up to {UiFormat.GiB(sum)}, not the {UiFormat.GiB(totalBudgetGiB)} memory budget."
            : null;
    }

    /// <summary>
    /// Lower bound of a GGUF's budget from its size alone (before its header can be read): weights × 1.10 + one CUDA
    /// context; no CUDA context on a CPU-only node (<paramref name="cpuOnly"/>).
    /// </summary>
    public static double MinimumNeedGiB(long bytes, bool cpuOnly = false) =>
        Math.Round((LlamaCppMemoryEstimate.Overhead * bytes / LlamaCppMemoryEstimate.GiB) + (cpuOnly ? 0 : LlamaCppMemoryEstimate.CudaContextGiBPerGpu), 1);

    /// <summary>
    /// The estimate of <paramref name="settings"/> over <paramref name="summary"/> on <paramref name="gpuCount"/> GPUs, or
    /// why it cannot be computed (a missing header value, a setting out of range). <paramref name="cpuOnly"/>: the model's
    /// node runs it on the CPU, so every weight counts in the budget (<see cref="LlamaCppMemoryEstimate.Estimate"/>).
    /// </summary>
    public static GgufMemoryEstimate? TryEstimate(GgufSummary? summary, LlamaCppSettings settings, int gpuCount, out string? error, bool cpuOnly = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        error = null;
        if (summary is null)
        {
            return null;
        }

        try
        {
            return LlamaCppMemoryEstimate.Estimate(summary, settings, Math.Max(1, gpuCount), cpuOnly);
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>"fits", "does not fit", "fit unknown" for <paramref name="need"/> against <paramref name="room"/>.</summary>
    public static bool? Fits(double need, GgufGpuRoom room)
    {
        ArgumentNullException.ThrowIfNull(room);
        return room.FreeGiB is { } free ? need <= free : null;
    }

    /// <summary>
    /// What the GGUF picker's verdict measures (MN-239 item 2, D130): the file's own need — the estimate from its header,
    /// or the weights alone before a download (<paramref name="atLeast"/>) — never the model's memory budget. The editor
    /// also shows the placement's verdict, which measures the budget (<see cref="PlacementEditing.FitLine"/>), and the
    /// two can differ (a 5 GiB budget per GPU against a 9.3 GiB estimate), so neither line is shown unlabelled.
    /// </summary>
    public static string NeedText(double need, bool atLeast = false) =>
        atLeast ? "the GGUF weights need at least " + UiFormat.GiB(need) : "the GGUF estimate needs " + UiFormat.GiB(need);

    /// <summary>
    /// "GPU 0: the GGUF estimate needs 8.9 GiB, 10.9 GiB free after the 2.5 GiB reserve" (§A.7), "… no fresh reading"
    /// (the need is named by <see cref="NeedText"/>).
    /// </summary>
    public static string FitText(double need, GgufGpuRoom room, bool atLeast = false)
    {
        ArgumentNullException.ThrowIfNull(room);
        string needs = NeedText(need, atLeast);
        return room.FreeGiB is { } free
            ? $"{room.Label}: {needs}, {UiFormat.GiB(Math.Max(0, free))} free after the {UiFormat.GiB(room.ReserveGiB)} reserve"
            : $"{room.Label}: {needs}, free memory {UiFormat.Missing} (no fresh reading)";
    }

    /// <summary>
    /// The candidates in the agent's "several model GGUFs; choose one with ?file=: a.gguf, b.gguf." refusal (MN-209's
    /// wording); empty when the message lists none.
    /// </summary>
    public static IReadOnlyList<string> CandidatesFrom(string? message)
    {
        const string Marker = "?file=:";
        int at = message?.IndexOf(Marker, StringComparison.Ordinal) ?? -1;
        if (at < 0)
        {
            return [];
        }

        string list = message![(at + Marker.Length)..].Trim().TrimEnd('.');
        return [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// The llama.cpp settings that differ between <paramref name="before"/> and <paramref name="after"/> and need a restart
    /// (every one is a <c>llama-server</c> argument or the executable, design-p3p4 §A.1.6), in the Apply dialog's words.
    /// The router's <c>dimensions</c> emulation needs none. Empty unless one of them is a llama.cpp model, so a vLLM edit
    /// lists exactly what <see cref="SettingChanges"/> lists.
    /// </summary>
    public static IReadOnlyList<SettingChange> RestartNeeded(ModelDefinition before, ModelDefinition after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Runtime != RuntimeKind.LlamaCpp && after.Runtime != RuntimeKind.LlamaCpp)
        {
            return [];
        }

        var changes = new List<SettingChange>();
        void Add(string label, string old, string @new)
        {
            if (!string.Equals(old, @new, StringComparison.Ordinal))
            {
                changes.Add(new SettingChange(label, old, @new));
            }
        }

        Add("Runtime", RuntimeName(before.Runtime), RuntimeName(after.Runtime));
        var a = before.LlamaCpp ?? new LlamaCppSettings { ModelFile = "" };
        var b = after.LlamaCpp ?? new LlamaCppSettings { ModelFile = "" };
        Add("GGUF file", Or(a.ModelFile, "none"), Or(b.ModelFile, "none"));
        Add("Projector", a.ProjectorFile ?? "none", b.ProjectorFile ?? "none");
        Add("Draft model", a.DraftModelFile ?? "none", b.DraftModelFile ?? "none");
        Add("Context per request", Tokens(a.ContextSize), Tokens(b.ContextSize));
        Add("Parallel slots", a.Parallel.ToString(Culture), b.Parallel.ToString(Culture));
        Add("GPU layers", Layers(a.GpuLayers), Layers(b.GpuLayers));
        Add("Flash attention", Auto(a.FlashAttention), Auto(b.FlashAttention));
        Add("K cache type", a.CacheTypeK ?? "default", b.CacheTypeK ?? "default");
        Add("V cache type", a.CacheTypeV ?? "default", b.CacheTypeV ?? "default");
        Add("Batch", Number(a.BatchSize), Number(b.BatchSize));
        Add("Micro-batch", Number(a.MicroBatchSize), Number(b.MicroBatchSize));
        Add("Embedding mode", a.Embedding ? "on" : "off", b.Embedding ? "on" : "off");
        Add("Pooling", a.Pooling ?? "from the GGUF", b.Pooling ?? "from the GGUF");
        Add("Chat template", a.ChatTemplate ?? "from the GGUF", b.ChatTemplate ?? "from the GGUF");
        Add("Reasoning", Auto(a.Reasoning), Auto(b.Reasoning));
        Add("Threads", a.Threads?.ToString(Culture) ?? "default", b.Threads?.ToString(Culture) ?? "default");
        Add("Per-GPU budgets", Budgets(a.GpuBudgetsGiB), Budgets(b.GpuBudgetsGiB));
        Add("llama.cpp version", a.ExecutableVersion ?? "node default", b.ExecutableVersion ?? "node default");
        Add("llama.cpp extra args", Args(a.ExtraArgs), Args(b.ExtraArgs));
        return changes;
    }

    /// <summary>
    /// The tags suggested for a llama.cpp model (<see cref="ModelTags.Suggest"/> reads vLLM arguments and parsers, which a
    /// llama.cpp model has none of): an embedding model is <c>embedding</c> (<c>reranker</c> with rank pooling), else
    /// <c>chat</c>, plus <c>vision</c> with a projector.
    /// </summary>
    public static IReadOnlyList<string> SuggestTags(LlamaCppForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.Embedding)
        {
            return [string.Equals(form.Pooling, LlamaCppPooling.Rank, StringComparison.Ordinal) ? ModelTags.Reranker : ModelTags.Embedding];
        }

        return string.IsNullOrWhiteSpace(form.ProjectorFile) ? [ModelTags.Chat] : [ModelTags.Chat, ModelTags.Vision];
    }

    /// <summary>"vLLM", "llama.cpp", "MLX".</summary>
    public static string RuntimeName(RuntimeKind kind) => kind switch
    {
        RuntimeKind.LlamaCpp => "llama.cpp",
        RuntimeKind.Mlx => "MLX",
        RuntimeKind.Sglang => "SGLang",
        RuntimeKind.Nim => "NIM",
        _ => "vLLM",
    };

    private static bool IsFlagToken(string arg) =>
        arg.Length > 1 && arg[0] == '-' && !double.TryParse(arg, NumberStyles.Float, Culture, out _);

    /// <summary>One argv element as <see cref="ParseExtraArgs"/> reads it back: as typed, or in double quotes.</summary>
    private static string Quoted(string arg) =>
        arg.Length > 0 && !arg.Any(char.IsWhiteSpace) && !arg.Contains('"', StringComparison.Ordinal)
            ? arg
            : "\"" + arg.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Or(string? value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;

    private static string Tokens(int value) => value.ToString("N0", Culture) + " tokens";

    private static string Layers(int value) => value >= 999 ? "all" : value.ToString(Culture);

    private static string Auto(bool? value) => value switch { true => "on", false => "off", null => "auto" };

    private static string Number(int? value) => value?.ToString(Culture) ?? "default";

    private static string Budgets(IReadOnlyList<double>? budgets) =>
        budgets is { Count: > 0 } ? string.Join(" + ", budgets.Select(b => UiFormat.GiB(b))) : "split by capacity";

    private static string Args(IReadOnlyList<string>? args) => args is { Count: > 0 } ? string.Join(' ', args) : "none";
}

/// <summary>The info-button texts of the editor's llama.cpp fields (design-p3p4 §A.7, MN-226).</summary>
public static class LlamaCppFieldHelp
{
    public static readonly FieldHelpText Runtime = new(
        What: "The engine that serves the model, from the runtimes this node reports.",
        Why: "The settings below belong to the runtime. llama.cpp serves one GGUF file and runs as a native process on Windows " +
             "nodes; the option is shown only for a node whose capabilities list a llama.cpp runtime. The runtime is chosen " +
             "when the model is added.",
        Example: "vLLM on the DGX; llama.cpp for Qwen3-Embedding-4B Q8_0 on the office PC's RTX 5060 Ti.");

    public static readonly FieldHelpText GgufFile = new(
        What: "The GGUF file llama-server loads (the first part of a split model; llama.cpp finds the other parts itself).",
        Why: "Its header gives the layers, heads and context the memory estimate needs, the pooling of an embedding model and " +
             "whether it carries a chat template. Reading it fills the settings below.",
        Example: "Qwen3-Embedding-4B-Q8_0.gguf in the folder qwen3-embedding-4b-gguf.");

    public static readonly FieldHelpText Projector = new(
        What: "A vision projector (mmproj) for a multimodal GGUF, passed as --mmproj.",
        Why: "Without it a vision model answers text only. Leave it at None for text and embedding models.",
        Example: "mmproj-F16.gguf next to the model's GGUF.");

    public static readonly FieldHelpText ContextSize = new(
        What: "Tokens one request may use (per slot). llama-server gets -c = this × Parallel slots.",
        Why: "The KV cache grows with context × slots, so it drives the KV term of the estimate. An input longer than this " +
             "gets a 400 from llama-server.",
        Example: "8 192 for Qwen3-Embedding-4B (2 slots → -c 16384).");

    public static readonly FieldHelpText Parallel = new(
        What: "How many requests llama-server serves at once (-np), each with its own context.",
        Why: "Always explicit: -np auto would make memory unpredictable. Each slot adds a full context of KV cache.",
        Example: "2 for an embedding model called by one service; 4 for a small chat model.");

    public static readonly FieldHelpText GpuLayers = new(
        What: "Layers offloaded to the GPUs (-ngl); 999 = all of them.",
        Why: "Fewer layers keep the rest on the CPU in host RAM (partial offload): slower, but a model larger than the card can run.",
        Example: "999 for a model that fits the card.");

    public static readonly FieldHelpText FlashAttention = new(
        What: "llama-server's -fa: on, off or auto (llama.cpp decides per GPU).",
        Why: "Flash attention saves memory and time on recent GPUs; a quantized V cache needs it.",
        Example: "Auto on an RTX 5060 Ti.");

    public static readonly FieldHelpText CacheType = new(
        What: "The K and V cache element types (-ctk / -ctv). Default f16.",
        Why: "q8_0 halves the KV cache with little quality loss; the estimate's KV term follows the type.",
        Example: "f16 for an embedding model (small cache); q8_0 for a long-context chat model.");

    public static readonly FieldHelpText Batch = new(
        What: "Logical batch (-b) and physical micro-batch (-ub) in tokens.",
        Why: "For an embedding model llama-server uses -b = -ub, and the compute buffer grows linearly with -ub: empty uses " +
             "min(context, 2048) for a causal model; a non-causal (BERT-style) GGUF needs its whole input in one micro-batch, " +
             "so the editor fills in the context. A chat model uses llama.cpp's defaults (2048 / 512) when empty.",
        Example: "Empty for Qwen3-Embedding-4B (causal): -ub 2048, about 1.4 GiB of compute buffer instead of 5.7 at 8192.");

    public static readonly FieldHelpText Embedding = new(
        What: "Serve /v1/embeddings (--embedding) with the chosen pooling instead of chat completions.",
        Why: "Pre-filled from the GGUF's pooling_type. The router then also answers OpenAI's dimensions field itself when the " +
             "emulation is on (truncate and renormalise), which llama-server ignores.",
        Example: "On with pooling last for Qwen3-Embedding-4B.");

    public static readonly FieldHelpText Pooling = new(
        What: "How token vectors become one embedding (--pooling): none, mean, cls, last or rank.",
        Why: "It must match how the model was trained; empty uses the GGUF's own pooling_type.",
        Example: "last for Qwen3-Embedding.");

    public static readonly FieldHelpText ChatTemplate = new(
        What: "The chat template llama-server applies: the GGUF's own, a built-in one (--chat-template) or a .jinja file in the model's folder.",
        Why: "A GGUF without a template, or with a broken one, answers chat requests badly. Changing it needs a restart.",
        Example: "Built-in chatml for an Ollama GGUF; the GGUF's own for most Hugging Face GGUFs.");

    public static readonly FieldHelpText Reasoning = new(
        What: "llama-server's --reasoning: on, off or auto.",
        Why: "Auto lets llama.cpp detect a thinking model from its template; off saves tokens when the reasoning is not wanted.",
        Example: "Auto.");

    public static readonly FieldHelpText Threads = new(
        What: "CPU threads (-t). Empty = llama.cpp's choice.",
        Why: "Matters only for layers left on the CPU; the desktop keeps the remaining cores.",
        Example: "Empty with every layer on the GPU.");

    public static readonly FieldHelpText DraftModel = new(
        What: "A draft GGUF for speculative decoding, passed as -md. The path starts at the models folder, because a drafter is its own download.",
        Why: "The drafter proposes several tokens and the model verifies them in one pass, which can raise single-stream speed. " +
             "llama.cpp reads the method from the draft file; whether the build and the drafter fit this model only shows at a start.",
        Example: "Qwen3.8-27B-DFlash2-GGUF/Qwen3.8-27B-DFlash2-Q8_0.gguf, with --spec-draft-n-max 7 under Extra args.");

    public static readonly FieldHelpText Version = new(
        What: "The llama-server build the model runs with: the node's default, or a version pinned on this model.",
        Why: "A pin keeps the model on a build you tested when the node's default changes. Only verified builds can start; " +
             "changing it needs a restart.",
        Example: "Node default (b11476).");

    public static readonly FieldHelpText ExtraArgs = new(
        What: "Other llama-server arguments, one flag and its value per line, appended last. A value that holds a space " +
              "goes in double quotes.",
        Why: "Every setting that has a field here, and every flag MirbadLLM sets itself or that takes a path, is refused: " +
             "llama-server takes the last value of a repeated flag, so an extra one would override the field the budget was " +
             "computed from.",
        Example: "-sm row (split mode row has no field of its own).");

    public static readonly FieldHelpText GpuBudgets = new(
        What: "The memory budget of each ticked GPU, in GiB. They must add up to the memory budget.",
        Why: "llama.cpp splits the layers by --tensor-split = each budget ÷ their sum, so unequal cards can work together, and " +
             "admission checks each GPU against its own budget. Empty: the split follows each card's memory minus its reserve.",
        Example: "6 + 9 GiB on an RTX 5070 12 GB and an RTX 5060 Ti 16 GB for a 15 GiB model.");

    public static readonly FieldHelpText EmulateDimensions = new(
        What: "The router answers OpenAI's dimensions field for this embedding model (truncate, then L2-renormalise).",
        Why: "llama-server ignores dimensions. A request without the field is passed through byte for byte. It takes effect " +
             "without a restart.",
        Example: "On for Qwen3-Embedding-4B (Matryoshka: 1024 of its 2560 values).");
}
