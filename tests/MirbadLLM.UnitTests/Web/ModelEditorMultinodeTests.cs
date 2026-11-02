using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Downloads.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Components.Pages;
using FakeHttpMessageHandler = MirbadLLM.UnitTests.Orchestration.Fakes.FakeHttpMessageHandler;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

namespace MirbadLLM.UnitTests.Web;

/// <summary>
/// MN-090: saving from the model editor keeps every field the form does not show (design §5.4: a UI save must never
/// unplace or re-place a model), and creating a model without a placement follows §5.6 (the only node, else refused).
/// </summary>
public sealed class ModelEditorMultinodeTests : IDisposable
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), "mirbadllm-editor-mn-" + Guid.NewGuid().ToString("N"));

    private static readonly ModelDefinition Placed = new()
    {
        Id = "qwen3-embedding-4b",
        ServedName = "Qwen3-Embedding-4B",
        ModelPath = "qwen3-embedding-4b",
        EnablePrefixCaching = true,
        Placement = new ModelPlacement("dgx", ["GPU-5967aaaa"], [0]),
        Runtime = RuntimeKind.Vllm,
        HostMemoryBudgetGiB = 12,
        KeepServingDuringJobs = true,
        LlamaCpp = new LlamaCppSettings { ModelFile = "model.gguf", ContextSize = 8192 },
    };

    public ModelEditorMultinodeTests() => Directory.CreateDirectory(_modelsRoot);

    public void Dispose()
    {
        if (Directory.Exists(_modelsRoot))
        {
            Directory.Delete(_modelsRoot, recursive: true);
        }
    }

    private Bunit.BunitContext MakeContext(IModelRegistry registry, INodeRegistry? nodes = null)
    {
        var ctx = new Bunit.BunitContext();
        ctx.Services.AddSingleton<IModelRegistry>(registry);
        ctx.Services.AddSingleton<IModelRuntime>(new FakeModelRuntime());
        ctx.Services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(new MemorySnapshot(128, 64)));
        ctx.Services.AddSingleton(Options.Create(new MirbadLLMOptions()));
        ctx.Services.AddNodeSourcing(_modelsRoot);
        ctx.Services.AddSingleton<IHuggingFaceDownloader>(new FakeHuggingFaceDownloader());
        ctx.Services.AddSingleton<IHuggingFaceRepoInspector>(new FakeHuggingFaceRepoInspector());
        if (nodes is not null)
        {
            ctx.Services.AddSingleton(nodes);
        }

        return ctx;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // the master's registry: validators run, but a save that keeps the placement is not re-validated
    public async Task Save_KeepsEveryFieldTheFormDoesNotShow(bool realRegistry)
    {
        await using var master = realRegistry ? await MasterRegistry.StartAsync(_modelsRoot, seedUnvalidated: Placed) : null;
        IModelRegistry registry = master?.Models ?? new FakeModelRegistry([Placed]);
        using var ctx = MakeContext(registry, master?.Nodes);
        var cut = Loaded(ctx.Render<ModelEditor>(p => p.Add(x => x.Id, Placed.Id)));

        await cut.InvokeAsync(() => cut.Find("[data-testid=field-prefix-caching]").Change("off"));
        await cut.InvokeAsync(() => cut.Find("form").Submit());

        // 15 s: the real-registry save runs slow on the macOS runner under the full suite.
        cut.WaitForAssertion(() =>
        {
            var saved = registry.GetAsync(Placed.Id, Ct).GetAwaiter().GetResult()!;
            Assert.False(saved.EnablePrefixCaching); // the edit landed
            Assert.Equal(Placed.Placement, saved.Placement);
            Assert.Equal(Placed.Runtime, saved.Runtime);
            Assert.Equal(Placed.HostMemoryBudgetGiB, saved.HostMemoryBudgetGiB);
            Assert.True(saved.KeepServingDuringJobs);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(Placed.LlamaCpp), System.Text.Json.JsonSerializer.Serialize(saved.LlamaCpp));
        }, TimeSpan.FromSeconds(15));

        // The save goes on after the write (it reads the stored model back, then leaves the page): wait for it to finish
        // before the registry and its directory go away (W9 review MN-132: an IOException on models.json in Dispose).
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        cut.WaitForAssertion(() => Assert.NotEmpty(nav.History), TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// D75/D87 over the master's registry and validators (MN-132; before, a validator-less fake let this save through): a
    /// placement change landing after the page read the model refuses the save (409 <c>placement_changed</c>) instead of
    /// reverting it, the page says so and keeps the other edits, and the next save goes onto the placement as it is now.
    /// </summary>
    [Fact]
    public async Task Save_AfterThePlacementChangedSinceThePageLoaded_IsRefused_KeepsTheEdits_AndTheNextSaveKeepsTheNewPlacement()
    {
        var model = new ModelDefinition
        {
            Id = "qwen3-embedding-4b", ServedName = "Qwen3-Embedding-4B", ModelPath = "qwen3-embedding-4b", EnablePrefixCaching = true,
            Placement = new ModelPlacement("dgx"),
        };
        await using var master = await MasterRegistry.StartAsync(_modelsRoot, seed: model);
        using var ctx = MakeContext(master.Models, master.Nodes);
        var cut = Loaded(ctx.Render<ModelEditor>(p => p.Add(x => x.Id, model.Id)));

        // An unplace committed while the editor was open (any writer other than this page).
        await master.Models.UpsertAsync((await master.Models.GetAsync(model.Id, Ct))! with { Placement = null }, Ct);
        await cut.InvokeAsync(() => cut.Find("[data-testid=field-prefix-caching]").Change("off"));
        await cut.InvokeAsync(() => cut.Find("form").Submit());

        cut.WaitForAssertion(() => Assert.Equal(
            "Not saved: the placement of qwen3-embedding-4b changed while this page was open (a move, an unplace or another editor); " +
            "it is now unplaced. The page shows it now and your other edits are still in the form: check them and save again.",
            cut.Find("[data-testid=save-error]").TextContent.Trim()), TimeSpan.FromSeconds(5));
        var stored = (await master.Models.GetAsync(model.Id, Ct))!;
        Assert.Null(stored.Placement);
        Assert.True(stored.EnablePrefixCaching); // nothing of the refused save was written
        Assert.Equal("off", cut.Find("[data-testid=field-prefix-caching]").GetAttribute("value"));
        Assert.Equal("Unplaced", cut.Find("[data-testid=field-node-readonly]").TextContent.Trim());

        await cut.InvokeAsync(() => cut.Find("form").Submit());

        cut.WaitForAssertion(() =>
        {
            var saved = master.Models.GetAsync(model.Id, Ct).GetAwaiter().GetResult()!;
            Assert.False(saved.EnablePrefixCaching);
            Assert.Null(saved.Placement); // never reverted to dgx
        }, TimeSpan.FromSeconds(5));

        // Let the save finish (read-back, then leaving the page) before the registry is disposed (see above).
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        cut.WaitForAssertion(() => Assert.NotEmpty(nav.History), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Create_WithoutANodeRegistry_PlacesTheModelOnTheBootstrapNode()
    {
        var registry = new FakeModelRegistry();
        using var ctx = MakeContext(registry);

        var cut = Create(ctx);

        cut.WaitForAssertion(() => Assert.Equal(new ModelPlacement(RegistryMigrationOptions.DefaultBootstrapNodeId), Saved(registry)?.Placement), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Create_WithExactlyOneNode_PlacesTheModelOnIt()
    {
        var registry = new FakeModelRegistry();
        using var ctx = MakeContext(registry, new ListOnlyNodeRegistry(ListOnlyNodeRegistry.Node("node-a"), ListOnlyNodeRegistry.Node("old", revoked: true)));

        var cut = Create(ctx);

        cut.WaitForAssertion(() => Assert.Equal(new ModelPlacement("node-a"), Saved(registry)?.Placement), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Create_WithSeveralNodes_IsRefused_AndNothingIsSaved()
    {
        var registry = new FakeModelRegistry();
        using var ctx = MakeContext(registry, new ListOnlyNodeRegistry(ListOnlyNodeRegistry.Node("node-a"), ListOnlyNodeRegistry.Node("node-b")));

        var cut = Create(ctx);

        cut.WaitForAssertion(() => Assert.Contains("More than one node", cut.Markup, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        Assert.Null(Saved(registry));
    }

    [Fact]
    public async Task New_OnANodeThatIsNotServing_SaysWhyTheFolderListIsEmpty()
    {
        // W4 review MN-096: an unreachable node is not "no checkpoint folders found".
        string dataPath = Path.Combine(_modelsRoot, "data");
        Directory.CreateDirectory(dataPath);
        var options = Options.Create(new MirbadLLMOptions { DataPath = dataPath });
        var nodes = new ListOnlyNodeRegistry();
        using var ca = CertificateAuthority.Create();
        using var master = ca.IssueMasterClientCertificate();
        using var trust = TrustAnchorSet.Create([ca.Certificate]);
        await using var clients = new AgentClientFactory(nodes, new MirbadLLM.UnitTests.Multinode.TestMasterIdentity(), master, trust, TimeProvider.System);
        using var references = new JsonAgentOperationReferenceStore(options, TimeProvider.System);
        var registry = new FakeModelRegistry();
        using var ctx = MakeContext(registry);
        ctx.Services.AddSingleton(new RemoteModelFolders(clients, new AgentOperationRunner(clients, references, nodes, TimeProvider.System), registry));
        ctx.Services.AddSingleton<INodeStatusCache>(new MirbadLLM.UnitTests.Benchmarking.RemoteJobTests.FakeNodeCache(new NodeLiveStatus
        {
            NodeId = "dgx", NodeUid = "0123456789abcdef0123456789abcdef", State = NodeState.Offline, Address = "https://mirbadllm-agent:9443/",
        }));

        var cut = ctx.Render<ModelEditor>();

        cut.WaitForAssertion(() => Assert.Contains("not serving", cut.Find("[data-testid=folder-error]").TextContent, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        Assert.Contains("Folders unavailable", cut.Find("[data-testid=field-source-folder]").TextContent, StringComparison.Ordinal);
    }

    private static Bunit.IRenderedComponent<ModelEditor> Create(Bunit.BunitContext ctx)
    {
        var cut = ctx.Render<ModelEditor>();
        cut.Find("[data-testid=field-id]").Change("new-model");
        cut.Find("[data-testid=field-served-name]").Change("New-Model");
        cut.Find("[data-testid=field-model-path]").Change("checkpoints/new-model");
        cut.Find("form").Submit();
        return cut;
    }

    /// <summary>
    /// Waits until the editor shows the stored model (over the real registry its load awaits file reads, and renders again
    /// as it goes on); the tests then find and trigger elements inside <c>InvokeAsync</c>, so a render in between cannot
    /// replace the handler they trigger.
    /// </summary>
    private static Bunit.IRenderedComponent<ModelEditor> Loaded(Bunit.IRenderedComponent<ModelEditor> cut)
    {
        cut.WaitForAssertion(() => Assert.Equal("on", cut.Find("[data-testid=field-prefix-caching]").GetAttribute("value")), TimeSpan.FromSeconds(10));
        return cut;
    }

    private static ModelDefinition? Saved(FakeModelRegistry registry) => registry.GetAsync("new-model", Ct).GetAwaiter().GetResult();

    /// <summary>
    /// The master's model registry as composed (MN-108, D82): <see cref="JsonModelRegistry"/> over the ownership and
    /// placement validators, with a <see cref="JsonNodeRegistry"/> holding the approved bootstrap node <c>dgx</c>.
    /// </summary>
    private sealed class MasterRegistry : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private MasterRegistry(ServiceProvider services)
        {
            _services = services;
            Models = services.GetRequiredService<IModelRegistry>();
            Nodes = services.GetRequiredService<INodeRegistry>();
        }

        public IModelRegistry Models { get; }

        public INodeRegistry Nodes { get; }

        /// <summary>
        /// Starts it under <paramref name="root"/>; <paramref name="seed"/> is created through the validators,
        /// <paramref name="seedUnvalidated"/> written as an older version stored it (no validators).
        /// </summary>
        public static async Task<MasterRegistry> StartAsync(string root, ModelDefinition? seed = null, ModelDefinition? seedUnvalidated = null)
        {
            string dataPath = Path.Combine(root, "master-data");
            Directory.CreateDirectory(dataPath);
            var options = Options.Create(new MirbadLLMOptions { DataPath = dataPath });
            if (seedUnvalidated is not null)
            {
                using var plain = new JsonModelRegistry(options);
                await plain.UpsertAsync(seedUnvalidated, Ct);
            }

            var services = new ServiceCollection();
            services.AddSingleton(options);
            services.AddSingleton(Options.Create(new RegistryMigrationOptions()));
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<INodeRegistry>(sp => new MirbadLLM.Orchestration.Nodes.JsonNodeRegistry(options, TimeProvider.System));
            services.AddPlacementValidation();
            services.AddSingleton<IModelRegistry>(sp => new JsonModelRegistry(
                options, sp.GetRequiredService<IOptions<RegistryMigrationOptions>>(), sp.GetServices<IModelDefinitionValidator>(), null));
            var master = new MasterRegistry(services.BuildServiceProvider());
            await master.Nodes.UpsertAsync(new NodeRecord { Id = "dgx", Uid = "dgx-uid", Address = "https://127.0.0.1:9443", Approved = true }, Ct);
            if (seed is not null)
            {
                await master.Models.UpsertAsync(seed, Ct);
            }

            return master;
        }

        public ValueTask DisposeAsync() => _services.DisposeAsync();
    }
}
