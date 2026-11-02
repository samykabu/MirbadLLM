using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MirbadLLM.Orchestration.Benchmarking;

namespace MirbadLLM.UnitTests.Multinode.Perf;

/// <summary>Summary of a list of millisecond samples (nearest-rank percentiles, <see cref="BenchmarkSuite.Percentile"/>).</summary>
internal sealed record LatencyStats(int Count, double MinMs, double MeanMs, double P50Ms, double P95Ms, double P99Ms, double MaxMs)
{
    public static LatencyStats From(IReadOnlyCollection<double> samplesMs)
    {
        if (samplesMs.Count == 0)
        {
            return new LatencyStats(0, 0, 0, 0, 0, 0, 0);
        }

        var list = samplesMs.ToList();
        return new LatencyStats(
            list.Count,
            Round(list.Min()),
            Round(list.Average()),
            Round(BenchmarkSuite.Percentile(list, 50)),
            Round(BenchmarkSuite.Percentile(list, 95)),
            Round(BenchmarkSuite.Percentile(list, 99)),
            Round(list.Max()));
    }

    internal static double Round(double value) => Math.Round(value, 4);
}

/// <summary>The proxy path minus the direct path, per percentile (a difference of percentiles, not a percentile of differences).</summary>
internal sealed record Overhead(double P50Ms, double P95Ms, double P99Ms)
{
    public static Overhead Between(LatencyStats direct, LatencyStats proxy) => new(
        LatencyStats.Round(proxy.P50Ms - direct.P50Ms),
        LatencyStats.Round(proxy.P95Ms - direct.P95Ms),
        LatencyStats.Round(proxy.P99Ms - direct.P99Ms));
}

internal sealed record MachineInfo(
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    string Cpu,
    int LogicalProcessors,
    long TotalAvailableMemoryBytes,
    string Runtime,
    string BuildConfiguration,
    bool ServerGc);

internal sealed record NonStreamingResult(
    int RequestedPerPath,
    int WarmupPerPath,
    int DirectFailures,
    int ProxyFailures,
    LatencyStats Direct,
    LatencyStats Proxy,
    Overhead Overhead);

internal sealed record StreamStats(
    int Requested,
    int Completed,
    int Failed,
    int ContentTokensMin,
    int ContentTokensMax,
    long ContentTokensTotal,
    LatencyStats TimeToFirstChunk,
    LatencyStats TimeToFirstToken,
    LatencyStats InterChunkGap,
    int ZeroGaps,
    LatencyStats Total,
    long ResponseBytesMax);

internal sealed record StreamingResult(
    int FakeTimeToFirstTokenMs,
    int FakeTokenRateMs,
    int WarmupPerPath,
    StreamStats Direct,
    StreamStats Proxy,
    Overhead TimeToFirstChunkOverhead,
    Overhead TimeToFirstTokenOverhead,
    Overhead InterChunkGapOverhead);

internal sealed record ConcurrencyResult(
    string Mode,
    string Path,
    int Concurrency,
    int Requested,
    int Succeeded,
    int Failed,
    double WallSeconds,
    double RequestsPerSecond,
    long ContentTokens,
    double TokensPerSecond,
    LatencyStats Latency,
    LatencyStats? TimeToFirstToken);

internal sealed record LargeBodySample(string Path, long BodyBytes, int Status, string Outcome, bool Ok, double Ms, double MibPerSecond);

internal sealed record LargeBodySummary(
    long BodyBytes,
    string Path,
    int Attempts,
    int Succeeded,
    Dictionary<string, int> Outcomes,
    LatencyStats SucceededLatency,
    double MibPerSecondAtP50);

internal sealed record LargeBodyConcurrentResult(
    int Concurrency,
    long BodyBytes,
    string BodyEncoding,
    int Requested,
    int Succeeded,
    int ServerBusy,
    Dictionary<string, int> Outcomes,
    double WallSeconds,
    long BudgetCapacityBytes,
    long PeakBudgetHeldBytes,
    long WorkingSetBeforeBytes,
    long PeakWorkingSetBytes,
    long ManagedHeapBeforeBytes,
    long PeakManagedHeapBytes,
    LatencyStats SucceededLatency);

internal sealed record LargeBodyResult(
    long RequestedBodyBytes,
    long? MasterV1MaxRequestBodySize,
    long AcceptedBodyBytes,
    string FakeModelDirectOutcome,
    string FakeModelProxyOutcome,
    string ChunkedAtAcceptedSizeOutcome,
    long ChunkedBodyBytes,
    int Repetitions,
    List<LargeBodySummary> Summaries,
    List<LargeBodySample> Samples,
    List<LargeBodyConcurrentResult> Concurrent);

internal sealed record StallSample(double AtMs, long UpstreamBytesWritten);

internal sealed record StallResult(
    string Path,
    long ResponseBytesRequested,
    long ClientBytesReadBeforeStall,
    double StallSeconds,
    long UpstreamWrittenAtStallStart,
    long UpstreamWrittenAtStallMid,
    long UpstreamWrittenAtStallEnd,
    long UpstreamGrowthSecondHalfBytes,
    long BytesBetweenUpstreamAndClientAtEnd,
    bool UpstreamFinishedDuringStall,
    long ManagedHeapAtStallStartBytes,
    long ManagedHeapAtStallEndBytes,
    long WorkingSetAtStallStartBytes,
    long WorkingSetAtStallEndBytes,
    double? AbortReachedUpstreamMs,
    List<StallSample> Samples);

internal sealed record SlowReaderResult(
    int Streams,
    int StreamsPerWorker,
    int SlowReaderBytesPerSecond,
    StreamStats Baseline,
    double BaselineWallSeconds,
    long BaselinePeakManagedHeapBytes,
    StreamStats WithSlowReader,
    double WithSlowReaderWallSeconds,
    long WithSlowReaderPeakManagedHeapBytes,
    long SlowReaderBytesRead,
    long SlowReaderUpstreamBytesWritten,
    long WorkingSetBeforeBytes,
    long WorkingSetAfterBytes);

internal sealed record BackpressureResult(List<StallResult> Stalls, SlowReaderResult SlowReader);

internal sealed record BudgetCheck(string Name, string Target, double Measured, string Unit, bool Within);

internal sealed record PerfReport(
    string Schema,
    string Label,
    string Provenance,
    DateTimeOffset GeneratedUtc,
    string Command,
    MachineInfo Machine,
    PerfOptions Options,
    double SetupSeconds,
    double RunSeconds,
    NonStreamingResult NonStreaming,
    StreamingResult Streaming,
    List<ConcurrencyResult> Concurrency,
    LargeBodyResult LargeBody,
    BackpressureResult Backpressure,
    List<BudgetCheck> Budgets,
    List<string> Findings,
    List<string> NotMeasured);

/// <summary>JSON and Markdown output. The Markdown is rendered from the JSON text, never from in-memory results.</summary>
internal static class PerfReportWriter
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string ToJson(PerfReport report) => JsonSerializer.Serialize(report, Json);

    public static PerfReport FromJson(string json) =>
        JsonSerializer.Deserialize<PerfReport>(json, Json) ?? throw new InvalidOperationException("Empty perf report.");

    /// <summary>Renders the Markdown report from <paramref name="json"/> (the file the harness wrote).</summary>
    public static string ToMarkdown(string json, string jsonFileName)
    {
        var r = FromJson(json);
        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"# Multinode performance harness: {r.Label}");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"Generated by the code from `{jsonFileName}` (schema `{r.Schema}`); do not edit by hand. Every number below was measured by this run.");
        md.AppendLine();
        md.AppendLine("| | |");
        md.AppendLine("|---|---|");
        md.AppendLine(CultureInfo.InvariantCulture, $"| Date (UTC) | {r.GeneratedUtc:yyyy-MM-dd HH:mm:ss} |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| Label | **{r.Label}** |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| Provenance | `{r.Provenance}` (client, master, agent and model servers in one process on loopback) |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| Command | `{r.Command}` |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| OS | {r.Machine.OsDescription} ({r.Machine.OsArchitecture}, process {r.Machine.ProcessArchitecture}) |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| CPU | {r.Machine.Cpu}, {r.Machine.LogicalProcessors} logical processors |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| Memory available to the process | {Mib(r.Machine.TotalAvailableMemoryBytes)} MiB |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| Runtime | {r.Machine.Runtime}, {r.Machine.BuildConfiguration} build, server GC {r.Machine.ServerGc} |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| Setup / run time | {F(r.SetupSeconds)} s / {F(r.RunSeconds)} s |");
        md.AppendLine();
        md.AppendLine("Paths: **direct** = client → FakeModelServer (or the perf upstream) over HTTP/1.1; **proxy** = client → master `/v1` (HTTP/1.1) → agent (mTLS, HTTP/2) → the same server. Overhead = proxy percentile − direct percentile.");
        md.AppendLine();

        md.AppendLine("## Budgets (design §14 P1, reported, not failing in dev)");
        md.AppendLine();
        md.AppendLine("| Budget | Target | Measured | Within |");
        md.AppendLine("|---|---|---|---|");
        foreach (var b in r.Budgets)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {b.Name} | {b.Target} | {F(b.Measured)} {b.Unit} | {(b.Within ? "yes" : "no")} |");
        }

        md.AppendLine();
        var ns = r.NonStreaming;
        md.AppendLine(CultureInfo.InvariantCulture, $"## Non-streaming request latency ({ns.RequestedPerPath} sequential requests per path, after {ns.WarmupPerPath} warm-up, interleaved)");
        md.AppendLine();
        LatencyHeader(md, "Path");
        LatencyRow(md, $"direct ({ns.DirectFailures} failed)", ns.Direct);
        LatencyRow(md, $"proxy ({ns.ProxyFailures} failed)", ns.Proxy);
        md.AppendLine(CultureInfo.InvariantCulture, $"| **overhead** | | | | **{F(ns.Overhead.P50Ms)}** | **{F(ns.Overhead.P95Ms)}** | **{F(ns.Overhead.P99Ms)}** | |");
        md.AppendLine();

        var st = r.Streaming;
        md.AppendLine(CultureInfo.InvariantCulture, $"## Streaming ({st.Direct.Requested} sequential streams per path, interleaved; fake model TTFT {st.FakeTimeToFirstTokenMs} ms, {st.FakeTokenRateMs} ms per token)");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"Completed direct {st.Direct.Completed}/{st.Direct.Requested}, proxy {st.Proxy.Completed}/{st.Proxy.Requested}; content tokens per stream {st.Proxy.ContentTokensMin}–{st.Proxy.ContentTokensMax} (proxy), {st.Direct.ContentTokensMin}–{st.Direct.ContentTokensMax} (direct). Zero gaps (two chunks in one read): direct {st.Direct.ZeroGaps}, proxy {st.Proxy.ZeroGaps}.");
        md.AppendLine();
        LatencyHeader(md, "Metric / path");
        LatencyRow(md, "time to first chunk, direct", st.Direct.TimeToFirstChunk);
        LatencyRow(md, "time to first chunk, proxy", st.Proxy.TimeToFirstChunk);
        LatencyRow(md, "time to first token, direct", st.Direct.TimeToFirstToken);
        LatencyRow(md, "time to first token, proxy", st.Proxy.TimeToFirstToken);
        LatencyRow(md, "inter-chunk gap, direct", st.Direct.InterChunkGap);
        LatencyRow(md, "inter-chunk gap, proxy", st.Proxy.InterChunkGap);
        LatencyRow(md, "whole stream, direct", st.Direct.Total);
        LatencyRow(md, "whole stream, proxy", st.Proxy.Total);
        md.AppendLine();
        md.AppendLine("| Overhead | p50 ms | p95 ms | p99 ms |");
        md.AppendLine("|---|---|---|---|");
        md.AppendLine(CultureInfo.InvariantCulture, $"| time to first chunk | {F(st.TimeToFirstChunkOverhead.P50Ms)} | {F(st.TimeToFirstChunkOverhead.P95Ms)} | {F(st.TimeToFirstChunkOverhead.P99Ms)} |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| time to first token | {F(st.TimeToFirstTokenOverhead.P50Ms)} | {F(st.TimeToFirstTokenOverhead.P95Ms)} | {F(st.TimeToFirstTokenOverhead.P99Ms)} |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| inter-chunk gap | {F(st.InterChunkGapOverhead.P50Ms)} | {F(st.InterChunkGapOverhead.P95Ms)} | {F(st.InterChunkGapOverhead.P99Ms)} |");
        md.AppendLine();

        md.AppendLine("## Concurrency");
        md.AppendLine();
        md.AppendLine("N workers start together and each sends its share back to back. req/s = successful requests / wall time of the run; tok/s = content tokens received / wall time (the fake model paces tokens, so tok/s shows whether the path slows delivery, not a capacity). Streaming latency columns are whole-stream times.");
        md.AppendLine();
        md.AppendLine("| Mode | Path | N | Requested | OK | Failed | Wall s | req/s | tok/s | p50 ms | p95 ms | p99 ms | TTFT p50 ms | TTFT p95 ms |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var c in r.Concurrency)
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {c.Mode} | {c.Path} | {c.Concurrency} | {c.Requested} | {c.Succeeded} | {c.Failed} | {F(c.WallSeconds)} | {F(c.RequestsPerSecond)} | {(c.Mode == "streaming" ? F(c.TokensPerSecond) : "–")} | {F(c.Latency.P50Ms)} | {F(c.Latency.P95Ms)} | {F(c.Latency.P99Ms)} | {(c.TimeToFirstToken is { } t ? F(t.P50Ms) : "–")} | {(c.TimeToFirstToken is { } t2 ? F(t2.P95Ms) : "–")} |");
        }

        md.AppendLine();
        var lb = r.LargeBody;
        md.AppendLine(CultureInfo.InvariantCulture, $"## Large request body ({lb.RequestedBodyBytes} bytes = {Mib(lb.RequestedBodyBytes)} MiB)");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"- To FakeModelServer: directly **{lb.FakeModelDirectOutcome}**, through the proxy **{lb.FakeModelProxyOutcome}** (FakeModelServer keeps Kestrel's default body limit), so the runs below use the perf upstream, which drains and counts the body. OK = 200 and the upstream counted every byte.");
        string limitText = lb.MasterV1MaxRequestBodySize is { } limit ? limit.ToString(CultureInfo.InvariantCulture) + " bytes" : "unlimited";
        md.AppendLine(CultureInfo.InvariantCulture, $"- The master's `/v1` request body limit (raised per request by the router since MN-047): **{limitText}**. Concurrent runs use {lb.AcceptedBodyBytes} bytes ({Mib(lb.AcceptedBodyBytes)} MiB), the largest body this master accepts up to the requested size. One chunked body of that size through the proxy: **{lb.ChunkedAtAcceptedSizeOutcome}**; chunked concurrent runs use {lb.ChunkedBodyBytes} bytes.");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"Sequential, {lb.Repetitions} per path and size after one warm-up each:");
        md.AppendLine();
        md.AppendLine("| Body bytes | Path | OK | Outcomes | p50 ms | min ms | max ms | MiB/s at p50 |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var s in lb.Summaries)
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {s.BodyBytes} | {s.Path} | {s.Succeeded}/{s.Attempts} | {Outcomes(s.Outcomes)} | {F(s.SucceededLatency.P50Ms)} | {F(s.SucceededLatency.MinMs)} | {F(s.SucceededLatency.MaxMs)} | {F(s.MibPerSecondAtP50)} |");
        }

        md.AppendLine();
        md.AppendLine("Concurrent through the proxy (all requests started together; budget = the master's upload budget, sampled every ~5 ms):");
        md.AppendLine();
        md.AppendLine("| N | Body bytes | Encoding | OK | server_busy | Outcomes | Wall s | Budget capacity MiB | Peak budget held MiB | Working set before → peak MiB | Managed heap before → peak MiB | OK latency p50 / max ms |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var c in lb.Concurrent)
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {c.Concurrency} | {c.BodyBytes} | {c.BodyEncoding} | {c.Succeeded}/{c.Requested} | {c.ServerBusy} | {Outcomes(c.Outcomes)} | {F(c.WallSeconds)} | {Mib(c.BudgetCapacityBytes)} | {Mib(c.PeakBudgetHeldBytes)} | {Mib(c.WorkingSetBeforeBytes)} → {Mib(c.PeakWorkingSetBytes)} | {Mib(c.ManagedHeapBeforeBytes)} → {Mib(c.PeakManagedHeapBytes)} | {F(c.SucceededLatency.P50Ms)} / {F(c.SucceededLatency.MaxMs)} |");
        }

        md.AppendLine();
        md.AppendLine("## Slow client and backpressure");
        md.AppendLine();
        md.AppendLine("A stalled reader: the client reads the start of a bulk response from the perf upstream, then stops reading. The upstream counts the bytes whose write-and-flush completed; a plateau means the server stopped pulling from upstream.");
        md.AppendLine();
        md.AppendLine("| Path | Response bytes | Read before stall | Stall s | Upstream written at start / mid / end | Growth in 2nd half | Upstream − client at end | Finished during stall | Managed heap start → end MiB | Abort reached upstream ms |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var s in r.Backpressure.Stalls)
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {s.Path} | {Mib(s.ResponseBytesRequested)} MiB | {Kib(s.ClientBytesReadBeforeStall)} KiB | {F(s.StallSeconds)} | {Kib(s.UpstreamWrittenAtStallStart)} / {Kib(s.UpstreamWrittenAtStallMid)} / {Kib(s.UpstreamWrittenAtStallEnd)} KiB | {Kib(s.UpstreamGrowthSecondHalfBytes)} KiB | {Kib(s.BytesBetweenUpstreamAndClientAtEnd)} KiB | {s.UpstreamFinishedDuringStall} | {Mib(s.ManagedHeapAtStallStartBytes)} → {Mib(s.ManagedHeapAtStallEndBytes)} | {(s.AbortReachedUpstreamMs is { } a ? F(a) : "not observed")} |");
        }

        var sr = r.Backpressure.SlowReader;
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"A {sr.SlowReaderBytesPerSecond} B/s reader on a bulk response through the proxy while {sr.Streams} streams ({sr.StreamsPerWorker} each, back to back) run through the proxy, against the same {sr.Streams} streams without it. The slow reader read {sr.SlowReaderBytesRead} bytes; the upstream had written {sr.SlowReaderUpstreamBytesWritten} bytes for it when it was cancelled.");
        md.AppendLine();
        md.AppendLine("| Run | Completed | Wall s | TTFT p50 ms | TTFT p95 ms | Gap p95 ms | Gap p99 ms | Peak managed heap MiB |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        md.AppendLine(CultureInfo.InvariantCulture, $"| without slow reader | {sr.Baseline.Completed}/{sr.Baseline.Requested} | {F(sr.BaselineWallSeconds)} | {F(sr.Baseline.TimeToFirstToken.P50Ms)} | {F(sr.Baseline.TimeToFirstToken.P95Ms)} | {F(sr.Baseline.InterChunkGap.P95Ms)} | {F(sr.Baseline.InterChunkGap.P99Ms)} | {Mib(sr.BaselinePeakManagedHeapBytes)} |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| with slow reader | {sr.WithSlowReader.Completed}/{sr.WithSlowReader.Requested} | {F(sr.WithSlowReaderWallSeconds)} | {F(sr.WithSlowReader.TimeToFirstToken.P50Ms)} | {F(sr.WithSlowReader.TimeToFirstToken.P95Ms)} | {F(sr.WithSlowReader.InterChunkGap.P95Ms)} | {F(sr.WithSlowReader.InterChunkGap.P99Ms)} | {Mib(sr.WithSlowReaderPeakManagedHeapBytes)} |");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"Process working set before → after the two runs: {Mib(sr.WorkingSetBeforeBytes)} → {Mib(sr.WorkingSetAfterBytes)} MiB.");
        md.AppendLine();

        md.AppendLine("## Findings of this run");
        md.AppendLine();
        foreach (string item in r.Findings)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"- {item}");
        }

        if (r.Findings.Count == 0)
        {
            md.AppendLine("- none");
        }

        md.AppendLine();
        md.AppendLine("## Not measured, and why");
        md.AppendLine();
        foreach (string item in r.NotMeasured)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"- {item}");
        }

        md.AppendLine();
        md.AppendLine("## Options of this run");
        md.AppendLine();
        md.AppendLine("```json");
        md.AppendLine(JsonSerializer.Serialize(r.Options, Json));
        md.AppendLine("```");
        return md.ToString();
    }

    private static void LatencyHeader(StringBuilder md, string first)
    {
        md.AppendLine(CultureInfo.InvariantCulture, $"| {first} | n | min ms | mean ms | p50 ms | p95 ms | p99 ms | max ms |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
    }

    private static void LatencyRow(StringBuilder md, string name, LatencyStats s) =>
        md.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {s.Count} | {F(s.MinMs)} | {F(s.MeanMs)} | {F(s.P50Ms)} | {F(s.P95Ms)} | {F(s.P99Ms)} | {F(s.MaxMs)} |");

    private static string Outcomes(Dictionary<string, int> outcomes) =>
        string.Join(", ", outcomes.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}: {kv.Value}"));

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Mib(long bytes) => (bytes / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture);

    private static string Kib(long bytes) => (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture);
}
