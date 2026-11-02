using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Recovery;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

namespace MirbadLLM.UnitTests.Recovery;

/// <summary>Node settings in a configuration import, and what fleet recovery installs besides identity and models.</summary>
public sealed partial class RestoreModesTests
{
    [Fact]
    public async Task ConfigurationImport_AppliesNodeSettingsToTheSameUid_NeverItsTrust()
    {
        var fleet = await SourceFleetAsync();
        var (backup, secrets) = await RoundTripAsync(fleet.Service);
        var h = Import(Node("spark", DgxUid, pin: "pin-local") with { MemoryReserveGiB = 4, DisplayName = "Spark" });

        var preview = await h.Planner.PreviewAsync(backup, secrets);
        var dgx = preview.Nodes.Single(n => n.BackupNodeId == "dgx");
        Assert.Equal("spark", dgx.LocalNodeId);
        Assert.Contains("memory reserve 4 GiB → 8 GiB", dgx.SettingChanges);
        Assert.Contains("labels (none) → arm64", dgx.SettingChanges);
        Assert.Contains(dgx.SettingChanges, c => c.StartsWith("vLLM image digest (none) → sha256:", StringComparison.Ordinal));
        Assert.Null(preview.Nodes.Single(n => n.BackupNodeId == "pc-office").LocalNodeId);
        Assert.DoesNotContain(preview.Nodes, n => n.BackupNodeId == "laptop"); // revoked in the backup

        var job = await RunAsync(h.Service, new RestoreRequest(backup, secrets, [], true, false, false));

        var spark = (await h.Nodes.GetAsync("spark"))!;
        Assert.Equal((8.0, "arm64", "sha256:" + new string('a', 64)), (spark.MemoryReserveGiB, string.Join(",", spark.Labels), spark.DefaultVllmImageDigest));
        Assert.Null(spark.DisplayName);
        // Identity and trust stay as they are here.
        Assert.Equal(("spark", DgxUid, "pin-local", true, false), (spark.Id, spark.Uid, spark.CertSpkiSha256, spark.Approved, spark.Revoked));
        Assert.Equal("https://spark.lan:9443", spark.Address);
        Assert.Contains(job.Notes, n => n == "Node settings applied to 'spark'.");
        Assert.Contains(job.Notes, n => n.Contains("'pc-office' were not applied: no node here has the same uid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfigurationImport_NodeSettingsUnticked_LeavesTheNodeAsItIs()
    {
        var fleet = await SourceFleetAsync();
        var (backup, secrets) = await RoundTripAsync(fleet.Service);
        var h = Import(Node("spark", DgxUid) with { MemoryReserveGiB = 4 });

        await RunAsync(h.Service, new RestoreRequest(backup, secrets, [], true, false, false, ApplyNodeSettings: false));

        var spark = (await h.Nodes.GetAsync("spark"))!;
        Assert.Equal(4, spark.MemoryReserveGiB);
        Assert.Empty(spark.Labels);
    }

    [Fact]
    public void StorageComparison_FlagsAMissingVolume_ADifferentSize_AndMissingImages()
    {
        static NodeCapabilities Caps(long modelsBytes, params string[] images) => new()
        {
            Protocol = new ProtocolRange(1, 1),
            AgentVersion = "1.0.0",
            Os = new NodeOsInfo("linux", "Ubuntu"),
            Arch = "arm64",
            Memory = new NodeMemoryInfo(MemoryModel.Unified, 119.7),
            ModelRoots = modelsBytes == 0 ? [] : [new ModelRootInfo("models", 1, modelsBytes, 4)],
            Images = [.. images.Select(i => new ImageInfo { Ref = i })],
        };

        var (same, sameDiffers) = RestoreNodeSettings.CompareStorage(Caps(4_000_000_000_000, "vllm:1"), Caps(4_000_000_000_000, "vllm:1"));
        Assert.False(sameDiffers);
        Assert.Contains("4 TB with 4 folder(s) in the backup, 4 TB with 4 here.", Assert.Single(same), StringComparison.Ordinal);

        var (lines, differs) = RestoreNodeSettings.CompareStorage(Caps(4_000_000_000_000, "vllm:1", "llama:2"), Caps(1_000_000_000_000, "vllm:1"));
        Assert.True(differs);
        Assert.Contains(lines, l => l.EndsWith("(different size).", StringComparison.Ordinal));
        Assert.Contains("Images not on this node: llama:2.", lines);

        Assert.True(RestoreNodeSettings.CompareStorage(Caps(4_000_000_000_000), Caps(0)).Differs);
        Assert.False(RestoreNodeSettings.CompareStorage(null, Caps(1)).Differs);
    }

    [Fact]
    public async Task FleetRecovery_SeedsTheRunningIntent_AndInstallsHistoryAgentUpdatesAndTheShare()
    {
        var fleet = await SourceFleetAsync();
        var (backup, secrets) = await RoundTripAsync(fleet.Service);
        backup = backup with
        {
            RunningModels = new Dictionary<string, IReadOnlyList<string>> { ["dgx"] = ["alpha"], ["pc-office"] = ["beta"], ["laptop"] = ["gone"] },
            AgentUpdatesAutomatic = true,
            History = new BackupHistory(
                [new BackupRunFile("alpha", "20261001T030000Z", JsonDocument.Parse("""{"marker":"bench"}""").RootElement.Clone())],
                [],
                new Dictionary<string, double> { ["alpha"] = 140 }),
        };
        secrets = secrets! with
        {
            BackupDestination = new BackupDestinationRequest { Server = "nas", Share = "backups", Username = "u", Password = "share-secret" },
        };
        string data = Dir("fleet-everything");
        File.WriteAllBytes(Path.Combine(data, RecoveryOptions.MarkerFileName), []);
        var protection = new EphemeralDataProtectionProvider();
        var recovery = Own(new FleetRecovery(Data(data), Options.Create(new RecoveryOptions()), Options.Create(new MasterPkiOptions()),
            TimeProvider.System, NullLogger<FleetRecovery>.Instance, bootstrapNodeId: "dgx", protection: protection));

        var result = await recovery.InstallAsync(backup, secrets);

        Assert.Contains(result.Notes, n => n.StartsWith("2 model(s) that were running come back", StringComparison.Ordinal));
        // After the restart each node's first snapshot carries a real boot id: what it does not run is queued for recovery.
        var state = new ModelRecoveryState(Data(data));
        var dgx = new NodeLiveStatus { NodeId = "dgx", NodeUid = DgxUid, BootId = "boot-after-restore", State = NodeState.Online };
        state.Observe(dgx);
        Assert.Equal(["alpha"], state.Pending(dgx));
        Assert.False(state.RunningModels().ContainsKey("laptop")); // revoked: not seeded

        Assert.True(new AgentUpdateSettingsStore(Path.Combine(data, AgentUpdateSettingsStore.FileName)).Automatic);
        Assert.Contains("bench", File.ReadAllText(Path.Combine(data, "benchmarks", "alpha", "20261001T030000Z.json")), StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(140), new StartupTimeTracker(Data(data)).GetLast("alpha"));
        Assert.Equal("share-secret", new BackupDestinationSecretStore(data, protection).Load()!.Password);
        Assert.DoesNotContain("share-secret", File.ReadAllText(Path.Combine(data, BackupDestinationSecretStore.FileName)), StringComparison.Ordinal);
        Assert.DoesNotContain(result.Notes, n => n.Contains("share-secret", StringComparison.Ordinal));
    }
}
