using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>
/// The execution fingerprint (design §5.1 "two fingerprints", D26; label <see cref="ContainerLabels.ExecutionFingerprint"/>):
/// 16 lowercase hex of SHA-256 over what the legacy <see cref="ModelConfigHash"/> cannot see — runtime kind, delivery,
/// node uid, the ordered GPU UUIDs, the image digest (or the logical executable name and version), the environment
/// (names and non-secret values; a secret contributes its name only), the logical mounts — plus the legacy config hash
/// for vLLM (or, for other runtimes, the argv as given, which their builder makes canonical).
/// <para>
/// Pure and deterministic: no I/O, no clock, culture-invariant. <see cref="ModelConfigHash"/> is not touched, so every
/// container launched before P1 keeps its legacy hash; those carry no fingerprint label and are compared on the legacy
/// hash only (<see cref="RestartPendingRule"/>).
/// </para>
/// <para>
/// Ordering rules: GPU UUIDs keep their order (it is the device/rank order of the launch), environment variables are
/// sorted by name (ordinal) and mounts by target (ordinal), because their order has no effect on the launch. Image
/// reference strings are not covered when a digest is present (the reference is already inside the legacy hash); with
/// no digest the reference itself is used, so the fingerprint is still defined in P1, where digests are optional.
/// </para>
/// </summary>
public static class ExecutionFingerprint
{
    /// <summary>Version tag of the canonical input. Changing the encoding requires a new tag (and a migration note).</summary>
    public const string Version = "exec-fp/1";

    /// <summary>Length of the fingerprint in hex characters (8 bytes of SHA-256).</summary>
    public const int HexLength = 16;

    /// <summary>
    /// The fingerprint of a launch the master is about to send (or would send for the saved definition).
    /// </summary>
    /// <param name="spec">The launch spec (env, secret names, mounts, image digest, executable, argv, kind, runtime).</param>
    /// <param name="nodeUid">The node's immutable uid (not its display id).</param>
    /// <param name="gpuUuids">
    /// The ordered GPU UUIDs the instance runs on; empty for unified nodes / <see cref="GpuSelectionMode.All"/>. When the
    /// spec selects <see cref="GpuSelectionMode.Uuids"/> it must equal <see cref="GpuSelection.Uuids"/>.
    /// </param>
    /// <param name="legacyConfigHash">
    /// The legacy <see cref="ModelConfigHash"/> of the launch; required for <see cref="RuntimeKind.Vllm"/>, ignored for
    /// other runtimes (their argv is covered instead).
    /// </param>
    /// <param name="executableVersion">Version of the logical executable (process delivery); null when unknown.</param>
    public static string Compute(
        LaunchSpec spec,
        string nodeUid,
        IReadOnlyList<string> gpuUuids,
        string? legacyConfigHash,
        string? executableVersion = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(gpuUuids);
        if (spec.Gpus.Mode == GpuSelectionMode.Uuids && !(spec.Gpus.Uuids ?? []).SequenceEqual(gpuUuids, StringComparer.Ordinal))
        {
            throw new ArgumentException("The GPU UUIDs must equal the spec's GPU selection (same order).", nameof(gpuUuids));
        }

        var secrets = new HashSet<string>(spec.SecretEnvNames, StringComparer.Ordinal);
        var env = spec.Env.ToDictionary(
            pair => pair.Key,
            pair => secrets.Contains(pair.Key) ? null : pair.Value,
            StringComparer.Ordinal);

        // A secret named in SecretEnvNames but absent from Env still is part of the launch shape.
        foreach (string name in secrets)
        {
            env.TryAdd(name, null);
        }

        return Compute(new Input
        {
            Runtime = spec.Runtime,
            Delivery = spec.Kind,
            NodeUid = nodeUid,
            GpuUuids = gpuUuids,
            ImageDigest = spec.ImageDigest,
            Image = spec.Image,
            Executable = spec.Executable,
            ExecutableVersion = executableVersion,
            Env = env,
            Mounts = spec.Mounts,
            LegacyConfigHash = legacyConfigHash,
            Argv = spec.Argv,
        });
    }

    /// <summary>
    /// The fingerprint recomputed from read-back (<c>GET /runtimes/{id}/launch-config</c>): secret values arrive as
    /// null and contribute their name only, exactly like <see cref="LaunchSpec.SecretEnvNames"/> on the launch side, so
    /// a launch and its read-back produce the same fingerprint. GPU UUIDs come from <see cref="LaunchConfig.Gpus"/> in
    /// order (empty = all/unified).
    /// </summary>
    public static string Compute(
        LaunchConfig config,
        RuntimeKind runtime,
        string nodeUid,
        string? legacyConfigHash,
        string? executableVersion = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Compute(new Input
        {
            Runtime = runtime,
            Delivery = config.Kind,
            NodeUid = nodeUid,
            GpuUuids = [.. config.Gpus.Select(g => g.Uuid)],
            ImageDigest = config.ImageDigest,
            Image = config.Image,
            Executable = config.Executable,
            ExecutableVersion = executableVersion,
            Env = config.Env,
            Mounts = config.Mounts,
            LegacyConfigHash = legacyConfigHash,
            Argv = config.Argv,
        });
    }

    /// <summary>The fingerprint of a canonical input. Environment values that are null are secrets (name only).</summary>
    public static string Compute(Input input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.NodeUid);

        var text = new StringBuilder();
        Field(text, "v", Version);
        Field(text, "runtime", Name(input.Runtime));
        Field(text, "delivery", Name(input.Delivery));
        Field(text, "node", input.NodeUid);

        Field(text, "gpus", input.GpuUuids.Count.ToString(CultureInfo.InvariantCulture));
        foreach (string uuid in input.GpuUuids)
        {
            Field(text, "gpu", uuid);
        }

        if (input.Delivery == LaunchDelivery.Container)
        {
            if (!string.IsNullOrEmpty(input.ImageDigest))
            {
                Field(text, "digest", input.ImageDigest);
            }
            else
            {
                Field(text, "image", input.Image ?? string.Empty);
            }
        }
        else
        {
            Field(text, "exe", input.Executable ?? string.Empty);
            Field(text, "exe-version", input.ExecutableVersion ?? string.Empty);
        }

        var env = input.Env.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
        Field(text, "env", env.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var (name, value) in env)
        {
            Field(text, "env-name", name);
            if (value is null)
            {
                Field(text, "env-secret", string.Empty);
            }
            else
            {
                Field(text, "env-value", value);
            }
        }

        var mounts = input.Mounts
            .OrderBy(m => m.Target, StringComparer.Ordinal)
            .ThenBy(m => m.Volume, StringComparer.Ordinal)
            .ThenBy(m => m.SubPath, StringComparer.Ordinal)
            .ToList();
        Field(text, "mounts", mounts.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var mount in mounts)
        {
            Field(text, "mount-volume", mount.Volume);
            Field(text, "mount-subpath", mount.SubPath ?? string.Empty);
            Field(text, "mount-subpath-set", mount.SubPath is null ? "0" : "1");
            Field(text, "mount-target", mount.Target);
            Field(text, "mount-ro", mount.ReadOnly ? "1" : "0");
        }

        if (input.Runtime == RuntimeKind.Vllm)
        {
            if (string.IsNullOrWhiteSpace(input.LegacyConfigHash))
            {
                throw new ArgumentException("A vLLM execution fingerprint needs the legacy config hash.", nameof(input));
            }

            Field(text, "config-hash", input.LegacyConfigHash);
        }
        else
        {
            Field(text, "argv", input.Argv.Count.ToString(CultureInfo.InvariantCulture));
            foreach (string arg in input.Argv)
            {
                Field(text, "arg", arg);
            }
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexStringLower(hash.AsSpan(0, HexLength / 2));
    }

    /// <summary>Length-prefixed, tagged field: unambiguous whatever characters a value contains.</summary>
    private static void Field(StringBuilder text, string tag, string value) =>
        text.Append(tag).Append(':').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('\n');

    private static string Name(RuntimeKind kind) => kind switch
    {
        RuntimeKind.Vllm => "vllm",
        RuntimeKind.LlamaCpp => "llamacpp",
        RuntimeKind.Mlx => "mlx",
        RuntimeKind.Sglang => "sglang",
        RuntimeKind.Nim => "nim",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown runtime kind."),
    };

    private static string Name(LaunchDelivery delivery) => delivery switch
    {
        LaunchDelivery.Container => "container",
        LaunchDelivery.Process => "process",
        _ => throw new ArgumentOutOfRangeException(nameof(delivery), delivery, "Unknown delivery."),
    };

    /// <summary>The canonical input of the fingerprint (both the launch side and the read-back side map to it).</summary>
    public sealed record Input
    {
        /// <summary>Runtime kind.</summary>
        public required RuntimeKind Runtime { get; init; }

        /// <summary>Container or process.</summary>
        public required LaunchDelivery Delivery { get; init; }

        /// <summary>The node's immutable uid.</summary>
        public required string NodeUid { get; init; }

        /// <summary>Ordered GPU UUIDs (empty = all/unified).</summary>
        public IReadOnlyList<string> GpuUuids { get; init; } = [];

        /// <summary>Image digest (container); when empty, <see cref="Image"/> is used instead.</summary>
        public string? ImageDigest { get; init; }

        /// <summary>Image reference (container), used only when there is no digest.</summary>
        public string? Image { get; init; }

        /// <summary>Logical executable name (process).</summary>
        public string? Executable { get; init; }

        /// <summary>Executable version (process).</summary>
        public string? ExecutableVersion { get; init; }

        /// <summary>Environment: name → value; null value = secret (name only).</summary>
        public IReadOnlyDictionary<string, string?> Env { get; init; } = new Dictionary<string, string?>();

        /// <summary>Logical mounts.</summary>
        public IReadOnlyList<MountSpec> Mounts { get; init; } = [];

        /// <summary>Legacy config hash (required for vLLM).</summary>
        public string? LegacyConfigHash { get; init; }

        /// <summary>Argv (covered for non-vLLM runtimes only).</summary>
        public IReadOnlyList<string> Argv { get; init; } = [];
    }
}
