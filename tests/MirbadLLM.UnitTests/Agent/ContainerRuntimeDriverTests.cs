using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Runtimes;
using MirbadLLM.Orchestration;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707, xUnit1051 // underscore test names; hand-written fakes ignore the token.

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// <see cref="ContainerRuntimeDriver"/> (MN-015): the ported <c>DockerModelRuntimeTests</c> cases (state mapping, crash
/// loop, failed-container replacement), launch modes, agent-set labels, guardrails (design §10.3), stage callbacks and
/// parity with the container the pre-P1 <c>DockerModelRuntime</c> created (recorded fixture, MN-047, D60).
/// </summary>
public sealed class ContainerRuntimeDriverTests
{
    private const double TotalGiB = 120;
    private const string NodeId = "dgx";
    private const string DefaultImage = "local/qwen38-vllm-dflash2:66e5414c6d75";
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Created = DateTimeOffset.FromUnixTimeSeconds(1790640000);

    private static readonly ModelDefinition Saved = new()
    {
        Id = "m1",
        ServedName = "M1",
        ModelPath = "ckpt",
        MemoryBudgetGiB = 40,
        Speculative = new SpeculativeDecoding("mtp", 2),
        CudaGraphMode = "PIECEWISE",
    };

    // ------------------------------------------------------------ harness

    private sealed class Harness
    {
        public Harness(ContainerRuntimeOptions? options = null, HttpStatusCode health = HttpStatusCode.OK)
        {
            Docker = new FakeDockerEngine();
            Upstream = new FakeHttpMessageHandler { Handler = _ => new HttpResponseMessage(health) };
            Options = options ?? new ContainerRuntimeOptions { NodeId = NodeId };
            Driver = new ContainerRuntimeDriver(
                Docker.Client,
                new HttpClient(Upstream),
                Ports,
                Microsoft.Extensions.Options.Options.Create(Options),
                NullLogger<ContainerRuntimeDriver>.Instance);
        }

        public FakeDockerEngine Docker { get; }

        public FakeHttpMessageHandler Upstream { get; }

        public FakePortAllocator Ports { get; } = new();

        public RecordingStageSink Stages { get; } = new();

        public ContainerRuntimeOptions Options { get; }

        public ContainerRuntimeDriver Driver { get; }

        public Task<RuntimeLaunchResult> LaunchAsync(LaunchMode mode, LaunchSpec? spec = null, int hostPort = 8101, string modelId = "m1", string operationId = "op-1") =>
            Driver.LaunchAsync(
                new RuntimeLaunch { ModelId = modelId, OperationId = operationId, Mode = mode, Spec = spec ?? SpecFor(Saved), HostPort = hostPort },
                Stages);
    }

    /// <summary>The spec the master builds for <paramref name="model"/> (what the pre-P1 <c>DockerModelRuntime</c> computed).</summary>
    private static LaunchSpec SpecFor(ModelDefinition model, bool tuning = false, string defaultImage = DefaultImage)
    {
        string image = model.Image ?? defaultImage;
        var argv = VllmArgsBuilder.Build(model, TotalGiB);
        var labels = new Dictionary<string, string>
        {
            [ContainerLabels.ServedName] = model.ServedName,
            [ContainerLabels.ConfigHash] = ModelConfigHash.Compute(image, argv),
            [ContainerLabels.BudgetGiB] = model.MemoryBudgetGiB.ToString("0.###", CultureInfo.InvariantCulture),
        };
        if (tuning)
        {
            labels[ContainerLabels.Tuning] = "1";
        }

        return new LaunchSpec
        {
            Kind = LaunchDelivery.Container,
            Image = image,
            Argv = argv,
            ContainerPort = 8000,
            Mounts =
            [
                new MountSpec(LogicalVolumes.Models, null, LogicalVolumes.ModelsTarget, ReadOnly: true),
                new MountSpec(LogicalVolumes.Cache, null, LogicalVolumes.CacheTarget, ReadOnly: false),
            ],
            Gpus = new GpuSelection { Mode = GpuSelectionMode.All },
            IpcHost = true,
            Labels = labels,
        };
    }

    private static Dictionary<string, string> Labels(string modelId, string port = "8101", params (string Key, string Value)[] extra)
    {
        var labels = new Dictionary<string, string> { [ContainerLabels.Model] = modelId, [ContainerLabels.Port] = port };
        foreach (var (key, value) in extra)
        {
            labels[key] = value;
        }

        return labels;
    }

    private static async Task<AgentException> AssertRejectedAsync(Harness h, LaunchSpec spec, string code = ErrorCodes.SpecRejected, string modelId = "m1")
    {
        var ex = await Assert.ThrowsAsync<AgentException>(() => h.LaunchAsync(LaunchMode.EnsureStarted, spec, modelId: modelId));
        Assert.Equal(code, ex.Code);
        Assert.Equal(code == ErrorCodes.SpecRejected ? 422 : 400, ex.StatusCode);
        Assert.Empty(h.Docker.Calls);
        return ex;
    }

    // ------------------------------------------------------------ state mapping (ported from DockerModelRuntimeTests)

    [Fact]
    public async Task Get_NoContainer_IsNull_AndListIsEmpty()
    {
        var h = new Harness();

        Assert.Null(await h.Driver.GetAsync("missing"));
        Assert.Empty(await h.Driver.ListAsync());
    }

    [Fact]
    public async Task Get_RunningAndHealthy_IsReady_ProbingTheContainerNetwork()
    {
        var h = new Harness();
        h.Docker.Add("container-m1", "m1", "running", Labels("m1"));

        var status = await h.Driver.GetAsync("m1");

        Assert.NotNull(status);
        Assert.Equal(ModelState.Ready, status.State);
        Assert.Equal("container-m1", status.InstanceId);
        Assert.Equal(8101, status.HostPort);
        Assert.Equal(Created, status.Since);
        var probe = Assert.Single(h.Upstream.Requests);
        Assert.Equal("http://ss-m1:8000/health", probe.RequestUri!.ToString());
    }

    [Fact]
    public async Task Get_NimContainer_PreservesRuntimeAndUsesApprovedReadinessPath()
    {
        var h = new Harness();
        h.Docker.Add("container-m1", "m1", "running", Labels("m1", extra:
            [(ContainerLabels.Runtime, "nim"), (ContainerLabels.HealthPath, "/v1/health/ready")]));

        var status = await h.Driver.GetAsync("m1");
        var config = await h.Driver.GetLaunchConfigAsync("m1");

        Assert.Equal(RuntimeKind.Nim, status!.Runtime);
        Assert.Equal(RuntimeKind.Nim, config!.Runtime);
        Assert.Equal("http://ss-m1:8000/v1/health/ready", Assert.Single(h.Upstream.Requests).RequestUri!.ToString());
    }

    [Fact]
    public async Task Get_WithoutContainerNetwork_ProbesTheLoopbackPort()
    {
        var h = new Harness(new ContainerRuntimeOptions { NodeId = NodeId, UseContainerNetwork = false });
        h.Docker.Add("container-m1", "m1", "running", Labels("m1", "8123"));

        Assert.Equal(ModelState.Ready, (await h.Driver.GetAsync("m1"))!.State);
        Assert.Equal("http://127.0.0.1:8123/health", Assert.Single(h.Upstream.Requests).RequestUri!.ToString());
        Assert.Equal(new Uri("http://127.0.0.1:8123/"), await h.Driver.GetUpstreamAsync("m1"));
    }

    [Fact]
    public async Task Get_RunningButHealthCheckFails_IsStarting_WithCreationTimeAsSince()
    {
        var h = new Harness(health: HttpStatusCode.ServiceUnavailable);
        h.Docker.Add("container-m1", "m1", "running", Labels("m1"));

        var status = await h.Driver.GetAsync("m1");

        Assert.Equal(ModelState.Starting, status!.State);
        Assert.Equal(Created, status.Since);
    }

    [Fact]
    public async Task Get_RunningButHealthCheckThrows_IsStarting()
    {
        var h = new Harness();
        h.Upstream.Handler = _ => throw new HttpRequestException("connection refused");
        h.Docker.Add("container-m1", "m1", "running", Labels("m1"));

        Assert.Equal(ModelState.Starting, (await h.Driver.GetAsync("m1"))!.State);
    }

    [Fact]
    public async Task Get_Exited_IsFailedWithLastLogLines()
    {
        var h = new Harness();
        h.Docker.Add("container-m1", "m1", "exited", Labels("m1"));
        h.Docker.LogText = "boom: out of memory\n";

        var status = await h.Driver.GetAsync("m1");

        Assert.Equal(ModelState.Failed, status!.State);
        Assert.Equal("Cause: out of memory\n\nboom: out of memory", status.Error);
    }

    [Fact]
    public void FailureReport_SkipsEngineWrapperAndDocstringNoise()
    {
        string[] lines =
        [
            "(EngineCore pid=174) Traceback (most recent call last):",
            "(EngineCore pid=174) ValueError: KV cache too small",
            "(APIServer pid=1) RuntimeError: Engine core initialization failed. See root cause above.",
            "(EngineCore pid=174) [ERROR] `min_frames` is part of Qwen3VLVideoProcessorInitKwargs, but not documented.",
        ];

        Assert.Equal("ValueError: KV cache too small", FailureReport.FindRootCause(lines));
    }

    [Fact]
    public async Task Get_Dead_IsFailed()
    {
        var h = new Harness();
        h.Docker.Add("container-m1", "m1", "dead", Labels("m1"));

        Assert.Equal(ModelState.Failed, (await h.Driver.GetAsync("m1"))!.State);
    }

    [Theory]
    [InlineData("created", ModelState.Starting)]
    [InlineData("paused", ModelState.Starting)]
    [InlineData("removing", ModelState.Stopping)]
    public async Task Get_OtherDockerStates_MapAsToday(string dockerState, ModelState expected)
    {
        var h = new Harness();
        h.Docker.Add("container-m1", "m1", dockerState, Labels("m1"));

        Assert.Equal(expected, (await h.Driver.GetAsync("m1"))!.State);
    }

    [Fact]
    public async Task Get_RunningButUnhealthyAfterRestart_IsFailedAndStopped()
    {
        // Docker's restart policy hid the crash: the container is "running" again, but it has restarted before.
        var h = new Harness(health: HttpStatusCode.ServiceUnavailable);
        h.Docker.Add("container-m1", "m1", "running", Labels("m1"), restartCount: 3);
        h.Docker.LogText =
            "(EngineCore pid=174) ValueError: To serve at least one request with the model's max seq len (262144), (4.11 GiB KV cache is needed, which is larger than the available KV cache memory (0.12 GiB).\n" +
            "(APIServer pid=1) RuntimeError: Engine core initialization failed. See root cause above. Failed core proc(s): {}\n";

        var status = await h.Driver.GetAsync("m1");

        Assert.Equal(ModelState.Failed, status!.State);
        Assert.Equal(3, status.RestartCount);
        Assert.StartsWith("Cause: ValueError: To serve at least one request", status.Error, StringComparison.Ordinal);
        Assert.Contains("POST /v1.47/containers/container-m1/stop?t=5", h.Docker.Calls);
    }

    [Fact]
    public async Task Get_Restarting_IsFailedAndStopped()
    {
        var h = new Harness();
        h.Docker.Add("container-m1", "m1", "restarting", Labels("m1"));

        Assert.Equal(ModelState.Failed, (await h.Driver.GetAsync("m1"))!.State);
        Assert.Contains("POST /v1.47/containers/container-m1/stop?t=5", h.Docker.Calls);
    }

    [Fact]
    public async Task Get_HealthyAfterRestart_IsReady_NotACrashLoop()
    {
        var h = new Harness();
        h.Docker.Add("container-m1", "m1", "running", Labels("m1"), restartCount: 2);

        Assert.Equal(ModelState.Ready, (await h.Driver.GetAsync("m1"))!.State);
        Assert.DoesNotContain(h.Docker.Calls, c => c.Contains("/stop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Status_CarriesTheLabels()
    {
        var h = new Harness();
        h.Docker.Add("container-m1", "m1", "running", Labels(
            "m1",
            extra:
            [
                (ContainerLabels.ServedName, "M1"), (ContainerLabels.ConfigHash, "9f2c"), (ContainerLabels.ExecutionFingerprint, "41ab"),
                (ContainerLabels.Tuning, "1"), (ContainerLabels.Operation, "op-7"), (ContainerLabels.Node, NodeId),
            ]));

        var status = await h.Driver.GetAsync("m1");

        Assert.Equal("M1", status!.ServedName);
        Assert.Equal("9f2c", status.ConfigHash);
        Assert.Equal("41ab", status.ExecutionFingerprint);
        Assert.True(status.Tuning);
        Assert.Equal("op-7", status.OperationId);
        Assert.Equal(LaunchDelivery.Container, status.Delivery);
        Assert.Equal(LaunchDelivery.Container, h.Driver.Delivery);
    }

    [Fact]
    public async Task List_AdoptsContainersWithoutANodeLabel_AndIgnoresOtherNodes()
    {
        var h = new Harness();
        h.Docker.Add("c-old", "old", "running", Labels("old", "8101"));
        h.Docker.Add("c-mine", "mine", "running", Labels("mine", "8102", (ContainerLabels.Node, NodeId)));
        h.Docker.Add("c-other", "other", "running", Labels("other", "8103", (ContainerLabels.Node, "pc1")));

        var ids = (await h.Driver.ListAsync()).Select(s => s.ModelId).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["mine", "old"], ids);
        Assert.Null(await h.Driver.GetAsync("other"));

        await h.Driver.StopAsync("other", h.Stages);
        Assert.DoesNotContain(h.Docker.Calls, c => c.Contains("c-other", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AModelStartedBeforeTheRename_IsAdopted_NotOrphanedOrStartedTwice()
    {
        var h = new Harness();
        // Created by a pre-rename agent: every label under spark-serve.*.
        h.Docker.Add("c-legacy", "m1", "running", new Dictionary<string, string>
        {
            ["spark-serve.model"] = "m1",
            ["spark-serve.port"] = "8107",
            ["spark-serve.node"] = NodeId,
            ["spark-serve.config-hash"] = "legacy-hash",
            ["spark-serve.operation"] = "op-old",
        });

        var listed = Assert.Single(await h.Driver.ListAsync());
        Assert.Equal(("m1", "c-legacy", 8107, "legacy-hash"), (listed.ModelId, listed.InstanceId, listed.HostPort, listed.ConfigHash));

        var result = await h.LaunchAsync(LaunchMode.EnsureStarted);
        Assert.Equal(RuntimeLaunchOutcome.AlreadyRunning, result.Outcome);
        Assert.Equal("c-legacy", result.Instance.InstanceId);
        Assert.DoesNotContain(h.Docker.Calls, c => c.StartsWith("POST", StringComparison.Ordinal) || c.StartsWith("DELETE", StringComparison.Ordinal));
        // Both label spellings were asked for.
        Assert.Contains(h.Docker.Calls, c => Uri.UnescapeDataString(c).Contains("\"spark-serve.model\"", StringComparison.Ordinal));

        // A new instance gets only the current labels.
        await h.Driver.StopAsync("m1", h.Stages);
        h.Docker.Containers.Clear();
        await h.LaunchAsync(LaunchMode.EnsureStarted);
        Assert.All(h.Docker.Containers.Single().Labels.Keys, k => Assert.StartsWith("mirbadllm.", k, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------ launch modes

    [Fact]
    public async Task EnsureStarted_NoInstance_CreatesAndStarts_ReportingStages()
    {
        var h = new Harness();

        var result = await h.LaunchAsync(LaunchMode.EnsureStarted);

        Assert.Equal(RuntimeLaunchOutcome.Created, result.Outcome);
        Assert.Equal(ModelState.Starting, result.Instance.State);
        Assert.Equal(8101, result.Instance.HostPort);
        Assert.Equal("op-1", result.Instance.OperationId);
        Assert.NotNull(result.Instance.InstanceId);
        Assert.Equal(
            ["before:created", "reached:created", "before:started", "reached:started"],
            h.Stages.Events);
        Assert.Equal("running", h.Docker.Containers.Single().State);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, ModelState.Ready)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ModelState.Starting)]
    public async Task EnsureStarted_RunningInstance_IsReturnedAsIs_WhateverItsConfiguration(HttpStatusCode health, ModelState state)
    {
        var h = new Harness(health: health);
        h.Docker.Add("container-m1", "m1", "running", Labels("m1", extra: [(ContainerLabels.ConfigHash, "an-old-hash")]));

        // The spec differs from what runs (other args, other hash): ensure-started must not touch it.
        var result = await h.LaunchAsync(LaunchMode.EnsureStarted, SpecFor(Saved with { MaxNumSeqs = 32 }));

        Assert.Equal(RuntimeLaunchOutcome.AlreadyRunning, result.Outcome);
        Assert.Equal(state, result.Instance.State);
        Assert.Equal("container-m1", result.Instance.InstanceId);
        Assert.Equal("an-old-hash", result.Instance.ConfigHash);
        Assert.Empty(h.Stages.Events);
        Assert.DoesNotContain(h.Docker.Calls, c => c.StartsWith("POST", StringComparison.Ordinal) || c.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("exited")]
    [InlineData("dead")]
    public async Task EnsureStarted_FailedContainer_IsRemovedAndRecreated(string dockerState)
    {
        var h = new Harness();
        h.Docker.Add("container-m1", "m1", dockerState, Labels("m1"));

        var result = await h.LaunchAsync(LaunchMode.EnsureStarted);

        Assert.Equal(RuntimeLaunchOutcome.Created, result.Outcome);
        int remove = h.Docker.Calls.IndexOf("DELETE /v1.47/containers/container-m1");
        int create = h.Docker.Calls.FindIndex(c => c.StartsWith("POST /v1.47/containers/create", StringComparison.Ordinal));
        Assert.True(remove >= 0 && create > remove);
        Assert.DoesNotContain("POST /v1.47/containers/container-m1/stop?t=30", h.Docker.Calls);
        Assert.Equal(
            ["before:removed", "reached:removed", "before:created", "reached:created", "before:started", "reached:started"],
            h.Stages.Events);
        Assert.NotEqual("container-m1", Assert.Single(h.Docker.Containers).Id);
    }

    [Fact]
    public async Task Replace_RunningInstance_StopsRemovesAndRecreates_EvenWithAnIdenticalSpec()
    {
        var h = new Harness();
        var spec = SpecFor(Saved);
        await h.LaunchAsync(LaunchMode.EnsureStarted, spec);
        string firstId = h.Docker.Containers.Single().Id;
        h.Stages.Events.Clear();
        h.Docker.Calls.Clear();

        var result = await h.LaunchAsync(LaunchMode.Replace, spec, operationId: "op-2");

        Assert.Equal(RuntimeLaunchOutcome.Created, result.Outcome);
        int stop = h.Docker.Calls.IndexOf($"POST /v1.47/containers/{firstId}/stop?t=30");
        int remove = h.Docker.Calls.IndexOf($"DELETE /v1.47/containers/{firstId}");
        int create = h.Docker.Calls.FindIndex(c => c.StartsWith("POST /v1.47/containers/create", StringComparison.Ordinal));
        Assert.True(stop >= 0 && remove > stop && create > remove);
        Assert.Equal(
            ["before:stopping", "reached:stopping", "before:removed", "reached:removed", "before:created", "reached:created", "before:started", "reached:started"],
            h.Stages.Events);
        var container = Assert.Single(h.Docker.Containers);
        Assert.NotEqual(firstId, container.Id);
        Assert.Equal("op-2", container.Labels[ContainerLabels.Operation]);
    }

    [Fact]
    public async Task Replace_ExitedInstance_RemovesWithoutStopping()
    {
        var h = new Harness();
        h.Docker.Add("container-m1", "m1", "exited", Labels("m1"));

        await h.LaunchAsync(LaunchMode.Replace);

        Assert.DoesNotContain(h.Docker.Calls, c => c.Contains("container-m1/stop", StringComparison.Ordinal));
        Assert.Contains("DELETE /v1.47/containers/container-m1", h.Docker.Calls);
    }

    [Fact]
    public async Task Replace_NoInstance_JustCreates()
    {
        var h = new Harness();

        var result = await h.LaunchAsync(LaunchMode.Replace);

        Assert.Equal(RuntimeLaunchOutcome.Created, result.Outcome);
        Assert.Equal(["before:created", "reached:created", "before:started", "reached:started"], h.Stages.Events);
    }

    [Fact]
    public async Task Replace_FencingRefusalBeforeStop_LeavesTheRunningContainerAlone()
    {
        var h = new Harness();
        h.Docker.Add("container-m1", "m1", "running", Labels("m1"));
        h.Stages.FailBefore = OperationStages.Stopping;

        var ex = await Assert.ThrowsAsync<AgentException>(() => h.LaunchAsync(LaunchMode.Replace));

        Assert.Equal(ErrorCodes.Superseded, ex.Code);
        Assert.DoesNotContain(h.Docker.Calls, c => c.StartsWith("POST", StringComparison.Ordinal) || c.StartsWith("DELETE", StringComparison.Ordinal));
        Assert.Equal(ModelState.Ready, (await h.Driver.GetAsync("m1"))!.State);
    }

    [Fact]
    public async Task Launch_AdmissionRefusalBeforeCreate_CreatesNothing()
    {
        var h = new Harness();
        h.Stages.FailBefore = OperationStages.Created;

        await Assert.ThrowsAsync<AgentException>(() => h.LaunchAsync(LaunchMode.EnsureStarted));

        Assert.Empty(h.Docker.Containers);
        Assert.DoesNotContain(h.Docker.Calls, c => c.Contains("containers/create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Launch_MissingImage_IsPulledAsItsOwnStage()
    {
        var h = new Harness();
        h.Docker.ImagesPresent = false;

        await h.LaunchAsync(LaunchMode.EnsureStarted);

        Assert.Equal(["reached:pulling", "reached:pulled", "before:created", "reached:created", "before:started", "reached:started"], h.Stages.Events);
        Assert.Contains(h.Docker.Calls, c => c.StartsWith("POST /v1.47/images/create?fromImage=local%2Fqwen38-vllm-dflash2&tag=66e5414c6d75", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------ ports

    [Fact]
    public async Task Ports_ComeFromTheInjectedAllocator_WhenTheLaunchCarriesNone()
    {
        var h = new Harness();
        h.Ports.Next = 8107;

        var result = await h.LaunchAsync(LaunchMode.EnsureStarted, hostPort: 0);

        Assert.Equal(8107, result.Instance.HostPort);
        Assert.Equal([("m1", "op-1")], h.Ports.Calls);
        var body = h.Docker.CreateBodies.Single();
        Assert.Equal("8107", body["Labels"]![ContainerLabels.Port]!.GetValue<string>());
        var binding = body["HostConfig"]!["PortBindings"]!["8000/tcp"]![0]!;
        Assert.Equal("127.0.0.1", binding["HostIp"]!.GetValue<string>());
        Assert.Equal("8107", binding["HostPort"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ports_FromTheReservation_AreUsedAsGiven()
    {
        var h = new Harness();

        var result = await h.LaunchAsync(LaunchMode.EnsureStarted, hostPort: 8104);

        Assert.Equal(8104, result.Instance.HostPort);
        Assert.Empty(h.Ports.Calls);
    }

    // ------------------------------------------------------------ labels and guardrails

    [Fact]
    public async Task Labels_AreSetByTheDriver_NodeAndOperationIncluded()
    {
        var h = new Harness();

        await h.LaunchAsync(LaunchMode.EnsureStarted, operationId: "op-42");

        var labels = h.Docker.Containers.Single().Labels;
        Assert.Equal("m1", labels[ContainerLabels.Model]);
        Assert.Equal(NodeId, labels[ContainerLabels.Node]);
        Assert.Equal("op-42", labels[ContainerLabels.Operation]);
        Assert.Equal("8101", labels[ContainerLabels.Port]);
        Assert.Equal("M1", labels[ContainerLabels.ServedName]);
        Assert.Equal("40", labels[ContainerLabels.BudgetGiB]);
        Assert.False(labels.ContainsKey(ContainerLaunchValidator.ImageDigestLabel));
    }

    [Theory]
    [InlineData(ContainerLabels.Model)]
    [InlineData(ContainerLabels.Node)]
    [InlineData(ContainerLabels.Operation)]
    [InlineData(ContainerLabels.Port)]
    [InlineData(ContainerLaunchValidator.ImageDigestLabel)]
    [InlineData(ContainerLabels.Download)]
    [InlineData("mirbadllm.download.folder")]
    public async Task ReservedLabel_InTheSpec_IsSpecRejected(string label)
    {
        var h = new Harness();
        var spec = SpecFor(Saved);
        spec = spec with { Labels = new Dictionary<string, string>(spec.Labels) { [label] = "spoofed" } };

        var ex = await AssertRejectedAsync(h, spec);

        Assert.Contains(label, ex.Error.ReadDetails<SpecRejectedDetails>()!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mounts_OnlyLogicalVolumesAtFixedTargets()
    {
        var h = new Harness();
        var models = new MountSpec(LogicalVolumes.Models, null, LogicalVolumes.ModelsTarget, true);
        var cache = new MountSpec(LogicalVolumes.Cache, null, LogicalVolumes.CacheTarget, false);

        await AssertRejectedAsync(h, SpecFor(Saved) with { Mounts = [new MountSpec("data", null, "/data", true)] });
        await AssertRejectedAsync(h, SpecFor(Saved) with { Mounts = [models with { Target = "/etc" }] });
        await AssertRejectedAsync(h, SpecFor(Saved) with { Mounts = [cache with { Target = LogicalVolumes.ModelsTarget }] });
        await AssertRejectedAsync(h, SpecFor(Saved) with { Mounts = [models with { ReadOnly = false }] });
        await AssertRejectedAsync(h, SpecFor(Saved) with { Mounts = [models, models] });
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData("a/../../etc")]
    [InlineData("/etc")]
    [InlineData("C:/Windows")]
    [InlineData("a\\b")]
    [InlineData("a//b")]
    [InlineData("./a")]
    public async Task Mounts_SubPath_MustBeAPlainRelativePath(string subPath)
    {
        var h = new Harness();
        var spec = SpecFor(Saved) with { Mounts = [new MountSpec(LogicalVolumes.Models, subPath, LogicalVolumes.ModelsTarget, true)] };

        await AssertRejectedAsync(h, spec);
    }

    [Fact]
    public async Task Mounts_SubPath_InsideTheRoot_BindsTheHostPath()
    {
        using var dirs = new TempDirs();
        Directory.CreateDirectory(Path.Combine(dirs.Models, "qwen", "weights"));
        var h = new Harness(new ContainerRuntimeOptions { NodeId = NodeId, ModelsHostPath = "/srv/mirbadllm/models", ModelsLocalPath = dirs.Models });
        var spec = SpecFor(Saved) with { Mounts = [new MountSpec(LogicalVolumes.Models, "qwen/weights", LogicalVolumes.ModelsTarget, true)] };

        await h.LaunchAsync(LaunchMode.EnsureStarted, spec);

        var binds = h.Docker.CreateBodies.Single()["HostConfig"]!["Binds"]!.AsArray().Select(b => b!.GetValue<string>());
        Assert.Equal(["/srv/mirbadllm/models/qwen/weights:/models:ro"], binds);
    }

    [Fact]
    public async Task Mounts_SubPath_ThatDoesNotExist_IsRejected()
    {
        using var dirs = new TempDirs();
        var h = new Harness(new ContainerRuntimeOptions { NodeId = NodeId, ModelsLocalPath = dirs.Models });

        await AssertRejectedAsync(h, SpecFor(Saved) with { Mounts = [new MountSpec(LogicalVolumes.Models, "nope", LogicalVolumes.ModelsTarget, true)] });
    }

    [Fact]
    public async Task Mounts_SubPath_SymlinkEscapingTheRoot_IsRejected_AndOneStayingInside_IsResolved()
    {
        using var dirs = new TempDirs();
        Directory.CreateDirectory(Path.Combine(dirs.Models, "real"));
        if (!TryCreateDirectoryLink(Path.Combine(dirs.Models, "escape"), dirs.Outside) ||
            !TryCreateDirectoryLink(Path.Combine(dirs.Models, "alias"), Path.Combine(dirs.Models, "real")))
        {
            Assert.Skip("Creating symbolic links is not permitted for this user.");
        }

        var h = new Harness(new ContainerRuntimeOptions { NodeId = NodeId, ModelsHostPath = "/srv/m", ModelsLocalPath = dirs.Models });

        await AssertRejectedAsync(h, SpecFor(Saved) with { Mounts = [new MountSpec(LogicalVolumes.Models, "escape", LogicalVolumes.ModelsTarget, true)] });

        await h.LaunchAsync(LaunchMode.EnsureStarted, SpecFor(Saved) with { Mounts = [new MountSpec(LogicalVolumes.Models, "alias", LogicalVolumes.ModelsTarget, true)] });
        var binds = h.Docker.CreateBodies.Single()["HostConfig"]!["Binds"]!.AsArray().Select(b => b!.GetValue<string>());
        Assert.Equal(["/srv/m/real:/models:ro"], binds);
    }

    [Fact]
    public async Task Spec_OutsideWhatThisAgentRuns_IsRejected()
    {
        var h = new Harness();
        var spec = SpecFor(Saved);

        await AssertRejectedAsync(h, spec with { Kind = LaunchDelivery.Process });
        await AssertRejectedAsync(h, spec with { Image = null });
        await AssertRejectedAsync(h, spec with { Image = "bad image" });
        await AssertRejectedAsync(h, spec with { Executable = "llama-server" });
        await AssertRejectedAsync(h, spec with { ContainerPort = 8001 });
        await AssertRejectedAsync(h, spec with { Env = new Dictionary<string, string> { ["NOT A NAME"] = "x" } }); // MN-107: env accepted, bad names not
        await AssertRejectedAsync(h, spec with { Gpus = new GpuSelection { Mode = GpuSelectionMode.Uuids, Uuids = ["GPU-1"] } });
        await AssertRejectedAsync(h, spec with { HealthPath = "/other" });
        await AssertRejectedAsync(h, spec, ErrorCodes.InvalidRequest, modelId: "../evil");
    }

    [Fact]
    public async Task ImageDigest_IsCheckedAgainstTheApprovedList_AndReadBack()
    {
        var options = new ContainerRuntimeOptions { NodeId = NodeId };
        options.ApprovedImages.Add($"local/qwen38-vllm-dflash2@{Digest}");
        var h = new Harness(options);

        await AssertRejectedAsync(h, SpecFor(Saved) with { ImageDigest = "sha256:" + new string('f', 64) });
        await AssertRejectedAsync(h, SpecFor(Saved) with { ImageDigest = "sha256:NOT-HEX" });
        await AssertRejectedAsync(h, SpecFor(Saved) with { Image = "evil/image:66e5414c6d75", ImageDigest = Digest });

        await h.LaunchAsync(LaunchMode.EnsureStarted, SpecFor(Saved) with { ImageDigest = Digest });

        Assert.Equal(Digest, (await h.Driver.GetLaunchConfigAsync("m1"))!.ImageDigest);
    }

    [Fact]
    public async Task ImageDigest_Required_RefusesTagOnlySpecs()
    {
        var h = new Harness(new ContainerRuntimeOptions { NodeId = NodeId, RequireApprovedDigest = true });

        await AssertRejectedAsync(h, SpecFor(Saved));
    }

    [Fact]
    public void Repository_StripsTagAndDigest_ButNotARegistryPort()
    {
        Assert.Equal("local/qwen", ContainerLaunchValidator.Repository("local/qwen:abc"));
        Assert.Equal("host:5000/a/b", ContainerLaunchValidator.Repository("host:5000/a/b:tag"));
        Assert.Equal("host:5000/a/b", ContainerLaunchValidator.Repository("host:5000/a/b"));
        Assert.Equal("a/b", ContainerLaunchValidator.Repository($"a/b@{Digest}"));
    }

    // ------------------------------------------------------------ parity with the pre-P1 container (recorded)

    public static TheoryData<string> ParityCases => ["default", "custom-image", "tuning"];

    /// <summary>
    /// The container the pre-P1 single-host runtime (<c>DockerModelRuntime</c>, deleted by MN-047, D60) created for the
    /// same definition and settings, recorded from it before its removal: <c>Agent/Fixtures/legacy-container-create.json</c>
    /// holds per case the container name, the full <c>containers/create</c> body and the Docker calls that changed state
    /// (its own container scans for admission and ports left out, the container id as <c>&lt;id&gt;</c>).
    /// </summary>
    private static JsonNode LegacyCreate(string name, [CallerFilePath] string sourceFile = "") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures", "legacy-container-create.json")))![name]!;

    [Theory]
    [MemberData(nameof(ParityCases))]
    public async Task Parity_TheCreatedContainerEqualsTheRecordedLegacyOne(string name)
    {
        var definition = name switch
        {
            "custom-image" => Saved with { Image = "registry.local:5000/vllm/custom:1.2", MaxNumSeqs = 8, MaxModelLen = 65536 },
            _ => Saved,
        };
        bool tuning = name == "tuning";
        var legacy = LegacyCreate(name);

        // Agent: the driver with the settings the legacy runtime had (MirbadLLM defaults, this image), given the master's spec.
        var sparkOptions = new MirbadLLMOptions { VllmImage = DefaultImage };
        var h = new Harness(ContainerRuntimeOptions.FromMirbadLLMOptions(sparkOptions, NodeId));
        await h.LaunchAsync(tuning ? LaunchMode.Replace : LaunchMode.EnsureStarted, SpecFor(definition, tuning));

        Assert.Equal([legacy["name"]!.GetValue<string>()], h.Docker.CreateNames);
        var expected = legacy["createBody"]!;
        // The one deliberate difference from the recording: model containers use RestartPolicy "no" so Docker never
        // starts a model ahead of the controller's one-at-a-time boot auto-start (2026-10-15, DockerEngineClient).
        Assert.Equal("unless-stopped", expected["HostConfig"]!["RestartPolicy"]!["Name"]!.GetValue<string>());
        expected["HostConfig"]!["RestartPolicy"]!["Name"] = "no";
        var actual = h.Docker.CreateBodies.Single();
        var actualLabels = actual["Labels"]!.AsObject();
        Assert.Equal(NodeId, actualLabels[ContainerLabels.Node]!.GetValue<string>());
        Assert.Equal("op-1", actualLabels[ContainerLabels.Operation]!.GetValue<string>());
        actualLabels.Remove(ContainerLabels.Node);
        actualLabels.Remove(ContainerLabels.Operation);
        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            $"legacy: {expected.ToJsonString()}\nagent:  {actual.ToJsonString()}");

        // The same Docker calls in the same order (minus the container scans).
        var mutations = h.Docker.Calls.Where(c => !c.StartsWith("GET /v1.47/containers/json", StringComparison.Ordinal))
            .Select(c => c.Replace(h.Docker.Containers.Single().Id, "<id>", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(legacy["calls"]!.AsArray().Select(c => c!.GetValue<string>()), mutations);
    }

    [Fact]
    public async Task Parity_LaunchConfig_RecomputesTheSameConfigHash()
    {
        var h = new Harness();
        var spec = SpecFor(Saved with { MaxNumSeqs = 32 });
        await h.LaunchAsync(LaunchMode.EnsureStarted, spec);

        var config = await h.Driver.GetLaunchConfigAsync("m1");

        Assert.NotNull(config);
        Assert.Equal(DefaultImage, config.Image);
        Assert.Equal(spec.Argv, config.Argv);
        Assert.Equal(spec.Labels[ContainerLabels.ConfigHash], ModelConfigHash.Compute(config.Image!, config.Argv));
        Assert.Equal(spec.Labels[ContainerLabels.ConfigHash], config.Labels[ContainerLabels.ConfigHash]);
        Assert.Equal(Created, config.CreatedAt);
        Assert.Empty(config.Gpus);

        // The master reads the definition back from it exactly as it did from the pre-P1 runtime's containers.
        var parsed = VllmArgsParser.Parse(config.Argv, config.Image!, DefaultImage, TotalGiB, Saved);
        Assert.Equal(32, parsed.MaxNumSeqs);
    }

    [Fact]
    public async Task LaunchConfig_NoInstance_IsNull()
    {
        Assert.Null(await new Harness().Driver.GetLaunchConfigAsync("m1"));
    }

    // ------------------------------------------------------------ stop, start-created, logs, upstream, folders

    [Fact]
    public async Task Stop_NoInstance_IsANoOp()
    {
        var h = new Harness();

        await h.Driver.StopAsync("m1", h.Stages);

        Assert.Empty(h.Stages.Events);
        Assert.DoesNotContain(h.Docker.Calls, c => c.StartsWith("POST", StringComparison.Ordinal) || c.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stop_StopsThenRemoves_ReportingStages()
    {
        var h = new Harness();
        h.Docker.Add("container-m1", "m1", "running", Labels("m1"));

        await h.Driver.StopAsync("m1", h.Stages);

        int stop = h.Docker.Calls.IndexOf("POST /v1.47/containers/container-m1/stop?t=30");
        int remove = h.Docker.Calls.IndexOf("DELETE /v1.47/containers/container-m1");
        Assert.True(stop >= 0 && remove > stop);
        Assert.Equal(["before:stopping", "reached:stopping", "before:removed", "reached:removed"], h.Stages.Events);
        Assert.Empty(h.Docker.Containers);
        Assert.Null(await h.Driver.GetAsync("m1"));
    }

    [Fact]
    public async Task Stop_ReportsStoppingWhileInProgress()
    {
        var h = new Harness();
        h.Docker.Add("container-m1", "m1", "running", Labels("m1"));
        RuntimeStatus? during = null;
        h.Stages.OnReached = async stage =>
        {
            if (stage == OperationStages.Stopping)
            {
                during = await h.Driver.GetAsync("m1");
            }
        };

        await h.Driver.StopAsync("m1", h.Stages);

        Assert.Equal(ModelState.Stopping, during!.State);
    }

    [Fact]
    public async Task Stop_ContainerAlreadyGone_Succeeds()
    {
        var h = new Harness();
        h.Docker.Add("container-m1", "m1", "running", Labels("m1"));
        h.Docker.VanishOnStop = true;

        await h.Driver.StopAsync("m1", h.Stages);

        Assert.Equal(["before:stopping", "reached:stopping", "before:removed", "reached:removed"], h.Stages.Events);
    }

    [Fact]
    public async Task StartCreated_StartsACreatedContainer()
    {
        var h = new Harness(health: HttpStatusCode.ServiceUnavailable);
        h.Docker.Add("container-m1", "m1", "created", Labels("m1"));

        var status = await h.Driver.StartCreatedAsync("m1");

        Assert.Contains("POST /v1.47/containers/container-m1/start", h.Docker.Calls);
        Assert.Equal(ModelState.Starting, status.State);
    }

    [Fact]
    public async Task StartCreated_NoInstance_IsNotFound()
    {
        var ex = await Assert.ThrowsAsync<AgentException>(() => new Harness().Driver.StartCreatedAsync("m1"));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task Logs_AreStreamedFromTheContainer()
    {
        var h = new Harness();
        h.Docker.Add("container-m1", "m1", "running", Labels("m1"));
        h.Docker.LogText = "line one\nline two\n";

        var lines = new List<string>();
        await foreach (var line in h.Driver.StreamLogsAsync("m1", tail: 50, follow: false))
        {
            lines.Add(line);
        }

        Assert.Equal(["line one", "line two"], lines);
        Assert.Contains("GET /v1.47/containers/container-m1/logs?stdout=1&stderr=1&tail=50&follow=false", h.Docker.Calls);
        Assert.Empty(await ToListAsync(h.Driver.StreamLogsAsync("other", 50, false)));
    }

    [Fact]
    public async Task Upstream_OnlyForARunningContainer()
    {
        var h = new Harness();
        h.Docker.Add("container-m1", "m1", "running", Labels("m1"));
        h.Docker.Add("container-m2", "m2", "exited", Labels("m2", "8102"));

        Assert.Equal(new Uri("http://ss-m1:8000/"), await h.Driver.GetUpstreamAsync("m1"));
        Assert.Null(await h.Driver.GetUpstreamAsync("m2"));
        Assert.Null(await h.Driver.GetUpstreamAsync("m3"));
    }

    [Fact]
    public async Task FolderInUse_WhenAnInstanceServesFromIt()
    {
        var h = new Harness();
        h.Docker.Add("container-m1", "m1", "exited", Labels("m1"), cmd: ["--model", "/models/qwen", "--port", "8000"]);
        h.Docker.Add("container-m2", "m2", "running", Labels("m2", "8102"), cmd: ["--model", "/models/llama/sub"]);
        h.Docker.Add("container-m3", "m3", "running", Labels("m3", "8103"), cmd: ["--tokenizer=/models/tok"]);

        Assert.True(await h.Driver.IsFolderInUseAsync("qwen"));
        Assert.True(await h.Driver.IsFolderInUseAsync("llama"));
        Assert.True(await h.Driver.IsFolderInUseAsync("tok"));
        Assert.False(await h.Driver.IsFolderInUseAsync("qwen2"));
        var ex = await Assert.ThrowsAsync<AgentException>(() => h.Driver.IsFolderInUseAsync("../etc"));
        Assert.Equal(ErrorCodes.InvalidRequest, ex.Code);
    }

    [Fact]
    public void PathConfinement_IsInside_RespectsSegmentBoundaries()
    {
        string root = Path.Combine(Path.GetTempPath(), "root");
        Assert.True(PathConfinement.IsInside(root, Path.Combine(root, "a")));
        Assert.True(PathConfinement.IsInside(root, root));
        Assert.False(PathConfinement.IsInside(root, root + "2"));
        Assert.False(PathConfinement.IsInside(root, Path.GetTempPath()));
    }

    private static async Task<List<string>> ToListAsync(IAsyncEnumerable<string> source)
    {
        var list = new List<string>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }

    private static bool TryCreateDirectoryLink(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------ fakes

    private sealed class TempDirs : IDisposable
    {
        private readonly string _base = Path.Combine(Path.GetTempPath(), "ss-driver-" + Guid.NewGuid().ToString("N"));

        public TempDirs()
        {
            Directory.CreateDirectory(Models);
            Directory.CreateDirectory(Outside);
        }

        public string Models => Path.Combine(_base, "models");

        public string Outside => Path.Combine(_base, "outside");

        public void Dispose()
        {
            try
            {
                Directory.Delete(_base, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    internal sealed class FakePortAllocator : IPortAllocator
    {
        public int Next { get; set; } = 8101;

        public List<(string ModelId, string OperationId)> Calls { get; } = [];

        public Task<int> AllocatePortAsync(string modelId, string operationId, CancellationToken ct = default)
        {
            Calls.Add((modelId, operationId));
            return Task.FromResult(Next);
        }
    }

    internal sealed class RecordingStageSink : IRuntimeStageSink
    {
        public List<string> Events { get; } = [];

        public string? FailBefore { get; set; }

        public Func<string, Task>? OnReached { get; set; }

        public Task BeforeStepAsync(string stage, CancellationToken ct)
        {
            if (stage == FailBefore)
            {
                throw new AgentException(409, new AgentError { Code = ErrorCodes.Superseded, Message = "superseded" });
            }

            Events.Add("before:" + stage);
            return Task.CompletedTask;
        }

        public async Task StageReachedAsync(string stage, OperationProgress? progress, CancellationToken ct)
        {
            Events.Add("reached:" + stage);
            if (OnReached is not null)
            {
                await OnReached(stage);
            }
        }
    }

    internal sealed class FakeContainer
    {
        public required string Id { get; init; }

        public required string Name { get; init; }

        public required string State { get; set; }

        public required Dictionary<string, string> Labels { get; init; }

        public string Image { get; init; } = DefaultImage;

        public IReadOnlyList<string> Cmd { get; init; } = [];

        public int RestartCount { get; init; }
    }

    /// <summary>A small stateful Docker Engine behind <see cref="FakeHttpMessageHandler"/>: containers, images, logs.</summary>
    internal sealed class FakeDockerEngine
    {
        private int _next;

        public FakeDockerEngine()
        {
            Handler = new FakeHttpMessageHandler { Handler = Handle };
            Client = new DockerEngineClient(new HttpClient(Handler) { BaseAddress = new Uri("http://docker/v1.47/") });
        }

        public FakeHttpMessageHandler Handler { get; }

        public DockerEngineClient Client { get; }

        public List<FakeContainer> Containers { get; } = [];

        public List<string> Calls { get; } = [];

        public List<JsonNode> CreateBodies { get; } = [];

        public List<string> CreateNames { get; } = [];

        public bool ImagesPresent { get; set; } = true;

        public bool VanishOnStop { get; set; }

        public string LogText { get; set; } = "";

        public void Add(string id, string modelId, string state, Dictionary<string, string> labels, int restartCount = 0, IReadOnlyList<string>? cmd = null) =>
            Containers.Add(new FakeContainer
            {
                Id = id,
                Name = "ss-" + modelId,
                State = state,
                Labels = labels,
                RestartCount = restartCount,
                Cmd = cmd ?? [],
            });

        private HttpResponseMessage Handle(HttpRequestMessage request)
        {
            string path = request.RequestUri!.AbsolutePath;
            string query = request.RequestUri.Query;
            Calls.Add($"{request.Method} {path}{query}");
            int bodyIndex = Handler.Requests.Count - 1;

            if (request.Method == HttpMethod.Get && path.EndsWith("/containers/json", StringComparison.Ordinal))
            {
                return Json(JsonSerializer.Serialize(Matching(query).Select(c => new
                {
                    c.Id,
                    Names = new[] { "/" + c.Name },
                    c.State,
                    Status = "irrelevant",
                    Created = Created.ToUnixTimeSeconds(),
                    c.Labels,
                })));
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/containers/create", StringComparison.Ordinal))
            {
                var body = JsonNode.Parse(Handler.RequestBodies[bodyIndex])!;
                CreateBodies.Add(body);
                string name = Uri.UnescapeDataString(query["?name=".Length..]);
                CreateNames.Add(name);
                string id = $"new-{++_next}";
                Containers.Add(new FakeContainer
                {
                    Id = id,
                    Name = name,
                    State = "created",
                    Labels = body["Labels"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()),
                    Image = body["Image"]!.GetValue<string>(),
                    Cmd = body["Cmd"]!.AsArray().Select(a => a!.GetValue<string>()).ToList(),
                });
                return Json($$"""{"Id":"{{id}}"}""");
            }

            if (path.Contains("/containers/", StringComparison.Ordinal))
            {
                string rest = path[(path.IndexOf("/containers/", StringComparison.Ordinal) + "/containers/".Length)..];
                string id = rest.Split('/')[0];
                string action = rest.Contains('/', StringComparison.Ordinal) ? rest[(rest.IndexOf('/', StringComparison.Ordinal) + 1)..] : "";
                var container = Containers.FirstOrDefault(c => c.Id == id);
                if (container is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"No such container"}""") };
                }

                switch (request.Method.Method, action)
                {
                    case ("GET", "json"):
                        return Json(JsonSerializer.Serialize(new
                        {
                            container.RestartCount,
                            State = new { ExitCode = 0, OOMKilled = false },
                            Config = new { container.Image, container.Cmd },
                        }));
                    case ("GET", "logs"):
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(LogFrame(LogText)) };
                    case ("POST", "start"):
                        container.State = "running";
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    case ("POST", "stop"):
                        if (VanishOnStop)
                        {
                            Containers.Remove(container);
                            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
                        }

                        container.State = "exited";
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    case ("DELETE", ""):
                        Containers.Remove(container);
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    default:
                        return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }
            }

            if (path.Contains("/networks/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }

            if (request.Method == HttpMethod.Get && path.Contains("/images/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(ImagesPresent ? HttpStatusCode.OK : HttpStatusCode.NotFound) { Content = new StringContent("{}") };
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/images/create", StringComparison.Ordinal))
            {
                ImagesPresent = true;
                return Json("""{"status":"Downloaded newer image"}""");
            }

            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        /// <summary>Containers matching every <c>label</c> filter (<c>key</c> or <c>key=value</c>) of the query.</summary>
        private List<FakeContainer> Matching(string query)
        {
            int start = query.IndexOf("filters=", StringComparison.Ordinal);
            string json = Uri.UnescapeDataString(query[(start + "filters=".Length)..]);
            using var doc = JsonDocument.Parse(json);
            var filters = doc.RootElement.GetProperty("label").EnumerateArray().Select(e => e.GetString()!).ToList();
            return Containers.Where(c => filters.All(f =>
            {
                int eq = f.IndexOf('=', StringComparison.Ordinal);
                return eq < 0
                    ? c.Labels.ContainsKey(f)
                    : c.Labels.TryGetValue(f[..eq], out string? v) && v == f[(eq + 1)..];
            })).ToList();
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static byte[] LogFrame(string text)
        {
            byte[] payload = Encoding.UTF8.GetBytes(text);
            var header = new byte[8];
            header[0] = 1;
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), payload.Length);
            return [.. header, .. payload];
        }
    }
}
