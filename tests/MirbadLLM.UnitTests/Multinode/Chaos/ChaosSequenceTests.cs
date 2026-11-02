using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MirbadLLM.Orchestration.Agents;

namespace MirbadLLM.UnitTests.Multinode.Chaos;

/// <summary>
/// Knobs: <c>MIRBADLLM_CHAOS_ITERATIONS</c> (plans per seed; default 2, a soak sets e.g. 50) and
/// <c>MIRBADLLM_CHAOS_SEED</c> (replaces the fixed seeds, to replay a failure).
/// </summary>
internal static class ChaosSettings
{
    public static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("MIRBADLLM_CHAOS_ITERATIONS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : 2;

    public static int Seed(int fixedSeed) =>
        int.TryParse(Environment.GetEnvironmentVariable("MIRBADLLM_CHAOS_SEED"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) ? s : fixedSeed;

    /// <summary>How long a healed system may take to satisfy every invariant (the master's reconnect backoff included).</summary>
    public static TimeSpan SettleTimeout => TimeSpan.FromSeconds(45);
}

/// <summary>
/// MN-044 (design §13.7): seeded, randomized fault sequences against the real master and the real agent
/// (<see cref="ChaosCluster"/>). Each plan mixes concurrent start/stop/restart/download/service-restart/backup-mount
/// operations with every fault type (partition, connection reset, lost responses, agent crash/restart, master restart,
/// Docker and hostd outages); once healed, the system must reach every <see cref="ChaosInvariants"/> invariant. The cluster
/// carries its state from one plan to the next.
/// </summary>
[Trait("Category", "Chaos")]
public sealed class ChaosSequenceTests
{
    private static readonly Action<ILogger, string, Exception?> Planted =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4400, "Planted"), "planted {Token}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(4401)]
    [InlineData(4402)]
    public async Task SeededFaultSequencesSettleToEveryInvariant(int fixedSeed)
    {
        int seed = ChaosSettings.Seed(fixedSeed);
        int iterations = ChaosSettings.Iterations;
        await using var cluster = await ChaosCluster.StartAsync(seed, Ct);
        await AssertSettledAsync(cluster, "the cluster did not settle before any fault", plan: null);

        for (int i = 0; i < iterations; i++)
        {
            var plan = ChaosPlan.Create(unchecked((seed * 1000) + i));
            cluster.Note($"=== iteration {i + 1}/{iterations}, plan seed {plan.Seed}");
            TestContext.Current.SendDiagnosticMessage($"chaos seed {seed} iteration {i + 1}/{iterations}: {plan}");
            try
            {
                await ChaosRunner.RunAsync(cluster, plan, Ct);
            }
            catch (TimeoutException)
            {
                Assert.Fail(ChaosRunner.Report(cluster, $"iteration {i + 1}: an operation never returned", plan, null));
            }

            await AssertSettledAsync(cluster, $"iteration {i + 1}/{iterations} (MIRBADLLM_CHAOS_ITERATIONS={i + 1}) did not settle", plan);
            TestContext.Current.SendDiagnosticMessage(string.Join(Environment.NewLine, cluster.Trace.SkipWhile(t => !t.Contains($"=== iteration {i + 1}/", StringComparison.Ordinal))));
        }

        // One clean download and mount, so the secrets provably travelled through both sides before the final scan.
        var clean = ChaosPlan.Clean();
        await ChaosRunner.RunAsync(cluster, clean, Ct);
        Assert.True(cluster.Downloads.TokensReceived > 0, "the HF token never reached the agent's Hugging Face side");
        Assert.True(cluster.Hostd.PasswordsReceived > 0, "the share password never reached hostd");
        Assert.True(cluster.AgentRequests.Accepted.Count > 0, "the recorder saw no accepted operation");
        await AssertSettledAsync(cluster, "the cluster did not settle after the clean download and mount", clean);
        TestContext.Current.SendDiagnosticMessage(
            $"chaos seed {seed}: {iterations} plans settled; {cluster.Faults.Dropped} responses dropped; {cluster.AgentRequests.Accepted.Count} accepted operations checked; {cluster.StaleHealthRefreshes} stale-health re-snapshots; {cluster.MasterLog.Count + cluster.AgentLog.Count} log entries scanned");

        // The checker itself can fail: a planted duplicate container and a planted secret are both reported.
        cluster.Node.Docker.AddModelContainer("planted", "running", port: 8190);
        cluster.Node.Docker.AddModelContainer("planted", "exited", port: 8191);
        Planted(cluster.MasterServices.GetRequiredService<ILoggerFactory>().CreateLogger("MirbadLLM.Chaos"), cluster.HfToken, null);
        var planted = await ChaosInvariants.CheckAsync(cluster, includeRouter: false, Ct);
        Assert.Contains(planted, v => v.StartsWith("duplicate model containers for 'planted'", StringComparison.Ordinal));
        Assert.Contains(planted, v => v.StartsWith("HF token in a master log entry", StringComparison.Ordinal));
    }

    private static async Task AssertSettledAsync(ChaosCluster cluster, string headline, ChaosPlan? plan)
    {
        var violations = await ChaosRunner.SettleAsync(cluster, ChaosSettings.SettleTimeout, Ct);
        if (violations is not null)
        {
            Assert.Fail(ChaosRunner.Report(cluster, headline, plan, violations));
        }

        // References may stay open only for operations the agent never received (checked above); count them for the log.
        var open = await cluster.MasterServices.GetRequiredService<IAgentOperationReferenceStore>().ListOpenAsync(Ct);
        cluster.Note($"open references (never reached the agent): {open.Count}");
    }
}
