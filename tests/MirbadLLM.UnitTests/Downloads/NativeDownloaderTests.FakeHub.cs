using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#pragma warning disable CA1707, CA2000, CA5350, CA5394 // test names; the app owns its services; git blob ids are SHA-1 by definition; test content is not security sensitive.

namespace MirbadLLM.UnitTests.Downloads;

public sealed partial class NativeDownloaderTests
{
    /// <summary>One request the fake Hub answered.</summary>
    internal sealed record HubRequest(string Host, string Path, string? Range, string? Authorization, int Status, long BodyBytes);

    /// <summary>
    /// A local Hugging Face Hub on loopback (real HTTP through Kestrel, nothing from the internet): the
    /// <c>api/models/{repo}[/revision/{rev}]?blobs=true</c> listing (commit SHA, sizes, LFS SHA-256, git blob ids) and
    /// <c>{repo}/resolve/{sha}/{path}</c> with byte ranges, plus per-file misbehaviour the tests switch on (ignore the
    /// range, 416, corrupt bytes, stall, drop the connection, redirect, refuse) and a <c>/cdn/</c> redirect target.
    /// </summary>
    internal sealed class FakeHub : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private FakeHub(WebApplication app)
        {
            _app = app;
        }

        public Uri BaseUri { get; private set; } = null!;

        /// <summary>The same server under another host name (another origin: no credential may reach it).</summary>
        public Uri LocalhostUri => new($"http://localhost:{BaseUri.Port}/");

        public ConcurrentQueue<HubRequest> Requests { get; } = new();

        public ConcurrentDictionary<string, FakeRepo> Repos { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<HubRequest> FileRequests(string path) =>
            Requests.Where(r => r.Path.EndsWith("/" + path, StringComparison.Ordinal) && !r.Path.StartsWith("/api/", StringComparison.Ordinal)).ToList();

        public static async Task<FakeHub> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            var hub = new FakeHub(app);
            app.Run(hub.HandleAsync);
            await app.StartAsync();
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            hub.BaseUri = new Uri(address.TrimEnd('/') + "/");
            return hub;
        }

        public FakeRepo AddRepo(string repoId, string? requiredToken = null)
        {
            var repo = new FakeRepo(repoId) { RequiredToken = requiredToken };
            Repos[repoId] = repo;
            return repo;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var file in Repos.Values.SelectMany(r => r.Commits.Values).SelectMany(c => c))
            {
                file.StallRelease.TrySetResult();
            }

            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private async Task HandleAsync(HttpContext context)
        {
            string path = context.Request.Path.Value ?? "/";
            string? auth = context.Request.Headers.Authorization.ToString() is { Length: > 0 } a ? a : null;
            string? range = context.Request.Headers.Range.ToString() is { Length: > 0 } r ? r : null;
            long sent = 0;
            int recorded = 0;
            void Record(long bytes)
            {
                if (Interlocked.Exchange(ref recorded, 1) == 0)
                {
                    Requests.Enqueue(new HubRequest(context.Request.Host.Host, path, range, auth, context.Response.StatusCode, bytes));
                }
            }

            context.Items["record"] = (Action<long>)Record;
            try
            {
                if (path.StartsWith("/api/models/", StringComparison.Ordinal))
                {
                    sent = await ListingAsync(context, path["/api/models/".Length..], auth);
                }
                else if (path.StartsWith("/cdn/", StringComparison.Ordinal))
                {
                    var (repo, sha, file) = Locate(path["/cdn/".Length..], "/");
                    sent = file is null ? await StatusAsync(context, 404) : await ServeAsync(context, file, range);
                }
                else if (path.IndexOf("/resolve/", StringComparison.Ordinal) is int at and > 0)
                {
                    var (repo, sha, file) = Locate(path[1..at] + "/" + path[(at + "/resolve/".Length)..], "/");
                    if (repo is null || file is null)
                    {
                        sent = await StatusAsync(context, 404);
                    }
                    else if (repo.RequiredToken is { } token && auth != "Bearer " + token)
                    {
                        sent = await StatusAsync(context, 401, $"Invalid credentials in Authorization header: {auth}");
                    }
                    else if (file.Redirect is { } redirect)
                    {
                        context.Response.StatusCode = 302;
                        context.Response.Headers.Location = redirect(sha!).AbsoluteUri;
                    }
                    else
                    {
                        sent = await ServeAsync(context, file, range);
                    }
                }
                else
                {
                    sent = await StatusAsync(context, 404);
                }
            }
            finally
            {
                Record(sent);
            }
        }

        private async Task<long> ListingAsync(HttpContext context, string rest, string? auth)
        {
            string? revision = null;
            int at = rest.IndexOf("/revision/", StringComparison.Ordinal);
            if (at >= 0)
            {
                revision = Uri.UnescapeDataString(rest[(at + "/revision/".Length)..]);
                rest = rest[..at];
            }

            if (!Repos.TryGetValue(rest, out var repo))
            {
                return await StatusAsync(context, 404);
            }

            if (repo.BeforeListing is { } hold)
            {
                await hold().ConfigureAwait(false);
            }

            if (repo.RequiredToken is { } token && auth != "Bearer " + token)
            {
                return await StatusAsync(context, 401, $"Invalid token {auth}");
            }

            string? sha = repo.Branches.TryGetValue(revision ?? "main", out var branchSha) ? branchSha
                : revision is not null && repo.Commits.ContainsKey(revision) ? revision : null;
            if (sha is null)
            {
                return await StatusAsync(context, 404);
            }

            var siblings = repo.Commits[sha].Select(f => f.Lfs
                ? (object)new { rfilename = f.Path, size = f.ListedSize ?? f.Content.Length, blobId = new string('a', 40), lfs = new { sha256 = f.Sha256, size = f.ListedSize ?? f.Content.Length, pointerSize = 134 } }
                : new { rfilename = f.Path, size = f.ListedSize ?? f.Content.Length, blobId = f.BlobSha1 }).ToList();
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { id = repo.Id, sha, siblings });
            context.Response.ContentType = "application/json";
            RecordNow(context, body.Length);
            await context.Response.Body.WriteAsync(body);
            repo.AfterListing?.Invoke();
            return body.Length;
        }

        private (FakeRepo? Repo, string? Sha, FakeFile? File) Locate(string rest, string separator)
        {
            // rest = {owner}/{name}/{sha}/{path...}
            string[] parts = rest.Split(separator, 4);
            if (parts.Length < 4 || !Repos.TryGetValue(parts[0] + "/" + parts[1], out var repo) || !repo.Commits.TryGetValue(parts[2], out var files))
            {
                return (null, null, null);
            }

            return (repo, parts[2], files.FirstOrDefault(f => f.Path == parts[3]));
        }

        private static void RecordNow(HttpContext context, long bytes) => ((Action<long>)context.Items["record"]!)(bytes);

        private static async Task<long> StatusAsync(HttpContext context, int status, string? body = null)
        {
            context.Response.StatusCode = status;
            if (body is null)
            {
                return 0;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(body);
            await context.Response.Body.WriteAsync(bytes);
            return bytes.Length;
        }

        private static async Task<long> ServeAsync(HttpContext context, FakeFile file, string? rangeHeader)
        {
            if (file.StatusOverride is { } status)
            {
                return await StatusAsync(context, status, file.StatusBody);
            }

            byte[] content = file.Content;
            if (Interlocked.Decrement(ref file.CorruptRemaining) >= 0)
            {
                content = (byte[])content.Clone();
                content[^1] ^= 0xFF;
            }

            long start = 0;
            bool ranged = false;
            if (rangeHeader is not null && !file.IgnoreRange)
            {
                if (Interlocked.Decrement(ref file.Refuse416Remaining) >= 0)
                {
                    context.Response.StatusCode = 416;
                    context.Response.Headers.ContentRange = $"bytes */{content.Length}";
                    return 0;
                }

                start = long.Parse(rangeHeader["bytes=".Length..].TrimEnd('-'), CultureInfo.InvariantCulture);
                if (start >= content.Length)
                {
                    context.Response.StatusCode = 416;
                    context.Response.Headers.ContentRange = $"bytes */{content.Length}";
                    return 0;
                }

                ranged = true;
            }

            context.Response.StatusCode = ranged ? 206 : 200;
            if (ranged)
            {
                context.Response.Headers.ContentRange = $"bytes {start}-{content.Length - 1}/{content.Length}";
            }

            context.Response.ContentLength = content.Length - start;
            long stopAt = content.Length;
            bool stall = false;
            bool drop = false;
            if (file.StallAfter is { } stallAt && start < stallAt)
            {
                stopAt = stallAt;
                stall = true;
            }
            else if (file.DropAfter is { } dropAt && start < dropAt && Interlocked.Decrement(ref file.DropRemaining) >= 0)
            {
                stopAt = dropAt;
                drop = true;
            }

            RecordNow(context, stopAt - start);
            long position = start;
            while (position < stopAt)
            {
                int chunk = (int)Math.Min(16 * 1024, stopAt - position);
                await context.Response.Body.WriteAsync(content.AsMemory((int)position, chunk), context.RequestAborted);
                position += chunk;
            }

            await context.Response.Body.FlushAsync(context.RequestAborted);
            if (stall)
            {
                file.Stalled.TrySetResult();
                try
                {
                    await file.StallRelease.Task.WaitAsync(context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    // The client went away (agent stopped).
                }

                context.Abort();
            }
            else if (drop)
            {
                // Give the client time to read what was flushed, then reset the connection mid-body.
                await Task.Delay(200);
                context.Abort();
            }

            return position - start;
        }
    }

    internal sealed class FakeRepo(string id)
    {
        public string Id { get; } = id;

        public string? RequiredToken { get; set; }

        public Dictionary<string, string> Branches { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<FakeFile>> Commits { get; } = new(StringComparer.Ordinal);

        public Action? AfterListing { get; set; }

        /// <summary>Awaited before the listing is answered, so a test can hold a <c>StartAsync</c> inside its plan step.</summary>
        public Func<Task>? BeforeListing { get; set; }

        public FakeRepo Commit(string sha, params FakeFile[] files)
        {
            Commits[sha] = files.ToList();
            Branches.TryAdd("main", sha);
            return this;
        }

        public FakeFile File(string sha, string path) => Commits[sha].Single(f => f.Path == path);
    }

    internal sealed class FakeFile(string path, byte[] content, bool lfs)
    {
        public int CorruptRemaining;
        public int Refuse416Remaining;
        public int DropRemaining;

        public string Path { get; } = path;

        public byte[] Content { get; } = content;

        public bool Lfs { get; } = lfs;

        public long? ListedSize { get; set; }

        public bool IgnoreRange { get; set; }

        public long? StallAfter { get; set; }

        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource StallRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long? DropAfter { get; set; }

        public Func<string, Uri>? Redirect { get; set; }

        public int? StatusOverride { get; set; }

        public string? StatusBody { get; set; }

        public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Content));

        public string BlobSha1 =>
            Convert.ToHexStringLower(SHA1.HashData([.. Encoding.ASCII.GetBytes($"blob {Content.Length}\0"), .. Content]));

        public static FakeFile Small(string path, string text) => new(path, Encoding.UTF8.GetBytes(text), lfs: false);

        public static FakeFile Large(string path, int size, int seed)
        {
            byte[] bytes = new byte[size];
            new Random(seed).NextBytes(bytes);
            return new FakeFile(path, bytes, lfs: true);
        }
    }

    /// <summary>A file stream that fails like a full volume once the file reaches <paramref name="limit"/> bytes.</summary>
    private sealed class FullDiskStream(Stream inner, long limit) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            long room = limit - inner.Position;
            if (buffer.Length > room)
            {
                if (room > 0)
                {
                    await inner.WriteAsync(buffer[..(int)room], cancellationToken);
                }

                await inner.FlushAsync(cancellationToken);
                throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
            }

            await inner.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Collects every formatted log line (with exceptions) so tests can prove the token never reaches a log.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => _lines.ToList();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner._lines.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
        }
    }
}
