using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Multinode;

#pragma warning disable CA1707 // underscore test names.

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-029 (design §5.1 "two fingerprints", §6.3): the execution fingerprint changes with every covered field and not
/// with secret values or irrelevant ordering, and <see cref="RestartPendingRule"/> combines it with the legacy hash.
/// </summary>
public sealed class ExecutionFingerprintTests
{
    private const string LegacyHash = "0123456789abcdef";
    private const string NodeUid = "node-uid-1";

    private static readonly ExecutionFingerprint.Input Base = new()
    {
        Runtime = RuntimeKind.Vllm,
        Delivery = LaunchDelivery.Container,
        NodeUid = NodeUid,
        GpuUuids = ["GPU-1111", "GPU-2222"],
        ImageDigest = "sha256:aaaa",
        Image = "local/vllm:1",
        Env = new Dictionary<string, string?>(StringComparer.Ordinal) { ["VLLM_LOGGING_LEVEL"] = "INFO", ["HF_TOKEN"] = null },
        Mounts =
        [
            new MountSpec(LogicalVolumes.Models, null, LogicalVolumes.ModelsTarget, ReadOnly: true),
            new MountSpec(LogicalVolumes.Cache, null, LogicalVolumes.CacheTarget, ReadOnly: false),
        ],
        LegacyConfigHash = LegacyHash,
        Argv = ["--model", "/models/a"],
    };

    private static readonly LaunchSpec Spec = new()
    {
        Kind = LaunchDelivery.Container,
        Runtime = RuntimeKind.Vllm,
        Image = "local/vllm:1",
        ImageDigest = "sha256:aaaa",
        Argv = ["--model", "/models/a"],
        Env = new Dictionary<string, string>(StringComparer.Ordinal) { ["VLLM_LOGGING_LEVEL"] = "INFO", ["HF_TOKEN"] = "hf_secret_1" },
        SecretEnvNames = ["HF_TOKEN"],
        Mounts = Base.Mounts,
        Gpus = new GpuSelection { Mode = GpuSelectionMode.Uuids, Uuids = ["GPU-1111", "GPU-2222"] },
        IpcHost = true,
    };

    private static Dictionary<string, string?> Env(params (string Name, string? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);

    /// <summary>One mutation per covered field; each must change the fingerprint.</summary>
    private static readonly Dictionary<string, Func<ExecutionFingerprint.Input, ExecutionFingerprint.Input>> Mutations = new(StringComparer.Ordinal)
    {
        ["runtime"] = i => i with { Runtime = RuntimeKind.LlamaCpp },
        ["delivery"] = i => i with { Delivery = LaunchDelivery.Process, Executable = null },
        ["node uid"] = i => i with { NodeUid = "node-uid-2" },
        ["gpu uuid replaced"] = i => i with { GpuUuids = ["GPU-1111", "GPU-3333"] },
        ["gpu order"] = i => i with { GpuUuids = ["GPU-2222", "GPU-1111"] },
        ["gpu added"] = i => i with { GpuUuids = ["GPU-1111", "GPU-2222", "GPU-3333"] },
        ["gpu removed"] = i => i with { GpuUuids = ["GPU-1111"] },
        ["gpus to all"] = i => i with { GpuUuids = [] },
        ["image digest"] = i => i with { ImageDigest = "sha256:bbbb" },
        ["digest removed"] = i => i with { ImageDigest = null },
        ["env value"] = i => i with { Env = Env(("VLLM_LOGGING_LEVEL", "DEBUG"), ("HF_TOKEN", null)) },
        ["env added"] = i => i with { Env = Env(("VLLM_LOGGING_LEVEL", "INFO"), ("HF_TOKEN", null), ("NCCL_DEBUG", "WARN")) },
        ["env removed"] = i => i with { Env = Env(("VLLM_LOGGING_LEVEL", "INFO")) },
        ["env renamed"] = i => i with { Env = Env(("VLLM_LOG_LEVEL", "INFO"), ("HF_TOKEN", null)) },
        ["secret became plain"] = i => i with { Env = Env(("VLLM_LOGGING_LEVEL", "INFO"), ("HF_TOKEN", "")) },
        ["mount volume"] = i => i with { Mounts = [new MountSpec(LogicalVolumes.Cache, null, LogicalVolumes.ModelsTarget, true), i.Mounts[1]] },
        ["mount subpath"] = i => i with { Mounts = [new MountSpec(LogicalVolumes.Models, "qwen", LogicalVolumes.ModelsTarget, true), i.Mounts[1]] },
        ["mount subpath empty"] = i => i with { Mounts = [new MountSpec(LogicalVolumes.Models, "", LogicalVolumes.ModelsTarget, true), i.Mounts[1]] },
        ["mount target"] = i => i with { Mounts = [new MountSpec(LogicalVolumes.Models, null, "/models2", true), i.Mounts[1]] },
        ["mount read-only"] = i => i with { Mounts = [new MountSpec(LogicalVolumes.Models, null, LogicalVolumes.ModelsTarget, false), i.Mounts[1]] },
        ["mount removed"] = i => i with { Mounts = [i.Mounts[0]] },
        ["legacy config hash"] = i => i with { LegacyConfigHash = "fedcba9876543210" },
    };

    public static TheoryData<string> MutationNames => [.. Mutations.Keys];

    [Fact]
    public void Fingerprint_Is16LowercaseHex_AndDeterministic()
    {
        string a = ExecutionFingerprint.Compute(Base);
        Assert.Matches("^[0-9a-f]{16}$", a);
        Assert.Equal(ExecutionFingerprint.HexLength, a.Length);
        Assert.Equal(a, ExecutionFingerprint.Compute(Base with { }));
        Assert.Equal(a, ExecutionFingerprint.Compute(Spec, NodeUid, ["GPU-1111", "GPU-2222"], LegacyHash));
    }

    [Theory]
    [MemberData(nameof(MutationNames))]
    public void Fingerprint_ChangesWithEachCoveredField(string mutation)
    {
        Assert.NotEqual(ExecutionFingerprint.Compute(Base), ExecutionFingerprint.Compute(Mutations[mutation](Base)));
    }

    [Fact]
    public void Fingerprint_AllMutations_AreDistinct()
    {
        var all = Mutations.Values.Select(m => ExecutionFingerprint.Compute(m(Base))).Append(ExecutionFingerprint.Compute(Base)).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Fingerprint_IgnoresSecretValues()
    {
        string a = ExecutionFingerprint.Compute(Spec, NodeUid, ["GPU-1111", "GPU-2222"], LegacyHash);
        var other = Spec with
        {
            Env = new Dictionary<string, string>(StringComparer.Ordinal) { ["VLLM_LOGGING_LEVEL"] = "INFO", ["HF_TOKEN"] = "hf_other_secret" },
        };
        Assert.Equal(a, ExecutionFingerprint.Compute(other, NodeUid, ["GPU-1111", "GPU-2222"], LegacyHash));

        // A secret named but without a value in the spec is the same launch shape.
        var unset = Spec with { Env = new Dictionary<string, string>(StringComparer.Ordinal) { ["VLLM_LOGGING_LEVEL"] = "INFO" } };
        Assert.Equal(a, ExecutionFingerprint.Compute(unset, NodeUid, ["GPU-1111", "GPU-2222"], LegacyHash));

        // But a non-secret value does count.
        var plain = Spec with { SecretEnvNames = [] };
        Assert.NotEqual(a, ExecutionFingerprint.Compute(plain, NodeUid, ["GPU-1111", "GPU-2222"], LegacyHash));
    }

    [Fact]
    public void Fingerprint_IgnoresEnvAndMountOrder()
    {
        var reordered = Base with
        {
            Env = Env(("HF_TOKEN", null), ("VLLM_LOGGING_LEVEL", "INFO")),
            Mounts = [Base.Mounts[1], Base.Mounts[0]],
        };
        Assert.Equal(ExecutionFingerprint.Compute(Base), ExecutionFingerprint.Compute(reordered));
    }

    [Fact]
    public void Fingerprint_ForVllm_CoversTheLegacyHash_NotTheArgv()
    {
        // The argv is inside the legacy hash already; a spelling difference the canonical hash absorbs must not flag.
        Assert.Equal(ExecutionFingerprint.Compute(Base), ExecutionFingerprint.Compute(Base with { Argv = ["--model=/models/a"] }));
    }

    [Fact]
    public void Fingerprint_ForOtherRuntimes_CoversTheArgv_NotTheLegacyHash()
    {
        var llama = Base with { Runtime = RuntimeKind.LlamaCpp, Delivery = LaunchDelivery.Process, Executable = "llama-server", ExecutableVersion = "b4000" };
        string a = ExecutionFingerprint.Compute(llama);
        Assert.Equal(a, ExecutionFingerprint.Compute(llama with { LegacyConfigHash = null }));
        Assert.NotEqual(a, ExecutionFingerprint.Compute(llama with { Argv = ["--model", "/models/b"] }));
        Assert.NotEqual(a, ExecutionFingerprint.Compute(llama with { Executable = "llama-server-cuda" }));
        Assert.NotEqual(a, ExecutionFingerprint.Compute(llama with { ExecutableVersion = "b4001" }));
    }

    [Fact]
    public void Fingerprint_ImageReference_CountsOnlyWithoutDigest()
    {
        Assert.Equal(ExecutionFingerprint.Compute(Base), ExecutionFingerprint.Compute(Base with { Image = "local/vllm:retagged" }));
        var noDigest = Base with { ImageDigest = null };
        Assert.NotEqual(ExecutionFingerprint.Compute(noDigest), ExecutionFingerprint.Compute(noDigest with { Image = "local/vllm:2" }));
    }

    [Fact]
    public void Fingerprint_FromLaunchConfigReadBack_EqualsTheLaunchSide()
    {
        var readBack = new LaunchConfig
        {
            Kind = LaunchDelivery.Container,
            Image = Spec.Image,
            ImageDigest = Spec.ImageDigest,
            Argv = Spec.Argv,
            Env = new Dictionary<string, string?>(StringComparer.Ordinal) { ["VLLM_LOGGING_LEVEL"] = "INFO", ["HF_TOKEN"] = null },
            Mounts = Spec.Mounts,
            Gpus = [new GpuRef(0, "GPU-1111"), new GpuRef(1, "GPU-2222")],
        };

        Assert.Equal(
            ExecutionFingerprint.Compute(Spec, NodeUid, ["GPU-1111", "GPU-2222"], LegacyHash),
            ExecutionFingerprint.Compute(readBack, RuntimeKind.Vllm, NodeUid, LegacyHash));

        // GPU indices are display only: a re-numbering after a driver update keeps the fingerprint.
        var renumbered = readBack with { Gpus = [new GpuRef(3, "GPU-1111"), new GpuRef(2, "GPU-2222")] };
        Assert.Equal(
            ExecutionFingerprint.Compute(readBack, RuntimeKind.Vllm, NodeUid, LegacyHash),
            ExecutionFingerprint.Compute(renumbered, RuntimeKind.Vllm, NodeUid, LegacyHash));
    }

    [Fact]
    public void Compute_RejectsInconsistentOrMissingInputs()
    {
        Assert.Throws<ArgumentException>(() => ExecutionFingerprint.Compute(Spec, NodeUid, ["GPU-2222", "GPU-1111"], LegacyHash));
        Assert.Throws<ArgumentException>(() => ExecutionFingerprint.Compute(Base with { LegacyConfigHash = null }));
        Assert.Throws<ArgumentException>(() => ExecutionFingerprint.Compute(Base with { NodeUid = " " }));
    }

    // ------------------------------------------------------------ restart-pending rule

    [Fact]
    public void Rule_NoFingerprintLabel_ComparesTheLegacyHashOnly()
    {
        string expectedFp = ExecutionFingerprint.Compute(Base);
        Assert.False(RestartPendingRule.IsPending(new HashPair(LegacyHash, LegacyHash), new HashPair(expectedFp, null)));
        Assert.Equal(
            RestartPendingReasons.LegacyConfigHash,
            RestartPendingRule.Evaluate(new HashPair(LegacyHash, "ffffffffffffffff"), new HashPair(expectedFp, null)));
    }

    [Fact]
    public void Rule_GpuUuidChange_FlagsRestart_WithTheLegacyHashUnchanged()
    {
        string running = ExecutionFingerprint.Compute(Base);
        string saved = ExecutionFingerprint.Compute(Base with { GpuUuids = ["GPU-1111", "GPU-3333"] });

        Assert.Equal(RestartPendingReasons.ExecutionFingerprint, RestartPendingRule.Evaluate(new HashPair(LegacyHash, LegacyHash), new HashPair(saved, running)));
        Assert.True(RestartPendingRule.IsPending(ModelState.Ready, tuning: false, new HashPair(LegacyHash, LegacyHash), new HashPair(saved, running)));
    }

    [Fact]
    public void Rule_RuntimeOrDeliveryChange_FlagsRestart()
    {
        string running = ExecutionFingerprint.Compute(Base);
        string llama = ExecutionFingerprint.Compute(Base with { Runtime = RuntimeKind.LlamaCpp });
        string process = ExecutionFingerprint.Compute(Base with { Delivery = LaunchDelivery.Process, Executable = "vllm" });

        Assert.True(RestartPendingRule.IsPending(ModelState.Ready, tuning: false, new HashPair(LegacyHash, LegacyHash), new HashPair(llama, running)));
        Assert.True(RestartPendingRule.IsPending(ModelState.Starting, tuning: false, new HashPair(LegacyHash, LegacyHash), new HashPair(process, running)));
    }

    [Fact]
    public void Rule_BothDiffer_ReportsBothReasons()
    {
        Assert.Equal(
            RestartPendingReasons.LegacyConfigHash | RestartPendingReasons.ExecutionFingerprint,
            RestartPendingRule.Evaluate(new HashPair("a", "b"), new HashPair("c", "d")));
    }

    [Fact]
    public void Rule_UnknownSides_NeverFlag()
    {
        Assert.False(RestartPendingRule.IsPending(new HashPair(LegacyHash, null), new HashPair("x", null)));
        Assert.False(RestartPendingRule.IsPending(new HashPair(null, LegacyHash), new HashPair(null, "x")));

        // Legacy label missing but fingerprints present and equal: not pending.
        Assert.False(RestartPendingRule.IsPending(new HashPair(LegacyHash, null), new HashPair("x", "x")));

        // Legacy label missing, fingerprints differ: the fingerprint alone flags it.
        Assert.True(RestartPendingRule.IsPending(new HashPair(LegacyHash, null), new HashPair("x", "y")));
    }

    [Theory]
    [InlineData(ModelState.Stopped)]
    [InlineData(ModelState.Stopping)]
    [InlineData(ModelState.Failed)]
    public void Rule_OnlyStartingOrReady_CanBePending(ModelState state)
    {
        Assert.False(RestartPendingRule.IsPending(state, tuning: false, new HashPair("a", "b"), new HashPair("c", "d")));
    }

    [Fact]
    public void Rule_TuningCandidates_AndUnknownDefinitions_AreNeverPending()
    {
        Assert.False(RestartPendingRule.IsPending(ModelState.Ready, tuning: true, new HashPair("a", "b"), new HashPair("c", "d")));
        Assert.False(RestartPendingRule.IsPending(ModelState.Ready, tuning: false, new HashPair(null, "b"), new HashPair("c", "d")));
        Assert.True(RestartPendingRule.IsPending(ModelState.Ready, tuning: false, new HashPair("a", "b"), new HashPair(null, null)));
    }
}
