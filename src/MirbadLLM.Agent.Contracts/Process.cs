namespace MirbadLLM.Agent.Contracts;

/// <summary>
/// Logical executable names a process launch spec may use (<see cref="LaunchSpec.Executable"/>, design-p3p4 §A.1.2;
/// frozen by MN-202, D116). The agent maps a name and version to a verified path through its executable catalogue
/// (<c>Agent:Executables</c>); the master never sends a path.
/// </summary>
public static class ExecutableNames
{
    /// <summary>llama.cpp's OpenAI-compatible server (<c>llama-server.exe</c> on Windows).</summary>
    public const string LlamaServer = "llama-server";

    /// <summary>
    /// mlx-lm's OpenAI-compatible server (docs/macos/implementation-plan.md, Phase 3, M6): the Python interpreter of the
    /// version folder's virtualenv, which the agent starts as <c>python -I -u -B -m mlx_lm.server</c>
    /// (<see cref="MlxInterpreterArgs"/>). The version is the mlx-lm release, e.g. <c>0.32.2</c>.
    /// </summary>
    public const string MlxLmServer = "mlx-lm-server";

    /// <summary>
    /// The interpreter arguments the agent puts before an MLX spec's argv: isolated mode (<c>-I</c>: no <c>PYTHON*</c>
    /// environment, no user site, no script folder on <c>sys.path</c>), unbuffered output (<c>-u</c>, so the log shows lines
    /// as they come) and no bytecode writes (<c>-B</c>: the folder is read-only for the agent's account).
    /// </summary>
    public static IReadOnlyList<string> MlxInterpreterArgs { get; } = ["-I", "-u", "-B", "-m", "mlx_lm.server"];
}

/// <summary>
/// The environment of a supervised process (design-p3p4 §A.1.3, §A.8; frozen by MN-202, D116). The agent builds the
/// child's environment block itself, complete, inheriting nothing from the service: <see cref="Reserved"/> names are set
/// by the agent and a launch spec that names any of them in <see cref="LaunchSpec.Env"/> is refused (422
/// <c>spec_rejected</c>). The per-instance API key goes in <see cref="LlamaApiKey"/>, never on a command line, and is never
/// returned by <c>launch-config</c>, logged or written to the operation log.
/// </summary>
public static class ProcessEnvironment
{
    /// <summary>The placed GPU UUIDs, comma-separated, in placement order (CUDA renumbers them from 0).</summary>
    public const string CudaVisibleDevices = "CUDA_VISIBLE_DEVICES";

    /// <summary>CUDA JIT cache in the agent's cache root (<c>{cache}/cuda</c>), not in the service account's profile.</summary>
    public const string CudaCachePath = "CUDA_CACHE_PATH";

    /// <summary>CUDA JIT cache size limit (<see cref="CudaCacheMaxBytes"/>).</summary>
    public const string CudaCacheMaxSize = "CUDA_CACHE_MAXSIZE";

    /// <summary><c>llama-server</c>'s API key (the per-instance 256-bit key, design-p3p4 §A.8).</summary>
    public const string LlamaApiKey = "LLAMA_API_KEY";

    /// <summary>The executable's version folder only.</summary>
    public const string Path = "PATH";

    /// <summary>Windows system root (needed by the CRT and CUDA on Windows).</summary>
    public const string SystemRoot = "SystemRoot";

    /// <summary>Temporary folder (<c>{cache}/tmp</c>).</summary>
    public const string Temp = "TEMP";

    /// <summary>Temporary folder (<c>{cache}/tmp</c>).</summary>
    public const string Tmp = "TMP";

    /// <summary>
    /// Temporary folder on macOS (<c>{cache}/tmp</c>; docs/macos/implementation-plan.md, Phase 2). Not in <see cref="Reserved"/>
    /// (the frozen P3 list): the driver sets it after the spec's own variables, so a spec cannot override it.
    /// </summary>
    public const string TmpDir = "TMPDIR";

    /// <summary>
    /// The MLX environment (docs/macos/implementation-plan.md, Phase 3), set by the driver after the spec's own variables,
    /// so a spec cannot override them (not in <see cref="Reserved"/>, the frozen P3 list; the validator refuses them in a spec):
    /// the Hugging Face libraries never download (<see cref="HfHubOffline"/>, <see cref="TransformersOffline"/>), keep their
    /// cache in the agent's cache root (<see cref="HfHome"/> = <c>{cache}/hf</c>) and send no telemetry; <see cref="Home"/>
    /// is <c>{cache}/home</c> so nothing writes to the service account's home.
    /// </summary>
    public static IReadOnlyList<string> MlxNames { get; } = [HfHubOffline, TransformersOffline, HfHome, HfHubDisableTelemetry, Home];

    /// <summary><c>HF_HUB_OFFLINE=1</c>: huggingface_hub never contacts the Hub (spike S4: a request's <c>model</c> could otherwise make the node download).</summary>
    public const string HfHubOffline = "HF_HUB_OFFLINE";

    /// <summary><c>TRANSFORMERS_OFFLINE=1</c>.</summary>
    public const string TransformersOffline = "TRANSFORMERS_OFFLINE";

    /// <summary>The Hugging Face cache (<c>{cache}/hf</c>).</summary>
    public const string HfHome = "HF_HOME";

    /// <summary><c>HF_HUB_DISABLE_TELEMETRY=1</c>.</summary>
    public const string HfHubDisableTelemetry = "HF_HUB_DISABLE_TELEMETRY";

    /// <summary>The home folder of an MLX process (<c>{cache}/home</c>).</summary>
    public const string Home = "HOME";

    /// <summary>
    /// True when a spec may not set <paramref name="name"/> for an MLX process: an <see cref="MlxNames"/> entry, or a
    /// <c>PYTHON*</c>, <c>HF_*</c>, <c>TRANSFORMERS_*</c> or <c>DYLD_*</c> variable (interpreter, library and loader
    /// behaviour the agent fixes). Ignoring case.
    /// </summary>
    public static bool IsReservedForMlx(string? name) =>
        name is not null && (IsReserved(name) || MlxNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
            name.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase) || name.StartsWith("HF_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("TRANSFORMERS_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("DYLD_", StringComparison.OrdinalIgnoreCase));

    /// <summary>Value of <see cref="CudaCacheMaxSize"/>: 4 GiB.</summary>
    public const long CudaCacheMaxBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Names only the agent sets. Compared case-insensitively (Windows environment names are).</summary>
    public static IReadOnlyList<string> Reserved { get; } =
        [CudaVisibleDevices, CudaCachePath, CudaCacheMaxSize, LlamaApiKey, Path, SystemRoot, Temp, Tmp];

    /// <summary>True when <paramref name="name"/> is reserved (ordinal, ignoring case).</summary>
    public static bool IsReserved(string? name) => name is not null && Reserved.Contains(name, StringComparer.OrdinalIgnoreCase);
}
