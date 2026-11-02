using System.Globalization;
using System.Text.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>
/// Builds the <c>mlx_lm.server</c> argv of an MLX <see cref="ModelDefinition"/> (docs/macos/implementation-plan.md,
/// Phase 3): the only place on the master that knows the server's flags. The order is fixed, so the argv is canonical and
/// the execution fingerprint is stable: <c>--model</c>, <c>--adapter-path</c>, <c>--draft-model</c>,
/// <c>--num-draft-tokens</c>, <c>--host 127.0.0.1 --port {port}</c>, <c>--decode-concurrency</c>,
/// <c>--prompt-concurrency</c>, <c>--prompt-cache-bytes</c>, <c>--kv-bits</c>, <c>--max-tokens</c>,
/// <c>--chat-template-args</c>, <c>--trust-remote-code</c>, then the ExtraArgs. Folders are <c>{models}/…</c> tokens the
/// agent substitutes and confines. The agent puts the interpreter arguments
/// (<see cref="ExecutableNames.MlxInterpreterArgs"/>) in front; they are not part of the spec. Pure: no I/O.
/// </summary>
public static class MlxArgsBuilder
{
    /// <summary>Largest <see cref="MlxSettings.Parallel"/> the editor and the builder accept.</summary>
    public const int MaxParallel = 64;

    /// <summary>Largest <see cref="MlxSettings.ContextSize"/> accepted (1 Mi tokens).</summary>
    public const int MaxContextSize = 1_048_576;

    /// <summary>
    /// Memory <c>mlx_lm.server</c> uses besides the weights, the KV cache and the prompt cache: the Python interpreter,
    /// mlx-lm, the tokenizer and Metal's buffers. Spike S4 measured ~620 MiB resident for a 335 MB 4-bit model, i.e.
    /// ~0.3 GiB above the weights; 0.5 GiB keeps a margin.
    /// </summary>
    public const double RuntimeOverheadGiB = 0.5;

    /// <summary>The headroom on weights and KV cache (as llama.cpp's estimate, design-p3p4 §A.1.10).</summary>
    public const double Margin = 1.10;

    /// <summary>
    /// The settings problems that make <paramref name="model"/> unlaunchable (placement problem
    /// <see cref="PlacementProblemCodes.MlxSettingsInvalid"/>): missing settings, folders that are not plain relative paths,
    /// sizes out of range, chat template arguments that are not a JSON object, and ExtraArgs that
    /// <see cref="MlxFlags.RefusedExtraArgs"/> refuses. Empty when the model can be built.
    /// </summary>
    public static IReadOnlyList<PlacementProblem> Problems(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var problems = new List<PlacementProblem>();
        void Add(string message) => problems.Add(new PlacementProblem(PlacementProblemCodes.MlxSettingsInvalid, message));

        if (model.Mlx is not { } s)
        {
            problems.Add(new PlacementProblem(PlacementProblemCodes.MlxSettingsMismatch, $"Model '{model.Id}' is an MLX model without MLX settings."));
            return problems;
        }

        foreach (var (name, folder) in new[] { ("model folder", (string?)s.ModelFolder), ("draft model folder", s.DraftModelFolder), ("adapter folder", s.AdapterFolder) })
        {
            if ((folder is not null || name == "model folder") && !LlamaCppArgsBuilder.IsRelativePath(folder))
            {
                Add($"The {name} '{folder}' must be a relative path inside the models folder ('/' separators, no '..').");
            }
        }

        if (s.ContextSize is < 1 or > MaxContextSize)
        {
            Add($"The context size must be 1 to {Int(MaxContextSize)} tokens per request (is {Int(s.ContextSize)}).");
        }

        if (s.Parallel is < 1 or > MaxParallel)
        {
            Add($"Parallel requests must be 1 to {Int(MaxParallel)} (is {Int(s.Parallel)}).");
        }

        if (!double.IsFinite(s.PromptCacheGiB) || s.PromptCacheGiB < 0 || s.PromptCacheGiB > 1024)
        {
            Add($"The prompt cache must be 0 to 1024 GiB (is {s.PromptCacheGiB.ToString(CultureInfo.InvariantCulture)}).");
        }

        if (s.MaxTokens is < 1)
        {
            Add($"The default answer length must be at least 1 token (is {s.MaxTokens}).");
        }

        if (s.KvBits is not null and not 4 and not 8)
        {
            Add($"The KV cache bits must be 4 or 8 (is {s.KvBits}).");
        }

        if (s.NumDraftTokens is < 1)
        {
            Add($"Draft tokens must be at least 1 (is {s.NumDraftTokens}).");
        }

        if (s.NumDraftTokens is not null && s.DraftModelFolder is null)
        {
            Add("Draft tokens are set without a draft model folder.");
        }

        if (s.ChatTemplateArgs is not null && !IsJsonObject(s.ChatTemplateArgs))
        {
            Add("The chat template arguments must be a JSON object, e.g. {\"enable_thinking\": false}.");
        }

        if (s.ExecutableVersion is not null && !IsVersion(s.ExecutableVersion))
        {
            Add($"The mlx-lm version '{s.ExecutableVersion}' is not a version number (e.g. 0.32.2).");
        }

        var refused = MlxFlags.RefusedExtraArgs(s.ExtraArgs ?? []);
        if (refused.Count > 0)
        {
            Add($"Extra arguments {string.Join(", ", refused.Select(r => r ?? "(null)"))} are not allowed: mirbadllm sets them itself, " +
                $"or they are not among the flags MLX models may set ({string.Join(", ", MlxFlags.AllowedInExtraArgs)}).");
        }

        return problems;
    }

    /// <summary>The <c>mlx_lm.server</c> argv of <paramref name="model"/> (still holding <c>{models}</c> and <c>{port}</c>).</summary>
    /// <exception cref="ArgumentException"><see cref="Problems"/> is not empty.</exception>
    public static IReadOnlyList<string> Build(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var problems = Problems(model);
        if (problems.Count > 0)
        {
            throw new ArgumentException($"Model '{model.Id}' cannot be launched: {string.Join(" ", problems.Select(p => p.Message))}");
        }

        var s = model.Mlx!;
        var args = new List<string> { MlxFlags.Model, LlamaCppArgsBuilder.Models(s.ModelFolder) };
        if (s.AdapterFolder is not null)
        {
            args.AddRange(["--adapter-path", LlamaCppArgsBuilder.Models(s.AdapterFolder)]);
        }

        if (s.DraftModelFolder is not null)
        {
            args.AddRange(["--draft-model", LlamaCppArgsBuilder.Models(s.DraftModelFolder)]);
            if (s.NumDraftTokens is { } draftTokens)
            {
                args.AddRange(["--num-draft-tokens", Int(draftTokens)]);
            }
        }

        args.AddRange(["--host", LlamaCppFlags.LoopbackHost, "--port", ProcessArgvTokens.Port]);
        args.AddRange(["--decode-concurrency", Int(s.Parallel)]);
        args.AddRange(["--prompt-concurrency", Int(Math.Min(s.Parallel, MlxSettings.MaxPromptConcurrency))]);
        args.AddRange(["--prompt-cache-bytes", Int(PromptCacheBytes(s))]);
        if (s.KvBits is { } kvBits)
        {
            args.AddRange(["--kv-bits", Int(kvBits)]);
        }

        if (s.MaxTokens is { } maxTokens)
        {
            args.AddRange(["--max-tokens", Int(maxTokens)]);
        }

        if (s.ChatTemplateArgs is not null)
        {
            // Canonical (compact) JSON, so whitespace edits do not mark the model "restart pending".
            args.AddRange(["--chat-template-args", JsonSerializer.Serialize(JsonDocument.Parse(s.ChatTemplateArgs).RootElement)]);
        }

        if (s.TrustRemoteCode)
        {
            args.Add("--trust-remote-code");
        }

        args.AddRange(s.ExtraArgs);
        return args;
    }

    /// <summary><see cref="MlxSettings.PromptCacheGiB"/> in bytes (rounded down to whole MiB).</summary>
    public static long PromptCacheBytes(MlxSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return (long)Math.Floor(settings.PromptCacheGiB * 1024) * 1024 * 1024;
    }

    /// <summary>
    /// The memory estimate of an MLX model from its checkpoint folder (the node's <c>/model-folders/{folder}</c>
    /// inspection): <c>1.10 × (weights + KV) + prompt cache + </c><see cref="RuntimeOverheadGiB"/>, where weights are the
    /// folder's safetensors and the KV cache holds <see cref="MlxSettings.ContextSize"/> × <see cref="MlxSettings.Parallel"/>
    /// tokens at 16 bits (or <see cref="MlxSettings.KvBits"/>; mlx-lm then serves one request at a time). The inspection's
    /// KV figure is per token at 8 bits. All values in GiB.
    /// </summary>
    public static MlxMemoryEstimate Estimate(MlxSettings settings, ModelFolderInspection folder)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(folder);
        double bitsPerElement = settings.KvBits ?? 16;
        int concurrent = settings.KvBits is null ? settings.Parallel : 1;
        double kvGiB = folder.KvBytesPerToken * (bitsPerElement / 8) * settings.ContextSize * concurrent / 1_073_741_824.0;
        double promptCache = PromptCacheBytes(settings) / 1_073_741_824.0;
        double budget = (Margin * (folder.WeightsGiB + kvGiB)) + promptCache + RuntimeOverheadGiB;
        return new MlxMemoryEstimate(folder.WeightsGiB, kvGiB, promptCache, RuntimeOverheadGiB, Math.Round(budget, 2));
    }

    private static bool IsJsonObject(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>A release number: digits and dots, optionally a pre-release suffix (<c>0.32.2</c>, <c>0.33.0rc1</c>).</summary>
    internal static bool IsVersion(string value) =>
        value.Length is > 0 and <= 32 && char.IsAsciiDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+');

    private static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The memory estimate of an MLX model (<see cref="MlxArgsBuilder.Estimate"/>). All values in GiB.</summary>
/// <param name="WeightsGiB">The checkpoint's safetensors.</param>
/// <param name="KvGiB">KV cache for the context × parallel requests.</param>
/// <param name="PromptCacheGiB">The prompt cache (<c>--prompt-cache-bytes</c>).</param>
/// <param name="OverheadGiB">Interpreter, libraries and Metal buffers.</param>
/// <param name="BudgetGiB">What the editor proposes as <see cref="ModelDefinition.MemoryBudgetGiB"/>.</param>
public sealed record MlxMemoryEstimate(double WeightsGiB, double KvGiB, double PromptCacheGiB, double OverheadGiB, double BudgetGiB);
