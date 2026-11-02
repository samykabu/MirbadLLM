using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.UnitTests.Multinode.Fakes;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-219 (W12 review, D125): <see cref="RemoteModelRuntime"/> with a llama.cpp model on a node reporting
/// <see cref="AgentFeatures.RuntimeLlamaCppProcess"/> (MN-210's builder, over <see cref="FakeAgent"/>'s minimal process
/// support). llama.cpp has no legacy config hash (design-p3p4 §A.1.6): the launch sends <c>expectedConfigHash</c> null (the
/// P3 fixture), restart-pending is decided by the execution fingerprint alone, the read-back parses the argv with
/// <see cref="LlamaCppArgsParser"/>, and the launcher's "config hash" of a llama.cpp model is its execution fingerprint
/// (so benchmarks and the tune page tell configurations apart). Before the fix the request carried <c>""</c>, the
/// read-back went through <c>VllmArgsParser</c> and both config-hash calls answered <c>""</c>/null.
/// </summary>
public sealed class RemoteModelRuntimeLlamaCppTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IReadOnlyList<string> P3Features =
        [.. FakeAgentOptions.P1Features, AgentFeatures.RuntimeLlamaCppProcess];

    /// <summary>OpenViking's embedder (the P3 fixture) placed on the fake node, without GPUs (the fake has none to select).</summary>
    private static ModelDefinition Embedder() => LlamaCppFixtures.Embedder() with { Placement = new ModelPlacement(RemoteRuntimeEnv.NodeId) };

    private static Task<RemoteRuntimeEnv> StartAsync(FakeModelRegistry registry) =>
        RemoteRuntimeEnv.StartAsync(registry, features: P3Features,
            lastCapabilities: LlamaCppFixtures.Capabilities() with { Features = P3Features });

    private static async Task<(RemoteRuntimeEnv Env, FakeModelRegistry Registry)> RunningAsync(ModelDefinition model)
    {
        var registry = new FakeModelRegistry().Add(model);
        var env = await StartAsync(registry);
        await env.Runtime.StartAsync(model.Id, ct: Ct);
        await env.WaitForStateAsync(model.Id, ModelState.Ready);
        return (env, registry);
    }

    [Fact]
    public async Task Launch_SendsNoLegacyConfigHash_AndTheFingerprintOfTheProcessSpec()
    {
        var model = Embedder();
        var (env, _) = await RunningAsync(model);
        await using var _env = env;

        var request = env.Agent.LaunchRequests[^1];
        Assert.Equal(LaunchDelivery.Process, request.Spec.Kind);
        Assert.Null(request.ExpectedConfigHash);
        var node = (await env.Nodes.GetAsync(RemoteRuntimeEnv.NodeId, Ct))!;
        var built = LaunchSpecBuilder.Build(model, node, RemoteRuntimeEnv.DefaultImage, 120, tuning: false);
        Assert.Equal(built.ExecutionFingerprint, request.ExpectedExecutionFingerprint);
        Assert.False(request.Spec.Labels.ContainsKey(ContainerLabels.ConfigHash));
    }

    [Fact]
    public async Task RestartPending_FollowsTheExecutionFingerprint()
    {
        var model = Embedder();
        var (env, registry) = await RunningAsync(model);
        await using var _env = env;

        Assert.False((await env.Runtime.GetStatusAsync(model.Id, Ct)).RestartPending);

        registry.Add(model with { AutoStart = !model.AutoStart }); // not part of the launch
        Assert.False((await env.Runtime.GetStatusAsync(model.Id, Ct)).RestartPending);

        registry.Add(model with { LlamaCpp = model.LlamaCpp! with { Parallel = model.LlamaCpp.Parallel + 1 } });
        Assert.True((await env.Runtime.GetStatusAsync(model.Id, Ct)).RestartPending);
        Assert.True(Assert.Single(await env.Runtime.ListStatusAsync(Ct)).RestartPending);
    }

    [Fact]
    public async Task LaunchedDefinition_IsReadBackAsALlamaCppDefinition()
    {
        var model = Embedder();
        var (env, registry) = await RunningAsync(model);
        await using var _env = env;
        // The saved definition changed since the launch: the read-back shows what runs (the Apply dialog's "old").
        registry.Add(model with { LlamaCpp = model.LlamaCpp! with { Parallel = model.LlamaCpp.Parallel + 1, Threads = 6 } });

        var launched = await env.Runtime.GetLaunchedDefinitionAsync(model.Id, Ct);

        Assert.NotNull(launched);
        Assert.Equal(RuntimeKind.LlamaCpp, launched.Runtime);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(model.LlamaCpp), System.Text.Json.JsonSerializer.Serialize(launched.LlamaCpp));
        Assert.Equal(model.ServedName, launched.ServedName);
        Assert.Equal(model.MemoryBudgetGiB, launched.MemoryBudgetGiB);
    }

    [Fact]
    public async Task ConfigHash_OfALlamaCppModel_IsItsExecutionFingerprint_OnBothSides()
    {
        var model = Embedder();
        var (env, _) = await RunningAsync(model);
        await using var _env = env;

        string wanted = await env.Runtime.ComputeConfigHashAsync(model, Ct);
        Assert.Equal(env.Agent.LaunchRequests[^1].ExpectedExecutionFingerprint, wanted);
        Assert.Equal(wanted, await env.Runtime.GetLaunchedConfigHashAsync(model.Id, Ct));

        var other = model with { LlamaCpp = model.LlamaCpp! with { ContextSize = model.LlamaCpp.ContextSize / 2 } };
        Assert.NotEqual(wanted, await env.Runtime.ComputeConfigHashAsync(other, Ct));
    }
}
