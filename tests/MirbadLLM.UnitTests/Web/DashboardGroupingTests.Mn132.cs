using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Components.Pages;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// MN-132 (W9 review): the Dashboard's "Start anyway?" states the agent's own refusal numbers (D96), naming the refusing
/// GPU by its current index on a discrete node and keeping the single-DGX text; a manual start or stop of a model another
/// record holds (409 <c>model_busy</c>, D101) names the holder and what to do instead of an exception text.
/// </summary>
public sealed partial class DashboardGroupingTests
{
    [Fact]
    public void ForceStart_OnADiscreteNode_NamesTheRefusingGpuByIndex_WithTheAgentsNumbers()
    {
        var runtime = new FakeModelRuntime
        {
            RefuseUnlessForce = true,
            Refusal = AgentRefusal(new AdmissionRefusedDetails
            {
                NeedGiB = 6, FreeGiB = 3.5, AvailableGiB = 5.5, ReserveGiB = 2, ReservedByOthersGiB = 1.5, Gpu = "GPU-b",
            }, "pc"),
        };
        var cache = new FakeNodeStatusCache(Dgx(), Pc());
        var registry = new ListOnlyNodeRegistry(Record("dgx", "DGX Spark"), Record("pc", "Office PC", reserve: 4));
        using var ctx = Context([Model("coder", "pc", budget: 12, gpus: ["GPU-a", "GPU-b"])], runtime, cache, registry);

        var cut = ctx.Render<Models>();
        cut.ClickAt("[data-testid=start-coder]");

        Assert.Equal(
            "coder needs 6.0 GiB on GPU 1 (RTX 4060 Ti) on pc, but only 3.5 GiB is free after the 2.0 GiB reserve " +
            "(5.5 GiB free in total, 1.5 GiB held by other starts).",
            Squash(cut.Find("[data-testid=force-start-numbers]").TextContent));
        cut.ClickAt("[data-testid=force-start-dialog-confirm]");
        Assert.Contains(("coder", true), runtime.StartCalls);
    }

    [Fact]
    public void ForceStart_OnTheSingleDgx_KeepsItsText_WithTheAgentsNumbers()
    {
        var runtime = new FakeModelRuntime
        {
            RefuseUnlessForce = true,
            Refusal = AgentRefusal(new AdmissionRefusedDetails { NeedGiB = 36, FreeGiB = 10, AvailableGiB = 18, ReserveGiB = 8 }, "dgx"),
        };
        using var ctx = Context([Model("big", "dgx", budget: 36)], runtime, memory: new MemorySnapshot(119.7, 80.5));

        var cut = ctx.Render<Models>();
        cut.ClickAt("[data-testid=start-big]");

        // The agent's numbers (not the page's re-read of 80.5 GiB), in the words the page always used: no node is named.
        Assert.Equal(
            "big needs 36.0 GiB, but only 10.0 GiB is free after the 8.0 GiB reserve (18.0 GiB free in total).",
            Squash(cut.Find("[data-testid=force-start-numbers]").TextContent));
    }

    [Theory]
    [InlineData("start", OwnershipKind.Job, "Couldn't start qwen: a benchmark on node dgx is running on it. Wait for it to finish, or stop it from the banner at the top of the page.")]
    [InlineData("stop", OwnershipKind.Job, "Couldn't stop qwen: a benchmark on node dgx is running on it. Wait for it to finish, or stop it from the banner at the top of the page.")]
    [InlineData("stop", OwnershipKind.Move, "Couldn't stop qwen: a move to node pc (stage downloading) is running on it. Wait for the move to finish, or cancel it in the model's editor.")]
    [InlineData("start", OwnershipKind.Restore, "Couldn't start qwen: a restore (configuration import) is running on it. Wait for the restore to finish; the Backup page shows its progress.")]
    public void StartOrStop_OfAHeldModel_NamesTheHolder_AndNothingIsSent(string action, OwnershipKind kind, string expected)
    {
        var runtime = new FakeModelRuntime();
        if (action == "stop")
        {
            runtime.SetStatus(new ModelStatus("qwen", ModelState.Ready, HostPort: 8101));
        }

        using var ctx = Context([Model("qwen", "dgx")], runtime);
        ctx.Services.AddSingleton<IOwnershipStore>(new OneRecordStore(Holder(kind, "qwen")));

        var cut = ctx.Render<Models>();
        cut.ClickAt($"[data-testid={action}-qwen]");

        Assert.Equal(expected, cut.Find("[data-testid=models-error]").TextContent.Trim());
        Assert.Empty(runtime.StartCalls);
        Assert.Empty(runtime.StopCalls);
    }

    internal static ModelMemoryAdmissionException AgentRefusal(AdmissionRefusedDetails details, string nodeId)
    {
        var error = new AgentError
        {
            Code = ErrorCodes.AdmissionRefused, Message = "refused", Details = System.Text.Json.JsonSerializer.SerializeToElement(details, AgentJson.Options),
        };
        return new ModelMemoryAdmissionException("refused", new AgentException(409, error, nodeId));
    }

    internal static OwnershipRecord Holder(OwnershipKind kind, string modelId) => kind switch
    {
        OwnershipKind.Job => new OwnershipRecord
        {
            Id = "job-1", Kind = kind, ModelId = modelId, Stage = "benchmark", StartedAt = DateTimeOffset.UtcNow, Description = "a benchmark on node dgx",
        },
        OwnershipKind.Move => new OwnershipRecord
        {
            Id = "move-1", Kind = kind, ModelId = modelId, Stage = MoveStages.Downloading, StartedAt = DateTimeOffset.UtcNow, Description = "a move to node pc",
        },
        _ => new OwnershipRecord
        {
            Id = "restore-1", Kind = kind, ModelId = modelId, Stage = "importing", StartedAt = DateTimeOffset.UtcNow,
            Description = "a restore (configuration import)",
        },
    };

    private static string Squash(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>An ownership store holding one record (the guard only reads it).</summary>
    internal sealed class OneRecordStore(OwnershipRecord record) : IOwnershipStore
    {
        public Task<IReadOnlyList<OwnershipRecord>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<OwnershipRecord>>([record]);

        public Task<OwnershipRecord?> GetForModelAsync(string modelId, CancellationToken ct = default) =>
            Task.FromResult(record.ModelId == modelId ? record : null);

        public Task<OwnershipAcquireResult> TryAcquireAsync(OwnershipRecord record, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<OwnershipRecord?> UpdateAsync(string recordId, Func<OwnershipRecord, OwnershipRecord> update, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> ReleaseAsync(string recordId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
