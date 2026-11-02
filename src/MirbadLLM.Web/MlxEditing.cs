using System.Globalization;
using System.Text.Json;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Multinode;

namespace MirbadLLM.Web;

/// <summary>
/// The MLX settings as the model editor edits them (docs/macos/implementation-plan.md, Phase 3 "UI"): plain strings and
/// nullable numbers the form binds to, read from and written back into <see cref="MlxSettings"/>. The checkpoint folder is
/// the model's own folder (<see cref="ModelDefinition.ModelPath"/>), so the form does not hold it.
/// </summary>
public sealed class MlxForm
{
    /// <summary>Tokens per request the memory budget covers.</summary>
    public int ContextSize { get; set; } = 8_192;

    /// <summary>Requests decoded together.</summary>
    public int Parallel { get; set; } = 4;

    /// <summary>The prompt cache in GiB.</summary>
    public double PromptCacheGiB { get; set; } = 0.5;

    /// <summary>Answer length when a request sets none; null = mlx-lm's default.</summary>
    public int? MaxTokens { get; set; }

    /// <summary>"" = full precision, "8", "4".</summary>
    public string KvBits { get; set; } = "";

    /// <summary>The chat template arguments (a JSON object); empty = none.</summary>
    public string ChatTemplateArgs { get; set; } = "";

    /// <summary>Lets the tokenizer run Python code from the checkpoint folder.</summary>
    public bool TrustRemoteCode { get; set; }

    /// <summary>Draft model folder relative to the models root; empty = none.</summary>
    public string DraftModelFolder { get; set; } = "";

    /// <summary>Draft tokens; null = mlx-lm's default.</summary>
    public int? NumDraftTokens { get; set; }

    /// <summary>LoRA adapter folder relative to the models root; empty = none.</summary>
    public string AdapterFolder { get; set; } = "";

    /// <summary>Pinned mlx-lm version; "" = the node's default.</summary>
    public string ExecutableVersion { get; set; } = "";

    /// <summary>Extra arguments, one flag (with its value) per line.</summary>
    public string ExtraArgsText { get; set; } = "";

    /// <summary>The stored settings, null for a new model.</summary>
    public MlxSettings? Baseline { get; private set; }

    /// <summary>The stored settings in the form.</summary>
    public static MlxForm From(MlxSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new MlxForm
        {
            Baseline = settings,
            ContextSize = settings.ContextSize,
            Parallel = settings.Parallel,
            PromptCacheGiB = settings.PromptCacheGiB,
            MaxTokens = settings.MaxTokens,
            KvBits = settings.KvBits?.ToString(CultureInfo.InvariantCulture) ?? "",
            ChatTemplateArgs = settings.ChatTemplateArgs ?? "",
            TrustRemoteCode = settings.TrustRemoteCode,
            DraftModelFolder = settings.DraftModelFolder ?? "",
            NumDraftTokens = settings.NumDraftTokens,
            AdapterFolder = settings.AdapterFolder ?? "",
            ExecutableVersion = settings.ExecutableVersion ?? "",
            ExtraArgsText = LlamaCppEditing.FormatExtraArgs(settings.ExtraArgs),
        };
    }

    /// <summary>The settings this form describes for the checkpoint in <paramref name="folder"/>.</summary>
    public MlxSettings ToSettings(string folder) =>
        (Baseline ?? new MlxSettings { ModelFolder = "" }) with
        {
            ModelFolder = (folder ?? "").Trim().Trim('/'),
            ContextSize = ContextSize,
            Parallel = Parallel,
            PromptCacheGiB = PromptCacheGiB,
            MaxTokens = MaxTokens,
            KvBits = int.TryParse(KvBits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bits) ? bits : null,
            ChatTemplateArgs = Blank(ChatTemplateArgs),
            TrustRemoteCode = TrustRemoteCode,
            DraftModelFolder = Folder(DraftModelFolder),
            NumDraftTokens = NumDraftTokens,
            AdapterFolder = Folder(AdapterFolder),
            ExecutableVersion = Blank(ExecutableVersion),
            ExtraArgs = LlamaCppEditing.ParseExtraArgs(ExtraArgsText),
        };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Folder(string? value) => Blank(value)?.Trim('/');
}

/// <summary>
/// The model editor's MLX rules and words (docs/macos/implementation-plan.md, Phase 3 "UI"), kept out of the components so
/// they are testable on their own: whether a node runs MLX, the ExtraArgs refusals, the chat template check, the estimate,
/// the tags and the settings a restart applies.
/// </summary>
public static class MlxEditing
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>The node's MLX runtime (always a native process); null when the node runs no MLX.</summary>
    public static RuntimeCapability? Runtime(NodeCapabilities? capabilities) =>
        (capabilities?.Runtimes ?? []).FirstOrDefault(r => r.Kind == RuntimeKind.Mlx && r.Delivery == LaunchDelivery.Process);

    /// <summary>The MLX runtime of an editor node (<see cref="Runtime(NodeCapabilities?)"/>).</summary>
    public static RuntimeCapability? Runtime(PlacementNode? node) =>
        node is null ? null : Runtime(node.Record.LastCapabilities ?? node.Live?.Capabilities);

    /// <summary>The ExtraArgs a save refuses (<see cref="MlxFlags.RefusedExtraArgs"/>: every flag not allowed, and the path tokens).</summary>
    public static IReadOnlyList<string> RefusedExtraArgs(string? text) => MlxFlags.RefusedExtraArgs(LlamaCppEditing.ParseExtraArgs(text));

    /// <summary>Why the chat template arguments would be refused (not a JSON object); null when empty or fine.</summary>
    public static string? ChatTemplateArgsProblem(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? null
                : "Must be a JSON object, e.g. {\"enable_thinking\": false}.";
        }
        catch (JsonException)
        {
            return "Not valid JSON: write an object, e.g. {\"enable_thinking\": false}.";
        }
    }

    /// <summary>The estimate of <paramref name="settings"/> over the checkpoint folder's inspection; null without one.</summary>
    public static MlxMemoryEstimate? TryEstimate(MlxSettings settings, ModelFolderInspection? folder)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return folder is null || settings.ContextSize < 1 || settings.Parallel < 1 || !double.IsFinite(settings.PromptCacheGiB) || settings.PromptCacheGiB < 0
            ? null
            : MlxArgsBuilder.Estimate(settings, folder);
    }

    /// <summary>"1.10 × (0.3 weights + 0.9 KV) + 0.5 prompt cache + 0.5 overhead = 2.3 GiB".</summary>
    public static string Formula(MlxMemoryEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        return $"{MlxArgsBuilder.Margin.ToString("0.00", Culture)} × ({Decimal(estimate.WeightsGiB)} weights + {Decimal(estimate.KvGiB)} KV) + " +
            $"{Decimal(estimate.PromptCacheGiB)} prompt cache + {Decimal(estimate.OverheadGiB)} overhead = {UiFormat.GiB(estimate.BudgetGiB)}";
    }

    /// <summary>The tags suggested for an MLX model: <c>chat</c>, plus <c>vision</c> for a checkpoint with a vision config.</summary>
    public static IReadOnlyList<string> SuggestTags(ModelFolderInspection? folder) =>
        folder is { HasVisionConfig: true } ? [ModelTags.Chat, ModelTags.Vision] : [ModelTags.Chat];

    /// <summary>
    /// The MLX settings that differ between <paramref name="before"/> and <paramref name="after"/> and need a restart (every
    /// one is an <c>mlx_lm.server</c> argument or the executable), in the Apply dialog's words. The context size is not an
    /// argument (mlx_lm.server does not enforce it; only the budget estimate uses it), so it needs none. Empty unless one
    /// of them is an MLX model.
    /// </summary>
    public static IReadOnlyList<SettingChange> RestartNeeded(ModelDefinition before, ModelDefinition after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Runtime != RuntimeKind.Mlx && after.Runtime != RuntimeKind.Mlx)
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

        if (before.Runtime != RuntimeKind.LlamaCpp && after.Runtime != RuntimeKind.LlamaCpp)
        {
            // LlamaCppEditing.RestartNeeded names a change from or to llama.cpp itself.
            Add("Runtime", LlamaCppEditing.RuntimeName(before.Runtime), LlamaCppEditing.RuntimeName(after.Runtime));
        }

        var a = before.Mlx ?? new MlxSettings { ModelFolder = "" };
        var b = after.Mlx ?? new MlxSettings { ModelFolder = "" };
        Add("Checkpoint folder", Or(a.ModelFolder, "none"), Or(b.ModelFolder, "none"));
        Add("Parallel requests", a.Parallel.ToString(Culture), b.Parallel.ToString(Culture));
        Add("Prompt cache", UiFormat.GiB(a.PromptCacheGiB), UiFormat.GiB(b.PromptCacheGiB));
        Add("Default answer length", Tokens(a.MaxTokens), Tokens(b.MaxTokens));
        Add("KV cache bits", Bits(a.KvBits), Bits(b.KvBits));
        Add("Chat template arguments", Json(a.ChatTemplateArgs), Json(b.ChatTemplateArgs));
        Add("Trust remote code", a.TrustRemoteCode ? "on" : "off", b.TrustRemoteCode ? "on" : "off");
        Add("Draft model", a.DraftModelFolder ?? "none", b.DraftModelFolder ?? "none");
        Add("Draft tokens", a.NumDraftTokens?.ToString(Culture) ?? "default", b.NumDraftTokens?.ToString(Culture) ?? "default");
        Add("Adapter", a.AdapterFolder ?? "none", b.AdapterFolder ?? "none");
        Add("mlx-lm version", a.ExecutableVersion ?? "node default", b.ExecutableVersion ?? "node default");
        Add("MLX extra args", Args(a.ExtraArgs), Args(b.ExtraArgs));
        return changes;
    }

    private static string Decimal(double value) => value.ToString("0.0", Culture);

    private static string Or(string? value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;

    private static string Tokens(int? value) => value is { } v ? v.ToString("N0", Culture) + " tokens" : "default";

    private static string Bits(int? value) => value is { } v ? v.ToString(Culture) + "-bit" : "full precision";

    /// <summary>The arguments compactly (as the builder emits them), so a whitespace edit is not a change.</summary>
    private static string Json(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException)
        {
            return value.Trim();
        }
    }

    private static string Args(IReadOnlyList<string>? args) => args is { Count: > 0 } ? string.Join(' ', args) : "none";
}

/// <summary>The info-button texts of the editor's MLX fields (docs/macos/implementation-plan.md, Phase 3 "UI").</summary>
public static class MlxFieldHelp
{
    public static readonly FieldHelpText Runtime = new(
        What: "The inference engine that serves the model: vLLM, llama.cpp (llama-server) or MLX (mlx_lm.server) on a node that reports it.",
        Why: "The settings below belong to the runtime. MLX serves an MLX checkpoint folder as a native process on an Apple " +
             "Silicon node; the option is shown only for a node whose capabilities list an MLX runtime. The runtime is chosen " +
             "when the model is added.",
        Example: "MLX for mlx-community/Qwen3-0.6B-4bit on the Mac mini.");

    public static readonly FieldHelpText ContextSize = new(
        What: "Tokens per request (prompt plus answer) the memory budget covers.",
        Why: "The KV cache grows with context × parallel requests, so it drives the KV term of the estimate. mlx_lm.server has " +
             "no context limit of its own: a longer request is still served, beyond what admission counted.",
        Example: "8 192 for a small chat model on an 8 GiB Mac.");

    public static readonly FieldHelpText Parallel = new(
        What: "Requests decoded together (--decode-concurrency; the prompt concurrency is the same, at most 8).",
        Why: "Each request holds its own KV cache. mlx-lm's own default of 32 needs more memory than a small Mac has.",
        Example: "4.");

    public static readonly FieldHelpText PromptCache = new(
        What: "Memory for KV caches kept between requests (--prompt-cache-bytes), in GiB.",
        Why: "A request that shares a prefix with an earlier one (a system prompt, a chat history) skips re-reading it. Always " +
             "bounded, so admission counts it.",
        Example: "0.5 GiB.");

    public static readonly FieldHelpText MaxTokens = new(
        What: "The answer length when a request sets no max_tokens (--max-tokens).",
        Why: "Empty keeps mlx-lm's default of 512 tokens; a thinking model may need more.",
        Example: "2048 for a reasoning model.");

    public static readonly FieldHelpText KvBits = new(
        What: "Quantize the KV cache to 8 or 4 bits (--kv-bits). Full precision by default.",
        Why: "A quantized cache takes half or a quarter of the memory, but mlx-lm then serves one request at a time.",
        Example: "Full precision for a chat model with several users; 8 for one long-context user.");

    public static readonly FieldHelpText ChatTemplateArgs = new(
        What: "A JSON object passed to the tokenizer's chat template (--chat-template-args).",
        Why: "Templates read switches from it, such as whether a Qwen3 model thinks before answering. Empty passes none.",
        Example: "{\"enable_thinking\": false}");

    public static readonly FieldHelpText TrustRemoteCode = new(
        What: "Let the tokenizer run Python code shipped in the checkpoint folder (--trust-remote-code).",
        Why: "Some checkpoints need it, but the code runs as the agent's account on the node. Leave it off unless the " +
             "checkpoint needs it and you trust where it came from.",
        Example: "Off for every mlx-community checkpoint.");

    public static readonly FieldHelpText DraftModel = new(
        What: "A small MLX model folder for speculative decoding (--draft-model), and how many tokens it drafts (--num-draft-tokens).",
        Why: "The draft proposes tokens the main model checks in one step: faster answers for the memory of a second model.",
        Example: "mlx-community/Qwen3-0.6B-4bit drafting for Qwen3-8B, 3 tokens.");

    public static readonly FieldHelpText Adapter = new(
        What: "A folder of LoRA adapter weights applied on top of the checkpoint (--adapter-path).",
        Why: "Serves a fine-tune without merging it into the weights. Empty = none.",
        Example: "adapters/qwen3-support-lora.");

    public static readonly FieldHelpText Version = new(
        What: "The mlx-lm release the model runs with: the node's default, or a version pinned on this model.",
        Why: "A pin keeps the model on a release you tested when the node's default changes. Only verified installs can " +
             "start; changing it needs a restart.",
        Example: "Node default (0.32.2).");

    public static readonly FieldHelpText ExtraArgs = new(
        What: "Other mlx_lm.server arguments, one flag and its value per line, appended last. A value that holds a space goes in double quotes.",
        Why: "Only the flags listed under the field are allowed: argparse accepts abbreviations and keeps the last value of a " +
             "repeated flag, so anything else could override a setting the budget was computed from.",
        Example: "--temp 0.7");
}
