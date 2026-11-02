using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Pki;

namespace MirbadLLM.UnitTests.Multinode.Chaos;

/// <summary>
/// MN-044 review follow-ups as scripted chaos scenarios on the real master and the real agent (<see cref="ChaosCluster"/>),
/// each ending with every <see cref="ChaosInvariants"/> invariant:
/// <list type="bullet">
/// <item>W4 (D61): a master restart while the agent keeps running: the node is back within the 5 s registry poll and the
/// running model is not started a second time.</item>
/// <item>W3 (D51): a master boot with the agent still down: boot auto-start waits for the node, then starts the model
/// exactly once (one launch, one container).</item>
/// <item>W4 (D58): an enrolled agent restarting with a bootstrap bundle present and the master down serves with its
/// identity after the bounded wait and keeps the bundle.</item>
/// <item>W3 (D56): an agent restart with Docker unreachable fails the interrupted launch <c>docker_unavailable</c>; once
/// Docker is back, starting the model again creates no second container.</item>
/// </list>
/// </summary>
[Trait("Category", "Chaos")]
public sealed class ChaosScenarioTests
{
    private const string AutoStarted = "phi";
    private const string Manual = "qwen";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MasterRestartsWithTheAgentUpAndWithTheAgentDown()
    {
        await using var cluster = await ChaosCluster.StartAsync(4410, Ct);
        await SettleAsync(cluster, "initial");
        await EventuallyAsync(cluster, async () => (await cluster.AgentClient().GetRuntimeAsync(AutoStarted, Ct))?.State == ModelState.Ready,
            "boot auto-start to bring phi Ready");

        // D61: master restart, agent keeps running.
        string? instance = (await cluster.AgentClient().GetRuntimeAsync(AutoStarted, Ct))?.InstanceId;
        await cluster.StopMasterAsync();
        await cluster.StartMasterAsync(Ct);
        var back = Stopwatch.StartNew();
        await cluster.WaitOnlineAsync(TimeSpan.FromSeconds(30), Ct);
        back.Stop();
        Assert.True(back.Elapsed < TimeSpan.FromSeconds(10), Report(cluster, $"D61: dgx Online {back.Elapsed.TotalSeconds:0.0} s after the master restart (5 s registry poll)"));
        await SettleAsync(cluster, "D61 master restart with the agent up");
        // The running phi is neither replaced nor duplicated. (Boot auto-start reads phi's status again after waiting for the
        // node's start slot, MN-047, so a read from before the first snapshot no longer sends a redundant ensureStarted; if
        // the snapshot lands in the last moment before the start, that launch is idempotent and creates nothing.)
        Assert.Equal(instance, (await cluster.AgentClient().GetRuntimeAsync(AutoStarted, Ct))?.InstanceId);
        Assert.Single(await cluster.ModelContainersAsync(Ct), c => c.Model == AutoStarted);

        // D51: phi stopped by the operator, then the master boots while the agent is down.
        using (var stop = await cluster.Http.PostAsync($"/api/models/{AutoStarted}/stop", null, Ct))
        {
            Assert.True(stop.StatusCode == System.Net.HttpStatusCode.NoContent, Report(cluster, $"D51: stop answered {(int)stop.StatusCode} {await stop.Content.ReadAsStringAsync(Ct)}"));
        }

        await SettleAsync(cluster, "D51 phi stopped");
        int launches = await LaunchesAsync(cluster, AutoStarted);
        await cluster.StopMasterAsync();
        await cluster.StopAgentAsync(crash: false);
        await cluster.StartMasterAsync(Ct);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct); // auto-start runs and must wait: the node is not synchronized
        Assert.Equal(launches, await LaunchesAsync(cluster, AutoStarted));
        Assert.DoesNotContain(await cluster.ModelContainersAsync(Ct), c => c.Model == AutoStarted);

        await cluster.StartAgentAsync(Ct);
        await EventuallyAsync(cluster, async () => (await cluster.AgentClient().GetRuntimeAsync(AutoStarted, Ct))?.State == ModelState.Ready,
            "the waiting auto-start to start phi once the agent is back");
        await SettleAsync(cluster, "D51 master boot with the agent down");
        Assert.Equal(launches + 1, await LaunchesAsync(cluster, AutoStarted));
        Assert.Single(await cluster.ModelContainersAsync(Ct), c => c.Model == AutoStarted);
    }

    [Fact]
    public async Task AgentRestartsWithABundleAndTheMasterDownAndWithDockerDown()
    {
        await using var cluster = await ChaosCluster.StartAsync(4420, Ct);
        await SettleAsync(cluster, "initial");

        // D58: master down, a bootstrap bundle waiting, the enrolled agent restarts.
        string bundle = Path.Combine(cluster.BootstrapDirectory, BootstrapBundle.FileName);
        await cluster.StopMasterAsync();
        await cluster.StopAgentAsync(crash: true);
        Directory.CreateDirectory(cluster.BootstrapDirectory);
        await File.WriteAllTextAsync(bundle, JsonSerializer.Serialize(new BootstrapBundle
        {
            NodeId = ChaosCluster.NodeId,
            Token = "stale-bootstrap-token",
            CaCertPem = cluster.Node.Ca.CertificatePem,
            CaSpkiSha256 = cluster.Node.Ca.SpkiSha256,
            EnrollEndpoint = new Uri($"https://127.0.0.1:{TestPorts.Reserve()}"),
            CreatedAt = DateTimeOffset.UtcNow,
        }, AgentJson.Options), Ct);
        var serving = Stopwatch.StartNew();
        await cluster.StartAgentAsync(Ct, o => o.BootstrapWait = TimeSpan.FromSeconds(1));
        serving.Stop();
        Assert.True(serving.Elapsed < TimeSpan.FromSeconds(20), $"D58: the agent served after {serving.Elapsed.TotalSeconds:0.0} s");
        Assert.True(File.Exists(bundle), "D58: the bundle is kept for the next start");
        File.Delete(bundle);
        await cluster.StartMasterAsync(Ct);
        await SettleAsync(cluster, "D58 agent restart with a bundle and the master down");

        // D56: a launch interrupted by an agent crash, the agent starts while Docker is unreachable. StartsHeld fails every
        // instance's /health, so phi is stopped first (a Starting phi would rightly hold the node's one start slot).
        await cluster.WaitAutoStartedAsync(Ct);
        using (var stop = await cluster.Http.PostAsync($"/api/models/{AutoStarted}/stop", null, Ct))
        {
            Assert.True(stop.StatusCode == System.Net.HttpStatusCode.NoContent, Report(cluster, $"D56: stop phi answered {(int)stop.StatusCode}"));
        }

        await SettleAsync(cluster, "D56 phi stopped");
        cluster.Node.StartsHeld = true;
        using (var start = await cluster.Http.PostAsync($"/api/models/{Manual}/start", null, Ct))
        {
            Assert.True(start.StatusCode == System.Net.HttpStatusCode.OK, Report(cluster, $"D56: start answered {(int)start.StatusCode} {await start.Content.ReadAsStringAsync(Ct)}"));
        }

        var launch = (await cluster.MasterServices.GetRequiredService<IAgentOperationReferenceStore>().ListAsync(Ct))
            .Last(r => r.Action == OperationKinds.Launch && r.Target == Manual);
        Assert.Single(await cluster.ModelContainersAsync(Ct), c => c.Model == Manual);
        await cluster.StopAgentAsync(crash: true);
        cluster.SetDockerDown(true);
        await cluster.StartAgentAsync(Ct, o => o.DockerStartupTimeout = TimeSpan.FromSeconds(1));
        var interrupted = await cluster.AgentClient().GetOperationAsync(launch.OperationId, Ct);
        Assert.NotNull(interrupted);
        Assert.Equal(OperationState.Failed, interrupted.State);
        Assert.Equal(ErrorCodes.DockerUnavailable, interrupted.Error?.Code);

        cluster.SetDockerDown(false);
        cluster.Node.StartsHeld = false;
        using (var again = await cluster.Http.PostAsync($"/api/models/{Manual}/start", null, Ct))
        {
            Assert.True(again.StatusCode == System.Net.HttpStatusCode.OK, Report(cluster, $"D56: start after Docker returned answered {(int)again.StatusCode} {await again.Content.ReadAsStringAsync(Ct)}"));
        }

        await EventuallyAsync(cluster, async () => (await cluster.AgentClient().GetRuntimeAsync(Manual, Ct))?.State == ModelState.Ready,
            "qwen to become Ready after Docker returned");
        await SettleAsync(cluster, "D56 agent start with Docker unreachable");
        Assert.Single(await cluster.ModelContainersAsync(Ct), c => c.Model == Manual);
    }

    private static async Task<int> LaunchesAsync(ChaosCluster cluster, string modelId) =>
        (await cluster.MasterServices.GetRequiredService<IAgentOperationReferenceStore>().ListAsync(Ct))
            .Count(r => r.Action == OperationKinds.Launch && r.Target == modelId);

    private static async Task SettleAsync(ChaosCluster cluster, string step)
    {
        cluster.Note($"--- settle after: {step}");
        var violations = await ChaosRunner.SettleAsync(cluster, ChaosSettings.SettleTimeout, Ct);
        if (violations is not null)
        {
            Assert.Fail(ChaosRunner.Report(cluster, $"{step}: did not settle", plan: null, violations));
        }
    }

    private static async Task EventuallyAsync(ChaosCluster cluster, Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is AgentException or AgentUnreachableException)
            {
            }

            Assert.True(DateTime.UtcNow < deadline, Report(cluster, $"timed out waiting for {what}"));
            await Task.Delay(100, Ct);
        }
    }

    private static string Report(ChaosCluster cluster, string headline) => ChaosRunner.Report(cluster, headline, plan: null, violations: null);
}
