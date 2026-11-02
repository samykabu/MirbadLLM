using System.Text.RegularExpressions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.NodeRuntime.Runtimes.Process;

/// <summary>
/// A process launch spec checked against the node's guardrails (design-p3p4 §A.1.3 steps 1–2, §A.8) and resolved: the
/// verified executable, the argv with <c>{models}</c> substituted and confined (<c>{port}</c> is substituted at start),
/// the served name the Ready check expects, the launch environment and the GPU selection.
/// </summary>
/// <param name="ModelId">Model id.</param>
/// <param name="Executable">The verified executable (catalogue).</param>
/// <param name="Arguments">Arguments with <c>{models}</c> substituted; still holding the <c>{port}</c> token.</param>
/// <param name="Alias">The <c>--alias</c> value: what <c>/v1/models</c> must list before the instance is Ready.</param>
/// <param name="Environment">The spec's environment variables (secret values included: for the environment block only).</param>
/// <param name="SecretEnvNames">Names in <paramref name="Environment"/> whose values are secrets.</param>
/// <param name="GpuMode">The GPU selection mode.</param>
/// <param name="GpuUuids">The GPUs by UUID in placement order when <paramref name="GpuMode"/> is <c>uuids</c>; else null.</param>
/// <param name="SpecLabels">The spec's labels (no reserved names).</param>
/// <param name="Runtime">The runtime kind (llama.cpp or MLX): which environment the driver builds.</param>
public sealed record ProcessLaunchPlan(
    string ModelId,
    ResolvedExecutable Executable,
    IReadOnlyList<string> Arguments,
    string Alias,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> SecretEnvNames,
    GpuSelectionMode GpuMode,
    IReadOnlyList<string>? GpuUuids,
    IReadOnlyDictionary<string, string> SpecLabels,
    RuntimeKind Runtime = RuntimeKind.LlamaCpp)
{
    /// <summary>The arguments for <paramref name="port"/> (the <c>{port}</c> token substituted): what the host starts.</summary>
    public IReadOnlyList<string> ArgumentsFor(int port)
    {
        string text = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return [.. Arguments.Select(a => string.Equals(a, ProcessArgvTokens.Port, StringComparison.Ordinal) ? text : a)];
    }
}

/// <summary>
/// Checks a <c>kind: process</c> <see cref="LaunchSpec"/> before anything is reserved or started (422
/// <c>spec_rejected</c>, or the catalogue's 422 <c>executable_missing</c>/<c>executable_unverified</c>; design-p3p4
/// §A.1.3, §A.1.6, §A.8). The master's builder (MN-210) emits a canonical argv; the agent does not trust it:
/// <list type="bullet">
/// <item>the runtime is llama.cpp and the executable a logical name the catalogue verifies (never a path);</item>
/// <item>no image, digest, mount or <c>ipc: host</c>; health path <c>/health</c>; no agent-set label;</item>
/// <item>exactly one <c>--host 127.0.0.1</c> and one <c>--port {port}</c>; no API key flag (the key goes through
/// <c>LLAMA_API_KEY</c>); <c>--fit</c> only <c>off</c>, <c>--cache-ram</c> only <c>0</c>, no web UI;</item>
/// <item>a path only behind a builder path flag (<see cref="LlamaCppFlags.BuilderPathFlags"/>), as <c>{models}/…</c>,
/// confined to the models root after resolving symlinks and existing; every other path, URL or download flag
/// (<see cref="LlamaCppFlags.PathValued"/>) is refused anywhere; <c>{models}</c>/<c>{cache}</c> nowhere else;</item>
/// <item>exactly one <c>-m</c> and one <c>--alias</c>;</item>
/// <item>environment names of letters, digits and <c>_</c>; none of <see cref="ProcessEnvironment.Reserved"/> (any
/// case) or the GPU-selection variables; every secret name present once.</item>
/// </list>
/// An MLX spec (docs/macos/implementation-plan.md, Phase 3) follows the same outer rules with its own argv rules
/// (<see cref="MlxFlags"/>): every flag spelled exactly as one the builder owns or ExtraArgs may carry (argparse would
/// expand an abbreviation), exactly one <c>--host 127.0.0.1</c>, <c>--port {port}</c> and <c>--model {models}/…</c>, the
/// path flags naming folders confined to the models root, no GPU selection, and none of the
/// <see cref="ProcessEnvironment.IsReservedForMlx"/> variables. The plan's arguments start with the interpreter arguments
/// (<see cref="ExecutableNames.MlxInterpreterArgs"/>), and its alias is the model folder's real path, which
/// <c>/v1/models</c> lists once the server is up.
/// </summary>
public static partial class ProcessLaunchValidator
{
    /// <summary>Most arguments one launch may pass.</summary>
    public const int MaxArguments = 512;

    /// <summary>Longest single argument.</summary>
    public const int MaxArgumentLength = 4096;

    private static readonly string[] HostFlags = ["--host"];
    private static readonly string[] PortFlags = ["--port"];
    private static readonly string[] ApiKeyFlags = ["--api-key", "--api-key-file"];
    private static readonly string[] ModelFlags = ["-m", "--model"];
    private static readonly string[] AliasFlags = ["--alias", "-a"];
    private static readonly string[] FitFlags = ["--fit", "-fit"];
    private static readonly string[] CacheRamFlags = ["--cache-ram", "-cram"];
    private static readonly string[] WebUiOnFlags = ["--ui", "--webui"];
    private static readonly string[] DeviceFlags = ["--device", "-dev"];

    /// <summary>Validates and resolves <paramref name="spec"/> for <paramref name="modelId"/>.</summary>
    public static ProcessLaunchPlan Validate(string modelId, LaunchSpec spec, ProcessRuntimeOptions options, IExecutableCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(catalog);

        if (string.IsNullOrEmpty(modelId) || modelId.Length > 200 || !ModelIdRegex().IsMatch(modelId))
        {
            throw new AgentException(400, new AgentError
            {
                Code = ErrorCodes.InvalidRequest,
                Message = $"Model id '{modelId}' cannot name a process instance (letters, digits, '_', '.', '-').",
            });
        }

        if (spec.Kind != LaunchDelivery.Process)
        {
            throw Rejected($"this driver runs native processes; the spec asks for '{spec.Kind}'");
        }

        if (spec.Runtime is not (RuntimeKind.LlamaCpp or RuntimeKind.Mlx))
        {
            throw Rejected($"native processes run llama.cpp or MLX; the spec asks for '{spec.Runtime}'");
        }

        bool mlx = spec.Runtime == RuntimeKind.Mlx;
        // The other runtime's executable is refused here; any other unknown name is the catalogue's 422 executable_missing.
        string expectedExecutable = mlx ? ExecutableNames.MlxLmServer : ExecutableNames.LlamaServer;
        string otherExecutable = mlx ? ExecutableNames.LlamaServer : ExecutableNames.MlxLmServer;
        if (spec.Executable is { Length: > 0 } named && string.Equals(named, otherExecutable, StringComparison.Ordinal))
        {
            throw Rejected($"{(mlx ? "an MLX" : "a llama.cpp")} process runs '{expectedExecutable}', not '{named}'");
        }

        if (spec.Image is not null || spec.ImageDigest is not null)
        {
            throw Rejected("a process spec carries no image");
        }

        if (spec.Mounts is null || spec.Mounts.Count > 0)
        {
            throw Rejected("a process spec has no mounts (model paths are {models}/... arguments)");
        }

        if (spec.IpcHost)
        {
            throw Rejected("ipc: host is a container setting");
        }

        if (!string.Equals(spec.HealthPath, "/health", StringComparison.Ordinal))
        {
            throw Rejected("the health path is fixed to /health");
        }

        foreach (string key in spec.Labels?.Keys ?? throw Rejected("labels must not be null"))
        {
            if (ContainerLaunchValidator.DriverLabels.Contains(key, StringComparer.Ordinal) ||
                key.StartsWith(ContainerLabels.Download, StringComparison.Ordinal))
            {
                throw Rejected($"label '{key}' is set by the agent and cannot be in a spec");
            }
        }

        CheckEnvironment(spec, mlx);
        var (mode, uuids) = CheckGpuSelection(spec.Gpus);
        if (mlx && mode != GpuSelectionMode.All)
        {
            throw Rejected("an MLX process uses the node's unified memory; its GPU selection is 'all'");
        }

        var (arguments, alias) = mlx ? CheckMlxArguments(spec.Argv, options) : CheckArguments(spec.Argv, options);

        if (string.IsNullOrWhiteSpace(spec.Executable))
        {
            throw Rejected("the spec names no executable");
        }

        // Last: the catalogue may re-hash files (synchronously) and throws its own 422 codes.
        var executable = catalog.Resolve(spec.Executable, spec.ExecutableVersion);

        return new ProcessLaunchPlan(
            modelId, executable, arguments, alias,
            new Dictionary<string, string>(spec.Env, StringComparer.Ordinal), [.. spec.SecretEnvNames], mode, uuids,
            new Dictionary<string, string>(spec.Labels, StringComparer.Ordinal), spec.Runtime);
    }

    /// <summary>
    /// The argv rules of an MLX spec (<see cref="MlxFlags"/>); returns the arguments with the interpreter arguments in front
    /// and the folders substituted, and the model folder's real path (what <c>/v1/models</c> lists).
    /// </summary>
    /// <summary>
    /// The readiness alias of an MLX argv (the model folder's real path) from the argv rules alone, without the executable
    /// check: an adopted instance needs it while the catalogue may still be hashing the MLX folder after an agent start.
    /// </summary>
    internal static string MlxAlias(IReadOnlyList<string> argv, ProcessRuntimeOptions options) => CheckMlxArguments(argv, options).Alias;

    private static (IReadOnlyList<string> Arguments, string Alias) CheckMlxArguments(IReadOnlyList<string>? argv, ProcessRuntimeOptions options)
    {
        if (argv is null || argv.Count == 0)
        {
            throw Rejected("argv is empty");
        }

        if (argv.Count > MaxArguments)
        {
            throw Rejected($"at most {MaxArguments} arguments are allowed");
        }

        var result = new List<string>(argv.Count + ExecutableNames.MlxInterpreterArgs.Count);
        result.AddRange(ExecutableNames.MlxInterpreterArgs);
        int hosts = 0, ports = 0;
        string? model = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < argv.Count; i++)
        {
            string arg = argv[i] ?? throw Rejected("argv contains a null argument");
            if (arg.Length > MaxArgumentLength || arg.Any(c => c is '\0' or '\r' or '\n'))
            {
                throw Rejected($"argument {i} is too long or contains NUL or a line break");
            }

            if (!MlxFlags.IsFlagToken(arg))
            {
                if (arg.Contains(LogicalVolumes.ModelsToken, StringComparison.Ordinal) ||
                    arg.Contains(LogicalVolumes.CacheToken, StringComparison.Ordinal) ||
                    string.Equals(arg, ProcessArgvTokens.Port, StringComparison.Ordinal))
                {
                    throw Rejected($"argument {i} uses a token outside the flag that takes it");
                }

                result.Add(arg); // a flag's value (or a stray positional, which the server refuses itself)
                continue;
            }

            string flag = MlxFlags.FlagOf(arg);
            if (!MlxFlags.Owned.Contains(flag, StringComparer.Ordinal) && !MlxFlags.AllowedInExtraArgs.Contains(flag, StringComparer.Ordinal))
            {
                throw Rejected($"'{flag}' is not a flag an MLX launch may pass (flags are spelled exactly; abbreviations are refused)");
            }

            if (MlxFlags.Owned.Contains(flag, StringComparer.Ordinal))
            {
                if (!string.Equals(arg, flag, StringComparison.Ordinal))
                {
                    throw Rejected($"'{flag}' must take its value as the next argument");
                }

                if (!seen.Add(flag))
                {
                    throw Rejected($"'{flag}' is given twice");
                }
            }

            if (MlxFlags.BuilderPathFlags.Contains(flag, StringComparer.Ordinal))
            {
                string confined = ConfineModelFolder(flag, Next(argv, ref i, arg), options.ModelsRoot);
                result.Add(flag);
                result.Add(confined);
                if (string.Equals(flag, MlxFlags.Model, StringComparison.Ordinal))
                {
                    model = confined;
                }

                continue;
            }

            switch (flag)
            {
                case "--host":
                    ExpectValue(argv, ref i, arg, LlamaCppFlags.LoopbackHost, "instances bind 127.0.0.1 only");
                    hosts++;
                    result.Add(flag);
                    result.Add(LlamaCppFlags.LoopbackHost);
                    continue;
                case "--port":
                    ExpectValue(argv, ref i, arg, ProcessArgvTokens.Port, "the port is the agent's ({port})");
                    ports++;
                    result.Add(flag);
                    result.Add(ProcessArgvTokens.Port);
                    continue;
            }

            result.Add(arg);
        }

        if (hosts != 1 || ports != 1)
        {
            throw Rejected("argv must contain exactly one '--host 127.0.0.1' and one '--port {port}'");
        }

        if (model is null)
        {
            throw Rejected("argv must name the model folder (--model {models}/...)");
        }

        return (result, model);
    }

    /// <summary>
    /// <c>{models}/a/b</c> → the real path of an existing folder inside the models root (symlinks resolved, so it equals
    /// the <c>Path.resolve()</c> id <c>mlx_lm.server</c> lists in <c>/v1/models</c>).
    /// </summary>
    private static string ConfineModelFolder(string flag, string value, string modelsRoot)
    {
        string prefix = LogicalVolumes.ModelsToken + "/";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw Rejected($"'{flag}' takes a folder under {LogicalVolumes.ModelsToken}/");
        }

        string relative = value[prefix.Length..];
        if (PathConfinement.CheckPlainRelative(relative) is { } problem)
        {
            throw Rejected($"'{flag}' path '{value}': {problem}");
        }

        if (relative.Contains(LogicalVolumes.ModelsToken, StringComparison.Ordinal) ||
            relative.Contains(LogicalVolumes.CacheToken, StringComparison.Ordinal) ||
            relative.Contains(ProcessArgvTokens.Port, StringComparison.Ordinal))
        {
            throw Rejected($"'{flag}' path '{value}' contains another token");
        }

        string? resolved = PathConfinement.ResolveInside(modelsRoot, relative);
        string? realRoot = PathConfinement.RealPath(modelsRoot);
        if (resolved is null || realRoot is null)
        {
            throw Rejected($"'{flag}' path '{value}' does not resolve to an existing folder inside the models folder");
        }

        string full = Path.Combine(realRoot, resolved.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(full))
        {
            throw Rejected($"'{flag}' path '{value}' is not a folder (an MLX checkpoint is a folder with config.json and its weights)");
        }

        return full;
    }

    private static (IReadOnlyList<string> Arguments, string Alias) CheckArguments(IReadOnlyList<string>? argv, ProcessRuntimeOptions options)
    {
        if (argv is null || argv.Count == 0)
        {
            throw Rejected("argv is empty");
        }

        if (argv.Count > MaxArguments)
        {
            throw Rejected($"at most {MaxArguments} arguments are allowed");
        }

        var result = new List<string>(argv.Count);
        int hosts = 0, ports = 0, models = 0, aliases = 0;
        string? alias = null;
        var seenOnce = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < argv.Count; i++)
        {
            string arg = argv[i] ?? throw Rejected("argv contains a null argument");
            if (arg.Length > MaxArgumentLength || arg.Any(c => c is '\0' or '\r' or '\n'))
            {
                throw Rejected($"argument {i} is too long or contains NUL or a line break");
            }

            if (LlamaCppFlags.IsFlag(arg, LlamaCppFlags.PathValued))
            {
                if (!LlamaCppFlags.IsFlag(arg, LlamaCppFlags.BuilderPathFlags))
                {
                    throw Rejected($"'{FlagOf(arg)}' takes a path, URL or download source, which a launch cannot pass");
                }

                if (arg.Contains('=', StringComparison.Ordinal))
                {
                    throw Rejected($"'{FlagOf(arg)}' must take its path as the next argument");
                }

                string path = Next(argv, ref i, arg);
                if (ModelFlags.Contains(arg, StringComparer.Ordinal))
                {
                    models++;
                }
                else if (!seenOnce.Add(Canonical(arg)))
                {
                    throw Rejected($"'{arg}' is given twice");
                }

                result.Add(arg);
                result.Add(ConfineModelPath(arg, path, options.ModelsRoot));
                continue;
            }

            if (arg.Contains(LogicalVolumes.ModelsToken, StringComparison.Ordinal) ||
                arg.Contains(LogicalVolumes.CacheToken, StringComparison.Ordinal))
            {
                throw Rejected($"argument {i} uses a path token outside a model path flag");
            }

            if (LlamaCppFlags.IsFlag(arg, ApiKeyFlags))
            {
                throw Rejected("the API key is set by the agent (LLAMA_API_KEY), never on the command line");
            }

            if (LlamaCppFlags.IsFlag(arg, WebUiOnFlags))
            {
                throw Rejected($"'{FlagOf(arg)}' turns the web UI on; instances run with --no-ui");
            }

            if (LlamaCppFlags.IsFlag(arg, HostFlags))
            {
                ExpectValue(argv, ref i, arg, LlamaCppFlags.LoopbackHost, "instances bind 127.0.0.1 only");
                hosts++;
                result.Add(arg);
                result.Add(LlamaCppFlags.LoopbackHost);
                continue;
            }

            if (LlamaCppFlags.IsFlag(arg, PortFlags))
            {
                ExpectValue(argv, ref i, arg, ProcessArgvTokens.Port, "the port is the agent's ({port})");
                ports++;
                result.Add(arg);
                result.Add(ProcessArgvTokens.Port);
                continue;
            }

            if (string.Equals(arg, ProcessArgvTokens.Port, StringComparison.Ordinal))
            {
                throw Rejected("{port} may only follow --port");
            }

            if (LlamaCppFlags.IsFlag(arg, FitFlags))
            {
                Once(seenOnce, "--fit");
                ExpectValue(argv, ref i, arg, "off", "admission decides what fits (--fit off)");
                result.Add(arg);
                result.Add("off");
                continue;
            }

            if (LlamaCppFlags.IsFlag(arg, CacheRamFlags))
            {
                Once(seenOnce, "--cache-ram");
                ExpectValue(argv, ref i, arg, "0", "the host-RAM prompt cache is not budgeted (--cache-ram 0)");
                result.Add(arg);
                result.Add("0");
                continue;
            }

            if (LlamaCppFlags.IsFlag(arg, AliasFlags))
            {
                if (arg.Contains('=', StringComparison.Ordinal))
                {
                    throw Rejected("--alias must take the served name as the next argument");
                }

                alias = Next(argv, ref i, arg);
                if (alias.Length == 0 || alias.Any(char.IsControl))
                {
                    throw Rejected("the --alias value is empty or contains control characters");
                }

                aliases++;
                result.Add(arg);
                result.Add(alias);
                continue;
            }

            if (LlamaCppFlags.IsFlag(arg, DeviceFlags))
            {
                Once(seenOnce, "--device");
            }

            result.Add(arg);
        }

        if (hosts != 1 || ports != 1)
        {
            throw Rejected("argv must contain exactly one '--host 127.0.0.1' and one '--port {port}'");
        }

        if (models != 1)
        {
            throw Rejected("argv must name exactly one model file (-m {models}/...)");
        }

        if (aliases != 1 || alias is null)
        {
            throw Rejected("argv must contain exactly one --alias (the served name)");
        }

        return (result, alias);
    }

    /// <summary>
    /// <c>{models}/a/b.gguf</c> → the absolute path under the models root, after checking it is a plain relative path that
    /// exists and stays inside the root once symlinks are resolved (design §10.3 via <see cref="PathConfinement"/>).
    /// </summary>
    private static string ConfineModelPath(string flag, string value, string modelsRoot)
    {
        string prefix = LogicalVolumes.ModelsToken + "/";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw Rejected($"'{flag}' takes a path under {LogicalVolumes.ModelsToken}/");
        }

        string relative = value[prefix.Length..];
        if (PathConfinement.CheckPlainRelative(relative) is { } problem)
        {
            throw Rejected($"'{flag}' path '{value}': {problem}");
        }

        if (relative.Contains(LogicalVolumes.ModelsToken, StringComparison.Ordinal) ||
            relative.Contains(LogicalVolumes.CacheToken, StringComparison.Ordinal) ||
            relative.Contains(ProcessArgvTokens.Port, StringComparison.Ordinal))
        {
            throw Rejected($"'{flag}' path '{value}' contains another token");
        }

        string resolved = PathConfinement.ResolveInside(modelsRoot, relative)
            ?? throw Rejected($"'{flag}' path '{value}' does not resolve to an existing file inside the models folder");
        return Path.Combine(modelsRoot, resolved.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void CheckEnvironment(LaunchSpec spec, bool mlx)
    {
        if (spec.Env is null || spec.SecretEnvNames is null)
        {
            throw Rejected("env and secretEnvNames must not be null");
        }

        if (spec.Env.Count > ContainerLaunchValidator.MaxEnvVariables)
        {
            throw Rejected($"at most {ContainerLaunchValidator.MaxEnvVariables} environment variables are allowed");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in spec.Env)
        {
            if (name is null || name.Length > 256 || !EnvNameRegex().IsMatch(name))
            {
                throw Rejected($"environment variable name '{name}' must be letters, digits and '_', not starting with a digit");
            }

            if (ProcessEnvironment.IsReserved(name) ||
                ContainerLaunchValidator.ReservedEnvNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw Rejected($"environment variable '{name}' is set by the agent");
            }

            if (mlx && ProcessEnvironment.IsReservedForMlx(name))
            {
                throw Rejected($"environment variable '{name}' is set or fixed by the agent for an MLX process (Python, Hugging Face and loader settings)");
            }

            if (!seen.Add(name))
            {
                throw Rejected($"environment variable '{name}' is given twice (names are compared ignoring case)");
            }

            if (value is null || value.Contains('\0', StringComparison.Ordinal))
            {
                throw Rejected($"environment variable '{name}' has no value or contains NUL");
            }
        }

        var secrets = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in spec.SecretEnvNames)
        {
            if (name is null || !spec.Env.ContainsKey(name))
            {
                throw Rejected($"secret environment variable '{name}' has no value in env");
            }

            if (!secrets.Add(name))
            {
                throw Rejected($"secret environment variable '{name}' is named twice");
            }
        }
    }

    private static (GpuSelectionMode Mode, IReadOnlyList<string>? Uuids) CheckGpuSelection(GpuSelection? gpus)
    {
        if (gpus is null)
        {
            throw Rejected("gpus must not be null");
        }

        switch (gpus.Mode)
        {
            case GpuSelectionMode.All or GpuSelectionMode.None:
                if (gpus.Uuids is { Count: > 0 })
                {
                    throw Rejected($"GPU selection '{gpus.Mode}' takes no uuids");
                }

                return (gpus.Mode, null);
            case GpuSelectionMode.Uuids:
                break;
            default:
                throw Rejected($"GPU selection '{gpus.Mode}' is not supported");
        }

        var uuids = gpus.Uuids ?? [];
        if (uuids.Count == 0 || uuids.Count > ContainerLaunchValidator.MaxGpus)
        {
            throw Rejected($"GPU selection 'uuids' names 1 to {ContainerLaunchValidator.MaxGpus} GPUs");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string uuid in uuids)
        {
            if (uuid is null || uuid.Length > 128 || !GpuUuidRegex().IsMatch(uuid))
            {
                throw Rejected($"GPU UUID '{uuid}' is not a GPU or MIG UUID");
            }

            if (!seen.Add(uuid))
            {
                throw Rejected($"GPU {uuid} is selected twice");
            }
        }

        return (GpuSelectionMode.Uuids, [.. uuids]);
    }

    private static string Next(IReadOnlyList<string> argv, ref int i, string flag)
    {
        if (i + 1 >= argv.Count || argv[i + 1] is null)
        {
            throw Rejected($"'{flag}' has no value");
        }

        return argv[++i];
    }

    private static void ExpectValue(IReadOnlyList<string> argv, ref int i, string flag, string expected, string why)
    {
        if (!string.Equals(flag, FlagOf(flag), StringComparison.Ordinal) ||
            !string.Equals(Next(argv, ref i, flag), expected, StringComparison.Ordinal))
        {
            throw Rejected($"'{FlagOf(flag)}' must be followed by '{expected}': {why}");
        }
    }

    private static void Once(HashSet<string> seen, string canonical)
    {
        if (!seen.Add(canonical))
        {
            throw Rejected($"'{canonical}' is given twice");
        }
    }

    private static string Canonical(string flag) => flag switch
    {
        "-mm" => "--mmproj",
        _ => flag,
    };

    private static string FlagOf(string arg)
    {
        int equals = arg.IndexOf('=', StringComparison.Ordinal);
        return equals > 0 ? arg[..equals] : arg;
    }

    private static AgentException Rejected(string reason) =>
        new(422, new AgentError
        {
            Code = ErrorCodes.SpecRejected,
            Message = $"Launch spec rejected: {reason}.",
            Details = AgentJson.ToElement(new SpecRejectedDetails(reason)),
        });

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelIdRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvNameRegex();

    [GeneratedRegex("^(GPU|MIG)-[A-Za-z0-9][A-Za-z0-9/-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex GpuUuidRegex();
}
