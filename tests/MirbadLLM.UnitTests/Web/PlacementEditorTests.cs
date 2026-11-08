using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.UnitTests.Downloads.Fakes;
using MirbadLLM.UnitTests.Multinode;
using MirbadLLM.UnitTests.Multinode.Fakes;
using MirbadLLM.UnitTests.Multinode.P1Exit;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web;
using MirbadLLM.Web.Components.ModelEditor;
using MirbadLLM.Web.Components.Pages;

#pragma warning disable CA1707 // MethodUnderTest_Scenario_ExpectedBehavior test naming style, used throughout this project.

namespace MirbadLLM.UnitTests.Web;

/// <summary>
/// MN-118: the model editor's Placement section, move dialog and unplace (design §5.1, §6.3, §6.4, §6.7, §12.2). The page
/// tests render the real editor over a real master (<see cref="MoveCluster"/>: the master composition with its registry
/// validators, <see cref="MoveService"/>, the fit preview and two FakeAgents, <c>dgx</c> unified and <c>pc-office</c>
/// discrete 2 × 16 GiB with GPU selection by UUID), so every save, move and refusal goes through the code the API uses.
/// The single-DGX tests use the editor's usual fakes: there the editor must look and behave as before P2.
/// </summary>
public sealed class PlacementEditorTests
{
    private const string Gpu0 = "GPU-fake-d000-0000-0000-000000000000";
    private const string Gpu1 = "GPU-fake-d001-0000-0000-000000000000";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private sealed class SilentNodeMemoryProbe : IMemoryProbe
    {
        public Task<MemorySnapshot> GetAsync(CancellationToken ct = default) =>
            throw new NodeTelemetryUnavailableException("Node 'dgx' has not reported memory recently.");
    }

    [Fact]
    public void Editor_StillOpens_WhenTheNodeHasNotReportedMemory()
    {
        var model = new ModelDefinition { Id = "m1", ServedName = "M1", ModelPath = "m1" };
        string root = Path.Combine(Path.GetTempPath(), "ss-silent-node-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var ctx = FakeContext(new FakeModelRegistry([model]), root, nodes: null);
            ctx.Services.AddSingleton<IMemoryProbe>(new SilentNodeMemoryProbe());

            var cut = ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "m1"));

            Assert.NotEmpty(cut.FindAll("[data-testid=field-speculative-enabled]"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SglangEdit_SavesSpeculativeDecoding_AndRefusesItWithoutAnAlgorithm_WhileNimDoesNotOfferIt()
    {
        var container = new ContainerRuntimeSettings { Image = "lmsysorg/sglang:1" };
        var sglang = new ModelDefinition { Id = "sg", ServedName = "SG", ModelPath = "sg", Runtime = RuntimeKind.Sglang, Container = container, ToolCallParser = null, ReasoningParser = null };
        var nim = sglang with { Id = "nim", ServedName = "NIM", Runtime = RuntimeKind.Nim };
        string root = Path.Combine(Path.GetTempPath(), "ss-sglang-speculative-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var registry = new FakeModelRegistry([sglang, nim]);
            using var ctx = FakeContext(registry, root, nodes: null);

            Assert.Empty(ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "nim")).FindAll("[data-testid=field-speculative-enabled]"));

            var cut = ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "sg"));
            Assert.Empty(cut.FindAll("[data-testid=field-speculative-method]"));
            cut.Find("[data-testid=field-speculative-enabled]").Change(true);
            cut.Find("[data-testid=field-speculative-draft-model]").Change("/models/sg-dflash");
            cut.Find("form").Submit();
            Assert.Contains("needs an algorithm", cut.Find("[data-testid=save-error]").TextContent, StringComparison.Ordinal);
            Assert.Null((await registry.GetAsync("sg", Ct))!.Speculative);

            cut.Find("[data-testid=field-speculative-method]").Change("DFLASH");
            cut.Find("[data-testid=field-speculative-num-tokens]").Change("16");
            cut.Find("form").Submit();
            var saved = (await registry.GetAsync("sg", Ct))!.Speculative!;
            Assert.Equal(("DFLASH", 16, "/models/sg-dflash"), (saved.Method, saved.NumSpeculativeTokens, saved.DraftModel));

            var reopened = ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "sg"));
            Assert.Equal("DFLASH", reopened.Find("[data-testid=field-speculative-method]").GetAttribute("value"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ================================================================ single DGX: as before P2

    [Fact]
    public void SingleNode_ShowsTheNodeReadOnly_AndNoPlacementSection_MoveOrUnplace()
    {
        var model = new ModelDefinition { Id = "m1", ServedName = "M1", ModelPath = "m1", Placement = new ModelPlacement("dgx") };
        string root = Path.Combine(Path.GetTempPath(), "ss-placement-single-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var ctx = FakeContext(new FakeModelRegistry([model]), root, new ListOnlyNodeRegistry(ListOnlyNodeRegistry.Node("dgx"), ListOnlyNodeRegistry.Node("old", revoked: true)));

            var edit = ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "m1"));
            var add = ctx.Render<ModelEditor>();

            foreach (var cut in new[] { edit, add })
            {
                Assert.Equal("dgx", cut.Find("[data-testid=field-node-readonly]").TextContent.Trim());
                Assert.Empty(cut.FindAll("[data-testid=placement-section]"));
                Assert.Empty(cut.FindAll("[data-testid=move-button]"));
                Assert.Empty(cut.FindAll("[data-testid=unplace-button]"));
                Assert.Empty(cut.FindAll("[data-testid=field-keep-serving]"));
                Assert.Empty(cut.FindAll("[data-testid=move-dialog]"));
            }

            Assert.Contains("Folder already on the DGX", add.Markup, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TheEmptyImageOption_NamesThePlacedNodesDefaultImage_ElseTheMastersImage()
    {
        // MN-124: an empty image launches the node's default image (LaunchSpecBuilder.DefaultImage); the option said the global one.
        var onPc = new ModelDefinition { Id = "m1", ServedName = "M1", ModelPath = "m1", Placement = new ModelPlacement("pc-office") };
        var onDgx = new ModelDefinition { Id = "m2", ServedName = "M2", ModelPath = "m2", Placement = new ModelPlacement("dgx") };
        string root = Path.Combine(Path.GetTempPath(), "ss-placement-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var pc = ListOnlyNodeRegistry.Node("pc-office") with { Approved = true, DefaultVllmImage = "registry.lan/vllm:pc" };
            var dgxNode = ListOnlyNodeRegistry.Node("dgx") with { Approved = true };
            using var ctx = FakeContext(new FakeModelRegistry([onPc, onDgx]), root, new ListOnlyNodeRegistry(dgxNode, pc));

            var cut = ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "m1"));
            cut.WaitForAssertion(() => Assert.Equal("Default image of pc-office (registry.lan/vllm:pc)",
                cut.Find("[data-testid=field-image-default]").TextContent.Trim()), Wait);

            var dgx = ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "m2"));
            string global = ctx.Services.GetRequiredService<IOptions<MirbadLLMOptions>>().Value.VllmImage;
            dgx.WaitForAssertion(() => Assert.Equal($"Default image ({global})", dgx.Find("[data-testid=field-image-default]").TextContent.Trim()), Wait);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SingleNode_WithoutANodeRegistry_ShowsTheBootstrapNode()
    {
        string root = Path.Combine(Path.GetTempPath(), "ss-placement-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var ctx = FakeContext(new FakeModelRegistry(), root, nodes: null);

            var cut = ctx.Render<ModelEditor>();

            Assert.Equal("dgx", cut.Find("[data-testid=field-node-readonly]").TextContent.Trim());
            Assert.Empty(cut.FindAll("[data-testid=placement-section]"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImageDigest_IsEditedNextToTheImageOverride_ValidatedAndDroppedWithAnotherImage()
    {
        const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var model = new ModelDefinition { Id = "m1", ServedName = "M1", ModelPath = "m1", Image = "repo/vllm:a", ImageDigest = Digest, Placement = new ModelPlacement("dgx") };
        string root = Path.Combine(Path.GetTempPath(), "ss-placement-digest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var registry = new FakeModelRegistry([model]);
            using var ctx = FakeContext(registry, root, nodes: null);
            var cut = ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "m1"));

            Assert.Equal(Digest, cut.Find("[data-testid=field-image-digest]").GetAttribute("value"));
            cut.Find("[data-testid=field-image-digest]").Change("sha256:XYZ");
            Assert.Contains("64 lowercase hex", cut.Find("[data-testid=field-image-digest-error]").TextContent, StringComparison.Ordinal);
            cut.Find("form").Submit();
            Assert.StartsWith("Image digest:", cut.Find("[data-testid=save-error]").TextContent.Trim(), StringComparison.Ordinal);
            Assert.Equal(Digest, (await registry.GetAsync("m1", Ct))!.ImageDigest);

            cut.Find("[data-testid=field-image-digest]").Change(Digest.Replace("0123", "4567", StringComparison.Ordinal));
            cut.Find("form").Submit();
            Assert.Equal(Digest.Replace("0123", "4567", StringComparison.Ordinal), (await registry.GetAsync("m1", Ct))!.ImageDigest);

            // Another image: the stored digest belongs to the old one and is dropped; the default image shows no digest field.
            var again = ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "m1"));
            again.Find("[data-testid=field-image]").Change("");
            Assert.Empty(again.FindAll("[data-testid=field-image-digest]"));
            again.Find("form").Submit();
            var saved = (await registry.GetAsync("m1", Ct))!;
            Assert.Equal((null, null), (saved.Image, saved.ImageDigest));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ================================================================ placement section over a real master

    [Fact]
    public async Task Editor_OnSeveralNodes_ShowsThePlacement_ReadOnlyNode_AndTheLiveFitOfEachGpu()
    {
        await using var cluster = await PlacementCluster.StartAsync(OnPc("m2", Gpu0));
        using var ctx = cluster.Context();

        var cut = Loaded(ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "m2").Add(x => x.LiveRefreshInterval, TimeSpan.FromMilliseconds(200))));

        Assert.Contains("pc-office", cut.Find("[data-testid=placement-node-id]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Move to another node", cut.Find("[data-testid=move-button]").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=unplace-button]"));
        Assert.Empty(cut.FindAll("[data-testid=field-node-readonly]"));
        Assert.Empty(cut.FindAll("[data-testid=field-node]")); // the node of an existing model is not a field: changing it is a move

        // The GPUs by current index and name, stored GPU ticked, each with its free VRAM from the fit preview (no GPU
        // picked = every GPU a single-GPU option).
        Assert.True(cut.Find($"[data-testid=placement-gpus-check-{Gpu0}]").HasAttribute("checked"));
        Assert.False(cut.Find($"[data-testid=placement-gpus-check-{Gpu1}]").HasAttribute("checked"));
        Assert.Contains("GPU 1 · NVIDIA GeForce RTX 5060 Ti · 16.0 GiB", cut.Find($"[data-testid=placement-gpus-option-{Gpu1}]").TextContent, StringComparison.Ordinal);
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("free after the", cut.Find($"[data-testid=placement-gpus-option-{Gpu1}]").TextContent, StringComparison.Ordinal);
            Assert.Contains("Fits on pc-office", cut.Find("[data-testid=placement-fit]").TextContent, StringComparison.Ordinal);
        }, Wait);
    }

    [Fact]
    public async Task GpuEdit_IsSavedAsUuids_WithTheStoredGeneration_AndATensorParallelArgIsLiftedIntoIt()
    {
        // Generation 3: a save that sent anything but the stored generation would be refused (placement_changed, D75).
        await using var cluster = await PlacementCluster.StartAsync(OnPc("m2", Gpu0, generation: 3));
        using var ctx = cluster.Context();
        var cut = Loaded(ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "m2")));

        cut.ChangeAt($"[data-testid=placement-gpus-check-{Gpu1}]", true);
        cut.Find("[data-testid=field-extra-args]").Change("--tensor-parallel-size 4\n--disable-log-requests");

        var lifted = cut.Find("[data-testid=extra-args-lifted]").TextContent;
        Assert.Contains("--tensor-parallel-size", lifted, StringComparison.Ordinal);
        Assert.Contains(VllmArgsNormalizer.TensorParallelField, lifted, StringComparison.Ordinal);
        Assert.Contains("--tensor-parallel-size 2", cut.Find("[data-testid=placement-gpus-hint]").TextContent, StringComparison.Ordinal);

        cut.Find("form").Submit();

        var saved = await ExitEnv.UntilValueAsync(async () =>
            await cluster.Master.Models.GetAsync("m2", Ct) is { Placement.GpuUuids.Count: 2 } m ? m : null, "the GPU edit saved");
        Assert.Equal([Gpu0, Gpu1], saved.Placement!.GpuUuids);
        Assert.Equal([0, 1], saved.Placement.GpuIndices);
        Assert.Equal(("pc-office", 4L), (saved.Placement.NodeId, saved.Placement.Generation));
        Assert.Equal(["--disable-log-requests"], saved.ExtraArgs);
        Assert.Empty(cut.FindAll("[data-testid=save-error]"));
    }

    [Fact]
    public async Task Save_AfterAMoveWhileThePageWasOpen_IsRefusedAsPlacementChanged_ThenSavesOntoTheNewPlacement()
    {
        await using var cluster = await PlacementCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        using var ctx = cluster.Context();
        var cut = Loaded(ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "m1").Add(x => x.LiveRefreshInterval, TimeSpan.FromHours(1))));

        // A move committed while the page is open (here through the service, as the API or another browser would).
        await cluster.Master.Moves.StartMoveAsync("m1", new ModelMoveRequest { TargetNodeId = "pc-office", GpuUuids = [Gpu1] }, new MoveCaller("admin:test", "test"), Ct);
        await cluster.Master.WaitForMoveEndAsync("m1");
        cut.Find("[data-testid=field-prefix-caching]").Change("off");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            string error = cut.Find("[data-testid=save-error]").TextContent;
            Assert.Contains("Not saved: the placement of m1 changed while this page was open", error, StringComparison.Ordinal);
            Assert.Contains("it is now on node pc-office, GPU 1", error, StringComparison.Ordinal);
        }, Wait);
        var unchanged = (await cluster.Master.Models.GetAsync("m1", Ct))!;
        Assert.True(unchanged.EnablePrefixCaching);
        Assert.Equal(("pc-office", 1L), (unchanged.Placement!.NodeId, unchanged.Placement.Generation));

        // The page now shows the new placement and keeps the edit: saving again lands on it.
        Assert.Contains("pc-office", cut.Find("[data-testid=placement-node-id]").TextContent, StringComparison.Ordinal);
        Assert.Equal("off", cut.Find("[data-testid=field-prefix-caching]").GetAttribute("value"));
        cut.Find("form").Submit();
        await ExitEnv.UntilAsync(async () => (await cluster.Master.Models.GetAsync("m1", Ct))!.EnablePrefixCaching == false, "the second save");
        var saved = (await cluster.Master.Models.GetAsync("m1", Ct))!;
        Assert.Equal(("pc-office", 1L), (saved.Placement!.NodeId, saved.Placement.Generation));
        Assert.Equal([Gpu1], saved.Placement.GpuUuids);
    }

    [Fact]
    public async Task Save_OfAHeldModelsGpuChange_NamesTheHolder_AndChangesNothing()
    {
        await using var cluster = await PlacementCluster.StartAsync(OnPc("m2", Gpu0));
        await cluster.Master.Ownership.TryAcquireAsync(new OwnershipRecord
        {
            Id = "edit-1", Kind = OwnershipKind.Edit, ModelId = "m2", Stage = "editing", StartedAt = DateTimeOffset.UtcNow, Description = "a restore",
        }, Ct);
        using var ctx = cluster.Context();
        var cut = Loaded(ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "m2")));

        cut.ChangeAt($"[data-testid=placement-gpus-check-{Gpu1}]", true);
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal(
            "Not saved: a restore (stage editing) holds m2, so its placement cannot change until that ends. Undo the placement change to save your other edits now, or save again when it has finished.",
            cut.Find("[data-testid=save-error]").TextContent.Trim()), Wait);
        Assert.Equal([Gpu0], (await cluster.Master.Models.GetAsync("m2", Ct))!.Placement!.GpuUuids);
    }

    [Fact]
    public async Task NewModel_ChoosesItsNodeAndGpu_ThenTheFoldersAndTheSaveAreThatNodes()
    {
        await using var cluster = await PlacementCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        cluster.Cluster.Pc.AddFolder("pc-only-ckpt");
        using var ctx = cluster.Context();
        var cut = Loaded(ctx.Render<ModelEditor>());

        Assert.Contains("Choose the node under Placement first", cut.Find("[data-testid=field-source-folder]").TextContent, StringComparison.Ordinal);
        cut.Find("[data-testid=field-id]").Change("new-on-pc");
        cut.Find("[data-testid=field-served-name]").Change("New-On-Pc");
        cut.Find("[data-testid=field-model-path]").Change("pc-only-ckpt");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Contains("More than one node exists", cut.Find("[data-testid=save-error]").TextContent, StringComparison.Ordinal), Wait);

        cut.ChangeAt("[data-testid=field-node]", "pc-office");
        cut.WaitForAssertion(() => Assert.Contains("pc-only-ckpt", cut.Find("[data-testid=field-source-folder]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Contains("Folder already on pc-office", cut.Markup, StringComparison.Ordinal);
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Contains("Tick at least one GPU on pc-office", cut.Find("[data-testid=save-error]").TextContent, StringComparison.Ordinal), Wait);

        cut.ChangeAt($"[data-testid=placement-gpus-check-{Gpu1}]", true);
        cut.ChangeAt("[data-testid=field-keep-serving]", true);
        cut.Find("form").Submit();

        var saved = await ExitEnv.UntilValueAsync(() => cluster.Master.Models.GetAsync("new-on-pc", Ct), "the new model");
        Assert.Equal(new ModelPlacement("pc-office", [Gpu1], [1]), saved.Placement);
        Assert.True(saved.KeepServingDuringJobs);
    }

    [Fact]
    public async Task NewModel_OnANodeThatDoesNotSelectGpus_ShowsEveryPlacementProblem()
    {
        // pc-office is discrete but its agent does not report gpus.uuids: the registry's validator refuses the GPUs.
        await using var cluster = await PlacementCluster.StartAsync([.. FakeAgentOptions.P1Features], MoveCluster.Model("m1", "m1-ckpt"));
        using var ctx = cluster.Context();
        var cut = Loaded(ctx.Render<ModelEditor>());

        cut.ChangeAt("[data-testid=field-node]", "pc-office");
        cut.ChangeAt($"[data-testid=placement-gpus-check-{Gpu0}]", true);
        cut.Find("[data-testid=field-id]").Change("refused");
        cut.Find("[data-testid=field-served-name]").Change("Refused");
        cut.Find("[data-testid=field-model-path]").Change("refused-ckpt");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            Assert.StartsWith("Not saved: the placement of refused was refused.", cut.Find("[data-testid=save-error]").TextContent.Trim(), StringComparison.Ordinal);
            Assert.Contains(AgentFeatures.GpuSelectionUuids, cut.Find("[data-testid=save-error-details]").TextContent, StringComparison.Ordinal);
        }, Wait);
        Assert.Null(await cluster.Master.Models.GetAsync("refused", Ct));
    }

    // ================================================================ move dialog

    [Fact]
    public async Task MoveDialog_MovesToTheTickedGpu_PassesThePickedSavedToken_AndNeverRendersIt()
    {
        const string token = "hf_editorMoveSecret0123456789";
        await using var cluster = await PlacementCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        var saved = await SaveTokenAsync(cluster, token);
        await cluster.Cluster.Pc.SetDownloadsHeldAsync(true);
        using var ctx = cluster.Context();
        var cut = Loaded(ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "m1")));

        cut.ClickAt("[data-testid=move-button]");
        Assert.Equal("Move m1 to another node", cut.Find("[data-testid=move-dialog-title]").TextContent);
        Assert.Contains("keeps serving on dgx", cut.Find("[data-testid=move-consequence]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=move-password]")); // stopped: nothing stops, no password
        Assert.True(cut.Find("[data-testid=move-confirm]").HasAttribute("disabled"));
        cut.ChangeAt("[data-testid=move-target]", "pc-office");
        Assert.True(cut.Find("[data-testid=move-confirm]").HasAttribute("disabled")); // a discrete target needs a GPU
        cut.ChangeAt($"[data-testid=move-gpus-check-{Gpu1}]", true);
        Assert.Contains("hf-test (…6789)", cut.Find("[data-testid=move-token]").TextContent, StringComparison.Ordinal); // picked by name
        cut.ChangeAt("[data-testid=move-token]", saved.Id);
        cut.ClickAt("[data-testid=move-confirm]");

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=move-steps]")), Wait);
        Assert.DoesNotContain(token, cut.Markup, StringComparison.Ordinal);
        await ExitEnv.UntilAsync(() => cluster.Cluster.Pc.DownloadTokens.Count > 0, "the target's download");
        Assert.Equal(token, Assert.Single(cluster.Cluster.Pc.DownloadTokens));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("current", cut.Find($"[data-testid=move-step-{MoveStages.Downloading}]").GetAttribute("data-state"));
            Assert.Contains("m1-ckpt", cut.Find("[data-testid=move-downloads]").TextContent, StringComparison.Ordinal);
        }, Wait);
        Assert.NotNull(cut.Find("[data-testid=move-cancel]"));

        await cluster.Cluster.Pc.SetDownloadsHeldAsync(false);
        cut.WaitForAssertion(() => Assert.Contains("m1 is placed on pc-office", cut.Find("[data-testid=move-done]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Equal("Moved m1 to pc-office", cut.Find("[data-testid=move-dialog-title]").TextContent);
        var moved = (await cluster.Master.Models.GetAsync("m1", Ct))!;
        Assert.Equal(new ModelPlacement("pc-office", [Gpu1], [1]), moved.Placement);
        cut.WaitForAssertion(() => Assert.Contains("pc-office", cut.Find("[data-testid=placement-node-id]").TextContent, StringComparison.Ordinal), Wait);

        cut.ClickAt("[data-testid=move-dialog-close]");
        cut.ClickAt("[data-testid=move-button]");
        cut.ChangeAt("[data-testid=move-target]", "dgx");
        cut.ChangeAt("[data-testid=move-token]", saved.Id);
        cut.ClickAt("[data-testid=move-dialog-close]");
        Assert.Equal("false", cut.Find("[data-testid=move-dialog]").GetAttribute("data-open"));
        Assert.Null(Secret(cut, "_hfTokenId")); // the pick is reset when the dialog closes
        Assert.Empty(cut.FindAll("[data-testid=move-token]"));
        Assert.DoesNotContain(token, cut.Markup, StringComparison.Ordinal);
        cut.ClickAt("[data-testid=move-button]");
        Assert.Equal("", cut.Find("[data-testid=move-token]").GetAttribute("value") ?? "");
        foreach (string file in Directory.GetFiles(cluster.Cluster.DataDir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".lock", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(token, await File.ReadAllTextAsync(file, Ct), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task MoveDialog_Refusal_IsShownInTheDialog_AndAManualModelCannotMove()
    {
        await using var cluster = await PlacementCluster.StartAsync(MoveCluster.Model("manual", "manual-ckpt") with { Sources = [] });
        using var ctx = cluster.Context();
        var cut = Loaded(ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "manual")));

        cut.ClickAt("[data-testid=move-button]");
        cut.ChangeAt("[data-testid=move-target]", "pc-office");
        cut.ChangeAt($"[data-testid=move-gpus-check-{Gpu0}]", true);
        const string token = "hf_refusedMoveSecret0123456789";
        cut.ChangeAt("[data-testid=move-token]", (await SaveTokenAsync(cluster, token)).Id);
        cut.ClickAt("[data-testid=move-confirm]");

        cut.WaitForAssertion(() => Assert.StartsWith("manual can't be moved: Model 'manual' has no Hugging Face sources",
            cut.Find("[data-testid=move-dialog-error]").TextContent.Trim(), StringComparison.Ordinal), Wait);
        Assert.Equal("setup", cut.Find("[data-testid=move-dialog]").GetAttribute("data-phase"));
        // Sent once (MN-132): a refused move never renders the token's value, and the pick is reset.
        Assert.Null(Secret(cut, "_hfTokenId"));
        Assert.Equal("", cut.Find("[data-testid=move-token]").GetAttribute("value") ?? "");
        Assert.DoesNotContain(token, cut.Markup, StringComparison.Ordinal);
        Assert.Equal("dgx", (await cluster.Master.Models.GetAsync("manual", Ct))!.Placement!.NodeId);
    }

    [Fact]
    public async Task MoveDialog_SourceOffline_PausesAndOffersTheForceCommit_WithTheMastersConsequence()
    {
        await using var cluster = await PlacementCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        await cluster.Master.StartModelAsync("m1");
        cluster.Cluster.Dgx.SetPartitioned(true);
        await cluster.Master.WaitForNodeAsync("dgx", n => !n.IsServing, "dgx offline");
        using var ctx = cluster.Context();
        var cut = Loaded(ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "m1")));

        cut.ClickAt("[data-testid=move-button]");
        cut.ChangeAt("[data-testid=move-target]", "pc-office");
        cut.ChangeAt($"[data-testid=move-gpus-check-{Gpu0}]", true);
        Assert.False(cut.Find("[data-testid=move-confirm]").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("[data-testid=move-password]"));
        cut.ClickAt("[data-testid=move-confirm]");

        cut.WaitForAssertion(() => Assert.Equal(MoveService.ForceCommitConsequenceFor("dgx"), cut.Find("[data-testid=move-force-consequence]").TextContent), Wait);
        Assert.Equal("If dgx comes back, the old instance may still be running there until it is stopped; no traffic is sent to it.",
            cut.Find("[data-testid=move-force-consequence]").TextContent);
        Assert.Equal(("dgx", 0L), PlacementOf(await cluster.Master.Models.GetAsync("m1", Ct))); // paused: never committed
        InputAt(cut, "[data-testid=move-force-confirm]", "pc-office");
        Assert.True(cut.Find("[data-testid=move-force-commit]").HasAttribute("disabled")); // the typed id must be the source's
        InputAt(cut, "[data-testid=move-force-confirm]", "dgx");
        cut.ClickAt("[data-testid=move-force-commit]");

        cut.WaitForAssertion(() => Assert.Contains("force-committed", cut.Find("[data-testid=move-done]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Equal(("pc-office", 1L), PlacementOf(await cluster.Master.Models.GetAsync("m1", Ct)));
        cluster.Cluster.Dgx.SetPartitioned(false);
    }

    [Fact]
    public async Task MoveDialog_Cancel_DuringTheDownload_LeavesTheModelOnItsNode()
    {
        await using var cluster = await PlacementCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        await cluster.Cluster.Pc.SetDownloadsHeldAsync(true);
        using var ctx = cluster.Context();
        var cut = Loaded(ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "m1")));
        cut.ClickAt("[data-testid=move-button]");
        cut.ChangeAt("[data-testid=move-target]", "pc-office");
        cut.ChangeAt($"[data-testid=move-gpus-check-{Gpu0}]", true);
        cut.ClickAt("[data-testid=move-confirm]");
        await ExitEnv.UntilAsync(async () => (await cluster.Master.Client("pc-office").GetDownloadAsync("m1-ckpt", Ct))?.State == DownloadState.Downloading, "the download");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=move-cancel]")), Wait);

        cut.ClickAt("[data-testid=move-cancel]");

        cut.WaitForAssertion(() =>
        {
            string failed = cut.Find("[data-testid=move-failed]").TextContent;
            Assert.Contains("Cancelled by the operator.", failed, StringComparison.Ordinal);
            Assert.Contains("Nothing changed: m1 stays on dgx", failed, StringComparison.Ordinal);
            Assert.Equal("failed", cut.Find($"[data-testid=move-step-{MoveStages.Downloading}]").GetAttribute("data-state"));
        }, Wait);
        Assert.Equal(("dgx", 0L), PlacementOf(await cluster.Master.Models.GetAsync("m1", Ct)));
        await cluster.Cluster.Pc.SetDownloadsHeldAsync(false);
    }

    [Fact]
    public async Task MoveDialog_StartRefusedOnTheTarget_EndsDoneWithTheReason_AndStartAnywayNamesTheRefusingGpu()
    {
        // 30 GiB on one 16 GiB card: the per-GPU admission of pc-office refuses the start after the commit (D94, D96).
        await using var cluster = await PlacementCluster.StartAsync(MoveCluster.Model("big", "big-ckpt") with { MemoryBudgetGiB = 30 });
        await cluster.Master.StartModelAsync("big");
        using var ctx = cluster.Context();
        var cut = Loaded(ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "big").Add(x => x.LiveRefreshInterval, TimeSpan.FromMilliseconds(300))));

        cut.ClickAt("[data-testid=move-button]");
        cut.ChangeAt("[data-testid=move-target]", "pc-office");
        cut.ChangeAt($"[data-testid=move-gpus-check-{Gpu0}]", true);
        cut.WaitForAssertion(() => Assert.Contains("Does not fit on pc-office", cut.Find("[data-testid=move-fit]").TextContent, StringComparison.Ordinal), Wait);
        cut.ClickAt("[data-testid=move-confirm]");

        cut.WaitForAssertion(() => Assert.Contains("starting it there was refused", cut.Find("[data-testid=move-done-error]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Equal("Moved big to pc-office, not started", cut.Find("[data-testid=move-dialog-title]").TextContent);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=placement-move-error]")), Wait); // the page says it too

        cut.ClickAt("[data-testid=move-start]");

        cut.WaitForAssertion(() => Assert.Equal("startanyway", cut.Find("[data-testid=move-dialog]").GetAttribute("data-phase")), Wait);
        string numbers = cut.Find("[data-testid=move-start-numbers]").TextContent;
        Assert.Contains("needs 30.0 GiB on GPU 0 (NVIDIA GeForce RTX 5060 Ti) on pc-office", numbers, StringComparison.Ordinal);
        Assert.Equal("Start big anyway?", cut.Find("[data-testid=move-dialog-title]").TextContent);
    }

    [Fact]
    public async Task RestartAnyway_AfterABudgetEditOnADiscreteNode_StatesTheRefusingGpusNumbers()
    {
        await using var cluster = await PlacementCluster.StartAsync(OnPc("m2", Gpu0) with { MemoryBudgetGiB = 12 });
        using (var start = await cluster.Master.Http.PostAsync("/api/models/m2/start", null, Ct))
        {
            Assert.True(start.IsSuccessStatusCode, $"start m2: {(int)start.StatusCode}");
        }

        await ExitEnv.UntilAsync(() => cluster.Master.Cache.GetRuntime("m2")?.State == ModelState.Ready, "m2 Ready on pc-office");
        using var ctx = cluster.Context();
        var cut = Loaded(ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "m2")));

        cut.Find("[data-testid=field-memory-budget]").Change("20");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=apply-dialog]").GetAttribute("data-open")), Wait);
        cut.ClickAt("[data-testid=apply-dialog-confirm]");

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=force-restart-dialog]").GetAttribute("data-open")), Wait);
        Assert.Contains("needs 20.0 GiB on GPU 0 (NVIDIA GeForce RTX 5060 Ti) on pc-office", cut.Find("[data-testid=force-restart-numbers]").TextContent, StringComparison.Ordinal);
    }

    // ================================================================ unplace

    [Fact]
    public async Task Unplace_ClearsThePlacement_AndThePageShowsTheModelUnplaced()
    {
        await using var cluster = await PlacementCluster.StartAsync(OnPc("m2", Gpu0));
        using var ctx = cluster.Context();
        var cut = Loaded(ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "m2")));

        cut.ClickAt("[data-testid=unplace-button]");
        Assert.Contains("It can't start or serve until you move it to a node again", cut.Find("[data-testid=unplace-consequence]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=unplace-password]")); // stopped: no password
        cut.ClickAt("[data-testid=confirm-unplace]");

        await ExitEnv.UntilAsync(async () => (await cluster.Master.Models.GetAsync("m2", Ct))!.Placement is null, "m2 unplaced");
        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("[data-testid=placement-unplaced]"));
            Assert.Contains("Move to a node", cut.Find("[data-testid=move-button]").TextContent, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-testid=unplace-button]"));
        }, Wait);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unplace_OfARunningModel_UsesTheSession_AndAnonymousIsRefused(bool signedIn)
    {
        await using var cluster = await PlacementCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        await cluster.Master.StartModelAsync("m1");
        using var ctx = cluster.Context(signedIn);
        var cut = Loaded(ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "m1")));

        cut.ClickAt("[data-testid=unplace-button]");
        Assert.Contains("m1 is stopped on dgx and then placed on no node", cut.Find("[data-testid=unplace-consequence]").TextContent, StringComparison.Ordinal);
        Assert.False(cut.Find("[data-testid=confirm-unplace]").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("[data-testid=unplace-password]"));
        cut.ClickAt("[data-testid=confirm-unplace]");
        if (!signedIn)
        {
            cut.WaitForAssertion(() => Assert.Contains("Sign in to the portal", cut.Find("[data-testid=unplace-error]").TextContent, StringComparison.Ordinal), Wait);
            Assert.Equal("dgx", (await cluster.Master.Models.GetAsync("m1", Ct))!.Placement!.NodeId);
            return;
        }
        await ExitEnv.UntilAsync(async () => (await cluster.Master.Models.GetAsync("m1", Ct))!.Placement is null, "m1 unplaced");
        Assert.Null(await cluster.Master.Client("dgx").GetRuntimeAsync("m1", Ct));
    }

    // ================================================================ components and rules

    [Fact]
    public void GpuPicker_TensorParallelNeedsEqualGpus_AndAMissingGpuIsListed()
    {
        using var ctx = new BunitContext();
        IReadOnlyList<GpuCapability> gpus =
        [
            Gpu(0, "A", "NVIDIA GeForce RTX 5060 Ti", 16), Gpu(1, "B", "NVIDIA GeForce RTX 5060 Ti", 16), Gpu(2, "C", "NVIDIA GeForce RTX 5070", 12),
        ];
        IReadOnlyList<string> selected = ["A", "GONE"];
        var cut = ctx.Render<GpuPicker>(p => p
            .Add(x => x.Gpus, gpus)
            .Add(x => x.Selected, selected)
            .Add(x => x.SelectedChanged, (IReadOnlyList<string> s) => selected = s)
            .Add(x => x.Node, "pc-office"));

        Assert.True(cut.Find("[data-testid=gpu-picker-check-C]").HasAttribute("disabled"));
        Assert.Contains("tensor parallel needs GPUs of the same model and memory", cut.Find("[data-testid=gpu-picker-option-C]").TextContent, StringComparison.Ordinal);
        Assert.False(cut.Find("[data-testid=gpu-picker-check-B]").HasAttribute("disabled"));
        Assert.Contains("no longer on pc-office", cut.Find("[data-testid=gpu-picker-missing-GONE]").TextContent, StringComparison.Ordinal);

        cut.Find("[data-testid=gpu-picker-check-GONE]").Change(false);
        cut.Find("[data-testid=gpu-picker-check-B]").Change(true);

        Assert.Equal(["A", "B"], selected);
        Assert.Contains("--tensor-parallel-size 2", cut.Find("[data-testid=gpu-picker-hint]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Offered_ListsServingAndOfflineApprovedNodes_AndAlwaysTheModelsOwn()
    {
        NodeRecord Node(string id, bool approved = true, bool revoked = false) => new()
        {
            Id = id, Uid = id + "-uid", Address = "https://x:9443", Approved = approved, Revoked = revoked,
        };
        NodeLiveStatus Live(string id, NodeState state) => new() { NodeId = id, NodeUid = id + "-uid", State = state };
        var states = new Dictionary<string, NodeLiveStatus>
        {
            ["online"] = Live("online", NodeState.Online), ["offline"] = Live("offline", NodeState.Offline),
            ["takeover"] = Live("takeover", NodeState.PendingTakeover), ["incompatible"] = Live("incompatible", NodeState.Incompatible),
        };
        NodeRecord[] nodes = [Node("online"), Node("offline"), Node("takeover"), Node("incompatible"), Node("pending", approved: false), Node("gone", revoked: true)];

        Assert.Equal(["offline", "online"], PlacementEditing.Offered(nodes, id => states.GetValueOrDefault(id), null).Select(n => n.Id));
        Assert.Equal(["offline", "online", "takeover"], PlacementEditing.Offered(nodes, id => states.GetValueOrDefault(id), "takeover").Select(n => n.Id));
        Assert.True(PlacementEditing.IsMultiNode(nodes));
        Assert.False(PlacementEditing.IsMultiNode([Node("dgx"), Node("old", revoked: true)]));
    }

    [Fact]
    public void PlacementRefusal_RefusesANodeWithoutTheModelsRuntime_AndSaysWhatItRuns()
    {
        // 2026-10-15: the move dialog offered the DGX (vLLM containers only) for a llama.cpp model on the Windows PC; the
        // master refused with placement_invalid, and the operator could not tell why from the dialog.
        PlacementNode Node(string id, params RuntimeCapability[] runtimes) => new(new NodeRecord
        {
            Id = id, Uid = id + "-uid", Address = "https://x:9443", Approved = true,
            LastCapabilities = new NodeCapabilities
            {
                Protocol = Protocol.Range, AgentVersion = "1.0.0", Os = new NodeOsInfo("linux", "Ubuntu 24.04"), Arch = "arm64",
                Memory = new NodeMemoryInfo(MemoryModel.Unified, 121), Runtimes = runtimes,
            },
        }, new NodeLiveStatus { NodeId = id, NodeUid = id + "-uid", State = NodeState.Online });
        var vllm = new RuntimeCapability { Kind = RuntimeKind.Vllm, Delivery = LaunchDelivery.Container };
        var llama = new RuntimeCapability { Kind = RuntimeKind.LlamaCpp, Delivery = LaunchDelivery.Process };
        var dgx = Node("dgx", vllm);
        var pc = Node("devpc", llama);
        var silent = Node("old"); // reports no runtimes: vLLM, as before P3

        Assert.Equal(
            "dgx runs vLLM only, so it cannot run this llama.cpp model. A move keeps the runtime: to serve it on dgx, add a vLLM model for it there.",
            PlacementEditing.PlacementRefusal(dgx, needsServing: true, RuntimeKind.LlamaCpp));
        Assert.Equal("no llama.cpp runtime", PlacementEditing.ShortRefusal(dgx, needsServing: true, RuntimeKind.LlamaCpp));
        Assert.Null(PlacementEditing.PlacementRefusal(pc, needsServing: true, RuntimeKind.LlamaCpp));
        Assert.Null(PlacementEditing.PlacementRefusal(dgx, needsServing: true, RuntimeKind.Vllm));
        Assert.Null(PlacementEditing.PlacementRefusal(silent, needsServing: true, RuntimeKind.Vllm));
        Assert.NotNull(PlacementEditing.PlacementRefusal(silent, needsServing: true, RuntimeKind.LlamaCpp));
        Assert.Null(PlacementEditing.PlacementRefusal(dgx, needsServing: true)); // the editor's callers pass no runtime
    }

    [Fact]
    public void Toggle_KeepsIndexOrder_ButTheStoredOrderForTheStoredSet_AndRefusesUnequalGpus()
    {
        IReadOnlyList<GpuCapability> gpus = [Gpu(0, "A", "X", 16), Gpu(1, "B", "X", 16), Gpu(2, "C", "Y", 12)];

        Assert.Equal(["A", "B"], PlacementEditing.Toggle(["B"], "A", on: true, gpus, stored: null));
        Assert.Equal(["B", "A"], PlacementEditing.Toggle(["B"], "A", on: true, gpus, stored: ["B", "A"]));
        Assert.Equal(["A"], PlacementEditing.Toggle(["A"], "C", on: true, gpus, stored: null));
        Assert.Equal(["C"], PlacementEditing.Toggle([], "C", on: true, gpus, stored: null)); // one GPU: no restriction

        var placement = PlacementEditing.Build("pc", ["B", "A"], gpus, generation: 7);
        Assert.Equal(new ModelPlacement("pc", ["B", "A"], [1, 0], 7), placement);
        Assert.Equal(7, placement.Generation);
        Assert.Null(PlacementEditing.Build("pc", ["B", "GONE"], gpus, 2).GpuIndices); // a card that is gone has no index
        Assert.Equal("on node pc, GPU 1 and GPU 0", PlacementEditing.Describe(placement, gpus));
    }

    [Fact]
    public void Refusals_NameTheModel_AndSayWhatToDoNext()
    {
        var current = new ModelDefinition { Id = "m1", ServedName = "M1", ModelPath = "m1", Placement = new ModelPlacement("pc", ["A"]) };
        Assert.Contains("it is now on node pc, GPU 0.", EditorRefusals.Save(new PlacementConflictException("x"), "m1", current, [Gpu(0, "A", "X", 16)]).Text, StringComparison.Ordinal);
        Assert.Equal("Not saved: m1 was deleted while this page was open. Go back to Models to add it again.",
            EditorRefusals.Save(new PlacementConflictException("x"), "m1").Text);
        var invalid = EditorRefusals.Save(new PlacementValidationException(
            [new PlacementProblem(PlacementProblemCodes.GpuUnknown, "No GPU A."), new PlacementProblem(PlacementProblemCodes.NodeDraining, "Draining.")]), "m1");
        Assert.Equal("Not saved: the placement of m1 was refused.", invalid.Text);
        Assert.Equal(["No GPU A.", "Draining."], invalid.Details);
        var busy = new ModelBusyException("m1", new OwnershipRecord
        {
            Id = "move-1", Kind = OwnershipKind.Move, ModelId = "m1", Stage = MoveStages.Downloading, StartedAt = DateTimeOffset.UtcNow, Description = "a move to node pc",
        });
        Assert.Equal("Not deleted: a move to node pc (stage downloading) holds m1. Delete it once that has finished, or cancel the move first.",
            EditorRefusals.Delete(busy, "m1").Text);
        Assert.Equal("Too many wrong passwords from this session: wait a minute, then try again.",
            EditorRefusals.Move(new MoveRefusedException(ErrorCodes.RateLimited, "x"), "m1").Text);
        Assert.Equal("Not moved: Node 'pc' is draining.", EditorRefusals.Move(new MoveRefusedException(ErrorCodes.NodeDraining, "Node 'pc' is draining."), "m1").Text);
        Assert.StartsWith("Not unplaced: m1 did not stop in time", EditorRefusals.Unplace(new TimeoutException(), "m1").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MoveSteps_SkipTheStopForAnUnplacedModel_AndTheStartForAStoppedOne()
    {
        OwnershipRecord Record(string stage, string source, bool running) => new()
        {
            Id = "move-1", Kind = OwnershipKind.Move, ModelId = "m1", Stage = stage, StartedAt = DateTimeOffset.UtcNow,
            Move = new MoveDetails { SourceNodeId = source, SourceNodeUid = "s", TargetNodeId = "pc", TargetNodeUid = "t", WasRunning = running },
        };

        var running = MoveProgress.Steps(Record(MoveStages.StoppingSource, "dgx", running: true));
        Assert.Equal([MoveStages.Downloading, MoveStages.StoppingSource, MoveStages.SourceStopped, MoveStages.PlacementCommitted, MoveStages.StartingTarget, MoveStages.Done],
            running.Select(s => s.Stage));
        Assert.Equal([MoveStepState.Done, MoveStepState.Current, MoveStepState.Todo], running.Take(3).Select(s => s.State));
        Assert.Equal([MoveStages.Downloading, MoveStages.PlacementCommitted, MoveStages.Done], MoveProgress.Steps(Record(MoveStages.Downloading, "", running: false)).Select(s => s.Stage));
        Assert.Equal(MoveStepState.Failed, MoveProgress.Steps(Record(MoveStages.Failed, "dgx", false), MoveStages.StoppingSource)[1].State);
        Assert.True(MoveProgress.DoneWithError(Record(MoveStages.Done, "dgx", true) with { Error = "refused" }));
    }

    [Fact]
    public void SettingChanges_ListAGpuChange_ByUuid_ButNotARenumbering()
    {
        var before = new ModelDefinition { Id = "m", ServedName = "m", ModelPath = "m", Placement = new ModelPlacement("pc", ["A"], [0]) };

        var change = Assert.Single(SettingChanges.RestartNeeded(before, before with { Placement = new ModelPlacement("pc", ["A", "B"], [0, 1], 1) }));
        Assert.Equal(new SettingChange("GPUs", "GPU 0 (A)", "GPU 0 (A), GPU 1 (B)"), change);
        Assert.Empty(SettingChanges.RestartNeeded(before, before with { Placement = new ModelPlacement("pc", ["A"], [3], 2) }));
        Assert.Empty(SettingChanges.RestartNeeded(before, before with { KeepServingDuringJobs = true }));
        Assert.Equal("Image digest", Assert.Single(SettingChanges.RestartNeeded(before, before with { ImageDigest = "sha256:" + new string('a', 64) })).Label);
    }

    [Fact]
    public void AdmissionNumbers_AreTheAgentsOwn_NamingTheRefusingGpuByItsIndex()
    {
        var details = System.Text.Json.JsonSerializer.SerializeToElement(new AdmissionRefusedDetails
        {
            NeedGiB = 30, FreeGiB = 15.2, AvailableGiB = 15.5, ReserveGiB = 0.3, Gpu = "B",
        }, AgentJson.Options);
        var agent = new AgentException(409, new AgentError { Code = ErrorCodes.AdmissionRefused, Message = "no", Details = details }, "pc");
        var refusal = new ModelMemoryAdmissionException("no", agent);

        var numbers = AdmissionNumbers.FromRefusal(refusal, "pc", [Gpu(0, "A", "X", 16), Gpu(1, "B", "RTX", 16)]);

        Assert.Equal(new AdmissionNumbers(30, 15.2, 15.5, 0.3, 0, 0, "GPU 1 (RTX) on pc", FromAgent: true), numbers);
        Assert.Null(AdmissionNumbers.FromRefusal(new ModelMemoryAdmissionException("no"), "pc"));
    }

    // ================================================================ helpers

    /// <summary>Saves <paramref name="value"/> as the master's saved token "hf-test" (Access > Tokens).</summary>
    private static Task<SavedToken> SaveTokenAsync(PlacementCluster cluster, string value) =>
        cluster.Master.Factory.Services.GetRequiredService<ITokenStore>().AddAsync("hf-test", value, Ct);

    /// <summary>A secret field of the move dialog (the token pick or a password), to prove it is not kept.</summary>
    private static string? Secret(IRenderedComponent<ModelEditor> cut, string field) =>
        (string?)typeof(MoveModelDialog).GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(cut.FindComponent<MoveModelDialog>().Instance)!;

    /// <summary>Waits for the editor's asynchronous load (the Placement section is its last part).</summary>
    private static IRenderedComponent<ModelEditor> Loaded(IRenderedComponent<ModelEditor> cut)
    {
        cut.WaitForElement("[data-testid=placement-section]", Wait);
        cut.WaitForElement("[data-testid=model-form]", Wait);
        return cut;
    }

    private static GpuCapability Gpu(int index, string uuid, string name, double totalGiB) =>
        new() { Index = index, Uuid = uuid, Name = name, MemoryTotalGiB = totalGiB };

    private static ModelDefinition OnPc(string id, string gpu, long generation = 0) =>
        MoveCluster.Model(id, id + "-ckpt") with { Placement = new ModelPlacement("pc-office", [gpu], [gpu == Gpu0 ? 0 : 1], generation) };

    private static (string NodeId, long Generation) PlacementOf(ModelDefinition? model) =>
        (model?.Placement?.NodeId ?? "(unplaced)", model?.Placement?.Generation ?? -1);

    private static BunitContext FakeContext(FakeModelRegistry registry, string modelsRoot, INodeRegistry? nodes)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelRegistry>(registry);
        ctx.Services.AddSingleton<IModelRuntime>(new FakeModelRuntime());
        ctx.Services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(new MemorySnapshot(128, 64)));
        ctx.Services.AddSingleton(Options.Create(new MirbadLLMOptions()));
        ctx.Services.AddNodeSourcing(modelsRoot);
        ctx.Services.AddSingleton<IHuggingFaceDownloader>(new FakeHuggingFaceDownloader());
        ctx.Services.AddSingleton<IHuggingFaceRepoInspector>(new FakeHuggingFaceRepoInspector());
        if (nodes is not null)
        {
            ctx.Services.AddSingleton(nodes);
        }

        return ctx;
    }

    /// <summary>
    /// Raises <c>oninput</c> on <paramref name="selector"/> (the dialogs bind their secrets per keystroke) and retries when a
    /// poll render retired the handler id first (as <see cref="BunitDispatch"/> does for change and click).
    /// </summary>
    private static void InputAt<TComponent>(IRenderedComponent<TComponent> cut, string selector, string value)
        where TComponent : IComponent
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                cut.InvokeAsync(() => cut.Find(selector).InputAsync(new ChangeEventArgs { Value = value })).GetAwaiter().GetResult();
                return;
            }
            catch (Bunit.Rendering.UnknownEventHandlerIdException) when (attempt < 5)
            {
                // Retired by a render between the snapshot and the dispatch; nothing was delivered.
            }
        }
    }

    /// <summary>
    /// A <see cref="MoveCluster"/> whose <c>pc-office</c> is a discrete 2 × 16 GiB node (GPU selection by UUID and per-GPU
    /// admission unless other features are given), its master, and bUnit contexts that resolve every service from it.
    /// </summary>
    private sealed class PlacementCluster : IAsyncDisposable
    {
        private PlacementCluster(MoveCluster cluster, MoveMaster master)
        {
            Cluster = cluster;
            Master = master;
        }

        public MoveCluster Cluster { get; }

        public MoveMaster Master { get; }

        public static Task<PlacementCluster> StartAsync(params ModelDefinition[] models) =>
            StartAsync([.. FakeAgentOptions.P1Features, AgentFeatures.GpuSelectionUuids, AgentFeatures.AdmissionDiscrete], models);

        public static async Task<PlacementCluster> StartAsync(IReadOnlyList<string> pcFeatures, params ModelDefinition[] models)
        {
            var cluster = await MoveCluster.StartAsync(
                pcOptions: o => o with { DiscreteGpus = FakeDiscreteGpu.TwoBy16, Features = pcFeatures, TotalMemoryGiB = 64, AvailableMemoryGiB = 60 },
                models: models);
            try
            {
                var master = await cluster.StartMasterAsync();
                // The editor reads the GPUs from the node's last capability report in nodes.json.
                await ExitEnv.UntilAsync(async () => (await master.Get<INodeRegistry>().GetAsync("pc-office", Ct))?.LastCapabilities?.Gpus.Count == 2,
                    "pc-office's GPUs in the registry");
                return new PlacementCluster(cluster, master);
            }
            catch
            {
                await cluster.DisposeAsync();
                throw;
            }
        }

        /// <summary>
        /// A bUnit context over the master's own service instances (registry with its validators, nodes, status cache,
        /// telemetry, moves, runtime, sourcing, maintenance), with a signed-in admin; the framework's services stay bUnit's.
        /// </summary>
        public BunitContext Context(bool signedIn = true)
        {
            var ctx = new BunitContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            var auth = ctx.AddAuthorization();
            auth.SetAuthorized("admin");
            auth.SetClaims(TestAdmin.User.Claims.ToArray());
            if (!signedIn) auth.SetNotAuthorized();
            var master = Master.Factory.Services;
            void Copy<T>()
                where T : class
            {
                if (master.GetService<T>() is { } instance)
                {
                    ctx.Services.AddSingleton(instance);
                }
            }

            Copy<IModelRegistry>();
            Copy<INodeRegistry>();
            Copy<INodeStatusCache>();
            Copy<INodeTelemetry>();
            Copy<IModelRuntime>();
            Copy<IModelLauncher>();
            Copy<IMemoryProbe>();
            Copy<IHuggingFaceDownloader>();
            Copy<IHuggingFaceRepoInspector>();
            Copy<RemoteModelFolders>();
            Copy<RemoteVllmCatalogService>();
            Copy<MoveService>();
            Copy<IOwnershipStore>();
            Copy<IMaintenanceMode>();
            Copy<IAgentClientFactory>();
            Copy<MirbadLLM.Orchestration.Agents.AgentOperationRunner>();
            Copy<TimeProvider>();
            Copy<ITokenStore>();
            ctx.Services.AddSingleton(master.GetRequiredService<IOptions<MirbadLLMOptions>>());
            return ctx;
        }

        public async ValueTask DisposeAsync()
        {
            await Master.DisposeAsync();
            await Cluster.DisposeAsync();
        }
    }
}
