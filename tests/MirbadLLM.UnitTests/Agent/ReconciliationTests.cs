using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Admission;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Events;
using MirbadLLM.Agent.Fencing;
using MirbadLLM.Agent.HostServices;
using MirbadLLM.Agent.Operations;
using MirbadLLM.Agent.Recovery;
using MirbadLLM.Agent.Runtimes;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Runtimes;
using MirbadLLM.UnitTests.Orchestration.Fakes;
using FakeDockerEngine = MirbadLLM.UnitTests.Agent.ContainerRuntimeDriverTests.FakeDockerEngine;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// MN-025: agent startup reconciliation (design §4.4 items 5 and 8, §4.6, §4.8) with the real operation log, fencing
/// state, reservation manager, event bus, runtime operations and container driver over the fake Docker engine. Each test
/// writes what a first agent process left on disk and in Docker, then starts a second process and reconciles.
/// </summary>
public sealed class ReconciliationTests : IDisposable
{
    private static readonly MasterTriple Master = new("master-a", 3, "boot-a");
    private static readonly MasterTriple NewerMaster = new("master-b", 4, "boot-b");

    private readonly Node _node = new();

    public void Dispose() => _node.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------- launch: created, not started

    [Fact]
    public async Task CreatedButNotStarted_IsStarted_AndSettledAtReady()
    {
        using (var before = await _node.StartProcessAsync("boot-1"))
        {
            await before.SeedLaunchAsync("op-1", "qwen", OperationStages.Created);
        }

        _node.Docker.Add("c-1", "qwen", "created", ContainerLabelsFor("qwen", "op-1"));
        _node.Health.Ready = false;

        using var agent = await _node.StartProcessAsync("boot-2");
        var report = await agent.Reconciler.RunAsync(Ct);

        Assert.Equal(ReconciliationAction.Resumed, Assert.Single(report.Operations).Action);
        Assert.Equal("running", _node.Docker.Containers.Single().State);
        var status = agent.Log.Find("op-1")!;
        Assert.Equal(OperationStages.Started, status.Stage);
        Assert.Equal(OperationState.Running, status.State);
        Assert.Equal("op-1", Assert.Single(agent.Reservations.List()).OperationId); // held until Ready (§4.8)
        Assert.Empty(_node.Docker.CreateNames);

        _node.Health.Ready = true;
        await agent.Watcher.PollOnceAsync(Ct);

        Assert.Equal(OperationState.Succeeded, agent.Log.Find("op-1")!.State);
        Assert.Empty(agent.Reservations.List());
        Assert.Single(_node.Docker.Containers);
    }

    [Fact]
    public async Task CreatedButNotStarted_WithASupersededMaster_IsRemoved_AndFailsSuperseded()
    {
        using (var before = await _node.StartProcessAsync("boot-1"))
        {
            await before.SeedLaunchAsync("op-1", "qwen", OperationStages.Created);
            await before.Fencing.CheckAsync(NewerMaster, Ct); // a newer master took over before the crash
        }

        _node.Docker.Add("c-1", "qwen", "created", ContainerLabelsFor("qwen", "op-1"));

        using var agent = await _node.StartProcessAsync("boot-2");
        await agent.Reconciler.RunAsync(Ct);

        var status = agent.Log.Find("op-1")!;
        Assert.Equal(OperationState.Failed, status.State);
        Assert.Equal(ErrorCodes.Superseded, status.Error!.Code);
        Assert.Empty(_node.Docker.Containers);
        Assert.DoesNotContain(_node.Docker.Calls, c => c.EndsWith("/start", StringComparison.Ordinal));
        Assert.Empty(_node.Docker.CreateNames);
        Assert.Empty(agent.Reservations.List());
    }

    // ---------------------------------------------------------------- launch: before create

    [Theory]
    [InlineData(OperationStages.Admitted)]
    [InlineData(OperationStages.Pulling)]
    [InlineData(OperationStages.Pulled)]
    public async Task AdmittedOrPulling_WithNoContainer_IsResumedFromThePull_CreatingOneContainer(string stage)
    {
        using (var before = await _node.StartProcessAsync("boot-1"))
        {
            await before.SeedLaunchAsync("op-1", "qwen", stage);
        }

        _node.Docker.ImagesPresent = false;
        _node.Health.Ready = false;

        using var agent = await _node.StartProcessAsync("boot-2");
        var report = await agent.Reconciler.RunAsync(Ct);
        await agent.Runtimes.WhenIdleAsync();

        Assert.Equal(ReconciliationAction.Resumed, Assert.Single(report.Operations).Action);
        Assert.Contains(_node.Docker.Calls, c => c.Contains("/images/create", StringComparison.Ordinal));
        var container = Assert.Single(_node.Docker.Containers);
        Assert.Equal("ss-qwen", container.Name);
        Assert.Equal("op-1", container.Labels[ContainerLabels.Operation]);
        Assert.Equal("8101", container.Labels[ContainerLabels.Port]); // the reservation's port
        Assert.Equal(OperationStages.Started, agent.Log.Find("op-1")!.Stage);
        Assert.Single(agent.Reservations.List());

        _node.Health.Ready = true;
        await agent.Watcher.PollOnceAsync(Ct);
        Assert.Equal(OperationState.Succeeded, agent.Log.Find("op-1")!.State);
        Assert.Empty(agent.Reservations.List());
    }

    [Fact]
    public async Task AcceptedWithoutReservation_IsReservedAgain_AndResumed()
    {
        using (var before = await _node.StartProcessAsync("boot-1"))
        {
            await before.SeedLaunchAsync("op-1", "qwen", OperationStages.Accepted);
        }

        using var agent = await _node.StartProcessAsync("boot-2");
        var report = await agent.Reconciler.RunAsync(Ct);
        await agent.Runtimes.WhenIdleAsync();

        Assert.Equal(0, report.ReservationsRebuilt);
        Assert.Single(_node.Docker.Containers);
        Assert.Equal(OperationStages.Started, agent.Log.Find("op-1")!.Stage);
        Assert.Equal("op-1", Assert.Single(agent.Reservations.List()).OperationId);
    }

    [Fact]
    public async Task AcceptedWithoutReservation_WhileAnotherStartHoldsTheNode_FailsWithStartConflict()
    {
        using (var before = await _node.StartProcessAsync("boot-1"))
        {
            await before.SeedLaunchAsync("op-other", "gemma", OperationStages.Started);
            await before.SeedLaunchAsync("op-1", "qwen", OperationStages.Accepted);
        }

        _node.Docker.Add("c-g", "gemma", "running", ContainerLabelsFor("gemma", "op-other", "8102"));
        _node.Health.Ready = false;

        using var agent = await _node.StartProcessAsync("boot-2");
        await agent.Reconciler.RunAsync(Ct);
        await agent.Runtimes.WhenIdleAsync();

        var status = agent.Log.Find("op-1")!;
        Assert.Equal(OperationState.Failed, status.State);
        Assert.Equal(ErrorCodes.StartConflict, status.Error!.Code);
        Assert.Equal("op-other", Assert.Single(agent.Reservations.List()).OperationId);
        Assert.Single(_node.Docker.Containers);
    }

    [Fact]
    public async Task Admitted_WithASupersededMaster_FailsSuperseded_WithoutPullingOrCreating()
    {
        using (var before = await _node.StartProcessAsync("boot-1"))
        {
            await before.SeedLaunchAsync("op-1", "qwen", OperationStages.Admitted);
            await before.Fencing.CheckAsync(NewerMaster, Ct);
        }

        _node.Docker.ImagesPresent = false;

        using var agent = await _node.StartProcessAsync("boot-2");
        await agent.Reconciler.RunAsync(Ct);
        await agent.Runtimes.WhenIdleAsync();

        Assert.Equal(ErrorCodes.Superseded, agent.Log.Find("op-1")!.Error!.Code);
        Assert.Empty(_node.Docker.Containers);
        Assert.DoesNotContain(_node.Docker.Calls, c => c.Contains("/images/create", StringComparison.Ordinal));
        Assert.Empty(agent.Reservations.List());
    }

    [Fact]
    public async Task Admitted_WithAnExpiredEnvelope_FailsExpired_WithoutCreating()
    {
        using (var before = await _node.StartProcessAsync("boot-1"))
        {
            await before.SeedLaunchAsync("op-1", "qwen", OperationStages.Admitted, lifetime: TimeSpan.FromHours(1));
        }

        _node.Time.Advance(TimeSpan.FromHours(2)); // the agent was down longer than the envelope lived

        using var agent = await _node.StartProcessAsync("boot-2");
        await agent.Reconciler.RunAsync(Ct);

        Assert.Equal(ErrorCodes.OperationExpired, agent.Log.Find("op-1")!.Error!.Code);
        Assert.Empty(_node.Docker.Containers);
        Assert.Empty(agent.Reservations.List());
    }

    [Fact]
    public async Task ReplaceAfterTheOldInstanceWasRemoved_CreatesTheNewInstanceOnce()
    {
        using (var before = await _node.StartProcessAsync("boot-1"))
        {
            await before.SeedLaunchAsync("op-1", "qwen", OperationStages.Removed, LaunchMode.Replace);
        }

        using var agent = await _node.StartProcessAsync("boot-2");
        await agent.Reconciler.RunAsync(Ct);
        await agent.Runtimes.WhenIdleAsync();

        Assert.Equal(["ss-qwen"], _node.Docker.CreateNames);
        Assert.Equal("op-1", Assert.Single(_node.Docker.Containers).Labels[ContainerLabels.Operation]);
    }

    // ---------------------------------------------------------------- launch: started / ready

    [Theory]
    [InlineData(OperationStages.Started)]
    [InlineData(OperationStages.Ready)]
    public async Task StartedOrReady_WithAReadyInstance_Succeeds_AndReleasesTheRebuiltReservation(string stage)
    {
        using (var before = await _node.StartProcessAsync("boot-1"))
        {
            await before.SeedLaunchAsync("op-1", "qwen", stage);
        }

        _node.Docker.Add("c-1", "qwen", "running", ContainerLabelsFor("qwen", "op-1"));

        using var agent = await _node.StartProcessAsync("boot-2");
        var report = await agent.Reconciler.RunAsync(Ct);

        Assert.Equal(1, report.ReservationsRebuilt);
        var status = agent.Log.Find("op-1")!;
        Assert.Equal(OperationState.Succeeded, status.State);
        Assert.Equal(ModelState.Ready, status.ReadResult<RuntimeStatus>()!.State);
        Assert.Empty(agent.Reservations.List());
        Assert.Equal(2, agent.Events.Of<ReservationEvent>().Count); // rebuilt, then released
        Assert.Equal(["GET"], _node.Docker.Calls.Select(c => c.Split(' ')[0]).Distinct());
    }

    [Fact]
    public async Task Started_WhoseInstanceIsGone_Fails()
    {
        using (var before = await _node.StartProcessAsync("boot-1"))
        {
            await before.SeedLaunchAsync("op-1", "qwen", OperationStages.Started);
        }

        using var agent = await _node.StartProcessAsync("boot-2");
        await agent.Reconciler.RunAsync(Ct);

        Assert.Equal(OperationState.Failed, agent.Log.Find("op-1")!.State);
        Assert.Empty(agent.Reservations.List());
        Assert.Empty(_node.Docker.CreateNames);
    }

    // ---------------------------------------------------------------- stop

    [Fact]
    public async Task Stop_IsFinished()
    {
        using (var before = await _node.StartProcessAsync("boot-1"))
        {
            await before.SeedStopAsync("stop-1", "qwen", OperationStages.Stopping);
        }

        _node.Docker.Add("c-1", "qwen", "running", ContainerLabelsFor("qwen", "op-old"));

        using var agent = await _node.StartProcessAsync("boot-2");
        var report = await agent.Reconciler.RunAsync(Ct);

        Assert.Equal(ReconciliationAction.Completed, Assert.Single(report.Operations).Action);
        Assert.Equal(OperationState.Succeeded, agent.Log.Find("stop-1")!.State);
        Assert.Empty(_node.Docker.Containers);
        Assert.Null(agent.Board.Find("qwen"));
    }

    [Fact]
    public async Task LaunchFollowedByAStop_EndsCancelled_AndNothingIsCreated()
    {
        using (var before = await _node.StartProcessAsync("boot-1"))
        {
            await before.SeedLaunchAsync("op-1", "qwen", OperationStages.Pulling);
            await before.SeedStopAsync("stop-1", "qwen", OperationStages.Accepted);
        }

        using var agent = await _node.StartProcessAsync("boot-2");
        await agent.Reconciler.RunAsync(Ct);
        await agent.Runtimes.WhenIdleAsync();

        Assert.Equal(OperationState.Cancelled, agent.Log.Find("op-1")!.State);
        Assert.Equal(OperationState.Succeeded, agent.Log.Find("stop-1")!.State);
        Assert.Empty(_node.Docker.CreateNames);
        Assert.Empty(agent.Reservations.List());
    }

    // ---------------------------------------------------------------- cancel left active

    [Fact]
    public async Task AcceptedCancelOfALaunchBeforeCreate_CancelsTheLaunch_AndSucceeds()
    {
        using (var before = await _node.StartProcessAsync("boot-1"))
        {
            await before.SeedLaunchAsync("op-1", "qwen", OperationStages.Admitted);
            await before.SeedAsync(OperationKinds.OperationCancel, "op-1", null, before.Envelope<OperationCancelRequest>("cancel-1"));
        }

        using var agent = await _node.StartProcessAsync("boot-2");
        var report = await agent.Reconciler.RunAsync(Ct);
        await agent.Runtimes.WhenIdleAsync();

        Assert.Equal(OperationState.Cancelled, agent.Log.Find("op-1")!.State);
        var cancel = agent.Log.Find("cancel-1")!;
        Assert.Equal(OperationState.Succeeded, cancel.State);
        Assert.Equal(OperationState.Cancelled, cancel.ReadResult<OperationStatus>()!.State);
        Assert.Equal(["op-1", "cancel-1"], report.Operations.Select(o => o.OperationId));
        Assert.Empty(_node.Docker.CreateNames);
        Assert.Empty(agent.Reservations.List());
    }

    [Fact]
    public async Task AcceptedCancelOfALaunchWhoseInstanceExists_FailsInvalidState()
    {
        using (var before = await _node.StartProcessAsync("boot-1"))
        {
            await before.SeedLaunchAsync("op-1", "qwen", OperationStages.Started);
            await before.SeedAsync(OperationKinds.OperationCancel, "op-1", null, before.Envelope<OperationCancelRequest>("cancel-1"));
        }

        _node.Docker.Add("c-1", "qwen", "running", ContainerLabelsFor("qwen", "op-1"));
        _node.Health.Ready = false;

        using var agent = await _node.StartProcessAsync("boot-2");
        await agent.Reconciler.RunAsync(Ct);

        Assert.Equal(OperationState.Running, agent.Log.Find("op-1")!.State);
        var cancel = agent.Log.Find("cancel-1")!;
        Assert.Equal(OperationState.Failed, cancel.State);
        Assert.Equal(ErrorCodes.InvalidState, cancel.Error!.Code);
        Assert.Single(_node.Docker.Containers);
    }

    // ---------------------------------------------------------------- host services, PKI, unclaimed kinds

    [Fact]
    public async Task HostServiceDispatched_DesiredStateIsReconciled_AndRestartEndsOutcomeUnknown()
    {
        using (var before = await _node.StartProcessAsync("boot-1"))
        {
            await before.SeedAsync(OperationKinds.ServiceStop, "comfyui", OperationStages.Dispatched, before.Envelope<HostServiceActionRequest>("op-stop"));
            await before.SeedAsync(OperationKinds.ServiceRestart, "comfyui", OperationStages.Dispatched, before.Envelope<HostServiceActionRequest>("op-restart"));
        }

        using var agent = await _node.StartProcessAsync("boot-2", withHostServices: true);
        var report = await agent.Reconciler.RunAsync(Ct);

        Assert.All(report.Operations, o => Assert.Equal(ReconciliationAction.Delegated, o.Action));
        Assert.Equal(OperationState.Succeeded, agent.Log.Find("op-stop")!.State);
        Assert.Equal("inactive", _node.Hostd.Units["comfyui"].ActiveState);
        var restart = agent.Log.Find("op-restart")!;
        Assert.Equal(OperationState.Unknown, restart.State);
        Assert.Equal(ErrorCodes.OutcomeUnknown, restart.Error!.Code);
        Assert.Equal(0, _node.Hostd.Restarts); // never replayed
        Assert.Empty(agent.Log.ListActive());
    }

    [Fact]
    public async Task PendingPkiRotation_IsKept()
    {
        using (var before = await _node.StartProcessAsync("boot-1"))
        {
            await before.SeedAsync(OperationKinds.PkiRotationStart, "rotation-1", OperationStages.KeyPending, before.Envelope<OperationCancelRequest>("rot-1"));
        }

        using var agent = await _node.StartProcessAsync("boot-2");
        var report = await agent.Reconciler.RunAsync(Ct);

        Assert.Equal(ReconciliationAction.Kept, Assert.Single(report.Operations).Action);
        var status = agent.Log.Find("rot-1")!;
        Assert.Equal(OperationState.Running, status.State);
        Assert.Equal(OperationStages.KeyPending, status.Stage);
    }

    [Fact]
    public async Task KindWithoutARecoveryHandler_Fails()
    {
        using (var before = await _node.StartProcessAsync("boot-1"))
        {
            await before.SeedAsync(OperationKinds.Download, "Qwen3-8B", OperationStages.Downloading, before.Envelope<OperationCancelRequest>("dl-1"));
        }

        using var agent = await _node.StartProcessAsync("boot-2");
        await agent.Reconciler.RunAsync(Ct);

        var status = agent.Log.Find("dl-1")!;
        Assert.Equal(OperationState.Failed, status.State);
        Assert.Equal(ErrorCodes.InternalError, status.Error!.Code);
    }

    // ---------------------------------------------------------------- adoption and Docker failures

    [Fact]
    public async Task TodaysFourDgxContainers_AreAdoptedUntouched()
    {
        string[] models = ["qwen36-35b-a3b-nvfp4", "qwen3-embedding-4b", "qwen3-vl-embedding-2b", "ov-intent-analysis"];
        for (int i = 0; i < models.Length; i++)
        {
            // Created by today's controller: no node or operation label.
            _node.Docker.Add("dgx-" + i, models[i], "running", new Dictionary<string, string>
            {
                [ContainerLabels.Model] = models[i],
                [ContainerLabels.Port] = (8101 + i).ToString(CultureInfo.InvariantCulture),
                [ContainerLabels.ServedName] = models[i],
                [ContainerLabels.ConfigHash] = "hash-" + i,
                [ContainerLabels.BudgetGiB] = "20",
            });
        }

        using var agent = await _node.StartProcessAsync("boot-1");
        var report = await agent.Reconciler.RunAsync(Ct);

        Assert.Empty(report.Operations);
        Assert.Equal(models.Order(StringComparer.Ordinal), report.Adopted.Select(a => a.ModelId).Order(StringComparer.Ordinal));
        Assert.All(report.Adopted, a => Assert.Equal(ModelState.Ready, a.State));
        Assert.Equal(4, agent.Board.Current.Count);
        Assert.All(_node.Docker.Calls, c => Assert.StartsWith("GET ", c, StringComparison.Ordinal));
        Assert.All(_node.Docker.Containers, c => Assert.Equal("running", c.State));
        Assert.Empty(_node.Docker.CreateNames);
        Assert.Empty(agent.Reservations.List());
        Assert.Empty(agent.Log.ListActive());
    }

    [Fact]
    public async Task InstanceWhoseLaunchEnded_IsAdopted_ButOneWithAnActiveLaunchIsNot()
    {
        using (var before = await _node.StartProcessAsync("boot-1"))
        {
            await before.SeedLaunchAsync("op-q", "qwen", OperationStages.Started);
        }

        _node.Docker.Add("c-q", "qwen", "running", ContainerLabelsFor("qwen", "op-q"));
        _node.Docker.Add("c-g", "gemma", "running", ContainerLabelsFor("gemma", "op-long-gone", "8102"));
        _node.Health.Ready = false;

        using var agent = await _node.StartProcessAsync("boot-2");
        var report = await agent.Reconciler.RunAsync(Ct);

        Assert.Equal("gemma", Assert.Single(report.Adopted).ModelId);
        Assert.Equal(2, agent.Board.Current.Count);
    }

    [Fact]
    public async Task DockerUnreachable_FailsLaunchesWithDockerUnavailable_AndReleasesTheirReservations()
    {
        using (var before = await _node.StartProcessAsync("boot-1"))
        {
            await before.SeedLaunchAsync("op-1", "qwen", OperationStages.Admitted);
        }

        _node.DockerDown = true;

        using var agent = await _node.StartProcessAsync("boot-2");
        var report = await agent.Reconciler.RunAsync(Ct);

        Assert.NotNull(report.InstanceListingError);
        Assert.Equal(ErrorCodes.DockerUnavailable, agent.Log.Find("op-1")!.Error!.Code);
        Assert.Empty(agent.Reservations.List());
        Assert.Empty(report.Adopted);
    }

    [Fact]
    public async Task NothingToDo_IsANoOp()
    {
        using var agent = await _node.StartProcessAsync("boot-1");
        var report = await agent.Reconciler.RunAsync(Ct);

        Assert.Empty(report.Operations);
        Assert.Empty(report.Adopted);
        Assert.Equal(0, report.ReservationsRebuilt);
    }

    // ---------------------------------------------------------------- harness

    private static Dictionary<string, string> ContainerLabelsFor(string modelId, string operationId, string port = "8101") => new()
    {
        [ContainerLabels.Model] = modelId,
        [ContainerLabels.Port] = port,
        [ContainerLabels.Node] = "dgx",
        [ContainerLabels.Operation] = operationId,
        [ContainerLabels.BudgetGiB] = "36",
        [ContainerLabels.ConfigHash] = "9f2c",
    };

    private static LaunchSpec Spec() => new()
    {
        Kind = LaunchDelivery.Container,
        Image = "local/vllm:test",
        Argv = ["--model", "/models/qwen"],
        ContainerPort = 8000,
        Mounts =
        [
            new MountSpec(LogicalVolumes.Models, null, LogicalVolumes.ModelsTarget, ReadOnly: true),
            new MountSpec(LogicalVolumes.Cache, null, LogicalVolumes.CacheTarget, ReadOnly: false),
        ],
        Gpus = new GpuSelection { Mode = GpuSelectionMode.All },
        IpcHost = true,
        Labels = new Dictionary<string, string> { [ContainerLabels.ConfigHash] = "9f2c", [ContainerLabels.BudgetGiB] = "36" },
    };

    /// <summary>What survives an agent restart: the state directory, Docker and hostd, and the clock.</summary>
    private sealed class Node : IDisposable
    {
        private readonly FakeDockerEngine _docker = new();

        public string StateDirectory { get; } = Path.Combine(Path.GetTempPath(), "ss-reconcile-" + Guid.NewGuid().ToString("N"));

        public ManualTimeProvider Time { get; } = new();

        public FakeDockerEngine Docker => _docker;

        public HealthHandler Health { get; } = new();

        public ScriptedHostd Hostd { get; } = new();

        /// <summary>Every Docker call fails as if the daemon were down.</summary>
        public bool DockerDown { get; set; }

        public async Task<AgentProcess> StartProcessAsync(string bootId, bool withHostServices = false)
        {
            var process = new AgentProcess(this, bootId, withHostServices);
            if (process.Fencing.Accepted is null)
            {
                await process.Fencing.CheckAsync(Master, Ct);
            }

            return process;
        }

        /// <summary>A Docker client for one process: requests are serialized (the fake engine is not thread-safe).</summary>
        public DockerEngineClient NewDockerClient() =>
            new(new HttpClient(new SerializingHandler(this, _docker.Handler)) { BaseAddress = new Uri("http://docker/v1.47/") });

        public void Dispose()
        {
            try
            {
                Directory.Delete(StateDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class SerializingHandler(Node node, HttpMessageHandler engine) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _engine = new(engine, disposeHandler: false); // shared by every process
        private readonly SemaphoreSlim _gate = new(1, 1);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (node.DockerDown)
            {
                throw new HttpRequestException("Connection refused (docker.sock)");
            }

            await _gate.WaitAsync(cancellationToken);
            try
            {
                return await _engine.SendAsync(request, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _gate.Dispose();
                _engine.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>The instances' <c>/health</c>: 200 when <see cref="Ready"/>, else 503 (still loading).</summary>
    private sealed class HealthHandler : HttpMessageHandler
    {
        public volatile bool Ready = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(Ready ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));
    }

    /// <summary>One agent process over the node: the real packages, composed as MN-026 will compose them.</summary>
    private sealed class AgentProcess : IDisposable
    {
        private readonly Node _node;
        private readonly HttpClient _upstream;
        private readonly DockerEngineClient _docker;

        public AgentProcess(Node node, string bootId, bool withHostServices)
        {
            _node = node;
            var time = node.Time;
            Log = new OperationLog(new OperationLogOptions { StateDirectory = node.StateDirectory, BootId = bootId }, time);
            Fencing = new FencingState(node.StateDirectory, time);
            Events = new RecordingEventBus(new EventBus(bootId, time: time));

            IRuntimeDriver? driver = null;
            Reservations = new ReservationManager(
                new AdmissionOptions { NodeId = "dgx" },
                new FakeMemoryProbe(120, 100),
                new LateInstanceSource(() => driver!),
                persistence: null,
                Events,
                time);

            _docker = node.NewDockerClient();
            _upstream = new HttpClient(node.Health, disposeHandler: false);
            driver = new ContainerRuntimeDriver(
                _docker, _upstream, Reservations,
                Microsoft.Extensions.Options.Options.Create(new ContainerRuntimeOptions { NodeId = "dgx" }),
                NullLogger<ContainerRuntimeDriver>.Instance, time);

            var options = new AgentRuntimeOptions { WatchInterval = TimeSpan.FromHours(1) };
            Gate = new MutationGate(Log, Fencing, Events, time);
            Board = new RuntimeStateBoard(Events);
            Runtimes = new RuntimeOperations([driver], Gate, Reservations, Board, Events, time, options);
            Watcher = new RuntimeWatcher(Runtimes, Board, Reservations, time, options);

            var handlers = new List<IOperationRecoveryHandler>();
            if (withHostServices)
            {
                var hostOperations = new HostServiceOperations(node.Hostd, null, Log, Fencing, new AgentHostServicesOptions(), time,
                    NullLogger<HostServiceOperations>.Instance, Events);
                handlers.Add(new HostServiceRecoveryHandler(hostOperations, Log, Fencing, time, NullLogger<HostServiceRecoveryHandler>.Instance));
            }

            Reconciler = new AgentReconciler(Runtimes, Gate, Board, Reservations, handlers, [Runtimes], time, NullLogger<AgentReconciler>.Instance);
        }

        public OperationLog Log { get; }

        public FencingState Fencing { get; }

        public RecordingEventBus Events { get; }

        public ReservationManager Reservations { get; }

        public MutationGate Gate { get; }

        public RuntimeStateBoard Board { get; }

        public RuntimeOperations Runtimes { get; }

        public RuntimeWatcher Watcher { get; }

        public AgentReconciler Reconciler { get; }

        /// <summary>Records a launch the way the live handler does, up to <paramref name="stage"/>.</summary>
        public async Task SeedLaunchAsync(
            string id, string modelId, string stage, LaunchMode mode = LaunchMode.EnsureStarted, TimeSpan? lifetime = null)
        {
            var now = _node.Time.GetUtcNow();
            var request = new LaunchRequest
            {
                OperationId = id,
                IssuedAt = now,
                ExpiresAt = now + (lifetime ?? OperationRetention.EnvelopeLifetime),
                Mode = mode,
                Spec = Spec(),
                Admission = new AdmissionRequest { BudgetGiB = 36, ReserveGiB = 8 },
                ExpectedConfigHash = "9f2c",
            };
            await AcceptAsync(OperationKinds.Launch, modelId, request);
            if (stage == OperationStages.Accepted)
            {
                return;
            }

            int port = 8101 + Log.ListActive().Count(o => o.Kind == OperationKinds.Launch && o.Stage != OperationStages.Accepted);
            await Log.RecordStageAsync(id, new OperationStageUpdate
            {
                Stage = OperationStages.Admitted,
                Reservation = new ReservationInfo { OperationId = id, ModelId = modelId, BudgetGiB = 36, HostPort = port, CreatedAt = now },
            }, Ct);
            if (stage != OperationStages.Admitted)
            {
                await Log.RecordStageAsync(id, new OperationStageUpdate { Stage = stage }, Ct);
            }
        }

        public async Task SeedStopAsync(string id, string modelId, string stage)
        {
            await AcceptAsync(OperationKinds.Stop, modelId, Envelope<StopRequest>(id));
            if (stage != OperationStages.Accepted)
            {
                await Log.RecordStageAsync(id, new OperationStageUpdate { Stage = stage }, Ct);
            }
        }

        public async Task SeedAsync(string kind, string target, string? stage, AgentMutation request)
        {
            await AcceptAsync(kind, target, request);
            if (stage is not null)
            {
                await Log.RecordStageAsync(request.OperationId, new OperationStageUpdate { Stage = stage, State = OperationState.Running }, Ct);
            }
        }

        /// <summary>A request of type <typeparamref name="T"/> carrying only the envelope (issued now, expiring in 24 h).</summary>
        public T Envelope<T>(string id)
            where T : AgentMutation
        {
            var now = _node.Time.GetUtcNow();
            var json = JsonSerializer.SerializeToElement(new { operationId = id, issuedAt = now, expiresAt = now + OperationRetention.EnvelopeLifetime });
            return json.Deserialize<T>(AgentJson.Options)!;
        }

        private async Task AcceptAsync(string kind, string target, AgentMutation request)
        {
            var accepted = await Log.AcceptAsync(new OperationSubmission
            {
                OperationId = request.OperationId,
                Kind = kind,
                Target = target,
                IssuedAt = request.IssuedAt,
                ExpiresAt = request.ExpiresAt,
                Master = Master,
                Payload = JsonSerializer.SerializeToElement(request, request.GetType(), AgentJson.Options),
            }, Ct);
            Assert.Equal(OperationAcceptOutcome.Accepted, accepted.Outcome);
        }

        public void Dispose()
        {
            Runtimes.WhenIdleAsync().GetAwaiter().GetResult();
            Watcher.Dispose();
            Runtimes.Dispose();
            Reservations.Dispose();
            Fencing.Dispose();
            Log.Dispose();
            _upstream.Dispose();
        }
    }

    private sealed class LateInstanceSource(Func<IRuntimeDriver> driver) : IRuntimeInstanceSource
    {
        public Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default) => driver().ListAsync(ct);
    }

    /// <summary>The real event bus, also keeping every published event for assertions.</summary>
    private sealed class RecordingEventBus(EventBus inner) : IEventBus
    {
        private readonly List<AgentEvent> _published = [];
        private readonly Lock _gate = new();

        public string BootId => inner.BootId;

        public long CurrentSeq => inner.CurrentSeq;

        public List<T> Of<T>()
            where T : AgentEvent
        {
            lock (_gate)
            {
                return [.. _published.OfType<T>()];
            }
        }

        public AgentEvent Publish(AgentEvent evt)
        {
            var published = inner.Publish(evt);
            Record(published);
            return published;
        }

        public AgentEvent Publish(Action apply, AgentEvent evt)
        {
            var published = inner.Publish(apply, evt);
            Record(published);
            return published;
        }

        public T Snapshot<T>(Func<long, T> build) => inner.Snapshot(build);

        public IEventSubscription Subscribe(string? bootId, long? afterSeq) => inner.Subscribe(bootId, afterSeq);

        private void Record(AgentEvent evt)
        {
            lock (_gate)
            {
                _published.Add(evt);
            }
        }
    }
}
