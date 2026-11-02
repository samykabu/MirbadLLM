using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Pki;
using MirbadLLM.Web.Api;
using MirbadLLM.Web.Enrollment;

#pragma warning disable CA1707, xUnit1051 // underscore test names; hand-written fakes ignore the test token.

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// W7 review MN-126 items 1 and 3 on <see cref="NodeAdminService"/> over a model registry composed WITH the MN-108
/// validators: a revoke sets a draining barrier before listing the node's models, so a placement racing the revoke is
/// refused under the registry lock; a held model is refused before anything is stopped; the unplace never erases a
/// placement changed meanwhile; and a join-token display name survives a master restart before approval.
/// </summary>
public sealed class NodeRevokeRaceTests : IDisposable
{
    private const string NodeId = "pc-office";
    private const string Password = "pw";

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "mirbadllm-mn126-revoke-" + Guid.NewGuid().ToString("N"));
    private readonly JsonNodeRegistry _nodes;
    private readonly OwnershipStore _ownership;
    private readonly JsonModelRegistry _models;
    private readonly CertificateAuthority _ca = CertificateAuthority.Create();
    private readonly HookRuntime _runtime = new();

    public NodeRevokeRaceTests()
    {
        Directory.CreateDirectory(_dataPath);
        var data = Options.Create(new MirbadLLMOptions { DataPath = _dataPath });
        _nodes = new JsonNodeRegistry(data, TimeProvider.System);
        _ownership = new OwnershipStore(data);
        _models = new JsonModelRegistry(data, Options.Create(new RegistryMigrationOptions()),
            [new OwnershipValidator(_ownership), new PlacementValidator(_nodes, _ownership)],
            Options.Create(new ModelRegistryOptions { SeedPolicy = ModelSeedPolicy.None }));
        _nodes.UpsertAsync(Node("dgx")).GetAwaiter().GetResult();
        _nodes.UpsertAsync(Node(NodeId)).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _models.Dispose();
        _ownership.Dispose();
        _nodes.Dispose();
        _ca.Dispose();
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private static NodeRecord Node(string id) => new()
    {
        Id = id,
        Uid = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()),
        Address = $"https://{id}.lan:9443",
        Approved = true,
        CertSpkiSha256 = Convert.ToBase64String(new byte[32]),
    };

    private static ModelDefinition Model(string id, string nodeId) => new()
    {
        Id = id,
        ServedName = id.ToUpperInvariant(),
        ModelPath = id + "-ckpt",
        MemoryBudgetGiB = 8,
        Placement = new ModelPlacement(nodeId),
    };

    private NodeAdminService Service(string? pendingNamesPath = null) => new(
        _nodes, _models, _ca, Options.Create(new NodeApiOptions()), Options.Create(new EnrollmentOptions()), TimeProvider.System,
        NullLogger<NodeAdminService>.Instance, new AllowReauth(), audit: null, refresher: null, cache: null, _runtime, _ownership, pendingNamesPath);

    private static readonly NodeApiCaller Caller = new("admin:test", "127.0.0.1", "master.lan") { User = TestAdmin.User };

    private static NodeRevokeRequest Revoke() => new() { Password = Password, Confirm = NodeId, UnplaceModels = true };

    [Fact]
    public async Task APlacementRacingTheRevoke_IsRefused_BecauseTheNodeIsDrainingFromTheStart()
    {
        await _models.UpsertAsync(Model("m-a", NodeId));
        Exception? raced = null;
        _runtime.OnStop = async _ =>
        {
            // While the revoke stops m-a, someone places a new model on the node (POST /api/models, a restore target, the editor).
            raced = await Record.ExceptionAsync(() => _models.UpsertAsync(Model("m-new", NodeId)));
        };

        var result = await Service().RevokeAsync(NodeId, Revoke(), Caller);

        Assert.Equal(["m-a"], result.UnplacedModelIds);
        var invalid = Assert.IsType<PlacementValidationException>(raced);
        Assert.Contains(invalid.Problems, p => p.Code == PlacementProblemCodes.NodeDraining);
        Assert.Null(await _models.GetAsync("m-new"));
        Assert.True((await _nodes.GetAsync(NodeId))!.Revoked);
        Assert.DoesNotContain(await _models.ListAsync(), m => m.Placement?.NodeId == NodeId);
    }

    [Fact]
    public async Task ARefusedRevoke_PutsTheDrainingFlagBack()
    {
        await _models.UpsertAsync(Model("m-a", NodeId));

        var refused = await Assert.ThrowsAsync<NodeApiException>(() =>
            Service().RevokeAsync(NodeId, Revoke() with { UnplaceModels = false }, Caller));

        Assert.Equal(ErrorCodes.ModelsPlaced, refused.Code);
        var node = (await _nodes.GetAsync(NodeId))!;
        Assert.False(node.Revoked);
        Assert.False(node.Draining);
        await _models.UpsertAsync(Model("m-b", NodeId)); // placements work again
    }

    [Fact]
    public async Task AnOperatorDrainedNode_StaysDraining_AfterARefusedRevoke()
    {
        await _models.UpsertAsync(Model("m-a", NodeId));
        await _nodes.UpdateAsync(NodeId, n => n with { Draining = true });

        var refused = await Assert.ThrowsAsync<NodeApiException>(() =>
            Service().RevokeAsync(NodeId, Revoke() with { UnplaceModels = false }, Caller));

        Assert.Equal(ErrorCodes.ModelsPlaced, refused.Code);
        Assert.True((await _nodes.GetAsync(NodeId))!.Draining); // the operator's flag, not the revoke's barrier
    }

    [Fact]
    public async Task AHeldModel_IsRefusedBeforeAnythingIsStopped_AndTheNodeIsNotRevoked()
    {
        await _models.UpsertAsync(Model("m-a", NodeId));
        await _models.UpsertAsync(Model("m-held", NodeId));
        await _ownership.TryAcquireAsync(new OwnershipRecord
        {
            Id = "job-1", Kind = OwnershipKind.Job, ModelId = "m-held", Stage = "tuning", StartedAt = DateTimeOffset.UtcNow,
            Description = "a tuning run on node pc-office",
        });

        var busy = await Assert.ThrowsAsync<NodeApiException>(() => Service().RevokeAsync(NodeId, Revoke(), Caller));

        Assert.Equal(ErrorCodes.ModelBusy, busy.Code);
        Assert.Contains("a tuning run on node pc-office holds it", busy.Message, StringComparison.Ordinal);
        Assert.Empty(_runtime.Stopped);
        Assert.Equal(NodeId, (await _models.GetAsync("m-a"))!.Placement?.NodeId);
        var node = (await _nodes.GetAsync(NodeId))!;
        Assert.False(node.Revoked);
        Assert.False(node.Draining);
    }

    [Fact]
    public async Task TheUnplace_NeverErasesAMoveCommittedMeanwhile()
    {
        await _models.UpsertAsync(Model("m-a", NodeId));
        _runtime.OnStop = async id =>
        {
            // A move (holder of its record) commits m-a to dgx while the revoke stops it, then releases its record.
            await _ownership.TryAcquireAsync(new OwnershipRecord { Id = "move-1", Kind = OwnershipKind.Move, ModelId = id, Stage = MoveStages.SourceStopped, StartedAt = DateTimeOffset.UtcNow });
            var current = (await _models.GetAsync(id))!;
            using (ModelWriteScope.AsOwner("move-1"))
            {
                await _models.UpsertAsync(current with { Placement = current.Placement! with { NodeId = "dgx" } });
            }

            await _ownership.ReleaseAsync("move-1");
        };

        var result = await Service().RevokeAsync(NodeId, Revoke(), Caller);

        Assert.Empty(result.UnplacedModelIds);
        var moved = (await _models.GetAsync("m-a"))!.Placement!;
        Assert.Equal(("dgx", 1L), (moved.NodeId, moved.Generation));
        Assert.True((await _nodes.GetAsync(NodeId))!.Revoked);
    }

    [Fact]
    public async Task AJoinTokenDisplayName_SurvivesAMasterRestart_AndIsAppliedAtApproval()
    {
        string path = Path.Combine(_dataPath, PendingNodeNames.FileName);
        await Service(path).CreateJoinTokenAsync(new JoinTokenCreateRequest { NodeId = "pc-new", DisplayName = "Office PC", Password = Password }, Caller);

        // The master restarts; the agent enrolls (a pending record without a name), then the operator approves it.
        string pin = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());
        await _nodes.UpsertAsync(new NodeRecord { Id = "pc-new", Uid = new string('a', 32), Address = "https://pc-new:9443", PendingCertSpkiSha256 = pin });
        var approved = await Service(path).ApproveAsync("pc-new", null, Caller);

        Assert.Equal("Office PC", (await _nodes.GetAsync("pc-new"))!.DisplayName);
        Assert.Equal("Office PC", approved.Node.DisplayName);
        Assert.Null(new PendingNodeNames(path).Get("pc-new")); // forgotten once applied
    }

    [Fact]
    public void PendingNodeNames_AnUnreadableFile_ReadsAsEmpty_AndIsReplacedByTheNextWrite()
    {
        string path = Path.Combine(_dataPath, PendingNodeNames.FileName);
        File.WriteAllText(path, "{ not json");

        var names = new PendingNodeNames(path);
        Assert.Null(names.Get("pc"));
        names.Set("pc", "PC");

        Assert.Equal("PC", new PendingNodeNames(path).Get("pc"));
    }

    private sealed class AllowReauth : IAdminReauthenticator
    {
        public bool LoginEnabled => true;

        public AdminReauthOutcome Verify(System.Security.Claims.ClaimsPrincipal? user) =>
            user?.Identity?.IsAuthenticated == true ? AdminReauthOutcome.Verified : AdminReauthOutcome.LoginRequired;
    }

    private sealed class HookRuntime : IModelRuntime
    {
        public List<string> Stopped { get; } = [];

        public Func<string, Task>? OnStop { get; set; }

        public Task<IReadOnlyList<ModelStatus>> ListStatusAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelStatus>>([]);

        public Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default) => Task.FromResult(new ModelStatus(id, ModelState.Stopped));

        public Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default) => throw new NotSupportedException();

        public async Task StopAsync(string id, CancellationToken ct = default)
        {
            Stopped.Add(id);
            if (OnStop is { } hook)
            {
                await hook(id);
            }
        }

        public async IAsyncEnumerable<string> StreamLogsAsync(string id, int tail = 200, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
