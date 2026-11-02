using System.Runtime.InteropServices;

namespace MirbadLLM.UnitTests.Multinode.Perf;

/// <summary>
/// MN-046: the performance harness. <see cref="SmokeRunMeasuresEveryScenario"/> runs in the normal suite with tiny
/// counts and checks that the harness really measured (counts, successes, ordered percentiles).
/// <see cref="FullRunWritesJsonAndMarkdownReport"/> is opt-in (<c>MIRBADLLM_PERF=1</c>, <c>Category=Perf</c>); see
/// <c>tools/perf-multinode/</c>.
/// <para>Not run in parallel with other tests: it measures timings and loads loopback, so it runs alone.</para>
/// </summary>
[Collection(nameof(PerfSerialGroup))]
public sealed class PerfHarnessTests
{
    public const string EnableVariable = "MIRBADLLM_PERF";
    public const string OutputVariable = "MIRBADLLM_PERF_OUT";
    public const string CommandVariable = "MIRBADLLM_PERF_COMMAND";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static bool PerfEnabled => Environment.GetEnvironmentVariable(EnableVariable) == "1";

    [Fact]
    public async Task SmokeRunMeasuresEveryScenario()
    {
        var options = PerfOptions.Smoke;
        await using var env = await PerfEnvironment.StartAsync(Ct);
        var report = await new PerfHarness(env, options).RunAsync("smoke", Ct);
        TestContext.Current.TestOutputHelper?.WriteLine($"setup {report.SetupSeconds} s ({string.Join(", ", env.SetupPhases)}), run {report.RunSeconds} s");

        AssertMeasured(report, options);

        // The Markdown is rendered from the JSON and carries the label and the measured numbers.
        string json = PerfReportWriter.ToJson(report);
        string markdown = PerfReportWriter.ToMarkdown(json, "smoke.json");
        Assert.Contains("loopback, fake model, not the DGX", markdown, StringComparison.Ordinal);
        Assert.Equal(report.NonStreaming.Proxy, PerfReportWriter.FromJson(json).NonStreaming.Proxy);
    }

    [Fact]
    public async Task SixtyFourMiBBodiesPassTheMasterWithAndWithoutContentLengthAndOneByteMoreIs413()
    {
        // MN-046 found the master refused /v1 bodies above Kestrel's 30,000,000-byte default (chunked ones a little
        // earlier) while the router allows 64 MiB; MN-047 raises the limit for /v1. Other endpoints keep the default.
        await using var env = await PerfEnvironment.StartAsync(Ct);
        var harness = new PerfHarness(env, PerfOptions.Smoke);
        long max = MirbadLLM.Agent.Contracts.Protocol.MaxProxyRequestBodyBytes;

        foreach (bool chunked in new[] { false, true })
        {
            var atLimit = await harness.SendThroughMasterAsync(max, chunked, Ct);
            Assert.True(atLimit.Ok, $"{max} bytes, chunked={chunked}: {atLimit.Outcome}");

            var over = await harness.SendThroughMasterAsync(max + 1, chunked, Ct);
            Assert.True(over.Outcome == "413 request_too_large", $"{max + 1} bytes, chunked={chunked}: {over.Outcome}");
        }

        Assert.Equal(30_000_000, env.MasterKestrelMaxRequestBodySize);
    }

    [Fact(Skip = "Opt-in: set MIRBADLLM_PERF=1 (tools/perf-multinode/run.ps1 or run.sh).", SkipUnless = nameof(PerfEnabled))]
    [Trait("Category", "Perf")]
    public async Task FullRunWritesJsonAndMarkdownReport()
    {
        var options = PerfOptions.Full;
        string outDir = Environment.GetEnvironmentVariable(OutputVariable) is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : Path.Combine(AppContext.BaseDirectory, "perf");
        string command = Environment.GetEnvironmentVariable(CommandVariable) is { Length: > 0 } given ? given : Environment.CommandLine;
        string os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "other";
        string name = $"perf-{os}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";

        PerfReport report;
        await using (var env = await PerfEnvironment.StartAsync(Ct))
        {
            report = await new PerfHarness(env, options).RunAsync(command, Ct);
        }

        Directory.CreateDirectory(outDir);
        string jsonPath = Path.Combine(outDir, name + ".json");
        await File.WriteAllTextAsync(jsonPath, PerfReportWriter.ToJson(report), Ct);
        string json = await File.ReadAllTextAsync(jsonPath, Ct);
        await File.WriteAllTextAsync(Path.Combine(outDir, name + ".md"), PerfReportWriter.ToMarkdown(json, name + ".json"), Ct);
        TestContext.Current.SendDiagnosticMessage($"Perf report written to {jsonPath}");

        AssertMeasured(report, options);
    }

    /// <summary>What every run must show: the harness sent what it was asked to, got answers and measured them.</summary>
    private static void AssertMeasured(PerfReport report, PerfOptions options)
    {
        var ns = report.NonStreaming;
        Assert.Equal(0, ns.DirectFailures);
        Assert.Equal(0, ns.ProxyFailures);
        Assert.Equal(options.NonStreamingRequests, ns.Direct.Count);
        Assert.Equal(options.NonStreamingRequests, ns.Proxy.Count);
        AssertOrdered(ns.Direct, positive: true);
        AssertOrdered(ns.Proxy, positive: true);

        var st = report.Streaming;
        foreach (var path in new[] { st.Direct, st.Proxy })
        {
            Assert.Equal(options.Streams, path.Requested);
            Assert.Equal(options.Streams, path.Completed);
            Assert.Equal(PerfHarness.FakeTokensPerStream, path.ContentTokensMin);
            Assert.Equal(PerfHarness.FakeTokensPerStream, path.ContentTokensMax);
            Assert.Equal(options.Streams, path.TimeToFirstChunk.Count);
            Assert.Equal(options.Streams, path.TimeToFirstToken.Count);
            Assert.Equal(options.Streams * (PerfHarness.FakeTokensPerStream - 1), path.InterChunkGap.Count);
            AssertOrdered(path.TimeToFirstChunk, positive: true);
            AssertOrdered(path.TimeToFirstToken, positive: true);
            AssertOrdered(path.InterChunkGap, positive: false);
            AssertOrdered(path.Total, positive: true);
            Assert.True(path.TimeToFirstChunk.P50Ms <= path.TimeToFirstToken.P50Ms + 0.001);
        }

        Assert.Equal(options.ConcurrencyLevels.Count * 4, report.Concurrency.Count);
        foreach (var c in report.Concurrency)
        {
            int expected = c.Mode == "streaming"
                ? c.Concurrency * options.ConcurrencyStreamsPerWorker
                : c.Concurrency * Math.Max(1, options.ConcurrencyRequestsPerLevel / c.Concurrency);
            Assert.Equal(expected, c.Requested);
            Assert.True(c.Succeeded == c.Requested, $"{c.Mode} {c.Path} N={c.Concurrency}: {c.Succeeded}/{c.Requested}");
            Assert.Equal(c.Requested, c.Latency.Count);
            Assert.True(c.RequestsPerSecond > 0);
            AssertOrdered(c.Latency, positive: true);
            if (c.Mode == "streaming")
            {
                Assert.Equal((long)c.Requested * PerfHarness.FakeTokensPerStream, c.ContentTokens);
                Assert.True(c.TokensPerSecond > 0);
            }
        }

        var lb = report.LargeBody;
        Assert.Equal(options.LargeBodyRepetitions * 2 * (lb.AcceptedBodyBytes == lb.RequestedBodyBytes ? 1 : 2), lb.Samples.Count);
        Assert.All(lb.Summaries, s => Assert.Equal(options.LargeBodyRepetitions, s.Attempts));
        Assert.All(lb.Summaries.Where(s => s.Path == "direct" || s.BodyBytes == lb.AcceptedBodyBytes), s =>
        {
            Assert.True(s.Succeeded == s.Attempts, $"{s.Path} {s.BodyBytes}: {string.Join(", ", s.Outcomes)}");
            AssertOrdered(s.SucceededLatency, positive: true);
        });
        Assert.NotEmpty(lb.Concurrent);
        foreach (var c in lb.Concurrent)
        {
            Assert.Equal(c.BodyEncoding == "chunked" ? lb.ChunkedBodyBytes : lb.AcceptedBodyBytes, c.BodyBytes);
            Assert.Equal(c.Requested, c.Outcomes.Values.Sum());
            Assert.True(c.Succeeded + c.ServerBusy == c.Requested, string.Join(", ", c.Outcomes.Select(kv => $"{kv.Key}: {kv.Value}")));
            Assert.True(c.Succeeded > 0);
            Assert.True(c.PeakBudgetHeldBytes <= c.BudgetCapacityBytes);
        }

        foreach (var stall in report.Backpressure.Stalls)
        {
            Assert.True(stall.ClientBytesReadBeforeStall >= options.StallReadBeforeBytes);
            Assert.True(stall.UpstreamWrittenAtStallEnd >= stall.UpstreamWrittenAtStallStart);
            Assert.True(stall.Samples.Count >= 3);

            // Backpressure: the client read only the start, so the upstream must not have been able to write it all.
            Assert.False(stall.UpstreamFinishedDuringStall, $"{stall.Path}: the upstream wrote the whole response to a stalled reader");
            Assert.True(stall.UpstreamWrittenAtStallEnd < stall.ResponseBytesRequested);
        }

        var slow = report.Backpressure.SlowReader;
        Assert.Equal(options.SlowReaderStreams * options.SlowReaderStreamsPerWorker, slow.Baseline.Completed);
        Assert.Equal(options.SlowReaderStreams * options.SlowReaderStreamsPerWorker, slow.WithSlowReader.Completed);
        Assert.True(slow.SlowReaderBytesRead > 0);

        Assert.Equal("loopback, fake model, not the DGX", report.Label);
        Assert.False(string.IsNullOrWhiteSpace(report.Machine.Cpu));
        Assert.True(report.Machine.LogicalProcessors > 0);
    }

    private static void AssertOrdered(LatencyStats s, bool positive)
    {
        Assert.True(s.Count > 0, "no samples");
        if (positive)
        {
            Assert.True(s.MinMs > 0, $"min {s.MinMs}");
        }
        else
        {
            Assert.True(s.MinMs >= 0, $"min {s.MinMs}");
        }

        Assert.True(s.MinMs <= s.P50Ms && s.P50Ms <= s.P95Ms && s.P95Ms <= s.P99Ms && s.P99Ms <= s.MaxMs, s.ToString());
        Assert.True(s.MinMs <= s.MeanMs && s.MeanMs <= s.MaxMs, s.ToString());
    }
}

/// <summary>Runs the perf tests on their own, after the parallel part of the suite.</summary>
[CollectionDefinition(nameof(PerfSerialGroup), DisableParallelization = true)]
public sealed class PerfSerialGroup;
