using System.Text.Json.Serialization;

namespace MirbadLLM.Core;

/// <summary>
/// MLX settings of a model (docs/macos/implementation-plan.md, Phase 3): <c>mlx_lm.server</c> on an Apple Silicon node,
/// always as a native process. Comments name the flag each value becomes; <c>MlxArgsBuilder</c> is the only place that
/// knows the server's flags. Members are omitted from JSON at their default, like <see cref="LlamaCppSettings"/>.
/// </summary>
public sealed record MlxSettings
{
    /// <summary>
    /// The MLX checkpoint folder relative to the models root (<c>config.json</c>, <c>*.safetensors</c> and the tokenizer,
    /// e.g. a <c>mlx-community/*</c> download): <c>--model {models}/{folder}</c>.
    /// </summary>
    public required string ModelFolder { get; init; }

    /// <summary>
    /// Tokens per request the memory budget covers (prompt plus answer). <c>mlx_lm.server</c> has no context limit of its
    /// own: a longer request is still served, beyond what admission counted. The editor's estimate and the model lists use it.
    /// </summary>
    public int ContextSize { get; init; } = 8_192;

    /// <summary>
    /// Requests decoded together (<c>--decode-concurrency</c>; <c>--prompt-concurrency</c> is the same number, at most
    /// <see cref="MaxPromptConcurrency"/>). mlx-lm's own default is 32, which an 8 GiB Mac cannot hold the KV cache for.
    /// </summary>
    public int Parallel { get; init; } = 4;

    /// <summary>
    /// The prompt cache (<c>--prompt-cache-bytes</c>), in GiB: KV caches kept between requests to skip re-reading a shared
    /// prefix. Always emitted, so it is bounded and admission counts it (mlx-lm otherwise only caps the entry count).
    /// </summary>
    public double PromptCacheGiB { get; init; } = 0.5;

    /// <summary><c>--max-tokens</c>: answer length when a request sets none. Null = mlx-lm's default (512).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTokens { get; init; }

    /// <summary><c>--kv-bits</c> (4 or 8): a quantized KV cache. mlx-lm then serves one request at a time. Null = full precision.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? KvBits { get; init; }

    /// <summary>
    /// <c>--chat-template-args</c>: a JSON object passed to the tokenizer's chat template, e.g.
    /// <c>{"enable_thinking":false}</c>. Null = none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChatTemplateArgs { get; init; }

    /// <summary>
    /// <c>--trust-remote-code</c>: lets the tokenizer run Python code shipped in the checkpoint folder. Off by default: a
    /// downloaded repository's code would run as the agent's account.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool TrustRemoteCode { get; init; }

    /// <summary><c>--draft-model {models}/{folder}</c>: a small MLX model for speculative decoding. Null = none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DraftModelFolder { get; init; }

    /// <summary><c>--num-draft-tokens</c> with a draft model. Null = mlx-lm's default (3).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NumDraftTokens { get; init; }

    /// <summary><c>--adapter-path {models}/{folder}</c>: LoRA adapter weights. Null = none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdapterFolder { get; init; }

    /// <summary>
    /// Pinned version of the node's <c>mlx-lm-server</c> (the mlx-lm release, e.g. <c>0.32.2</c>), sent as
    /// <c>spec.executableVersion</c>. Null = the node's default version. Part of the execution fingerprint.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutableVersion { get; init; }

    /// <summary>
    /// Additional raw <c>mlx_lm.server</c> arguments, appended last. <see cref="MlxFlags.RefusedExtraArgs"/> refuses the
    /// builder's own flags at save time (400) and the agent re-checks the argv (422 <c>spec_rejected</c>).
    /// </summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    /// <summary>Most requests whose prompts are processed together (mlx-lm's default <c>--prompt-concurrency</c>).</summary>
    public const int MaxPromptConcurrency = 8;

    /// <summary>Value equality, <see cref="ExtraArgs"/> element by element (a definition read back from JSON equals the one written).</summary>
    public bool Equals(MlxSettings? other) =>
        other is not null &&
        string.Equals(ModelFolder, other.ModelFolder, StringComparison.Ordinal) &&
        ContextSize == other.ContextSize &&
        Parallel == other.Parallel &&
        PromptCacheGiB.Equals(other.PromptCacheGiB) &&
        MaxTokens == other.MaxTokens &&
        KvBits == other.KvBits &&
        string.Equals(ChatTemplateArgs, other.ChatTemplateArgs, StringComparison.Ordinal) &&
        TrustRemoteCode == other.TrustRemoteCode &&
        string.Equals(DraftModelFolder, other.DraftModelFolder, StringComparison.Ordinal) &&
        NumDraftTokens == other.NumDraftTokens &&
        string.Equals(AdapterFolder, other.AdapterFolder, StringComparison.Ordinal) &&
        string.Equals(ExecutableVersion, other.ExecutableVersion, StringComparison.Ordinal) &&
        (ExtraArgs ?? []).SequenceEqual(other.ExtraArgs ?? [], StringComparer.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ModelFolder, StringComparer.Ordinal);
        hash.Add(ContextSize);
        hash.Add(Parallel);
        hash.Add(PromptCacheGiB);
        hash.Add(MaxTokens);
        hash.Add(KvBits);
        hash.Add(TrustRemoteCode);
        foreach (string arg in ExtraArgs ?? [])
        {
            hash.Add(arg, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// <c>mlx_lm.server</c> flags both sides agree on (docs/macos/implementation-plan.md, Phase 3; spike S4 pinned mlx-lm
/// 0.32.2). The server parses its arguments with Python's argparse, which also accepts any unambiguous <b>abbreviation</b>
/// of a long flag (<c>--hos</c> is <c>--host</c>) and keeps the last value of a repeated one, so a deny list cannot work:
/// every flag in an argv must be spelled exactly as one of <see cref="Owned"/> (only the builder emits those) or
/// <see cref="AllowedInExtraArgs"/>. The master's <c>MlxArgsBuilder</c> refuses anything else in ExtraArgs at save time;
/// the agent's <c>ProcessLaunchValidator</c> re-checks the whole argv: <c>--host</c> must be
/// <see cref="LlamaCppFlags.LoopbackHost"/>, <c>--port</c> the <c>{port}</c> token, and the path flags
/// (<see cref="BuilderPathFlags"/>) take <c>{models}/…</c> folders confined to the models root. The server has no API-key
/// option: its port is bound to loopback and reached only through the agent's mTLS proxy (plan M4).
/// </summary>
public static class MlxFlags
{
    /// <summary>The checkpoint folder flag.</summary>
    public const string Model = "--model";

    /// <summary>Path-valued flags only the builder may emit, each value a <c>{models}/…</c> folder.</summary>
    public static IReadOnlyList<string> BuilderPathFlags { get; } = [Model, "--adapter-path", "--draft-model"];

    /// <summary>
    /// The flags the builder emits (bind address, port, the folders, and the typed settings admission relies on). ExtraArgs
    /// may not repeat them: argparse keeps the last value, which would silently override the typed setting.
    /// </summary>
    public static IReadOnlyList<string> Owned { get; } =
    [
        "--host", "--port", Model, "--adapter-path", "--draft-model", "--num-draft-tokens", "--trust-remote-code",
        "--decode-concurrency", "--prompt-concurrency", "--prompt-cache-bytes", "--kv-bits", "--max-tokens", "--chat-template-args",
    ];

    /// <summary>
    /// The only flags ExtraArgs may carry: sampling defaults, logging, the chat template (a template string, not a path),
    /// prefill and prompt-cache entry tuning, the KV-quantization details and CORS. Not here, so refused: <c>--pipeline</c>
    /// (distributed serving across Macs) and <c>-h</c>/<c>--help</c> (the server would print its usage and exit, which
    /// reads as a crash loop).
    /// </summary>
    public static IReadOnlyList<string> AllowedInExtraArgs { get; } =
    [
        "--log-level", "--chat-template", "--use-default-chat-template", "--temp", "--top-p", "--top-k", "--min-p",
        "--prefill-step-size", "--prompt-cache-size", "--kv-group-size", "--quantized-kv-start", "--allowed-origins",
    ];

    /// <summary>
    /// True when <paramref name="arg"/> is a flag token: it starts with <c>-</c> and is not a number (argparse reads
    /// <c>-1</c> or <c>-0.5</c> as a value, since the server has no flag that looks like a number).
    /// </summary>
    public static bool IsFlagToken(string? arg) =>
        arg is { Length: > 1 } && arg[0] == '-' &&
        !double.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);

    /// <summary>The flag of a token: <c>--flag=value</c> → <c>--flag</c>.</summary>
    public static string FlagOf(string arg)
    {
        ArgumentNullException.ThrowIfNull(arg);
        int equals = arg.IndexOf('=', StringComparison.Ordinal);
        return equals > 0 ? arg[..equals] : arg;
    }

    /// <summary>
    /// The request fields the agent's proxy rewrites before a request reaches the server (spike S4 "contract trap"): the
    /// server loads whatever <c>model</c>, <c>draft_model</c> or <c>adapters</c> names, so <c>model</c> is set to
    /// <see cref="DefaultModel"/> (the folder it was started with) and the other two are removed.
    /// </summary>
    public static IReadOnlyList<string> RemovedRequestFields { get; } = ["draft_model", "adapters"];

    /// <summary>The <c>model</c> value that selects the folder the server was started with.</summary>
    public const string DefaultModel = "default_model";

    /// <summary>
    /// The ExtraArgs entries the builder refuses, in order (empty when none): every flag token that is not exactly one of
    /// <see cref="AllowedInExtraArgs"/> (owned flags, abbreviations, unknown flags), and any <c>{models}</c>/<c>{cache}</c>
    /// /<c>{port}</c> token.
    /// </summary>
    public static IReadOnlyList<string> RefusedExtraArgs(IEnumerable<string> extraArgs)
    {
        ArgumentNullException.ThrowIfNull(extraArgs);
        return [.. extraArgs.Where(a => a is null ||
            (IsFlagToken(a) && !AllowedInExtraArgs.Contains(FlagOf(a), StringComparer.Ordinal)) ||
            a.Contains(ProcessArgvTokens.Models, StringComparison.Ordinal) || a.Contains(ProcessArgvTokens.Cache, StringComparison.Ordinal) ||
            a.Contains(ProcessArgvTokens.Port, StringComparison.Ordinal))];
    }
}
