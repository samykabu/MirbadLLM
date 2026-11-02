using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MirbadLLM.UnitTests.Multinode.Perf;

/// <summary>
/// The second upstream of the performance harness (MN-046), for the two scenarios <see cref="Fakes.FakeModelServer"/> cannot
/// serve: its Kestrel keeps the default 30,000,000-byte body limit (a 64 MiB body gets 413), and its responses are a few
/// KiB (they fit in socket buffers, so a stalled reader never pushes back). <c>POST /v1/chat/completions</c>:
/// <list type="bullet">
/// <item>a body over 64 KiB is drained and counted (<c>{"received": n}</c>), never buffered: the large-body sink;</item>
/// <item>a small JSON body with <c>perf_bulk_bytes</c> streams that many bytes as 16 KiB SSE chunks, each awaited and
/// flushed, and records per <c>perf_id</c> how many bytes the writes completed, so a stalled reader's backpressure is
/// visible as a plateau; a write that throws records the abort time.</item>
/// </list>
/// </summary>
internal sealed class PerfUpstreamServer : IAsyncDisposable
{
    public const int ChunkBytes = 16 * 1024;
    private const int SmallBodyBytes = 64 * 1024;
    private static readonly byte[] Chunk = BuildChunk();

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, BulkState> _bulks = new(StringComparer.Ordinal);

    private PerfUpstreamServer(WebApplication app) => _app = app;

    public Uri BaseUri { get; private set; } = null!;

    public static async Task<PerfUpstreamServer> CreateAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(System.Net.IPAddress.Loopback, 0);
            o.Limits.MaxRequestBodySize = null;
        });
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var server = new PerfUpstreamServer(app);
        app.MapPost("/v1/chat/completions", server.HandleAsync);
        app.MapGet("/health", () => Results.Json(new { status = "ok" }));
        await app.StartAsync(ct);
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server.BaseUri = new Uri(address.TrimEnd('/') + "/");
        return server;
    }

    /// <summary>The state of the bulk response tagged <paramref name="id"/>; null before its request arrived.</summary>
    public BulkState? GetBulk(string id) => _bulks.TryGetValue(id, out var state) ? state : null;

    private async Task HandleAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        if (context.Request.ContentLength is null or > SmallBodyBytes)
        {
            byte[] buffer = new byte[64 * 1024];
            long received = 0;
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, ct)) > 0)
            {
                received += read;
            }

            await context.Response.WriteAsJsonAsync(new { received }, ct);
            return;
        }

        using var memory = new MemoryStream();
        await context.Request.Body.CopyToAsync(memory, ct);
        using var doc = JsonDocument.Parse(memory.ToArray());
        if (!doc.RootElement.TryGetProperty("perf_bulk_bytes", out var bytesProperty))
        {
            await context.Response.WriteAsJsonAsync(new { received = memory.Length }, ct);
            return;
        }

        long total = bytesProperty.GetInt64();
        string id = doc.RootElement.GetProperty("perf_id").GetString()!;
        var state = new BulkState();
        _bulks[id] = state;
        context.Response.ContentType = "text/event-stream";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        try
        {
            while (state.BytesWritten < total)
            {
                await context.Response.Body.WriteAsync(Chunk, ct);
                await context.Response.Body.FlushAsync(ct);
                Interlocked.Add(ref state.BytesWrittenField, Chunk.Length);
            }

            state.Completed = true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            Interlocked.Exchange(ref state.AbortedAt, Stopwatch.GetTimestamp());
        }
    }

    private static byte[] BuildChunk()
    {
        // "data: xxxx…\n\n", exactly ChunkBytes long.
        var text = new StringBuilder("data: ");
        text.Append('x', ChunkBytes - 8);
        text.Append("\n\n");
        byte[] bytes = Encoding.ASCII.GetBytes(text.ToString());
        Debug.Assert(bytes.Length == ChunkBytes);
        return bytes;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>Progress of one bulk response, read by the harness while the client stalls.</summary>
    internal sealed class BulkState
    {
        public long BytesWrittenField;

        public long BytesWritten => Interlocked.Read(ref BytesWrittenField);

        public volatile bool Completed;

        /// <summary><see cref="Stopwatch"/> timestamp of the failed write (the client went away); 0 while not aborted.</summary>
        public long AbortedAt;

        public long AbortedAtTimestamp => Interlocked.Read(ref AbortedAt);
    }
}
