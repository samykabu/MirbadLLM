using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Pki;

namespace MirbadLLM.UnitTests.Multinode.Contract;

/// <summary>
/// Where the agent under test listens and the credentials to reach it (design §4.1–4.2): an mTLS HTTP/2 endpoint whose
/// certificate is issued by <see cref="Ca"/> for <see cref="NodeId"/>, a master client certificate from the same CA, and
/// the agent's SPKI pin. The suite also signs rotation CSRs (§10.2) with <see cref="Ca"/>.
/// </summary>
/// <param name="BaseAddress">Scheme, host and port only, e.g. <c>https://127.0.0.1:9443/</c>; routes live under <see cref="Protocol.BasePath"/>.</param>
/// <param name="NodeId">The node id in the agent certificate's SAN.</param>
/// <param name="Ca">The master CA (with its key): the agent's only trust anchor.</param>
/// <param name="MasterCertificate">A master client certificate with its private key, TLS-ready (<see cref="IssuedCredential.CreateTlsCertificate"/>).</param>
/// <param name="AgentSpkiPin">Base64 SHA-256 SPKI of the agent's current certificate.</param>
public sealed record AgentContractTarget(Uri BaseAddress, string NodeId, CertificateAuthority Ca, X509Certificate2 MasterCertificate, string AgentSpkiPin);

/// <summary>
/// An agent the shared contract suite runs against (<c>FakeAgent</c>, MN-022; the real agent in-process with fake Docker
/// and hostd, MN-026). A fresh agent per test: empty operation log, no accepted master, the <see cref="AgentContractSeed"/>
/// fixture present. The knobs are the only non-HTTP inputs: every assertion goes through the public protocol.
/// </summary>
public interface IAgentContractHarness : IAsyncDisposable
{
    /// <summary>Address and credentials.</summary>
    AgentContractTarget Target { get; }

    /// <summary>
    /// While held, every instance stays Starting (its health check does not pass); on release they become Ready. Holding
    /// keeps a launch's start reservation in place (design §4.8).
    /// </summary>
    Task SetStartsHeldAsync(bool held);

    /// <summary>While set, instances that start fail before they become Ready (the launch operation ends Failed).</summary>
    Task SetStartsFailingAsync(bool failing);

    /// <summary>While held, image pulls (standalone or inside a launch) do not finish.</summary>
    Task SetPullsHeldAsync(bool held);

    /// <summary>While held, downloads stay Downloading.</summary>
    Task SetDownloadsHeldAsync(bool held);

    /// <summary>Docker answers or not: the node reports <c>docker_unreachable</c> while it does not (MN-047 / D68).</summary>
    Task SetDockerAvailableAsync(bool available);
}

/// <summary>
/// The fixture every agent under test must be seeded with. Memory is read from <c>/status</c>, so it is not fixed here,
/// but the node must have at least 8 GiB available. Instances that become Ready must be served by a vLLM-shaped upstream
/// (<c>FakeModelServer</c>) behind the proxy.
/// </summary>
public static class AgentContractSeed
{
    /// <summary>An approved image that is present on the node.</summary>
    public const string Image = "local/contract-vllm:1";

    /// <summary>The approved digest of <see cref="Image"/>.</summary>
    public const string ImageDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    /// <summary>An approved image that is not present: launching it goes through the <c>pulling</c> stage.</summary>
    public const string PullImage = "local/contract-pull:1";

    /// <summary>The approved digest of <see cref="PullImage"/>.</summary>
    public const string PullImageDigest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";

    /// <summary>A digest that is not approved on the node (design §10.3).</summary>
    public const string UnapprovedDigest = "sha256:3333333333333333333333333333333333333333333333333333333333333333";

    /// <summary>A checkpoint folder with a <c>config.json</c>; launches in this suite reference it.</summary>
    public const string ModelFolder = "contract-model";

    /// <summary>A second checkpoint folder with a <c>config.json</c> that no instance uses (deleted by a test).</summary>
    public const string ScratchFolder = "contract-scratch";

    /// <summary>A Hugging Face repo id the node's downloader can fetch.</summary>
    public const string DownloadRepo = "contract/tiny-model";

    /// <summary>A whitelisted, unprotected systemd unit (name as hostd shows it), initially active.</summary>
    public const string Service = "contract-svc";

    /// <summary>A whitelisted, protected unit, initially active.</summary>
    public const string ProtectedService = "contract-protected";

    /// <summary>A non-model container, initially running.</summary>
    public const string Container = "contract-container";

    /// <summary>The GPU UUID that is NOT on the node (any UUID the node lacks).</summary>
    public const string MissingGpuUuid = "GPU-00000000-0000-0000-0000-00000000dead";
}

/// <summary>Per-request header overrides for negative tests.</summary>
public sealed record AgentRequestOptions
{
    /// <summary>Send no <c>MirbadLLM-Master</c> header on a mutation.</summary>
    public bool OmitMaster { get; init; }

    /// <summary>A raw <c>MirbadLLM-Master</c> value instead of the client's triple.</summary>
    public string? RawMaster { get; init; }

    /// <summary>The <c>MirbadLLM-Request-Time</c> to send instead of now.</summary>
    public DateTimeOffset? RequestTime { get; init; }

    /// <summary>Send no <c>MirbadLLM-Protocol</c> header.</summary>
    public bool OmitProtocol { get; init; }

    /// <summary>A raw <c>MirbadLLM-Protocol</c> value (also sent on routes that need none, such as <c>/info</c>).</summary>
    public string? Protocol { get; init; }
}

/// <summary>A response as the suite sees it.</summary>
/// <param name="Status">HTTP status.</param>
/// <param name="MediaType">Content-Type media type, when any.</param>
/// <param name="Body">The body as text.</param>
public sealed record AgentReply(int Status, string? MediaType, string Body)
{
    /// <summary>Deserializes the body with <see cref="AgentJson.Options"/>.</summary>
    public T Read<T>() =>
        JsonSerializer.Deserialize<T>(Body, AgentJson.Options) ?? throw new InvalidOperationException($"Empty {typeof(T).Name} body.");
}

/// <summary>One SSE event of <c>/events</c>.</summary>
/// <param name="Name">The SSE <c>event:</c> name.</param>
/// <param name="Event">The parsed event.</param>
/// <param name="Json">The raw <c>data:</c> JSON.</param>
public sealed record ReceivedAgentEvent(string Name, AgentEvent Event, string Json);

/// <summary>
/// A master-side HTTP client for the contract suite: mTLS with the master certificate, the agent pinned by SPKI (design
/// §4.2), HTTP/2 only (<c>RequestVersionExact</c>, §7.2), the protocol, master and request-time headers. Deliberately not
/// the production client (MN-021): the suite tests the wire, not a client library.
/// </summary>
public sealed class AgentContractClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly TrustAnchorSet _trust;
    private readonly Uri _baseAddress;

    /// <summary>Creates a client with its own connection pool (a fresh handler always handshakes anew, §10.2).</summary>
    /// <param name="target">The agent.</param>
    /// <param name="pins">SPKI pins to accept; default the target's current pin.</param>
    /// <param name="clientCertificate">Client certificate; default the master certificate. Pass <see cref="NoCertificate"/> for none.</param>
    public AgentContractClient(AgentContractTarget target, IEnumerable<string>? pins = null, X509Certificate2? clientCertificate = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        _baseAddress = target.BaseAddress;
        _trust = TrustAnchorSet.Create([target.Ca.Certificate]);
        var validator = new PeerValidatorSource(PeerValidator.ForAgent(target.NodeId, _trust, pins ?? [target.AgentSpkiPin]));
        X509Certificate2? certificate = ReferenceEquals(clientCertificate, NoCertificate) ? null : clientCertificate ?? target.MasterCertificate;
        var handler = new SocketsHttpHandler
        {
            SslOptions = PkiTls.CreateClientOptions(certificate is null ? null : PkiTls.CreateContext(certificate), validator),
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxResponseDrainSize = 0,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Marker for "send no client certificate".</summary>
    public static X509Certificate2 NoCertificate { get; } = X509CertificateLoader.LoadCertificate(CreateMarkerCertificate());

    /// <summary>The master triple sent on mutations.</summary>
    public MasterTriple Master { get; set; } = new("contract-master", 1, "contract-boot-1");

    /// <summary>The negotiated protocol version sent on every request but <c>/info</c>.</summary>
    public int ProtocolVersion { get; set; } = Protocol.CurrentVersion;

    /// <summary>Sends a catalogue route. <paramref name="path"/> fills the route's parameters in order.</summary>
    public async Task<AgentReply> SendAsync(
        AgentRoute route,
        IReadOnlyList<string> path,
        object? body = null,
        string? query = null,
        AgentRequestOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        using HttpRequestMessage request = Build(route, path, body, query, options, method: null);
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        string text = await response.Content.ReadAsStringAsync(ct);
        return new AgentReply((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, text);
    }

    /// <summary>Sends a request through <c>/proxy/{modelId}/{**path}</c> and returns the response unread (caller disposes).</summary>
    public Task<HttpResponseMessage> ProxyAsync(
        string modelId,
        string path,
        HttpMethod method,
        HttpContent? content = null,
        AgentRequestOptions? options = null,
        CancellationToken ct = default)
    {
        HttpRequestMessage request = Build(AgentRoutes.Proxy, [modelId, path], body: null, query: null, options, method);
        request.Content = content;
        return SendAndDisposeRequestAsync(request, ct);
    }

    /// <summary>Opens <c>/events</c>; the stream is read with <see cref="AgentEventReader.NextAsync"/>.</summary>
    public async Task<AgentEventReader> OpenEventsAsync(string? bootId, long? afterSeq, AgentRequestOptions? options = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (bootId is not null)
        {
            query.Add("bootId=" + Uri.EscapeDataString(bootId));
        }

        if (afterSeq is { } seq)
        {
            query.Add("afterSeq=" + seq.ToString(CultureInfo.InvariantCulture));
        }

        using HttpRequestMessage request = Build(AgentRoutes.Events, [], null, query.Count == 0 ? null : "?" + string.Join('&', query), options, null);
        HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return await AgentEventReader.CreateAsync(response, ct);
    }

    /// <summary>Opens a raw SSE route (logs) and returns the response unread.</summary>
    public Task<HttpResponseMessage> OpenStreamAsync(AgentRoute route, IReadOnlyList<string> path, string? query, CancellationToken ct = default)
    {
        HttpRequestMessage request = Build(route, path, null, query, null, null);
        return SendAndDisposeRequestAsync(request, ct, HttpCompletionOption.ResponseHeadersRead);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _http.Dispose();
        _trust.Dispose();
    }

    private async Task<HttpResponseMessage> SendAndDisposeRequestAsync(
        HttpRequestMessage request,
        CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseHeadersRead)
    {
        using (request)
        {
            return await _http.SendAsync(request, completion, ct);
        }
    }

    private HttpRequestMessage Build(AgentRoute route, IReadOnlyList<string> path, object? body, string? query, AgentRequestOptions? options, HttpMethod? method)
    {
        options ??= new AgentRequestOptions();
        method ??= new HttpMethod(route.Method == AgentHttpMethods.Any ? AgentHttpMethods.Get : route.Method);
        var uri = new Uri(_baseAddress, Protocol.BasePath + route.Expand([.. path]) + (query ?? ""));
        var request = new HttpRequestMessage(method, uri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        if (options.Protocol is not null)
        {
            request.Headers.TryAddWithoutValidation(Protocol.ProtocolHeader, options.Protocol);
        }
        else if (route.RequiresProtocolHeader && !options.OmitProtocol)
        {
            request.Headers.TryAddWithoutValidation(Protocol.ProtocolHeader, ProtocolVersion.ToString(CultureInfo.InvariantCulture));
        }

        if (route.IsMutation)
        {
            if (!options.OmitMaster)
            {
                request.Headers.TryAddWithoutValidation(Protocol.MasterHeader, options.RawMaster ?? Master.ToHeaderValue());
            }

            request.Headers.TryAddWithoutValidation(
                Protocol.RequestTimeHeader, (options.RequestTime ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture));
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), AgentJson.Options), Encoding.UTF8, AgentJson.JsonContentType);
        }

        return request;
    }

    private static byte[] CreateMarkerCertificate()
    {
        using var key = PkiDefaults.CreateKey();
        var request = new CertificateRequest("CN=no-certificate-marker", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        using X509Certificate2 cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return cert.Export(X509ContentType.Cert);
    }
}

/// <summary>Reads <c>text/event-stream</c> frames of <c>/events</c>.</summary>
public sealed class AgentEventReader : IAsyncDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly StreamReader? _reader;

    private AgentEventReader(HttpResponseMessage response, StreamReader? reader, string? errorBody)
    {
        _response = response;
        _reader = reader;
        ErrorBody = errorBody;
    }

    /// <summary>HTTP status.</summary>
    public int Status => (int)_response.StatusCode;

    /// <summary>Content-Type media type.</summary>
    public string? MediaType => _response.Content.Headers.ContentType?.MediaType;

    /// <summary>The body when the status was not 200.</summary>
    public string? ErrorBody { get; }

    internal static async Task<AgentEventReader> CreateAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            return new AgentEventReader(response, null, body);
        }

        Stream stream = await response.Content.ReadAsStreamAsync(ct);
        return new AgentEventReader(response, new StreamReader(stream, Encoding.UTF8), null);
    }

    /// <summary>The next event, or null when the stream ended. Fails the test when nothing arrives within <paramref name="timeout"/>.</summary>
    public async Task<ReceivedAgentEvent?> NextAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_reader is null)
        {
            throw new InvalidOperationException($"The event stream was refused: {Status} {ErrorBody}");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        string? name = null;
        var data = new StringBuilder();
        try
        {
            while (true)
            {
                string? line = await _reader.ReadLineAsync(cts.Token);
                if (line is null)
                {
                    return null;
                }

                if (line.Length == 0)
                {
                    if (data.Length == 0)
                    {
                        continue;
                    }

                    string json = data.ToString();
                    var evt = JsonSerializer.Deserialize<AgentEvent>(json, AgentJson.Options)
                        ?? throw new InvalidOperationException("Null event.");
                    return new ReceivedAgentEvent(name ?? "message", evt, json);
                }

                if (line.StartsWith(':'))
                {
                    continue;
                }

                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    name = line["event:".Length..].Trim();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(line["data:".Length..].TrimStart());
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Assert.Fail($"No event within {timeout.TotalSeconds:F0} s.");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // The agent closed (or aborted) the stream.
            return null;
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _reader?.Dispose();
        _response.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The shared agent contract suite (design §13 item 2, plan §3.7): the protocol of design §4 asserted over HTTP only, so
/// the same tests run against <c>FakeAgent</c> (MN-022) and against the real agent hosted in-process (MN-026). Anything
/// asserted here must hold for the real agent: when the two disagree, one of them is wrong about the design.
/// </summary>
public abstract class AgentContractSuite : IAsyncLifetime
{
    /// <summary>How long the suite waits for asynchronous progress (operations, instances, events).</summary>
    protected static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private const string HostContainerPrefix = "ss-";

    private static readonly string[] SpecLabelKeys = [ContainerLabels.ServedName, ContainerLabels.ConfigHash, ContainerLabels.ExecutionFingerprint, ContainerLabels.BudgetGiB];

    private IAgentContractHarness? _harness;
    private AgentContractClient? _agent;

    /// <summary>The agent under test.</summary>
    protected IAgentContractHarness Harness => _harness ?? throw new InvalidOperationException("Not initialized.");

    /// <summary>Its address and credentials.</summary>
    protected AgentContractTarget Target => Harness.Target;

    /// <summary>The master client (epoch 1 by default).</summary>
    protected AgentContractClient Agent => _agent ?? throw new InvalidOperationException("Not initialized.");

    /// <summary>The test's cancellation token.</summary>
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Starts a fresh agent for one test (empty state, seeded fixture).</summary>
    protected abstract Task<IAgentContractHarness> StartAgentAsync();

    /// <summary>
    /// Starts a second agent of the kind under test that offers none of the host features (<c>services.systemd</c>,
    /// <c>services.docker</c>, <c>backup-mount</c>, <c>gpu-processes</c>), so the feature gate is exercised on every route
    /// that needs one (W4 review MN-096: the agent under test offers every P1 feature, which left nothing to check).
    /// </summary>
    protected abstract Task<IAgentContractHarness> StartAgentWithoutHostFeaturesAsync();

    /// <summary>
    /// False when the agent under test cannot select GPUs by UUID yet: the P1 container driver answers
    /// <c>spec_rejected</c> for any selection but <c>all</c> (D47) until MN-106 (W2 review MN-092 item 7).
    /// </summary>
    protected virtual bool SupportsGpuSelectionByUuid => true;

    /// <summary>
    /// False when the agent under test refuses launch environment variables: the P1 container driver answers
    /// <c>spec_rejected</c> for a non-empty <c>env</c>/<c>secretEnvNames</c> (D47) and reports neither env nor mounts in
    /// <c>/launch-config</c> until MN-107.
    /// </summary>
    protected virtual bool SupportsLaunchEnvironment => true;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        _harness = await StartAgentAsync();
        _agent = new AgentContractClient(_harness.Target);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _agent?.Dispose();
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    // =====================================================================================================
    // Identity, negotiation, error shape, transport (design §3.6, §4.1–4.3)
    // =====================================================================================================

    [Fact]
    public async Task InfoAnswersIdentityWithoutAProtocolHeader()
    {
        var reply = await Agent.SendAsync(AgentRoutes.Info, [], ct: Ct);

        Assert.Equal(200, reply.Status);
        var info = reply.Read<InfoResponse>();
        Assert.Equal(Target.NodeId, info.NodeId);
        Assert.False(string.IsNullOrWhiteSpace(info.NodeUid));
        Assert.False(string.IsNullOrWhiteSpace(info.AgentVersion));
        Assert.False(string.IsNullOrWhiteSpace(info.BootId));
        Assert.True(info.Protocol.Min >= 1 && info.Protocol.Min <= info.Protocol.Current);
        Assert.NotNull(Protocol.Negotiate(Protocol.Range, info.Protocol));
        Assert.True(Math.Abs((info.Time - DateTimeOffset.UtcNow).TotalSeconds) < 60, "The agent clock is reported.");
        Assert.True(info.StartedAt <= info.Time);
        Assert.Null(info.AcceptedMaster);
    }

    [Fact]
    public async Task InfoIgnoresAnUnsupportedProtocolHeader()
    {
        var reply = await Agent.SendAsync(AgentRoutes.Info, [], options: new AgentRequestOptions { Protocol = "999" }, ct: Ct);

        Assert.Equal(200, reply.Status);
    }

    [Fact]
    public async Task CapabilitiesAgreeWithInfo()
    {
        var info = await InfoAsync();
        var reply = await Agent.SendAsync(AgentRoutes.Capabilities, [], ct: Ct);

        Assert.Equal(200, reply.Status);
        var caps = reply.Read<NodeCapabilities>();
        Assert.Equal(info.Protocol, caps.Protocol);
        Assert.Equal(info.AgentVersion, caps.AgentVersion);
        Assert.True(caps.Memory.TotalGiB > 0);
        Assert.NotNull(caps.Features);
    }

    [Fact]
    public async Task EveryRouteButInfoRefusesAnUnsupportedProtocolVersion()
    {
        foreach (var route in AgentRoutes.All.Where(r => r.RequiresProtocolHeader))
        {
            var options = new AgentRequestOptions { Protocol = "999" };
            if (route == AgentRoutes.Proxy)
            {
                using var response = await Agent.ProxyAsync("contract-none", "health", HttpMethod.Get, options: options, ct: Ct);
                AssertError(await ToReplyAsync(response), route, 400, ErrorCodes.ProtocolMismatch);
                continue;
            }

            var reply = await Agent.SendAsync(route, SamplePath(route), SampleBody(route), SampleQuery(route), options, Ct);
            AssertError(reply, route, 400, ErrorCodes.ProtocolMismatch);
        }
    }

    [Fact]
    public async Task ReadRoutesAnswerTheirResponseTypes()
    {
        var caps = await CapabilitiesAsync();
        (AgentRoute Route, string? Query)[] reads =
        [
            (AgentRoutes.Info, null), (AgentRoutes.Capabilities, null), (AgentRoutes.Status, null),
            (AgentRoutes.Telemetry, null), (AgentRoutes.GpuIdle, "?seconds=1"),
            (AgentRoutes.RuntimesList, null), (AgentRoutes.ImagesList, null), (AgentRoutes.ModelFoldersList, null),
            (AgentRoutes.DownloadsList, null), (AgentRoutes.OperationsList, null), (AgentRoutes.OperationsList, "?state=active"),
            (AgentRoutes.HostServicesList, null), (AgentRoutes.HostServicesList, "?include=all"), (AgentRoutes.HostGpuProcesses, null),
            (AgentRoutes.BackupMountGet, null), (AgentRoutes.PkiTrustGet, null),
        ];

        foreach (var (route, query) in reads)
        {
            if (!route.IsOfferedBy(caps.Features))
            {
                continue;
            }

            var reply = await Agent.SendAsync(route, [], query: query, ct: Ct);
            Assert.True(reply.Status == 200, $"{route.Name}{query}: {reply.Status} {reply.Body}");
            Assert.Equal(AgentJson.JsonContentType, reply.MediaType);
            Assert.NotNull(JsonSerializer.Deserialize(reply.Body, route.ResponseType, AgentJson.Options));
        }
    }

    [Fact]
    public async Task ErrorsUseTheAgentErrorShape()
    {
        var reply = await Agent.SendAsync(AgentRoutes.RuntimeGet, ["contract-absent"], ct: Ct);

        AssertError(reply, AgentRoutes.RuntimeGet, 404, ErrorCodes.NotFound);
        using var doc = JsonDocument.Parse(reply.Body);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.Equal(["error"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal(JsonValueKind.String, error.GetProperty("code").ValueKind);
        Assert.Equal(JsonValueKind.String, error.GetProperty("message").ValueKind);
    }

    [Fact]
    public async Task AClientWithoutACertificateIsRefused()
    {
        using var anonymous = new AgentContractClient(Target, clientCertificate: AgentContractClient.NoCertificate);

        await AssertRefusedAsync(anonymous);
    }

    [Fact]
    public async Task AClientFromAnotherCaIsRefused()
    {
        using var rogueCa = CertificateAuthority.Create();
        using var rogueMaster = rogueCa.IssueMasterClientCertificate();
        using var rogueTls = rogueMaster.CreateTlsCertificate();
        using var rogue = new AgentContractClient(Target, clientCertificate: rogueTls);

        await AssertRefusedAsync(rogue);
    }

    // =====================================================================================================
    // Fencing and request freshness (design §4.2)
    // =====================================================================================================

    [Fact]
    public async Task TheFirstMasterIsAcceptedAndReportedByInfo()
    {
        await AcceptedAsync(AgentRoutes.RuntimeStop, Stop(), "contract-a");

        var info = await InfoAsync();
        Assert.Equal(Agent.Master, info.AcceptedMaster);
    }

    [Fact]
    public async Task ALowerEpochIsAStaleMaster()
    {
        Agent.Master = new MasterTriple("contract-master", 5, "boot-5");
        await AcceptedAsync(AgentRoutes.RuntimeStop, Stop(), "contract-a");

        Agent.Master = new MasterTriple("contract-master", 4, "boot-4");
        var reply = await Agent.SendAsync(AgentRoutes.RuntimeStop, ["contract-a"], Stop(), ct: Ct);

        AssertError(reply, AgentRoutes.RuntimeStop, 409, ErrorCodes.StaleMaster);
        Assert.Equal(new MasterTriple("contract-master", 5, "boot-5"), (await InfoAsync()).AcceptedMaster);
    }

    [Theory]
    [InlineData("contract-master", "other-boot")]
    [InlineData("other-master", "boot-5")]
    public async Task AnEqualEpochFromAnotherInstanceOrBootIsACompetingMaster(string instanceId, string bootId)
    {
        Agent.Master = new MasterTriple("contract-master", 5, "boot-5");
        await AcceptedAsync(AgentRoutes.RuntimeStop, Stop(), "contract-a");

        Agent.Master = new MasterTriple(instanceId, 5, bootId);
        var reply = await Agent.SendAsync(AgentRoutes.RuntimeStop, ["contract-a"], Stop(), ct: Ct);

        AssertError(reply, AgentRoutes.RuntimeStop, 409, ErrorCodes.CompetingMaster);
    }

    [Fact]
    public async Task AHigherEpochIsAcceptedAndSupersedesTheOldMaster()
    {
        var first = Agent.Master;
        await AcceptedAsync(AgentRoutes.RuntimeStop, Stop(), "contract-a");

        var second = new MasterTriple("another-master", first.Epoch + 1, "boot-new");
        Agent.Master = second;
        await AcceptedAsync(AgentRoutes.RuntimeStop, Stop(), "contract-a");
        Assert.Equal(second, (await InfoAsync()).AcceptedMaster);

        Agent.Master = first;
        var reply = await Agent.SendAsync(AgentRoutes.RuntimeStop, ["contract-a"], Stop(), ct: Ct);
        AssertError(reply, AgentRoutes.RuntimeStop, 409, ErrorCodes.StaleMaster);
    }

    [Fact]
    public async Task EveryMutationRouteIsFenced()
    {
        var caps = await CapabilitiesAsync();
        var current = Agent.Master with { Epoch = 7 };
        Agent.Master = current;
        await AcceptedAsync(AgentRoutes.RuntimeStop, Stop(), "contract-a");

        Agent.Master = current with { Epoch = 6 };
        foreach (var route in AgentRoutes.All.Where(r => r.IsMutation))
        {
            if (!route.IsOfferedBy(caps.Features))
            {
                continue;
            }

            var reply = await Agent.SendAsync(route, SamplePath(route), SampleBody(route), ct: Ct);
            AssertError(reply, route, 409, ErrorCodes.StaleMaster);
        }
    }

    [Fact]
    public async Task AMutationWithoutAValidMasterHeaderIsInvalid()
    {
        var missing = await Agent.SendAsync(AgentRoutes.RuntimeStop, ["contract-a"], Stop(), options: new AgentRequestOptions { OmitMaster = true }, ct: Ct);
        AssertError(missing, AgentRoutes.RuntimeStop, 400, ErrorCodes.InvalidRequest);

        var malformed = await Agent.SendAsync(AgentRoutes.RuntimeStop, ["contract-a"], Stop(), options: new AgentRequestOptions { RawMaster = "no-epoch-here" }, ct: Ct);
        AssertError(malformed, AgentRoutes.RuntimeStop, 400, ErrorCodes.InvalidRequest);

        Assert.Null((await InfoAsync()).AcceptedMaster);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(-6)]
    public async Task ARequestTimeSkewedByMoreThanFiveMinutesIsRefused(int minutes)
    {
        var reply = await Agent.SendAsync(
            AgentRoutes.RuntimeStop, ["contract-a"], Stop(),
            options: new AgentRequestOptions { RequestTime = DateTimeOffset.UtcNow.AddMinutes(minutes) }, ct: Ct);

        AssertError(reply, AgentRoutes.RuntimeStop, 400, ErrorCodes.ClockSkew);
    }

    [Fact]
    public async Task ASupersededOperationStopsAtItsNextDestructiveStage()
    {
        await Harness.SetPullsHeldAsync(true);
        var launch = LaunchFor("contract-a", image: AgentContractSeed.PullImage, digest: AgentContractSeed.PullImageDigest);
        var accepted = await AcceptedAsync(AgentRoutes.RuntimeLaunch, launch, "contract-a");

        Agent.Master = Agent.Master with { Epoch = Agent.Master.Epoch + 1, BootId = "newer-boot" };
        await AcceptedAsync(AgentRoutes.RuntimeStop, Stop(), "contract-b");
        await Harness.SetPullsHeldAsync(false);

        var final = await WaitOperationAsync(accepted.Id);
        Assert.Equal(OperationState.Failed, final.State);
        Assert.Equal(ErrorCodes.Superseded, final.Error?.Code);
        Assert.Null(await GetRuntimeAsync("contract-a"));
        await EventuallyAsync(async () => (await StatusAsync()).Reservations.Count == 0 ? "" : null, "the reservation to be released");
    }

    // =====================================================================================================
    // Envelope, idempotency, expiry (design §4.3 envelope, §4.4 items 1, 2, 6)
    // =====================================================================================================

    [Fact]
    public async Task ARepeatedOperationIdReturnsTheStoredOperation()
    {
        var stop = Stop();
        var first = await AcceptedAsync(AgentRoutes.RuntimeStop, stop, "contract-a");
        await WaitOperationAsync(first.Id);
        var second = await AcceptedAsync(AgentRoutes.RuntimeStop, stop, "contract-a");

        Assert.Equal(stop.OperationId, first.Id);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(OperationKinds.Stop, second.Kind);
        Assert.Equal("contract-a", second.Target);
        Assert.Equal(first.PayloadDigest, second.PayloadDigest);
        // The master's operation reference digest is the agent's (one canonical form, W2 review D38).
        Assert.Equal(OperationPayload.ComputeRequestDigest(stop, typeof(StopRequest)), second.PayloadDigest);
        Assert.Equal(first.StartedAt, second.StartedAt);
        Assert.Equal(stop.IssuedAt, second.IssuedAt);
        Assert.Equal(stop.ExpiresAt, second.ExpiresAt);
        var all = (await Agent.SendAsync(AgentRoutes.OperationsList, [], ct: Ct)).Read<List<OperationStatus>>();
        Assert.Single(all, o => o.Id == stop.OperationId);
    }

    [Fact]
    public async Task ARepeatedLaunchCreatesOneInstance()
    {
        var launch = LaunchFor("contract-a");
        var first = await AcceptedAsync(AgentRoutes.RuntimeLaunch, launch, "contract-a");
        var again = await AcceptedAsync(AgentRoutes.RuntimeLaunch, launch, "contract-a");
        Assert.Equal(first.Id, again.Id);

        await WaitOperationAsync(first.Id);
        var ready = await WaitRuntimeAsync("contract-a", ModelState.Ready);
        var third = await AcceptedAsync(AgentRoutes.RuntimeLaunch, launch, "contract-a");

        Assert.Equal(OperationState.Succeeded, third.State);
        Assert.Equal(ready.InstanceId, (await GetRuntimeAsync("contract-a"))?.InstanceId);
        Assert.Single(await ListRuntimesAsync(), r => r.ModelId == "contract-a");
        Assert.Equal(launch.OperationId, ready.OperationId);
    }

    [Fact]
    public async Task TheSameIdWithAnotherPayloadIsAConflict()
    {
        var launch = LaunchFor("contract-a");
        await AcceptedAsync(AgentRoutes.RuntimeLaunch, launch, "contract-a");

        var changed = launch with { Spec = launch.Spec with { Argv = [.. launch.Spec.Argv, "--max-model-len", "4096"] } };
        var reply = await Agent.SendAsync(AgentRoutes.RuntimeLaunch, ["contract-a"], changed, ct: Ct);

        AssertError(reply, AgentRoutes.RuntimeLaunch, 409, ErrorCodes.OperationIdConflict);
    }

    [Fact]
    public async Task TheSameIdWithAChangedExpiryIsAConflict()
    {
        var stop = Stop();
        await AcceptedAsync(AgentRoutes.RuntimeStop, stop, "contract-a");

        var reply = await Agent.SendAsync(AgentRoutes.RuntimeStop, ["contract-a"], stop with { ExpiresAt = stop.ExpiresAt.AddMinutes(1) }, ct: Ct);

        AssertError(reply, AgentRoutes.RuntimeStop, 409, ErrorCodes.OperationIdConflict);
    }

    [Fact]
    public async Task TheSameIdForAnotherTargetOrKindIsAConflict()
    {
        var stop = Stop();
        await AcceptedAsync(AgentRoutes.RuntimeStop, stop, "contract-a");

        var otherTarget = await Agent.SendAsync(AgentRoutes.RuntimeStop, ["contract-b"], stop, ct: Ct);
        AssertError(otherTarget, AgentRoutes.RuntimeStop, 409, ErrorCodes.OperationIdConflict);

        var otherKind = await Agent.SendAsync(
            AgentRoutes.OperationCancel, ["contract-unknown-op"],
            new OperationCancelRequest { OperationId = stop.OperationId, IssuedAt = stop.IssuedAt, ExpiresAt = stop.ExpiresAt }, ct: Ct);
        AssertError(otherKind, AgentRoutes.OperationCancel, 409, ErrorCodes.OperationIdConflict);
    }

    [Fact]
    public async Task AnExpiredEnvelopeOfAnUnknownIdIsGoneAndNothingRuns()
    {
        var now = DateTimeOffset.UtcNow;
        var launch = LaunchFor("contract-a") with { IssuedAt = now.AddHours(-25), ExpiresAt = now.AddHours(-1) };

        var reply = await Agent.SendAsync(AgentRoutes.RuntimeLaunch, ["contract-a"], launch, ct: Ct);

        AssertError(reply, AgentRoutes.RuntimeLaunch, 410, ErrorCodes.OperationExpired);
        Assert.Null(await GetOperationAsync(launch.OperationId));
        Assert.Null(await GetRuntimeAsync("contract-a"));
        Assert.Empty((await StatusAsync()).Reservations);
    }

    [Fact]
    public async Task AnIssuedAtFarInTheFutureIsRefused()
    {
        var now = DateTimeOffset.UtcNow;
        var stop = Stop() with { IssuedAt = now.AddMinutes(10), ExpiresAt = now.AddMinutes(10).AddHours(24) };

        var reply = await Agent.SendAsync(AgentRoutes.RuntimeStop, ["contract-a"], stop, ct: Ct);

        AssertError(reply, AgentRoutes.RuntimeStop, 400, ErrorCodes.ClockSkew);
        Assert.Null(await GetOperationAsync(stop.OperationId));
    }

    [Fact]
    public async Task SecretsAreNeitherStoredNorPartOfTheDigest()
    {
        await RequireFeatureAsync(AgentFeatures.DownloadsHfContainer);
        await Harness.SetDownloadsHeldAsync(true);
        const string token = "hf_contractSecretToken123";
        var download = Download("contract-secret") with { Token = token };

        var reply = await Agent.SendAsync(AgentRoutes.DownloadStart, [], download, ct: Ct);
        Assert.Equal(AgentRoutes.DownloadStart.SuccessStatus, reply.Status);
        Assert.DoesNotContain(token, reply.Body, StringComparison.Ordinal);

        var op = await GetOperationAsync(download.OperationId);
        Assert.NotNull(op);
        var opReply = await Agent.SendAsync(AgentRoutes.OperationGet, [download.OperationId], ct: Ct);
        Assert.DoesNotContain(token, opReply.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(token, (await Agent.SendAsync(AgentRoutes.Status, [], ct: Ct)).Body, StringComparison.Ordinal);

        // The digest is computed after redaction: a retry with another token is the same operation, not a conflict.
        var retry = await Agent.SendAsync(AgentRoutes.DownloadStart, [], download with { Token = "hf_anotherToken" }, ct: Ct);
        Assert.Equal(AgentRoutes.DownloadStart.SuccessStatus, retry.Status);
        Assert.Equal(download.OperationId, retry.Read<NodeDownloadStatus>().OperationId);
        Assert.Equal(op.PayloadDigest, (await GetOperationAsync(download.OperationId))?.PayloadDigest);
        Assert.Equal(OperationPayload.ComputeRequestDigest(download, typeof(DownloadStartRequest)), op.PayloadDigest);
        await Harness.SetDownloadsHeldAsync(false);
    }

    // =====================================================================================================
    // Launch, modes, admission, reservations (design §4.3, §4.8, §4.9, §10.3)
    // =====================================================================================================

    [Fact]
    public async Task ALaunchCreatesALabelledInstanceThatBecomesReady()
    {
        var launch = LaunchFor("contract-a");
        var accepted = await AcceptedAsync(AgentRoutes.RuntimeLaunch, launch, "contract-a");

        Assert.Equal(launch.OperationId, accepted.Id);
        Assert.Equal(OperationKinds.Launch, accepted.Kind);
        Assert.Equal("contract-a", accepted.Target);
        Assert.Equal(OperationPayload.ComputeRequestDigest(launch, typeof(LaunchRequest)), accepted.PayloadDigest);
        Assert.False(accepted.State.IsTerminal() && accepted.State != OperationState.Succeeded);

        var final = await WaitOperationAsync(accepted.Id);
        Assert.Equal(OperationState.Succeeded, final.State);
        Assert.NotNull(final.EndedAt);
        var result = final.ReadResult<RuntimeStatus>();
        Assert.NotNull(result);
        Assert.Equal("contract-a", result.ModelId);

        var runtime = await WaitRuntimeAsync("contract-a", ModelState.Ready);
        Assert.Equal(result.InstanceId, runtime.InstanceId);
        Assert.Equal(launch.OperationId, runtime.OperationId);
        Assert.True(runtime.HostPort > 0);
        Assert.Equal("contract-a", runtime.Labels[ContainerLabels.Model]);
        Assert.Equal(Target.NodeId, runtime.Labels[ContainerLabels.Node]);
        Assert.Equal(launch.OperationId, runtime.Labels[ContainerLabels.Operation]);
        foreach (string key in SpecLabelKeys)
        {
            Assert.Equal(launch.Spec.Labels[key], runtime.Labels[key]);
        }

        Assert.Equal(launch.ExpectedConfigHash, runtime.ConfigHash);
        Assert.Equal(launch.ExpectedExecutionFingerprint, runtime.ExecutionFingerprint);
        Assert.Equal("contract-a", runtime.ServedName);
        Assert.False(runtime.Tuning);
        await EventuallyAsync(async () => (await StatusAsync()).Reservations.Count == 0 ? "" : null, "the reservation to be released on Ready");
    }

    [Fact]
    public async Task LaunchConfigReflectsTheSpecButNeverSecretValues()
    {
        if (!SupportsLaunchEnvironment)
        {
            Assert.Skip("Launch environment variables and mounts in /launch-config come with MN-107 (P1 driver: spec_rejected, D47).");
        }

        var launch = LaunchFor(
            "contract-a",
            env: new Dictionary<string, string> { ["VLLM_LOGGING_LEVEL"] = "INFO", ["HF_TOKEN"] = "hf_launchSecret" },
            secretEnvNames: ["HF_TOKEN"]);
        await LaunchReadyAsync(launch);

        var reply = await Agent.SendAsync(AgentRoutes.RuntimeLaunchConfig, ["contract-a"], ct: Ct);

        Assert.Equal(200, reply.Status);
        Assert.DoesNotContain("hf_launchSecret", reply.Body, StringComparison.Ordinal);
        var config = reply.Read<LaunchConfig>();
        Assert.Equal(launch.Spec.Argv, config.Argv);
        Assert.Equal(AgentContractSeed.Image, config.Image);
        Assert.Equal(AgentContractSeed.ImageDigest, config.ImageDigest);
        Assert.Equal("INFO", config.Env["VLLM_LOGGING_LEVEL"]);
        Assert.True(config.Env.ContainsKey("HF_TOKEN"));
        Assert.Null(config.Env["HF_TOKEN"]);
        Assert.Equal(launch.Spec.Mounts, config.Mounts);
        Assert.Equal(launch.ExpectedExecutionFingerprint, config.ExecutionFingerprint);
        Assert.Equal(launch.OperationId, config.Labels[ContainerLabels.Operation]);
        Assert.NotNull(config.CreatedAt);
        Assert.DoesNotContain("hf_launchSecret", (await Agent.SendAsync(AgentRoutes.OperationGet, [launch.OperationId], ct: Ct)).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureStartedReturnsTheRunningInstanceWhateverItsConfiguration()
    {
        var ready = await LaunchReadyAsync(LaunchFor("contract-a"));

        var different = LaunchFor("contract-a", extraArgs: ["--max-model-len", "2048"]);
        var accepted = await AcceptedAsync(AgentRoutes.RuntimeLaunch, different, "contract-a");
        var final = await WaitOperationAsync(accepted.Id);

        Assert.Equal(OperationState.Succeeded, final.State);
        Assert.Equal(ready.InstanceId, final.ReadResult<RuntimeStatus>()?.InstanceId);
        var runtime = await GetRuntimeAsync("contract-a");
        Assert.Equal(ready.InstanceId, runtime?.InstanceId);
        Assert.Equal(ModelState.Ready, runtime?.State);
        var config = (await Agent.SendAsync(AgentRoutes.RuntimeLaunchConfig, ["contract-a"], ct: Ct)).Read<LaunchConfig>();
        Assert.DoesNotContain("2048", config.Argv);
    }

    [Fact]
    public async Task ReplaceAlwaysRecreatesTheInstance()
    {
        var first = LaunchFor("contract-a");
        var ready = await LaunchReadyAsync(first);

        var replace = LaunchFor("contract-a", mode: LaunchMode.Replace);
        var accepted = await AcceptedAsync(AgentRoutes.RuntimeLaunch, replace, "contract-a");
        var final = await WaitOperationAsync(accepted.Id);

        Assert.Equal(OperationState.Succeeded, final.State);
        var replaced = await WaitRuntimeAsync("contract-a", ModelState.Ready);
        Assert.NotEqual(ready.InstanceId, replaced.InstanceId);
        Assert.Equal(replace.OperationId, replaced.OperationId);
        Assert.Single(await ListRuntimesAsync(), r => r.ModelId == "contract-a");
    }

    [Fact]
    public async Task EnsureStartedRecreatesAFailedInstance()
    {
        await Harness.SetStartsFailingAsync(true);
        var failing = await AcceptedAsync(AgentRoutes.RuntimeLaunch, LaunchFor("contract-a"), "contract-a");
        var broken = await WaitRuntimeAsync("contract-a", ModelState.Failed);
        Assert.False(string.IsNullOrWhiteSpace(broken.Error));
        Assert.True((await WaitOperationAsync(failing.Id)).State.IsTerminal());
        await EventuallyAsync(async () => (await StatusAsync()).Reservations.Count == 0 ? "" : null, "the reservation to be released on failure");

        await Harness.SetStartsFailingAsync(false);
        var recovered = await LaunchReadyAsync(LaunchFor("contract-a"));

        Assert.NotEqual(broken.InstanceId, recovered.InstanceId);
    }

    [Fact]
    public async Task AnAdmissionRefusalCarriesTheNumbers()
    {
        var memory = (await StatusAsync()).Memory;
        double budget = Math.Ceiling(memory.HostTotalGiB) + 100;

        var reply = await Agent.SendAsync(AgentRoutes.RuntimeLaunch, ["contract-a"], LaunchFor("contract-a", budgetGiB: budget, reserveGiB: 2), ct: Ct);

        var error = AssertError(reply, AgentRoutes.RuntimeLaunch, 409, ErrorCodes.AdmissionRefused);
        var details = error.ReadDetails<AdmissionRefusedDetails>();
        Assert.NotNull(details);
        Assert.Equal(budget, details.NeedGiB, 3);
        Assert.Equal(2, details.ReserveGiB, 3);
        Assert.True(details.AvailableGiB <= memory.HostTotalGiB);
        Assert.True(details.FreeGiB < budget);
        Assert.StartsWith("Model 'contract-a' needs ", error.Message, StringComparison.Ordinal);
        Assert.Contains($"on node {Target.NodeId}", error.Message, StringComparison.Ordinal);
        Assert.Null(await GetRuntimeAsync("contract-a"));
        Assert.Empty((await StatusAsync()).Reservations);
    }

    [Fact]
    public async Task ForceSkipsMemoryAdmission()
    {
        await Harness.SetStartsHeldAsync(true);
        var memory = (await StatusAsync()).Memory;
        double budget = Math.Ceiling(memory.HostTotalGiB) + 100;

        var accepted = await AcceptedAsync(AgentRoutes.RuntimeLaunch, LaunchFor("contract-a", budgetGiB: budget, force: true), "contract-a");

        var reservation = Assert.Single((await StatusAsync()).Reservations);
        Assert.Equal(accepted.Id, reservation.OperationId);
        Assert.Equal(budget, reservation.BudgetGiB, 3);
        await Harness.SetStartsHeldAsync(false);
        await WaitOperationAsync(accepted.Id);
    }

    [Fact]
    public async Task ASecondStartWhileAReservationIsHeldIsAStartConflict()
    {
        await Harness.SetStartsHeldAsync(true);
        var first = await AcceptedAsync(AgentRoutes.RuntimeLaunch, LaunchFor("contract-a", budgetGiB: 2), "contract-a");
        await WaitRuntimeAsync("contract-a", ModelState.Starting);

        var snapshot = await StatusAsync();
        var held = Assert.Single(snapshot.Reservations);
        Assert.Equal(first.Id, held.OperationId);
        Assert.Equal("contract-a", held.ModelId);
        Assert.Equal(2, held.BudgetGiB, 3);
        Assert.Contains(snapshot.Operations, o => o.Id == first.Id);

        var conflict = await Agent.SendAsync(AgentRoutes.RuntimeLaunch, ["contract-b"], LaunchFor("contract-b", force: true), ct: Ct);
        var error = AssertError(conflict, AgentRoutes.RuntimeLaunch, 409, ErrorCodes.StartConflict);
        Assert.Equal("contract-a", error.ReadDetails<StartConflictDetails>()?.StartingModelId);
        Assert.Contains($"on node {Target.NodeId}", error.Message, StringComparison.Ordinal);

        await Harness.SetStartsHeldAsync(false);
        await WaitRuntimeAsync("contract-a", ModelState.Ready);
        await EventuallyAsync(async () => (await StatusAsync()).Reservations.Count == 0 ? "" : null, "the reservation to be released on Ready");
        await LaunchReadyAsync(LaunchFor("contract-b"));
    }

    [Fact]
    public async Task ConcurrentLaunchesOnAUnifiedNodeAdmitExactlyOne()
    {
        var caps = await CapabilitiesAsync();
        if (caps.Memory.Model != MemoryModel.Unified)
        {
            Assert.Skip("Unified-node start rule.");
        }

        await Harness.SetStartsHeldAsync(true);
        var launches = Enumerable.Range(0, 8).Select(i => (Model: $"contract-c{i}", Request: LaunchFor($"contract-c{i}"))).ToList();

        var replies = await Task.WhenAll(launches.Select(l => Agent.SendAsync(AgentRoutes.RuntimeLaunch, [l.Model], l.Request, ct: Ct)));

        Assert.Single(replies, r => r.Status == 202);
        foreach (var refused in replies.Where(r => r.Status != 202))
        {
            AssertError(refused, AgentRoutes.RuntimeLaunch, 409, ErrorCodes.StartConflict);
        }

        Assert.Single((await StatusAsync()).Reservations);
        await Harness.SetStartsHeldAsync(false);
    }

    [Theory]
    [InlineData("reservedLabel")]
    [InlineData("unapprovedDigest")]
    [InlineData("unknownVolume")]
    [InlineData("wrongTarget")]
    [InlineData("subPathTraversal")]
    [InlineData("absoluteSubPath")]
    public async Task ASpecBreakingAGuardrailIsRejected(string guardrail)
    {
        var launch = LaunchFor("contract-a");
        MountSpec models = launch.Spec.Mounts[0];
        launch = guardrail switch
        {
            "reservedLabel" => launch with
            {
                Spec = launch.Spec with { Labels = new Dictionary<string, string>(launch.Spec.Labels) { [ContainerLabels.Model] = "spoofed" } },
            },
            "unapprovedDigest" => launch with { Spec = launch.Spec with { ImageDigest = AgentContractSeed.UnapprovedDigest } },
            "unknownVolume" => launch with { Spec = launch.Spec with { Mounts = [models with { Volume = "rootfs" }] } },
            "wrongTarget" => launch with { Spec = launch.Spec with { Mounts = [models with { Target = "/etc" }] } },
            "subPathTraversal" => launch with { Spec = launch.Spec with { Mounts = [models with { SubPath = "../outside" }] } },
            "absoluteSubPath" => launch with { Spec = launch.Spec with { Mounts = [models with { SubPath = "/etc" }] } },
            _ => throw new ArgumentOutOfRangeException(nameof(guardrail)),
        };

        var reply = await Agent.SendAsync(AgentRoutes.RuntimeLaunch, ["contract-a"], launch, ct: Ct);

        var error = AssertError(reply, AgentRoutes.RuntimeLaunch, 422, ErrorCodes.SpecRejected);
        Assert.False(string.IsNullOrWhiteSpace(error.ReadDetails<SpecRejectedDetails>()?.Reason));
        Assert.Null(await GetRuntimeAsync("contract-a"));
        Assert.Empty((await StatusAsync()).Reservations);
    }

    [Fact]
    public async Task AGpuThatIsNotOnTheNodeIsRefused()
    {
        if (!SupportsGpuSelectionByUuid)
        {
            Assert.Skip("GPU selection by UUID comes with MN-106 (P1 driver: spec_rejected for any selection but 'all', D47).");
        }

        var launch = LaunchFor("contract-a", gpus: new GpuSelection { Mode = GpuSelectionMode.Uuids, Uuids = [AgentContractSeed.MissingGpuUuid] });

        var reply = await Agent.SendAsync(AgentRoutes.RuntimeLaunch, ["contract-a"], launch, ct: Ct);

        var error = AssertError(reply, AgentRoutes.RuntimeLaunch, 422, ErrorCodes.GpuMissing);
        Assert.Equal([AgentContractSeed.MissingGpuUuid], error.ReadDetails<GpuMissingDetails>()?.Uuids);
    }

    [Fact]
    public async Task StopRemovesTheInstance()
    {
        await LaunchReadyAsync(LaunchFor("contract-a"));

        var accepted = await AcceptedAsync(AgentRoutes.RuntimeStop, Stop(), "contract-a");
        Assert.Equal(OperationKinds.Stop, accepted.Kind);
        var final = await WaitOperationAsync(accepted.Id);

        Assert.Equal(OperationState.Succeeded, final.State);
        Assert.Null(await GetRuntimeAsync("contract-a"));
        Assert.DoesNotContain(await ListRuntimesAsync(), r => r.ModelId == "contract-a");
    }

    [Fact]
    public async Task StopWithoutAnInstanceSucceeds()
    {
        var accepted = await AcceptedAsync(AgentRoutes.RuntimeStop, Stop(), "contract-nothing");

        Assert.Equal(OperationState.Succeeded, (await WaitOperationAsync(accepted.Id)).State);
    }

    [Fact]
    public async Task RuntimeReadsOfAModelWithoutAnInstanceAreNotFound()
    {
        AssertError(await Agent.SendAsync(AgentRoutes.RuntimeGet, ["contract-a"], ct: Ct), AgentRoutes.RuntimeGet, 404, ErrorCodes.NotFound);
        AssertError(await Agent.SendAsync(AgentRoutes.RuntimeLaunchConfig, ["contract-a"], ct: Ct), AgentRoutes.RuntimeLaunchConfig, 404, ErrorCodes.NotFound);
        using var logs = await Agent.OpenStreamAsync(AgentRoutes.RuntimeLogs, ["contract-a"], "?tail=10&follow=false", Ct);
        AssertError(await ToReplyAsync(logs), AgentRoutes.RuntimeLogs, 404, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task TheRuntimesListFiltersTuningCandidates()
    {
        await LaunchReadyAsync(LaunchFor("contract-a"));
        await LaunchReadyAsync(LaunchFor("contract-t", extraLabels: new Dictionary<string, string> { [ContainerLabels.Tuning] = "1" }));

        var all = await ListRuntimesAsync();
        var tuning = (await Agent.SendAsync(AgentRoutes.RuntimesList, [], query: "?label=tuning", ct: Ct)).Read<List<RuntimeStatus>>();

        Assert.Contains(all, r => r.ModelId == "contract-a" && !r.Tuning);
        Assert.Contains(all, r => r.ModelId == "contract-t" && r.Tuning);
        var only = Assert.Single(tuning);
        Assert.Equal("contract-t", only.ModelId);
        Assert.Equal(2, all.Select(r => r.HostPort).Distinct().Count());
    }

    [Fact]
    public async Task LogsStreamAsServerSentEvents()
    {
        await LaunchReadyAsync(LaunchFor("contract-a"));

        using var response = await Agent.OpenStreamAsync(AgentRoutes.RuntimeLogs, ["contract-a"], "?tail=50&follow=false", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AgentJson.EventStreamContentType, response.Content.Headers.ContentType?.MediaType);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Patience);
        string body = await response.Content.ReadAsStringAsync(timeout.Token);
        foreach (string line in body.Split('\n').Where(l => l.Length > 0))
        {
            Assert.True(line.StartsWith("data:", StringComparison.Ordinal) || line.StartsWith(':') || line.StartsWith("event:", StringComparison.Ordinal), line);
        }
    }

    [Fact]
    public async Task CancellingALaunchBeforeItsInstanceExistsReleasesTheReservation()
    {
        await Harness.SetPullsHeldAsync(true);
        var launch = await AcceptedAsync(
            AgentRoutes.RuntimeLaunch, LaunchFor("contract-a", image: AgentContractSeed.PullImage, digest: AgentContractSeed.PullImageDigest), "contract-a");
        Assert.Single((await StatusAsync()).Reservations);

        var cancel = Cancel();
        var reply = await Agent.SendAsync(AgentRoutes.OperationCancel, [launch.Id], cancel, ct: Ct);

        Assert.Equal(200, reply.Status);
        var cancelOp = reply.Read<OperationStatus>();
        Assert.Equal(cancel.OperationId, cancelOp.Id);
        Assert.Equal(OperationKinds.OperationCancel, cancelOp.Kind);
        Assert.Equal(launch.Id, cancelOp.Target);
        var cancelled = await WaitOperationAsync(launch.Id);
        Assert.Equal(OperationState.Cancelled, cancelled.State);
        await EventuallyAsync(async () => (await StatusAsync()).Reservations.Count == 0 ? "" : null, "the reservation to be released");
        await Harness.SetPullsHeldAsync(false);
        Assert.Null(await GetRuntimeAsync("contract-a"));
    }

    [Fact]
    public async Task CancellingALaunchWhoseInstanceExistsIsInvalid()
    {
        await Harness.SetStartsHeldAsync(true);
        var launch = await AcceptedAsync(AgentRoutes.RuntimeLaunch, LaunchFor("contract-a"), "contract-a");
        await WaitRuntimeAsync("contract-a", ModelState.Starting);

        var reply = await Agent.SendAsync(AgentRoutes.OperationCancel, [launch.Id], Cancel(), ct: Ct);

        AssertError(reply, AgentRoutes.OperationCancel, 409, ErrorCodes.InvalidState);
        await Harness.SetStartsHeldAsync(false);
        Assert.Equal(OperationState.Succeeded, (await WaitOperationAsync(launch.Id)).State);
    }

    [Fact]
    public async Task CancellingAFinishedOperationSucceedsWithItsFinalState()
    {
        var stop = Stop();
        var stopOp = await AcceptedAsync(AgentRoutes.RuntimeStop, stop, "contract-a");
        var final = await WaitOperationAsync(stopOp.Id);
        Assert.Equal(OperationState.Succeeded, final.State);

        var cancel = Cancel();
        var reply = await Agent.SendAsync(AgentRoutes.OperationCancel, [stopOp.Id], cancel, ct: Ct);

        Assert.Equal(200, reply.Status);
        var cancelOp = reply.Read<OperationStatus>();
        Assert.Equal(cancel.OperationId, cancelOp.Id);
        Assert.Equal(OperationState.Succeeded, cancelOp.State);
        var target = cancelOp.ReadResult<OperationStatus>();
        Assert.NotNull(target);
        Assert.Equal(stopOp.Id, target.Id);
        Assert.Equal(OperationState.Succeeded, target.State);
        Assert.Equal(OperationState.Succeeded, (await GetOperationAsync(stopOp.Id))?.State);
    }

    [Fact]
    public async Task CancellingAnUnknownOperationIsNotFound()
    {
        var reply = await Agent.SendAsync(AgentRoutes.OperationCancel, ["contract-unknown-op"], Cancel(), ct: Ct);

        AssertError(reply, AgentRoutes.OperationCancel, 404, ErrorCodes.NotFound);
    }

    // =====================================================================================================
    // Inference proxy (design §7.2)
    // =====================================================================================================

    [Fact]
    public async Task TheProxyForwardsToTheRunningModel()
    {
        await LaunchReadyAsync(LaunchFor("contract-a"));

        using var health = await Agent.ProxyAsync("contract-a", "health", HttpMethod.Get, ct: Ct);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var content = new StringContent("""{"model":"contract-a","messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json");
        using var chat = await Agent.ProxyAsync("contract-a", "v1/chat/completions", HttpMethod.Post, content, ct: Ct);
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        using var doc = JsonDocument.Parse(await chat.Content.ReadAsStringAsync(Ct));
        Assert.True(doc.RootElement.TryGetProperty("choices", out _));
    }

    [Fact]
    public async Task TheProxyStreamsServerSentEvents()
    {
        await LaunchReadyAsync(LaunchFor("contract-a"));

        using var content = new StringContent("""{"model":"contract-a","stream":true,"messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json");
        using var response = await Agent.ProxyAsync("contract-a", "v1/chat/completions", HttpMethod.Post, content, ct: Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("chat.completion.chunk", body, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]", body.TrimEnd(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("collective_rpc")]
    [InlineData("v2/anything")]
    [InlineData("server_info")]
    public async Task TheProxyRefusesPathsOutsideTheAllowList(string path)
    {
        await LaunchReadyAsync(LaunchFor("contract-a"));

        using var response = await Agent.ProxyAsync("contract-a", path, HttpMethod.Post, ct: Ct);

        Assert.Equal(ErrorCodes.NotFound, Assert.Single(response.Headers.GetValues(Protocol.AgentErrorHeader)));
        AssertError(await ToReplyAsync(response), AgentRoutes.Proxy, 404, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task TheProxyToAModelWithoutAnInstanceIsModelNotRunning()
    {
        using var response = await Agent.ProxyAsync("contract-a", "v1/models", HttpMethod.Get, ct: Ct);

        // The marker header tells the master this is the agent's answer, not the model's (D40).
        Assert.Equal(ErrorCodes.ModelNotRunning, Assert.Single(response.Headers.GetValues(Protocol.AgentErrorHeader)));
        AssertError(await ToReplyAsync(response), AgentRoutes.Proxy, 503, ErrorCodes.ModelNotRunning);
    }

    // =====================================================================================================
    // Images, model folders, downloads (design §4.3, §6.4, §10.3)
    // =====================================================================================================

    [Fact]
    public async Task ImagesListTheSeededImage()
    {
        var images = (await Agent.SendAsync(AgentRoutes.ImagesList, [], ct: Ct)).Read<List<ImageInfo>>();

        Assert.Contains(images, i => i.Ref == AgentContractSeed.Image);
        Assert.DoesNotContain(images, i => i.Ref == AgentContractSeed.PullImage);
    }

    [Fact]
    public async Task AnImagePullIsAnOperationWithTheImageAsResult()
    {
        var pull = Pull(AgentContractSeed.PullImage, AgentContractSeed.PullImageDigest);
        var accepted = await AcceptedAsync(AgentRoutes.ImagePull, pull);

        Assert.Equal(OperationKinds.ImagePull, accepted.Kind);
        var final = await WaitOperationAsync(accepted.Id);
        Assert.Equal(OperationState.Succeeded, final.State);
        Assert.Equal(AgentContractSeed.PullImage, final.ReadResult<ImageInfo>()?.Ref);
        var images = (await Agent.SendAsync(AgentRoutes.ImagesList, [], ct: Ct)).Read<List<ImageInfo>>();
        Assert.Contains(images, i => i.Ref == AgentContractSeed.PullImage);
    }

    [Fact]
    public async Task APullOfAnUnapprovedDigestIsRejected()
    {
        var reply = await Agent.SendAsync(AgentRoutes.ImagePull, [], Pull(AgentContractSeed.PullImage, AgentContractSeed.UnapprovedDigest), ct: Ct);

        AssertError(reply, AgentRoutes.ImagePull, 422, ErrorCodes.SpecRejected);
    }

    [Fact]
    public async Task ModelFoldersAreListedAndInspected()
    {
        var folders = (await Agent.SendAsync(AgentRoutes.ModelFoldersList, [], ct: Ct)).Read<List<ModelFolderEntry>>();
        var entry = Assert.Single(folders, f => f.Folder == AgentContractSeed.ModelFolder);
        Assert.True(entry.HasConfig);
        Assert.False(entry.Partial);

        var inspect = await Agent.SendAsync(AgentRoutes.ModelFolderInspect, [AgentContractSeed.ModelFolder], ct: Ct);
        Assert.Equal(200, inspect.Status);
        Assert.Equal(AgentContractSeed.ModelFolder, inspect.Read<ModelFolderInspection>().Folder);

        AssertError(await Agent.SendAsync(AgentRoutes.ModelFolderInspect, ["contract-missing"], ct: Ct), AgentRoutes.ModelFolderInspect, 404, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task AFolderDeleteNeedsTheTypedName()
    {
        var request = FolderDelete("not-the-folder");

        var reply = await Agent.SendAsync(AgentRoutes.ModelFolderDelete, [AgentContractSeed.ScratchFolder], request, ct: Ct);

        AssertError(reply, AgentRoutes.ModelFolderDelete, 400, ErrorCodes.InvalidRequest);
        Assert.Equal(200, (await Agent.SendAsync(AgentRoutes.ModelFolderInspect, [AgentContractSeed.ScratchFolder], ct: Ct)).Status);
    }

    [Fact]
    public async Task AFolderDeleteRemovesTheFolder()
    {
        var accepted = await AcceptedAsync(AgentRoutes.ModelFolderDelete, FolderDelete(AgentContractSeed.ScratchFolder), AgentContractSeed.ScratchFolder);

        Assert.Equal(OperationKinds.FolderDelete, accepted.Kind);
        Assert.Equal(OperationState.Succeeded, (await WaitOperationAsync(accepted.Id)).State);
        AssertError(
            await Agent.SendAsync(AgentRoutes.ModelFolderInspect, [AgentContractSeed.ScratchFolder], ct: Ct), AgentRoutes.ModelFolderInspect, 404, ErrorCodes.NotFound);
        var folders = (await Agent.SendAsync(AgentRoutes.ModelFoldersList, [], ct: Ct)).Read<List<ModelFolderEntry>>();
        Assert.DoesNotContain(folders, f => f.Folder == AgentContractSeed.ScratchFolder);
    }

    [Fact]
    public async Task AFolderUsedByAnInstanceIsInUse()
    {
        await LaunchReadyAsync(LaunchFor("contract-a"));

        var reply = await Agent.SendAsync(AgentRoutes.ModelFolderDelete, [AgentContractSeed.ModelFolder], FolderDelete(AgentContractSeed.ModelFolder), ct: Ct);

        AssertError(reply, AgentRoutes.ModelFolderDelete, 409, ErrorCodes.InUse);
    }

    [Fact]
    public async Task DeletingAnUnknownFolderIsNotFound()
    {
        var reply = await Agent.SendAsync(AgentRoutes.ModelFolderDelete, ["contract-missing"], FolderDelete("contract-missing"), ct: Ct);

        AssertError(reply, AgentRoutes.ModelFolderDelete, 404, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task ADownloadRunsAsAnOperation()
    {
        await RequireFeatureAsync(AgentFeatures.DownloadsHfContainer);
        var download = Download("contract-new");

        var reply = await Agent.SendAsync(AgentRoutes.DownloadStart, [], download, ct: Ct);

        Assert.Equal(202, reply.Status);
        var started = reply.Read<NodeDownloadStatus>();
        Assert.Equal("contract-new", started.Folder);
        Assert.Equal(AgentContractSeed.DownloadRepo, started.RepoId);
        Assert.Equal(download.OperationId, started.OperationId);
        Assert.Equal(200, (await Agent.SendAsync(AgentRoutes.DownloadGet, ["contract-new"], ct: Ct)).Status);
        Assert.Contains((await Agent.SendAsync(AgentRoutes.DownloadsList, [], ct: Ct)).Read<List<NodeDownloadStatus>>(), d => d.Folder == "contract-new");

        var final = await WaitOperationAsync(download.OperationId);
        Assert.Equal(OperationKinds.Download, final.Kind);
        Assert.Equal("contract-new", final.Target);
        Assert.Equal(OperationState.Succeeded, final.State);
        Assert.Equal(DownloadState.Completed, final.ReadResult<NodeDownloadStatus>()?.State);
        Assert.Equal(DownloadState.Completed, (await Agent.SendAsync(AgentRoutes.DownloadGet, ["contract-new"], ct: Ct)).Read<NodeDownloadStatus>().State);
        var folders = (await Agent.SendAsync(AgentRoutes.ModelFoldersList, [], ct: Ct)).Read<List<ModelFolderEntry>>();
        Assert.Contains(folders, f => f.Folder == "contract-new" && !f.Partial);
    }

    [Fact]
    public async Task ADownloadIntoAnExistingFolderIsRefused()
    {
        await RequireFeatureAsync(AgentFeatures.DownloadsHfContainer);

        var download = Download(AgentContractSeed.ModelFolder);
        var reply = await Agent.SendAsync(AgentRoutes.DownloadStart, [], download, ct: Ct);

        AssertError(reply, AgentRoutes.DownloadStart, 409, ErrorCodes.FolderExists);
        // A storage refusal is recorded (D41): the same id answers the same refusal again, nothing runs.
        var retry = await Agent.SendAsync(AgentRoutes.DownloadStart, [], download, ct: Ct);
        AssertError(retry, AgentRoutes.DownloadStart, 409, ErrorCodes.FolderExists);
    }

    [Fact]
    public async Task ASecondDownloadIntoTheSameFolderIsAlreadyRunning()
    {
        await RequireFeatureAsync(AgentFeatures.DownloadsHfContainer);
        await Harness.SetDownloadsHeldAsync(true);
        Assert.Equal(202, (await Agent.SendAsync(AgentRoutes.DownloadStart, [], Download("contract-new"), ct: Ct)).Status);

        var reply = await Agent.SendAsync(AgentRoutes.DownloadStart, [], Download("contract-new") with { Existing = DownloadExistingMode.Complete }, ct: Ct);

        AssertError(reply, AgentRoutes.DownloadStart, 409, ErrorCodes.AlreadyRunning);
        var delete = await Agent.SendAsync(AgentRoutes.ModelFolderDelete, ["contract-new"], FolderDelete("contract-new"), ct: Ct);
        AssertError(delete, AgentRoutes.ModelFolderDelete, 409, ErrorCodes.InUse);
        await Harness.SetDownloadsHeldAsync(false);
    }

    [Fact]
    public async Task ARunningDownloadCanBeCancelled()
    {
        await RequireFeatureAsync(AgentFeatures.DownloadsHfContainer);
        await Harness.SetDownloadsHeldAsync(true);
        var download = Download("contract-new");
        Assert.Equal(202, (await Agent.SendAsync(AgentRoutes.DownloadStart, [], download, ct: Ct)).Status);

        var cancel = new DownloadCancelRequest { OperationId = NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24) };
        var reply = await Agent.SendAsync(AgentRoutes.DownloadCancel, ["contract-new"], cancel, ct: Ct);

        Assert.Equal(200, reply.Status);
        var op = reply.Read<OperationStatus>();
        Assert.Equal(OperationKinds.DownloadCancel, op.Kind);
        Assert.Equal(OperationState.Succeeded, (await WaitOperationAsync(op.Id)).State);
        await EventuallyAsync(
            async () => (await Agent.SendAsync(AgentRoutes.DownloadGet, ["contract-new"], ct: Ct)).Read<NodeDownloadStatus>().State == DownloadState.Cancelled ? "" : null,
            "the download to be cancelled");
        Assert.Equal(OperationState.Cancelled, (await WaitOperationAsync(download.OperationId)).State);
        await Harness.SetDownloadsHeldAsync(false);
    }

    [Fact]
    public async Task CancellingAFinishedDownloadSucceedsWithItsFinalState()
    {
        await RequireFeatureAsync(AgentFeatures.DownloadsHfContainer);
        var download = Download("contract-new");
        Assert.Equal(202, (await Agent.SendAsync(AgentRoutes.DownloadStart, [], download, ct: Ct)).Status);
        await WaitOperationAsync(download.OperationId);

        var cancel = new DownloadCancelRequest { OperationId = NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24) };
        var reply = await Agent.SendAsync(AgentRoutes.DownloadCancel, ["contract-new"], cancel, ct: Ct);

        Assert.Equal(200, reply.Status);
        var final = await WaitOperationAsync(reply.Read<OperationStatus>().Id);
        Assert.Equal(OperationState.Succeeded, final.State);
        Assert.Equal(DownloadState.Completed, final.ReadResult<NodeDownloadStatus>()?.State);
    }

    [Fact]
    public async Task DownloadReadsAndCancelOfAnUnknownFolderAreNotFound()
    {
        await RequireFeatureAsync(AgentFeatures.DownloadsHfContainer);
        AssertError(await Agent.SendAsync(AgentRoutes.DownloadGet, ["contract-none"], ct: Ct), AgentRoutes.DownloadGet, 404, ErrorCodes.NotFound);

        var cancel = new DownloadCancelRequest { OperationId = NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24) };
        AssertError(await Agent.SendAsync(AgentRoutes.DownloadCancel, ["contract-none"], cancel, ct: Ct), AgentRoutes.DownloadCancel, 404, ErrorCodes.NotFound);
    }

    // =====================================================================================================
    // Operations (design §4.3 "Operations", §4.4)
    // =====================================================================================================

    [Fact]
    public async Task OperationsAreListedAndActiveOnesFiltered()
    {
        await Harness.SetPullsHeldAsync(true);
        var running = await AcceptedAsync(
            AgentRoutes.RuntimeLaunch, LaunchFor("contract-a", image: AgentContractSeed.PullImage, digest: AgentContractSeed.PullImageDigest), "contract-a");
        var done = await AcceptedAsync(AgentRoutes.RuntimeStop, Stop(), "contract-b");
        await WaitOperationAsync(done.Id);

        var all = (await Agent.SendAsync(AgentRoutes.OperationsList, [], ct: Ct)).Read<List<OperationStatus>>();
        var active = (await Agent.SendAsync(AgentRoutes.OperationsList, [], query: "?state=active", ct: Ct)).Read<List<OperationStatus>>();

        Assert.Contains(all, o => o.Id == running.Id);
        Assert.Contains(all, o => o.Id == done.Id);
        Assert.Contains(active, o => o.Id == running.Id);
        Assert.DoesNotContain(active, o => o.Id == done.Id);
        Assert.All(active, o => Assert.False(o.State.IsTerminal()));
        AssertError(await Agent.SendAsync(AgentRoutes.OperationGet, ["contract-unknown-op"], ct: Ct), AgentRoutes.OperationGet, 404, ErrorCodes.NotFound);
        await Harness.SetPullsHeldAsync(false);
    }

    // =====================================================================================================
    // Snapshot, events, replay, reset, overflow (design §3.4, §4.7)
    // =====================================================================================================

    [Fact]
    public async Task ASnapshotWatermarkThenEventsLoseAndDoubleNothing()
    {
        var info = await InfoAsync();
        var snapshot = await StatusAsync();
        Assert.Equal(info.BootId, snapshot.BootId);
        var stops = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            stops.Add((await AcceptedAsync(AgentRoutes.RuntimeStop, Stop(), $"contract-{i}")).Id);
        }

        foreach (string id in stops)
        {
            await WaitOperationAsync(id);
        }

        await using var events = await Agent.OpenEventsAsync(snapshot.BootId, snapshot.Seq, ct: Ct);
        Assert.Equal(200, events.Status);
        Assert.Equal(AgentJson.EventStreamContentType, events.MediaType);
        var received = await ReadStateEventsUntilAsync(events, snapshot.Seq, list => stops.All(id => list.Any(e => IsTerminalOperationEvent(e, id))));

        Assert.All(received, e => Assert.Equal(snapshot.BootId, e.Event.BootId));
        Assert.Equal(snapshot.Seq + 1, received[0].Event.Seq);
    }

    [Fact]
    public async Task EventsReplayFromACursorInTheRing()
    {
        var snapshot = await StatusAsync();
        for (int i = 0; i < 3; i++)
        {
            await WaitOperationAsync((await AcceptedAsync(AgentRoutes.RuntimeStop, Stop(), $"contract-{i}")).Id);
        }

        long now = (await StatusAsync()).Seq;
        Assert.True(now >= snapshot.Seq + 3, "Every operation change is an event.");
        long cursor = snapshot.Seq + 2;

        await using var events = await Agent.OpenEventsAsync(snapshot.BootId, cursor, ct: Ct);
        var received = await ReadStateEventsUntilAsync(events, cursor, list => list.Count > 0 && list[^1].Event.Seq >= now);

        Assert.Equal(cursor + 1, received[0].Event.Seq);
        Assert.Equal(now, received[^1].Event.Seq);
    }

    [Fact]
    public async Task AnUnknownBootGetsOneResetAndTheStreamCloses()
    {
        await using var events = await Agent.OpenEventsAsync("not-this-boot", 0, ct: Ct);

        Assert.Equal(200, events.Status);
        var first = await events.NextAsync(Patience, Ct);
        Assert.NotNull(first);
        var reset = Assert.IsType<ResetEvent>(first.Event);
        Assert.Equal(AgentEventTypes.Reset, first.Name);
        Assert.Equal(ResetEvent.BootChanged, reset.Reason);
        Assert.Equal((await InfoAsync()).BootId, reset.BootId);
        Assert.Null(await events.NextAsync(Patience, Ct));
    }

    [Fact]
    public async Task HeartbeatsCarryTheBootAndTheCurrentSeq()
    {
        var snapshot = await StatusAsync();
        await using var events = await Agent.OpenEventsAsync(snapshot.BootId, snapshot.Seq, ct: Ct);

        var first = await events.NextAsync(Protocol.HeartbeatInterval + TimeSpan.FromSeconds(3), Ct);

        Assert.NotNull(first);
        var heartbeat = Assert.IsType<HeartbeatEvent>(first.Event);
        Assert.Equal(AgentEventTypes.Heartbeat, first.Name);
        Assert.Equal(snapshot.BootId, heartbeat.BootId);
        Assert.Equal(snapshot.Seq, heartbeat.Seq);
    }

    [Fact]
    public async Task ASlowSubscriberGetsOverflowAndTheStreamCloses()
    {
        var snapshot = await StatusAsync();
        using var listener = new AgentContractClient(Target);
        await using var events = await listener.OpenEventsAsync(snapshot.BootId, snapshot.Seq, ct: Ct);
        Assert.Equal(200, events.Status);

        // Do not read while producing: the subscriber's bounded queue (1,000) plus the transport windows must fill.
        int produced = 0;
        int target = Protocol.EventSubscriberQueueCapacity + 600;
        await Parallel.ForAsync(0, target, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = Ct }, async (i, ct) =>
        {
            var reply = await Agent.SendAsync(AgentRoutes.RuntimeStop, [$"contract-o{i % 8}"], Stop(), ct: ct);
            Assert.Equal(202, reply.Status);
            Interlocked.Increment(ref produced);
        });

        long expected = snapshot.Seq;
        ReceivedAgentEvent? last = null;
        while (await events.NextAsync(Patience, Ct) is { } next)
        {
            last = next;
            if (next.Event is OverflowEvent)
            {
                break;
            }

            if (next.Event is HeartbeatEvent)
            {
                continue;
            }

            Assert.Equal(expected + 1, next.Event.Seq);
            expected = next.Event.Seq;
        }

        Assert.NotNull(last);
        Assert.IsType<OverflowEvent>(last.Event);
        Assert.Equal(AgentEventTypes.Overflow, last.Name);
        Assert.Null(await events.NextAsync(Patience, Ct));
        Assert.Equal(target, produced);
    }

    [Fact]
    public async Task ALaunchPublishesOperationReservationAndRuntimeEvents()
    {
        var snapshot = await StatusAsync();
        var opening = System.Diagnostics.Stopwatch.StartNew();
        await using var events = await Agent.OpenEventsAsync(snapshot.BootId, snapshot.Seq, ct: Ct);
        Assert.True(opening.Elapsed < Protocol.HeartbeatInterval, "The stream's headers are sent at once, not with the first event.");
        var launch = LaunchFor("contract-a");
        await AcceptedAsync(AgentRoutes.RuntimeLaunch, launch, "contract-a");

        var received = await ReadStateEventsUntilAsync(
            events, snapshot.Seq,
            list => list.Any(e => IsTerminalOperationEvent(e, launch.OperationId)) &&
                    list.Any(e => e.Event is RuntimeEvent { Runtime.State: ModelState.Ready }) &&
                    list.Any(e => e.Event is ReservationEvent { Released: true }));

        Assert.All(received, e => Assert.Equal(e.Name, JsonDocument.Parse(e.Json).RootElement.GetProperty("type").GetString()));
        Assert.All(received, e => Assert.False(JsonDocument.Parse(e.Json).RootElement.TryGetProperty("eventType", out _)));
        var taken = Assert.Single(received, e => e.Event is ReservationEvent { Released: false });
        Assert.Equal(launch.OperationId, ((ReservationEvent)taken.Event).Reservation.OperationId);
        var released = Assert.Single(received, e => e.Event is ReservationEvent { Released: true });
        Assert.True(taken.Event.Seq < released.Event.Seq);
        var starting = received.First(e => e.Event is RuntimeEvent { ModelId: "contract-a", Runtime.State: ModelState.Starting });
        var ready = received.First(e => e.Event is RuntimeEvent { ModelId: "contract-a", Runtime.State: ModelState.Ready });
        Assert.True(starting.Event.Seq < ready.Event.Seq);
        Assert.True(received.First(e => e.Event is OperationEvent o && o.Operation.Id == launch.OperationId).Event.Seq < starting.Event.Seq);
    }

    [Fact]
    public async Task AHealthChangeIsPublishedAsAHealthEvent()
    {
        // MN-047 / D68: Online ↔ Degraded reaches a connected master through the stream, without a re-snapshot.
        var snapshot = await StatusAsync();
        Assert.Empty(snapshot.Health.Reasons);
        await using var events = await Agent.OpenEventsAsync(snapshot.BootId, snapshot.Seq, ct: Ct);

        await Harness.SetDockerAvailableAsync(false);
        var down = await ReadStateEventsUntilAsync(events, snapshot.Seq,
            list => list.Any(e => e.Event is HealthEvent h && h.Health.Reasons.Any(r => r.Code == HealthReasonCodes.DockerUnreachable)));
        var degraded = down.Last(e => e.Event is HealthEvent);
        Assert.Equal(AgentEventTypes.Health, degraded.Name);
        Assert.Contains((await StatusAsync()).Health.Reasons, r => r.Code == HealthReasonCodes.DockerUnreachable);

        await Harness.SetDockerAvailableAsync(true);
        var up = await ReadStateEventsUntilAsync(events, degraded.Event.Seq, list => list.Any(e => e.Event is HealthEvent { Health.Reasons.Count: 0 }));
        Assert.Single(up, e => e.Event is HealthEvent);
    }

    [Fact]
    public async Task StoppingPublishesARemovedRuntime()
    {
        await LaunchReadyAsync(LaunchFor("contract-a"));
        var snapshot = await StatusAsync();
        await using var events = await Agent.OpenEventsAsync(snapshot.BootId, snapshot.Seq, ct: Ct);

        var stop = await AcceptedAsync(AgentRoutes.RuntimeStop, Stop(), "contract-a");
        var received = await ReadStateEventsUntilAsync(events, snapshot.Seq, list => list.Any(e => IsTerminalOperationEvent(e, stop.Id)));

        Assert.Contains(received, e => e.Event is RuntimeEvent { ModelId: "contract-a", Runtime: null });
    }

    // =====================================================================================================
    // Telemetry (design §8.1)
    // =====================================================================================================

    [Fact]
    public async Task TelemetryPagesAreBootAware()
    {
        await RequireFeatureAsync(AgentFeatures.TelemetryV1);
        var info = await InfoAsync();
        var page = await EventuallyAsync(
            async () =>
            {
                var p = (await Agent.SendAsync(AgentRoutes.Telemetry, [], ct: Ct)).Read<TelemetryPage>();
                return p.Samples.Count > 0 ? p : null;
            },
            "a telemetry sample", Protocol.TelemetryInterval * 3);

        Assert.Equal(info.BootId, page.BootId);
        Assert.True(page.Samples.Zip(page.Samples.Skip(1)).All(p => p.First.Seq < p.Second.Seq && p.First.At <= p.Second.At));
        long last = page.Samples[^1].Seq;

        var next = (await Agent.SendAsync(AgentRoutes.Telemetry, [], query: $"?bootId={info.BootId}&afterSeq={last}", ct: Ct)).Read<TelemetryPage>();
        Assert.False(next.Gap);
        Assert.All(next.Samples, s => Assert.True(s.Seq > last));

        var otherBoot = (await Agent.SendAsync(AgentRoutes.Telemetry, [], query: "?bootId=another-boot&afterSeq=5", ct: Ct)).Read<TelemetryPage>();
        Assert.True(otherBoot.Gap);
        Assert.Equal(info.BootId, otherBoot.BootId);
        Assert.NotEmpty(otherBoot.Samples);

        var limited = (await Agent.SendAsync(AgentRoutes.Telemetry, [], query: "?max=1", ct: Ct)).Read<TelemetryPage>();
        Assert.Single(limited.Samples);
    }

    [Fact]
    public async Task GpuIdleSamplesForTheRequestedSeconds()
    {
        var reply = await Agent.SendAsync(AgentRoutes.GpuIdle, [], query: "?seconds=1", ct: Ct);

        Assert.Equal(200, reply.Status);
        var idle = reply.Read<GpuIdleResponse>();
        Assert.True(idle.Samples >= 0);
        Assert.NotNull(idle.PerGpu);
    }

    // =====================================================================================================
    // Host services (design §4.3 "Host services", §4.4 item 8, §10.3) — feature-gated
    // =====================================================================================================

    [Fact]
    public async Task HostServicesListUnitsAndContainers()
    {
        await RequireFeatureAsync(AgentFeatures.ServicesSystemd);
        var list = (await Agent.SendAsync(AgentRoutes.HostServicesList, [], ct: Ct)).Read<HostServicesResponse>();

        Assert.True(list.HostdAvailable);
        Assert.Contains(list.Units, u => u.Name == AgentContractSeed.Service && !u.Protected);
        Assert.Contains(list.Units, u => u.Name == AgentContractSeed.ProtectedService && u.Protected);
        var all = (await Agent.SendAsync(AgentRoutes.HostServicesList, [], query: "?include=all", ct: Ct)).Read<HostServicesResponse>();
        Assert.NotNull(all.Discovered);
    }

    [Fact]
    public async Task ServiceActionsAreSynchronousOperations()
    {
        await RequireFeatureAsync(AgentFeatures.ServicesSystemd);

        var stop = await AcceptedAsync(AgentRoutes.HostServiceStop, ServiceAction(), AgentContractSeed.Service);
        Assert.Equal(OperationKinds.ServiceStop, stop.Kind);
        Assert.Equal(AgentContractSeed.Service, stop.Target);
        Assert.Equal(OperationState.Succeeded, stop.State);
        Assert.NotEqual("active", stop.ReadResult<HostUnit>()?.ActiveState);

        var start = await AcceptedAsync(AgentRoutes.HostServiceStart, ServiceAction(), AgentContractSeed.Service);
        Assert.Equal(OperationState.Succeeded, start.State);
        Assert.Equal("active", start.ReadResult<HostUnit>()?.ActiveState);

        var restart = await AcceptedAsync(AgentRoutes.HostServiceRestart, ServiceAction(), AgentContractSeed.Service);
        Assert.Equal(OperationKinds.ServiceRestart, restart.Kind);
        Assert.Equal(OperationState.Succeeded, restart.State);
        Assert.Equal("active", restart.ReadResult<HostUnit>()?.ActiveState);
    }

    [Fact]
    public async Task AProtectedServiceNeedsTheTypedName()
    {
        await RequireFeatureAsync(AgentFeatures.ServicesSystemd);

        var refused = await Agent.SendAsync(AgentRoutes.HostServiceStop, [AgentContractSeed.ProtectedService], ServiceAction(), ct: Ct);
        AssertError(refused, AgentRoutes.HostServiceStop, 409, ErrorCodes.ConfirmationRequired);

        var confirmed = await AcceptedAsync(AgentRoutes.HostServiceStop, ServiceAction(AgentContractSeed.ProtectedService), AgentContractSeed.ProtectedService);
        Assert.Equal(OperationState.Succeeded, confirmed.State);

        var start = await AcceptedAsync(AgentRoutes.HostServiceStart, ServiceAction(), AgentContractSeed.ProtectedService);
        Assert.Equal(OperationState.Succeeded, start.State);
    }

    [Fact]
    public async Task AnUnknownServiceIsNotFound()
    {
        await RequireFeatureAsync(AgentFeatures.ServicesSystemd);

        var reply = await Agent.SendAsync(AgentRoutes.HostServiceStart, ["contract-no-such-unit"], ServiceAction(), ct: Ct);

        AssertError(reply, AgentRoutes.HostServiceStart, 404, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task AutostartTogglesAndDisablingAProtectedUnitNeedsTheTypedName()
    {
        await RequireFeatureAsync(AgentFeatures.ServicesSystemd);

        var off = await AcceptedAsync(AgentRoutes.HostServiceAutostart, Autostart(false), AgentContractSeed.Service);
        Assert.Equal(OperationKinds.ServiceAutostart, off.Kind);
        Assert.Equal("disabled", off.ReadResult<HostUnit>()?.UnitFileState);
        var on = await AcceptedAsync(AgentRoutes.HostServiceAutostart, Autostart(true), AgentContractSeed.Service);
        Assert.Equal("enabled", on.ReadResult<HostUnit>()?.UnitFileState);

        var refused = await Agent.SendAsync(AgentRoutes.HostServiceAutostart, [AgentContractSeed.ProtectedService], Autostart(false), ct: Ct);
        AssertError(refused, AgentRoutes.HostServiceAutostart, 409, ErrorCodes.ConfirmationRequired);
        var confirmed = await AcceptedAsync(AgentRoutes.HostServiceAutostart, Autostart(false, AgentContractSeed.ProtectedService), AgentContractSeed.ProtectedService);
        Assert.Equal("disabled", confirmed.ReadResult<HostUnit>()?.UnitFileState);
    }

    [Fact]
    public async Task TheWhitelistCanBeEdited()
    {
        await RequireFeatureAsync(AgentFeatures.ServicesSystemd);
        var all = (await Agent.SendAsync(AgentRoutes.HostServicesList, [], query: "?include=all", ct: Ct)).Read<HostServicesResponse>();
        var candidate = all.Discovered?.FirstOrDefault(d => !d.Managed && !d.Denied);
        if (candidate is null)
        {
            Assert.Skip("No discovered, unmanaged unit on this node.");
        }

        var add = new HostWhitelistAddRequest
        {
            OperationId = NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
            Unit = candidate.Unit, Scope = candidate.Scope, Confirm = candidate.Name,
        };
        var added = await AcceptedAsync(AgentRoutes.HostWhitelistAdd, add);
        Assert.Equal(OperationKinds.WhitelistAdd, added.Kind);
        Assert.Equal(candidate.Name, added.ReadResult<HostUnit>()?.Name);

        var again = await Agent.SendAsync(AgentRoutes.HostWhitelistAdd, [], add with { OperationId = NewId() }, ct: Ct);
        AssertError(again, AgentRoutes.HostWhitelistAdd, 409, ErrorCodes.Conflict);

        var update = new HostWhitelistUpdateRequest
        {
            OperationId = NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24), DisplayName = "Contract unit",
        };
        var updated = await AcceptedAsync(AgentRoutes.HostWhitelistUpdate, update, candidate.Name);
        Assert.Equal("Contract unit", updated.ReadResult<HostUnit>()?.DisplayName);

        var remove = new HostWhitelistRemoveRequest { OperationId = NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24) };
        var removed = await AcceptedAsync(AgentRoutes.HostWhitelistRemove, remove, candidate.Name);
        Assert.Equal(OperationState.Succeeded, removed.State);
        var after = (await Agent.SendAsync(AgentRoutes.HostServicesList, [], ct: Ct)).Read<HostServicesResponse>();
        Assert.DoesNotContain(after.Units, u => u.Name == candidate.Name);

        var unknown = await Agent.SendAsync(AgentRoutes.HostWhitelistRemove, [candidate.Name], remove with { OperationId = NewId() }, ct: Ct);
        AssertError(unknown, AgentRoutes.HostWhitelistRemove, 404, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task GpuProcessesAreListed()
    {
        await RequireFeatureAsync(AgentFeatures.GpuProcesses);

        var reply = await Agent.SendAsync(AgentRoutes.HostGpuProcesses, [], ct: Ct);

        Assert.Equal(200, reply.Status);
        Assert.NotNull(reply.Read<List<GpuProcess>>());
    }

    [Fact]
    public async Task ContainerActionsAreSynchronousOperations()
    {
        await RequireFeatureAsync(AgentFeatures.ServicesDocker);
        var info = await Agent.SendAsync(AgentRoutes.HostContainerGet, [AgentContractSeed.Container], ct: Ct);
        Assert.Equal(200, info.Status);
        Assert.Equal(AgentContractSeed.Container, info.Read<HostContainerInfo>().Name);

        var stop = await AcceptedAsync(AgentRoutes.HostContainerStop, ContainerAction(), AgentContractSeed.Container);
        Assert.Equal(OperationKinds.ContainerStop, stop.Kind);
        Assert.NotEqual("running", stop.ReadResult<HostContainerInfo>()?.State);
        var start = await AcceptedAsync(AgentRoutes.HostContainerStart, ContainerAction(), AgentContractSeed.Container);
        Assert.Equal("running", start.ReadResult<HostContainerInfo>()?.State);
        var restart = await AcceptedAsync(AgentRoutes.HostContainerRestart, ContainerAction(), AgentContractSeed.Container);
        Assert.Equal("running", restart.ReadResult<HostContainerInfo>()?.State);

        var policy = new HostContainerAutostartRequest
        {
            OperationId = NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24), RestartPolicy = "unless-stopped",
        };
        var autostart = await AcceptedAsync(AgentRoutes.HostContainerAutostart, policy, AgentContractSeed.Container);
        Assert.Equal("unless-stopped", autostart.ReadResult<HostContainerInfo>()?.RestartPolicy);

        AssertError(await Agent.SendAsync(AgentRoutes.HostContainerGet, ["contract-no-such-container"], ct: Ct), AgentRoutes.HostContainerGet, 404, ErrorCodes.NotFound);
    }

    [Theory]
    [InlineData("mirbadllm")]
    [InlineData("mirbadllm-agent")]
    [InlineData("ss-contract-a")]
    public async Task TheAgentRefusesToActOnItsOwnAndModelContainers(string name)
    {
        await RequireFeatureAsync(AgentFeatures.ServicesDocker);
        if (name.StartsWith(HostContainerPrefix, StringComparison.Ordinal))
        {
            // A model container exists only while its model runs; a hand-made ss-* container without mirbadllm labels
            // stays manageable (W2 review MN-092), so the refusal is asserted on a real instance.
            await LaunchReadyAsync(LaunchFor(name[HostContainerPrefix.Length..]));
        }

        var reply = await Agent.SendAsync(AgentRoutes.HostContainerStop, [name], ContainerAction(), ct: Ct);

        AssertError(reply, AgentRoutes.HostContainerStop, 403, ErrorCodes.Denied);
    }

    [Fact]
    public async Task TheBackupMountPasswordIsNeverStored()
    {
        await RequireFeatureAsync(AgentFeatures.BackupMount);
        const string password = "contract-share-password";
        var set = new BackupMountSetRequest
        {
            OperationId = NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
            Settings = new BackupMountSettings { Type = "smb", Server = "nas.local", Share = "backup", Username = "svc", Password = password },
        };

        var reply = await Agent.SendAsync(AgentRoutes.BackupMountSet, [], set, ct: Ct);

        Assert.Equal(200, reply.Status);
        Assert.DoesNotContain(password, reply.Body, StringComparison.Ordinal);
        var op = reply.Read<OperationStatus>();
        Assert.Equal(OperationKinds.BackupMountSet, op.Kind);
        Assert.Equal(OperationState.Succeeded, op.State);
        Assert.True(op.ReadResult<BackupMountInfo>()?.Configured);
        Assert.DoesNotContain(password, (await Agent.SendAsync(AgentRoutes.OperationGet, [op.Id], ct: Ct)).Body, StringComparison.Ordinal);
        Assert.DoesNotContain(password, (await Agent.SendAsync(AgentRoutes.BackupMountGet, [], ct: Ct)).Body, StringComparison.Ordinal);

        var test = await Agent.SendAsync(AgentRoutes.BackupMountTest, [], new BackupMountTestRequest { Settings = set.Settings }, ct: Ct);
        Assert.Equal(200, test.Status);
        Assert.NotNull(test.Read<BackupMountTestResult>());

        var remove = new BackupMountRemoveRequest { OperationId = NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24) };
        var removed = await AcceptedAsync(AgentRoutes.BackupMountRemove, remove);
        Assert.Equal(OperationKinds.BackupMountRemove, removed.Kind);
        Assert.False(removed.ReadResult<BackupMountInfo>()?.Configured);
        Assert.False((await Agent.SendAsync(AgentRoutes.BackupMountGet, [], ct: Ct)).Read<BackupMountInfo>().Configured);
    }

    // =====================================================================================================
    // PKI: two-phase rotation and trust (design §4.3 "PKI", §10.2)
    // =====================================================================================================

    [Fact]
    public async Task ARotationStartIsRepeatableAndReturnsTheSameCsr()
    {
        var first = await AcceptedAsync(AgentRoutes.PkiRotationStart, RotationStart("rot-1"));
        Assert.Equal(OperationKinds.PkiRotationStart, first.Kind);
        Assert.Equal("rot-1", first.Target);
        var csr = first.ReadResult<PkiRotationCsr>();
        Assert.NotNull(csr);
        Assert.Equal("rot-1", csr.RotationId);
        Assert.Contains("CERTIFICATE REQUEST", csr.CsrPem, StringComparison.Ordinal);

        var second = await AcceptedAsync(AgentRoutes.PkiRotationStart, RotationStart("rot-1"));
        Assert.Equal(csr.CsrPem, second.ReadResult<PkiRotationCsr>()?.CsrPem);

        var status = (await Agent.SendAsync(AgentRoutes.PkiRotationGet, ["rot-1"], ct: Ct)).Read<PkiRotationStatus>();
        Assert.Equal(PkiRotationStage.Pending, status.Stage);
        Assert.Equal(Target.AgentSpkiPin, status.CurrentSpkiSha256);
        Assert.False(string.IsNullOrEmpty(status.PendingSpkiSha256));

        var other = await Agent.SendAsync(AgentRoutes.PkiRotationStart, [], RotationStart("rot-2"), ct: Ct);
        var refused = AssertError(other, AgentRoutes.PkiRotationStart, 409, ErrorCodes.InvalidState);

        // The master's orphan-abort path reads the open rotation from the details (W3 review MN-094).
        Assert.True(refused.Details is { ValueKind: System.Text.Json.JsonValueKind.Object } details
            && details.TryGetProperty("activeRotationId", out var active) && active.GetString() == "rot-1", other.Body);
    }

    [Fact]
    public async Task ARotationPresentsTheNewCertificateOnNewConnectionsAndCommits()
    {
        var csr = (await AcceptedAsync(AgentRoutes.PkiRotationStart, RotationStart("rot-1"))).ReadResult<PkiRotationCsr>()!;
        using var issued = Target.Ca.IssueAgentCertificate(csr.CsrPem, Target.NodeId, ["127.0.0.1"]);

        var commitTooEarly = await Agent.SendAsync(AgentRoutes.PkiRotationCommit, ["rot-1"], RotationCommit("rot-1"), ct: Ct);
        AssertError(commitTooEarly, AgentRoutes.PkiRotationCommit, 409, ErrorCodes.InvalidState);

        var installed = await AcceptedAsync(AgentRoutes.PkiRotationCert, RotationCert("rot-1", issued.CertificatePem), "rot-1");
        var installedStatus = installed.ReadResult<PkiRotationStatus>();
        Assert.Equal(PkiRotationStage.CertInstalled, installedStatus?.Stage);
        Assert.Equal(issued.SpkiSha256, installedStatus?.PendingSpkiSha256);

        // The pooled connection keeps the old certificate; a fresh one negotiates the new one.
        Assert.Equal(200, (await Agent.SendAsync(AgentRoutes.Info, [], ct: Ct)).Status);
        using (var oldOnly = new AgentContractClient(Target, pins: [Target.AgentSpkiPin]))
        {
            await AssertRefusedAsync(oldOnly);
        }

        // A commit over the pooled connection (old certificate) proves nothing and is refused (§10.2 step 3).
        var overOld = await Agent.SendAsync(AgentRoutes.PkiRotationCommit, ["rot-1"], RotationCommit("rot-1"), ct: Ct);
        AssertError(overOld, AgentRoutes.PkiRotationCommit, 409, ErrorCodes.InvalidState);

        using var fresh = new AgentContractClient(Target, pins: [issued.SpkiSha256]) { Master = Agent.Master };
        Assert.Equal(200, (await fresh.SendAsync(AgentRoutes.Info, [], ct: Ct)).Status);
        var commit = await fresh.SendAsync(AgentRoutes.PkiRotationCommit, ["rot-1"], RotationCommit("rot-1"), ct: Ct);
        Assert.Equal(200, commit.Status);
        var committed = commit.Read<OperationStatus>().ReadResult<PkiRotationStatus>();
        Assert.Equal(PkiRotationStage.Committed, committed?.Stage);
        Assert.Equal(issued.SpkiSha256, committed?.CurrentSpkiSha256);
        Assert.Null(committed?.PendingSpkiSha256);

        var again = await fresh.SendAsync(AgentRoutes.PkiRotationCommit, ["rot-1"], RotationCommit("rot-1"), ct: Ct);
        Assert.Equal(200, again.Status);
        var abort = await fresh.SendAsync(AgentRoutes.PkiRotationAbort, ["rot-1"], RotationAbort("rot-1"), ct: Ct);
        AssertError(abort, AgentRoutes.PkiRotationAbort, 409, ErrorCodes.AlreadyCommitted);
        Assert.Equal(PkiRotationStage.Committed, (await fresh.SendAsync(AgentRoutes.PkiRotationGet, ["rot-1"], ct: Ct)).Read<PkiRotationStatus>().Stage);
    }

    [Fact]
    public async Task AnAbortedRotationKeepsTheOldCertificate()
    {
        var csr = (await AcceptedAsync(AgentRoutes.PkiRotationStart, RotationStart("rot-1"))).ReadResult<PkiRotationCsr>()!;
        using var issued = Target.Ca.IssueAgentCertificate(csr.CsrPem, Target.NodeId, ["127.0.0.1"]);
        await AcceptedAsync(AgentRoutes.PkiRotationCert, RotationCert("rot-1", issued.CertificatePem), "rot-1");

        var aborted = await AcceptedAsync(AgentRoutes.PkiRotationAbort, RotationAbort("rot-1"), "rot-1");

        Assert.Equal(PkiRotationStage.Aborted, aborted.ReadResult<PkiRotationStatus>()?.Stage);
        using (var oldOnly = new AgentContractClient(Target, pins: [Target.AgentSpkiPin]))
        {
            Assert.Equal(200, (await oldOnly.SendAsync(AgentRoutes.Info, [], ct: Ct)).Status);
        }

        using var newOnly = new AgentContractClient(Target, pins: [issued.SpkiSha256]);
        await AssertRefusedAsync(newOnly);
        var commit = await Agent.SendAsync(AgentRoutes.PkiRotationCommit, ["rot-1"], RotationCommit("rot-1"), ct: Ct);
        AssertError(commit, AgentRoutes.PkiRotationCommit, 409, ErrorCodes.InvalidState);
    }

    [Fact]
    public async Task ACertificateForAnotherKeyOrRotationIsRefused()
    {
        await AcceptedAsync(AgentRoutes.PkiRotationStart, RotationStart("rot-1"));
        using var otherKey = PkiDefaults.CreateKey();
        using var foreign = Target.Ca.IssueAgentCertificate(CertificateSigningRequests.CreatePem(otherKey, Target.NodeId), Target.NodeId, ["127.0.0.1"]);

        var wrongKey = await Agent.SendAsync(AgentRoutes.PkiRotationCert, ["rot-1"], RotationCert("rot-1", foreign.CertificatePem), ct: Ct);
        AssertError(wrongKey, AgentRoutes.PkiRotationCert, 400, ErrorCodes.InvalidRequest);

        var mismatch = await Agent.SendAsync(AgentRoutes.PkiRotationCert, ["rot-1"], RotationCert("rot-other", foreign.CertificatePem), ct: Ct);
        AssertError(mismatch, AgentRoutes.PkiRotationCert, 400, ErrorCodes.InvalidRequest);

        var unknown = await Agent.SendAsync(AgentRoutes.PkiRotationCert, ["rot-9"], RotationCert("rot-9", foreign.CertificatePem), ct: Ct);
        AssertError(unknown, AgentRoutes.PkiRotationCert, 404, ErrorCodes.NotFound);
        AssertError(await Agent.SendAsync(AgentRoutes.PkiRotationGet, ["rot-9"], ct: Ct), AgentRoutes.PkiRotationGet, 404, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task TrustAnchorsCanBeReplacedButMustKeepTheCaller()
    {
        var trust = (await Agent.SendAsync(AgentRoutes.PkiTrustGet, [], ct: Ct)).Read<PkiTrustResponse>();
        Assert.Equal(Protocol.IssuedMasterSpiffeId, trust.MasterSan);
        Assert.Contains(trust.Anchors, a => a.SpkiSha256 == Target.Ca.SpkiSha256);

        using var second = CertificateAuthority.Create();
        var both = await AcceptedAsync(AgentRoutes.PkiTrustSet, TrustSet(Target.Ca.CertificatePem, second.CertificatePem));
        Assert.Equal(OperationKinds.PkiTrustSet, both.Kind);
        Assert.Equal(2, both.ReadResult<PkiTrustResponse>()?.Anchors.Count);

        var lockout = await Agent.SendAsync(AgentRoutes.PkiTrustSet, [], TrustSet(second.CertificatePem), ct: Ct);
        AssertError(lockout, AgentRoutes.PkiTrustSet, 409, ErrorCodes.InvalidState);

        var back = await AcceptedAsync(AgentRoutes.PkiTrustSet, TrustSet(Target.Ca.CertificatePem));
        Assert.Single(back.ReadResult<PkiTrustResponse>()!.Anchors);
        Assert.Equal(200, (await Agent.SendAsync(AgentRoutes.Info, [], ct: Ct)).Status);
    }

    // =====================================================================================================
    // Feature gating (design §3.6)
    // =====================================================================================================

    [Fact]
    public async Task RoutesOfMissingFeaturesAreNotSupported()
    {
        string[] hostFeatures = [AgentFeatures.ServicesSystemd, AgentFeatures.ServicesDocker, AgentFeatures.BackupMount, AgentFeatures.GpuProcesses];
        await using var reduced = await StartAgentWithoutHostFeaturesAsync();
        using var client = new AgentContractClient(reduced.Target);
        var caps = (await client.SendAsync(AgentRoutes.Capabilities, [], ct: Ct)).Read<NodeCapabilities>();
        var gated = AgentRoutes.All.Where(r => !r.IsOfferedBy(caps.Features)).ToList();

        Assert.All(hostFeatures, f => Assert.DoesNotContain(f, caps.Features));
        Assert.All(hostFeatures, f => Assert.Contains(gated, r => r.Feature == f));
        foreach (var route in gated)
        {
            var reply = await client.SendAsync(route, SamplePath(route), SampleBody(route), SampleQuery(route), ct: Ct);
            AssertError(reply, route, 501, ErrorCodes.NotSupported);
        }
    }

    // =====================================================================================================
    // Helpers
    // =====================================================================================================

    /// <summary>A new random operation id.</summary>
    protected internal static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Asserts an agent error: status, JSON shape, a code of the agent API that the route declares (besides
    /// <c>not_supported</c>, <c>forbidden</c> and <c>internal_error</c>, which any route may answer).
    /// </summary>
    protected static AgentError AssertError(AgentReply reply, AgentRoute route, int status, string code)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(route);
        Assert.True(reply.Status == status, $"{route.Name}: expected {status} {code}, got {reply.Status}: {reply.Body}");
        Assert.Equal(AgentJson.JsonContentType, reply.MediaType);
        var error = reply.Read<AgentErrorResponse>().Error;
        Assert.Equal(code, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Contains(code, ErrorCodes.AgentCodes);
        if (code is not (ErrorCodes.NotSupported or ErrorCodes.Forbidden or ErrorCodes.InternalError))
        {
            Assert.True(route.ErrorCodes.Contains(code), $"{route.Name} does not declare {code}.");
        }

        return error;
    }

    /// <summary>Sends a mutation and asserts the route's success status; returns the operation.</summary>
    protected async Task<OperationStatus> AcceptedAsync(AgentRoute route, AgentMutation request, params string[] path)
    {
        ArgumentNullException.ThrowIfNull(route);
        var reply = await Agent.SendAsync(route, path, request, ct: Ct);
        Assert.True(reply.Status == route.SuccessStatus, $"{route.Name}: expected {route.SuccessStatus}, got {reply.Status}: {reply.Body}");
        var op = reply.Read<OperationStatus>();
        Assert.Equal(request.OperationId, op.Id);
        Assert.Equal(route.OperationKind, op.Kind);
        return op;
    }

    /// <summary>The operation, or null when the agent does not know it (404).</summary>
    protected async Task<OperationStatus?> GetOperationAsync(string id)
    {
        var reply = await Agent.SendAsync(AgentRoutes.OperationGet, [id], ct: Ct);
        if (reply.Status == 404)
        {
            AssertError(reply, AgentRoutes.OperationGet, 404, ErrorCodes.NotFound);
            return null;
        }

        Assert.Equal(200, reply.Status);
        return reply.Read<OperationStatus>();
    }

    /// <summary>Polls the operation until <paramref name="until"/> (default: terminal).</summary>
    protected Task<OperationStatus> WaitOperationAsync(string id, Func<OperationStatus, bool>? until = null) =>
        EventuallyAsync(
            async () => await GetOperationAsync(id) is { } op && (until?.Invoke(op) ?? op.State.IsTerminal()) ? op : null,
            $"operation {id}");

    /// <summary>The runtime, or null when there is no instance (404 = Stopped).</summary>
    protected async Task<RuntimeStatus?> GetRuntimeAsync(string modelId)
    {
        var reply = await Agent.SendAsync(AgentRoutes.RuntimeGet, [modelId], ct: Ct);
        if (reply.Status == 404)
        {
            AssertError(reply, AgentRoutes.RuntimeGet, 404, ErrorCodes.NotFound);
            return null;
        }

        Assert.Equal(200, reply.Status);
        return reply.Read<RuntimeStatus>();
    }

    /// <summary>Polls the runtime until it is in <paramref name="state"/>.</summary>
    protected Task<RuntimeStatus> WaitRuntimeAsync(string modelId, ModelState state) =>
        EventuallyAsync(async () => await GetRuntimeAsync(modelId) is { } r && r.State == state ? r : null, $"{modelId} {state}");

    /// <summary>All runtimes.</summary>
    protected async Task<List<RuntimeStatus>> ListRuntimesAsync() =>
        (await Agent.SendAsync(AgentRoutes.RuntimesList, [], ct: Ct)).Read<List<RuntimeStatus>>();

    /// <summary>Launches and waits until the operation succeeded and the instance is Ready.</summary>
    protected async Task<RuntimeStatus> LaunchReadyAsync(LaunchRequest launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        string modelId = launch.Spec.Labels[ContainerLabels.ServedName];
        var accepted = await AcceptedAsync(AgentRoutes.RuntimeLaunch, launch, modelId);
        var final = await WaitOperationAsync(accepted.Id);
        Assert.True(final.State == OperationState.Succeeded, $"Launch of {modelId} ended {final.State}: {final.Error?.Message}");
        return await WaitRuntimeAsync(modelId, ModelState.Ready);
    }

    /// <summary>The agent's <c>/info</c>.</summary>
    protected async Task<InfoResponse> InfoAsync() => (await Agent.SendAsync(AgentRoutes.Info, [], ct: Ct)).Read<InfoResponse>();

    /// <summary>The agent's <c>/capabilities</c>.</summary>
    protected async Task<NodeCapabilities> CapabilitiesAsync() => (await Agent.SendAsync(AgentRoutes.Capabilities, [], ct: Ct)).Read<NodeCapabilities>();

    /// <summary>The agent's <c>/status</c>.</summary>
    protected async Task<StatusSnapshot> StatusAsync()
    {
        var reply = await Agent.SendAsync(AgentRoutes.Status, [], ct: Ct);
        Assert.Equal(200, reply.Status);
        return reply.Read<StatusSnapshot>();
    }

    /// <summary>Skips the test when the node lacks <paramref name="feature"/>.</summary>
    protected async Task RequireFeatureAsync(string feature)
    {
        if (!(await CapabilitiesAsync()).Features.Contains(feature))
        {
            Assert.Skip($"The node does not offer {feature}.");
        }
    }

    /// <summary>Polls <paramref name="probe"/> until it returns non-null.</summary>
    protected static async Task<T> EventuallyAsync<T>(Func<Task<T?>> probe, string what, TimeSpan? timeout = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(probe);
        var deadline = DateTime.UtcNow + (timeout ?? Patience);
        while (true)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for {what}.");
            }

            await Task.Delay(20, Ct);
        }
    }

    /// <summary>A launch request for <paramref name="modelId"/> with the seeded image and folder.</summary>
    protected internal static LaunchRequest LaunchFor(
        string modelId,
        LaunchMode mode = LaunchMode.EnsureStarted,
        double budgetGiB = 1,
        double reserveGiB = 0,
        bool force = false,
        IReadOnlyList<string>? extraArgs = null,
        IReadOnlyDictionary<string, string>? extraLabels = null,
        string? image = null,
        string? digest = null,
        GpuSelection? gpus = null,
        IReadOnlyDictionary<string, string>? env = null,
        IReadOnlyList<string>? secretEnvNames = null)
    {
        string configHash = "cfg" + modelId.GetHashCode(StringComparison.Ordinal).ToString("x8", CultureInfo.InvariantCulture);
        const string fingerprint = "41ab0123456789cd";
        var labels = new Dictionary<string, string>
        {
            [ContainerLabels.ServedName] = modelId,
            [ContainerLabels.ConfigHash] = configHash,
            [ContainerLabels.ExecutionFingerprint] = fingerprint,
            [ContainerLabels.BudgetGiB] = budgetGiB.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var (key, value) in extraLabels ?? new Dictionary<string, string>())
        {
            labels[key] = value;
        }

        var now = DateTimeOffset.UtcNow;
        return new LaunchRequest
        {
            OperationId = NewId(),
            IssuedAt = now,
            ExpiresAt = now + OperationRetention.EnvelopeLifetime,
            Mode = mode,
            Spec = new LaunchSpec
            {
                Kind = LaunchDelivery.Container,
                Image = image ?? AgentContractSeed.Image,
                ImageDigest = digest ?? AgentContractSeed.ImageDigest,
                Argv = ["--model", $"{LogicalVolumes.ModelsTarget}/{AgentContractSeed.ModelFolder}", "--served-model-name", modelId, .. extraArgs ?? []],
                Env = env ?? new Dictionary<string, string>(),
                SecretEnvNames = secretEnvNames ?? [],
                ContainerPort = 8000,
                Mounts =
                [
                    new MountSpec(LogicalVolumes.Models, null, LogicalVolumes.ModelsTarget, true),
                    new MountSpec(LogicalVolumes.Cache, null, LogicalVolumes.CacheTarget, false),
                ],
                Gpus = gpus ?? new GpuSelection(),
                IpcHost = true,
                HealthPath = "/health",
                Labels = labels,
            },
            Admission = new AdmissionRequest { BudgetGiB = budgetGiB, ReserveGiB = reserveGiB, Force = force },
            ExpectedConfigHash = configHash,
            ExpectedExecutionFingerprint = fingerprint,
        };
    }

    /// <summary>A fresh stop request.</summary>
    protected internal static StopRequest Stop() =>
        new() { OperationId = NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow + OperationRetention.EnvelopeLifetime };

    /// <summary>A fresh cancel request.</summary>
    protected internal static OperationCancelRequest Cancel() =>
        new() { OperationId = NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow + OperationRetention.EnvelopeLifetime };

    /// <summary>A fresh download request into <paramref name="folder"/>.</summary>
    protected internal static DownloadStartRequest Download(string folder) => new()
    {
        OperationId = NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow + OperationRetention.EnvelopeLifetime,
        RepoId = AgentContractSeed.DownloadRepo, Folder = folder,
    };

    private static ImagePullRequest Pull(string image, string? digest) => new()
    {
        OperationId = NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow + OperationRetention.EnvelopeLifetime,
        Image = image, Digest = digest,
    };

    private static FolderDeleteRequest FolderDelete(string confirm) => new()
    {
        OperationId = NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow + OperationRetention.EnvelopeLifetime, Confirm = confirm,
    };

    private static HostServiceActionRequest ServiceAction(string? confirm = null) => new()
    {
        OperationId = NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow + OperationRetention.EnvelopeLifetime, Confirm = confirm,
    };

    private static HostServiceAutostartRequest Autostart(bool enabled, string? confirm = null) => new()
    {
        OperationId = NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow + OperationRetention.EnvelopeLifetime,
        Enabled = enabled, Confirm = confirm,
    };

    private static HostContainerActionRequest ContainerAction() => new()
    {
        OperationId = NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow + OperationRetention.EnvelopeLifetime, TimeoutSeconds = 5,
    };

    private static PkiRotationStartRequest RotationStart(string rotationId) => new()
    {
        OperationId = NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow + OperationRetention.EnvelopeLifetime, RotationId = rotationId,
    };

    private static PkiRotationCertRequest RotationCert(string rotationId, string certPem) => new()
    {
        OperationId = NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow + OperationRetention.EnvelopeLifetime,
        RotationId = rotationId, CertPem = certPem,
    };

    private static PkiRotationCommitRequest RotationCommit(string rotationId) => new()
    {
        OperationId = NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow + OperationRetention.EnvelopeLifetime, RotationId = rotationId,
    };

    private static PkiRotationAbortRequest RotationAbort(string rotationId) => new()
    {
        OperationId = NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow + OperationRetention.EnvelopeLifetime, RotationId = rotationId,
    };

    private static PkiTrustSetRequest TrustSet(params string[] anchors) => new()
    {
        OperationId = NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow + OperationRetention.EnvelopeLifetime, Anchors = anchors,
    };

    /// <summary>Path values that satisfy the route's template (for generic, route-independent checks).</summary>
    private static string[] SamplePath(AgentRoute route) => [.. route.PathParameters.Select(p => p switch
    {
        "modelId" => "contract-a",
        "folder" => "contract-sample",
        "operationId" => "contract-sample-op",
        "rotationId" => "rot-sample",
        "name" => AgentContractSeed.Service,
        "path" => "health",
        _ => "sample",
    })];

    private static string? SampleQuery(AgentRoute route) => route == AgentRoutes.GpuIdle ? "?seconds=1" : null;

    /// <summary>A well-formed body of the route's request type (fresh envelope), or null for routes without a JSON body.</summary>
    private static object? SampleBody(AgentRoute route)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now + OperationRetention.EnvelopeLifetime;
        string id = NewId();
        var settings = new BackupMountSettings { Type = "nfs", Server = "nas.local", Share = "backup" };
        return route.RequestType switch
        {
            var t when t == typeof(LaunchRequest) => LaunchFor("contract-a"),
            var t when t == typeof(StopRequest) => new StopRequest { OperationId = id, IssuedAt = now, ExpiresAt = expires },
            var t when t == typeof(ImagePullRequest) => new ImagePullRequest { OperationId = id, IssuedAt = now, ExpiresAt = expires, Image = AgentContractSeed.PullImage, Digest = AgentContractSeed.PullImageDigest },
            var t when t == typeof(FolderDeleteRequest) => new FolderDeleteRequest { OperationId = id, IssuedAt = now, ExpiresAt = expires, Confirm = "contract-sample" },
            var t when t == typeof(DownloadStartRequest) => new DownloadStartRequest { OperationId = id, IssuedAt = now, ExpiresAt = expires, RepoId = AgentContractSeed.DownloadRepo, Folder = "contract-sample" },
            var t when t == typeof(DownloadCancelRequest) => new DownloadCancelRequest { OperationId = id, IssuedAt = now, ExpiresAt = expires },
            var t when t == typeof(OperationCancelRequest) => new OperationCancelRequest { OperationId = id, IssuedAt = now, ExpiresAt = expires },
            var t when t == typeof(HostServiceActionRequest) => new HostServiceActionRequest { OperationId = id, IssuedAt = now, ExpiresAt = expires },
            var t when t == typeof(HostServiceAutostartRequest) => new HostServiceAutostartRequest { OperationId = id, IssuedAt = now, ExpiresAt = expires, Enabled = true },
            var t when t == typeof(HostWhitelistAddRequest) => new HostWhitelistAddRequest { OperationId = id, IssuedAt = now, ExpiresAt = expires, Unit = "contract-sample.service", Scope = "system" },
            var t when t == typeof(HostWhitelistUpdateRequest) => new HostWhitelistUpdateRequest { OperationId = id, IssuedAt = now, ExpiresAt = expires, DisplayName = "x" },
            var t when t == typeof(HostWhitelistRemoveRequest) => new HostWhitelistRemoveRequest { OperationId = id, IssuedAt = now, ExpiresAt = expires },
            var t when t == typeof(HostContainerActionRequest) => new HostContainerActionRequest { OperationId = id, IssuedAt = now, ExpiresAt = expires },
            var t when t == typeof(HostContainerAutostartRequest) => new HostContainerAutostartRequest { OperationId = id, IssuedAt = now, ExpiresAt = expires, RestartPolicy = "no" },
            var t when t == typeof(BackupMountSetRequest) => new BackupMountSetRequest { OperationId = id, IssuedAt = now, ExpiresAt = expires, Settings = settings },
            var t when t == typeof(BackupMountRemoveRequest) => new BackupMountRemoveRequest { OperationId = id, IssuedAt = now, ExpiresAt = expires },
            var t when t == typeof(BackupMountTestRequest) => new BackupMountTestRequest { Settings = settings },
            var t when t == typeof(PkiRotationStartRequest) => new PkiRotationStartRequest { OperationId = id, IssuedAt = now, ExpiresAt = expires, RotationId = "rot-sample" },
            var t when t == typeof(PkiRotationCertRequest) => new PkiRotationCertRequest { OperationId = id, IssuedAt = now, ExpiresAt = expires, RotationId = "rot-sample", CertPem = "-" },
            var t when t == typeof(PkiRotationCommitRequest) => new PkiRotationCommitRequest { OperationId = id, IssuedAt = now, ExpiresAt = expires, RotationId = "rot-sample" },
            var t when t == typeof(PkiRotationAbortRequest) => new PkiRotationAbortRequest { OperationId = id, IssuedAt = now, ExpiresAt = expires, RotationId = "rot-sample" },
            var t when t == typeof(PkiTrustSetRequest) => new PkiTrustSetRequest { OperationId = id, IssuedAt = now, ExpiresAt = expires, Anchors = ["-"] },
            _ => null,
        };
    }

    private static bool IsTerminalOperationEvent(ReceivedAgentEvent e, string operationId) =>
        e.Event is OperationEvent o && o.Operation.Id == operationId && o.Operation.State.IsTerminal();

    /// <summary>
    /// Reads state events (heartbeats skipped) until <paramref name="done"/>; asserts they are contiguous from
    /// <paramref name="afterSeq"/> + 1 (nothing lost, nothing doubled) and that the SSE name matches the JSON type.
    /// </summary>
    private static async Task<List<ReceivedAgentEvent>> ReadStateEventsUntilAsync(
        AgentEventReader events,
        long afterSeq,
        Func<List<ReceivedAgentEvent>, bool> done)
    {
        var list = new List<ReceivedAgentEvent>();
        long expected = afterSeq;
        var deadline = DateTime.UtcNow + Patience;
        while (!done(list))
        {
            var remaining = deadline - DateTime.UtcNow;
            Assert.True(remaining > TimeSpan.Zero, $"Timed out after {list.Count} event(s).");
            var next = await events.NextAsync(remaining, Ct);
            Assert.NotNull(next);
            using (var doc = JsonDocument.Parse(next.Json))
            {
                Assert.Equal(next.Name, doc.RootElement.GetProperty("type").GetString());
            }

            if (next.Event is HeartbeatEvent)
            {
                continue;
            }

            Assert.False(next.Event is ResetEvent or OverflowEvent, $"Unexpected {next.Name}.");
            Assert.Equal(expected + 1, next.Event.Seq);
            expected = next.Event.Seq;
            list.Add(next);
        }

        return list;
    }

    private static async Task<AgentReply> ToReplyAsync(HttpResponseMessage response) =>
        new((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsStringAsync(Ct));

    /// <summary>The TLS handshake (or the first request) fails, or the agent answers 403 <c>forbidden</c>.</summary>
    private static async Task AssertRefusedAsync(AgentContractClient client)
    {
        AgentReply? reply = null;
        try
        {
            reply = await client.SendAsync(AgentRoutes.Info, [], ct: Ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or AuthenticationException)
        {
            return;
        }

        AssertError(reply, AgentRoutes.Info, 403, ErrorCodes.Forbidden);
    }
}
