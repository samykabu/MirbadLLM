using System.Text.Json;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Multinode.Fakes;
using MirbadLLM.UnitTests.Orchestration;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707 // underscore test names, like the originals they mirror.

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-028: <see cref="RemoteModelRuntime"/> against <see cref="FakeAgent"/> over the real agent client (mTLS + HTTP/2).
/// The first sections are parameterized variants of today's runtime-facing tests (<c>RestartPendingTests</c>,
/// <c>DockerModelRuntimeTests</c>, <c>CanonicalConfigTests</c>): the same scenarios and expectations, with exception
/// texts equal to today's plus " on node dgx". Instances are either launched through the runtime ("remote") or adopted
/// as a container from before P1 ("adopted": legacy labels, no execution fingerprint).
/// </summary>
public sealed class RemoteModelRuntimeTests
{
    private const double TotalGiB = 120;
    private const string Remote = "remote";
    private const string Adopted = "adopted";

    private static readonly ModelDefinition Saved = new()
    {
        Id = "m1",
        ServedName = "M1",
        ModelPath = "ckpt",
        MemoryBudgetGiB = 40,
        Speculative = new SpeculativeDecoding("mtp", 2),
        CudaGraphMode = "PIECEWISE",
        Placement = new ModelPlacement(RemoteRuntimeEnv.NodeId),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A start returns once the container exists (today: Starting). The fake loads in milliseconds, so under load the
    /// instance can already be Ready when the runtime reads it back.
    /// </summary>
    private static void AssertCreated(ModelStatus status)
    {
        Assert.True(status.State is ModelState.Starting or ModelState.Ready, $"State {status.State}");
        Assert.NotNull(status.ContainerId);
    }

    private static string SavedHash => ModelConfigHash.Compute(Saved, RemoteRuntimeEnv.DefaultImage, TotalGiB);

    /// <summary>Puts <see cref="Saved"/> on the node, Ready: launched by the runtime, or adopted from before P1.</summary>
    private static async Task RunSavedAsync(RemoteRuntimeEnv env, string how, string? hashLabel = "")
    {
        if (how == Remote)
        {
            await env.Runtime.StartAsync(Saved.Id, ct: Ct);
            await env.WaitForStateAsync(Saved.Id, ModelState.Ready);
            return;
        }

        var labels = new Dictionary<string, string> { [ContainerLabels.ServedName] = Saved.ServedName, [ContainerLabels.BudgetGiB] = "40" };
        if (hashLabel is not null)
        {
            labels[ContainerLabels.ConfigHash] = hashLabel.Length == 0 ? SavedHash : hashLabel;
        }

        await env.AdoptAsync(Saved.Id, RemoteRuntimeEnv.DefaultImage, VllmArgsBuilder.Build(Saved, TotalGiB), labels, budgetGiB: 40);
    }

    // ------------------------------------------------------------ RestartPendingTests, parameterized

    [Theory]
    [InlineData(Remote)]
    [InlineData(Adopted)]
    public async Task Pending_False_WhenTheContainerRunsTheSavedConfiguration(string how)
    {
        await using var env = await RemoteRuntimeEnv.StartAsync(new FakeModelRegistry().Add(Saved));
        await RunSavedAsync(env, how);

        var status = await env.Runtime.GetStatusAsync("m1", Ct);

        Assert.Equal(ModelState.Ready, status.State);
        Assert.False(status.RestartPending);
        Assert.False(Assert.Single(await env.Runtime.ListStatusAsync(Ct)).RestartPending);
    }

    [Theory]
    [InlineData(Remote)]
    [InlineData(Adopted)]
    public async Task Pending_True_WhenTheSavedDefinitionDiffersFromTheLaunchedOne(string how)
    {
        var registry = new FakeModelRegistry().Add(Saved);
        await using var env = await RemoteRuntimeEnv.StartAsync(registry);
        await RunSavedAsync(env, how);
        registry.Add(Saved with { MaxNumSeqs = 32 });

        Assert.True((await env.Runtime.GetStatusAsync("m1", Ct)).RestartPending);
        Assert.True(Assert.Single(await env.Runtime.ListStatusAsync(Ct)).RestartPending);
    }

    [Theory]
    [InlineData(Remote)]
    [InlineData(Adopted)]
    public async Task Pending_False_WhenOnlyAutoStartChanged(string how)
    {
        var registry = new FakeModelRegistry().Add(Saved);
        await using var env = await RemoteRuntimeEnv.StartAsync(registry);
        await RunSavedAsync(env, how);
        registry.Add(Saved with { AutoStart = true });

        Assert.False((await env.Runtime.GetStatusAsync("m1", Ct)).RestartPending);
    }

    [Fact]
    public async Task Pending_Unknown_ForContainersWithoutTheLabel()
    {
        var registry = new FakeModelRegistry().Add(Saved);
        await using var env = await RemoteRuntimeEnv.StartAsync(registry);
        await RunSavedAsync(env, Adopted, hashLabel: null);
        registry.Add(Saved with { MaxNumSeqs = 32 });

        Assert.False((await env.Runtime.GetStatusAsync("m1", Ct)).RestartPending);
    }

    [Theory]
    [InlineData(Remote)]
    [InlineData(Adopted)]
    public async Task Pending_NotReported_ForATuningCandidate(string how)
    {
        var registry = new FakeModelRegistry().Add(Saved);
        await using var env = await RemoteRuntimeEnv.StartAsync(registry);
        if (how == Remote)
        {
            await env.Runtime.LaunchAsync(Saved with { MaxNumSeqs = 8 }, new LaunchOptions(Tuning: true), Ct);
        }
        else
        {
            await env.AdoptAsync("m1", RemoteRuntimeEnv.DefaultImage, VllmArgsBuilder.Build(Saved, TotalGiB), new Dictionary<string, string>
            {
                [ContainerLabels.ConfigHash] = "0123456789abcdef",
                [ContainerLabels.Tuning] = "1",
            }, budgetGiB: 40);
        }

        await env.WaitForStateAsync("m1", ModelState.Ready);

        Assert.False((await env.Runtime.GetStatusAsync("m1", Ct)).RestartPending);
        Assert.Equal(["m1"], await env.Runtime.ListTuningContainersAsync(Ct));
    }

    [Fact]
    public async Task LaunchedDefinition_IsReadBackFromTheContainerCommandLine()
    {
        await using var env = await RemoteRuntimeEnv.StartAsync(new FakeModelRegistry().Add(Saved));
        var plain = Saved with { Speculative = null, CudaGraphMode = null };
        await env.AdoptAsync("m1", RemoteRuntimeEnv.DefaultImage, VllmArgsBuilder.Build(plain, TotalGiB), new Dictionary<string, string>(), budgetGiB: 40);

        var launched = await env.Runtime.GetLaunchedDefinitionAsync("m1", Ct);

        Assert.NotNull(launched);
        Assert.Null(launched.Speculative);
        Assert.Null(launched.CudaGraphMode);
        Assert.Null(launched.Image);
        Assert.Equal(Saved.MaxModelLen, launched.MaxModelLen);
        Assert.Equal(40, launched.MemoryBudgetGiB);
        Assert.Equal(Saved.Placement, launched.Placement);
    }

    [Theory]
    [InlineData(Remote)]
    [InlineData(Adopted)]
    public async Task Restart_StopsRemovesAndRecreates_WithHashAndBudgetLabels(string how)
    {
        var registry = new FakeModelRegistry().Add(Saved);
        await using var env = await RemoteRuntimeEnv.StartAsync(registry);
        await RunSavedAsync(env, how);
        string? before = (await env.Runtime.GetStatusAsync("m1", Ct)).ContainerId;
        var changed = Saved with { MaxNumSeqs = 32 };
        registry.Add(changed);

        var status = await env.Runtime.RestartAsync("m1", ct: Ct);

        AssertCreated(status);
        Assert.NotEqual(before, status.ContainerId);
        var config = await env.LaunchConfigAsync("m1");
        Assert.Equal(ModelConfigHash.Compute(changed, RemoteRuntimeEnv.DefaultImage, TotalGiB), config.Labels[ContainerLabels.ConfigHash]);
        Assert.Equal("40", config.Labels[ContainerLabels.BudgetGiB]);
        Assert.False(config.Labels.ContainsKey(ContainerLabels.Tuning));
        Assert.Equal(VllmArgsBuilder.Build(changed, TotalGiB), config.Argv);
    }

    [Fact]
    public async Task Restart_Admission_CountsTheModelsOwnBudgetAsReleased()
    {
        // 30 GiB available + 40 released − 8 reserve = 62 ≥ 44: admitted although 30 − 8 < 44.
        var registry = new FakeModelRegistry().Add(Saved);
        await using var env = await RemoteRuntimeEnv.StartAsync(registry);
        await RunSavedAsync(env, Remote);
        env.Agent.SetAvailableMemory(70); // 70 − 40 held by the Ready m1 = 30 available
        registry.Add(Saved with { MemoryBudgetGiB = 44 });

        AssertCreated(await env.Runtime.RestartAsync("m1", ct: Ct));
    }

    [Fact]
    public async Task Restart_RefusedAdmission_LeavesTheRunningContainerAlone()
    {
        // 10 + 40 − 8 = 42 < 60.
        var registry = new FakeModelRegistry().Add(Saved);
        await using var env = await RemoteRuntimeEnv.StartAsync(registry);
        await RunSavedAsync(env, Remote);
        string? before = (await env.Runtime.GetStatusAsync("m1", Ct)).ContainerId;
        env.Agent.SetAvailableMemory(50);
        registry.Add(Saved with { MemoryBudgetGiB = 60 });

        var ex = await Assert.ThrowsAsync<ModelMemoryAdmissionException>(() => env.Runtime.RestartAsync("m1", ct: Ct));

        Assert.Equal(
            "Model 'm1' needs 60.0 GiB but only 42.0 GiB is free (10.0 GiB available plus 40.0 GiB released by its current container, " +
            "minus a 8.0 GiB reserve) on node dgx.",
            ex.Message);
        var unchanged = await env.Runtime.GetStatusAsync("m1", Ct);
        Assert.Equal(ModelState.Ready, unchanged.State);
        Assert.Equal(before, unchanged.ContainerId);

        AssertCreated(await env.Runtime.RestartAsync("m1", force: true, Ct));
    }

    [Fact]
    public async Task Launch_TuningCandidate_IsLabelled_AndLeavesTheRegistryAlone()
    {
        var registry = new FakeModelRegistry().Add(Saved);
        await using var env = await RemoteRuntimeEnv.StartAsync(registry);
        await RunSavedAsync(env, Remote);

        await env.Runtime.LaunchAsync(Saved with { MaxNumSeqs = 8 }, new LaunchOptions(Tuning: true), Ct);

        var config = await env.LaunchConfigAsync("m1");
        Assert.Equal("1", config.Labels[ContainerLabels.Tuning]);
        Assert.Contains("8", config.Argv);
        Assert.Equal(Saved, await registry.GetAsync("m1", Ct));
    }

    // ------------------------------------------------------------ DockerModelRuntimeTests, parameterized

    [Fact]
    public async Task GetStatusAsync_NoContainer_IsStopped()
    {
        await using var env = await RemoteRuntimeEnv.StartAsync(new FakeModelRegistry().Add(Saved));

        var status = await env.Runtime.GetStatusAsync("m1", Ct);

        Assert.Equal(ModelState.Stopped, status.State);
        Assert.Null(status.ContainerId);
        Assert.Equal(ModelState.Stopped, (await env.Runtime.GetStatusAsync("not-registered", Ct)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_MemoryBudgetExceedsAvailable(bool force)
    {
        var model = new ModelDefinition { Id = "m1", ServedName = "M1", ModelPath = "p", MemoryBudgetGiB = 90, Placement = Saved.Placement };
        await using var env = await RemoteRuntimeEnv.StartAsync(new FakeModelRegistry().Add(model), availableGiB: 50); // 50 − 8 = 42 < 90

        if (!force)
        {
            var ex = await Assert.ThrowsAsync<ModelMemoryAdmissionException>(() => env.Runtime.StartAsync("m1", ct: Ct));
            Assert.Equal("Model 'm1' needs 90.0 GiB but only 42.0 GiB is free (50.0 GiB available minus a 8.0 GiB reserve) on node dgx.", ex.Message);
            Assert.Equal(ModelState.Stopped, (await env.Runtime.GetStatusAsync("m1", Ct)).State);
            return;
        }

        var status = await env.Runtime.StartAsync("m1", force: true, Ct);
        AssertCreated(status);
        Assert.NotNull(status.ContainerId);
        Assert.NotNull(status.HostPort);
    }

    [Fact]
    public async Task StartAsync_WhileAnotherModelIsStarting_RefusesWithTheOtherModelsName()
    {
        var m2 = new ModelDefinition { Id = "m2", ServedName = "M2", ModelPath = "p", MemoryBudgetGiB = 10, Placement = Saved.Placement };
        await using var env = await RemoteRuntimeEnv.StartAsync(new FakeModelRegistry()
            .Add(new ModelDefinition { Id = "m1", ServedName = "M1", ModelPath = "p", MemoryBudgetGiB = 10, Placement = Saved.Placement })
            .Add(m2));
        await env.Agent.SetStartsHeldAsync(true); // m2's /health does not answer yet: it is Starting
        Assert.Equal(ModelState.Starting, (await env.Runtime.StartAsync("m2", ct: Ct)).State);

        var ex = await Assert.ThrowsAsync<ModelStartConflictException>(() => env.Runtime.StartAsync("m1", ct: Ct));

        Assert.Equal("m2", ex.StartingModelId);
        Assert.Equal(
            "Model 'm2' is still starting. Start 'm1' once it is Ready: vLLM measures memory while it loads, " +
            "so two models starting together both fail on node dgx.",
            ex.Message);
        Assert.Equal(ModelState.Stopped, (await env.Runtime.GetStatusAsync("m1", Ct)).State);
    }

    [Fact]
    public async Task StartAsync_UnknownModel_Throws()
    {
        await using var env = await RemoteRuntimeEnv.StartAsync(new FakeModelRegistry());

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() => env.Runtime.StartAsync("does-not-exist", ct: Ct));
        Assert.Equal("Unknown model 'does-not-exist'.", ex.Message);
    }

    [Fact]
    public async Task StartAsync_CreatesContainer_WithModelArgsAsCommand()
    {
        var model = new ModelDefinition { Id = "m1", ServedName = "M1", ModelPath = "some-path", MemoryBudgetGiB = 10, Placement = Saved.Placement };
        await using var env = await RemoteRuntimeEnv.StartAsync(new FakeModelRegistry().Add(model));

        await env.Runtime.StartAsync("m1", ct: Ct);

        var config = await env.LaunchConfigAsync("m1");
        Assert.Contains("/models/some-path", config.Argv);
        Assert.Contains("--served-model-name", config.Argv);
        Assert.Equal(VllmArgsBuilder.Build(model, TotalGiB), config.Argv);
        Assert.Equal(RemoteRuntimeEnv.DefaultImage, config.Image);
        Assert.Empty(config.Env);
        Assert.Empty(config.Gpus);
        Assert.Equal(
            new[] { new MountSpec(LogicalVolumes.Models, null, LogicalVolumes.ModelsTarget, true), new MountSpec(LogicalVolumes.Cache, null, LogicalVolumes.CacheTarget, false) },
            config.Mounts);
        Assert.Equal("M1", config.Labels[ContainerLabels.ServedName]);
    }

    [Fact]
    public async Task StartAsync_OnARunningModel_ReturnsIt_WithoutANewContainer()
    {
        await using var env = await RemoteRuntimeEnv.StartAsync(new FakeModelRegistry().Add(Saved));
        await RunSavedAsync(env, Remote);
        string? before = (await env.Runtime.GetStatusAsync("m1", Ct)).ContainerId;

        var status = await env.Runtime.StartAsync("m1", ct: Ct);

        Assert.Equal(ModelState.Ready, status.State);
        Assert.Equal(before, status.ContainerId);
    }

    [Fact]
    public async Task GetStatusAsync_FailedInstance_IsFailedWithItsError()
    {
        await using var env = await RemoteRuntimeEnv.StartAsync(new FakeModelRegistry().Add(Saved));
        await RunSavedAsync(env, Remote);

        env.Agent.FailInstance("m1", "CUDA out of memory");

        var status = await env.Runtime.GetStatusAsync("m1", Ct);
        Assert.Equal(ModelState.Failed, status.State);
        Assert.Equal("CUDA out of memory", status.Error);
        Assert.False(status.RestartPending);
    }

    // ------------------------------------------------------------ CanonicalConfigTests (restart pending after the migration), parameterized

    [Theory]
    [InlineData("qwen")]
    [InlineData("nemotron")]
    public async Task Pending_False_ForLiveContainers_WhoseEffectiveConfigIsUnchanged(string which)
    {
        var legacy = (which == "qwen" ? LiveDefinitions.Qwen35B : LiveDefinitions.Nemotron) with { Placement = Saved.Placement };
        var saved = VllmArgsNormalizer.Apply(legacy);
        await using var env = await RemoteRuntimeEnv.StartAsync(new FakeModelRegistry().Add(saved), LiveDefinitions.TotalGiB, defaultImage: LiveDefinitions.Image);
        var cmd = LiveDefinitions.LegacyBuild(legacy, LiveDefinitions.TotalGiB);
        await env.AdoptLegacyAsync(saved.Id, cmd, LiveDefinitions.LegacyHash(LiveDefinitions.Image, cmd));

        Assert.False((await env.Runtime.GetStatusAsync(legacy.Id, Ct)).RestartPending);
        Assert.False(Assert.Single(await env.Runtime.ListStatusAsync(Ct)).RestartPending);
        Assert.Equal(await env.Runtime.ComputeConfigHashAsync(saved, Ct), await env.Runtime.GetLaunchedConfigHashAsync(legacy.Id, Ct));
    }

    [Fact]
    public async Task Pending_True_ForTheEmbeddingModel_WhosePrefixCachingBecomesExplicitlyOff()
    {
        var embedding = LiveDefinitions.Embedding with { Placement = Saved.Placement };
        await using var env = await RemoteRuntimeEnv.StartAsync(new FakeModelRegistry().Add(embedding), LiveDefinitions.TotalGiB, defaultImage: LiveDefinitions.Image);
        var cmd = LiveDefinitions.LegacyBuild(embedding, LiveDefinitions.TotalGiB);
        await env.AdoptLegacyAsync(embedding.Id, cmd, LiveDefinitions.LegacyHash(LiveDefinitions.Image, cmd));

        Assert.True((await env.Runtime.GetStatusAsync(embedding.Id, Ct)).RestartPending);
    }

    [Fact]
    public async Task Pending_True_WhenTheSavedSettingsReallyDiffer_FromALegacyContainer()
    {
        var legacy = LiveDefinitions.Qwen35B with { Placement = Saved.Placement };
        var saved = VllmArgsNormalizer.Apply(legacy) with { Speculative = new SpeculativeDecoding("mtp", 2) };
        await using var env = await RemoteRuntimeEnv.StartAsync(new FakeModelRegistry().Add(saved), LiveDefinitions.TotalGiB, defaultImage: LiveDefinitions.Image);
        var cmd = LiveDefinitions.LegacyBuild(legacy, LiveDefinitions.TotalGiB);
        await env.AdoptLegacyAsync(saved.Id, cmd, LiveDefinitions.LegacyHash(LiveDefinitions.Image, cmd));

        Assert.True((await env.Runtime.GetStatusAsync(saved.Id, Ct)).RestartPending);
    }

    // ------------------------------------------------------------ MN-028: hashes, spec, fingerprint

    [Theory]
    [InlineData("qwen")]
    [InlineData("nemotron")]
    [InlineData("embedding")]
    public async Task ConfigHash_OfALaunchedDefinition_EqualsTodaysHash(string which)
    {
        var model = VllmArgsNormalizer.Apply(which switch
        {
            "qwen" => LiveDefinitions.Qwen35B,
            "nemotron" => LiveDefinitions.Nemotron,
            _ => LiveDefinitions.Embedding,
        }) with { Placement = Saved.Placement };
        await using var env = await RemoteRuntimeEnv.StartAsync(new FakeModelRegistry().Add(model), LiveDefinitions.TotalGiB, defaultImage: LiveDefinitions.Image);

        await env.Runtime.StartAsync(model.Id, force: true, Ct);

        string today = ModelConfigHash.Compute(model, LiveDefinitions.Image, LiveDefinitions.TotalGiB);
        Assert.Equal(today, await env.Runtime.ComputeConfigHashAsync(model, Ct));
        Assert.Equal(today, await env.Runtime.GetLaunchedConfigHashAsync(model.Id, Ct));
        var config = await env.LaunchConfigAsync(model.Id);
        Assert.Equal(today, config.Labels[ContainerLabels.ConfigHash]);
        Assert.Equal(VllmArgsBuilder.Build(model, LiveDefinitions.TotalGiB), config.Argv);

        // The fingerprint label equals the one recomputed from the read-back (design §5.1).
        Assert.Equal(
            ExecutionFingerprint.Compute(config, RuntimeKind.Vllm, env.Agent.NodeUid, today),
            config.Labels[ContainerLabels.ExecutionFingerprint]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_FollowsTheExecutionFingerprint_WhenBothSidesCarryOne(bool differs)
    {
        await using var env = await RemoteRuntimeEnv.StartAsync(new FakeModelRegistry().Add(Saved));
        var node = (await env.Nodes.GetAsync(RemoteRuntimeEnv.NodeId, Ct))!;
        var built = LaunchSpecBuilder.Build(Saved, node, RemoteRuntimeEnv.DefaultImage, TotalGiB, tuning: false);
        await env.AdoptAsync("m1", RemoteRuntimeEnv.DefaultImage, built.Spec.Argv, new Dictionary<string, string>
        {
            [ContainerLabels.ConfigHash] = built.ConfigHash,
            [ContainerLabels.ExecutionFingerprint] = differs ? "00000000deadbeef" : built.ExecutionFingerprint,
        }, budgetGiB: 40);

        Assert.Equal(differs, (await env.Runtime.GetStatusAsync("m1", Ct)).RestartPending);
    }

    [Fact]
    public void LaunchSpec_IsTheP1Shape_EmptyEnvNoSecretsAllGpus()
    {
        var node = new NodeRecord { Id = "dgx", Uid = "uid-1", Address = "https://127.0.0.1:9443", MemoryReserveGiB = 6 };

        var built = LaunchSpecBuilder.Build(Saved, node, "default-image", TotalGiB, tuning: true);

        var spec = built.Spec;
        Assert.Equal(LaunchDelivery.Container, spec.Kind);
        Assert.Equal("default-image", spec.Image);
        Assert.Null(spec.ImageDigest);
        Assert.Empty(spec.Env);
        Assert.Empty(spec.SecretEnvNames);
        Assert.Equal(GpuSelectionMode.All, spec.Gpus.Mode);
        Assert.True(spec.IpcHost);
        Assert.Equal(8000, spec.ContainerPort);
        Assert.Equal(VllmArgsBuilder.Build(Saved, TotalGiB), spec.Argv);
        Assert.Equal(SavedHash, built.ConfigHash);
        Assert.Equal(ExecutionFingerprint.Compute(spec, "uid-1", [], SavedHash), built.ExecutionFingerprint);
        Assert.Equal(
            new Dictionary<string, string>
            {
                [ContainerLabels.ServedName] = "M1",
                [ContainerLabels.ConfigHash] = SavedHash,
                [ContainerLabels.ExecutionFingerprint] = built.ExecutionFingerprint,
                [ContainerLabels.BudgetGiB] = "40",
                [ContainerLabels.Tuning] = "1",
            },
            spec.Labels);
        Assert.DoesNotContain(spec.Labels.Keys, k => ContainerLabels.Reserved.Contains(k) || k == ContainerLabels.Port);

        var admission = LaunchSpecBuilder.Admission(Saved, node, force: true);
        Assert.Equal(40, admission.BudgetGiB);
        Assert.Equal(6, admission.ReserveGiB);
        Assert.True(admission.Force);

        // On the wire: "env": {}, "secretEnvNames": [], "gpus": {"mode": "all"} (D47).
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(spec, AgentJson.Options));
        Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty("env").ValueKind);
        Assert.Empty(json.RootElement.GetProperty("env").EnumerateObject());
        Assert.Equal(0, json.RootElement.GetProperty("secretEnvNames").GetArrayLength());
        Assert.Equal("all", json.RootElement.GetProperty("gpus").GetProperty("mode").GetString());
    }

    [Theory]
    [InlineData(null, null, "global-image")]
    [InlineData("node-image", null, "node-image")]
    [InlineData("node-image", "own-image", "own-image")]
    [InlineData(null, "", "global-image")]
    public void Image_IsTheModelsOwn_ElseTheNodeDefault_ElseTheGlobalDefault(string? nodeDefault, string? own, string expected)
    {
        var node = new NodeRecord { Id = "dgx", Uid = "uid-1", Address = "https://127.0.0.1:9443", DefaultVllmImage = nodeDefault };
        string defaultImage = LaunchSpecBuilder.DefaultImage(node, new MirbadLLMOptions { VllmImage = "global-image" });

        var built = LaunchSpecBuilder.Build(Saved with { Image = own }, node, defaultImage, TotalGiB, tuning: false);

        Assert.Equal(expected, built.Image);
        Assert.Equal(expected, built.Spec.Image);
        Assert.Equal(ModelConfigHash.Compute(Saved with { Image = own }, defaultImage, TotalGiB), built.ConfigHash);
    }

    // ------------------------------------------------------------ MN-028: waiting for "created", self-conflicts

    [Fact]
    public async Task Start_WaitsForTheContainer_WhileThePullRuns_BeyondTheCreateBound()
    {
        var options = new RemoteModelRuntimeOptions { PollInterval = TimeSpan.FromMilliseconds(20), CreateTimeout = TimeSpan.FromMilliseconds(200) };
        await using var env = await RemoteRuntimeEnv.StartAsync(new FakeModelRegistry().Add(Saved), runtimeOptions: options);
        await env.Agent.SetPullsHeldAsync(true);

        var start = env.Runtime.StartAsync("m1", ct: Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(600), Ct);
        Assert.False(start.IsCompleted);

        await env.Agent.SetPullsHeldAsync(false);
        var status = await start.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        AssertCreated(status);
        Assert.NotNull(status.ContainerId);
    }

    [Fact]
    public async Task EnsureStarted_OnItsOwnPendingLaunch_WaitsForItInsteadOfAConflict()
    {
        // A second runtime stands for the master after a restart: the first launch is still pulling on the node.
        await using var env = await RemoteRuntimeEnv.StartAsync(new FakeModelRegistry().Add(Saved));
        await env.Agent.SetPullsHeldAsync(true);
        var first = env.Runtime.StartAsync("m1", ct: Ct);
        await env.WaitForActiveLaunchAsync("m1");

        var second = env.CreateRuntime().StartAsync("m1", ct: Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(300), Ct);
        Assert.False(second.IsCompleted);

        await env.Agent.SetPullsHeldAsync(false);
        var a = await first.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var b = await second.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.NotNull(a.ContainerId);
        Assert.Equal(a.ContainerId, b.ContainerId);
        Assert.True(b.State is ModelState.Starting or ModelState.Ready);
    }

    [Fact]
    public async Task Replace_OnItsOwnPendingLaunch_SupersedesIt_AndLaunchesAgain()
    {
        var registry = new FakeModelRegistry().Add(Saved);
        await using var env = await RemoteRuntimeEnv.StartAsync(registry);
        await env.Agent.SetPullsHeldAsync(true);
        var first = env.Runtime.StartAsync("m1", ct: Ct);
        string firstLaunch = (await env.WaitForActiveLaunchAsync("m1")).Id;
        var changed = Saved with { MaxNumSeqs = 32 };
        registry.Add(changed);

        var restart = env.CreateRuntime().RestartAsync("m1", ct: Ct);

        // Ended before create: its own type (409 at the admin API, W4 review MN-096), still an InvalidOperationException.
        var cancelled = await Assert.ThrowsAsync<ModelStartInterruptedException>(() => first.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.IsAssignableFrom<InvalidOperationException>(cancelled);
        Assert.Contains("cancelled", cancelled.Message, StringComparison.Ordinal);
        Assert.EndsWith("(on node dgx)", cancelled.Message, StringComparison.Ordinal);
        Assert.Equal("m1", cancelled.ModelId);
        Assert.Equal("dgx", cancelled.NodeId);
        await env.Agent.SetPullsHeldAsync(false);
        var status = await restart.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        AssertCreated(status);
        var config = await env.LaunchConfigAsync("m1");
        Assert.NotEqual(firstLaunch, config.Labels[ContainerLabels.Operation]);
        Assert.Equal(VllmArgsBuilder.Build(changed, TotalGiB), config.Argv);
    }

    // ------------------------------------------------------------ MN-028: stop, logs, nodes

    [Fact]
    public async Task Stop_RemovesTheInstance_AndAgainIsANoOp()
    {
        await using var env = await RemoteRuntimeEnv.StartAsync(new FakeModelRegistry().Add(Saved));
        await RunSavedAsync(env, Remote);

        await env.Runtime.StopAsync("m1", Ct);

        Assert.Equal(ModelState.Stopped, (await env.Runtime.GetStatusAsync("m1", Ct)).State);
        Assert.Equal(ModelState.Stopped, Assert.Single(await env.Runtime.ListStatusAsync(Ct)).State);
        await env.Runtime.StopAsync("m1", Ct);
        await env.Runtime.StopAsync("not-registered", Ct);
        Assert.DoesNotContain(await env.Store.ListOpenAsync(Ct), r => r.Action == OperationKinds.Stop);
    }

    [Fact]
    public async Task Logs_StreamTheInstancesLines_AndNothingWithoutAnInstance()
    {
        await using var env = await RemoteRuntimeEnv.StartAsync(new FakeModelRegistry().Add(Saved));
        Assert.Empty(await ReadLogsAsync(env));

        await RunSavedAsync(env, Remote);
        env.Agent.AppendLog("m1", "hello from vLLM");

        var lines = await ReadLogsAsync(env);
        Assert.Contains(lines, l => l.Contains("hello from vLLM", StringComparison.Ordinal));

        static async Task<List<string>> ReadLogsAsync(RemoteRuntimeEnv env)
        {
            var lines = new List<string>();
            await foreach (var line in env.Runtime.StreamLogsAsync("m1", tail: 50, follow: false, Ct))
            {
                lines.Add(line);
            }

            return lines;
        }
    }

    [Theory]
    [InlineData(NodeState.Offline, false, "Node 'dgx' is offline", NodeUnavailableReason.NotServing)]
    [InlineData(NodeState.Incompatible, false, "Node 'dgx' is incompatible", NodeUnavailableReason.NotServing)]
    [InlineData(NodeState.Online, true, "Node 'dgx' is draining", NodeUnavailableReason.Draining)]
    public async Task Start_IsRefused_OnANodeThatCannotTakeIt(NodeState state, bool draining, string expected, NodeUnavailableReason reason)
    {
        await using var env = await RemoteRuntimeEnv.StartAsync(new FakeModelRegistry().Add(Saved));
        env.Cache.State = state;
        env.Cache.Draining = draining;

        // A NodeUnavailableException (still an InvalidOperationException), so the admin API answers 409/503, not 500 (MN-094).
        var ex = await Assert.ThrowsAsync<NodeUnavailableException>(() => env.Runtime.StartAsync("m1", ct: Ct));

        Assert.StartsWith(expected, ex.Message, StringComparison.Ordinal);
        Assert.Equal(reason, ex.Reason);
        Assert.Equal("dgx", ex.NodeId);
        Assert.Null(await env.Factory.GetClient(RemoteRuntimeEnv.NodeId).GetRuntimeAsync("m1", Ct));
    }

    [Fact]
    public async Task Status_OfAnOfflineNode_IsTheCachesLastView()
    {
        await using var env = await RemoteRuntimeEnv.StartAsync(new FakeModelRegistry().Add(Saved));
        await RunSavedAsync(env, Remote);
        await env.Cache.RefreshNodeAsync(RemoteRuntimeEnv.NodeId, Ct);
        env.Cache.State = NodeState.Offline;
        env.Agent.SetPartitioned(true);

        var status = await env.Runtime.GetStatusAsync("m1", Ct);

        Assert.Equal(ModelState.Ready, status.State);
        Assert.True((await Assert.ThrowsAsync<NodeUnavailableException>(() => env.Runtime.StopAsync("m1", Ct))).IsTransient);
        env.Agent.SetPartitioned(false);
    }

    [Fact]
    public async Task Start_OfAnUnplacedModel_IsRefused()
    {
        await using var env = await RemoteRuntimeEnv.StartAsync(new FakeModelRegistry().Add(Saved with { Placement = null }));

        var ex = await Assert.ThrowsAsync<NodeUnavailableException>(() => env.Runtime.StartAsync("m1", ct: Ct));

        Assert.Equal("Model 'm1' is not placed on a node.", ex.Message);
        Assert.Equal(NodeUnavailableReason.Unplaced, ex.Reason);
    }
}

/// <summary>
/// A <see cref="FakeAgent"/> registered as node <c>dgx</c>, the real <see cref="AgentClientFactory"/> and
/// <see cref="AgentOperationRunner"/>, an <see cref="AgentBackedStatusCache"/> and the runtime under test.
/// </summary>
internal sealed class RemoteRuntimeEnv : IAsyncDisposable
{
    public const string NodeId = "dgx";
    public const string DefaultImage = "default-image";

    private static readonly RemoteModelRuntimeOptions FastOptions = new() { PollInterval = TimeSpan.FromMilliseconds(20) };

    private readonly CertificateAuthority _ca;
    private readonly IssuedCredential _master;
    private readonly TrustAnchorSet _trust;
    private readonly string _dataDir;
    private readonly IModelRegistry _models;
    private readonly IOptions<MirbadLLMOptions> _options;
    private readonly RemoteModelRuntimeOptions _runtimeOptions;

    private RemoteRuntimeEnv(
        CertificateAuthority ca, IssuedCredential master, TrustAnchorSet trust, string dataDir, IModelRegistry models,
        IOptions<MirbadLLMOptions> options, RemoteModelRuntimeOptions runtimeOptions, FakeAgent agent, JsonNodeRegistry nodes,
        AgentClientFactory factory, JsonAgentOperationReferenceStore store, AgentOperationRunner runner, AgentBackedStatusCache cache)
    {
        _ca = ca;
        _master = master;
        _trust = trust;
        _dataDir = dataDir;
        _models = models;
        _options = options;
        _runtimeOptions = runtimeOptions;
        Agent = agent;
        Nodes = nodes;
        Factory = factory;
        Store = store;
        Runner = runner;
        Cache = cache;
        Runtime = CreateRuntime();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FakeAgent Agent { get; }

    public JsonNodeRegistry Nodes { get; }

    public AgentClientFactory Factory { get; }

    public JsonAgentOperationReferenceStore Store { get; }

    public AgentOperationRunner Runner { get; }

    public AgentBackedStatusCache Cache { get; }

    public RemoteModelRuntime Runtime { get; }

    public static async Task<RemoteRuntimeEnv> StartAsync(
        IModelRegistry models,
        double totalGiB = 120,
        double availableGiB = 100,
        string defaultImage = DefaultImage,
        RemoteModelRuntimeOptions? runtimeOptions = null,
        IReadOnlyList<string>? features = null,
        NodeCapabilities? lastCapabilities = null)
    {
        var ca = CertificateAuthority.Create();
        var agentOptions = new FakeAgentOptions { Ca = ca, TotalMemoryGiB = totalGiB, AvailableMemoryGiB = availableGiB };
        var agent = await FakeAgent.StartAsync(features is null ? agentOptions : agentOptions with { Features = features }, Ct);
        string dataDir = Path.Combine(Path.GetTempPath(), "remote-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var options = Options.Create(new MirbadLLMOptions { DataPath = dataDir, VllmImage = defaultImage });
        var nodes = new JsonNodeRegistry(options, TimeProvider.System);
        await nodes.UpsertAsync(new NodeRecord
        {
            Id = NodeId,
            Uid = agent.NodeUid,
            Address = agent.BaseAddress.AbsoluteUri,
            Approved = true,
            CertSpkiSha256 = agent.Target.AgentSpkiPin,
            LastCapabilities = lastCapabilities,
        }, Ct);
        var master = ca.IssueMasterClientCertificate();
        var trust = TrustAnchorSet.Create([ca.Certificate]);
        var factory = new AgentClientFactory(nodes, new TestMasterIdentity(), master, trust, TimeProvider.System);
        await factory.RefreshAsync(Ct);
        var store = new JsonAgentOperationReferenceStore(options, TimeProvider.System);
        var runner = new AgentOperationRunner(factory, store, nodes, TimeProvider.System);
        var cache = new AgentBackedStatusCache(factory, NodeId, agent.NodeUid);
        await cache.RefreshNodeAsync(NodeId, Ct);
        return new RemoteRuntimeEnv(ca, master, trust, dataDir, models, options, runtimeOptions ?? FastOptions, agent, nodes, factory, store, runner, cache);
    }

    /// <summary>Another runtime over the same node (the master after a restart: no shared locks or caches).</summary>
    public RemoteModelRuntime CreateRuntime(ModelRecoveryState? recovery = null) =>
        new(_models, Nodes, Cache, Factory, Runner, _options, TimeProvider.System, runtimeOptions: _runtimeOptions, recovery: recovery);

    /// <summary>Launches an instance directly through the agent, as a container from before P1 would be adopted, and waits for Ready.</summary>
    public async Task AdoptAsync(string modelId, string image, IReadOnlyList<string> argv, IReadOnlyDictionary<string, string> labels, double budgetGiB)
    {
        var envelope = AgentEnvelope.Create(TimeProvider.System);
        await Factory.GetClient(NodeId).LaunchAsync(modelId, new LaunchRequest
        {
            OperationId = envelope.OperationId,
            IssuedAt = envelope.IssuedAt,
            ExpiresAt = envelope.ExpiresAt,
            Mode = LaunchMode.Replace,
            Spec = new LaunchSpec
            {
                Kind = LaunchDelivery.Container,
                Image = image,
                Argv = argv,
                Mounts =
                [
                    new MountSpec(LogicalVolumes.Models, null, LogicalVolumes.ModelsTarget, true),
                    new MountSpec(LogicalVolumes.Cache, null, LogicalVolumes.CacheTarget, false),
                ],
                IpcHost = true,
                Labels = labels,
            },
            Admission = new AdmissionRequest { BudgetGiB = budgetGiB, ReserveGiB = 8, Force = true },
        }, Ct);
        await WaitForStateAsync(modelId, ModelState.Ready);
    }

    /// <summary>A container launched before the canonical configuration: the legacy command line and its legacy hash label.</summary>
    public Task AdoptLegacyAsync(string modelId, IReadOnlyList<string> cmd, string legacyHash) =>
        AdoptAsync(modelId, LiveDefinitions.Image, cmd, new Dictionary<string, string>
        {
            [ContainerLabels.ConfigHash] = legacyHash,
            [ContainerLabels.BudgetGiB] = "40",
        }, budgetGiB: 40);

    public async Task<LaunchConfig> LaunchConfigAsync(string modelId) =>
        await Factory.GetClient(NodeId).GetLaunchConfigAsync(modelId, Ct) ?? throw new InvalidOperationException($"No instance of {modelId}.");

    /// <summary>Waits until the agent reports <paramref name="state"/>, then refreshes the cache (the node monitor's events in production).</summary>
    public async Task WaitForStateAsync(string modelId, ModelState state)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while ((await Factory.GetClient(NodeId).GetRuntimeAsync(modelId, Ct))?.State != state)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"{modelId} did not reach {state}.");
            }

            await Task.Delay(10, Ct);
        }

        await Cache.RefreshNodeAsync(NodeId, Ct);
    }

    /// <summary>Waits until the agent lists an active launch of the model.</summary>
    public async Task<OperationStatus> WaitForActiveLaunchAsync(string modelId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            var active = await Factory.GetClient(NodeId).ListOperationsAsync(new OperationsQuery { State = OperationsQuery.ActiveState }, Ct);
            if (active.FirstOrDefault(o => o.Kind == OperationKinds.Launch && o.Target == modelId) is { } launch)
            {
                return launch;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"No active launch of {modelId}.");
            }

            await Task.Delay(10, Ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Agent.DisposeAsync();
        Store.Dispose();
        Nodes.Dispose();
        _master.Dispose();
        _trust.Dispose();
        _ca.Dispose();
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }
}

/// <summary>
/// A hand-written <see cref="INodeStatusCache"/> for one node (MN-027 builds the real one in parallel): the node state is
/// set by the test, runtimes and memory come from the agent when the cache is refreshed (the node monitor keeps the real
/// cache current from events).
/// </summary>
internal sealed class AgentBackedStatusCache(IAgentClientFactory clients, string nodeId, string nodeUid) : INodeStatusCache
{
    private readonly object _gate = new();
    private Dictionary<string, NodeModelRuntime> _runtimes = new(StringComparer.Ordinal);
    private NodeMemorySnapshot? _memory;
    private string? _bootId;
    private long _version;

    public NodeState State { get; set; } = NodeState.Online;

    public bool Draining { get; set; }

    public long Version => Interlocked.Read(ref _version);

    public IReadOnlyList<NodeLiveStatus> ListNodes() => [GetNode(nodeId)!];

    public NodeLiveStatus? GetNode(string id)
    {
        if (!string.Equals(id, nodeId, StringComparison.Ordinal))
        {
            return null;
        }

        lock (_gate)
        {
            return new NodeLiveStatus
            {
                NodeId = nodeId,
                NodeUid = nodeUid,
                BootId = _bootId,
                State = State,
                Draining = Draining,
                Memory = _memory,
                Runtimes = [.. _runtimes.Values],
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    public NodeModelRuntime? GetRuntime(string modelId)
    {
        lock (_gate)
        {
            return _runtimes.GetValueOrDefault(modelId);
        }
    }

    public IReadOnlyList<NodeModelRuntime> ListRuntimes(string? id = null)
    {
        lock (_gate)
        {
            return id is null || string.Equals(id, nodeId, StringComparison.Ordinal) ? [.. _runtimes.Values] : [];
        }
    }

    public ServedModelRoute? ResolveServedName(string servedName)
    {
        lock (_gate)
        {
            return _runtimes.Values.FirstOrDefault(r => r.ServedName == servedName) is { } r
                ? new ServedModelRoute(r.ModelId, nodeId, r.State, State, Draining, r.UpdatedAt)
                : null;
        }
    }

    public async Task RefreshModelAsync(string modelId, CancellationToken ct = default)
    {
        var runtime = await clients.GetClient(nodeId).GetRuntimeAsync(modelId, ct);
        lock (_gate)
        {
            if (runtime is null)
            {
                _runtimes.Remove(modelId);
            }
            else
            {
                _runtimes[modelId] = Map(runtime);
            }

            _version++;
        }
    }

    public async Task RefreshNodeAsync(string id, CancellationToken ct = default)
    {
        var status = await clients.GetClient(nodeId).GetStatusAsync(ct);
        lock (_gate)
        {
            _memory = status.Memory;
            _bootId = status.BootId;
            _runtimes = status.Runtimes.ToDictionary(r => r.ModelId, Map, StringComparer.Ordinal);
            _version++;
        }
    }

    private NodeModelRuntime Map(RuntimeStatus r) => new()
    {
        ModelId = r.ModelId,
        NodeId = nodeId,
        State = r.State,
        ServedName = r.ServedName,
        InstanceId = r.InstanceId,
        HostPort = r.HostPort,
        Since = r.Since,
        Error = r.Error,
        RestartCount = r.RestartCount,
        ConfigHash = r.ConfigHash,
        ExecutionFingerprint = r.ExecutionFingerprint,
        Tuning = r.Tuning,
        OperationId = r.OperationId,
        Labels = r.Labels,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}

/// <summary>A fixed master triple.</summary>
internal sealed class TestMasterIdentity : IMasterIdentity
{
    public string InstanceId => "instance-1";

    public long Epoch => 1;

    public string BootId => "boot-1";

    public MasterTriple Current => new(InstanceId, Epoch, BootId);

    public bool TakeoverRequested => false;

    public IReadOnlyList<TakeoverRecord> Takeovers => [];

    public Task<MasterTriple> RaiseEpochToAsync(long epoch, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<MasterTriple> CompleteTakeoverAsync(TakeoverRecord record, CancellationToken ct = default) => throw new NotSupportedException();
}
