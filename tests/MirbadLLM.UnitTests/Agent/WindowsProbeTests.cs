using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;
using MirbadLLM.NodeRuntime.Abstractions;

#pragma warning disable CA1707, CA2000 // underscore test names; HttpResponseMessage ownership passes to HttpClient in fakes.

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// MN-214: the Windows probes. <see cref="WindowsMemoryProbe"/> (<c>GlobalMemoryStatusEx</c>), the GPU process probe over
/// <c>nvidia-smi --query-compute-apps</c> with WDDM's <c>[N/A]</c> memory mapped to models through
/// <c>processes.json</c>, the "measured at start" meter (<c>nvidia-smi</c> only, spike S1 / C9) and the
/// <c>gpu_external_use_high</c> reason with its 2-sample hysteresis, fed by the sampler's hook. The CSV fixtures are the
/// shape captured on the owner's PC (RTX 5070 + RTX 5060 Ti, driver 617.14) with placeholder UUIDs, PIDs and user names.
/// </summary>
public sealed class WindowsProbeTests
{
    private const string Gpu0 = "GPU-00000000-0000-4000-8000-000000000000";
    private const string Gpu1 = "GPU-00000000-0000-4000-8000-000000000001";
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// <c>--query-compute-apps=gpu_uuid,pid,used_memory,process_name --format=csv,noheader,nounits</c> as the PC prints it
    /// (CRLF, every memory <c>[N/A]</c> under WDDM). PID 4100 is the agent's llama-server, 11884 the owner's own one in
    /// <c>D:\AI</c> (foreign), and one path contains a comma.
    /// </summary>
    private const string ComputeAppsCsv =
        Gpu0 + ", 2648, [N/A], C:\\Windows\\System32\\dwm.exe\r\n" +
        Gpu0 + ", 23420, [N/A], C:\\Users\\owner\\AppData\\Local\\PowerToys\\PowerToys.exe\r\n" +
        Gpu0 + ", 35616, [N/A], C:\\Program Files (x86)\\Microsoft\\EdgeWebView\\Application\\154.0.4258.53\\msedgewebview2.exe\r\n" +
        Gpu0 + ", 11884, [N/A], D:\\AI\\llama.cpp\\llama-server.exe\r\n" +
        Gpu1 + ", 4100, [N/A], C:\\Program Files\\mirbadllm-agent\\runtimes\\llama.cpp\\b11476\\llama-server.exe\r\n" +
        Gpu1 + ", 69420, [N/A], C:\\Users\\owner\\AppData\\Local\\Comfy, Desktop\\standalone-env\\python.exe\r\n" +
        Gpu1 + ", 59420, [N/A], C:\\Windows\\System32\\dwm.exe\r\n";

    // ------------------------------------------------------------------ host memory

    [Fact]
    public void WindowsMemory_FromStatus_ConvertsBytesToGiB()
    {
        // 127 GiB installed (the PC), 96.5 GiB available.
        var snapshot = WindowsMemoryProbe.FromStatus(127UL * 1024 * 1024 * 1024, 103_616_086_016UL);

        Assert.Equal(127, snapshot.TotalGiB, 6);
        Assert.Equal(96.5, snapshot.AvailableGiB, 6);
    }

    [Theory]
    [InlineData(0UL, 0UL)]
    [InlineData(1024UL, 2048UL)]
    public void WindowsMemory_FromStatus_RefusesImpossibleValues(ulong total, ulong available) =>
        Assert.Throws<FormatException>(() => WindowsMemoryProbe.FromStatus(total, available));

    [Fact]
    [Trait("Os", "Windows")]
    public async Task WindowsMemory_RealCall_MatchesTheRuntimesPhysicalMemory()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("GlobalMemoryStatusEx exists only on Windows.");
            return;
        }

        var snapshot = await new WindowsMemoryProbe().GetAsync(TestContext.Current.CancellationToken);

        // The GC reads the same physical total (no job memory limit on a test run).
        double gcTotalGiB = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024d * 1024 * 1024);
        Assert.InRange(snapshot.TotalGiB, gcTotalGiB * 0.99, gcTotalGiB * 1.01);
        Assert.InRange(snapshot.AvailableGiB, 0.01, snapshot.TotalGiB);
    }

    // ------------------------------------------------------------------ GPU processes

    [Fact]
    public void ComputeApps_CapturedWddmCsv_ParsesWithoutMemoryAndNamesTheModel()
    {
        var processes = NvidiaSmiGpuProcessProbe.Parse(ComputeAppsCsv, new Dictionary<int, string> { [4100] = "qwen3-embedding-4b" });

        Assert.NotNull(processes);
        Assert.Equal(7, processes.Count);
        Assert.All(processes, p => Assert.Equal(0, p.UsedMiB));
        Assert.All(processes, p => Assert.Null(p.ContainerId));
        Assert.All(processes, p => Assert.Null(p.Unit));

        var model = Assert.Single(processes, p => p.DisplayName is not null);
        Assert.Equal((4100, "llama-server.exe", "qwen3-embedding-4b", Gpu1), (model.Pid, model.Name, model.DisplayName, model.GpuUuid));

        // Desktop processes: listed by file name (no path, no user name), on their GPU, not mirbadllm's.
        Assert.Equal(
            ["dwm.exe", "PowerToys.exe", "msedgewebview2.exe", "llama-server.exe", "python.exe", "dwm.exe"],
            processes.Where(p => p.DisplayName is null).Select(p => p.Name));
        var foreign = Assert.Single(processes, p => p.Pid == 11884);
        Assert.Null(foreign.DisplayName);
        Assert.Equal(Gpu0, foreign.GpuUuid);
        Assert.DoesNotContain(processes, p => p.Name.Contains("owner", StringComparison.Ordinal) || p.Name.Contains('\\', StringComparison.Ordinal));
    }

    [Fact]
    public void ComputeApps_ReportedMemoryIsKept()
    {
        // A driver that reports per-process memory (TCC, Linux) keeps the figure.
        var processes = NvidiaSmiGpuProcessProbe.Parse($"{Gpu1}, 4100, 7786, /opt/llama/llama-server\n", new Dictionary<int, string>());

        var process = Assert.Single(processes!);
        Assert.Equal(7786, process.UsedMiB);
        Assert.Equal("llama-server", process.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\n")]
    public void ComputeApps_NoProcess_IsAnEmptyList(string output) =>
        Assert.Empty(NvidiaSmiGpuProcessProbe.Parse(output, new Dictionary<int, string>())!);

    [Theory]
    [InlineData("GPU-x, 4100, [N/A]")]
    [InlineData("GPU-x, [N/A], [N/A], a.exe")]
    [InlineData("GPU-x, -3, [N/A], a.exe")]
    [InlineData("GPU-x, 4.5, [N/A], a.exe")]
    [InlineData("No devices were found")]
    public void ComputeApps_UnexpectedOutput_IsUnavailable(string output) =>
        Assert.Null(NvidiaSmiGpuProcessProbe.Parse(output, new Dictionary<int, string>()));

    [Fact]
    public async Task ProcessProbe_MapsPidsThroughTheRecordStore()
    {
        var smi = new FakeNvidiaSmi(args => args == NvidiaSmiGpuProcessProbe.Arguments ? Ok(ComputeAppsCsv) : Failed());
        var store = new FakeRecordStore(Record("qwen3-embedding-4b", 4100, [Gpu1], measured: 7.6), Record("other", 999, [Gpu0], measured: 1));
        var probe = new NvidiaSmiGpuProcessProbe(smi, store, NullLogger<NvidiaSmiGpuProcessProbe>.Instance);

        var processes = await probe.GetAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(processes);
        Assert.Equal([4100], processes.Where(p => p.DisplayName == "qwen3-embedding-4b").Select(p => p.Pid));
        Assert.DoesNotContain(processes, p => p.DisplayName == "other"); // its PID is not on any GPU
        Assert.Equal([NvidiaSmiGpuProcessProbe.Arguments], smi.Calls);
    }

    [Fact]
    public async Task ProcessProbe_FailingNvidiaSmi_IsUnavailable()
    {
        var store = new FakeRecordStore();
        Assert.Null(await new NvidiaSmiGpuProcessProbe(new FakeNvidiaSmi(_ => Failed()), store, NullLogger<NvidiaSmiGpuProcessProbe>.Instance)
            .GetAsync(TestContext.Current.CancellationToken));
        Assert.Null(await new NvidiaSmiGpuProcessProbe(new FakeNvidiaSmi(_ => NvidiaSmiResult.NotFound), store, NullLogger<NvidiaSmiGpuProcessProbe>.Instance)
            .GetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProcessProbe_UnreadableRecords_ListsEveryProcessWithoutModels()
    {
        var store = new FakeRecordStore(Record("qwen3-embedding-4b", 4100, [Gpu1], measured: 7.6)) { Fail = true };
        var probe = new NvidiaSmiGpuProcessProbe(new FakeNvidiaSmi(_ => Ok(ComputeAppsCsv)), store, NullLogger<NvidiaSmiGpuProcessProbe>.Instance);

        var processes = await probe.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, processes!.Count);
        Assert.All(processes, p => Assert.Null(p.DisplayName));
    }

    // ------------------------------------------------------------------ measured at start

    [Fact]
    public async Task StartMeter_MeasuresTheDeltaOfThePlacedGpusFromNvidiaSmi()
    {
        // S1's ub-2048 start on the 5060 Ti: 3851 → 11637 MiB (+7786); the display card moves on its own (desktop noise)
        // and is not placed, so it does not count.
        var smi = new SteppingNvidiaSmi(QueryGpu((10167, 3851)), QueryGpu((10300, 11637)));
        var meter = Meter(smi);

        var mark = await meter.MarkAsync([Gpu1], TestContext.Current.CancellationToken);
        Assert.NotNull(mark);
        Assert.Equal(T0, mark.At);
        Assert.Equal(3851 / 1024d, mark.UsedGiB[Gpu1], 9);

        double? measured = await meter.MeasureAsync(mark, TestContext.Current.CancellationToken);
        Assert.Equal(7786 / 1024d, measured!.Value, 9);
        Assert.All(smi.Calls, c => Assert.Equal(NvidiaSmiGpuTelemetryProbe.Arguments, c));
    }

    [Fact]
    public async Task StartMeter_SumsSeveralGpus_AndIsNeverNegative()
    {
        var two = Meter(new SteppingNvidiaSmi(QueryGpu((1000, 2000)), QueryGpu((4000, 6000))));
        var mark = await two.MarkAsync([Gpu0, Gpu1], TestContext.Current.CancellationToken);
        Assert.Equal(7000 / 1024d, (await two.MeasureAsync(mark!, TestContext.Current.CancellationToken))!.Value, 9);

        // The desktop released more than the model took (S1's cold start: Δ below the buffers): 0, not negative.
        var released = Meter(new SteppingNvidiaSmi(QueryGpu((1000, 9000)), QueryGpu((1000, 8000))));
        var mark2 = await released.MarkAsync([Gpu1], TestContext.Current.CancellationToken);
        Assert.Equal(0, await released.MeasureAsync(mark2!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartMeter_UnreadableGpus_AreNotMeasured()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Null(await Meter(new SteppingNvidiaSmi(QueryGpu((1000, 2000)))).MarkAsync(["GPU-not-there"], ct));
        Assert.Null(await Meter(new SteppingNvidiaSmi(QueryGpu((1000, 2000)))).MarkAsync([], ct));
        Assert.Null(await Meter(new SteppingNvidiaSmi(Failed())).MarkAsync([Gpu1], ct));
        Assert.Null(await Meter(new SteppingNvidiaSmi(Ok($"1, {Gpu1}, 0, 7, 33, 225, [N/A], [N/A], [N/A], NVIDIA GeForce RTX 5060 Ti\r\n")))
            .MarkAsync([Gpu1], ct));

        // Readable at the mark, gone after Ready: not measured (never a partial sum).
        var meter = Meter(new SteppingNvidiaSmi(QueryGpu((1000, 2000)), Failed()));
        var mark = await meter.MarkAsync([Gpu0, Gpu1], ct);
        Assert.Null(await meter.MeasureAsync(mark!, ct));
    }

    // ------------------------------------------------------------------ external use + hysteresis

    [Fact]
    public async Task ExternalUse_IsUsedMinusMeasuredModels_AndNeedsTwoSamplesToRaise()
    {
        var store = new FakeRecordStore(Record("qwen3-embedding-4b", 4100, [Gpu1], measured: 7.6));
        var external = External(store, (Gpu1, 2.0));
        var ct = TestContext.Current.CancellationToken;

        // 12.6 used − 7.6 measured = 5.0 GiB external > 2.0 GiB reserve, once: not yet.
        await external.ObserveAsync(Sample(gpu1UsedGiB: 12.6), ct);
        Assert.Empty(await external.CheckAsync(ct));
        Assert.Equal(5.0, external.ExternalGiB[Gpu1], 9);

        await external.ObserveAsync(Sample(gpu1UsedGiB: 12.6), ct);
        var reason = Assert.Single(await external.CheckAsync(ct));
        Assert.Equal(HealthReasonCodes.GpuExternalUseHigh, reason.Code);
        Assert.Equal(
            "GPU 1 (NVIDIA GeForce RTX 5060 Ti): 5.0 GiB used by programs other than mirbadllm's models, above its 2.0 GiB " +
            "reserve (12.6 GiB used, 7.6 GiB measured at start for models). Models keep running; new starts on these GPUs may be refused.",
            reason.Message);

        // One sample back under the reserve does not clear it; the second does.
        await external.ObserveAsync(Sample(gpu1UsedGiB: 9.0), ct);
        Assert.Single(await external.CheckAsync(ct));
        await external.ObserveAsync(Sample(gpu1UsedGiB: 9.0), ct);
        Assert.Empty(await external.CheckAsync(ct));
    }

    [Fact]
    public async Task ExternalUse_AlternatingSamplesNeverFlap()
    {
        var external = External(new FakeRecordStore(), (Gpu1, 2.0));
        var ct = TestContext.Current.CancellationToken;
        for (int i = 0; i < 6; i++)
        {
            await external.ObserveAsync(Sample(gpu1UsedGiB: i % 2 == 0 ? 3.0 : 1.0), ct);
            Assert.Empty(await external.CheckAsync(ct));
        }
    }

    [Fact]
    public async Task ExternalUse_AnInstanceWithoutMeasurement_LeavesTheGpuUnjudged()
    {
        // A model still starting (no measured delta): its memory would count as external, so the GPU keeps its state.
        var store = new FakeRecordStore(Record("starting", 4100, [Gpu1], measured: null));
        var external = External(store, (Gpu1, 2.0));
        var ct = TestContext.Current.CancellationToken;

        await external.ObserveAsync(Sample(gpu1UsedGiB: 12.0), ct);
        await external.ObserveAsync(Sample(gpu1UsedGiB: 12.0), ct);
        Assert.Empty(await external.CheckAsync(ct));
        Assert.False(external.ExternalGiB.ContainsKey(Gpu1));

        // Measured now (7.6): 4.4 external, twice → raised.
        store.Set(Record("starting", 4100, [Gpu1], measured: 7.6));
        await external.ObserveAsync(Sample(gpu1UsedGiB: 12.0), ct);
        await external.ObserveAsync(Sample(gpu1UsedGiB: 12.0), ct);
        Assert.Single(await external.CheckAsync(ct));
    }

    [Fact]
    public async Task ExternalUse_AReadyInstanceWhoseStartWasNotMeasured_CountsItsBudget_SoTheGpuIsStillJudged()
    {
        // MN-219: a failed "measured at start" (one nvidia-smi timeout at create) left MeasuredGpuGiB null for good, so the
        // GPU stayed unjudged until the model restarted. A Ready instance without a measurement counts its budget.
        var unmeasured = Record("embedder", 4100, [Gpu1], measured: null) with { ReadyAt = T0.AddSeconds(4) };
        unmeasured = unmeasured with { Spec = unmeasured.Spec with { Labels = new Dictionary<string, string> { [ContainerLabels.BudgetGiB] = "8.9" } } };
        var external = External(new FakeRecordStore(unmeasured), (Gpu1, 2.0));
        var ct = TestContext.Current.CancellationToken;

        await external.ObserveAsync(Sample(gpu1UsedGiB: 12.0), ct);
        await external.ObserveAsync(Sample(gpu1UsedGiB: 12.0), ct);

        Assert.Equal(3.1, external.ExternalGiB[Gpu1], 9);
        Assert.Single(await external.CheckAsync(ct));
    }

    [Fact]
    public async Task ExternalUse_ARaisedReason_Clears_WhenItsGpuNoLongerHasAReserve()
    {
        // MN-219: a GPU that stopped being judged (the master's new reserves leave it out) kept a raised reason forever.
        var external = External(new FakeRecordStore(), (Gpu1, 2.0));
        var ct = TestContext.Current.CancellationToken;
        await external.ObserveAsync(Sample(gpu1UsedGiB: 5.0), ct);
        await external.ObserveAsync(Sample(gpu1UsedGiB: 5.0), ct);
        Assert.Single(await external.CheckAsync(ct));

        external.RememberReserves(new AdmissionRequest
        {
            BudgetGiB = 8.9,
            ReserveGiB = 8,
            GpuReservesGiB = new Dictionary<string, double> { [Gpu0] = 40.0 },
        });
        await external.ObserveAsync(Sample(gpu1UsedGiB: 5.0), ct);

        Assert.Empty(await external.CheckAsync(ct));
    }

    [Fact]
    public async Task ExternalUse_UnreadableRecordsOrMemory_KeepTheState()
    {
        var store = new FakeRecordStore();
        var external = External(store, (Gpu1, 2.0));
        var ct = TestContext.Current.CancellationToken;
        await external.ObserveAsync(Sample(gpu1UsedGiB: 5.0), ct);
        await external.ObserveAsync(Sample(gpu1UsedGiB: 5.0), ct);
        Assert.Single(await external.CheckAsync(ct));

        store.Fail = true;
        await external.ObserveAsync(Sample(gpu1UsedGiB: 0.5), ct);
        await external.ObserveAsync(Sample(gpu1UsedGiB: 0.5), ct);
        Assert.Single(await external.CheckAsync(ct));

        store.Fail = false;
        await external.ObserveAsync(Sample(gpu1UsedGiB: null), ct);
        await external.ObserveAsync(Sample(gpu1UsedGiB: null), ct);
        Assert.Single(await external.CheckAsync(ct));
    }

    [Fact]
    public async Task ExternalUse_GpuWithoutReserve_IsNotChecked_UntilTheMasterSendsOne()
    {
        // The display card (GPU 0) carries 10 GiB of desktop load; no reserve known → never Degraded for it.
        var external = External(new FakeRecordStore());
        var ct = TestContext.Current.CancellationToken;
        await external.ObserveAsync(Sample(gpu1UsedGiB: 1.0, gpu0UsedGiB: 10.0), ct);
        await external.ObserveAsync(Sample(gpu1UsedGiB: 1.0, gpu0UsedGiB: 10.0), ct);
        Assert.Empty(await external.CheckAsync(ct));

        // A launch carries the node's reserves: GPU 1 has its own (2.0), every other GPU the default (4.0).
        external.RememberReserves(new AdmissionRequest
        {
            BudgetGiB = 8.9,
            ReserveGiB = 8,
            GpuReserveGiB = 4.0,
            GpuReservesGiB = new Dictionary<string, double> { [Gpu1] = 2.0 },
        });
        await external.ObserveAsync(Sample(gpu1UsedGiB: 1.0, gpu0UsedGiB: 10.0), ct);
        await external.ObserveAsync(Sample(gpu1UsedGiB: 1.0, gpu0UsedGiB: 10.0), ct);

        var reason = Assert.Single(await external.CheckAsync(ct));
        Assert.StartsWith("GPU 0 (NVIDIA GeForce RTX 5070): 10.0 GiB used by programs other than mirbadllm's models, above its 4.0 GiB reserve", reason.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("GPU 1", reason.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalUse_ModelOnTwoGpus_IsSplitByItsTensorSplit()
    {
        var split = Record("chat", 4200, [Gpu0, Gpu1], measured: 10.0) with
        {
            Spec = Record("chat", 4200, [Gpu0, Gpu1], 10).Spec with { Argv = ["-m", "{models}/x.gguf", "--tensor-split", "0.429,0.571"] },
        };
        var even = Record("even", 4300, [Gpu0, Gpu1], measured: 4.0);

        var shares = ExternalGpuUseContributor.SharesPerGpu([split, even]);

        Assert.Equal(4.29 + 2.0, shares.MeasuredGiB[Gpu0], 9);
        Assert.Equal(5.71 + 2.0, shares.MeasuredGiB[Gpu1], 9);
        Assert.Empty(shares.Unmeasured);
        Assert.Null(ExternalGpuUseContributor.TensorSplit(["--tensor-split", "1,2,3"], 2));
        Assert.Null(ExternalGpuUseContributor.TensorSplit(["--tensor-split", "a,b"], 2));
    }

    // ------------------------------------------------------------------ the sampler's hook

    [Fact]
    public async Task Sampler_FeedsEverySampleToTheExternalUseCheck_AndSurvivesAFailingObserver()
    {
        // Real sampler + real nvidia-smi probe over a fake runner: GPU 1 at 12.6 GiB with one model measured at 7.6.
        var smi = new FakeNvidiaSmi(args => args == NvidiaSmiGpuTelemetryProbe.Arguments
            ? Ok(QueryGpuText((10375, (int)(12.6 * 1024))))
            : Failed());
        var gpuProbe = new NvidiaSmiGpuTelemetryProbe(smi, NullLogger<NvidiaSmiGpuTelemetryProbe>.Instance);
        var store = new FakeRecordStore(Record("qwen3-embedding-4b", 4100, [Gpu1], measured: 7.6));
        var external = External(store, (Gpu1, 2.0));
        var throwing = new ThrowingObserver();
        var options = Options.Create(new TelemetryOptions());
        using var http = new TelemetryHttpClient(new NoHttp());
        var sampler = new TelemetrySampler(
            new TelemetryRing("boot-w", options),
            new FixedMemory(),
            gpuProbe,
            [],
            http,
            options,
            new FixedTime(T0),
            NullLogger<TelemetrySampler>.Instance,
            gpuProcesses: new NvidiaSmiGpuProcessProbe(smi, store, NullLogger<NvidiaSmiGpuProcessProbe>.Instance),
            observers: [throwing, external]);
        var ct = TestContext.Current.CancellationToken;

        var first = await sampler.SampleOnceAsync(ct);
        Assert.NotNull(first);
        Assert.Empty(await external.CheckAsync(ct));
        Assert.Null(first.GpuProcesses); // the compute-apps query fails in this fake: unavailable, not empty

        var second = await sampler.SampleOnceAsync(ct);
        Assert.Equal(2, second!.Seq);
        Assert.Equal(2, throwing.Seen);
        Assert.Equal(HealthReasonCodes.GpuExternalUseHigh, Assert.Single(await external.CheckAsync(ct)).Code);
    }

    // ------------------------------------------------------------------ helpers

    private static NvidiaSmiGpuStartMeter Meter(INvidiaSmiRunner smi) =>
        new(new NvidiaSmiGpuTelemetryProbe(smi, NullLogger<NvidiaSmiGpuTelemetryProbe>.Instance), new FixedTime(T0));

    private static ExternalGpuUseContributor External(IProcessRecordStore store, params (string Uuid, double Reserve)[] reserves) =>
        new(store, new ExternalGpuUseOptions { GpuReservesGiB = reserves.ToDictionary(r => r.Uuid, r => r.Reserve) },
            NullLogger<ExternalGpuUseContributor>.Instance);

    private static NodeSample Sample(double? gpu1UsedGiB, double? gpu0UsedGiB = 10.0) => new()
    {
        Seq = 1,
        At = T0,
        Memory = new NodeMemorySnapshot { Model = MemoryModel.Discrete, HostTotalGiB = 127, HostAvailableGiB = 90, Gpus = [] },
        Gpus =
        [
            new GpuSample { Index = 0, Uuid = Gpu0, Name = "NVIDIA GeForce RTX 5070", MemoryUsedGiB = gpu0UsedGiB, MemoryTotalGiB = 11.94 },
            new GpuSample { Index = 1, Uuid = Gpu1, Name = "NVIDIA GeForce RTX 5060 Ti", MemoryUsedGiB = gpu1UsedGiB, MemoryTotalGiB = 15.93 },
        ],
    };

    private static ProcessRecord Record(string modelId, int pid, IReadOnlyList<string> gpus, double? measured) => new()
    {
        ModelId = modelId,
        OperationId = "op-" + modelId,
        Spec = new LaunchSpec { Kind = LaunchDelivery.Process, Runtime = RuntimeKind.LlamaCpp, Executable = "llama-server", ExecutableVersion = "b11476" },
        Process = new ProcessIdentity(pid, T0, @"C:\Program Files\mirbadllm-agent\runtimes\llama.cpp\b11476\llama-server.exe"),
        ExecutableName = "llama-server",
        ExecutableVersion = "b11476",
        Port = 8102,
        ArgvDigest = "0123456789abcdef",
        ProtectedApiKey = "protected",
        JobName = "mirbadllm-ss-" + modelId,
        LogPath = @"C:\ProgramData\mirbadllm-agent\state\logs\ss-" + modelId + ".log",
        Gpus = [.. gpus.Select((u, i) => new GpuRef(u == Gpu0 ? 0 : 1, u))],
        StartedAt = T0,
        ReadyAt = measured is null ? null : T0.AddSeconds(4),
        MeasuredGpuGiB = measured,
    };

    /// <summary><c>--query-gpu</c> output of the two cards with the given <c>memory.used</c> MiB (GPU 0, GPU 1).</summary>
    private static NvidiaSmiResult QueryGpu((int Gpu0Used, int Gpu1Used) used) => Ok(QueryGpuText(used));

    private static string QueryGpuText((int Gpu0Used, int Gpu1Used) used) => string.Create(CultureInfo.InvariantCulture,
        $"0, {Gpu0}, 0, 2.17, 33, 210, {used.Gpu0Used}, 12227, {12227 - used.Gpu0Used}, NVIDIA GeForce RTX 5070\r\n" +
        $"1, {Gpu1}, 0, 6.93, 33, 225, {used.Gpu1Used}, 16311, {16311 - used.Gpu1Used}, NVIDIA GeForce RTX 5060 Ti\r\n");

    private static NvidiaSmiResult Ok(string output) => new(true, 0, output, "");

    private static NvidiaSmiResult Failed() => new(true, 9, "", "NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver.");

    private sealed class FakeNvidiaSmi(Func<string, NvidiaSmiResult> answer) : INvidiaSmiRunner
    {
        public List<string> Calls { get; } = [];

        public Task<NvidiaSmiResult> RunAsync(string arguments, CancellationToken ct = default)
        {
            lock (Calls)
            {
                Calls.Add(arguments);
            }

            return Task.FromResult(answer(arguments));
        }
    }

    /// <summary>Answers each run with the next result (the last one repeats).</summary>
    private sealed class SteppingNvidiaSmi(params NvidiaSmiResult[] results) : INvidiaSmiRunner
    {
        private int _next;

        public List<string> Calls { get; } = [];

        public Task<NvidiaSmiResult> RunAsync(string arguments, CancellationToken ct = default)
        {
            Calls.Add(arguments);
            var result = results[Math.Min(_next, results.Length - 1)];
            _next++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeRecordStore(params ProcessRecord[] initial) : IProcessRecordStore
    {
        private readonly Dictionary<string, ProcessRecord> _records = initial.ToDictionary(r => r.ModelId, StringComparer.Ordinal);

        public bool Fail { get; set; }

        public void Set(ProcessRecord record) => _records[record.ModelId] = record;

        public Task<IReadOnlyList<ProcessRecord>> ListAsync(CancellationToken ct = default) =>
            Fail ? throw new IOException("processes.json is torn.") : Task.FromResult<IReadOnlyList<ProcessRecord>>([.. _records.Values]);

        public Task<ProcessRecord?> GetAsync(string modelId, CancellationToken ct = default) => Task.FromResult(_records.GetValueOrDefault(modelId));

        public Task UpsertAsync(ProcessRecord record, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> RemoveAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PendingProcessStart>> ListPendingAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PendingProcessStart>>([]);

        public Task AddPendingAsync(PendingProcessStart pending, CancellationToken ct = default) => throw new NotSupportedException();

        public Task CommitPendingAsync(ProcessRecord record, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> RemovePendingAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingObserver : ITelemetrySampleObserver
    {
        public int Seen { get; private set; }

        public Task ObserveAsync(NodeSample sample, CancellationToken ct = default)
        {
            Seen++;
            throw new InvalidOperationException("observer bug");
        }
    }

    private sealed class FixedMemory : IMemoryProbe
    {
        public Task<MemorySnapshot> GetAsync(CancellationToken ct = default) => Task.FromResult(new MemorySnapshot(127, 90));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NoHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
