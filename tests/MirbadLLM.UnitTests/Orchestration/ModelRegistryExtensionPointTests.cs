using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Orchestration;

/// <summary>
/// The two P2 extension points of <see cref="JsonModelRegistry"/> (MN-101, design D73): injected
/// <see cref="IModelDefinitionValidator"/>s, run under the registry lock with the stored definition (MN-108), and the
/// <see cref="ModelSeedPolicy"/> (recovery mode, MN-113). Both are no-ops by default.
/// </summary>
public sealed class ModelRegistryExtensionPointTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "mirbadllm-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private JsonModelRegistry Create(IEnumerable<IModelDefinitionValidator>? validators = null, ModelSeedPolicy seed = ModelSeedPolicy.Seed) =>
        new(Options.Create(new MirbadLLMOptions { DataPath = _dataDir }), Options.Create(new RegistryMigrationOptions()), validators,
            Options.Create(new ModelRegistryOptions { SeedPolicy = seed }));

    private static ModelDefinition Model(string id, ModelPlacement? placement = null) => new()
    {
        Id = id, ServedName = id, ModelPath = id, MemoryBudgetGiB = 8, Placement = placement ?? new ModelPlacement("dgx"),
    };

    [Fact]
    public async Task WithoutExtensions_TheRegistrySeedsAsBefore()
    {
        using var registry = Create();
        var seed = Assert.Single(await registry.ListAsync(Ct));
        Assert.Equal("ornith-9b-fp8", seed.Id);
    }

    [Fact]
    public async Task SeedPolicyNone_LeavesAMissingFileEmpty_UntilTheFirstSave()
    {
        using var registry = Create(seed: ModelSeedPolicy.None);
        Assert.Empty(await registry.ListAsync(Ct));
        Assert.False(File.Exists(Path.Combine(_dataDir, ModelRegistryDocument.FileName)));

        await registry.UpsertAsync(Model("restored"), Ct);
        Assert.Equal("restored", Assert.Single(await registry.ListAsync(Ct)).Id);
        Assert.True(File.Exists(Path.Combine(_dataDir, ModelRegistryDocument.FileName)));
    }

    [Fact]
    public async Task Validators_SeeTheStoredDefinition_RunInOrder_AndMayStampTheResult()
    {
        var seen = new List<string>();
        var stamp = new DelegateValidator(change =>
        {
            seen.Add($"stamp:{change.Stored?.Placement?.Generation}");
            return change.Proposed! with { Placement = change.Proposed.Placement! with { Generation = (change.Stored?.Placement?.Generation ?? 0) + 1 } };
        });
        var observe = new DelegateValidator(change =>
        {
            seen.Add($"observe:{change.Proposed?.Placement?.Generation}");
            return change.Proposed;
        });
        using var registry = Create([stamp, observe], ModelSeedPolicy.None);

        await registry.UpsertAsync(Model("m"), Ct);
        await registry.UpsertAsync(Model("m"), Ct);

        Assert.Equal(["stamp:", "observe:1", "stamp:1", "observe:2"], seen);
        Assert.Equal(2, (await registry.GetAsync("m", Ct))!.Placement!.Generation);
    }

    [Fact]
    public async Task AValidatorRefusal_LeavesTheFileUntouched()
    {
        var refuse = new DelegateValidator(change =>
            change.Stored is not null ? throw new PlacementConflictException() : change.Proposed);
        using var registry = Create([refuse], ModelSeedPolicy.None);
        await registry.UpsertAsync(Model("m"), Ct);
        byte[] before = await File.ReadAllBytesAsync(Path.Combine(_dataDir, ModelRegistryDocument.FileName), Ct);

        await Assert.ThrowsAsync<PlacementConflictException>(() => registry.UpsertAsync(Model("m") with { MaxNumSeqs = 4 }, Ct));

        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(_dataDir, ModelRegistryDocument.FileName), Ct));
        Assert.NotEqual(4, (await registry.GetAsync("m", Ct))!.MaxNumSeqs);
    }

    [Fact]
    public async Task Deletes_RunTheValidators_WhichMayRefuseThem()
    {
        var changes = new List<ModelDefinitionChange>();
        var busy = new DelegateValidator(change =>
        {
            changes.Add(change);
            return change.IsDelete && change.ModelId == "held"
                ? throw new ModelBusyException("held", new OwnershipRecord { Id = "o", Kind = OwnershipKind.Job, ModelId = "held", Stage = "tuning", StartedAt = DateTimeOffset.UnixEpoch })
                : change.Proposed;
        });
        using var registry = Create([busy], ModelSeedPolicy.None);
        await registry.UpsertAsync(Model("held"), Ct);
        await registry.UpsertAsync(Model("free"), Ct);

        await Assert.ThrowsAsync<ModelBusyException>(() => registry.DeleteAsync("held", Ct));
        Assert.True(await registry.DeleteAsync("free", Ct));
        Assert.False(await registry.DeleteAsync("missing", Ct));

        Assert.Equal(["held"], (await registry.ListAsync(Ct)).Select(m => m.Id));
        Assert.Contains(changes, c => c.IsDelete && c.Stored!.Id == "free");
        Assert.DoesNotContain(changes, c => c.ModelId == "missing"); // nothing to delete: validators are not asked
    }

    [Fact]
    public async Task BuiltInChecks_RunBeforeTheValidators()
    {
        int calls = 0;
        var count = new DelegateValidator(change =>
        {
            calls++;
            return change.Proposed;
        });
        using var registry = Create([count], ModelSeedPolicy.None);

        await Assert.ThrowsAsync<ArgumentException>(() => registry.UpsertAsync(Model("Bad Id"), Ct));
        Assert.Equal(0, calls);
    }

    private sealed class DelegateValidator(Func<ModelDefinitionChange, ModelDefinition?> validate) : IModelDefinitionValidator
    {
        public Task<ModelDefinition?> ValidateAsync(ModelDefinitionChange change, CancellationToken ct = default) => Task.FromResult(validate(change));
    }
}
