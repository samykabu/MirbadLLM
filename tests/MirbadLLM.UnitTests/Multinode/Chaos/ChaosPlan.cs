using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;
using MirbadLLM.UnitTests.Multinode.Contract;

namespace MirbadLLM.UnitTests.Multinode.Chaos;

/// <summary>Operations a chaos plan issues through the master's admin API (or its services, for the backup mount).</summary>
internal enum ChaosOp
{
    Start,
    Stop,
    Restart,
    Download,
    ServiceRestart,
    BackupMount,
}

/// <summary>Faults a chaos plan injects.</summary>
internal enum ChaosFault
{
    /// <summary>Master ↔ agent traffic stalls for a window (new connections hang in the handshake); healed by resuming or resetting.</summary>
    Partition,

    /// <summary>Every master ↔ agent connection is reset (in-flight requests and responses are lost).</summary>
    ConnectionReset,

    /// <summary>The next N mutation responses are lost after the agent answered them.</summary>
    DropResponses,

    /// <summary>The agent stops abruptly (or gracefully) and starts again over the same state directory.</summary>
    AgentRestart,

    /// <summary>The master stops and starts again over the same data folder.</summary>
    MasterRestart,

    /// <summary>The Docker engine (and the download helper) is unreachable for a window.</summary>
    DockerOutage,

    /// <summary>hostd is unreachable (or hangs without answering) for a window.</summary>
    HostdOutage,
}

/// <summary>One step of a plan: an operation started in the background, or a fault (windows heal in the background).</summary>
internal sealed record ChaosStep(int DelayMs, ChaosOp? Op = null, string? Target = null, ChaosFault? Fault = null, int DurationMs = 0, int Count = 0, bool Flag = false)
{
    public override string ToString()
    {
        string head = string.Create(CultureInfo.InvariantCulture, $"+{DelayMs}ms ");
        if (Op is { } op)
        {
            return head + $"op {op}" + (Target is null ? "" : $" {Target}");
        }

        return head + Fault switch
        {
            ChaosFault.Partition => $"partition {DurationMs}ms, heal by {(Flag ? "reset" : "resume")}",
            ChaosFault.ConnectionReset => "reset connections",
            ChaosFault.DropResponses => $"drop next {Count} mutation responses",
            ChaosFault.AgentRestart => $"agent {(Flag ? "crash" : "graceful stop")}, down {DurationMs}ms",
            ChaosFault.MasterRestart => $"master restart, down {DurationMs}ms",
            ChaosFault.DockerOutage => $"docker outage {DurationMs}ms",
            ChaosFault.HostdOutage => $"hostd {(Flag ? "hanging" : "down")} {DurationMs}ms",
            _ => "?",
        };
    }
}

/// <summary>
/// A seeded fault sequence: the same seed always yields the same steps. Every plan contains every fault type at least
/// once and at least one download (HF token) and one backup mount (share password), so the secret invariant is never
/// vacuous; the rest is random operations and extra faults.
/// </summary>
internal sealed class ChaosPlan
{
    private static readonly string[] ModelIds = [.. ChaosCluster.Models.Select(m => m.Id)];

    private ChaosPlan(int seed, List<ChaosStep> steps)
    {
        Seed = seed;
        Steps = steps;
    }

    public int Seed { get; }

    public IReadOnlyList<ChaosStep> Steps { get; }

    public static ChaosPlan Create(int seed)
    {
        var random = new Random(seed);
        var faults = Enum.GetValues<ChaosFault>().ToList();
        var steps = new List<ChaosStep>();
        foreach (var fault in faults)
        {
            steps.Add(FaultStep(random, fault));
        }

        steps.Add(OpStep(random, ChaosOp.Download));
        steps.Add(OpStep(random, ChaosOp.BackupMount));
        // Extra faults are the cheap ones (restarts cost seconds each and every plan already has one of each).
        var cheap = faults.Where(f => f is not (ChaosFault.MasterRestart or ChaosFault.AgentRestart)).ToList();
        int extra = random.Next(6, 10);
        for (int i = 0; i < extra; i++)
        {
            steps.Add(random.Next(100) < 70 ? OpStep(random, PickOp(random)) : FaultStep(random, cheap[random.Next(cheap.Count)]));
        }

        // Fisher-Yates with the same random: the order is part of the seed.
        for (int i = steps.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (steps[i], steps[j]) = (steps[j], steps[i]);
        }

        return new ChaosPlan(seed, steps);
    }

    /// <summary>A download and a backup mount with no fault.</summary>
    public static ChaosPlan Clean() => new(0, [new ChaosStep(0, Op: ChaosOp.Download), new ChaosStep(0, Op: ChaosOp.BackupMount)]);

    public override string ToString() =>
        $"plan seed {Seed}:" + Environment.NewLine + string.Join(Environment.NewLine, Steps.Select((s, i) => $"  {i,2}. {s}"));

    private static ChaosOp PickOp(Random random) => random.Next(100) switch
    {
        < 25 => ChaosOp.Start,
        < 45 => ChaosOp.Stop,
        < 60 => ChaosOp.Restart,
        < 75 => ChaosOp.Download,
        < 88 => ChaosOp.ServiceRestart,
        _ => ChaosOp.BackupMount,
    };

    private static ChaosStep OpStep(Random random, ChaosOp op) =>
        new(random.Next(20, 250), Op: op, Target: op is ChaosOp.Start or ChaosOp.Stop or ChaosOp.Restart ? ModelIds[random.Next(ModelIds.Length)] : null);

    private static ChaosStep FaultStep(Random random, ChaosFault fault) => fault switch
    {
        ChaosFault.Partition => new(random.Next(20, 250), Fault: fault, DurationMs: random.Next(300, 1500), Flag: random.Next(2) == 0),
        ChaosFault.ConnectionReset => new(random.Next(20, 250), Fault: fault),
        ChaosFault.DropResponses => new(random.Next(20, 250), Fault: fault, Count: random.Next(1, 3)),
        ChaosFault.AgentRestart => new(random.Next(20, 250), Fault: fault, DurationMs: random.Next(0, 800), Flag: random.Next(4) != 0),
        ChaosFault.MasterRestart => new(random.Next(20, 250), Fault: fault, DurationMs: random.Next(0, 800)),
        ChaosFault.DockerOutage => new(random.Next(20, 250), Fault: fault, DurationMs: random.Next(300, 1500)),
        ChaosFault.HostdOutage => new(random.Next(20, 250), Fault: fault, DurationMs: random.Next(300, 1500), Flag: random.Next(2) == 0),
        _ => throw new ArgumentOutOfRangeException(nameof(fault)),
    };
}

/// <summary>Executes a <see cref="ChaosPlan"/> against a <see cref="ChaosCluster"/>, then heals everything.</summary>
internal static class ChaosRunner
{
    /// <summary>
    /// How long one issued operation may take before <see cref="IssueAsync"/> records "no answer within …" and its task
    /// completes. Exceeding it is <b>not</b> a violation: the chaos invariants are about the cluster settling, not about
    /// latency under faults.
    /// </summary>
    public static readonly TimeSpan OperationCeiling = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long <see cref="RunAsync"/> waits for every issued operation to come back before it reports "an operation
    /// never returned".
    /// <para>It must stay comfortably above <see cref="OperationCeiling"/> plus the plan's own step offsets, or a loaded
    /// machine produces that violation for an operation that <i>did</i> return — at its ceiling, on time. It was 60 s
    /// against a 45 s ceiling, i.e. about 14 s of slack once a plan's offsets are counted, which is what
    /// <c>SeededFaultSequencesSettleToEveryInvariant(4402)</c> was failing on: once in the MN-219 sweep, and again in
    /// MN-239's full Linux run, each time only inside the full suite and never in 5 runs of the class alone (D129 (6),
    /// diagnosed in MN-239). The relationship is pinned by
    /// <c>ChaosFindingTests.TheChaosPlanBudgetStaysAboveItsOwnOperationCeiling</c>.</para>
    /// </summary>
    public static readonly TimeSpan PlanCeiling = OperationCeiling + TimeSpan.FromSeconds(45);

    /// <summary>The margin <see cref="PlanCeiling"/> must keep over <see cref="OperationCeiling"/>.</summary>
    public static readonly TimeSpan PlanMargin = TimeSpan.FromSeconds(30);

    public static async Task RunAsync(ChaosCluster cluster, ChaosPlan plan, CancellationToken ct)
    {
        var background = new List<Task>();
        int index = 0;
        foreach (var step in plan.Steps)
        {
            await Task.Delay(step.DelayMs, ct);
            int n = index++;
            if (step.Op is { } op)
            {
                cluster.Note($"#{n} issue {op} {step.Target}");
                background.Add(Task.Run(() => IssueAsync(cluster, n, op, step.Target, ct), ct));
                continue;
            }

            cluster.Note($"#{n} fault {step}");
            switch (step.Fault)
            {
                case ChaosFault.Partition:
                    cluster.Proxy.Partition();
                    background.Add(HealLaterAsync(step.DurationMs, () =>
                    {
                        cluster.Proxy.Heal(step.Flag);
                        cluster.Note($"#{n} partition healed ({(step.Flag ? "reset" : "resumed")})");
                    }, ct));
                    break;
                case ChaosFault.ConnectionReset:
                    cluster.Proxy.ResetAll();
                    break;
                case ChaosFault.DropResponses:
                    cluster.Faults.DropNextMutationResponses(step.Count);
                    break;
                case ChaosFault.AgentRestart:
                    await cluster.StopAgentAsync(crash: step.Flag);
                    await Task.Delay(step.DurationMs, ct);
                    await cluster.StartAgentAsync(ct);
                    break;
                case ChaosFault.MasterRestart:
                    await cluster.StopMasterAsync();
                    await Task.Delay(step.DurationMs, ct);
                    await cluster.StartMasterAsync(ct);
                    break;
                case ChaosFault.DockerOutage:
                    cluster.SetDockerDown(true);
                    background.Add(HealLaterAsync(step.DurationMs, () =>
                    {
                        cluster.SetDockerDown(false);
                        cluster.Note($"#{n} docker back");
                    }, ct));
                    break;
                case ChaosFault.HostdOutage:
                    cluster.Hostd.Mode = step.Flag ? HostdMode.Hanging : HostdMode.Down;
                    background.Add(HealLaterAsync(step.DurationMs, () =>
                    {
                        cluster.Hostd.Mode = HostdMode.Up;
                        cluster.Note($"#{n} hostd back");
                    }, ct));
                    break;
            }
        }

        // Heal: windows end, both sides run, and every operation the plan issued has returned.
        var all = Task.WhenAll(background);
        try
        {
            await all.WaitAsync(PlanCeiling, ct);
        }
        catch (TimeoutException)
        {
            cluster.Note($"VIOLATION: an issued operation did not return within {PlanCeiling.TotalSeconds:0} s of its plan ending");
            throw;
        }

        cluster.Faults.ClearDrops();
        cluster.Proxy.Heal(reset: false);
        cluster.SetDockerDown(false);
        cluster.Hostd.Mode = HostdMode.Up;
        await cluster.StartAgentAsync(ct);
        await cluster.StartMasterAsync(ct);
        cluster.Note($"plan done (responses dropped so far: {cluster.Faults.Dropped})");
    }

    /// <summary>Polls the invariants until they hold; null when they do, else the last violations.</summary>
    public static async Task<List<string>?> SettleAsync(ChaosCluster cluster, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        List<string> last;
        while (true)
        {
            last = await ChaosInvariants.CheckAsync(cluster, includeRouter: true, ct);
            if (last.Count == 0)
            {
                cluster.Note("settled: every invariant holds");
                return null;
            }

            if (last.All(v => v.StartsWith(ChaosInvariants.StaleHealth, StringComparison.Ordinal)))
            {
                // Known finding (ChaosFindingTests, skipped): health reaches the master only in snapshots. Everything else
                // holds, so take the snapshot the UI's refresh would take, and keep checking every other invariant.
                cluster.StaleHealthRefreshes++;
                cluster.Note("workaround for the stale-health finding: re-snapshot dgx (INodeStatusCache.RefreshNodeAsync)");
                await cluster.MasterServices.GetRequiredService<INodeStatusCache>().RefreshNodeAsync(ChaosCluster.NodeId, ct);
                continue;
            }

            if (DateTime.UtcNow > deadline)
            {
                return last;
            }

            await Task.Delay(200, ct);
        }
    }

    public static string Report(ChaosCluster cluster, string headline, ChaosPlan? plan, IEnumerable<string>? violations) =>
        string.Join(Environment.NewLine, new[]
        {
            headline,
            $"seed {cluster.Seed}, plan seed {plan?.Seed.ToString(CultureInfo.InvariantCulture) ?? "-"} (rerun: MIRBADLLM_CHAOS_SEED={cluster.Seed}, iterations as in the headline)",
            plan?.ToString() ?? "",
            "violations:",
            string.Join(Environment.NewLine, (violations ?? []).Select(v => "  - " + v)),
            "trace:",
            string.Join(Environment.NewLine, cluster.Trace.TakeLast(80).Select(t => "  " + t)),
            "last warnings:",
            string.Join(Environment.NewLine, cluster.MasterLog.Entries.Concat(cluster.AgentLog.Entries)
                .Where(e => e.StartsWith("Warning", StringComparison.Ordinal) || e.StartsWith("Error", StringComparison.Ordinal) || e.StartsWith("Critical", StringComparison.Ordinal))
                .TakeLast(25).Select(e => "  " + (e.Length > 400 ? e[..400] + "…" : e))),
            "errors (full):",
            string.Join(Environment.NewLine, cluster.MasterLog.Entries.Select(e => "  [master] " + e).Concat(cluster.AgentLog.Entries.Select(e => "  [agent] " + e))
                .Where(e => e.Contains("] Error ", StringComparison.Ordinal) || e.Contains("] Critical ", StringComparison.Ordinal))
                .Take(6).Select(e => e.Length > 3000 ? e[..3000] + "…" : e)),
        });

    private static async Task HealLaterAsync(int ms, Action heal, CancellationToken ct)
    {
        try
        {
            await Task.Delay(ms, ct);
        }
        finally
        {
            heal();
        }
    }

    private static async Task IssueAsync(ChaosCluster cluster, int n, ChaosOp op, string? target, CancellationToken ct)
    {
        HttpClient http;
        IServiceProvider services;
        try
        {
            http = cluster.Http;
            services = cluster.MasterServices;
        }
        catch (InvalidOperationException)
        {
            cluster.Note($"#{n} {op} {target}: master down, not sent");
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(OperationCeiling);
        try
        {
            string outcome = op switch
            {
                ChaosOp.Start => await PostAsync(http, $"/api/models/{target}/start", null, timeout.Token),
                ChaosOp.Stop => await PostAsync(http, $"/api/models/{target}/stop", null, timeout.Token),
                ChaosOp.Restart => await PostAsync(http, $"/api/models/{target}/restart", null, timeout.Token),
                ChaosOp.Download => await PostAsync(http, "/api/downloads",
                    JsonContent.Create(new { repoId = AgentContractSeed.DownloadRepo, token = cluster.HfToken, folder = $"chaos-dl-{cluster.NextSerial()}" }), timeout.Token),
                ChaosOp.ServiceRestart => await PostAsync(http, $"/api/services/{AgentContractSeed.Service}/restart", null, timeout.Token),
                ChaosOp.BackupMount => await MountAsync(services, cluster.SharePassword, timeout.Token),
                _ => "?",
            };
            cluster.Note($"#{n} {op} {target} -> {outcome}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Either no answer within the bound, or the master stopped under the request (its client was disposed).
            cluster.Note($"#{n} {op} {target} -> {(timeout.IsCancellationRequested ? $"no answer within {OperationCeiling.TotalSeconds:0} s" : "cancelled (master stopped)")}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            cluster.Note($"#{n} {op} {target} -> {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<string> PostAsync(HttpClient http, string path, HttpContent? body, CancellationToken ct)
    {
        using var response = await http.PostAsync(path, body, ct);
        string text = await response.Content.ReadAsStringAsync(ct);
        return $"{(int)response.StatusCode} {(text.Length > 160 ? text[..160] + "…" : text)}";
    }

    private static async Task<string> MountAsync(IServiceProvider services, string password, CancellationToken ct)
    {
        var mount = await services.GetRequiredService<IHostAgentBackupMountClient>().SetBackupMountAsync(new HostAgentBackupMountRequest
        {
            Type = "smb",
            Server = "nas.chaos",
            Share = "backups",
            Username = "chaos",
            Password = password,
        }, ct);
        return $"mounted={mount.Mounted}";
    }
}
