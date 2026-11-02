using Microsoft.Extensions.Options;
using System.Text.Json;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.UnitTests.Multinode;
using MirbadLLM.UnitTests.Orchestration.Fakes;

namespace MirbadLLM.UnitTests.Orchestration;

#pragma warning disable CA1707

public sealed class ModelRecoveryStateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "model-recovery-" + Guid.NewGuid().ToString("N"));

    private ModelRecoveryState Open(IMaintenanceMode? maintenance = null) =>
        new(Options.Create(new MirbadLLMOptions { DataPath = _directory }), maintenance);

    internal static NodeLiveStatus Node(string boot = "before", params string[] running) => new()
    {
        NodeId = "pc",
        NodeUid = "pc-uid",
        BootId = boot,
        State = NodeState.Online,
        Runtimes = [.. running.Select(id => new NodeModelRuntime { NodeId = "pc", ModelId = id, State = ModelState.Ready })],
    };

    [Fact]
    public void Crashes_PauseAutomaticRestartsAtTheLimit_AndAStartByHandClearsThePause()
    {
        var state = Open();
        var node = Node("boot", "a");
        state.Observe(node);

        Assert.False(state.RecordCrash("pc", "a", "crash 1").Paused);
        Assert.True(state.CanAutoRestart(node, "a"));
        Assert.False(state.RecordCrash("pc", "a", "crash 2").Paused);
        var paused = state.RecordCrash("pc", "a", "crash 3");

        Assert.Equal((3, 3, true, "crash 3", "pc"), (paused.Crashes, paused.Limit, paused.Paused, paused.LastError, paused.NodeId));
        Assert.False(state.CanAutoRestart(node, "a"));
        Assert.True(Open().CrashRestartOf("a")?.Paused); // Survives a controller restart.
        Assert.True(File.Exists(Path.Combine(_directory, "restart-pauses.json")));

        state.ResetCrashes("a");
        Assert.Null(state.CrashRestartOf("a"));
        Assert.Null(Open().CrashRestartOf("a"));
        Assert.True(state.CanAutoRestart(node, "a"));

        state.Observe(node, "a", ModelState.Stopped);
        Assert.False(state.CanAutoRestart(node, "a")); // An acknowledged Stop is not a crash.
    }

    [Fact]
    public void Reboot_QueuesOnlyPreviouslyRunningModels_AndSurvivesControllerRestarts()
    {
        var before = Open();
        before.Observe(Node("before", "a", "b"));
        before.Observe(Node("before", "a", "b"), "b", ModelState.Stopped);

        var after = Open();
        var rebooted = Node("after");
        after.Observe(rebooted);

        Assert.Equal(["a"], after.Pending(rebooted));
        Assert.Equal(["a"], Open().Pending(rebooted)); // Controller crashes again while the recovery queue is pending.
        after.Observe(rebooted, "a", ModelState.Starting);
        Assert.Empty(Open().Pending(rebooted));

        var secondReboot = Node("after-again");
        var next = Open();
        next.Observe(secondReboot);
        Assert.Equal(["a"], next.Pending(secondReboot));
    }

    [Fact]
    public void SameBootReconnect_DoesNotRestartModels_AndAlreadyAdoptedModelsAreSkipped()
    {
        var state = Open();
        state.Observe(Node("before", "a", "b"));
        state.Observe(Node("before", "a", "b") with { State = NodeState.Offline });
        Assert.Empty(state.Pending(Node("before")));

        var adopted = Node("after", "a");
        state.Observe(adopted);
        Assert.Equal(["b"], state.Pending(adopted));
    }

    [Fact]
    public void ExplicitStop_CancelsARecoveryThatHasNotStartedYet()
    {
        var state = Open();
        state.Observe(Node("before", "a"));
        var rebooted = Node("after");
        state.Observe(rebooted);
        state.Observe(rebooted, "a", ModelState.Stopped);

        Assert.Empty(Open().Pending(rebooted));
        state.Observe(Node("next"));
        Assert.Empty(state.Pending(Node("next")));
    }

    [Fact]
    public void AnAcknowledgedStop_WinsOverDelayedRunningEvents_UntilANewStart()
    {
        var state = Open();
        var before = Node("before", "a");
        state.Observe(before);
        state.Observe(before, "a", ModelState.Stopped);
        state.Observe(before, "a"); // Ready from the event stream, delivered after the Stop API returned.
        state.Observe(before); // A concurrent snapshot must not undo the user's intent either.
        var rebooted = Node("after");
        state.Observe(rebooted);
        Assert.Empty(Open().Pending(rebooted));

        state.Observe(rebooted, "a", ModelState.Starting);
        state.Observe(Node("next"));
        Assert.Equal(["a"], Open().Pending(Node("next")));
    }

    [Fact]
    public void AReenrolledNode_DoesNotInheritTheOldMachinesRunningModels()
    {
        var state = Open();
        state.Observe(Node("before", "a"));
        var replacement = Node("after") with { NodeUid = "replacement-uid" };
        state.Observe(replacement);
        Assert.Empty(state.Pending(replacement));
    }

    [Fact]
    public void LateResultsFromThePreviousBoot_CannotEraseTheNewBootsRecovery()
    {
        var state = Open();
        var before = Node("before", "a");
        state.Observe(before);
        var rebooted = Node("after");
        state.Observe(rebooted);
        state.Observe(before, "a", ModelState.Stopped);

        Assert.Equal(["a"], state.Pending(rebooted));
        Assert.Empty(state.Pending(before));
    }

    [Fact]
    public void OfflineAndDrainingNodes_DoNotConsumeTheQueue()
    {
        var state = Open();
        state.Observe(Node("before", "a"));
        var rebooted = Node("after");
        state.Observe(rebooted);
        Assert.Empty(state.Pending(rebooted with { State = NodeState.Offline }));
        Assert.Empty(state.Pending(rebooted with { Draining = true }));
        Assert.Equal(["a"], state.Pending(rebooted));
    }

    [Fact]
    public void TemporaryJobStopsAndTuningCandidates_DoNotReplaceTheRunningSet()
    {
        var maintenance = new MaintenanceModeService(Options.Create(new MirbadLLMOptions { DataPath = string.Empty }));
        var state = Open(maintenance);
        state.Observe(Node("before", "a"));
        maintenance.Enter(new MaintenanceState("job", "tuning", "b", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "pc"));
        using (ModelWriteScope.AsOwner("job"))
        {
            state.Observe(Node("before"), "a", ModelState.Stopped);
        }

        state.Observe(Node("before", "b"));
        maintenance.Exit("job");

        state.Observe(Node("after"));
        Assert.Equal(["a"], state.Pending(Node("after")));
    }

    [Fact]
    public void AManualStopDuringMaintenance_IsRemembered_AndTheFinalJobStateIsRefreshed()
    {
        var maintenance = new MaintenanceModeService(Options.Create(new MirbadLLMOptions { DataPath = string.Empty }));
        var state = Open(maintenance);
        state.Observe(Node("before", "manual", "job"));
        maintenance.Enter(new MaintenanceState("job", "tuning", "job", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "pc"));
        state.Observe(Node("before"), "manual", ModelState.Stopped);
        using (ModelWriteScope.AsOwner("job"))
        {
            state.Observe(Node("before"), "job", ModelState.Stopped);
        }

        maintenance.Exit("job");
        state.RefreshAfterMaintenance(Node("before"));
        state.Observe(Node("after"));
        Assert.Empty(Open().Pending(Node("after")));
    }

    [Fact]
    public async Task AcknowledgedStartAndStop_PersistIntentWithoutWaitingForTheEventStream()
    {
        var registry = new FakeModelRegistry().Add(new ModelDefinition
        {
            Id = "model-a", ServedName = "Model A", ModelPath = "checkpoint", MemoryBudgetGiB = 10,
            Placement = new ModelPlacement(RemoteRuntimeEnv.NodeId),
        });
        await using var env = await RemoteRuntimeEnv.StartAsync(registry);
        var runtime = env.CreateRuntime(Open());
        await runtime.StartAsync("model-a", ct: TestContext.Current.CancellationToken);
        using (var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, "running-models.json"))))
        {
            Assert.Equal("model-a", Assert.Single(data.RootElement.GetProperty("dgx").GetProperty("running").EnumerateArray()).GetString());
        }

        await runtime.StopAsync("model-a", TestContext.Current.CancellationToken);
        using var stopped = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, "running-models.json")));
        Assert.Empty(stopped.RootElement.GetProperty("dgx").GetProperty("running").EnumerateArray());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"pc\":{\"nodeUid\":\"uid\",\"bootId\":\"boot\",\"running\":null,\"pending\":[]}}")]
    public void CorruptState_DisablesRecovery_AndIsNotOverwritten(string contents)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "running-models.json");
        File.WriteAllText(path, contents);
        var state = Open();
        state.Observe(Node("before", "a"));
        state.Observe(Node("after"));

        Assert.Empty(state.Pending(Node("after")));
        Assert.Equal(contents, File.ReadAllText(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
