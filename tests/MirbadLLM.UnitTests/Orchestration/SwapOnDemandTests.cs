using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Audit;
using MirbadLLM.UnitTests.Orchestration.Fakes;
using MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names.

namespace MirbadLLM.UnitTests.Orchestration;

/// <summary>Idle stop: a Ready model with an idle limit is stopped once nothing has asked for it for that long.</summary>
public sealed class IdleStopServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeModelRegistry _registry = new();
    private readonly SwapRuntime _runtime = new();
    private readonly SwapUse _use = new();
    private readonly ManualTimeProvider _time = new();

    private IdleStopService Service(IMaintenanceMode? maintenance = null, IOwnershipStore? ownership = null) =>
        new(_registry, _runtime, _use, NullLogger<IdleStopService>.Instance, _time, maintenance, ownership);

    private async Task AfterAsync(IdleStopService service, int minutes)
    {
        _time.Advance(TimeSpan.FromMinutes(minutes));
        await service.SweepAsync(Ct);
    }

    [Fact]
    public async Task StopsAReadyModel_OnceItsLimitHasPassedSinceItBecameReady()
    {
        _registry.Add(Swap.Model("chat", idle: 15));
        _runtime.Set("chat", ModelState.Ready);
        var service = Service();

        await service.SweepAsync(Ct); // first seen Ready: the count starts here
        await AfterAsync(service, 14);
        Assert.Empty(_runtime.Stops);

        await AfterAsync(service, 1);

        var stop = Assert.Single(_runtime.Stops);
        Assert.Equal("chat", stop.Id);
        Assert.Equal(AuditActors.SystemIdleStop, stop.Actor);
    }

    [Fact]
    public async Task CountsFromTheLastRequest_WhenThatIsLater()
    {
        _registry.Add(Swap.Model("chat", idle: 15));
        _runtime.Set("chat", ModelState.Ready);
        var service = Service();
        await service.SweepAsync(Ct);

        _time.Advance(TimeSpan.FromMinutes(10));
        _use.Last["chat-served"] = _time.GetUtcNow();
        await AfterAsync(service, 10); // 20 minutes Ready, 10 since the request
        Assert.Empty(_runtime.Stops);

        await AfterAsync(service, 5);
        Assert.Equal("chat", Assert.Single(_runtime.Stops).Id);
    }

    [Fact]
    public async Task NeverStops_AModelInUse_NotReady_OrWithoutALimit()
    {
        _registry.Add(Swap.Model("busy", idle: 1)).Add(Swap.Model("loading", idle: 1)).Add(Swap.Model("resident"));
        _runtime.Set("busy", ModelState.Ready).Set("loading", ModelState.Starting).Set("resident", ModelState.Ready);
        _use.Busy.Add("busy-served");
        var service = Service();

        await service.SweepAsync(Ct);
        await AfterAsync(service, 600);

        Assert.Empty(_runtime.Stops);
    }

    [Fact]
    public async Task AModelThatStartedAgain_GetsItsFullLimitAgain()
    {
        _registry.Add(Swap.Model("chat", idle: 15));
        _runtime.Set("chat", ModelState.Ready);
        var service = Service();
        await service.SweepAsync(Ct);

        _runtime.Set("chat", ModelState.Stopped);
        await AfterAsync(service, 10);
        _runtime.Set("chat", ModelState.Ready);
        await AfterAsync(service, 2); // Ready again, 12 minutes after it was first seen
        await AfterAsync(service, 8);
        Assert.Empty(_runtime.Stops);

        await AfterAsync(service, 7);
        Assert.Single(_runtime.Stops);
    }

    [Fact]
    public async Task LeavesAModelAlone_WhileAJobHoldsItsNode()
    {
        _registry.Add(Swap.Model("on-pc", idle: 5)).Add(Swap.Model("on-dgx", idle: 5, node: "dgx"));
        _runtime.Set("on-pc", ModelState.Ready).Set("on-dgx", ModelState.Ready);
        var window = new MaintenanceState("job-1", MaintenanceJobKinds.Benchmark, "other", _time.GetUtcNow(), _time.GetUtcNow().AddHours(1), "pc");
        var service = Service(new SwapWindows(window));

        await service.SweepAsync(Ct);
        await AfterAsync(service, 30);

        Assert.Equal("on-dgx", Assert.Single(_runtime.Stops).Id);
    }

    [Fact]
    public async Task LeavesAModelAlone_WhileAMoveJobOrRestoreHoldsIt()
    {
        _registry.Add(Swap.Model("chat", idle: 5));
        _runtime.Set("chat", ModelState.Ready);
        var record = new OwnershipRecord { Id = "r1", Kind = OwnershipKind.Move, ModelId = "chat", Stage = "copying", StartedAt = _time.GetUtcNow() };
        var service = Service(ownership: new DashboardGroupingTests.OneRecordStore(record));

        await service.SweepAsync(Ct);
        await AfterAsync(service, 30);

        Assert.Empty(_runtime.Stops);
    }

    [Fact]
    public async Task AStopThatFails_IsTriedAgainOnTheNextPass()
    {
        _registry.Add(Swap.Model("chat", idle: 5));
        _runtime.Set("chat", ModelState.Ready);
        int calls = 0;
        _runtime.OnStop = _ => ++calls == 1 ? throw new NodeUnavailableException("node is offline") : Task.CompletedTask;
        var service = Service();
        await service.SweepAsync(Ct);

        await AfterAsync(service, 5); // throws inside, must not escape
        Assert.Equal(ModelState.Ready, (await _runtime.GetStatusAsync("chat", Ct)).State);

        await AfterAsync(service, 1);
        Assert.Equal(ModelState.Stopped, (await _runtime.GetStatusAsync("chat", Ct)).State);
    }

    [Fact]
    public async Task WithoutRouterStats_NothingIsStopped()
    {
        _registry.Add(Swap.Model("chat", idle: 1));
        _runtime.Set("chat", ModelState.Ready);
        var service = new IdleStopService(_registry, _runtime, stats: null, NullLogger<IdleStopService>.Instance, _time);

        await service.SweepAsync(Ct);
        await AfterAsync(service, 60);

        Assert.Empty(_runtime.Stops);
    }
}

/// <summary>Make room: after an admission refusal, idle models that allow it are stopped, least recently used first.</summary>
public sealed class RoomMakerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeModelRegistry _registry = new();
    private readonly SwapRuntime _runtime = new();
    private readonly SwapUse _use = new();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    private RoomMaker Maker(IMaintenanceMode? maintenance = null, IOwnershipStore? ownership = null) =>
        new(_registry, _runtime, _use, NullLogger<RoomMaker>.Instance, maintenance, ownership);

    private void Ready(ModelDefinition model, int? lastRequestMinutesAgo = null)
    {
        _registry.Add(model);
        _runtime.Set(model.Id, ModelState.Ready);
        if (lastRequestMinutesAgo is { } minutes)
        {
            _use.Last[model.ServedName] = _now.AddMinutes(-minutes);
        }
    }

    [Fact]
    public async Task StopsTheLeastRecentlyUsedModels_AndOnlyAsManyAsTheMissingMemoryNeeds()
    {
        _registry.Add(Swap.Model("wanted", budget: 12, room: false));
        Ready(Swap.Model("old", budget: 8), lastRequestMinutesAgo: 30);
        Ready(Swap.Model("recent", budget: 8), lastRequestMinutesAgo: 1);
        Ready(Swap.Model("never", budget: 8)); // no request since the controller started: the oldest

        bool made = await Maker().TryMakeRoomAsync("wanted", Swap.Refusal(need: 12, free: 2), Ct);

        Assert.True(made);
        Assert.Equal(["never", "old"], _runtime.Stops.Select(s => s.Id));
        Assert.All(_runtime.Stops, s => Assert.Equal(AuditActors.SystemMakeRoom, s.Actor));
    }

    [Fact]
    public async Task LeavesOut_ModelsInUse_NotReady_WithoutTheFlag_OnAnotherNode_AndTheWantedModelItself()
    {
        _registry.Add(Swap.Model("wanted", budget: 8));
        Ready(Swap.Model("in-use"));
        _use.Busy.Add("in-use-served");
        _registry.Add(Swap.Model("loading"));
        _runtime.Set("loading", ModelState.Starting);
        Ready(Swap.Model("resident", room: false));
        Ready(Swap.Model("elsewhere", node: "dgx"));
        Ready(Swap.Model("idle"), lastRequestMinutesAgo: 1);

        Assert.True(await Maker().TryMakeRoomAsync("wanted", Swap.Refusal(need: 8, free: 0), Ct));

        Assert.Equal(["idle"], _runtime.Stops.Select(s => s.Id));
    }

    [Fact]
    public async Task StopsNothing_WhenTheCandidatesTogetherDoNotCoverWhatIsMissing()
    {
        _registry.Add(Swap.Model("wanted", budget: 14));
        Ready(Swap.Model("small", budget: 4));
        Ready(Swap.Model("in-use", budget: 12));
        _use.Busy.Add("in-use-served");

        Assert.False(await Maker().TryMakeRoomAsync("wanted", Swap.Refusal(need: 14, free: 1), Ct));

        Assert.Empty(_runtime.Stops);
    }

    [Fact]
    public async Task OnDiscreteGpus_CountsOnlyModelsOnTheGpuThatIsShort_ByTheirShareThere()
    {
        _registry.Add(Swap.Model("wanted", budget: 12, gpus: ["GPU-2"]));
        Ready(Swap.Model("other-card", budget: 8, gpus: ["GPU-1"])); // the least recently used, but its memory is not needed
        Ready(Swap.Model("same-card", budget: 6, gpus: ["GPU-2"]), lastRequestMinutesAgo: 20);
        Ready(Swap.Model("both-cards", budget: 16, gpus: ["GPU-1", "GPU-2"]), lastRequestMinutesAgo: 5); // 8 GiB on each

        Assert.True(await Maker().TryMakeRoomAsync("wanted", Swap.Refusal(need: 12, free: 0, gpu: "GPU-2"), Ct));

        Assert.Equal(["same-card", "both-cards"], _runtime.Stops.Select(s => s.Id));
    }

    [Fact]
    public async Task StopsNothing_ForHostMemoryOnADiscreteNode_OrWithoutTheAgentsNumbers()
    {
        _registry.Add(Swap.Model("wanted", gpus: ["GPU-1"]));
        Ready(Swap.Model("idle", gpus: ["GPU-1"]));
        var maker = Maker();

        Assert.False(await maker.TryMakeRoomAsync("wanted", Swap.Refusal(need: 6, free: 1, host: true), Ct));
        Assert.False(await maker.TryMakeRoomAsync("wanted", new ModelMemoryAdmissionException("not enough memory"), Ct));

        Assert.Empty(_runtime.Stops);
    }

    [Fact]
    public async Task StopsNothing_WhileAJobHoldsTheNode_AndNeverAModelAMoveHolds()
    {
        _registry.Add(Swap.Model("wanted"));
        Ready(Swap.Model("idle"));
        var window = new MaintenanceState("job-1", MaintenanceJobKinds.Tuning, "idle", _now, _now.AddHours(1), "pc");
        var record = new OwnershipRecord { Id = "r1", Kind = OwnershipKind.Move, ModelId = "idle", Stage = "copying", StartedAt = _now };

        Assert.False(await Maker(new SwapWindows(window)).TryMakeRoomAsync("wanted", Swap.Refusal(need: 8, free: 0), Ct));
        Assert.False(await Maker(ownership: new DashboardGroupingTests.OneRecordStore(record)).TryMakeRoomAsync("wanted", Swap.Refusal(need: 8, free: 0), Ct));

        Assert.Empty(_runtime.Stops);
    }

    [Fact]
    public async Task TwoRequestsOnOneNode_TheSecondWaitsAndStopsNothingMore()
    {
        _registry.Add(Swap.Model("first-wanted")).Add(Swap.Model("second-wanted"));
        Ready(Swap.Model("a"), lastRequestMinutesAgo: 30);
        Ready(Swap.Model("b"), lastRequestMinutesAgo: 10);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runtime.OnStop = _ =>
        {
            stopping.TrySetResult();
            return release.Task;
        };
        var maker = Maker();

        var first = maker.TryMakeRoomAsync("first-wanted", Swap.Refusal(need: 8, free: 0), Ct);
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var second = maker.TryMakeRoomAsync("second-wanted", Swap.Refusal(need: 8, free: 0), Ct);
        Assert.False(second.IsCompleted); // one round at a time per node
        release.SetResult();

        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(10), Ct)); // room was made meanwhile: ask admission again
        Assert.Equal(["a"], _runtime.Stops.Select(s => s.Id));
    }

    [Fact]
    public async Task ARequestThatComesAfterARound_MakesItsOwnRoom()
    {
        _registry.Add(Swap.Model("first-wanted")).Add(Swap.Model("second-wanted"));
        Ready(Swap.Model("a"), lastRequestMinutesAgo: 30);
        Ready(Swap.Model("b"), lastRequestMinutesAgo: 10);
        var maker = Maker();

        Assert.True(await maker.TryMakeRoomAsync("first-wanted", Swap.Refusal(need: 8, free: 0), Ct));
        Assert.True(await maker.TryMakeRoomAsync("second-wanted", Swap.Refusal(need: 8, free: 0), Ct));

        Assert.Equal(["a", "b"], _runtime.Stops.Select(s => s.Id));
    }

    [Fact]
    public async Task AStopThatFails_MakesNoRoom()
    {
        _registry.Add(Swap.Model("wanted"));
        Ready(Swap.Model("idle"));
        _runtime.OnStop = _ => throw new NodeUnavailableException("node is offline");

        Assert.False(await Maker().TryMakeRoomAsync("wanted", Swap.Refusal(need: 8, free: 0), Ct));
    }
}

internal static class Swap
{
    public static ModelDefinition Model(string id, double budget = 8, string node = "pc", bool room = true, int? idle = null, string[]? gpus = null) => new()
    {
        Id = id,
        ServedName = id + "-served",
        ModelPath = id,
        MemoryBudgetGiB = budget,
        StopToMakeRoom = room,
        IdleStopMinutes = idle,
        Placement = new ModelPlacement(node, gpus),
    };

    /// <summary>The refusal as <c>RemoteModelRuntime</c> throws it: the agent's <c>admission_refused</c> with its numbers.</summary>
    public static ModelMemoryAdmissionException Refusal(double need, double free, string? gpu = null, bool? host = null) =>
        (ModelMemoryAdmissionException)AgentErrorMapper.ToMasterException(new AgentException(
            409,
            new AgentError
            {
                Code = ErrorCodes.AdmissionRefused,
                Message = "refused",
                Details = JsonSerializer.SerializeToElement(
                    new AdmissionRefusedDetails { NeedGiB = need, FreeGiB = free, AvailableGiB = free, ReserveGiB = 0, Gpu = gpu, Host = host },
                    AgentJson.Options),
            },
            "pc"));
}

/// <summary>States set by the test; every stop is recorded with the audit actor it ran under, then the model reads Stopped.</summary>
internal sealed class SwapRuntime : IModelRuntime
{
    private readonly ConcurrentDictionary<string, ModelState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Id, string? Actor)> _stops = new();

    public IReadOnlyList<(string Id, string? Actor)> Stops => [.. _stops];

    /// <summary>Runs inside <see cref="StopAsync"/> before the model reads Stopped; a throw fails the stop.</summary>
    public Func<string, Task>? OnStop { get; set; }

    public SwapRuntime Set(string id, ModelState state)
    {
        _states[id] = state;
        return this;
    }

    public Task<IReadOnlyList<ModelStatus>> ListStatusAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ModelStatus>>([.. _states.Select(s => new ModelStatus(s.Key, s.Value))]);

    public Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(new ModelStatus(id, _states.GetValueOrDefault(id, ModelState.Stopped)));

    public Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default) =>
        Task.FromResult(new ModelStatus(id, _states[id] = ModelState.Starting));

    public async Task StopAsync(string id, CancellationToken ct = default)
    {
        _stops.Enqueue((id, AuditScope.Current?.Actor));
        if (OnStop is { } onStop)
        {
            await onStop(id);
        }

        _states[id] = ModelState.Stopped;
    }

#pragma warning disable CS1998
    public async IAsyncEnumerable<string> StreamLogsAsync(string id, int tail = 200, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield break;
    }
#pragma warning restore CS1998
}

/// <summary>The router's view of use, set by the test.</summary>
internal sealed class SwapUse : IRouterStats
{
    public HashSet<string> Busy { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, DateTimeOffset> Last { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<RouterModelStats> Snapshot() => [];

    public bool InUse(string servedName) => Busy.Contains(servedName);

    public DateTimeOffset? LastRequestAt(string servedName) => Last.TryGetValue(servedName, out var at) ? at : null;
}

internal sealed class SwapWindows(params MaintenanceState[] windows) : IMaintenanceMode
{
    public MaintenanceState? Current => windows.FirstOrDefault();

    public IReadOnlyList<MaintenanceState> Active => windows;
}
