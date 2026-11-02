using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.UnitTests.Orchestration;

#pragma warning disable CA1707 // underscore test names.

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-029 (design §5.1 "two fingerprints", D26): the legacy config hash (<see cref="ModelConfigHash"/>) of every live
/// definition is exactly what today's code computes — golden constants recorded once from the unchanged code — before
/// and after the MN-006 v1 → v2 migration, and adding the execution fingerprint marks no live model restart-pending.
/// </summary>
public sealed class LegacyHashGoldenTests : IDisposable
{
    private const string Image = LiveDefinitions.Image;
    private const double TotalGiB = LiveDefinitions.TotalGiB;
    private const string DgxNodeUid = "8f0c2a4e-dgx-node-uid";

    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = true };

    /// <summary>Golden legacy hashes of <see cref="LiveDefinitions"/> (today's <see cref="ModelConfigHash"/>, recorded once).</summary>
    private static readonly Dictionary<string, string> LiveDefinitionGolden = new(StringComparer.Ordinal)
    {
        ["qwen36-35b-a3b-nvfp4"] = "6b9064a9c15081fb",
        ["nemotron-3-5"] = "a3ec54f3f162246f",
        ["qwen3-embedding"] = "775edf47c2192c64",
    };

    /// <summary>Golden legacy hashes of the MN-006 live-shaped <c>models.live-v1.json</c> (today's view, recorded once).</summary>
    private static readonly Dictionary<string, string> LiveFixtureGolden = new(StringComparer.Ordinal)
    {
        ["qwen36-35b-a3b-nvfp4"] = "ed516902a95a3319",
        ["nemotron-3-5"] = "a3ec54f3f162246f",
        ["qwen3-embedding-4b"] = "09d742bc33a42a54",
        ["qwen3-vl-embedding-2b"] = "d5149e266014d92f",
        ["ov-intent-analysis"] = "f83dce031d87e81f",
        ["qwen38-27b-nvfp4"] = "632596a126fc5937",
        ["qwen3-embedding"] = "775edf47c2192c64",
        ["ornith-9b-fp8"] = "356bc1da7d9161cf",
    };

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "mirbadllm-mn029-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    public static TheoryData<string> LiveDefinitionIds => [.. LiveDefinitionGolden.Keys];

    public static TheoryData<string> LiveFixtureIds => [.. LiveFixtureGolden.Keys];

    private static IEnumerable<ModelDefinition> LiveDefinitionModels => [LiveDefinitions.Qwen35B, LiveDefinitions.Nemotron, LiveDefinitions.Embedding];

    private static string LiveFixture([CallerFilePath] string sourceFile = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures", "models.live-v1.json"));

    /// <summary>The definitions exactly as today's registry returns them from the v1 file (deserialize + normalize).</summary>
    private static List<ModelDefinition> TodaysView() =>
        JsonSerializer.Deserialize<List<ModelDefinition>>(LiveFixture(), FileJson)!
            .Select(VllmArgsNormalizer.Apply)
            .ToList();

    private static string LegacyHash(ModelDefinition model) => ModelConfigHash.Compute(model, Image, TotalGiB);

    /// <summary>The P1 launch spec of a DGX model: today's container shape (all GPUs, models ro + cache rw, ipc host).</summary>
    private static LaunchSpec DgxSpec(ModelDefinition model) => new()
    {
        Kind = LaunchDelivery.Container,
        Runtime = RuntimeKind.Vllm,
        Image = string.IsNullOrEmpty(model.Image) ? Image : model.Image,
        Argv = VllmArgsBuilder.Build(model, TotalGiB),
        Mounts =
        [
            new MountSpec(LogicalVolumes.Models, null, LogicalVolumes.ModelsTarget, ReadOnly: true),
            new MountSpec(LogicalVolumes.Cache, null, LogicalVolumes.CacheTarget, ReadOnly: false),
        ],
        Gpus = new GpuSelection { Mode = GpuSelectionMode.All },
        IpcHost = true,
    };

    private static string Fingerprint(ModelDefinition model) =>
        ExecutionFingerprint.Compute(DgxSpec(model), DgxNodeUid, [], LegacyHash(model));

    // ------------------------------------------------------------ golden legacy hashes

    [Theory]
    [MemberData(nameof(LiveDefinitionIds))]
    public void LiveDefinitions_LegacyHash_EqualsGolden(string id)
    {
        var model = LiveDefinitionModels.Single(m => m.Id == id);
        Assert.Equal(LiveDefinitionGolden[id], LegacyHash(model));
    }

    [Theory]
    [MemberData(nameof(LiveFixtureIds))]
    public void LiveFixture_LegacyHash_EqualsGolden(string id)
    {
        var model = TodaysView().Single(m => m.Id == id);
        Assert.Equal(LiveFixtureGolden[id], LegacyHash(model));
    }

    [Fact]
    public void Golden_CoversEveryLiveModel()
    {
        Assert.Equal(TodaysView().Select(m => m.Id).Order(StringComparer.Ordinal), LiveFixtureGolden.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(LiveDefinitionModels.Select(m => m.Id).Order(StringComparer.Ordinal), LiveDefinitionGolden.Keys.Order(StringComparer.Ordinal));
        Assert.All(LiveFixtureGolden.Values.Concat(LiveDefinitionGolden.Values), h => Assert.Matches("^[0-9a-f]{16}$", h));
    }

    [Fact]
    public void LiveDefinitions_RunningContainers_GetTodaysVerdict()
    {
        // The running containers were launched by the pre-redesign builder; today's Cmd recheck canonicalizes them.
        // Today, only the embedding model is restart-pending (EnablePrefixCaching=false now emits an explicit
        // --no-enable-prefix-caching, CanonicalConfigTests); the fingerprint (absent on these containers) adds nothing.
        var todaysVerdict = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [LiveDefinitions.Qwen35B.Id] = false,
            [LiveDefinitions.Nemotron.Id] = false,
            [LiveDefinitions.Embedding.Id] = true,
        };

        foreach (var model in LiveDefinitionModels)
        {
            string recheck = ModelConfigHash.Compute(Image, LiveDefinitions.LegacyBuild(model, TotalGiB));
            var legacy = new HashPair(LegacyHash(model), recheck);
            Assert.Equal(todaysVerdict[model.Id], legacy.Differs);
            Assert.Equal(
                todaysVerdict[model.Id],
                RestartPendingRule.IsPending(ModelState.Ready, tuning: false, legacy, new HashPair(Fingerprint(model), null)));
            Assert.Equal(
                todaysVerdict[model.Id] ? RestartPendingReasons.LegacyConfigHash : RestartPendingReasons.None,
                RestartPendingRule.Evaluate(legacy, new HashPair(Fingerprint(model), null)));
        }
    }

    // ------------------------------------------------------------ migration (MN-006 live-shaped fixture)

    [Fact]
    public async Task Migration_KeepsEveryLegacyHash_AndNoModelBecomesRestartPending()
    {
        Directory.CreateDirectory(_dataDir);
        await File.WriteAllBytesAsync(Path.Combine(_dataDir, ModelRegistryDocument.FileName), Encoding.UTF8.GetBytes(LiveFixture()), Ct);
        using var registry = new JsonModelRegistry(
            Options.Create(new MirbadLLMOptions { DataPath = _dataDir }),
            Options.Create(new RegistryMigrationOptions()));

        var migrated = await registry.ListAsync(Ct);

        Assert.Equal(LiveFixtureGolden.Count, migrated.Count);
        foreach (var model in migrated)
        {
            string golden = LiveFixtureGolden[model.Id];
            string expectedLegacy = LegacyHash(model);
            Assert.Equal(golden, expectedLegacy);

            // Today's container: carries the legacy label computed by today's code, and no fingerprint label.
            var todaysContainer = new HashPair(expectedLegacy, golden);
            var noFingerprintLabel = new HashPair(Fingerprint(model), null);
            Assert.Equal(RestartPendingReasons.None, RestartPendingRule.Evaluate(todaysContainer, noFingerprintLabel));
            Assert.False(RestartPendingRule.IsPending(ModelState.Ready, tuning: false, todaysContainer, noFingerprintLabel), model.Id);
            Assert.False(RestartPendingRule.IsPending(ModelState.Starting, tuning: false, todaysContainer, noFingerprintLabel), model.Id);

            // Relaunched by P1 (fingerprint label set from the same definition): still not pending.
            var relaunched = new HashPair(Fingerprint(model), Fingerprint(model));
            Assert.False(RestartPendingRule.IsPending(ModelState.Ready, tuning: false, todaysContainer, relaunched), model.Id);
        }
    }

    [Fact]
    public void Fingerprint_OfLiveModels_IsStableAndDistinct()
    {
        var view = TodaysView();
        var fingerprints = view.Select(Fingerprint).ToList();

        Assert.Equal(fingerprints, view.Select(Fingerprint));
        Assert.Equal(view.Count, fingerprints.Distinct(StringComparer.Ordinal).Count());
        Assert.All(fingerprints, f => Assert.Matches("^[0-9a-f]{16}$", f));
    }

    [Fact]
    public void LiveModel_MovedToAnotherGpu_OrRuntime_IsRestartPending_WithItsLegacyHashUnchanged()
    {
        var model = TodaysView().First();
        string legacy = LegacyHash(model);
        string running = ExecutionFingerprint.Compute(DgxSpec(model) with { Gpus = new GpuSelection { Mode = GpuSelectionMode.Uuids, Uuids = ["GPU-aaaa"] } }, DgxNodeUid, ["GPU-aaaa"], legacy);
        string saved = ExecutionFingerprint.Compute(DgxSpec(model) with { Gpus = new GpuSelection { Mode = GpuSelectionMode.Uuids, Uuids = ["GPU-bbbb"] } }, DgxNodeUid, ["GPU-bbbb"], legacy);

        Assert.Equal(
            RestartPendingReasons.ExecutionFingerprint,
            RestartPendingRule.Evaluate(new HashPair(legacy, legacy), new HashPair(saved, running)));
        Assert.True(RestartPendingRule.IsPending(ModelState.Ready, tuning: false, new HashPair(legacy, legacy), new HashPair(saved, running)));

        string process = ExecutionFingerprint.Compute(DgxSpec(model) with { Kind = LaunchDelivery.Process, Executable = "vllm" }, DgxNodeUid, [], legacy);
        Assert.True(RestartPendingRule.IsPending(ModelState.Ready, tuning: false, new HashPair(legacy, legacy), new HashPair(process, Fingerprint(model))));
    }
}
