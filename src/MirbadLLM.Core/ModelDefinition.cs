using System.Text.Json;
using System.Text.Json.Serialization;

namespace MirbadLLM.Core;

/// <summary>
/// A model the controller can run as one runtime instance (today a vLLM container). Multi-node placement and runtime
/// members are described in design §5.1; they are optional and merge-on-absent for <c>PUT</c> (§5.4).
/// </summary>
public sealed record ModelDefinition
{
    /// <summary>Stable slug ([a-z0-9-], max 40 chars). Used in container names (<c>ss-{Id}</c>) and URLs.</summary>
    public required string Id { get; init; }

    /// <summary>Name clients send in the OpenAI <c>model</c> field; passed to vLLM as <c>--served-model-name</c>.</summary>
    public required string ServedName { get; init; }

    /// <summary>Checkpoint directory relative to the models root (mounted at <c>/models</c> in the container).</summary>
    public required string ModelPath { get; init; }

    /// <summary>False for NIM: its image fetches the model, so <see cref="ModelPath"/> is only a label and no folder is checked.</summary>
    [JsonIgnore]
    public bool NeedsCheckpoint => Runtime != RuntimeKind.Nim;

    /// <summary>Online quantization (e.g. <c>fp8</c>); null when the checkpoint is already quantized.</summary>
    public string? Quantization { get; init; }

    /// <summary>vLLM <c>--kv-cache-dtype</c>.</summary>
    public string KvCacheDtype { get; init; } = "fp8";
    public int MaxModelLen { get; init; } = 262_144;
    public int MaxNumSeqs { get; init; } = 16;

    /// <summary>vLLM <c>--max-num-batched-tokens</c> (the chunked-prefill token budget per step). Null = vLLM default.</summary>
    public int? MaxNumBatchedTokens { get; init; }

    /// <summary>Unified-memory budget for this model (weights + KV cache + activations), in GiB.</summary>
    public double MemoryBudgetGiB { get; init; } = 36;

    public string? ToolCallParser { get; init; } = "qwen3_coder";
    public string? ReasoningParser { get; init; } = "qwen3";

    /// <summary>
    /// Prefix caching: true emits <c>--enable-prefix-caching</c>, false <c>--no-enable-prefix-caching</c>, null leaves
    /// vLLM's default. Definitions saved before the tri-state stored <c>false</c> meaning "off", and that is what false
    /// means now (it used to emit nothing, so vLLM's default — on for generation models — applied).
    /// </summary>
    public bool? EnablePrefixCaching { get; init; } = true;

    /// <summary>
    /// The whole <c>--speculative-config</c> object: method, depth, draft model and any other key (e.g.
    /// <c>moe_backend</c>). Null = speculative decoding off. A SGLang model uses it too: the method, depth and draft model
    /// become <c>sglang.launch_server</c> flags (<c>SglangSpeculativeArgs</c>).
    /// </summary>
    public SpeculativeDecoding? Speculative { get; init; }

    /// <summary>
    /// vLLM <c>cudagraph_mode</c> (e.g. <c>PIECEWISE</c>, required for MTP on GB10). Null = vLLM default. This is the
    /// <c>cudagraph_mode</c> key of the single <c>--compilation-config</c> argument; its other keys live in
    /// <see cref="CompilationConfig"/>.
    /// </summary>
    public string? CudaGraphMode { get; init; }

    /// <summary>
    /// Every <c>--compilation-config</c> key except <c>cudagraph_mode</c> (that one is <see cref="CudaGraphMode"/>),
    /// kept verbatim. The builder merges both into one argument. Null = none.
    /// </summary>
    public JsonFields? CompilationConfig { get; init; }

    /// <summary><c>--async-scheduling</c> (true), <c>--no-async-scheduling</c> (false), or vLLM's default (null).</summary>
    public bool? AsyncScheduling { get; init; }

    /// <summary>vLLM <c>--mamba-cache-mode</c> for hybrid (Mamba) models, e.g. <c>align</c> or <c>none</c>. Null = vLLM default.</summary>
    public string? MambaCacheMode { get; init; }

    /// <summary>Overrides <see cref="MirbadLLMOptions.VllmImage"/> for this model.</summary>
    public string? Image { get; init; }

    /// <summary>
    /// The approved digest (<c>sha256:</c> + 64 hex) of <see cref="Image"/>, sent as <c>spec.imageDigest</c> so a node that
    /// requires digests (design §10.3) runs this model's own image (P2, W7 review MN-126). Null = none: the image runs by
    /// tag where the node allows tag pulls (the DGX, whose locally built images have no repo digest, keeps
    /// <c>AllowTagPulls=true</c>). Ignored without an <see cref="Image"/> of its own (the node default image carries its own
    /// digest, <see cref="NodeRecord.DefaultVllmImageDigest"/>). Not part of the legacy config hash; the execution
    /// fingerprint covers it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageDigest { get; init; }

    /// <summary>
    /// Additional raw vLLM CLI arguments, appended last. Flags mirbadllm owns (speculative and compilation config,
    /// max num seqs, max batched tokens, KV cache dtype, prefix caching, async scheduling, mamba cache mode) never stay
    /// here: <c>VllmArgsNormalizer</c> lifts them into their typed fields when a definition is loaded or saved.
    /// </summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    /// <summary>Start this model when the controller boots.</summary>
    public bool AutoStart { get; init; }

    /// <summary>
    /// Labels for finding the model (<c>chat</c>, <c>embedding</c>, <c>vision</c>, … — see <see cref="ModelTags"/>):
    /// lowercase slugs, at most <see cref="ModelTags.MaxTags"/>. Not a launch argument, so changing them never
    /// needs a restart.
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// Where the checkpoint folders come from, so a restore can download them again. The source whose
    /// <see cref="ModelSource.Folder"/> equals <see cref="ModelPath"/> is the primary checkpoint; any other is an
    /// extra checkpoint the model needs (e.g. a speculative draft model referenced in <see cref="ExtraArgs"/>).
    /// Not a launch argument, so changing them never needs a restart.
    /// </summary>
    public IReadOnlyList<ModelSource> Sources { get; init; } = [];

    // ---- Multi-node members (design §5.1). All optional in JSON and omitted when at their default, so files and
    // ---- API payloads written before multi-node stay byte-identical when re-saved, and old clients keep working.

    /// <summary>
    /// Where the model runs (design §5.1, §5.4). Null = unplaced (restored without a node, or its node was revoked).
    /// In a version-1 <c>models.json</c> (bare array) every definition is placed on the bootstrap node by the one-time
    /// migration; in a version-2 file <c>null</c> is preserved and nothing re-places a model on load.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelPlacement? Placement { get; init; }

    /// <summary>Runtime kind (design §5.1). Absent in JSON = <see cref="RuntimeKind.Vllm"/> (every existing definition).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public RuntimeKind Runtime { get; init; } = RuntimeKind.Vllm;

    /// <summary>
    /// llama.cpp settings (design §5.1); required when <see cref="Runtime"/> is <see cref="RuntimeKind.LlamaCpp"/>,
    /// ignored otherwise. Shape only in P1; used from P3.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LlamaCppSettings? LlamaCpp { get; init; }

    /// <summary>
    /// MLX settings (docs/macos/implementation-plan.md, Phase 3); required when <see cref="Runtime"/> is
    /// <see cref="RuntimeKind.Mlx"/>, refused otherwise. MLX always runs as a native process on an Apple Silicon node.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MlxSettings? Mlx { get; init; }

    /// <summary>Shared settings for SGLang and NIM container runtimes. Null for all existing runtime kinds.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContainerRuntimeSettings? Container { get; init; }

    /// <summary>
    /// Host RAM this model needs besides its GPU budget on discrete nodes (design §5.1, §6.1). Null = the runtime's
    /// default estimate. Unified nodes have one pool and ignore it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? HostMemoryBudgetGiB { get; init; }

    /// <summary>
    /// Keep serving this model while a benchmark or tuning job holds its node (design §6.5, from P2). The model under
    /// test is never exempt. P1 keeps today's global maintenance, so the flag is stored but has no effect yet.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool KeepServingDuringJobs { get; init; }

    // ---- Swap on demand. All three are off by default and omitted from JSON at their default; none is a launch
    // ---- argument, so changing them never needs a restart.

    /// <summary>
    /// Start this model when a request names it while it is stopped; the request waits until the model is Ready or
    /// <see cref="MirbadLLMOptions.StartupTimeout"/> passes. Also on for every model when
    /// <see cref="MirbadLLMOptions.AutoStartOnRequest"/> is set. Not <see cref="AutoStart"/>, which starts it at boot.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool StartOnRequest { get; init; }

    /// <summary>
    /// Stop this model once it has been Ready for this many minutes with no request in flight and none finished.
    /// Null = never. At least 1.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? IdleStopMinutes { get; init; }

    /// <summary>
    /// This model may be stopped, while Ready and unused, to free the memory a start on request needs on its node
    /// (<see cref="IRoomMaker"/>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool StopToMakeRoom { get; init; }
}

/// <summary>
/// Where a model runs (design §5.1): the node and, on a discrete-GPU node, the GPUs. Equality compares the GPU lists
/// element by element (a placement read back from JSON equals the one it was written from).
/// </summary>
/// <param name="NodeId">Node id slug (design §3.1), e.g. <c>dgx</c>. Immutable once the model is placed; changing it is a move (§6.3).</param>
/// <param name="GpuUuids">
/// Ordered GPU UUIDs, persisted when the placement is saved: the identity of the placement (design §5.1 "GPU identity
/// is the UUID"). Null or empty on unified nodes.
/// </param>
/// <param name="GpuIndices">Display/selection aid only, refreshed from the UUIDs; never used to pick a device.</param>
/// <param name="Generation">
/// Placement generation (design §6.7; P2, frozen by MN-101, D73): incremented on every placement change (move, unplace,
/// GPU change) by the registry's placement validator (MN-108). A save carrying an older generation than the stored one is
/// refused with 409 <c>placement_changed</c>, so a stale read-merge-write can never revert a move. 0 = never changed;
/// omitted from JSON at 0, so P1 files and payloads are unchanged. Not part of equality (same node and GPUs = same
/// placement).
/// </param>
public sealed record ModelPlacement(
    string NodeId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? GpuUuids = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<int>? GpuIndices = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long Generation = 0)
{
    /// <summary>True when <paramref name="other"/> names the same node and the same ordered GPU UUIDs (indices and generation ignored).</summary>
    public bool SameLocation(ModelPlacement? other) =>
        other is not null &&
        string.Equals(NodeId, other.NodeId, StringComparison.Ordinal) &&
        SequenceEqual(GpuUuids, other.GpuUuids);

    /// <inheritdoc/>
    public bool Equals(ModelPlacement? other) =>
        other is not null &&
        string.Equals(NodeId, other.NodeId, StringComparison.Ordinal) &&
        SequenceEqual(GpuUuids, other.GpuUuids) &&
        SequenceEqual(GpuIndices, other.GpuIndices);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(NodeId, StringComparer.Ordinal);
        foreach (string uuid in GpuUuids ?? [])
        {
            hash.Add(uuid, StringComparer.Ordinal);
        }

        foreach (int index in GpuIndices ?? [])
        {
            hash.Add(index);
        }

        return hash.ToHashCode();
    }

    private static bool SequenceEqual<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b) =>
        (a is null || a.Count == 0) ? (b is null || b.Count == 0) : b is not null && a.SequenceEqual(b);
}

/// <summary>
/// How a model is served (design §1 glossary, §5.1). Serialized as <c>vllm</c> / <c>llamacpp</c> / <c>mlx</c> by every
/// serializer (the registry's default options included).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeKind>))]
public enum RuntimeKind
{
    /// <summary>vLLM (today's only runtime).</summary>
    [JsonStringEnumMemberName("vllm")]
    Vllm,

    /// <summary>llama.cpp <c>llama-server</c> (P3).</summary>
    [JsonStringEnumMemberName("llamacpp")]
    LlamaCpp,

    /// <summary>MLX <c>mlx_lm.server</c> on Apple Silicon (macOS Phase 3), always a native process.</summary>
    [JsonStringEnumMemberName("mlx")]
    Mlx,

    /// <summary>SGLang's OpenAI-compatible server in a Linux container.</summary>
    [JsonStringEnumMemberName("sglang")]
    Sglang,

    /// <summary>NVIDIA NIM in an approved container on configured nodes.</summary>
    [JsonStringEnumMemberName("nim")]
    Nim,
}

/// <summary>Minimal settings shared by the additional container runtimes.</summary>
public sealed record ContainerRuntimeSettings
{
    /// <summary>Explicit container image.</summary>
    public required string Image { get; init; }

    /// <summary>Approved image digest. Required for NIM and on nodes enforcing digest pinning.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageDigest { get; init; }

    /// <summary>Validated runtime arguments appended after arguments owned by mirbadllm.</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    /// <summary>Plain environment variables. These values show in the portal and APIs, so a secret goes in <see cref="SecretEnv"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>Secret variables: the variable's name and the name of the saved token holding its value, read only when the model starts.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? SecretEnv { get; init; }

    /// <summary>The login for pulling <see cref="Image"/> from a private registry; null for a public image.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContainerRegistryLogin? RegistryLogin { get; init; }
}

/// <summary>A private registry login. The password is the saved token <paramref name="TokenName"/>, sent only to <paramref name="Host"/>.</summary>
public sealed record ContainerRegistryLogin(string Host, string Username, string TokenName);

/// <summary>Reads the registry out of a container image reference.</summary>
public static class ContainerImageRegistry
{
    /// <summary>The registry host of <paramref name="image"/>, or <c>docker.io</c> when it names none.</summary>
    public static string HostOf(string image)
    {
        ArgumentNullException.ThrowIfNull(image);
        int slash = image.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
        {
            return "docker.io";
        }

        string first = image[..slash];
        return first.Contains('.', StringComparison.Ordinal) || first.Contains(':', StringComparison.Ordinal) || first == "localhost"
            ? first
            : "docker.io";
    }
}

/// <summary>
/// How a runtime kind is executed on a node (design §1 glossary, §6.6): a Docker container or a supervised native
/// process. Serialized as <c>container</c> / <c>process</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LaunchDelivery>))]
public enum LaunchDelivery
{
    /// <summary>A Docker container (the DGX always uses this).</summary>
    [JsonStringEnumMemberName("container")]
    Container,

    /// <summary>A native process supervised by the agent (Linux PCs, Windows, macOS).</summary>
    [JsonStringEnumMemberName("process")]
    Process,
}

/// <summary>
/// llama.cpp settings of a model (design §5.1, design-p3p4 §A.1.6). Shape only in P1: stored and round-tripped, used by
/// the launch-spec builder from P3 (<c>LlamaCppArgsBuilder</c>, MN-210, is the only place that knows <c>llama-server</c>
/// flags). Comments name the flag each value becomes. The members added by MN-202 (D116) are omitted from JSON at their
/// default, so a definition saved before P3 is byte-identical when re-saved.
/// </summary>
public sealed record LlamaCppSettings
{
    /// <summary>GGUF path relative to the models root (the first part of a split model).</summary>
    public required string ModelFile { get; init; }

    /// <summary><c>--mmproj</c> (vision projector), relative to the models root. Null = none.</summary>
    public string? ProjectorFile { get; init; }

    /// <summary>
    /// Context size in tokens <b>per slot</b>, i.e. per request (design-p3p4 §A.1.6, MN-202). The builder emits
    /// <c>-c ContextSize × Parallel</c>, because <c>llama-server</c> divides <c>-c</c> among its <c>-np</c> slots.
    /// </summary>
    public int ContextSize { get; init; } = 32_768;

    /// <summary><c>-np</c> parallel slots.</summary>
    public int Parallel { get; init; } = 4;

    /// <summary><c>-ngl</c> layers offloaded to the GPU (999 = all; fewer = partial offload, design §6.1).</summary>
    public int GpuLayers { get; init; } = 999;

    /// <summary><c>-fa</c>: true = on, false = off, null = auto.</summary>
    public bool? FlashAttention { get; init; }

    /// <summary><c>-ctk</c> K cache type (e.g. <c>q8_0</c>). Null = llama.cpp default.</summary>
    public string? CacheTypeK { get; init; }

    /// <summary><c>-ctv</c> V cache type. Null = llama.cpp default.</summary>
    public string? CacheTypeV { get; init; }

    /// <summary><c>-b</c> logical batch size. Null = default.</summary>
    public int? BatchSize { get; init; }

    /// <summary><c>-ub</c> physical (micro) batch size. Null = default.</summary>
    public int? MicroBatchSize { get; init; }

    /// <summary>Container (default; the DGX's only option, design §6.6) or native process.</summary>
    public LaunchDelivery Delivery { get; init; } = LaunchDelivery.Container;

    /// <summary>Container image for container delivery. Null = the node's default llama.cpp image.</summary>
    public string? Image { get; init; }

    /// <summary>Approved digest of <see cref="Image"/>. Null uses the node default image digest.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageDigest { get; init; }

    /// <summary>
    /// Additional raw <c>llama-server</c> arguments, appended last. The flags in <see cref="LlamaCppFlags.RefusedInExtraArgs"/>
    /// and every path-valued flag (<see cref="LlamaCppFlags.PathValued"/>) are refused at save time (400) and again by the
    /// agent (422 <c>spec_rejected</c>).
    /// </summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    // ---- P3 (MN-202, D116). Omitted from JSON at their default. ----

    /// <summary>
    /// Embedding mode: <c>--embedding --pooling {Pooling}</c> and <c>-b = -ub = </c><see cref="EmbeddingMicroBatch"/>
    /// (§A.1.7; llama-server forces <c>n_batch = n_ubatch</c> with embeddings on). Makes <see cref="PoolingDetection.Detect"/>
    /// answer <see cref="PoolingMode.Embedding"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Embedding { get; init; }

    /// <summary>
    /// <c>--pooling</c> for embedding models: one of <see cref="LlamaCppPooling.All"/>. Null = the GGUF's own
    /// <c>pooling_type</c> (no flag emitted).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Pooling { get; init; }

    /// <summary>
    /// Router embeddings compatibility (§A.3, MN-217): the router honours the OpenAI <c>dimensions</c> field itself
    /// (truncate + L2 renormalise), because <c>llama-server</c> ignores it. Null = on for embedding models
    /// (<see cref="EmulatesDimensions"/>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EmulateDimensions { get; init; }

    /// <summary>
    /// Chat template (§A.2.2, §A.7): <c>builtin:{name}</c> (<c>--chat-template {name}</c>, e.g. <c>builtin:chatml</c>) or a
    /// file name relative to the model's folder (<c>--chat-template-file {models}/{folder}/{file}</c>). Null = the GGUF's
    /// embedded template. Parse with <see cref="LlamaCppChatTemplate.TryParse"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChatTemplate { get; init; }

    /// <summary><c>--reasoning</c>: true = on, false = off, null = auto (llama.cpp's default; no flag emitted).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Reasoning { get; init; }

    /// <summary><c>-t</c> CPU threads. Null = llama.cpp's choice.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Threads { get; init; }

    /// <summary>
    /// Per-GPU budget in GiB, in <see cref="ModelPlacement.GpuUuids"/> order (§A.1.5): the tensor split is
    /// <c>budget_i / Σ budget</c> (3 decimals) and admission checks each GPU against its own entry (feature
    /// <c>admission.gpu-budgets</c>). When set, its count must equal the placed GPU count and its sum must equal
    /// <see cref="ModelDefinition.MemoryBudgetGiB"/> (placement problem <see cref="PlacementProblemCodes.GpuBudgetsMismatch"/>).
    /// Null = proportional to each card's <c>memory.total</c> minus its reserve, and P2's even split in admission.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<double>? GpuBudgetsGiB { get; init; }

    /// <summary>
    /// <c>--split-mode</c> across GPUs. Null = derived: <see cref="LlamaCppSplitMode.None"/> on one GPU,
    /// <see cref="LlamaCppSplitMode.Layer"/> on two or more (the only modes the editor offers; <c>row</c> stays an ExtraArg).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LlamaCppSplitMode? SplitMode { get; init; }

    /// <summary>
    /// Pinned version of the node's <c>llama-server</c> executable (a release tag such as <c>b11476</c>), sent as
    /// <c>spec.executableVersion</c>. Null = the node's default version (the agent resolves it and reports it back in
    /// <c>launch-config</c>). Part of the execution fingerprint, so changing it marks the model "restart pending".
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutableVersion { get; init; }

    /// <summary>True when the router emulates <c>dimensions</c> (§A.3): <see cref="EmulateDimensions"/>, else <see cref="Embedding"/>.</summary>
    [JsonIgnore]
    public bool EmulatesDimensions => EmulateDimensions ?? Embedding;

    /// <summary>
    /// The micro-batch (<c>-ub</c>, also emitted as <c>-b</c>) of an embedding model (design-p3p4 §A.1.7, spike S1 / C2;
    /// MN-207, D118): <see cref="MicroBatchSize"/> when set; otherwise, for a causal model (GGUF
    /// <c>{arch}.attention.causal</c> absent or true: Qwen3-Embedding, last-token pooling) <c>min(ContextSize,
    /// </c><see cref="CausalEmbeddingMicroBatch"/><c>)</c>, because a causal model processes a long input over several
    /// micro-batches against the KV cache (8 102-token inputs embed identically at <c>-ub 2048</c> and <c>-ub 8192</c>, and
    /// the compute buffer is linear in <c>ub</c>: 1.3 vs 5.1 GiB); for a non-causal model (BERT-style,
    /// <c>attention.causal = false</c>) <see cref="ContextSize"/>, because its whole input must fit one micro-batch. The
    /// builder (MN-210) does not read the GGUF, so it calls this with the default (causal); the editor (MN-226) writes
    /// <see cref="MicroBatchSize"/> = <see cref="ContextSize"/> for a GGUF whose summary says non-causal.
    /// </summary>
    /// <param name="causalAttention">The GGUF's <c>attention.causal</c>; null (absent) counts as causal, as in llama.cpp.</param>
    public int EmbeddingMicroBatch(bool? causalAttention = null) =>
        MicroBatchSize ?? (causalAttention == false ? ContextSize : Math.Min(ContextSize, CausalEmbeddingMicroBatch));

    /// <summary>Default micro-batch of a causal embedding model (<see cref="EmbeddingMicroBatch"/>): 2048 tokens.</summary>
    public const int CausalEmbeddingMicroBatch = 2048;

    /// <summary>The split mode that applies to <paramref name="gpuCount"/> placed GPUs (<see cref="SplitMode"/> or the derived default).</summary>
    public LlamaCppSplitMode EffectiveSplitMode(int gpuCount) =>
        SplitMode ?? (gpuCount > 1 ? LlamaCppSplitMode.Layer : LlamaCppSplitMode.None);
}

/// <summary><c>llama-server --split-mode</c> (§A.1.5). Serialized as <c>none</c> / <c>layer</c> by every serializer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LlamaCppSplitMode>))]
public enum LlamaCppSplitMode
{
    /// <summary>One GPU (<c>--split-mode none --device CUDA0</c>).</summary>
    [JsonStringEnumMemberName("none")]
    None,

    /// <summary>Layers spread over the GPUs by <c>--tensor-split</c> (unequal GPUs allowed for llama.cpp).</summary>
    [JsonStringEnumMemberName("layer")]
    Layer,
}

/// <summary>
/// vLLM's <c>--speculative-config</c> object. <see cref="Method"/> (<c>method</c>; empty = vLLM infers it from the
/// draft model), <see cref="NumSpeculativeTokens"/> (<c>num_speculative_tokens</c>; 0 = not set) and
/// <see cref="DraftModel"/> (<c>model</c>) are typed; every other key is kept verbatim in <see cref="Extra"/>.
/// Serialized as the vLLM object itself (<c>{"method":"mtp","num_speculative_tokens":2}</c>); the pre-redesign shape
/// <c>{"Method":"mtp","NumSpeculativeTokens":2}</c> still reads.
/// </summary>
[JsonConverter(typeof(SpeculativeDecodingJsonConverter))]
public sealed record SpeculativeDecoding(string Method, int NumSpeculativeTokens)
{
    public const string MethodKey = "method";
    public const string NumTokensKey = "num_speculative_tokens";
    public const string DraftModelKey = "model";

    private readonly JsonFields _extra = JsonFields.Empty;

    /// <summary>Path of the draft model (vLLM <c>model</c> key), e.g. <c>/models/…-DSpark</c>. Null = none.</summary>
    public string? DraftModel { get; init; }

    /// <summary>
    /// Every other key of the object. A typed key in here (only possible when it had an unexpected JSON type) is
    /// overridden by the typed property when the object is written.
    /// </summary>
    public JsonFields Extra
    {
        get => _extra;
        init => _extra = value ?? JsonFields.Empty;
    }

    /// <summary>The full vLLM object.</summary>
    public JsonFields ToFields()
    {
        var fields = Extra;
        if (!string.IsNullOrEmpty(Method))
        {
            fields = fields.WithString(MethodKey, Method);
        }

        if (!string.IsNullOrEmpty(DraftModel))
        {
            fields = fields.WithString(DraftModelKey, DraftModel);
        }

        if (NumSpeculativeTokens != 0)
        {
            fields = fields.WithInt(NumTokensKey, NumSpeculativeTokens);
        }

        return fields;
    }

    /// <summary>The <c>--speculative-config</c> value: compact JSON, keys in ordinal order.</summary>
    public string ToJson() => ToFields().ToJson();

    /// <summary>Splits a vLLM object into the typed keys and <see cref="Extra"/>; a typed key of the wrong JSON type stays in Extra.</summary>
    public static SpeculativeDecoding FromFields(JsonFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        string? method = fields.GetString(MethodKey);
        int? tokens = fields.GetInt(NumTokensKey);
        string? draft = fields.GetString(DraftModelKey);
        var extra = fields;
        if (method is not null)
        {
            extra = extra.Without(MethodKey);
        }

        if (tokens is not null)
        {
            extra = extra.Without(NumTokensKey);
        }

        if (draft is not null)
        {
            extra = extra.Without(DraftModelKey);
        }

        return new SpeculativeDecoding(method ?? "", tokens ?? 0) { DraftModel = draft, Extra = extra };
    }
}

/// <summary>Writes <see cref="SpeculativeDecoding"/> as the vLLM object; reads that or the legacy PascalCase/camelCase shape.</summary>
public sealed class SpeculativeDecodingJsonConverter : JsonConverter<SpeculativeDecoding>
{
    private static readonly (string Legacy, string Key)[] LegacyNames =
    [
        ("Method", SpeculativeDecoding.MethodKey),
        ("NumSpeculativeTokens", SpeculativeDecoding.NumTokensKey),
        ("numSpeculativeTokens", SpeculativeDecoding.NumTokensKey),
        ("DraftModel", SpeculativeDecoding.DraftModelKey),
        ("draftModel", SpeculativeDecoding.DraftModelKey),
    ];

    public override SpeculativeDecoding? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        var fields = JsonFields.FromElement(doc.RootElement);
        foreach (var (legacy, key) in LegacyNames)
        {
            if (fields.TryGetValue(legacy, out string? raw))
            {
                fields = fields.Without(legacy);
                if (!fields.ContainsKey(key))
                {
                    fields = fields.With(key, raw);
                }
            }
        }

        return SpeculativeDecoding.FromFields(fields);
    }

    public override void Write(Utf8JsonWriter writer, SpeculativeDecoding value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteRawValue(value.ToJson());
    }
}

/// <summary>
/// Where a checkpoint folder under the models root is downloaded from: a Hugging Face repo (every source before P3), or
/// from P3 an Ollama-registry model pinned by digest (<see cref="Kind"/> = <see cref="ModelSourceKind.Ollama"/>,
/// design-p3p4 §A.2.2, owner decision O4 / D110). A move or restore re-downloads each source the same way.
/// </summary>
/// <param name="RepoId">
/// Hugging Face repo id, e.g. <c>ornith-ai/Ornith-1.5-9B</c>. For an Ollama source: <c>{name}:{tag}</c>, a display key
/// only (<see cref="Ollama"/> is authoritative).
/// </param>
/// <param name="Folder">Folder under the models root the repo is downloaded into.</param>
/// <param name="Revision">Branch, tag or commit SHA. Null means the repo's default branch. Unused for an Ollama source.</param>
/// <param name="AllowPatterns">Glob patterns of files to fetch. Null/empty uses the downloader's default set.</param>
public sealed record ModelSource(
    string RepoId,
    string Folder,
    string? Revision = null,
    IReadOnlyList<string>? AllowPatterns = null)
{
    /// <summary>
    /// Source kind (MN-202, D116). Absent in JSON = <see cref="ModelSourceKind.HuggingFace"/>, so every stored source
    /// is unchanged. Forward-only: a P2 master ignores the member and would treat an Ollama source as a Hugging Face repo,
    /// so downgrading the master after an Ollama source exists is not supported (design-p3p4 §A.9).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ModelSourceKind Kind { get; init; } = ModelSourceKind.HuggingFace;

    /// <summary>The Ollama-registry model when <see cref="Kind"/> is <see cref="ModelSourceKind.Ollama"/>; null otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OllamaSource? Ollama { get; init; }
}

/// <summary>Kind of a <see cref="ModelSource"/>. Serialized as <c>huggingface</c> / <c>ollama</c> by every serializer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelSourceKind>))]
public enum ModelSourceKind
{
    /// <summary>A Hugging Face repo (container helper or native downloader).</summary>
    [JsonStringEnumMemberName("huggingface")]
    HuggingFace,

    /// <summary>An Ollama-registry manifest's model layer, pinned by digest (feature <c>downloads.ollama</c>, MN-221).</summary>
    [JsonStringEnumMemberName("ollama")]
    Ollama,
}

/// <summary>
/// An Ollama-registry model (design-p3p4 §A.2.2): <c>{Registry}/v2/{Name}/manifests/{Tag}</c>, whose single
/// <c>application/vnd.ollama.image.model</c> layer is downloaded as <c>{folder}/model.gguf</c> and verified against
/// <see cref="ModelDigest"/>.
/// </summary>
/// <param name="Registry">Registry host, e.g. <c>registry.ollama.ai</c> (HTTPS only).</param>
/// <param name="Name">Model name with namespace, e.g. <c>guoxuter/ov_intent_analysis_sft</c>.</param>
/// <param name="Tag">Tag, e.g. <c>v7_q8</c>.</param>
/// <param name="ModelDigest">
/// The model layer's digest, <c>sha256:</c> + 64 lowercase hex. Null on a first download (the agent records the digest it
/// verified); set = the pin: a manifest whose model layer has another digest is refused.
/// </param>
public sealed record OllamaSource(string Registry, string Name, string Tag, string? ModelDigest = null)
{
    /// <summary>The public registry.</summary>
    public const string DefaultRegistry = "registry.ollama.ai";

    /// <summary>The display key <c>{name}:{tag}</c> (also <see cref="ModelSource.RepoId"/> of an Ollama source).</summary>
    [JsonIgnore]
    public string Key => Name + ":" + Tag;
}
