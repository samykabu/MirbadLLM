using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MirbadLLM.Core;

/// <summary>
/// Values of <see cref="LlamaCppSettings.Pooling"/> (<c>llama-server --pooling</c>, design-p3p4 §A.1.6). Frozen by MN-202
/// (D116); strings rather than an enum so a definition written by a later build with a new value still loads.
/// </summary>
public static class LlamaCppPooling
{
    /// <summary>No pooling (per-token vectors).</summary>
    public const string None = "none";

    /// <summary>Mean of the token vectors.</summary>
    public const string Mean = "mean";

    /// <summary>The CLS token.</summary>
    public const string Cls = "cls";

    /// <summary>The last token (Qwen3-Embedding's <c>pooling_type</c>).</summary>
    public const string Last = "last";

    /// <summary>Reranking.</summary>
    public const string Rank = "rank";

    /// <summary>Every accepted value, in the order the editor lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [None, Mean, Cls, Last, Rank];

    /// <summary>True for one of <see cref="All"/> (ordinal).</summary>
    public static bool IsValid(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);
}

/// <summary>
/// <see cref="LlamaCppSettings.ChatTemplate"/>: <c>builtin:{name}</c> or a file name inside the model's folder (design-p3p4
/// §A.2.2: the planner uses <c>builtin:chatml</c>, the fallback is <c>chatml-ollama.jinja</c>). Frozen by MN-202 (D116).
/// </summary>
/// <param name="BuiltinName">The built-in template name (<c>--chat-template</c>); null for a file.</param>
/// <param name="File">The file name relative to the model folder (<c>--chat-template-file</c>); null for a built-in.</param>
public sealed partial record LlamaCppChatTemplate(string? BuiltinName, string? File)
{
    /// <summary>Prefix of a built-in template value.</summary>
    public const string BuiltinPrefix = "builtin:";

    /// <summary>The built-in ChatML template (<c>src/llama-chat.cpp</c>).</summary>
    public const string ChatMl = BuiltinPrefix + "chatml";

    /// <summary>
    /// Parses a stored value. A built-in name is <c>[a-z0-9._-]{1,64}</c>; a file is one plain file name (no directory
    /// separator, no <c>..</c>, no drive or stream colon), confined to the model folder by the agent. False otherwise.
    /// </summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out LlamaCppChatTemplate? template)
    {
        template = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.StartsWith(BuiltinPrefix, StringComparison.Ordinal))
        {
            string name = value[BuiltinPrefix.Length..];
            if (!BuiltinNameRegex().IsMatch(name))
            {
                return false;
            }

            template = new LlamaCppChatTemplate(name, null);
            return true;
        }

        if (!FileNameRegex().IsMatch(value) || value is "." or "..")
        {
            return false;
        }

        template = new LlamaCppChatTemplate(null, value);
        return true;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex BuiltinNameRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._ -]{0,199}$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNameRegex();
}

/// <summary>
/// <c>llama-server</c> flags both sides must agree on (design-p3p4 §A.1.6, §A.8; frozen by MN-202, D116). The master's
/// <c>LlamaCppArgsBuilder</c> (MN-210) refuses <see cref="RefusedInExtraArgs"/> and <see cref="PathValued"/> in
/// <see cref="LlamaCppSettings.ExtraArgs"/> at save time (400) and emits <see cref="AlwaysArgs"/>; the agent's
/// <c>ProcessLaunchValidator</c> (MN-211) re-checks the whole argv (422 <c>spec_rejected</c>): a path-valued flag is
/// allowed only if it is in <see cref="BuilderPathFlags"/> and its value starts with <c>{models}/</c> and stays inside the
/// models root after resolving links; <c>--host</c> must be <see cref="LoopbackHost"/>; <c>--port</c> must be the
/// <c>{port}</c> token; <c>--fit</c> must be <c>off</c>; no download or API-key flag at all. Flags are compared ordinally;
/// <c>--flag=value</c> counts as <c>--flag</c>.
/// </summary>
public static class LlamaCppFlags
{
    /// <summary>The only address a process may bind.</summary>
    public const string LoopbackHost = "127.0.0.1";

    /// <summary>
    /// Arguments the builder always emits, in this order, after the typed settings and before ExtraArgs
    /// (<c>{port}</c> is substituted by the agent): bind loopback, Prometheus metrics, no web UI (<c>--no-ui</c>;
    /// <c>--no-webui</c> is deprecated at the pinned build, spike S1 C4), no host-RAM prompt cache (<c>--cache-ram 0</c>;
    /// llama-server otherwise keeps up to 8 GiB of host RAM for it, which admission does not count, S1 C6), and
    /// <c>--fit off</c> because admission decides what fits (llama.cpp would otherwise silently shrink unset values,
    /// §A.1.6). MN-207 (D118) replaced <c>--no-webui</c> and added <c>--cache-ram 0</c>.
    /// </summary>
    public static IReadOnlyList<string> AlwaysArgs { get; } =
        ["--host", LoopbackHost, "--port", ProcessArgvTokens.Port, "--metrics", "--no-ui", "--cache-ram", "0", "--fit", "off"];

    /// <summary>Path-valued flags only the builder may emit (each value <c>{models}/…</c>).</summary>
    public static IReadOnlyList<string> BuilderPathFlags { get; } = ["-m", "--model", "-mm", "--mmproj", "--chat-template-file"];

    /// <summary>
    /// Every flag that takes a path, a URL or a download source. Refused in ExtraArgs; in argv only the
    /// <see cref="BuilderPathFlags"/> pass (with the confinement rule above). The agent always downloads and verifies
    /// models itself, so llama.cpp's own download flags (<c>-hf</c>, <c>--hf-token</c>, <c>-mu</c>) never reach it.
    /// </summary>
    public static IReadOnlyList<string> PathValued { get; } =
    [
        "-m", "--model", "-mm", "--mmproj", "--chat-template-file", "-md", "--model-draft", "-mv", "--model-vocoder",
        "--lora", "--lora-scaled", "--control-vector", "--control-vector-scaled", "--grammar-file", "-jf", "--json-schema-file",
        "--slot-save-path", "--ssl-key-file", "--ssl-cert-file", "--log-file", "--path", "--api-key-file",
        "-mu", "--model-url", "-mmu", "--mmproj-url", "-hf", "-hfr", "--hf-repo", "-hff", "--hf-file", "-hfd", "-hfrd",
        "--hf-repo-draft", "-hfv", "-hfrv", "--hf-repo-v", "-hffv", "--hf-file-v", "-hft", "--hf-token",
        "-lcs", "--lookup-cache-static", "-lcd", "--lookup-cache-dynamic", "--rpc",
    ];

    /// <summary>
    /// Flags the builder owns and ExtraArgs may not set (besides <see cref="PathValued"/>): bind address and port, API
    /// key, served name, model file, <c>--fit</c>, the log file, device selection (§A.1.6), and the web UI and host-RAM
    /// prompt cache switches that <see cref="AlwaysArgs"/> fixes (MN-207, D118).
    /// </summary>
    public static IReadOnlyList<string> RefusedInExtraArgs { get; } =
    [
        "--host", "--port", "--api-key", "--api-key-file", "--alias", "-a", "-m", "--model", "--fit", "-fit", "--log-file", "--device", "-dev",
        "--ui", "--no-ui", "--webui", "--no-webui", "--cache-ram", "-cram",
    ];

    /// <summary>
    /// The typed settings the builder emits whose value admission, the GGUF estimate or the read-back rely on (context and
    /// slots, offload, batches, KV cache types, flash attention, embedding mode and pooling, the tensor split and main
    /// GPU), with their long and short aliases. Refused in ExtraArgs too: llama-server takes the last value of a repeated
    /// flag, so an ExtraArg would silently override the typed setting and skew the budget (MN-219, D126). Split mode
    /// stays an ExtraArg (<c>-sm row</c> has no typed setting).
    /// </summary>
    public static IReadOnlyList<string> TypedInArgv { get; } =
    [
        "-c", "--ctx-size", "-np", "--parallel", "-ngl", "--gpu-layers", "--n-gpu-layers", "-b", "--batch-size",
        "-ub", "--ubatch-size", "-ctk", "--cache-type-k", "-ctv", "--cache-type-v", "-fa", "--flash-attn",
        "--embedding", "--embeddings", "--pooling", "-ts", "--tensor-split", "-mg", "--main-gpu",
    ];

    /// <summary>True when <paramref name="arg"/> (or its <c>--flag=</c> part) is one of <paramref name="flags"/>.</summary>
    public static bool IsFlag(string arg, IReadOnlyList<string> flags)
    {
        ArgumentNullException.ThrowIfNull(flags);
        if (string.IsNullOrEmpty(arg) || arg[0] != '-')
        {
            return false;
        }

        int equals = arg.IndexOf('=', StringComparison.Ordinal);
        string flag = equals > 0 ? arg[..equals] : arg;
        return flags.Contains(flag, StringComparer.Ordinal);
    }

    /// <summary>
    /// The ExtraArgs entries the builder refuses (owned, path-valued or typed: <see cref="TypedInArgv"/>), in order; empty
    /// when none.
    /// </summary>
    public static IReadOnlyList<string> RefusedExtraArgs(IEnumerable<string> extraArgs)
    {
        ArgumentNullException.ThrowIfNull(extraArgs);
        return [.. extraArgs.Where(a => IsFlag(a, RefusedInExtraArgs) || IsFlag(a, PathValued) || IsFlag(a, TypedInArgv))];
    }
}

/// <summary>
/// Argv tokens of a process launch spec (design §4.3, design-p3p4 §A.1.4): the master never knows a host path or the
/// port; the agent substitutes these before it confines paths. <c>{models}</c> and <c>{cache}</c> are
/// <c>MirbadLLM.Agent.Contracts.LogicalVolumes.ModelsToken</c>/<c>CacheToken</c>.
/// </summary>
public static class ProcessArgvTokens
{
    /// <summary>The loopback port the agent allocated for the instance (MN-202, D116).</summary>
    public const string Port = "{port}";

    /// <summary>The models root.</summary>
    public const string Models = "{models}";

    /// <summary>The cache root.</summary>
    public const string Cache = "{cache}";
}

/// <summary>
/// The router's <c>dimensions</c> emulation for llama.cpp embedding models (design-p3p4 §A.3, frozen by MN-202, D116;
/// implemented by <c>MirbadLLM.Router.EmbeddingsCompat</c>, MN-217). It acts only on a request that <b>carries</b>
/// <see cref="DimensionsField"/> and goes to a model it <see cref="Applies"/> to: the router reads the field from the
/// buffered <c>/v1/embeddings</c> body, removes it and forwards the rest; on a 200 JSON answer it keeps the first N values
/// of every <c>data[i].embedding</c> (float array, or base64 little-endian float32 when <see cref="EncodingFormatField"/>
/// is <see cref="Base64Format"/>) and L2-renormalises them. N &lt; 1, a non-integer, or N above the returned vector length
/// → 400 in the OpenAI shape (<see cref="ErrorType"/>, param <c>dimensions</c>). A request <b>without</b> the field is
/// forwarded and answered byte for byte (OpenViking with <c>provider: "openai"</c> never sends it, takes the full
/// 2560 values and truncates to 1024 itself: spike S2, C7), as is everything else (other paths, other runtimes,
/// streaming, non-200 answers). MN-207 (D119) made the "only when sent" rule explicit. The DGX vLLM refuses
/// <c>dimensions</c> for Qwen3-Embedding-4B with 400 (not Matryoshka-enabled there); llama.cpp models answer it.
/// </summary>
public static class EmbeddingDimensions
{
    /// <summary>The OpenAI request field.</summary>
    public const string DimensionsField = "dimensions";

    /// <summary>The OpenAI request field selecting the vector encoding.</summary>
    public const string EncodingFormatField = "encoding_format";

    /// <summary>Base64 float32 vectors.</summary>
    public const string Base64Format = "base64";

    /// <summary>Float arrays (the default).</summary>
    public const string FloatFormat = "float";

    /// <summary>OpenAI error type of a refused <c>dimensions</c>.</summary>
    public const string ErrorType = "invalid_request_error";

    /// <summary>The only path the emulation touches, relative to <c>/v1</c>.</summary>
    public const string Path = "embeddings";

    /// <summary>True when the router emulates <c>dimensions</c> for <paramref name="model"/>.</summary>
    public static bool Applies(ModelDefinition? model) =>
        model is { Runtime: RuntimeKind.LlamaCpp, LlamaCpp: { Embedding: true } settings } && settings.EmulatesDimensions;
}

/// <summary>
/// The GGUF admission estimate of a llama.cpp model (design-p3p4 §A.1.10; computed by MN-209's
/// <c>LlamaCppMemoryEstimate</c>, shown by the editor and the fit preview, MN-224/MN-226). All values in GiB.
/// <c>Budget = 1.10 × (Weights + Kv + SsmState + Compute) + CudaContext × GPUs</c>.
/// </summary>
public sealed record GgufMemoryEstimate
{
    /// <summary>Tensor data offloaded to the GPUs (all split parts, × gpuLayers / blockCount for partial offload).</summary>
    public required double WeightsGiB { get; init; }

    /// <summary>KV cache for <c>ContextSize × Parallel</c> tokens with the chosen cache types.</summary>
    public required double KvGiB { get; init; }

    /// <summary>Recurrent state of hybrid models (0 for pure transformers).</summary>
    public double SsmStateGiB { get; init; }

    /// <summary>
    /// Compute buffers: <c>max(256 MiB, ub × (embeddingLength + vocab) × 4 B × 1.2)</c> with the <c>ub</c> the builder
    /// emits (<see cref="LlamaCppSettings.EmbeddingMicroBatch"/> for embedding models). Spike S1 measured ×1.076 on
    /// Qwen3-Embedding-4B (linear in <c>ub</c>), so ×1.2 stays as the conservative side (MN-207, D118).
    /// </summary>
    public required double ComputeGiB { get; init; }

    /// <summary>CUDA context, summed over the GPUs.</summary>
    public required double CudaContextGiB { get; init; }

    /// <summary>The estimated budget (what the editor proposes as <see cref="ModelDefinition.MemoryBudgetGiB"/>).</summary>
    public required double BudgetGiB { get; init; }

    /// <summary>Host RAM for layers left on the CPU (partial offload); 0 when every layer is on a GPU.</summary>
    public double HostGiB { get; init; }

    /// <summary>Per-GPU share of <see cref="BudgetGiB"/> in placement order (from the tensor split); empty for one GPU.</summary>
    public IReadOnlyList<double> PerGpuGiB { get; init; } = [];
}

/// <summary>
/// <c>deploy/agent/windows/llama-cpp.lock.json</c> (design-p3p4 §A.1.2, owner decision O2 / D108): the pinned official
/// llama.cpp release the Windows installer downloads and verifies, shipped in the <c>win-x64</c> agent bundle. Written by
/// MN-216 (SHA-256 from the GitHub API digest <b>and</b> recomputed after download; both must agree), read by
/// <c>install-agent.ps1</c> and checked by the bundle tests (MN-218). camelCase JSON.
/// </summary>
public sealed record LlamaCppLockFile
{
    /// <summary>Lock-file schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Release tag, e.g. <c>b11476</c>.</summary>
    public required string Version { get; init; }

    /// <summary>CUDA variant, <c>13.4</c> (native sm_120 kernels for RTX 50 cards).</summary>
    public required string Cuda { get; init; }

    /// <summary>
    /// Minimum NVIDIA driver version for that CUDA runtime: <c>615.00</c> for CUDA 13.4 (its R615 branch and the tested
    /// range; spike S1 / C10). The installer refuses an older driver.
    /// </summary>
    public required string MinDriver { get; init; }

    /// <summary>
    /// The release's commit (hex, e.g. <c>988190680</c>, as <c>llama-server --version</c> prints it). Every recent
    /// <c>bNNNN</c> release is flagged prerelease, so <c>releases/latest</c> never returns it: the pin is the tag, looked up
    /// by tag, plus this commit (spike S1 / C10; added by MN-207, D120). Null in a lock file written before.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Commit { get; init; }

    /// <summary>The release assets (the CUDA binaries zip and the cudart zip).</summary>
    public required IReadOnlyList<LlamaCppLockAsset> Assets { get; init; }

    /// <summary>
    /// SHA-256 of <b>every</b> file the assets unpack into the version folder (spike S1 / C3: <c>llama-server.exe</c> is a
    /// 9 KB launcher; the server is <c>llama-server-impl.dll</c> with <c>llama*.dll</c>, <c>ggml*.dll</c>, <c>mtmd.dll</c>,
    /// <c>libomp.dll</c> and the CUDA DLLs; 55 files at <c>b11476</c>). The installer unpacks both zips into one folder and
    /// requires exactly this set (every file present with its hash and size, nothing unlisted), then writes the folder's
    /// <c>manifest.json</c> (<c>ExecutableManifest</c>) with the same list, which the agent's catalogue re-verifies (added
    /// by MN-207, D120). An empty list (a lock file written before) is refused by the installer.
    /// </summary>
    public IReadOnlyList<LlamaCppLockedFile> Files { get; init; } = [];
}

/// <summary>One unpacked file in <see cref="LlamaCppLockFile.Files"/> (MN-207, D120).</summary>
/// <param name="Path">Path relative to the version folder, <c>/</c>-separated (the release zips are flat).</param>
/// <param name="Sha256">Lowercase hex SHA-256.</param>
/// <param name="Bytes">Size in bytes.</param>
public sealed record LlamaCppLockedFile(string Path, string Sha256, long Bytes);

/// <summary>One release asset in <see cref="LlamaCppLockFile"/>.</summary>
/// <param name="Name">Asset file name, e.g. <c>llama-b11476-bin-win-cuda-13.4-x64.zip</c>.</param>
/// <param name="Url">HTTPS download URL on <c>github.com</c>.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the asset.</param>
/// <param name="Bytes">Asset size in bytes.</param>
public sealed record LlamaCppLockAsset(
    string Name,
    string Url,
    string Sha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long Bytes = 0);
