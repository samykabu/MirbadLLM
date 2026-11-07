using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Contracts;

/// <summary>
/// The P2 contract delta (MN-101, design D73): golden fixtures for the master <c>/api</c> DTOs (web options, as
/// <c>Program.cs</c> configures them), the presence of a fixture for every P2 wire and file-format type (those round-trip
/// in <see cref="GoldenJsonTests"/>), and the compatibility rules: every P2 field is omitted at its default, so a P2 master
/// talking to a P1 agent sends what P1 sent, and P1 files stay byte-identical.
/// </summary>
public sealed class P2ContractTests
{
    /// <summary>The master /api's options: web defaults + <see cref="JsonStringEnumConverter"/> (PascalCase enums), as in Program.cs.</summary>
    private static readonly JsonSerializerOptions Api = CreateApi();

    private static readonly JsonSerializerOptions ApiStrict = new(Api) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    private static readonly JsonSerializerOptions WireStrict = new(AgentJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>P2 types on the agent wire or in master files; each must have <c>Fixtures/{Name}.json</c> (round-tripped by <see cref="GoldenJsonTests"/>).</summary>
    private static readonly Type[] WireAndFileTypes =
    [
        typeof(RekeyRequest), typeof(AdmissionRequest), typeof(GpuSample), typeof(NodeRecord), typeof(JoinTokenRecord),
        typeof(OwnershipRecord), typeof(OwnershipDocument), typeof(AuditRecord), typeof(BenchmarkProvenance),
        typeof(MasterTrustDocument), typeof(RestoreTarget),
    ];

    /// <summary>P2 master /api DTOs; each must have <c>Fixtures/Api/{Name}.json</c>.</summary>
    private static readonly Type[] ApiTypes =
    [
        typeof(JoinTokenCreateRequest), typeof(JoinTokenCreateResponse), typeof(NodeApproveRequest), typeof(NodePatchRequest),
        typeof(NodeRevokeRequest), typeof(NodeRevokeResult), typeof(NodeView), typeof(NodeJobSummary), typeof(NodeFitRequest),
        typeof(NodeFitResult), typeof(PlacementProblem), typeof(ModelMoveRequest), typeof(ModelMoveForceCommitRequest),
        typeof(ModelUnplaceRequest), typeof(TakeoverRequest), typeof(TakeoverContinueRequest), typeof(FencingStatus),
    ];

    public static TheoryData<string> ApiFixtureNames() => [.. ApiTypes.Select(t => t.Name)];

    [Fact]
    public void EveryP2WireAndFileType_HasAGoldenFixture()
    {
        var missing = WireAndFileTypes.Where(t => !File.Exists(Path.Combine(FixturesPath(), t.Name + ".json"))).Select(t => t.Name).ToList();
        Assert.True(missing.Count == 0, "P2 types without a golden fixture: " + string.Join(", ", missing));
    }

    [Theory]
    [MemberData(nameof(ApiFixtureNames))]
    public void ApiFixture_RoundTripsExactly_WithTheMasterApiOptions(string typeName)
    {
        Type type = ApiTypes.Single(t => t.Name == typeName);
        string path = Path.Combine(FixturesPath(), "Api", typeName + ".json");
        Assert.True(File.Exists(path), $"Missing /api golden fixture: {path}");
        var fixture = JsonNode.Parse(File.ReadAllText(path))!;

        object? value;
        try
        {
            value = fixture.Deserialize(type, ApiStrict);
        }
        catch (JsonException ex)
        {
            Assert.Fail($"Api/{typeName}.json does not match the DTO: {ex.Message}");
            return;
        }

        var output = JsonSerializer.SerializeToNode(value, type, Api)!;
        Assert.True(SameJson(fixture, output), $"Api/{typeName}.json does not round-trip.\nfixture: {fixture.ToJsonString()}\noutput:  {output.ToJsonString()}");
    }

    // ---------------------------------------------------------------- compatibility: P2 fields omitted at default

    [Fact]
    public void P1ShapedAdmissionAndSamples_SerializeWithoutP2Keys()
    {
        var admission = JsonSerializer.SerializeToNode(new AdmissionRequest { BudgetGiB = 36, ReserveGiB = 8 }, AgentJson.Options)!.AsObject();
        Assert.False(admission.ContainsKey("gpuReservesGiB"));

        var sample = JsonSerializer.SerializeToNode(new GpuSample { Index = 0 }, AgentJson.Options)!.AsObject();
        Assert.False(sample.ContainsKey("memoryFreeGiB"));
        Assert.False(sample.ContainsKey("name"));

        var node = JsonSerializer.SerializeToNode(
            new NodeRecord { Id = "dgx", Uid = "u", Address = "https://127.0.0.1:9443" }, MasterStateJson.Options)!.AsObject();
        Assert.False(node.ContainsKey("defaultVllmImageDigest"));
        Assert.False(node.ContainsKey("defaultLlamaCppImage"));
        Assert.False(node.ContainsKey("defaultLlamaCppImageDigest"));

        var host = JsonSerializer.SerializeToNode(new HostSample(DateTimeOffset.UnixEpoch, new MemorySnapshot(128, 90), null))!.AsObject();
        Assert.False(host.ContainsKey("Gpus"));

        var maintenance = new MaintenanceState("j", "benchmark", "m", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        Assert.Null(maintenance.NodeId); // positional P1 construction still compiles and means "global"
    }

    [Fact]
    public void PlacementGeneration_IsOmittedAtZero_WrittenOtherwise_AndNotPartOfEquality()
    {
        var registryOptions = new JsonSerializerOptions { WriteIndented = false };
        Assert.Equal("""{"NodeId":"dgx"}""", JsonSerializer.Serialize(new ModelPlacement("dgx"), registryOptions));

        var moved = new ModelPlacement("pc-office", ["GPU-a"], [0], Generation: 4);
        string json = JsonSerializer.Serialize(moved, registryOptions);
        Assert.Contains("\"Generation\":4", json, StringComparison.Ordinal);
        Assert.Equal(4, JsonSerializer.Deserialize<ModelPlacement>(json, registryOptions)!.Generation);

        Assert.Equal(moved, moved with { Generation = 9 });
        Assert.True(moved.SameLocation(new ModelPlacement("pc-office", ["GPU-a"], [3])));
        Assert.False(moved.SameLocation(new ModelPlacement("pc-office", ["GPU-b"])));
        Assert.False(moved.SameLocation(null));
    }

    [Fact]
    public void ModelDefinitionChange_DetectsPlacementChanges()
    {
        var stored = new ModelDefinition { Id = "m", ServedName = "m", ModelPath = "m", Placement = new ModelPlacement("dgx") };
        Assert.False(new ModelDefinitionChange(stored, stored with { MaxNumSeqs = 4 }).PlacementChanges);
        Assert.False(new ModelDefinitionChange(stored, stored with { Placement = new ModelPlacement("dgx", Generation: 2) }).PlacementChanges);
        Assert.True(new ModelDefinitionChange(stored, stored with { Placement = new ModelPlacement("pc") }).PlacementChanges);
        Assert.True(new ModelDefinitionChange(stored, stored with { Placement = null }).PlacementChanges);
        Assert.False(new ModelDefinitionChange(null, stored).PlacementChanges);
        Assert.True(new ModelDefinitionChange(stored, null).IsDelete);
        Assert.Equal("m", new ModelDefinitionChange(stored, null).ModelId);
    }

    [Fact]
    public void AdmissionRequest_ReserveForGpu_PrefersTheMapThenTheDefault()
    {
        var admission = new AdmissionRequest
        {
            BudgetGiB = 24, ReserveGiB = 4, GpuReserveGiB = 0.5, GpuReservesGiB = new Dictionary<string, double> { ["GPU-a"] = 1 },
        };
        Assert.Equal(1, admission.ReserveForGpu("GPU-a"));
        Assert.Equal(0.5, admission.ReserveForGpu("GPU-b"));
        Assert.Equal(0, new AdmissionRequest { BudgetGiB = 1, ReserveGiB = 8 }.ReserveForGpu("GPU-a"));
    }

    // ---------------------------------------------------------------- features, codes, routes

    [Fact]
    public void Features_P1AndP2AreDisjointAndCoverEveryConstant()
    {
        var all = typeof(AgentFeatures).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(AgentFeatures.P1.Intersect(AgentFeatures.P2, StringComparer.Ordinal));
        // P3 adds its own list (MN-202, D116; P3ContractTests), macOS Phase 3 another: together they cover every constant.
        Assert.Equal(all.Order(StringComparer.Ordinal), AgentFeatures.P1.Concat(AgentFeatures.P2).Concat(AgentFeatures.P3)
            .Concat(AgentFeatures.RuntimeExpansion).Concat(AgentFeatures.MacOS).Concat(AgentFeatures.Updates).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ApiCodes_EachHaveAStatus_AndTheP2CodesAreListed()
    {
        string[] p2 =
        [
            "placement_invalid", "placement_changed", "model_not_movable", "models_placed", "node_revoked",
            "reauthentication_failed", "job_conflict", "job_limit_reached", "master_not_empty", "backup_unsuitable", "recovery_mode",
        ];
        Assert.All(p2, c => Assert.Contains(c, ErrorCodes.ApiCodes));
        Assert.All(ErrorCodes.ApiCodes, c => Assert.InRange(ErrorCodes.ApiStatus(c), 400, 599));
        Assert.Equal(409, ErrorCodes.ApiStatus(ErrorCodes.ModelBusy));
        Assert.Equal(409, ErrorCodes.ApiStatus(ErrorCodes.PlacementChanged));
        Assert.Equal(403, ErrorCodes.ApiStatus(ErrorCodes.ReauthenticationFailed));
        Assert.Equal(503, ErrorCodes.ApiStatus(ErrorCodes.RecoveryMode));
        Assert.Equal(422, ErrorCodes.ApiStatus(ErrorCodes.GpuMissing));

        // No router code was added: /v1 clients see exactly P1's codes (recovery mode reuses "maintenance").
        Assert.Equal(9, ErrorCodes.RouterCodes.Count);
    }

    [Fact]
    public void Protocol_StaysVersionOne_AndTheAgentCatalogueIsUnchanged()
    {
        Assert.Equal(1, Protocol.MinVersion);
        Assert.Equal(1, Protocol.CurrentVersion);
        // P2 kept P1's 50 routes; P3 adds only the feature-gated GgufInspect (MN-202, D116).
        // The gated agent update (docs/multinode/agent-updates.md) is the 52nd.
        Assert.Equal(52, AgentRoutes.All.Count);
        Assert.Equal(AgentFeatures.AgentUpdate, AgentRoutes.AgentUpdate.Feature);
        Assert.Equal(50, AgentRoutes.All.Count(r => r != AgentRoutes.GgufInspect && r != AgentRoutes.AgentUpdate));
        Assert.DoesNotContain(AgentRoutes.Rekey, AgentRoutes.All);
        Assert.Equal([AgentRoutes.Enroll, AgentRoutes.Rekey], AgentRoutes.EnrollListener);
        Assert.Equal("/enroll/rekey", AgentRoutes.Rekey.Path);
        Assert.False(AgentRoutes.Rekey.RequiresProtocolHeader);
        Assert.Contains(typeof(RekeyRequest), AgentRoutes.DtoTypes);
    }

    [Fact]
    public void HealthReasonCodes_AreUnique()
    {
        var values = typeof(HealthReasonCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(HealthReasonCodes.PlacedGpuMissing, values);
        Assert.Contains(HealthReasonCodes.CaRotationPending, values);
    }

    [Fact]
    public void Enums_UseTheP2WireNames()
    {
        Assert.Equal("\"rekey\"", JsonSerializer.Serialize(JoinTokenPurpose.Rekey, MasterStateJson.Options));
        Assert.Equal("\"move\"", JsonSerializer.Serialize(OwnershipKind.Move, MasterStateJson.Options));
        Assert.Equal("\"agentsRotating\"", JsonSerializer.Serialize(CaRotationStage.AgentsRotating, MasterStateJson.Options));
        Assert.Equal("\"quiet\"", JsonSerializer.Serialize(BenchmarkNoise.Quiet, AgentJson.Options));
    }

    // ---------------------------------------------------------------- default interface members (P1 behaviour)

    [Fact]
    public void MaintenanceMode_DefaultMembers_KeepTheGlobalP1Window()
    {
        var state = new MaintenanceState("job-1", MaintenanceJobKinds.Benchmark, "qwen", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(5));
        IMaintenanceMode mode = new FixedMaintenance(state);
        Assert.Same(state, mode.ForModel("anything"));
        Assert.Same(state, mode.ForNode("pc-office"));
        Assert.Equal([state], mode.Active);

        IMaintenanceMode scoped = new FixedMaintenance(state with { NodeId = "dgx" });
        Assert.NotNull(scoped.ForNode("dgx"));
        Assert.Null(scoped.ForNode("pc-office"));

        IMaintenanceMode none = new FixedMaintenance(null);
        Assert.Empty(none.Active);
        Assert.Null(none.ForModel("qwen"));
    }

    [Fact]
    public void MaintenanceState_Describe_IsUnchangedForGlobalWindows_AndNamesTheNodeOrTheRestore()
    {
        var now = new DateTimeOffset(2026, 10, 6, 14, 0, 0, TimeSpan.Zero);
        var p1 = new MaintenanceState("j", "benchmark", "qwen", now, now.AddMinutes(5));
        Assert.Equal("mirbadllm is running a benchmark on qwen; API paused until ~14:05 UTC", p1.Describe(now));
        Assert.True(p1.IsGlobal);
        Assert.Equal("mirbadllm is running a tuning run on qwen on node dgx; API paused until ~14:05 UTC",
            (p1 with { JobKind = "tuning", NodeId = "dgx" }).Describe(now));
        Assert.Equal("mirbadllm is restoring a backup; API paused until ~14:05 UTC",
            (p1 with { JobKind = MaintenanceJobKinds.Restore, ModelId = "" }).Describe(now));
    }

    [Fact]
    public async Task NodeRegistry_P2Members_AreStubsUntilMN102()
    {
        INodeRegistry registry = new EmptyNodeRegistry();
        await Assert.ThrowsAsync<NotSupportedException>(() => registry.ExpireUnusedJoinTokensAsync("pc", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => registry.PruneJoinTokensAsync(TimeSpan.FromDays(7), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void GpuInventory_Missing_ListsAbsentUuids()
    {
        IGpuInventory inventory = new FixedInventory([new GpuCapability { Index = 0, Uuid = "GPU-a", Name = "x" }]);
        Assert.Equal(["GPU-b"], inventory.Missing(["GPU-a", "GPU-b"]));
        Assert.Equal(["GPU-a"], ((IGpuInventory)new FixedInventory(null)).Missing(["GPU-a"]));
    }

    [Fact]
    public void Provenance_ComparesHardwareAndRuntime_NotSettings()
    {
        var a = new BenchmarkProvenance { NodeId = "pc", NodeUid = "u1", Gpus = [new ProvenanceGpu("GPU-a")], ConfigHash = "h1" };
        Assert.True(a.IsComparableWith(a with { ConfigHash = "h2", ImageDigest = "sha256:x" }));
        Assert.False(a.IsComparableWith(a with { NodeUid = "u2" }));
        Assert.False(a.IsComparableWith(a with { Gpus = [new ProvenanceGpu("GPU-b")] }));
        Assert.False(a.IsComparableWith(a with { Delivery = LaunchDelivery.Process }));
        Assert.False(a.IsComparableWith(null));
    }

    /// <summary>
    /// W8 review (MN-128): the admin API (PascalCase enum options) writes a provenance block exactly as the benchmark store
    /// and the frozen fixture do: camelCase noise, no null members.
    /// </summary>
    [Fact]
    public void Provenance_IsWrittenAlike_ByTheApiOptions_AndTheBenchmarkStore()
    {
        var provenance = new BenchmarkProvenance { NodeId = "pc", NodeUid = "u1", Gpus = [new ProvenanceGpu("GPU-a")], Noise = BenchmarkNoise.Quiet };

        string api = JsonSerializer.Serialize(provenance, Api);
        string store = JsonSerializer.Serialize(provenance, MirbadLLM.Orchestration.Benchmarking.BenchmarkStore.JsonOptions);

        Assert.Equal("""{"nodeId":"pc","nodeUid":"u1","gpus":[{"uuid":"GPU-a"}],"runtime":"vllm","delivery":"container","noise":"quiet"}""", api);
        Assert.Equal(JsonNode.Parse(api)!.ToJsonString(), JsonNode.Parse(store)!.ToJsonString());
        Assert.Equal(BenchmarkNoise.Noisy, JsonSerializer.Deserialize<BenchmarkNoise>("\"noisy\"", Api));
    }

    [Fact]
    public void RecoveryOptions_MarkerFileOrOptionSelectsFleetRestore()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mirbadllm-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(RecoveryOptions.IsActive(new RecoveryOptions(), dir));
            Assert.True(RecoveryOptions.IsActive(new RecoveryOptions { Mode = RecoveryMode.FleetRestore }, dir));
            File.WriteAllText(Path.Combine(dir, RecoveryOptions.MarkerFileName), "");
            Assert.Equal(RecoveryMode.FleetRestore, RecoveryOptions.Effective(null, dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task NullAuditLog_KeepsNothing()
    {
        await NullAuditLog.Instance.WriteAsync(new AuditRecord { Time = DateTimeOffset.UnixEpoch, Actor = AuditActors.Config, Action = AuditActions.FencingTakeover, Result = AuditResults.Succeeded }, TestContext.Current.CancellationToken);
        Assert.Empty(await NullAuditLog.Instance.ReadAsync(new AuditQuery(), TestContext.Current.CancellationToken));
        // The month is the UTC month: 23:00 at UTC-3 on 31 October is 02:00 UTC on 1 November.
        Assert.Equal("2026-11.jsonl", AuditLogFiles.FileNameFor(new DateTimeOffset(2026, 10, 31, 23, 0, 0, TimeSpan.FromHours(-3))));
    }

    [Fact]
    public void ModelBusyException_NamesTheHolder()
    {
        var holder = new OwnershipRecord { Id = "o", Kind = OwnershipKind.Move, ModelId = "m", Stage = MoveStages.Downloading, StartedAt = DateTimeOffset.UnixEpoch };
        Assert.Equal("Model 'm' is busy: a move holds it.", new ModelBusyException("m", holder).Message);
        Assert.Equal("Model 'm' is busy: a move to pc holds it.", new ModelBusyException("m", holder with { Description = "a move to pc" }).Message);
        Assert.IsAssignableFrom<InvalidOperationException>(new ModelBusyException("m", holder));
        Assert.IsAssignableFrom<ArgumentException>(new PlacementValidationException([new PlacementProblem(PlacementProblemCodes.NodeUnknown, "Node 'x' does not exist.")]));
    }

    [Fact]
    public void Secrets_NeverAppearInToString()
    {
        const string secret = "s3cr3t-fixture";
        object[] values =
        [
            new RekeyRequest { Token = secret, NodeId = "pc", NodeUid = "u", CsrPem = "csr" },
            new JoinTokenCreateRequest { NodeId = "pc", Password = secret },
            new JoinTokenCreateResponse { NodeId = "pc", Token = secret, CaHash = "h", EnrollUrl = "https://m:8443", ExpiresAt = DateTimeOffset.UnixEpoch, Commands = new JoinCommands("join --token " + secret) },
            new JoinCommands("join --token " + secret),
            new NodeRevokeRequest { Password = secret, Confirm = "pc" },
            new ModelMoveRequest { TargetNodeId = "pc", Password = secret },
            new ModelMoveForceCommitRequest { Password = secret, Confirm = "dgx" },
            new ModelUnplaceRequest { Password = secret },
            new TakeoverRequest { Password = secret, Confirm = "x" },
            new TakeoverContinueRequest { NodeId = "pc", TakeoverId = "t", AgentEpoch = 1, Password = secret, Confirm = "pc" },
        ];
        Assert.All(values, v => Assert.DoesNotContain(secret, v.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void TheStrictWireOptions_ReadTheP2Fixtures()
    {
        // Guards the wire fixtures against a key typo that the lenient production options would drop silently.
        var record = JsonSerializer.Deserialize<NodeRecord>(File.ReadAllText(Path.Combine(FixturesPath(), "NodeRecord.json")), WireStrict)!;
        Assert.Equal(MemoryModel.Discrete, record.LastCapabilities!.Memory.Model);
        Assert.Contains(AgentFeatures.GpuSelectionUuids, record.LastCapabilities.Features);
        var token = JsonSerializer.Deserialize<JoinTokenRecord>(File.ReadAllText(Path.Combine(FixturesPath(), "JoinTokenRecord.json")), WireStrict)!;
        Assert.Equal(JoinTokenPurpose.Rekey, token.Purpose);
    }

    // ---------------------------------------------------------------- helpers

    private static bool SameJson(JsonNode expected, JsonNode actual)
    {
        if (expected is JsonValue ev && actual is JsonValue av)
        {
            if (ev.GetValueKind() == JsonValueKind.Number && av.GetValueKind() == JsonValueKind.Number)
            {
                return ev.GetValue<decimal>() == av.GetValue<decimal>();
            }

            if (ev.GetValueKind() == JsonValueKind.String && av.GetValueKind() == JsonValueKind.String &&
                DateTimeOffset.TryParse(ev.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var de) &&
                DateTimeOffset.TryParse(av.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var da))
            {
                return de == da && de.Offset == da.Offset;
            }

            return JsonNode.DeepEquals(ev, av);
        }

        if (expected is JsonObject eo && actual is JsonObject ao)
        {
            return eo.Count == ao.Count && eo.All(p => ao.ContainsKey(p.Key) && (p.Value is null ? ao[p.Key] is null : ao[p.Key] is not null && SameJson(p.Value, ao[p.Key]!)));
        }

        if (expected is JsonArray ea && actual is JsonArray aa)
        {
            return ea.Count == aa.Count && ea.Zip(aa).All(p => p.First is null ? p.Second is null : p.Second is not null && SameJson(p.First, p.Second));
        }

        return false;
    }

    private static JsonSerializerOptions CreateApi()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static string FixturesPath([CallerFilePath] string sourceFile = "") => Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures");

    private sealed class FixedMaintenance(MaintenanceState? state) : IMaintenanceMode
    {
        public MaintenanceState? Current => state;
    }

    private sealed class FixedInventory(IReadOnlyList<GpuCapability>? gpus) : IGpuInventory
    {
        public IReadOnlyList<GpuCapability>? Last => gpus;

        public Task<IReadOnlyList<GpuCapability>> RefreshAsync(CancellationToken ct = default) => Task.FromResult(gpus ?? []);
    }

    private sealed class EmptyNodeRegistry : INodeRegistry
    {
        public Task<IReadOnlyList<NodeRecord>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NodeRecord>>([]);

        public Task<NodeRecord?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<NodeRecord?>(null);

        public Task UpsertAsync(NodeRecord node, CancellationToken ct = default) => Task.CompletedTask;

        public Task<NodeRecord?> UpdateAsync(string id, Func<NodeRecord, NodeRecord> update, CancellationToken ct = default) => Task.FromResult<NodeRecord?>(null);

        public Task<bool> RevokeAsync(string id, CancellationToken ct = default) => Task.FromResult(false);

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => Task.FromResult(false);

        public Task<JoinTokenIssue> CreateJoinTokenAsync(string nodeId, TimeSpan lifetime, JoinTokenPurpose purpose, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<JoinTokenConsumption> TryConsumeJoinTokenAsync(string token, string nodeId, CancellationToken ct = default) =>
            Task.FromResult(new JoinTokenConsumption(JoinTokenConsumeOutcome.Unknown, null));

        public Task<IReadOnlyList<JoinTokenRecord>> ListJoinTokensAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JoinTokenRecord>>([]);
    }
}
