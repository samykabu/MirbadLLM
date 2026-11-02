using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MirbadLLM.UnitTests.Multinode.Perf;

/// <summary>Sizes of one harness run. <see cref="Full"/> is the committed run; <see cref="Smoke"/> runs in the normal suite.</summary>
internal sealed record PerfOptions
{
    public int NonStreamingWarmup { get; init; } = 200;
    public int NonStreamingRequests { get; init; } = 2000;
    public int StreamWarmup { get; init; } = 10;
    public int Streams { get; init; } = 100;
    public int FakeTimeToFirstTokenMs { get; init; } = 20;
    public int FakeTokenRateMs { get; init; } = 10;
    public List<int> ConcurrencyLevels { get; init; } = [1, 16, 64];
    public int ConcurrencyRequestsPerLevel { get; init; } = 8192;
    public int ConcurrencyStreamsPerWorker { get; init; } = 16;
    public long LargeBodyBytes { get; init; } = 64L * 1024 * 1024;
    public int LargeBodyRepetitions { get; init; } = 5;
    public List<int> LargeBodyConcurrency { get; init; } = [16, 64];
    public bool LargeBodyChunkedToo { get; init; } = true;
    public long StallResponseBytes { get; init; } = 256L * 1024 * 1024;
    public long StallReadBeforeBytes { get; init; } = 1024 * 1024;
    public int StallMilliseconds { get; init; } = 5000;
    public int SlowReaderStreams { get; init; } = 63;
    public int SlowReaderStreamsPerWorker { get; init; } = 16;
    public int SlowReaderBytesPerSecond { get; init; } = 1024;

    public static PerfOptions Full { get; } = new();

    /// <summary>A few seconds: every scenario runs, with tiny counts and no model pacing.</summary>
    public static PerfOptions Smoke { get; } = new()
    {
        NonStreamingWarmup = 3,
        NonStreamingRequests = 20,
        StreamWarmup = 1,
        Streams = 3,
        FakeTimeToFirstTokenMs = 0,
        FakeTokenRateMs = 0,
        ConcurrencyLevels = [1, 4],
        ConcurrencyRequestsPerLevel = 8,
        ConcurrencyStreamsPerWorker = 1,
        LargeBodyBytes = 1024 * 1024,
        LargeBodyRepetitions = 2,
        LargeBodyConcurrency = [4],
        LargeBodyChunkedToo = false,
        StallResponseBytes = 64L * 1024 * 1024,
        StallReadBeforeBytes = 64 * 1024,
        StallMilliseconds = 300,
        SlowReaderStreams = 3,
        SlowReaderStreamsPerWorker = 1,
        SlowReaderBytesPerSecond = 1024,
    };
}

/// <summary>
/// MN-046 performance harness (design §13.8, §14 P1 budgets): every number comes from requests this code sends and times
/// with <see cref="Stopwatch"/> against <see cref="PerfEnvironment"/>, on two paths: <b>direct</b> (client → model server)
/// and <b>proxy</b> (client → master <c>/v1</c> → agent over mTLS HTTP/2 → the same model server).
/// </summary>
internal sealed class PerfHarness(PerfEnvironment env, PerfOptions options)
{
    /// <summary>Content tokens per stream FakeModelServer sends (its fixed <c>totalTokens</c>).</summary>
    public const int FakeTokensPerStream = 20;

    private static readonly MediaTypeHeaderValue JsonType = new("application/json");
    private static readonly byte[] ChatBody = Encoding.UTF8.GetBytes(
        """{"model":"qwen","messages":[{"role":"user","content":"hi"}],"max_tokens":4}""");
    private static readonly byte[] StreamBody = Encoding.UTF8.GetBytes(
        """{"model":"qwen","messages":[{"role":"user","content":"hi"}],"max_tokens":4,"stream":true}""");

    private Uri ProxyChat => new(env.MasterUri, "v1/chat/completions");

    private Uri DirectChat => new(env.FakeModelUri, "v1/chat/completions");

    private Uri DirectUpstreamChat => new(env.Upstream.BaseUri, "v1/chat/completions");

    public async Task<PerfReport> RunAsync(string command, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();
        var nonStreaming = await RunNonStreamingAsync(ct);
        var streaming = await RunStreamingAsync(ct);
        var concurrency = await RunConcurrencyAsync(ct);
        var largeBody = await RunLargeBodyAsync(ct);
        var backpressure = await RunBackpressureAsync(ct);
        double runSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;

        return new PerfReport(
            Schema: "mirbadllm-perf/1",
            Label: "loopback, fake model, not the DGX",
            Provenance: "agent-mtls-loopback",
            GeneratedUtc: DateTimeOffset.UtcNow,
            Command: command,
            Machine: ReadMachine(),
            Options: options,
            SetupSeconds: LatencyStats.Round(env.SetupTime.TotalSeconds),
            RunSeconds: LatencyStats.Round(runSeconds),
            NonStreaming: nonStreaming,
            Streaming: streaming,
            Concurrency: concurrency,
            LargeBody: largeBody,
            Backpressure: backpressure,
            Budgets: Budgets(streaming, concurrency),
            Findings: Findings(largeBody),
            NotMeasured: NotMeasured(largeBody, streaming));
    }

    // ---- Non-streaming ----

    private async Task<NonStreamingResult> RunNonStreamingAsync(CancellationToken ct)
    {
        SetFakePacing(0, 0);
        for (int i = 0; i < options.NonStreamingWarmup; i++)
        {
            await SendOnceAsync(DirectChat, ChatBody, ct);
            await SendOnceAsync(ProxyChat, ChatBody, ct);
        }

        var direct = new List<double>(options.NonStreamingRequests);
        var proxy = new List<double>(options.NonStreamingRequests);
        int directFailed = 0, proxyFailed = 0;
        for (int i = 0; i < options.NonStreamingRequests; i++)
        {
            // Interleaved (alternating which goes first) so drift and background noise hit both paths alike.
            bool directFirst = i % 2 == 0;
            for (int k = 0; k < 2; k++)
            {
                bool isDirect = directFirst ? k == 0 : k == 1;
                var sample = await SendOnceAsync(isDirect ? DirectChat : ProxyChat, ChatBody, ct);
                if (!sample.Ok)
                {
                    if (isDirect)
                    {
                        directFailed++;
                    }
                    else
                    {
                        proxyFailed++;
                    }

                    continue;
                }

                (isDirect ? direct : proxy).Add(sample.Ms);
            }
        }

        var d = LatencyStats.From(direct);
        var p = LatencyStats.From(proxy);
        return new NonStreamingResult(options.NonStreamingRequests, options.NonStreamingWarmup, directFailed, proxyFailed, d, p, Overhead.Between(d, p));
    }

    private sealed record RequestSample(bool Ok, int Status, double Ms);

    private async Task<RequestSample> SendOnceAsync(Uri url, byte[] body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = JsonType;
        long start = Stopwatch.GetTimestamp();
        try
        {
            using var response = await env.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            bool ok = response.StatusCode == HttpStatusCode.OK && bytes.AsSpan().IndexOf("\"choices\""u8) >= 0;
            return new RequestSample(ok, (int)response.StatusCode, ms);
        }
        catch (HttpRequestException)
        {
            return new RequestSample(false, 0, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }

    // ---- Streaming ----

    private async Task<StreamingResult> RunStreamingAsync(CancellationToken ct)
    {
        SetFakePacing(options.FakeTimeToFirstTokenMs, options.FakeTokenRateMs);
        for (int i = 0; i < options.StreamWarmup; i++)
        {
            await StreamOnceAsync(DirectChat, ct);
            await StreamOnceAsync(ProxyChat, ct);
        }

        var direct = new List<StreamSample>();
        var proxy = new List<StreamSample>();
        for (int i = 0; i < options.Streams; i++)
        {
            if (i % 2 == 0)
            {
                direct.Add(await StreamOnceAsync(DirectChat, ct));
                proxy.Add(await StreamOnceAsync(ProxyChat, ct));
            }
            else
            {
                proxy.Add(await StreamOnceAsync(ProxyChat, ct));
                direct.Add(await StreamOnceAsync(DirectChat, ct));
            }
        }

        var d = Summarize(direct);
        var p = Summarize(proxy);
        return new StreamingResult(
            options.FakeTimeToFirstTokenMs, options.FakeTokenRateMs, options.StreamWarmup, d, p,
            Overhead.Between(d.TimeToFirstChunk, p.TimeToFirstChunk),
            Overhead.Between(d.TimeToFirstToken, p.TimeToFirstToken),
            Overhead.Between(d.InterChunkGap, p.InterChunkGap));
    }

    internal sealed record StreamSample(bool Ok, int Status, double TimeToFirstChunkMs, double TimeToFirstTokenMs, double TotalMs, List<double> GapsMs, int ContentTokens, long Bytes = 0);

    private async Task<StreamSample> StreamOnceAsync(Uri url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(StreamBody) };
        request.Content.Headers.ContentType = JsonType;
        long start = Stopwatch.GetTimestamp();
        var gaps = new List<double>(FakeTokensPerStream);
        double ttfc = -1, ttft = -1;
        long lastContent = 0;
        int tokens = 0;
        long bytes = 0;
        bool done = false;
        try
        {
            using var response = await env.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new StreamSample(false, (int)response.StatusCode, 0, 0, Stopwatch.GetElapsedTime(start).TotalMilliseconds, gaps, 0);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                bytes += Encoding.UTF8.GetByteCount(line) + 1; // every SSE line ends with a line feed
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                long now = Stopwatch.GetTimestamp();
                if (ttfc < 0)
                {
                    ttfc = Stopwatch.GetElapsedTime(start, now).TotalMilliseconds;
                }

                string data = line[5..].Trim();
                if (data == "[DONE]")
                {
                    done = true;
                    continue;
                }

                // Same rule as BenchmarkSuite's TTFT: the first delta with content.
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(content.GetString()))
                {
                    tokens++;
                    if (ttft < 0)
                    {
                        ttft = Stopwatch.GetElapsedTime(start, now).TotalMilliseconds;
                    }
                    else
                    {
                        gaps.Add(Stopwatch.GetElapsedTime(lastContent, now).TotalMilliseconds);
                    }

                    lastContent = now;
                }
            }

            double total = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return new StreamSample(done && tokens > 0, 200, ttfc, ttft, total, gaps, tokens, bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new StreamSample(false, 0, ttfc, ttft, Stopwatch.GetElapsedTime(start).TotalMilliseconds, gaps, tokens);
        }
    }

    internal static StreamStats Summarize(IReadOnlyCollection<StreamSample> samples)
    {
        var ok = samples.Where(s => s.Ok).ToList();
        var gaps = ok.SelectMany(s => s.GapsMs).ToList();
        return new StreamStats(
            samples.Count,
            ok.Count,
            samples.Count - ok.Count,
            ok.Count == 0 ? 0 : ok.Min(s => s.ContentTokens),
            ok.Count == 0 ? 0 : ok.Max(s => s.ContentTokens),
            ok.Sum(s => (long)s.ContentTokens),
            LatencyStats.From(ok.Select(s => s.TimeToFirstChunkMs).ToList()),
            LatencyStats.From(ok.Select(s => s.TimeToFirstTokenMs).ToList()),
            LatencyStats.From(gaps),
            gaps.Count(g => g <= 0.001),
            LatencyStats.From(ok.Select(s => s.TotalMs).ToList()),
            ok.Count == 0 ? 0 : ok.Max(s => s.Bytes));
    }

    // ---- Concurrency ----

    private async Task<List<ConcurrencyResult>> RunConcurrencyAsync(CancellationToken ct)
    {
        var results = new List<ConcurrencyResult>();
        SetFakePacing(0, 0);
        foreach (int level in options.ConcurrencyLevels)
        {
            int perWorker = Math.Max(1, options.ConcurrencyRequestsPerLevel / level);
            foreach ((string path, Uri url) in new[] { ("direct", DirectChat), ("proxy", ProxyChat) })
            {
                var samples = new ConcurrentBag<RequestSample>();
                long start = Stopwatch.GetTimestamp();
                await Task.WhenAll(Enumerable.Range(0, level).Select(_ => Task.Run(async () =>
                {
                    for (int i = 0; i < perWorker; i++)
                    {
                        samples.Add(await SendOnceAsync(url, ChatBody, ct));
                    }
                }, ct)));
                double wall = Stopwatch.GetElapsedTime(start).TotalSeconds;
                int ok = samples.Count(s => s.Ok);
                results.Add(new ConcurrencyResult(
                    "non-streaming", path, level, level * perWorker, ok, samples.Count - ok, LatencyStats.Round(wall),
                    LatencyStats.Round(ok / wall), 0, 0, LatencyStats.From(samples.Where(s => s.Ok).Select(s => s.Ms).ToList()), null));
            }
        }

        SetFakePacing(options.FakeTimeToFirstTokenMs, options.FakeTokenRateMs);
        foreach (int level in options.ConcurrencyLevels)
        {
            foreach ((string path, Uri url) in new[] { ("direct", DirectChat), ("proxy", ProxyChat) })
            {
                var (samples, wall) = await RunStreamWorkersAsync(url, level, options.ConcurrencyStreamsPerWorker, ct);
                var stats = Summarize(samples);
                results.Add(new ConcurrencyResult(
                    "streaming", path, level, samples.Count, stats.Completed, stats.Failed, LatencyStats.Round(wall),
                    LatencyStats.Round(stats.Completed / wall), stats.ContentTokensTotal, LatencyStats.Round(stats.ContentTokensTotal / wall),
                    stats.Total, stats.TimeToFirstToken));
            }
        }

        return results;
    }

    private async Task<(List<StreamSample> Samples, double WallSeconds)> RunStreamWorkersAsync(Uri url, int workers, int perWorker, CancellationToken ct)
    {
        var samples = new ConcurrentBag<StreamSample>();
        long start = Stopwatch.GetTimestamp();
        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            for (int i = 0; i < perWorker; i++)
            {
                samples.Add(await StreamOnceAsync(url, ct));
            }
        }, ct)));
        return ([.. samples], Stopwatch.GetElapsedTime(start).TotalSeconds);
    }

    // ---- Large bodies ----

    private async Task<LargeBodyResult> RunLargeBodyAsync(CancellationToken ct)
    {
        SetFakePacing(0, 0);
        long requested = options.LargeBodyBytes;
        long? masterLimit = PerfEnvironment.MasterMaxRequestBodySize;
        long accepted = masterLimit is { } limit ? Math.Min(requested, limit) : requested;

        byte[] toFake = BuildBody(PerfEnvironment.FakeModelId, requested);
        string fakeDirect = await LargeOutcomeAsync(DirectChat, toFake, ct);
        string fakeProxy = await LargeOutcomeAsync(ProxyChat, toFake, ct);
        toFake = [];

        var samples = new List<LargeBodySample>();
        var summaries = new List<LargeBodySummary>();
        byte[] body = [];
        foreach (long size in accepted == requested ? new[] { requested } : [requested, accepted])
        {
            body = BuildBody(PerfEnvironment.UpstreamModelId, size);
            await SendLargeAsync(DirectUpstreamChat, body, chunked: false, ct);
            await SendLargeAsync(ProxyChat, body, chunked: false, ct);
            var sized = new List<LargeBodySample>();
            for (int i = 0; i < options.LargeBodyRepetitions; i++)
            {
                foreach ((string path, Uri url) in new[] { ("direct", DirectUpstreamChat), ("proxy", ProxyChat) })
                {
                    var s = await SendLargeAsync(url, body, chunked: false, ct);
                    sized.Add(new LargeBodySample(path, size, s.Status, s.Outcome, s.Ok, LatencyStats.Round(s.Ms),
                        s.Ok ? LatencyStats.Round(size / 1048576.0 / (s.Ms / 1000.0)) : 0));
                }
            }

            samples.AddRange(sized);
            foreach (string path in new[] { "direct", "proxy" })
            {
                var mine = sized.Where(x => x.Path == path).ToList();
                var ok = LatencyStats.From(mine.Where(x => x.Ok).Select(x => x.Ms).ToList());
                summaries.Add(new LargeBodySummary(
                    size, path, mine.Count, mine.Count(x => x.Ok),
                    mine.GroupBy(x => x.Outcome, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
                    ok, ok.Count == 0 ? 0 : LatencyStats.Round(size / 1048576.0 / (ok.P50Ms / 1000.0))));
            }
        }

        // body now holds the accepted size: the concurrent runs measure the budget with bodies the master takes. A chunked
        // body of that size is tried once and the chunked runs use the same size (MN-047 raised the /v1 limit for chunked
        // bodies too; MN-046 had to use one MiB less).
        var concurrent = new List<LargeBodyConcurrentResult>();
        foreach (int level in options.LargeBodyConcurrency)
        {
            concurrent.Add(await RunLargeConcurrentAsync(body, level, chunked: false, ct));
        }

        string chunkedAtAccepted = "not run";
        long chunkedBytes = 0;
        if (options.LargeBodyChunkedToo)
        {
            chunkedAtAccepted = (await SendLargeAsync(ProxyChat, body, chunked: true, ct)).Outcome;
            chunkedBytes = accepted;
            foreach (int level in options.LargeBodyConcurrency)
            {
                concurrent.Add(await RunLargeConcurrentAsync(body, level, chunked: true, ct));
            }
        }

        return new LargeBodyResult(
            requested, masterLimit, accepted, fakeDirect, fakeProxy, chunkedAtAccepted, chunkedBytes, options.LargeBodyRepetitions, summaries, samples, concurrent);
    }

    private async Task<LargeBodyConcurrentResult> RunLargeConcurrentAsync(byte[] body, int level, bool chunked, CancellationToken ct)
    {
        await SettleMemoryAsync();
        var budget = env.UploadBudget;
        long workingSetBefore = Environment.WorkingSet;
        long heapBefore = GC.GetTotalMemory(false);
        await using var sampler = MemorySampler.Start(() => budget.Capacity - budget.Available);
        var results = new ConcurrentBag<LargeSendResult>();
        long start = Stopwatch.GetTimestamp();
        var tasks = Enumerable.Range(0, level).Select(_ => Task.Run(async () => results.Add(await SendLargeAsync(ProxyChat, body, chunked, ct)), ct)).ToList();
        await Task.WhenAll(tasks);
        double wall = Stopwatch.GetElapsedTime(start).TotalSeconds;
        var peaks = await sampler.StopAsync();
        var outcomes = results.GroupBy(r => r.Outcome, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new LargeBodyConcurrentResult(
            level, body.Length, chunked ? "chunked" : "content-length", level, results.Count(r => r.Ok), results.Count(r => r.Outcome == "503 server_busy"),
            outcomes, LatencyStats.Round(wall), budget.Capacity, peaks.PeakGauge, workingSetBefore, peaks.PeakWorkingSet, heapBefore,
            peaks.PeakManagedHeap, LatencyStats.From(results.Where(r => r.Ok).Select(r => r.Ms).ToList()));
    }

    private sealed record LargeSendResult(bool Ok, int Status, string Outcome, double Ms);

    /// <summary>Sends <paramref name="body"/>; OK = 200 and the perf upstream counted exactly every byte.</summary>
    private async Task<LargeSendResult> SendLargeAsync(Uri url, byte[] body, bool chunked, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = chunked ? new StreamContent(new NonSeekableStream(body)) : new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = JsonType;
        long start = Stopwatch.GetTimestamp();
        try
        {
            using var response = await env.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            string text = await response.Content.ReadAsStringAsync(ct);
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            int status = (int)response.StatusCode;
            if (status == 200)
            {
                using var doc = JsonDocument.Parse(text);
                long received = doc.RootElement.GetProperty("received").GetInt64();
                return received == body.Length
                    ? new LargeSendResult(true, status, "200", ms)
                    : new LargeSendResult(false, status, $"200 short body ({received} of {body.Length})", ms);
            }

            string code = ErrorCode(text);
            return new LargeSendResult(false, status, code.Length == 0 ? $"{status}" : $"{status} {code}", ms);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new LargeSendResult(false, 0, $"exception {ex.GetType().Name}", Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Sends one <paramref name="size"/>-byte chat body to the perf upstream through the master (<c>/v1</c>), with a
    /// <c>Content-Length</c> or chunked: (status, outcome, OK = 200 and every byte arrived). Used by the body-limit check.
    /// </summary>
    internal async Task<(int Status, string Outcome, bool Ok)> SendThroughMasterAsync(long size, bool chunked, CancellationToken ct)
    {
        var result = await SendLargeAsync(ProxyChat, BuildBody(PerfEnvironment.UpstreamModelId, size), chunked, ct);
        return (result.Status, result.Outcome, result.Ok);
    }

    private async Task<string> LargeOutcomeAsync(Uri url, byte[] body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = JsonType;
        try
        {
            using var response = await env.Client.SendAsync(request, ct);
            string text = await response.Content.ReadAsStringAsync(ct);
            string code = ErrorCode(text);
            return code.Length == 0 ? $"{(int)response.StatusCode}" : $"{(int)response.StatusCode} {code}";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return $"exception {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string ErrorCode(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                {
                    return code.GetString()!;
                }

                if (error.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                {
                    return type.GetString()!;
                }
            }
        }
        catch (JsonException)
        {
        }

        return "";
    }

    /// <summary>A chat request of exactly <paramref name="size"/> bytes: the prompt is padded with 'a'.</summary>
    internal static byte[] BuildBody(string model, long size)
    {
        byte[] prefix = Encoding.UTF8.GetBytes("{\"model\":\"" + model + "\",\"messages\":[{\"role\":\"user\",\"content\":\"");
        byte[] suffix = Encoding.UTF8.GetBytes("\"}]}");
        var body = new byte[size];
        prefix.CopyTo(body, 0);
        body.AsSpan(prefix.Length, (int)size - prefix.Length - suffix.Length).Fill((byte)'a');
        suffix.CopyTo(body, (int)size - suffix.Length);
        return body;
    }

    // ---- Backpressure ----

    private async Task<BackpressureResult> RunBackpressureAsync(CancellationToken ct)
    {
        var stalls = new List<StallResult>
        {
            await RunStallAsync("direct", DirectUpstreamChat, ct),
            await RunStallAsync("proxy", ProxyChat, ct),
        };
        return new BackpressureResult(stalls, await RunSlowReaderAsync(ct));
    }

    private async Task<StallResult> RunStallAsync(string path, Uri url, CancellationToken ct)
    {
        await SettleMemoryAsync();
        string id = Guid.NewGuid().ToString("N");
        var response = await SendBulkAsync(url, id, options.StallResponseBytes, ct);
        var stream = await response.Content.ReadAsStreamAsync(ct);
        byte[] buffer = new byte[16 * 1024];
        long read = 0;
        while (read < options.StallReadBeforeBytes)
        {
            int n = await stream.ReadAsync(buffer, ct);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        var state = env.Upstream.GetBulk(id) ?? throw new InvalidOperationException("The perf upstream never saw the bulk request.");
        long heapStart = GC.GetTotalMemory(false);
        long wsStart = Environment.WorkingSet;
        var samples = new List<StallSample>();
        long stallStart = Stopwatch.GetTimestamp();
        int steps = Math.Max(2, options.StallMilliseconds / 100);
        long atMid = 0;
        samples.Add(new StallSample(0, state.BytesWritten));
        for (int i = 1; i <= steps; i++)
        {
            await Task.Delay(options.StallMilliseconds / steps, ct);
            samples.Add(new StallSample(LatencyStats.Round(Stopwatch.GetElapsedTime(stallStart).TotalMilliseconds), state.BytesWritten));
            if (i == steps / 2)
            {
                atMid = state.BytesWritten;
            }
        }

        long atEnd = state.BytesWritten;
        bool finished = state.Completed;
        long heapEnd = GC.GetTotalMemory(false);
        long wsEnd = Environment.WorkingSet;

        // The client goes away: dispose without reading the rest; how long until the upstream's write fails?
        long abortStart = Stopwatch.GetTimestamp();
        await stream.DisposeAsync();
        response.Dispose();
        double? abortMs = null;
        while (Stopwatch.GetElapsedTime(abortStart) < TimeSpan.FromSeconds(10) && !finished)
        {
            long at = state.AbortedAtTimestamp;
            if (at != 0)
            {
                abortMs = LatencyStats.Round(Stopwatch.GetElapsedTime(abortStart, at).TotalMilliseconds);
                break;
            }

            await Task.Delay(1, ct);
        }

        return new StallResult(
            path, options.StallResponseBytes, read, options.StallMilliseconds / 1000.0,
            samples[0].UpstreamBytesWritten, atMid, atEnd, atEnd - atMid, atEnd - read, finished,
            heapStart, heapEnd, wsStart, wsEnd, abortMs, samples);
    }

    private async Task<HttpResponseMessage> SendBulkAsync(Uri url, string id, long bytes, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(
            $$"""{"model":"{{PerfEnvironment.UpstreamModelId}}","perf_bulk_bytes":{{bytes.ToString(CultureInfo.InvariantCulture)}},"perf_id":"{{id}}"}""");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = JsonType;
        var response = await env.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            string text = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            throw new InvalidOperationException($"Bulk request answered {(int)response.StatusCode}: {text}");
        }

        return response;
    }

    private async Task<SlowReaderResult> RunSlowReaderAsync(CancellationToken ct)
    {
        SetFakePacing(options.FakeTimeToFirstTokenMs, options.FakeTokenRateMs);
        await SettleMemoryAsync();
        long wsBefore = Environment.WorkingSet;

        MemorySampler.Peaks baselinePeaks;
        List<StreamSample> baseline;
        double baselineWall;
        await using (var sampler = MemorySampler.Start(() => 0))
        {
            (baseline, baselineWall) = await RunStreamWorkersAsync(ProxyChat, options.SlowReaderStreams, options.SlowReaderStreamsPerWorker, ct);
            baselinePeaks = await sampler.StopAsync();
        }

        await SettleMemoryAsync();
        string id = Guid.NewGuid().ToString("N");
        using var slowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long slowRead = 0;
        var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = await SendBulkAsync(ProxyChat, id, options.StallResponseBytes, ct);
        var slowTask = Task.Run(async () =>
        {
            await using var stream = await response.Content.ReadAsStreamAsync(slowCts.Token);
            byte[] buffer = new byte[options.SlowReaderBytesPerSecond];
            try
            {
                while (!slowCts.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buffer, slowCts.Token);
                    if (n == 0)
                    {
                        return;
                    }

                    Interlocked.Add(ref slowRead, n);
                    firstRead.TrySetResult();
                    await Task.Delay(TimeSpan.FromSeconds(n / (double)options.SlowReaderBytesPerSecond), slowCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, ct);

        // The slow reader must be reading before the measured streams start (a tiny run would otherwise finish first).
        await Task.WhenAny(firstRead.Task, slowTask).WaitAsync(TimeSpan.FromSeconds(30), ct);

        MemorySampler.Peaks withPeaks;
        List<StreamSample> with;
        double withWall;
        await using (var sampler = MemorySampler.Start(() => 0))
        {
            (with, withWall) = await RunStreamWorkersAsync(ProxyChat, options.SlowReaderStreams, options.SlowReaderStreamsPerWorker, ct);
            withPeaks = await sampler.StopAsync();
        }

        long upstreamWritten = env.Upstream.GetBulk(id)?.BytesWritten ?? 0;
        await slowCts.CancelAsync();
        await slowTask;
        response.Dispose();

        return new SlowReaderResult(
            options.SlowReaderStreams, options.SlowReaderStreamsPerWorker, options.SlowReaderBytesPerSecond,
            Summarize(baseline), LatencyStats.Round(baselineWall), baselinePeaks.PeakManagedHeap,
            Summarize(with), LatencyStats.Round(withWall), withPeaks.PeakManagedHeap,
            Interlocked.Read(ref slowRead), upstreamWritten, wsBefore, Environment.WorkingSet);
    }

    // ---- Budgets, notes, machine ----

    private static List<BudgetCheck> Budgets(StreamingResult streaming, List<ConcurrencyResult> concurrency)
    {
        var checks = new List<BudgetCheck>
        {
            new("TTFT p50 overhead (sequential streams)", "+≤ 3 ms", streaming.TimeToFirstTokenOverhead.P50Ms, "ms", streaming.TimeToFirstTokenOverhead.P50Ms <= 3),
            new("TTFT p95 overhead (sequential streams)", "+≤ 10 ms", streaming.TimeToFirstTokenOverhead.P95Ms, "ms", streaming.TimeToFirstTokenOverhead.P95Ms <= 10),
        };
        int top = concurrency.Where(c => c.Mode == "streaming").Select(c => c.Concurrency).DefaultIfEmpty(0).Max();
        var direct = concurrency.FirstOrDefault(c => c.Mode == "streaming" && c.Path == "direct" && c.Concurrency == top);
        var proxy = concurrency.FirstOrDefault(c => c.Mode == "streaming" && c.Path == "proxy" && c.Concurrency == top);
        if (direct is not null && proxy is not null && direct.TokensPerSecond > 0)
        {
            double delta = LatencyStats.Round((proxy.TokensPerSecond - direct.TokensPerSecond) / direct.TokensPerSecond * 100);
            checks.Add(new BudgetCheck($"Aggregate tok/s at N={top}, proxy vs direct", "within 2 %", delta, "%", delta >= -2));
        }

        return checks;
    }

    /// <summary>Statements derived from this run's numbers.</summary>
    private static List<string> Findings(LargeBodyResult largeBody)
    {
        var findings = new List<string>();
        var refused = largeBody.Summaries.FirstOrDefault(s => s.Path == "proxy" && s.BodyBytes == largeBody.RequestedBodyBytes && s.Succeeded < s.Attempts);
        if (refused is not null)
        {
            string outcomes = string.Join(", ", refused.Outcomes.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}: {kv.Value}"));
            string limit = largeBody.MasterV1MaxRequestBodySize?.ToString(CultureInfo.InvariantCulture) ?? "unlimited";
            findings.Add(
                $"A {largeBody.RequestedBodyBytes}-byte body through the master failed {refused.Attempts - refused.Succeeded} of {refused.Attempts} times ({outcomes}). " +
                $"The master's /v1 body limit is {limit} bytes (the router's Protocol.MaxProxyRequestBodyBytes, raised per request since MN-047); " +
                $"the concurrent large-body runs use {largeBody.AcceptedBodyBytes}-byte bodies.");
        }

        if (largeBody.ChunkedAtAcceptedSizeOutcome is not ("200" or "not run"))
        {
            var withLength = largeBody.Summaries.FirstOrDefault(s => s.Path == "proxy" && s.BodyBytes == largeBody.AcceptedBodyBytes);
            string compare = withLength is null ? "" : $" (with a Content-Length: {withLength.Succeeded} of {withLength.Attempts} OK)";
            findings.Add(
                $"A chunked (no Content-Length) {largeBody.AcceptedBodyBytes}-byte body through the master: {largeBody.ChunkedAtAcceptedSizeOutcome}{compare}; " +
                $"the chunked concurrent runs use {largeBody.ChunkedBodyBytes}-byte bodies.");
        }

        return findings;
    }

    private static List<string> NotMeasured(LargeBodyResult largeBody, StreamingResult streaming) =>
    [
        "The DGX, real vLLM, GPUs and arm64: this is a dev-box run against fakes. The §14 P1 budgets are acceptance criteria for the DGX run (plan §3.6 W3 notes: not while a CI build runs; label `agent-mtls-loopback`, D59) and are only reported here.",
        "LAN latency and real TLS offload between two machines: client, master, agent and both model servers share one process and its CPUs on loopback, so contention between the hops is part of every number.",
        $"A {largeBody.RequestedBodyBytes}-byte body to FakeModelServer: it keeps Kestrel's default request limit (measured outcome direct: {largeBody.FakeModelDirectOutcome}; proxy: {largeBody.FakeModelProxyOutcome}). The large-body runs use the perf upstream instead.",
        "Large-body success timing through the master at the requested size when the master refuses it (see Findings); the agent hop alone at 64 MiB was measured by the MN-010 spike.",
        $"Backpressure with FakeModelServer: its whole streamed response measured {streaming.Direct.ResponseBytesMax} bytes at most, which fits in socket buffers, so a stalled reader cannot push back on it. The stall and slow-reader runs use the perf upstream's bulk stream instead.",
        "Bytes held in buffers per hop (socket, HTTP/2 window, Kestrel pipes): only the total between the upstream's completed writes and the client's reads is measured.",
        "Peak buffered bytes on the master is the upload budget's accounting (capacity − available) sampled every ~5 ms, not allocator bytes; working set and managed heap are for the whole process (client and servers included).",
        $"Exact model pacing: the fake model waits with Task.Delay, which the OS timer rounds up; this run asked for {streaming.FakeTokenRateMs} ms per token and measured a direct inter-chunk gap p50 of {streaming.Direct.InterChunkGap.P50Ms} ms. Both paths see the same pacing.",
    ];

    private static MachineInfo ReadMachine() => new(
        RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture.ToString(),
        RuntimeInformation.ProcessArchitecture.ToString(),
        ReadCpuName(),
        Environment.ProcessorCount,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        RuntimeInformation.FrameworkDescription,
#if DEBUG
        "Debug",
#else
        "Release",
#endif
        GCSettings.IsServerGC);

    /// <summary>The CPU's name as the OS reports it (registry on Windows, /proc/cpuinfo on Linux).</summary>
    private static string ReadCpuName()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (key?.GetValue("ProcessorNameString") is string name)
                {
                    return name.Trim();
                }
            }
            else if (File.Exists("/proc/cpuinfo"))
            {
                foreach (string line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name", StringComparison.Ordinal) || line.StartsWith("Model", StringComparison.Ordinal))
                    {
                        return line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown (not reported by the OS)";
    }

    private void SetFakePacing(int ttftMs, int tokenRateMs)
    {
        env.FakeModel.SetTimeToFirstTokenMs(ttftMs);
        env.FakeModel.SetTokenRateMs(tokenRateMs);
    }

    private static async Task SettleMemoryAsync()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        await Task.Delay(50);
    }

    /// <summary>Samples process memory (and an optional gauge) every ~5 ms until stopped; keeps the peaks.</summary>
    private sealed class MemorySampler : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task<Peaks> _loop;

        private MemorySampler(Func<long> gauge) => _loop = Task.Run(() => LoopAsync(gauge));

        public sealed record Peaks(long PeakGauge, long PeakWorkingSet, long PeakManagedHeap);

        public static MemorySampler Start(Func<long> gauge) => new(gauge);

        public async Task<Peaks> StopAsync()
        {
            await _stop.CancelAsync();
            return await _loop;
        }

        private async Task<Peaks> LoopAsync(Func<long> gauge)
        {
            long peakGauge = 0, peakWs = 0, peakHeap = 0;
            int tick = 0;
            while (true)
            {
                peakGauge = Math.Max(peakGauge, gauge());
                peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(false));
                if (tick++ % 4 == 0)
                {
                    peakWs = Math.Max(peakWs, Environment.WorkingSet);
                }

                if (_stop.IsCancellationRequested)
                {
                    return new Peaks(peakGauge, peakWs, peakHeap);
                }

                try
                {
                    await Task.Delay(5, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_stop.IsCancellationRequested)
            {
                await _stop.CancelAsync();
                await _loop;
            }

            _stop.Dispose();
        }
    }

    /// <summary>A read-only, non-seekable view of a byte array, so <see cref="StreamContent"/> sends it chunked.</summary>
    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = Math.Min(buffer.Length, data.Length - _position);
            data.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
