using System.Net;
using System.Net.Security;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Pki;

namespace MirbadLLM.Orchestration.Agents;

/// <summary>Where a node's agent listens and who it must be.</summary>
/// <param name="NodeId">Node id (SAN <c>spiffe://mirbadllm/node/{id}</c>).</param>
/// <param name="NodeUid">Node uid from the registry (operation references are bound to it).</param>
/// <param name="BaseUri">Agent base address, e.g. <c>https://127.0.0.1:9443/</c> (always ends with <c>/</c>).</param>
internal sealed record AgentNodeEndpoint(string NodeId, string NodeUid, Uri BaseUri)
{
    /// <summary>Absolute URI of an agent route: base + <see cref="Protocol.BasePath"/> + <paramref name="relative"/>.</summary>
    public Uri Resolve(string relative) => new(BaseUri.AbsoluteUri.TrimEnd('/') + Protocol.BasePath + relative, UriKind.Absolute);
}

/// <summary>
/// One generation of a node's two independently owned handlers (design §7.2, review N1): <b>control</b>
/// (<c>MaxConnectionsPerServer = 2</c>, <c>EnableMultipleHttp2Connections = false</c>) and <b>data</b> (16 / true), each
/// owned by its own <see cref="HttpMessageInvoker"/>, both with the same authentication policy: the master client
/// certificate context, the node's <see cref="PeerValidatorSource"/> (read per handshake, so a pending pin applies to the
/// next new connection without a rebuild) and <c>AllowTlsResume = false</c> (<see cref="PkiTls.CreateClientOptions"/>).
/// <para>A generation is replaced on a certificate or trust change (<see cref="IAgentClientFactory.RebuildHandlers"/>);
/// the old one drains: it is disposed when its last in-flight request (including a streamed response body) ends, or after
/// the drain timeout (5 minutes), whichever comes first.</para>
/// </summary>
internal sealed class AgentHandlerGeneration : IAsyncDisposable
{
    private readonly MasterTlsMaterial _material;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ITimer? _drainTimer;
    private int _inFlight;
    private int _draining;
    private int _disposedFlag;

    public AgentHandlerGeneration(
        long id,
        AgentNodeEndpoint endpoint,
        PeerValidatorSource validators,
        MasterTlsMaterial material,
        AgentClientContext context,
        InFlightLimiter dataLimiter)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(validators);
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(context);
        Id = id;
        Endpoint = endpoint;
        Validators = validators;
        _material = material;
        _logger = context.Logger;
        _time = context.Time;

        ControlTracker = new ValidationTracker();
        DataTracker = new ValidationTracker();
        Control = new HttpMessageInvoker(
            new GenerationHandler(this, CreateHandler(AgentChannel.Control, material, validators, ControlTracker), null),
            disposeHandler: true);
        Data = new HttpMessageInvoker(
            new GenerationHandler(this, CreateHandler(AgentChannel.Data, material, validators, DataTracker), () => context.ProtocolVersion(endpoint.NodeId)),
            disposeHandler: true);
        Client = new AgentClient(this, context, dataLimiter);
    }

    /// <summary>Generation number (increases per node; diagnostics and tests).</summary>
    public long Id { get; }

    /// <summary>The node.</summary>
    public AgentNodeEndpoint Endpoint { get; }

    /// <summary>The validator source both handlers read per handshake.</summary>
    public PeerValidatorSource Validators { get; }

    /// <summary>Control channel (status, operations, events, telemetry, PKI, …).</summary>
    public HttpMessageInvoker Control { get; }

    /// <summary>Data channel (proxy, chat, benchmark load, model metrics scrapes).</summary>
    public HttpMessageInvoker Data { get; }

    /// <summary>Validation results seen by the control handler.</summary>
    public ValidationTracker ControlTracker { get; }

    /// <summary>Validation results seen by the data handler.</summary>
    public ValidationTracker DataTracker { get; }

    /// <summary>The typed client of this generation.</summary>
    public AgentClient Client { get; }

    /// <summary>Requests (or streamed response bodies) in flight on either handler.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>True after <see cref="BeginDrain"/>.</summary>
    public bool IsDraining => Volatile.Read(ref _draining) != 0;

    /// <summary>True once the handlers were disposed.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposedFlag) != 0;

    /// <summary>Completes when the handlers were disposed.</summary>
    public Task Disposed => _disposed.Task;

    /// <summary>
    /// The §7.2 handler for one channel. Pool, ping and connect settings per §7.2 (authoritative over §4.1); proxy, cookies,
    /// redirects and decompression off (an <c>HTTPS_PROXY</c> must never carry agent traffic, MN-090).
    /// </summary>
    internal static SocketsHttpHandler CreateHandler(AgentChannel channel, MasterTlsMaterial material, PeerValidatorSource validators, ValidationTracker tracker)
    {
        SslClientAuthenticationOptions ssl = PkiTls.CreateClientOptions(material.Context, validators);

        // Same per-handshake validator, observed: a SecureConnectionError after the validator accepted the agent is
        // "HTTP/2 not negotiated", one after it refused the agent is a certificate problem (design §7.2).
        ssl.RemoteCertificateValidationCallback = tracker.CreateCallback(validators);
        bool data = channel == AgentChannel.Data;
        return new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = AgentClientDefaults.HandlerConnectTimeout,
            PooledConnectionIdleTimeout = AgentClientDefaults.PooledConnectionIdleTimeout,
            KeepAlivePingDelay = AgentClientDefaults.KeepAlivePingDelay,
            KeepAlivePingTimeout = AgentClientDefaults.KeepAlivePingTimeout,
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            EnableMultipleHttp2Connections = data,
            MaxConnectionsPerServer = data ? AgentClientDefaults.DataMaxConnections : AgentClientDefaults.ControlMaxConnections,
            SslOptions = ssl,
        };
    }

    internal void Enter() => Interlocked.Increment(ref _inFlight);

    internal void Exit()
    {
        if (Interlocked.Decrement(ref _inFlight) == 0 && IsDraining)
        {
            DisposeNow();
        }
    }

    /// <summary>Stops handing out this generation: disposes it once idle, or after <paramref name="timeout"/> at the latest.</summary>
    internal void BeginDrain(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _draining, 1) != 0)
        {
            return;
        }

        if (InFlight == 0)
        {
            DisposeNow();
            return;
        }

        _drainTimer = _time.CreateTimer(_ => DisposeNow(), null, timeout, Timeout.InfiniteTimeSpan);
        if (InFlight == 0)
        {
            DisposeNow();
        }
    }

    /// <summary>Disposes both handlers now (idempotent) and releases the TLS material.</summary>
    internal void DisposeNow()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0)
        {
            return;
        }

        _drainTimer?.Dispose();
        Control.Dispose();
        Data.Dispose();
        _material.Release();
        AgentClientLog.GenerationDisposed(_logger, Endpoint.NodeId, Id, InFlight);
        _disposed.TrySetResult();
    }

    public ValueTask DisposeAsync()
    {
        DisposeNow();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Constants of the §7.2 handler configuration and §4.5 policies used by the client.</summary>
public static class AgentClientDefaults
{
    /// <summary>Control handler connections (§7.2).</summary>
    public const int ControlMaxConnections = 2;

    /// <summary>
    /// Data handler <c>MaxConnectionsPerServer</c> (§7.2). Note (MN-021, measured on .NET 10): <c>SocketsHttpHandler</c>
    /// does not apply this limit to HTTP/2 connections when <c>EnableMultipleHttp2Connections</c> is on (a 17th connection
    /// opened under a limit of 16), so the effective bound of the data channel is the per-node in-flight limit
    /// (<see cref="AgentClientFactoryOptions.MaxDataInFlightPerNode"/>) times the agent's streams per connection.
    /// </summary>
    public const int DataMaxConnections = 16;

    /// <summary>Handler connect timeout, TCP + TLS (§7.2). Call totals (§4.5) are enforced per request on top.</summary>
    public static readonly TimeSpan HandlerConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Idle pooled connections are closed after this (§4.1).</summary>
    public static readonly TimeSpan PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>HTTP/2 ping delay (§4.1, §7.2).</summary>
    public static readonly TimeSpan KeepAlivePingDelay = TimeSpan.FromSeconds(30);

    /// <summary>HTTP/2 ping timeout (§4.1, §7.2).</summary>
    public static readonly TimeSpan KeepAlivePingTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Old handlers are disposed at the latest this long after a rebuild (§7.2).</summary>
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromMinutes(5);
}

/// <summary>
/// The innermost delegating handler of a generation: forces HTTP/2 exact on <b>every</b> request (finding 14:
/// <c>DefaultRequestVersion</c> does not apply to <c>SendAsync(HttpRequestMessage)</c>; no silent downgrade), adds the
/// protocol header on the data channel, and counts the request in flight until its response body is disposed.
/// </summary>
internal sealed class GenerationHandler : DelegatingHandler
{
    private readonly AgentHandlerGeneration _generation;
    private readonly Func<int>? _protocolVersion;

    public GenerationHandler(AgentHandlerGeneration generation, HttpMessageHandler inner, Func<int>? protocolVersion)
        : base(inner)
    {
        _generation = generation;
        _protocolVersion = protocolVersion;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Version = HttpVersion.Version20;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        if (_protocolVersion is not null && !request.Headers.Contains(Protocol.ProtocolHeader))
        {
            request.Headers.TryAddWithoutValidation(Protocol.ProtocolHeader, _protocolVersion().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        AddLegacyHeaders(request);

        _generation.Enter();
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _generation.Exit();
            throw;
        }

        AcceptLegacyHeaders(response);
        response.Content = new ReleasingContent(response.Content, _generation.Exit);
        return response;
    }

    /// <summary>A pre-rename agent reads only <c>SparkServe-*</c> headers: send each protocol header under both names.</summary>
    internal static void AddLegacyHeaders(HttpRequestMessage request)
    {
        foreach (var (name, values) in request.Headers.ToList())
        {
            if (Protocol.LegacyHeaderOf(name) is { } legacy && !request.Headers.Contains(legacy))
            {
                request.Headers.TryAddWithoutValidation(legacy, values);
            }
        }
    }

    /// <summary>A pre-rename agent answers with <c>SparkServe-*</c> headers: add them under the current names.</summary>
    internal static void AcceptLegacyHeaders(HttpResponseMessage response)
    {
        foreach (var (name, values) in response.Headers.ToList())
        {
            if (Protocol.CurrentHeaderOf(name) is { } current && !response.Headers.Contains(current))
            {
                response.Headers.TryAddWithoutValidation(current, values);
            }
        }
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Agent requests are asynchronous.");
}

/// <summary>Response content that runs a release action exactly once when it (or its read stream) is disposed.</summary>
internal sealed class ReleasingContent : HttpContent
{
    private readonly HttpContent _inner;
    private Action? _release;

    public ReleasingContent(HttpContent inner, Action release)
    {
        _inner = inner;
        _release = release;
        foreach (var header in inner.Headers)
        {
            Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _inner.CopyToAsync(stream);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
        _inner.CopyToAsync(stream, cancellationToken);

    protected override async Task<Stream> CreateContentReadStreamAsync() =>
        new ReleasingStream(await _inner.ReadAsStreamAsync().ConfigureAwait(false), Release);

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
        new ReleasingStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), Release);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            Release();
        }

        base.Dispose(disposing);
    }

    private void Release() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>A read-only pass-through stream that runs a release action when disposed.</summary>
internal sealed class ReleasingStream : Stream
{
    private readonly Stream _inner;
    private Action? _release;

    public ReleasingStream(Stream inner, Action release)
    {
        _inner = inner;
        _release = release;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
        _inner.CopyToAsync(destination, bufferSize, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        Release();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            Release();
        }

        base.Dispose(disposing);
    }

    private void Release() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>
/// Counts the peer-validation results one handler saw (design §7.2 "Error classification"): the client compares the
/// rejection count before and after a failed request to tell "the master refused the agent's certificate" from
/// "HTTP/2 not negotiated".
/// </summary>
internal sealed class ValidationTracker
{
    private long _accepted;
    private long _rejected;
    private PeerValidationResult? _lastRejection;

    /// <summary>Handshakes whose agent certificate the validator accepted.</summary>
    public long Accepted => Interlocked.Read(ref _accepted);

    /// <summary>Handshakes whose agent certificate the validator refused.</summary>
    public long Rejected => Interlocked.Read(ref _rejected);

    /// <summary>The most recent refusal (public certificate data only).</summary>
    public PeerValidationResult? LastRejection => Volatile.Read(ref _lastRejection);

    /// <summary>The handler callback: the source's current validator, observed.</summary>
    public RemoteCertificateValidationCallback CreateCallback(PeerValidatorSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return (sender, certificate, chain, errors) =>
            source.Current.WithResultObserver(Record).ValidateRemoteCertificate(sender, certificate, chain, errors);
    }

    private void Record(PeerValidationResult result)
    {
        if (result.IsValid)
        {
            Interlocked.Increment(ref _accepted);
            return;
        }

        Volatile.Write(ref _lastRejection, result);
        Interlocked.Increment(ref _rejected);
    }
}

/// <summary>The per-node in-flight bound of proxied requests on the master (design §7.2, default 512).</summary>
internal sealed class InFlightLimiter
{
    private readonly int _limit;
    private int _count;

    public InFlightLimiter(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        _limit = limit;
    }

    /// <summary>Requests currently counted.</summary>
    public int Count => Volatile.Read(ref _count);

    public bool TryEnter()
    {
        if (Interlocked.Increment(ref _count) <= _limit)
        {
            return true;
        }

        Interlocked.Decrement(ref _count);
        return false;
    }

    public void Exit() => Interlocked.Decrement(ref _count);
}
