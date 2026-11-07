using System.Text.RegularExpressions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.NodeRuntime.Runtimes;

/// <summary>
/// A launch spec checked against the node's guardrails (design §10.3) and turned into Docker terms: the same image,
/// <c>Cmd</c>, binds, port and IPC mode the pre-P1 <c>DockerModelRuntime</c> used, plus the P2 environment (MN-107).
/// </summary>
/// <param name="ModelId">Model id (container <c>ss-{id}</c>).</param>
/// <param name="Image">Image reference as given (kept as is so the config hash, computed over image + Cmd, is stable).</param>
/// <param name="ImageDigest">Approved digest of the image, when the spec names one.</param>
/// <param name="Cmd">Container command (the spec's argv, unchanged).</param>
/// <param name="Binds">Docker bind strings, e.g. <c>/srv/mirbadllm/models:/models:ro</c>.</param>
/// <param name="ContainerPort">Runtime port inside the container.</param>
/// <param name="IpcMode"><c>host</c> or empty (Docker default).</param>
/// <param name="SpecLabels">The spec's labels (no reserved names).</param>
/// <param name="Environment">The spec's environment variables (empty in P1); its <c>ToString</c> never shows secret values.</param>
/// <param name="GpuUuids">
/// The GPUs selected by UUID, in order (<c>gpus.mode = uuids</c>, MN-106): Docker's <c>DeviceIDs</c>. Null = every GPU
/// (<c>Count = -1</c>), today's DGX launch.
/// </param>
public sealed record ContainerLaunchPlan(
    string ModelId,
    RuntimeKind Runtime,
    string HealthPath,
    string Image,
    string? ImageDigest,
    IReadOnlyList<string> Cmd,
    IReadOnlyList<string> Binds,
    int ContainerPort,
    string IpcMode,
    IReadOnlyDictionary<string, string> SpecLabels,
    ContainerEnvironment Environment,
    IReadOnlyList<string>? GpuUuids = null)
{
    /// <summary>
    /// The reference Docker creates (and pulls) the container from: <c>repository@digest</c> when the spec names a digest
    /// (design §10.3, W2 review MN-092: a tag can be re-pointed, a digest cannot), else <see cref="Image"/> as today.
    /// </summary>
    public string CreateImage => ImageDigest is null ? Image : $"{ContainerLaunchValidator.Repository(Image)}@{ImageDigest}";
}

/// <summary>
/// The environment variables of a launch (design §4.3, §10.4; MN-107): ordered name/value pairs and the names whose values
/// are secrets. Secret values go to Docker only: they are never written to labels, logs, <c>launch-config</c> or the
/// operation log, and <see cref="ToString"/> and <see cref="Redact"/> replace them with <see cref="Protocol.RedactedValue"/>.
/// </summary>
public sealed class ContainerEnvironment
{
    private readonly KeyValuePair<string, string>[] _variables;
    private readonly HashSet<string> _secrets;

    internal ContainerEnvironment(IEnumerable<KeyValuePair<string, string>> variables, IEnumerable<string> secretNames)
    {
        _variables = [.. variables];
        _secrets = new HashSet<string>(secretNames, StringComparer.Ordinal);
        Names = [.. _variables.Select(v => v.Key)];
        SecretNames = [.. Names.Where(_secrets.Contains)];
    }

    /// <summary>No variables (every P1 launch).</summary>
    public static ContainerEnvironment Empty { get; } = new([], []);

    /// <summary>True when the launch sets no variable.</summary>
    public bool IsEmpty => _variables.Length == 0;

    /// <summary>Variable names in spec order.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>Names (a subset of <see cref="Names"/>, same order) whose values are secrets.</summary>
    public IReadOnlyList<string> SecretNames { get; }

    /// <summary>Docker's <c>Env</c> list (<c>NAME=value</c>), secret values included: for the create call only.</summary>
    public IReadOnlyList<string> ToDockerEnv() => [.. _variables.Select(v => v.Key + "=" + v.Value)];

    /// <summary>
    /// <paramref name="text"/> with every non-empty secret value replaced by <see cref="Protocol.RedactedValue"/>, for
    /// messages that may echo the create request (Docker errors).
    /// </summary>
    public string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var (name, value) in _variables)
        {
            if (_secrets.Contains(name) && value.Length > 0)
            {
                text = text.Replace(value, Protocol.RedactedValue, StringComparison.Ordinal);
            }
        }

        return text;
    }

    /// <summary>Names and non-secret values; secret values are shown as <see cref="Protocol.RedactedValue"/>.</summary>
    public override string ToString() =>
        "{" + string.Join(", ", _variables.Select(v => v.Key + "=" + (_secrets.Contains(v.Key) ? Protocol.RedactedValue : v.Value))) + "}";
}

/// <summary>
/// Checks a container <see cref="LaunchSpec"/> before anything is created (design §4.3 422 <c>spec_rejected</c>, §10.3).
/// The agent's launch handler can call it before answering 202, so a bad spec never becomes an operation; the driver
/// calls it again before it touches Docker.
/// </summary>
public static partial class ContainerLaunchValidator
{
    /// <summary>
    /// Label recording the approved digest of the image an instance was created from (agent-set, only when the spec
    /// names one). Read back by <c>launch-config</c>.
    /// </summary>
    public const string ImageDigestLabel = "mirbadllm.image-digest";

    /// <summary>
    /// Label recording the spec's image reference (normally a tag) when the container is created from
    /// <c>repository@digest</c>, so <c>launch-config</c> reports the image the master asked for and the master's
    /// <c>ModelConfigHash</c> (image + Cmd) recomputes unchanged (W2 review MN-092; MN-107).
    /// </summary>
    public const string ImageLabel = "mirbadllm.image";

    /// <summary>
    /// Label listing the names of the spec's environment variables, comma-separated in spec order (names only, never
    /// values), so <c>launch-config</c> reports the launch environment and not the image's own (MN-107).
    /// </summary>
    public const string EnvNamesLabel = "mirbadllm.env-names";

    /// <summary>Label listing the names of the secret environment variables, comma-separated (names only; MN-107).</summary>
    public const string SecretEnvNamesLabel = "mirbadllm.secret-env-names";

    /// <summary>
    /// Environment variables the agent keeps for itself because they select GPUs (GPU selection is the agent's, design
    /// §5.1, §10.3): a spec setting them could widen what the container's device request grants.
    /// </summary>
    public static IReadOnlyList<string> ReservedEnvNames { get; } = ["NVIDIA_VISIBLE_DEVICES", "CUDA_VISIBLE_DEVICES"];

    /// <summary>Most environment variables one launch may set.</summary>
    public const int MaxEnvVariables = 128;

    /// <summary>Most GPUs one launch may select by UUID.</summary>
    public const int MaxGpus = 64;

    /// <summary>
    /// Labels only the driver writes: the contract's reserved names plus the port, the image digest, the image, the
    /// environment names and the selected GPUs (<see cref="ContainerLabels.GpuUuids"/>, from the GPU selection itself, so
    /// the read-back always names the GPUs Docker was given).
    /// </summary>
    public static IReadOnlyList<string> DriverLabels { get; } =
        [.. ContainerLabels.Reserved, ContainerLabels.Port, ContainerLabels.Runtime, ContainerLabels.HealthPath,
            ImageDigestLabel, ImageLabel, EnvNamesLabel, SecretEnvNamesLabel, ContainerLabels.GpuUuids];

    /// <summary>
    /// Validates <paramref name="spec"/> for <paramref name="modelId"/>. Throws <see cref="AgentException"/> 422
    /// <c>spec_rejected</c> (details <see cref="SpecRejectedDetails"/>) on a guardrail breach, 400
    /// <c>invalid_request</c> for an unusable model id.
    /// </summary>
    public static ContainerLaunchPlan Validate(string modelId, LaunchSpec spec, ContainerRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrEmpty(modelId) || modelId.Length > 200 || !ContainerNameRegex().IsMatch(modelId))
        {
            throw new AgentException(400, new AgentError
            {
                Code = ErrorCodes.InvalidRequest,
                Message = $"Model id '{modelId}' cannot name a container (letters, digits, '_', '.', '-').",
            });
        }

        if (spec.Kind != LaunchDelivery.Container)
        {
            throw Rejected($"this driver runs containers; the spec asks for '{spec.Kind}'");
        }

        if (string.IsNullOrWhiteSpace(spec.Image))
        {
            throw Rejected("the spec names no image");
        }

        if (spec.Image.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw Rejected("the image reference contains whitespace");
        }

        CheckImageDigest(spec.Image, spec.ImageDigest, options);
        if (spec.Runtime == RuntimeKind.Nim)
        {
            if (spec.ImageDigest is null)
            {
                throw Rejected("NIM requires an approved image digest");
            }

            string repository = Repository(spec.Image);
            bool approved = options.ApprovedImages.Any(entry =>
            {
                int separator = entry.IndexOf('@', StringComparison.Ordinal);
                return separator > 0 && string.Equals(entry[..separator], repository, StringComparison.Ordinal) &&
                    string.Equals(entry[(separator + 1)..], spec.ImageDigest, StringComparison.Ordinal);
            });
            if (!approved)
            {
                throw Rejected($"NIM image '{spec.Image}' with digest '{spec.ImageDigest}' is not approved on this node");
            }
        }

        if (spec.Executable is not null)
        {
            throw Rejected("a container spec carries no executable");
        }

        if (spec.Argv.Any(a => a is null))
        {
            throw Rejected("argv contains a null argument");
        }

        var environment = CheckEnvironment(spec);

        if (spec.ContainerPort != options.ContainerPort)
        {
            throw Rejected($"the container port is fixed to {options.ContainerPort}");
        }

        var gpuUuids = CheckGpuSelection(spec.Gpus);

        string approvedHealthPath = spec.Runtime switch
        {
            RuntimeKind.Vllm or RuntimeKind.LlamaCpp or RuntimeKind.Sglang => "/health",
            RuntimeKind.Nim => "/v1/health/ready",
            _ => throw Rejected($"runtime '{spec.Runtime}' is not approved for container delivery"),
        };
        if (!string.Equals(spec.HealthPath, approvedHealthPath, StringComparison.Ordinal))
        {
            throw Rejected($"the health path for {spec.Runtime} is fixed to {approvedHealthPath}");
        }

        foreach (string key in spec.Labels.Keys)
        {
            if (DriverLabels.Contains(key, StringComparer.Ordinal) ||
                key.StartsWith(ContainerLabels.Download, StringComparison.Ordinal))
            {
                throw Rejected($"label '{key}' is set by the agent and cannot be in a spec");
            }
        }

        return new ContainerLaunchPlan(
            modelId,
            spec.Runtime,
            approvedHealthPath,
            spec.Image,
            spec.ImageDigest,
            [.. spec.Argv],
            BuildBinds(spec.Mounts, options),
            spec.ContainerPort,
            spec.IpcHost ? "host" : "",
            new Dictionary<string, string>(spec.Labels, StringComparer.Ordinal),
            environment,
            gpuUuids);
    }

    /// <summary>
    /// The GPU selection (design §5.1, MN-106 lifts D47's refusal): <c>all</c> (every GPU, today's DGX launch) or
    /// <c>uuids</c> with 1 to <see cref="MaxGpus"/> distinct GPU UUIDs (<c>GPU-…</c> or <c>MIG-…</c>), kept in order. Whether
    /// the UUIDs are on this node is checked against the GPU inventory (<c>gpu_missing</c>), not here. Null for <c>all</c>.
    /// </summary>
    private static IReadOnlyList<string>? CheckGpuSelection(GpuSelection? gpus)
    {
        if (gpus is null)
        {
            throw Rejected("gpus must not be null");
        }

        switch (gpus.Mode)
        {
            case GpuSelectionMode.All:
                if (gpus.Uuids is { Count: > 0 })
                {
                    throw Rejected("GPU selection 'all' takes no uuids");
                }

                return null;
            case GpuSelectionMode.Uuids:
                break;
            default:
                throw Rejected($"GPU selection '{gpus.Mode}' is not supported by this agent (all GPUs or GPUs by UUID)");
        }

        var uuids = gpus.Uuids ?? [];
        if (uuids.Count == 0)
        {
            throw Rejected("GPU selection 'uuids' names no GPU");
        }

        if (uuids.Count > MaxGpus)
        {
            throw Rejected($"at most {MaxGpus} GPUs can be selected");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string uuid in uuids)
        {
            if (uuid is null || uuid.Length > 128 || !GpuUuidRegex().IsMatch(uuid))
            {
                throw Rejected($"GPU UUID '{uuid}' is not a GPU or MIG UUID (e.g. GPU-xxxxxxxx-xxxx-…)");
            }

            if (!seen.Add(uuid))
            {
                throw Rejected($"GPU {uuid} is selected twice");
            }
        }

        return [.. uuids];
    }

    /// <summary>The image repository: the reference without its tag or digest (<c>host:5000/a/b:tag</c> → <c>host:5000/a/b</c>).</summary>
    public static string Repository(string image)
    {
        ArgumentNullException.ThrowIfNull(image);
        int at = image.IndexOf('@', StringComparison.Ordinal);
        string withoutDigest = at >= 0 ? image[..at] : image;
        int lastSlash = withoutDigest.LastIndexOf('/');
        int colon = withoutDigest.LastIndexOf(':');
        return colon > lastSlash ? withoutDigest[..colon] : withoutDigest;
    }

    /// <summary>
    /// The launch environment (design §4.3, §10.4; MN-107 lifts D47's refusal): names of letters, digits and <c>_</c>, no
    /// NUL in values, every secret name present in <c>env</c> exactly once, the GPU-selection variables refused.
    /// Refusals name the variable, never its value.
    /// </summary>
    private static ContainerEnvironment CheckEnvironment(LaunchSpec spec)
    {
        if (spec.Env is null || spec.SecretEnvNames is null)
        {
            throw Rejected("env and secretEnvNames must not be null");
        }

        if (spec.Env.Count == 0 && spec.SecretEnvNames.Count == 0)
        {
            return ContainerEnvironment.Empty;
        }

        if (spec.Env.Count > MaxEnvVariables)
        {
            throw Rejected($"at most {MaxEnvVariables} environment variables are allowed");
        }

        foreach (var (name, value) in spec.Env)
        {
            if (name is null || name.Length > 256 || !EnvNameRegex().IsMatch(name))
            {
                throw Rejected($"environment variable name '{name}' must be letters, digits and '_', not starting with a digit");
            }

            if (ReservedEnvNames.Contains(name, StringComparer.Ordinal))
            {
                throw Rejected($"environment variable '{name}' selects GPUs, which the agent does itself");
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

        return new ContainerEnvironment(spec.Env, secrets);
    }

    private static void CheckImageDigest(string image, string? digest, ContainerRuntimeOptions options)
    {
        int at = image.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0 && digest is not null && !string.Equals(image[(at + 1)..], digest, StringComparison.Ordinal))
        {
            throw Rejected($"image '{image}' pins another digest than the spec's '{digest}'");
        }

        if (digest is null)
        {
            if (options.RequireApprovedDigest)
            {
                throw Rejected("the spec must name an approved image digest");
            }

            return;
        }

        if (!DigestRegex().IsMatch(digest))
        {
            throw Rejected("the image digest must be 'sha256:' and 64 lowercase hex digits");
        }

        if (options.ApprovedImages.Count == 0)
        {
            if (options.RequireApprovedDigest)
            {
                throw Rejected($"image '{image}' with digest '{digest}' is not approved on this node");
            }

            return;
        }

        string repository = Repository(image);
        bool approved = options.ApprovedImages.Any(entry =>
        {
            int at = entry.IndexOf('@', StringComparison.Ordinal);
            return at > 0 &&
                string.Equals(entry[..at], repository, StringComparison.Ordinal) &&
                string.Equals(entry[(at + 1)..], digest, StringComparison.Ordinal);
        });
        if (!approved)
        {
            throw Rejected($"image '{image}' with digest '{digest}' is not approved on this node");
        }
    }

    private static List<string> BuildBinds(IReadOnlyList<MountSpec> mounts, ContainerRuntimeOptions options)
    {
        var binds = new List<string>(mounts.Count);
        var volumes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mount in mounts)
        {
            if (mount is null)
            {
                throw Rejected("a mount is null");
            }

            (string hostRoot, string localRoot, string target) = mount.Volume switch
            {
                LogicalVolumes.Models => (options.ModelsHostPath, options.ModelsLocalRoot, LogicalVolumes.ModelsTarget),
                LogicalVolumes.Cache => (options.CacheHostPath, options.CacheLocalRoot, LogicalVolumes.CacheTarget),
                _ => throw Rejected($"volume '{mount.Volume}' is not one of '{LogicalVolumes.Models}', '{LogicalVolumes.Cache}'"),
            };

            if (!volumes.Add(mount.Volume))
            {
                throw Rejected($"volume '{mount.Volume}' is mounted twice");
            }

            if (!string.Equals(mount.Target, target, StringComparison.Ordinal))
            {
                throw Rejected($"volume '{mount.Volume}' mounts only at '{target}'");
            }

            if (mount.Volume == LogicalVolumes.Models && !mount.ReadOnly)
            {
                throw Rejected($"runtimes mount '{LogicalVolumes.Models}' read-only");
            }

            string source = hostRoot;
            if (!string.IsNullOrEmpty(mount.SubPath))
            {
                if (PathConfinement.CheckPlainRelative(mount.SubPath) is { } problem)
                {
                    throw Rejected($"subPath '{mount.SubPath}' of volume '{mount.Volume}': {problem}");
                }

                string resolved = PathConfinement.ResolveInside(localRoot, mount.SubPath)
                    ?? throw Rejected($"subPath '{mount.SubPath}' does not resolve to an existing path inside volume '{mount.Volume}'");
                source = resolved == "." ? hostRoot : $"{hostRoot.TrimEnd('/')}/{resolved}";
            }

            // Same strings as the pre-P1 DockerModelRuntime: "{root}:/models:ro" and "{root}:/root/.cache".
            binds.Add(mount.ReadOnly ? $"{source}:{target}:ro" : $"{source}:{target}");
        }

        return binds;
    }

    private static AgentException Rejected(string reason) =>
        new(422, new AgentError
        {
            Code = ErrorCodes.SpecRejected,
            Message = $"Launch spec rejected: {reason}.",
            Details = AgentJson.ToElement(new SpecRejectedDetails(reason)),
        });

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerNameRegex();

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvNameRegex();

    [GeneratedRegex("^(GPU|MIG)-[A-Za-z0-9][A-Za-z0-9/-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex GpuUuidRegex();
}
