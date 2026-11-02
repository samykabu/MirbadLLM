using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Api;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Benchmarking;

#pragma warning disable CA1707, CA2000 // underscore test names; HttpResponseMessage ownership passes to HttpClient in fakes.

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// MN-018: the agent telemetry ring (boot-aware cursor, gap flag, back-fill after a partition, honest gap after
/// downtime), the filtered <c>/metrics</c> text (the master's KPIs are unchanged by the filter), the sampler's failure
/// isolation, GPU probes, contributors, <c>/gpu/idle</c> and the two endpoints. All with hand-written fakes.
/// </summary>
public sealed class TelemetryTests
{
    private const string Boot = "boot-a";
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ ring

    [Fact]
    public void Append_AssignsPerBootSeqFromOne_AndTracksLatest()
    {
        var ring = Ring();
        Assert.Null(ring.Latest);

        var first = ring.Append(Sample(T0, seq: 99));
        var second = ring.Append(Sample(T0.AddSeconds(5)));

        Assert.Equal(1, first.Seq);
        Assert.Equal(2, second.Seq);
        Assert.Equal(Boot, ring.BootId);
        Assert.Same(second, ring.Latest);
    }

    [Fact]
    public void Read_NoCursor_ReturnsOldestFirstWithoutGap()
    {
        var ring = Filled(3);

        var page = ring.Read(null, null, 100);

        Assert.Equal(Boot, page.BootId);
        Assert.False(page.Gap);
        Assert.Equal([1L, 2L, 3L], page.Samples.Select(s => s.Seq));
    }

    [Fact]
    public void Read_SameBoot_ReturnsOnlySamplesAfterCursor()
    {
        var ring = Filled(5);

        var page = ring.Read(Boot, 3, 100);

        Assert.False(page.Gap);
        Assert.Equal([4L, 5L], page.Samples.Select(s => s.Seq));
    }

    [Fact]
    public void Read_SameBootUpToDate_ReturnsNothingWithoutGap()
    {
        var ring = Filled(2);

        var page = ring.Read(Boot, 2, 100);

        Assert.False(page.Gap);
        Assert.Empty(page.Samples);
    }

    [Fact]
    public void Read_SameBootWithoutSeq_ReturnsFromOldest()
    {
        var ring = Filled(2);

        var page = ring.Read(Boot, null, 100);

        Assert.False(page.Gap);
        Assert.Equal(2, page.Samples.Count);
    }

    [Fact]
    public void Read_OtherBoot_ReturnsOldestOfThisBootWithGap()
    {
        // The agent restarted: the master's cursor names the previous boot. Nothing was sampled while it was down, so
        // the page starts at this boot's first sample and flags a gap (no interpolation, design §8.1).
        var ring = Filled(3);

        var page = ring.Read("boot-before-restart", 41, 100);

        Assert.True(page.Gap);
        Assert.Equal(Boot, page.BootId);
        Assert.Equal([1L, 2L, 3L], page.Samples.Select(s => s.Seq));
    }

    [Fact]
    public void Read_OtherBootAndEmptyRing_FlagsGapAndNamesTheNewBoot()
    {
        var page = Ring().Read("old", 7, 100);

        Assert.True(page.Gap);
        Assert.Equal(Boot, page.BootId);
        Assert.Empty(page.Samples);
    }

    [Fact]
    public void Read_CursorEvictedByWindow_ReturnsOldestKeptWithGap()
    {
        // 5-second samples for 20 minutes; the ring keeps 15. A master whose cursor is older than that lost samples.
        var ring = Ring();
        for (int i = 0; i < 240; i++)
        {
            ring.Append(Sample(T0.AddSeconds(5 * i)));
        }

        var page = ring.Read(Boot, 10, 1_000);

        Assert.True(page.Gap);
        var newest = page.Samples[^1];
        Assert.Equal(240, newest.Seq);
        Assert.True(newest.At - page.Samples[0].At <= Protocol.TelemetryRingWindow);
        Assert.Equal(181, page.Samples.Count); // 15 min at 5 s, both ends inclusive
        Assert.Equal(60, page.Samples[0].Seq);
    }

    [Fact]
    public void Read_CursorJustBeforeOldestKept_IsNotAGap()
    {
        var ring = Ring();
        for (int i = 0; i < 240; i++)
        {
            ring.Append(Sample(T0.AddSeconds(5 * i)));
        }

        long oldest = ring.Read(null, null, 1).Samples[0].Seq;

        var page = ring.Read(Boot, oldest - 1, 1_000);
        Assert.False(page.Gap);
        Assert.Equal(oldest, page.Samples[0].Seq);

        var evicted = ring.Read(Boot, oldest - 2, 1_000);
        Assert.True(evicted.Gap);
    }

    [Fact]
    public void Read_AfterPartitionShorterThanWindow_BackFillsEverySampleWithoutGap()
    {
        // Network partition, agent alive: the master pulled up to seq 12, then could not reach the agent for 10 minutes.
        var ring = Ring();
        for (int i = 0; i < 12; i++)
        {
            ring.Append(Sample(T0.AddSeconds(5 * i)));
        }

        var beforePartition = ring.Read(null, null, 1_000);
        long cursor = beforePartition.Samples[^1].Seq;
        for (int i = 12; i < 12 + 120; i++)
        {
            ring.Append(Sample(T0.AddSeconds(5 * i)));
        }

        var backFill = new List<NodeSample>();
        TelemetryPage page;
        do
        {
            page = ring.Read(Boot, cursor, 50);
            Assert.False(page.Gap);
            backFill.AddRange(page.Samples);
            if (page.Samples.Count > 0)
            {
                cursor = page.Samples[^1].Seq;
            }
        }
        while (page.Samples.Count > 0);

        Assert.Equal(Enumerable.Range(13, 120).Select(i => (long)i), backFill.Select(s => s.Seq));
        Assert.True(backFill.Zip(backFill.Skip(1)).All(p => p.First.At < p.Second.At)); // time order
    }

    [Fact]
    public void Read_HonoursMax()
    {
        var ring = Filled(10);

        var page = ring.Read(Boot, 2, 3);

        Assert.Equal([3L, 4L, 5L], page.Samples.Select(s => s.Seq));
        Assert.False(page.Gap);
    }

    [Fact]
    public void Append_BeyondCapacity_EvictsOldest()
    {
        var ring = new TelemetryRing(Boot, Options.Create(new TelemetryOptions { RingCapacity = 4 }));
        for (int i = 0; i < 6; i++)
        {
            ring.Append(Sample(T0.AddSeconds(i)));
        }

        Assert.Equal(4, ring.Count);
        var page = ring.Read(Boot, 1, 100);
        Assert.True(page.Gap);
        Assert.Equal([3L, 4L, 5L, 6L], page.Samples.Select(s => s.Seq));
    }

    [Fact]
    public async Task Append_Concurrent_AssignsUniqueContiguousSeqs()
    {
        var ring = new TelemetryRing(Boot, Options.Create(new TelemetryOptions { RingCapacity = 10_000 }));
        var stored = new ConcurrentBag<long>();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 250; i++)
            {
                stored.Add(ring.Append(Sample(T0)).Seq);
            }
        }, TestContext.Current.CancellationToken)));

        Assert.Equal(Enumerable.Range(1, 2_000).Select(i => (long)i), stored.Order());
        Assert.Equal(Enumerable.Range(1, 2_000).Select(i => (long)i), ring.Read(null, null, 5_000).Samples.Select(s => s.Seq));
    }

    // ------------------------------------------------------------------ metric family filter

    [Fact]
    public void Filter_KeepsOnlyUsedFamilies_WithTheirHelpAndTypeLines()
    {
        string text = """
            # HELP python_gc_objects_collected_total Objects collected during gc
            # TYPE python_gc_objects_collected_total counter
            python_gc_objects_collected_total{generation="0"} 1234.0
            # HELP vllm:prompt_tokens_total Number of prefill tokens processed.
            # TYPE vllm:prompt_tokens counter
            vllm:prompt_tokens_total{engine="0",model_name="m"} 42.0
            vllm:prompt_tokens_created{engine="0",model_name="m"} 1.7e9
            # TYPE vllm:num_requests_waiting gauge
            vllm:num_requests_waiting{engine="0",model_name="m"} 1.0
            vllm:num_requests_waiting_by_reason{engine="0",reason="capacity"} 1.0
            # TYPE vllm:time_to_first_token_seconds histogram
            vllm:time_to_first_token_seconds_bucket{engine="0",le="0.1",model_name="m"} 3.0
            vllm:time_to_first_token_seconds_bucket{engine="0",le="+Inf",model_name="m"} 4.0
            vllm:time_to_first_token_seconds_count{engine="0",model_name="m"} 4.0
            vllm:time_to_first_token_seconds_sum{engine="0",model_name="m"} 0.9
            vllm:time_to_first_token_seconds_created{engine="0",model_name="m"} 1.7e9
            vllm:cache_config_info{block_size="16",cache_dtype="auto"} 1.0
            process_resident_memory_bytes 1.2e9

            llamacpp:requests_processing 2
            llamacpp:n_busy_slots_per_decode 1
            """;

        string filtered = MetricFamilyFilter.Filter(text);
        string[] lines = filtered.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(
        [
            "# HELP vllm:prompt_tokens_total Number of prefill tokens processed.",
            "# TYPE vllm:prompt_tokens counter",
            "vllm:prompt_tokens_total{engine=\"0\",model_name=\"m\"} 42.0",
            "# TYPE vllm:num_requests_waiting gauge",
            "vllm:num_requests_waiting{engine=\"0\",model_name=\"m\"} 1.0",
            "# TYPE vllm:time_to_first_token_seconds histogram",
            "vllm:time_to_first_token_seconds_bucket{engine=\"0\",le=\"0.1\",model_name=\"m\"} 3.0",
            "vllm:time_to_first_token_seconds_bucket{engine=\"0\",le=\"+Inf\",model_name=\"m\"} 4.0",
            "vllm:time_to_first_token_seconds_count{engine=\"0\",model_name=\"m\"} 4.0",
            "vllm:time_to_first_token_seconds_sum{engine=\"0\",model_name=\"m\"} 0.9",
            "llamacpp:requests_processing 2",
        ], lines);
        Assert.EndsWith("\n", filtered, StringComparison.Ordinal);
    }

    [Fact]
    public void Filter_KeepsEveryNameTheMasterReads()
    {
        // Drift guard: every metric ModelKpiCalculator and BenchmarkSuite read must survive the agent's filter, or the
        // master's charts would silently lose a series.
        var calculatorNames = typeof(ModelKpiCalculator)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && !f.Name.EndsWith("Label", StringComparison.Ordinal))
            .Select(f => (f.Name, Value: (string)f.GetRawConstantValue()!))
            .ToList();
        Assert.NotEmpty(calculatorNames);

        foreach (var (field, name) in calculatorNames)
        {
            if (field.EndsWith("Histogram", StringComparison.Ordinal))
            {
                Assert.Contains(name, MetricFamilyFilter.Histograms);
                Assert.True(MetricFamilyFilter.KeepsSample(name + "_bucket"), name);
                Assert.True(MetricFamilyFilter.KeepsSample(name + "_sum"), name);
                Assert.True(MetricFamilyFilter.KeepsSample(name + "_count"), name);
            }
            else
            {
                Assert.True(MetricFamilyFilter.KeepsSample(name), $"{field} = {name}");
            }
        }

        var benchmarkNames = (string[])typeof(BenchmarkSuite)
            .GetField("PrefixCacheHitMetrics", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        Assert.NotEmpty(benchmarkNames);
        Assert.All(benchmarkNames, name => Assert.True(MetricFamilyFilter.KeepsSample(name), name));
    }

    [Fact]
    public void Filter_RealDgxScrape_KeepsEveryUsedSampleUnchanged()
    {
        string raw = File.ReadAllText(DgxFixturePath());
        var all = PrometheusText.Parse(raw);
        var kept = PrometheusText.Parse(MetricFamilyFilter.Filter(raw));

        var expected = all.Where(s => MetricFamilyFilter.KeepsSample(s.Name)).ToList();
        Assert.Equal(expected.Count, kept.Count);
        Assert.DoesNotContain(kept, s => s.Name.EndsWith("_created", StringComparison.Ordinal));
        Assert.DoesNotContain(kept, s => s.Name == "vllm:num_requests_waiting_by_reason");
        Assert.True(PrometheusText.HasMetric(kept, "vllm:kv_cache_usage_perc"));
        Assert.Equal(
            PrometheusText.SumByLabel(all, "vllm:request_success_total", "finished_reason").Keys.Order(),
            PrometheusText.SumByLabel(kept, "vllm:request_success_total", "finished_reason").Keys.Order());
    }

    [Fact]
    public void Filter_MasterKpisFromFilteredText_AreIdenticalToKpisFromRawText()
    {
        // The master keeps producing identical charts: ModelKpiCalculator over two consecutive filtered scrapes equals
        // the result over the raw scrapes (rates, latency percentiles, ratios, finished-by-reason).
        string previousRaw = SyntheticVllmScrape(1);
        string currentRaw = SyntheticVllmScrape(3);

        var fromRaw = ModelKpiCalculator.Compute(PrometheusText.Parse(currentRaw), PrometheusText.Parse(previousRaw), 5);
        var fromFiltered = ModelKpiCalculator.Compute(
            PrometheusText.Parse(MetricFamilyFilter.Filter(currentRaw)), PrometheusText.Parse(MetricFamilyFilter.Filter(previousRaw)), 5);

        Assert.True(fromRaw.Load.GenerationTokensPerSecond > 0);
        Assert.NotNull(fromRaw.TimeToFirstToken);
        Assert.NotNull(fromRaw.SpecDecodeAcceptanceRate);
        Assert.NotEmpty(fromRaw.FinishedByReasonPerMinute);
        Assert.Equal(JsonSerializer.Serialize(fromRaw), JsonSerializer.Serialize(fromFiltered));
        Assert.True(MetricFamilyFilter.Filter(currentRaw).Length < currentRaw.Length);
    }

    // ------------------------------------------------------------------ GPU probes and contributors

    [Fact]
    public void ParseGpuTelemetry_Gb10_HasNoMemoryFields()
    {
        var gpus = NvidiaSmiGpuTelemetryProbe.Parse("0, GPU-5967aa, 96, 41.25, 55, 2405, [N/A], [N/A]\n");

        var gpu = Assert.Single(gpus!);
        Assert.Equal(0, gpu.Index);
        Assert.Equal("GPU-5967aa", gpu.Uuid);
        Assert.Equal(96, gpu.UtilizationPercent);
        Assert.Equal(41.25, gpu.PowerWatts);
        Assert.Equal(55, gpu.TemperatureC);
        Assert.Equal(2405, gpu.SmClockMHz);
        Assert.Null(gpu.MemoryUsedGiB);
        Assert.Null(gpu.MemoryTotalGiB);
        Assert.Equal(new GpuSnapshot(96, 41.25, 55, 2405), gpu.ToSnapshot());
    }

    [Fact]
    public void ParseGpuTelemetry_Discrete_OrdersByIndexAndConvertsMiB()
    {
        var gpus = NvidiaSmiGpuTelemetryProbe.Parse("1, GPU-b, 5, N/A, 40, 210, 1024, 24576\n0, GPU-a, 70, 300.5, 66, 1800, 20480, 24576\n")!;

        Assert.Equal([0, 1], gpus.Select(g => g.Index));
        Assert.Equal(20, gpus[0].MemoryUsedGiB);
        Assert.Equal(24, gpus[0].MemoryTotalGiB);
        Assert.Null(gpus[1].PowerWatts);
    }

    [Fact]
    public void ParseGpuTelemetry_UnexpectedOutput_IsNull()
    {
        Assert.Null(NvidiaSmiGpuTelemetryProbe.Parse("NVIDIA-SMI has failed because it couldn't communicate with the driver"));
        Assert.Empty(NvidiaSmiGpuTelemetryProbe.Parse("")!);
    }

    [Fact]
    public async Task GpuTelemetryProbe_MapsNotInstalledFailedAndOk_AndHealthFollowsLastRead()
    {
        var runner = new FakeNvidiaSmi(_ => NvidiaSmiResult.NotFound);
        var probe = new NvidiaSmiGpuTelemetryProbe(runner, NullLogger<NvidiaSmiGpuTelemetryProbe>.Instance);
        var health = new GpuHealthContributor(probe);
        var ct = TestContext.Current.CancellationToken;

        // No read yet: the contributor reads once. No nvidia-smi = CPU node = healthy.
        Assert.Empty(await health.CheckAsync(ct));
        Assert.Equal(GpuProbeStatus.NotInstalled, probe.Last!.Status);

        runner.Answer = _ => new NvidiaSmiResult(true, 9, "", "Failed to initialize NVML: Driver/library version mismatch");
        var failed = await probe.ReadAsync(ct);
        Assert.Equal(GpuProbeStatus.Failed, failed.Status);
        Assert.Contains("version mismatch", failed.Error, StringComparison.Ordinal);
        var reason = Assert.Single(await health.CheckAsync(ct));
        Assert.Equal(HealthReasonCodes.GpuProbeFailing, reason.Code);

        runner.Answer = _ => new NvidiaSmiResult(true, 0, "garbage", "");
        Assert.Equal(GpuProbeStatus.Failed, (await probe.ReadAsync(ct)).Status);

        runner.Answer = _ => new NvidiaSmiResult(true, 0, "0, GPU-a, 1, 2, 3, 4, [N/A], [N/A]", "");
        var ok = await probe.ReadAsync(ct);
        Assert.Equal(GpuProbeStatus.Ok, ok.Status);
        Assert.Single(ok.Gpus);
        Assert.Empty(await health.CheckAsync(ct));
        Assert.Equal(4, runner.Calls.Count); // health reused the last read instead of running nvidia-smi again
    }

    [Fact]
    public async Task Inventory_ParsesGb10_AndFallsBackWithoutComputeCap_AndCaches()
    {
        var runner = new FakeNvidiaSmi(args => args == NvidiaSmiGpuInventory.Arguments
            ? new NvidiaSmiResult(true, 2, "", "Field \"compute_cap\" is not a valid field to query.")
            : new NvidiaSmiResult(true, 0, "0, GPU-5967aa, NVIDIA GB10, [N/A], 580.95.05\n", ""));
        var inventory = new NvidiaSmiGpuInventory(runner);
        var ct = TestContext.Current.CancellationToken;

        var gpus = await inventory.GetAsync(ct);
        var again = await inventory.GetAsync(ct);

        var gpu = Assert.Single(gpus);
        Assert.Equal("GPU-5967aa", gpu.Uuid);
        Assert.Equal("NVIDIA GB10", gpu.Name);
        Assert.Null(gpu.MemoryTotalGiB);
        Assert.True(gpu.SharesHostMemory);
        Assert.Null(gpu.ComputeCapability);
        Assert.Equal("580.95.05", gpu.Driver);
        Assert.Same(gpus, again);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(MemoryModel.Unified, NvidiaSmiGpuInventory.MemoryModelOf(gpus));
    }

    [Fact]
    public async Task CapabilityContributor_AddsGpusFeatureAndDiscreteModel_WithoutRemovingOthers()
    {
        var runner = new FakeNvidiaSmi(_ => new NvidiaSmiResult(true, 0,
            "0, GPU-a, NVIDIA GeForce RTX 4090, 24564, 8.9, 581.0\n1, GPU-b, NVIDIA GeForce RTX 3090, 24576, 8.6, 581.0\n", ""));
        var contributor = new TelemetryCapabilityContributor(new NvidiaSmiGpuInventory(runner));
        var current = BaseCapabilities() with { Features = [AgentFeatures.RuntimeVllmContainer] };

        var result = await contributor.ContributeAsync(current, TestContext.Current.CancellationToken);
        var twice = await contributor.ContributeAsync(result, TestContext.Current.CancellationToken);

        Assert.Equal([AgentFeatures.RuntimeVllmContainer, AgentFeatures.TelemetryV1], twice.Features);
        Assert.Equal(["GPU-a", "GPU-b"], result.Gpus.Select(g => g.Uuid));
        Assert.Equal("8.9", result.Gpus[0].ComputeCapability);
        Assert.Equal(24564 / 1024.0, result.Gpus[0].MemoryTotalGiB);
        Assert.Equal(MemoryModel.Discrete, result.Memory.Model);
        Assert.Equal(current.Memory.TotalGiB, result.Memory.TotalGiB);
        Assert.Equal(current.AgentVersion, result.AgentVersion);
    }

    [Fact]
    public async Task CapabilityContributor_NoNvidiaSmi_OnlyAddsTheFeature()
    {
        var contributor = new TelemetryCapabilityContributor(new NvidiaSmiGpuInventory(new FakeNvidiaSmi(_ => NvidiaSmiResult.NotFound)));
        var current = BaseCapabilities();

        var result = await contributor.ContributeAsync(current, TestContext.Current.CancellationToken);

        Assert.Equal([AgentFeatures.TelemetryV1], result.Features);
        Assert.Empty(result.Gpus);
        Assert.Equal(MemoryModel.Unified, result.Memory.Model);
    }

    [Fact]
    public async Task HostdGpuProcessProbe_NullWhenMissingSlowOrFailing()
    {
        var options = Options.Create(new TelemetryOptions { GpuProcessTimeout = TimeSpan.FromMilliseconds(50) });
        var ct = TestContext.Current.CancellationToken;
        GpuProcess[] processes = [new(4242, "python3", 60_000, "abc123abc123", null)];

        Assert.Null(await new HostdGpuProcessProbe(null, options).GetAsync(ct));
        Assert.Null(await new HostdGpuProcessProbe(new FakeHostd { Installed = false, Processes = processes }, options).GetAsync(ct));
        Assert.Same(processes, await new HostdGpuProcessProbe(new FakeHostd { Processes = processes }, options).GetAsync(ct));
        Assert.Null(await new HostdGpuProcessProbe(new FakeHostd { Delay = TimeSpan.FromSeconds(30) }, options).GetAsync(ct));
        Assert.Null(await new HostdGpuProcessProbe(new FakeHostd { Throw = true }, options).GetAsync(ct));
    }

    // ------------------------------------------------------------------ sampler

    [Fact]
    public async Task Sampler_ScrapesOnlyReadyModels_FiltersText_AndAppendsOneSample()
    {
        var driver = new FakeDriver()
            .Add("ready", ModelState.Ready, 8101)
            .Add("starting", ModelState.Starting, 8102)
            .Add("stopped", ModelState.Stopped, null);
        var http = new FakeHttp(_ => Ok(SyntheticVllmScrape(1)));
        var processes = new GpuProcess[] { new(1, "vllm", 1024, "c0ffee", null) };
        var (sampler, ring) = Sampler(driver, http, gpuProcesses: new FakeGpuProcesses(processes));

        var sample = await sampler.SampleOnceAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(sample);
        Assert.Equal(1, sample!.Seq);
        Assert.Equal(T0, sample.At);
        Assert.Same(sample, ring.Latest);
        Assert.Equal(["http://127.0.0.1:8101/metrics"], http.Requests);
        var scrape = Assert.Single(sample.Models);
        Assert.Equal("ready", scrape.ModelId);
        Assert.Null(scrape.Error);
        Assert.Equal(MetricFamilyFilter.Filter(SyntheticVllmScrape(1)), scrape.Text);
        Assert.DoesNotContain("python_gc", scrape.Text, StringComparison.Ordinal);
        Assert.Equal(121, sample.Memory.HostTotalGiB);
        Assert.Equal(60, sample.Memory.HostAvailableGiB);
        Assert.Equal(MemoryModel.Unified, sample.Memory.Model);
        Assert.Equal("GPU-5967aa", Assert.Single(sample.Memory.Gpus).Uuid);
        Assert.Null(sample.Memory.Gpus[0].TotalGiB);
        Assert.Equal(96, Assert.Single(sample.Gpus).UtilizationPercent);
        Assert.Same(processes, sample.GpuProcesses);
    }

    [Fact]
    public async Task Sampler_OneFailingAndOneSlowScrape_DoNotAffectTheOthers()
    {
        var driver = new FakeDriver()
            .Add("healthy", ModelState.Ready, 8101)
            .Add("broken", ModelState.Ready, 8102)
            .Add("slow", ModelState.Ready, 8103)
            .Add("refused", ModelState.Ready, 8104);
        var http = new FakeHttp(async (uri, ct) =>
        {
            switch (uri.Port)
            {
                case 8102:
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                case 8103:
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    return Ok("never");
                case 8104:
                    throw new HttpRequestException("Connection refused");
                default:
                    return Ok("vllm:num_requests_running 2\n");
            }
        });
        var (sampler, _) = Sampler(driver, http, scrapeTimeout: TimeSpan.FromMilliseconds(200));

        var sample = await sampler.SampleOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["broken", "healthy", "refused", "slow"], sample!.Models.Select(m => m.ModelId));
        var byId = sample.Models.ToDictionary(m => m.ModelId);
        Assert.Equal("vllm:num_requests_running 2\n", byId["healthy"].Text);
        Assert.Null(byId["healthy"].Error);
        Assert.Null(byId["broken"].Text);
        Assert.Contains("500", byId["broken"].Error, StringComparison.Ordinal);
        Assert.Contains("did not answer", byId["slow"].Error, StringComparison.Ordinal);
        Assert.Contains("Connection refused", byId["refused"].Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sampler_MemoryReadFails_SkipsTheSampleInsteadOfInventingOne()
    {
        var (sampler, ring) = Sampler(new FakeDriver(), new FakeHttp(_ => Ok("")), memory: new ThrowingMemoryProbe());

        var sample = await sampler.SampleOnceAsync(TestContext.Current.CancellationToken);

        Assert.Null(sample);
        Assert.Null(ring.Latest);
    }

    [Fact]
    public async Task Sampler_GpuAndProcessAndRuntimeFailures_StillAppendHostMemory()
    {
        var driver = new FakeDriver { ListThrows = true };
        var gpu = new FakeNvidiaSmi(_ => new NvidiaSmiResult(true, 15, "", "GPU is lost"));
        var (sampler, ring) = Sampler(driver, new FakeHttp(_ => Ok("")), gpuRunner: gpu, gpuProcesses: new FakeGpuProcesses(null) { Throw = true });

        var sample = await sampler.SampleOnceAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(sample);
        Assert.Empty(sample!.Gpus);
        Assert.Null(sample.GpuProcesses);
        Assert.Empty(sample.Models);
        Assert.Equal(1, ring.Count);
    }

    [Fact]
    public async Task Sampler_ModelGoneBetweenListAndScrape_IsLeftOut()
    {
        var driver = new FakeDriver().Add("gone", ModelState.Ready, null);
        var http = new FakeHttp(_ => Ok("x 1\n"));
        var (sampler, _) = Sampler(driver, http);

        var sample = await sampler.SampleOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sample!.Models);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Sampler_DiscreteGpus_ReportDiscreteMemoryWithFreeVram()
    {
        var gpu = new FakeNvidiaSmi(_ => new NvidiaSmiResult(true, 0, "0, GPU-a, 3, 30, 40, 210, 4096, 24576\n", ""));
        var (sampler, _) = Sampler(new FakeDriver(), new FakeHttp(_ => Ok("")), gpuRunner: gpu);

        var sample = await sampler.SampleOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MemoryModel.Discrete, sample!.Memory.Model);
        var vram = Assert.Single(sample.Memory.Gpus);
        Assert.Equal(new GpuMemorySnapshot(0, "GPU-a", 24, 4, 20), vram);
    }

    [Fact]
    public async Task Sampler_ConsecutivePasses_ProduceTimeOrderedSamplesTheMasterCanDiff()
    {
        // Two passes 5 s apart: the master pulls both and computes KPIs exactly as the pre-P1 sampler did.
        var driver = new FakeDriver().Add("m", ModelState.Ready, 8101);
        int pass = 0;
        var http = new FakeHttp(_ => Ok(SyntheticVllmScrape(pass == 0 ? 1 : 3)));
        var time = new SteppingTime(T0);
        var (sampler, ring) = Sampler(driver, http, time: time);

        await sampler.SampleOnceAsync(TestContext.Current.CancellationToken);
        pass = 1;
        time.Now = T0.AddSeconds(5);
        await sampler.SampleOnceAsync(TestContext.Current.CancellationToken);

        var page = ring.Read(null, null, 10);
        Assert.Equal(2, page.Samples.Count);
        var previous = PrometheusText.Parse(page.Samples[0].Models[0].Text!);
        var current = PrometheusText.Parse(page.Samples[1].Models[0].Text!);
        double elapsed = (page.Samples[1].At - page.Samples[0].At).TotalSeconds;
        var kpis = ModelKpiCalculator.Compute(current, previous, elapsed);
        var expected = ModelKpiCalculator.Compute(PrometheusText.Parse(SyntheticVllmScrape(3)), PrometheusText.Parse(SyntheticVllmScrape(1)), 5);
        Assert.Equal(5, elapsed);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(kpis));
    }

    // ------------------------------------------------------------------ /gpu/idle

    [Fact]
    public async Task GpuIdle_AveragesReadingsPerGpu_SkipsFailedReads_AndFilters()
    {
        int call = 0;
        var runner = new FakeNvidiaSmi(_ => Interlocked.Increment(ref call) switch
        {
            2 => new NvidiaSmiResult(true, 9, "", "busy"),
            int n => new NvidiaSmiResult(true, 0, $"0, GPU-a, {n * 10}, 1, 1, 1, 1, 2\n1, GPU-b, 50, 1, 1, 1, 1, 2\n", ""),
        });
        var probe = new NvidiaSmiGpuTelemetryProbe(runner, NullLogger<NvidiaSmiGpuTelemetryProbe>.Instance);
        var idle = new GpuIdleSampler(probe, Options.Create(new TelemetryOptions { GpuIdleSampleInterval = TimeSpan.FromMilliseconds(100) }), TimeProvider.System);

        // 1 s at 100 ms = 10 reads; read 2 fails and adds nothing. GPU 0 reads 10, 30, 40 … 100; GPU 1 always 50.
        var all = await idle.SampleAsync(1, null, TestContext.Current.CancellationToken);
        double expected0 = Enumerable.Range(1, 10).Where(i => i != 2).Average(i => i * 10.0);
        Assert.Equal(10, call);
        Assert.Equal(9, all.Samples);
        Assert.Equal([0, 1], all.PerGpu.Select(g => g.Index));
        Assert.Equal("GPU-b", all.PerGpu[1].Uuid);
        Assert.Equal(50, all.PerGpu[1].AveragePercent);
        Assert.Equal(9, all.PerGpu[0].Samples);
        Assert.Equal(expected0, all.PerGpu[0].AveragePercent!.Value, precision: 9);
        Assert.Equal((expected0 + 50) / 2, all.AveragePercent!.Value, precision: 9);
        Assert.Equal(100, all.PerGpu[0].MaxPercent);
        Assert.Equal(50, all.PerGpu[1].MaxPercent);
        Assert.Equal(100, all.MaxPercent);

        call = 0;
        var only0 = await idle.SampleAsync(1, [0], TestContext.Current.CancellationToken);
        var gpu0 = Assert.Single(only0.PerGpu);
        Assert.Equal(expected0, gpu0.AveragePercent!.Value, precision: 9);
        Assert.Equal(expected0, only0.AveragePercent!.Value, precision: 9);
    }

    [Fact]
    public async Task GpuIdle_NoGpuTelemetry_ReportsNullAverage()
    {
        var probe = new NvidiaSmiGpuTelemetryProbe(new FakeNvidiaSmi(_ => NvidiaSmiResult.NotFound), NullLogger<NvidiaSmiGpuTelemetryProbe>.Instance);
        var idle = new GpuIdleSampler(probe, Options.Create(new TelemetryOptions { GpuIdleSampleInterval = TimeSpan.FromMilliseconds(250) }), TimeProvider.System);

        var result = await idle.SampleAsync(1, null, TestContext.Current.CancellationToken);

        Assert.Null(result.AveragePercent);
        Assert.Null(result.MaxPercent);
        Assert.Equal(0, result.Samples);
        Assert.Empty(result.PerGpu);
    }

    // ------------------------------------------------------------------ endpoints and DI

    [Fact]
    public async Task TelemetryEndpoint_ServesCursorPages_AndRejectsMalformedQueries()
    {
        var ct = TestContext.Current.CancellationToken;
        var ring = Filled(4);
        await using var app = await StartApiAsync(services => services.AddSingleton<ITelemetryRing>(ring), ct);
        var client = app.GetTestClient();

        var all = await client.GetFromJsonAsync<TelemetryPage>("/agent/v1/telemetry", AgentJson.Options, ct);
        Assert.Equal([1L, 2L, 3L, 4L], all!.Samples.Select(s => s.Seq));
        Assert.Equal(Boot, all.BootId);

        var after = await client.GetFromJsonAsync<TelemetryPage>($"/agent/v1/telemetry?bootId={Boot}&afterSeq=2&max=1", AgentJson.Options, ct);
        Assert.Equal([3L], after!.Samples.Select(s => s.Seq));
        Assert.False(after.Gap);

        var restarted = await client.GetFromJsonAsync<TelemetryPage>("/agent/v1/telemetry?bootId=old&afterSeq=900", AgentJson.Options, ct);
        Assert.True(restarted!.Gap);
        Assert.Equal(4, restarted.Samples.Count);

        foreach (string bad in new[] { "afterSeq=-1&bootId=b", "afterSeq=x&bootId=b", "afterSeq=3", "max=0", "max=abc", "bootId=a&bootId=b" })
        {
            using var response = await client.GetAsync($"/agent/v1/telemetry?{bad}", ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<AgentErrorResponse>(AgentJson.Options, ct);
            Assert.Equal(ErrorCodes.InvalidRequest, error!.Error.Code);
        }
    }

    [Fact]
    public async Task GpuIdleEndpoint_SamplesAndValidates()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new FakeNvidiaSmi(_ => new NvidiaSmiResult(true, 0, "0, GPU-a, 4, 1, 1, 1, [N/A], [N/A]\n1, GPU-b, 8, 1, 1, 1, [N/A], [N/A]\n", ""));
        await using var app = await StartApiAsync(services =>
        {
            services.Configure<TelemetryOptions>(o => o.GpuIdleSampleInterval = TimeSpan.FromMilliseconds(100));
            services.AddSingleton<IGpuTelemetryProbe>(new NvidiaSmiGpuTelemetryProbe(runner, NullLogger<NvidiaSmiGpuTelemetryProbe>.Instance));
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<GpuIdleSampler>();
        }, ct);
        var client = app.GetTestClient();

        var idle = await client.GetFromJsonAsync<GpuIdleResponse>("/agent/v1/gpu/idle?seconds=1&gpus=1", AgentJson.Options, ct);
        Assert.Equal(8, idle!.AveragePercent);
        Assert.Equal(10, idle.Samples);
        Assert.Equal("GPU-b", Assert.Single(idle.PerGpu).Uuid);

        foreach (string bad in new[] { "seconds=0", "seconds=61", "seconds=abc", "gpus=a", "gpus=-1", "seconds=1&seconds=2" })
        {
            using var response = await client.GetAsync($"/agent/v1/gpu/idle?{bad}", ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(ErrorCodes.InvalidRequest, await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AddAgentTelemetry_ComposesWithoutDriversOrHostd()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentTelemetry("boot-x", o => o.SampleInterval = TimeSpan.FromSeconds(2));
        services.AddSingleton<INvidiaSmiRunner>(new FakeNvidiaSmi(_ => NvidiaSmiResult.NotFound));
        services.AddSingleton<IMemoryProbe>(new FixedMemoryProbe());
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var ring = provider.GetRequiredService<ITelemetryRing>();
        Assert.Equal("boot-x", ring.BootId);
        Assert.Single(provider.GetServices<ICapabilityContributor>().OfType<TelemetryCapabilityContributor>());
        Assert.Single(provider.GetServices<IHealthContributor>().OfType<GpuHealthContributor>());
        Assert.IsType<HostdGpuProcessProbe>(provider.GetRequiredService<IGpuProcessProbe>());
        var sampler = provider.GetRequiredService<TelemetrySampler>();
        Assert.Contains(provider.GetServices<IHostedService>(), s => ReferenceEquals(s, sampler));
        Assert.Equal(TimeSpan.FromSeconds(2), provider.GetRequiredService<IOptions<TelemetryOptions>>().Value.SampleInterval);

        var sample = await sampler.SampleOnceAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(sample);
        Assert.Null(sample!.GpuProcesses);
        Assert.Same(sample, ring.Latest);
    }

    // ------------------------------------------------------------------ helpers

    private static TelemetryRing Ring() => new(Boot, Options.Create(new TelemetryOptions()));

    private static TelemetryRing Filled(int count)
    {
        var ring = Ring();
        for (int i = 0; i < count; i++)
        {
            ring.Append(Sample(T0.AddSeconds(5 * i)));
        }

        return ring;
    }

    private static NodeSample Sample(DateTimeOffset at, long seq = 0) => new()
    {
        Seq = seq,
        At = at,
        Memory = new NodeMemorySnapshot { Model = MemoryModel.Unified, HostTotalGiB = 121, HostAvailableGiB = 60 },
    };

    private static (TelemetrySampler Sampler, TelemetryRing Ring) Sampler(
        FakeDriver driver,
        FakeHttp http,
        TimeSpan? scrapeTimeout = null,
        IMemoryProbe? memory = null,
        FakeNvidiaSmi? gpuRunner = null,
        IGpuProcessProbe? gpuProcesses = null,
        TimeProvider? time = null)
    {
        var options = Options.Create(new TelemetryOptions { ScrapeTimeout = scrapeTimeout ?? TimeSpan.FromSeconds(3) });
        var ring = new TelemetryRing(Boot, options);
        var runner = gpuRunner ?? new FakeNvidiaSmi(_ => new NvidiaSmiResult(true, 0, "0, GPU-5967aa, 96, 41.2, 55, 2405, [N/A], [N/A]\n", ""));
        var sampler = new TelemetrySampler(
            ring,
            memory ?? new FixedMemoryProbe(),
            new NvidiaSmiGpuTelemetryProbe(runner, NullLogger<NvidiaSmiGpuTelemetryProbe>.Instance),
            [driver],
            new TelemetryHttpClient(http),
            options,
            time ?? new SteppingTime(T0),
            NullLogger<TelemetrySampler>.Instance,
            gpuProcesses);
        return (sampler, ring);
    }

    private static async Task<WebApplication> StartApiAsync(Action<IServiceCollection> configure, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddOptions<TelemetryOptions>();
        configure(builder.Services);
        var app = builder.Build();
        app.MapGroup(Protocol.BasePath).MapTelemetryEndpoints();
        await app.StartAsync(ct);
        return app;
    }

    private static NodeCapabilities BaseCapabilities() => new()
    {
        Protocol = new ProtocolRange(1, 1),
        AgentVersion = "1.0.0+test",
        Os = new NodeOsInfo("linux", "Ubuntu 24.04 LTS"),
        Arch = "arm64",
        Memory = new NodeMemoryInfo(MemoryModel.Unified, 121),
    };

    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    /// <summary>
    /// A vLLM-shaped exposition with HELP/TYPE lines, noise families and every family ModelKpiCalculator reads. Counters,
    /// histogram buckets, sums and counts scale with <paramref name="k"/> (cumulative buckets stay monotonic), so two
    /// scrapes with different k give non-zero rates and latency percentiles.
    /// </summary>
    private static string SyntheticVllmScrape(double k)
    {
        var text = new StringBuilder();
        void Line(string line) => text.Append(line).Append('\n');
        string V(double value) => value.ToString("0.0###", CultureInfo.InvariantCulture);
        const string Labels = "engine=\"0\",model_name=\"m\"";

        Line("# HELP python_gc_objects_collected_total Objects collected during gc");
        Line("# TYPE python_gc_objects_collected_total counter");
        Line($"python_gc_objects_collected_total{{generation=\"0\"}} {V(1000 * k)}");
        Line("process_resident_memory_bytes 1.2e+09");
        Line($"vllm:cache_config_info{{block_size=\"16\",cache_dtype=\"auto\",{Labels}}} 1.0");
        Line("# TYPE vllm:num_requests_running gauge");
        Line($"vllm:num_requests_running{{{Labels}}} {V(k)}");
        Line($"vllm:num_requests_waiting{{{Labels}}} {V(k - 1)}");
        Line($"vllm:num_requests_waiting_by_reason{{{Labels},reason=\"capacity\"}} 7.0");
        Line($"vllm:kv_cache_usage_perc{{{Labels}}} {V(0.1 * k)}");
        Line("# TYPE vllm:generation_tokens counter");
        Line($"vllm:generation_tokens_total{{{Labels}}} {V(500 * k)}");
        Line($"vllm:generation_tokens_created{{{Labels}}} 1.7e+09");
        Line($"vllm:prompt_tokens_total{{{Labels}}} {V(2000 * k)}");
        Line($"vllm:prefix_cache_hits_total{{{Labels}}} {V(300 * k)}");
        Line($"vllm:prefix_cache_queries_total{{{Labels}}} {V(900 * k)}");
        Line($"vllm:spec_decode_num_accepted_tokens_total{{{Labels}}} {V(70 * k)}");
        Line($"vllm:spec_decode_num_draft_tokens_total{{{Labels}}} {V(100 * k)}");
        Line($"vllm:num_preemptions_total{{{Labels}}} {V(k)}");
        Line($"vllm:tool_call_parser_invocations_total{{{Labels}}} {V(4 * k)}");
        foreach (string reason in new[] { "stop", "length", "abort" })
        {
            Line($"vllm:request_success_total{{{Labels},finished_reason=\"{reason}\"}} {V(10 * k + reason.Length)}");
        }

        string[] histograms =
        [
            "vllm:time_to_first_token_seconds", "vllm:inter_token_latency_seconds",
            "vllm:e2e_request_latency_seconds", "vllm:request_queue_time_seconds",
        ];
        double[] bounds = [0.01, 0.05, 0.1, 0.5, 1, 5];
        foreach (string h in histograms)
        {
            Line($"# HELP {h} Histogram.");
            Line($"# TYPE {h} histogram");
            for (int i = 0; i < bounds.Length; i++)
            {
                Line($"{h}_bucket{{{Labels},le=\"{V(bounds[i])}\"}} {V(k * (i + 1) * 2)}");
            }

            Line($"{h}_bucket{{{Labels},le=\"+Inf\"}} {V(k * 14)}");
            Line($"{h}_count{{{Labels}}} {V(k * 14)}");
            Line($"{h}_sum{{{Labels}}} {V(k * 3.5)}");
            Line($"{h}_created{{{Labels}}} 1.7e+09");
        }

        return text.ToString();
    }

    private static string DgxFixturePath([CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "Orchestration", "Fixtures", "vllm-ornith-9b-nvfp4.metrics.txt");

    // ------------------------------------------------------------------ fakes

    private sealed class FakeNvidiaSmi(Func<string, NvidiaSmiResult> answer) : INvidiaSmiRunner
    {
        public Func<string, NvidiaSmiResult> Answer { get; set; } = answer;

        public ConcurrentQueue<string> Calls { get; } = new();

        public Task<NvidiaSmiResult> RunAsync(string arguments, CancellationToken ct = default)
        {
            Calls.Enqueue(arguments);
            return Task.FromResult(Answer(arguments));
        }
    }

    private sealed class FixedMemoryProbe : IMemoryProbe
    {
        public Task<MemorySnapshot> GetAsync(CancellationToken ct = default) => Task.FromResult(new MemorySnapshot(121, 60));
    }

    private sealed class ThrowingMemoryProbe : IMemoryProbe
    {
        public Task<MemorySnapshot> GetAsync(CancellationToken ct = default) => throw new FormatException("Expected 'MemTotal:'");
    }

    private sealed class FakeGpuProcesses(IReadOnlyList<GpuProcess>? processes) : IGpuProcessProbe
    {
        public bool Throw { get; init; }

        public Task<IReadOnlyList<GpuProcess>?> GetAsync(CancellationToken ct = default) =>
            Throw ? throw new InvalidOperationException("hostd broke") : Task.FromResult(processes);
    }

    private sealed class SteppingTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeHttp : HttpMessageHandler
    {
        private readonly Func<Uri, CancellationToken, Task<HttpResponseMessage>> _answer;

        public FakeHttp(Func<Uri, HttpResponseMessage> answer) => _answer = (uri, _) => Task.FromResult(answer(uri));

        public FakeHttp(Func<Uri, CancellationToken, Task<HttpResponseMessage>> answer) => _answer = answer;

        public ConcurrentQueue<string> RequestQueue { get; } = new();

        public IReadOnlyList<string> Requests => [.. RequestQueue];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestQueue.Enqueue(request.RequestUri!.ToString());
            return _answer(request.RequestUri!, cancellationToken);
        }
    }

    private sealed class FakeDriver : IRuntimeDriver
    {
        private readonly List<RuntimeStatus> _instances = [];

        public bool ListThrows { get; init; }

        public LaunchDelivery Delivery => LaunchDelivery.Container;

        public FakeDriver Add(string modelId, ModelState state, int? port)
        {
            _instances.Add(new RuntimeStatus { ModelId = modelId, State = state, HostPort = port });
            return this;
        }

        public Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default) =>
            ListThrows ? throw new HttpRequestException("Docker is unreachable") : Task.FromResult<IReadOnlyList<RuntimeStatus>>(_instances);

        public Task<RuntimeStatus?> GetAsync(string modelId, CancellationToken ct = default) =>
            Task.FromResult(_instances.FirstOrDefault(i => i.ModelId == modelId));

        public Task<Uri?> GetUpstreamAsync(string modelId, CancellationToken ct = default) =>
            Task.FromResult(_instances.FirstOrDefault(i => i.ModelId == modelId) is { HostPort: { } port }
                ? new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/")
                : null);

        public Task<RuntimeLaunchResult> LaunchAsync(RuntimeLaunch launch, IRuntimeStageSink stages, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RuntimeStatus> StartCreatedAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task StopAsync(string modelId, IRuntimeStageSink stages, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LaunchConfig?> GetLaunchConfigAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

        public IAsyncEnumerable<string> StreamLogsAsync(string modelId, int tail, bool follow, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> IsFolderInUseAsync(string folder, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeHostd : IHostdClient
    {
        public bool Installed { get; init; } = true;

        public IReadOnlyList<GpuProcess>? Processes { get; init; }

        public TimeSpan Delay { get; init; }

        public bool Throw { get; init; }

        public bool IsInstalled => Installed;

        public async Task<IReadOnlyList<GpuProcess>?> GetGpuProcessesAsync(CancellationToken ct = default)
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, ct);
            }

            return Throw ? throw new HostServiceException("hostd socket refused") : Processes;
        }

        public Task<IReadOnlyList<HostUnit>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<HostUnitDiscovery> DiscoverAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<HostUnit> AddToWhitelistAsync(string unit, string scope, string? displayName, bool isProtected, string? confirm, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<HostUnit> UpdateWhitelistAsync(string name, string? displayName, bool? isProtected, string? confirm, CancellationToken ct = default) => throw new NotSupportedException();

        public Task RemoveFromWhitelistAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<HostUnit> RunAsync(string name, HostServiceAction action, string? confirm, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<HostUnit> SetAutostartAsync(string name, bool enabled, string? confirm, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<BackupMountInfo> GetBackupMountAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<BackupMountTestResult> TestBackupMountAsync(BackupMountSettings settings, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<BackupMountInfo> SetBackupMountAsync(BackupMountSettings settings, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<BackupMountInfo> RemoveBackupMountAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
