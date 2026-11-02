using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Api;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Fencing;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Core;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Pki;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required here.

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// MN-014: the agent host in process over real mTLS (Kestrel + <c>SocketsHttpHandler</c>, HTTP/2): client-certificate
/// refusals (none, wrong CA, wrong SAN, wrong EKU), certificate hot swap, protocol header, request-time skew, the fencing
/// decisions through the pipeline, error shape, <c>/info</c> and <c>/capabilities</c>, and the body-size limit.
/// </summary>
public sealed class AgentHostTests : IDisposable
{
    private const string NodeId = "dgx";
    private const string NodeUid = "uid-0123456789abcdef";

    private readonly CertificateAuthority _ca = CertificateAuthority.Create();
    private readonly TrustAnchorSet _trust;
    private readonly IssuedCredential _master;
    private readonly X509Certificate2 _masterTls;
    private readonly string _stateDir = Path.Combine(Path.GetTempPath(), "mn014-agent-" + Guid.NewGuid().ToString("N"));
    private readonly string _agentPin;

    public AgentHostTests()
    {
        _trust = TrustAnchorSet.Create([_ca.Certificate]);
        _master = _ca.IssueMasterClientCertificate();
        _masterTls = _master.CreateTlsCertificate();
        _agentPin = WriteAgentCredentials(_stateDir);
    }

    public void Dispose()
    {
        _masterTls.Dispose();
        _master.Dispose();
        _trust.Dispose();
        _ca.Dispose();
        try
        {
            Directory.Delete(_stateDir, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    // ---- TLS: who may connect ----

    [Fact]
    public async Task Master_ConnectsOverHttp2_AndGetsInfo()
    {
        await using AgentTestHost host = await AgentTestHost.StartAsync(_stateDir);
        using HttpClient client = CreateClient(_masterTls, _agentPin);

        using HttpResponseMessage response = await client.SendAsync(Request(host, HttpMethod.Get, "/info", protocol: false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpVersion.Version20, response.Version);
        InfoResponse info = (await response.Content.ReadFromJsonAsync<InfoResponse>(AgentJson.Options))!;
        Assert.Equal(NodeId, info.NodeId);
        Assert.Equal(NodeUid, info.NodeUid);
        Assert.Equal(Protocol.Range, info.Protocol);
        Assert.Equal(host.Services.GetRequiredService<AgentBootInfo>().BootId, info.BootId);
        Assert.Null(info.AcceptedMaster);
        Assert.True((DateTimeOffset.UtcNow - info.Time).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task NoClientCertificate_IsRefused()
    {
        await using AgentTestHost host = await AgentTestHost.StartAsync(_stateDir);
        using HttpClient client = CreateClient(clientCertificate: null, _agentPin);

        await AssertRefusedAsync(client, Request(host, HttpMethod.Get, "/info", protocol: false));
    }

    [Fact]
    public async Task ClientCertificateFromAnotherCa_IsRefused_AndTheReasonIsLogged()
    {
        await using AgentTestHost host = await AgentTestHost.StartAsync(_stateDir);
        using CertificateAuthority rogue = CertificateAuthority.Create();
        using IssuedCredential rogueMaster = rogue.IssueMasterClientCertificate();
        using X509Certificate2 rogueTls = rogueMaster.CreateTlsCertificate();
        using HttpClient client = CreateClient(rogueTls, _agentPin);

        await AssertRefusedAsync(client, Request(host, HttpMethod.Get, "/info", protocol: false));
        await host.Logs.WaitForAsync(nameof(PeerValidationFailure.UntrustedChain));
    }

    [Fact]
    public async Task ClientCertificateWithAnotherSan_IsRefused()
    {
        await using AgentTestHost host = await AgentTestHost.StartAsync(_stateDir);
        using X509Certificate2 wrongSan = CustomClientCertificate([Protocol.NodeSpiffeId("evil")], [PkiDefaults.ClientAuthOid]);
        using HttpClient client = CreateClient(wrongSan, _agentPin);

        await AssertRefusedAsync(client, Request(host, HttpMethod.Get, "/info", protocol: false));
        await host.Logs.WaitForAsync(nameof(PeerValidationFailure.WrongSan));
    }

    [Fact]
    public async Task ClientCertificateWithoutClientAuthEku_IsRefused()
    {
        await using AgentTestHost host = await AgentTestHost.StartAsync(_stateDir);
        using X509Certificate2 wrongEku = CustomClientCertificate([Protocol.MasterSpiffeId], [PkiDefaults.ServerAuthOid]);
        using HttpClient client = CreateClient(wrongEku, _agentPin);

        await AssertRefusedAsync(client, Request(host, HttpMethod.Get, "/info", protocol: false));
        await host.Logs.WaitForAsync(nameof(PeerValidationFailure.WrongEku));
    }

    [Fact]
    public async Task ACorrectlyBuiltCustomClientCertificate_IsAccepted()
    {
        // Control for the two tests above: the same builder with the right SAN and EKU passes.
        await using AgentTestHost host = await AgentTestHost.StartAsync(_stateDir);
        using X509Certificate2 good = CustomClientCertificate([Protocol.MasterSpiffeId], [PkiDefaults.ClientAuthOid]);
        using HttpClient client = CreateClient(good, _agentPin);

        using HttpResponseMessage response = await client.SendAsync(Request(host, HttpMethod.Get, "/info", protocol: false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NotEnrolled_RefusesEveryHandshake()
    {
        string empty = Path.Combine(_stateDir, "empty");
        Directory.CreateDirectory(empty);
        await using AgentTestHost host = await AgentTestHost.StartAsync(empty);
        using HttpClient client = CreateClient(_masterTls, _agentPin);

        Assert.False(host.Services.GetRequiredService<IAgentCredentials>().IsEnrolled);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(Request(host, HttpMethod.Get, "/info", protocol: false)));
    }

    [Fact]
    public async Task AReloadThatFindsHalfWrittenCredentials_KeepsTheLoadedOnes()
    {
        await using AgentTestHost host = await AgentTestHost.StartAsync(_stateDir);
        var credentials = (FileAgentCredentials)host.Services.GetRequiredService<IAgentCredentials>();
        File.WriteAllText(Path.Combine(_stateDir, FileAgentCredentials.CertificateFileName), "-----BEGIN CERTIFICATE----- torn");

        Assert.True(credentials.Reload());

        Assert.True(credentials.IsEnrolled);
        using HttpClient client = CreateClient(_masterTls, _agentPin);
        using HttpResponseMessage response = await client.SendAsync(Request(host, HttpMethod.Get, "/info", protocol: false));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CertificateRotation_NewConnectionsGetTheNewCertificate_EstablishedOnesKeepTheOld()
    {
        await using AgentTestHost host = await AgentTestHost.StartAsync(_stateDir);
        using HttpClient pooled = CreateClient(_masterTls, _agentPin);
        using (HttpResponseMessage first = await pooled.SendAsync(Request(host, HttpMethod.Get, "/info", protocol: false)))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        // Rotation (design §10.2 step 3): new key + certificate in the state directory, then a reload.
        string newPin = WriteAgentCredentials(_stateDir);
        Assert.True(((FileAgentCredentials)host.Services.GetRequiredService<IAgentCredentials>()).Reload());

        using HttpClient freshNew = CreateClient(_masterTls, newPin);
        using HttpResponseMessage fresh = await freshNew.SendAsync(Request(host, HttpMethod.Get, "/info", protocol: false));
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);

        using HttpClient freshOld = CreateClient(_masterTls, _agentPin);
        await Assert.ThrowsAsync<HttpRequestException>(() => freshOld.SendAsync(Request(host, HttpMethod.Get, "/info", protocol: false)));

        using HttpResponseMessage reused = await pooled.SendAsync(Request(host, HttpMethod.Get, "/info", protocol: false));
        Assert.Equal(HttpStatusCode.OK, reused.StatusCode);
    }

    [Fact]
    public async Task MasterTrustSwap_AppliesToTheNextHandshake()
    {
        await using AgentTestHost host = await AgentTestHost.StartAsync(_stateDir);
        AgentListenerTls tls = host.Services.GetRequiredService<AgentListenerTls>();
        tls.Refresh();
        using CertificateAuthority next = CertificateAuthority.Create();
        using IssuedCredential nextMaster = next.IssueMasterClientCertificate();
        using X509Certificate2 nextMasterTls = nextMaster.CreateTlsCertificate();
        using HttpClient before = CreateClient(nextMasterTls, _agentPin);
        await AssertRefusedAsync(before, Request(host, HttpMethod.Get, "/info", protocol: false));

        using (TrustAnchorSet both = TrustAnchorSet.Create([_ca.Certificate, next.Certificate]))
        {
            tls.SetMasterTrust(both);
        }

        using HttpClient after = CreateClient(nextMasterTls, _agentPin);
        using HttpResponseMessage response = await after.SendAsync(Request(host, HttpMethod.Get, "/info", protocol: false));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Protocol negotiation ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("one")]
    [InlineData("-1")]
    public async Task UnsupportedProtocolHeader_Is400ProtocolMismatch(string? header)
    {
        await using AgentTestHost host = await AgentTestHost.StartAsync(_stateDir);
        using HttpClient client = CreateClient(_masterTls, _agentPin);
        using HttpRequestMessage request = Request(host, HttpMethod.Get, "/capabilities", protocol: false);
        if (header is not null)
        {
            request.Headers.TryAddWithoutValidation(Protocol.ProtocolHeader, header);
        }

        using HttpResponseMessage response = await client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ProtocolMismatch);
    }

    [Fact]
    public async Task Info_NeedsNoProtocolHeader_AndIgnoresAnUnsupportedOne()
    {
        await using AgentTestHost host = await AgentTestHost.StartAsync(_stateDir);
        using HttpClient client = CreateClient(_masterTls, _agentPin);
        using HttpRequestMessage request = Request(host, HttpMethod.Get, "/info", protocol: false);
        request.Headers.Add(Protocol.ProtocolHeader, "99");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Capabilities_FoldsContributorsOverTheBaseReport()
    {
        await using AgentTestHost host = await AgentTestHost.StartAsync(_stateDir, services =>
        {
            services.AddSingleton<ICapabilityContributor>(new FeatureContributor(AgentFeatures.TelemetryV1));
            services.AddSingleton<ICapabilityContributor>(new ThrowingContributor());
            services.AddSingleton<ICapabilityContributor>(new FeatureContributor(AgentFeatures.RuntimeVllmContainer));
            services.AddSingleton<IMemoryProbe>(new FixedMemoryProbe(new MemorySnapshot(121.7, 100)));
        });
        using HttpClient client = CreateClient(_masterTls, _agentPin);

        using HttpResponseMessage response = await client.SendAsync(Request(host, HttpMethod.Get, "/capabilities"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        NodeCapabilities caps = (await response.Content.ReadFromJsonAsync<NodeCapabilities>(AgentJson.Options))!;
        Assert.Equal(Protocol.Range, caps.Protocol);
        Assert.Equal(host.Services.GetRequiredService<AgentBootInfo>().AgentVersion, caps.AgentVersion);
        Assert.Equal([AgentFeatures.TelemetryV1, AgentFeatures.RuntimeVllmContainer], caps.Features);
        Assert.Equal(121.7, caps.Memory.TotalGiB);
        Assert.Equal(MemoryModel.Unified, caps.Memory.Model);
        Assert.True(caps.Arch is "arm64" or "amd64", caps.Arch);
        Assert.Equal(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "macos", caps.Os.Family);
        Assert.Equal(Environment.ProcessorCount, caps.Cpu!.LogicalCores);
        await host.Logs.WaitForAsync(nameof(ThrowingContributor));
    }

    // ---- Mutations: freshness and fencing ----

    [Fact]
    public async Task Mutation_FromTheFirstMaster_IsAccepted_PersistedAndReportedByInfo()
    {
        await using AgentTestHost host = await AgentTestHost.StartAsync(_stateDir);
        using HttpClient client = CreateClient(_masterTls, _agentPin);
        MasterTriple master = new("inst-a", 3, "boot-1");

        using HttpResponseMessage response = await client.SendAsync(Mutation(host, master));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(master.ToHeaderValue(), await response.Content.ReadAsStringAsync());
        Assert.Equal(master, await GetAcceptedMasterAsync(host, client));
        Assert.Contains("\"epoch\": 3", File.ReadAllText(Path.Combine(_stateDir, FencingStateDocument.FileName)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mutation_FencingDecisions_ThroughThePipeline()
    {
        await using AgentTestHost host = await AgentTestHost.StartAsync(_stateDir);
        using HttpClient client = CreateClient(_masterTls, _agentPin);
        (await client.SendAsync(Mutation(host, new MasterTriple("inst-a", 5, "boot-1")))).Dispose();

        using (HttpResponseMessage lower = await client.SendAsync(Mutation(host, new MasterTriple("inst-a", 4, "boot-9"))))
        {
            JsonElement details = await AssertErrorAsync(lower, HttpStatusCode.Conflict, ErrorCodes.StaleMaster);
            Assert.Equal(5, details.GetProperty("acceptedMaster").GetProperty("epoch").GetInt64());
        }

        using (HttpResponseMessage competing = await client.SendAsync(Mutation(host, new MasterTriple("inst-b", 5, "boot-2"))))
        {
            await AssertErrorAsync(competing, HttpStatusCode.Conflict, ErrorCodes.CompetingMaster);
        }

        using (HttpResponseMessage otherBoot = await client.SendAsync(Mutation(host, new MasterTriple("inst-a", 5, "boot-2"))))
        {
            await AssertErrorAsync(otherBoot, HttpStatusCode.Conflict, ErrorCodes.CompetingMaster);
        }

        using (HttpResponseMessage higher = await client.SendAsync(Mutation(host, new MasterTriple("inst-b", 6, "boot-3"))))
        {
            Assert.Equal(HttpStatusCode.OK, higher.StatusCode);
        }

        Assert.Equal(new MasterTriple("inst-b", 6, "boot-3"), await GetAcceptedMasterAsync(host, client));
        Assert.False(host.Services.GetRequiredService<IFencingState>().IsCurrent(new MasterTriple("inst-a", 5, "boot-1")));
    }

    [Fact]
    public async Task AcceptedTriple_SurvivesAnAgentRestart()
    {
        await using (AgentTestHost first = await AgentTestHost.StartAsync(_stateDir))
        {
            using HttpClient client = CreateClient(_masterTls, _agentPin);
            (await client.SendAsync(Mutation(first, new MasterTriple("inst-a", 8, "boot-1")))).Dispose();
        }

        await using AgentTestHost restarted = await AgentTestHost.StartAsync(_stateDir);
        using HttpClient again = CreateClient(_masterTls, _agentPin);

        Assert.Equal(new MasterTriple("inst-a", 8, "boot-1"), await GetAcceptedMasterAsync(restarted, again));
        using HttpResponseMessage stale = await again.SendAsync(Mutation(restarted, new MasterTriple("inst-a", 7, "boot-2")));
        await AssertErrorAsync(stale, HttpStatusCode.Conflict, ErrorCodes.StaleMaster);
    }

    [Theory]
    [InlineData(-6)]
    [InlineData(6)]
    [InlineData(-60 * 24)]
    public async Task Mutation_WithClockSkewOverFiveMinutes_Is400ClockSkew_AndChangesNothing(int skewMinutes)
    {
        await using AgentTestHost host = await AgentTestHost.StartAsync(_stateDir);
        using HttpClient client = CreateClient(_masterTls, _agentPin);

        using HttpResponseMessage response = await client.SendAsync(
            Mutation(host, new MasterTriple("inst-a", 1, "boot-1"), requestTime: DateTimeOffset.UtcNow.AddMinutes(skewMinutes)));

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ClockSkew);
        Assert.Null(await GetAcceptedMasterAsync(host, client));
    }

    [Fact]
    public async Task Mutation_WithinTheSkewWindow_IsAccepted()
    {
        await using AgentTestHost host = await AgentTestHost.StartAsync(_stateDir);
        using HttpClient client = CreateClient(_masterTls, _agentPin);

        using HttpResponseMessage response = await client.SendAsync(
            Mutation(host, new MasterTriple("inst-a", 1, "boot-1"), requestTime: DateTimeOffset.UtcNow.AddMinutes(-4)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null, "inst-a/1/boot-1")]
    [InlineData("yesterday", "inst-a/1/boot-1")]
    [InlineData("now", null)]
    [InlineData("now", "inst-a/1")]
    [InlineData("now", "inst-a/x/boot-1")]
    [InlineData("now", "inst-a/-1/boot-1")]
    public async Task Mutation_WithMissingOrMalformedHeaders_Is400InvalidRequest(string? requestTime, string? master)
    {
        await using AgentTestHost host = await AgentTestHost.StartAsync(_stateDir);
        using HttpClient client = CreateClient(_masterTls, _agentPin);
        using HttpRequestMessage request = Request(host, HttpMethod.Post, AgentRoutes.RuntimeStop.Expand("m1"));
        if (requestTime is not null)
        {
            request.Headers.TryAddWithoutValidation(Protocol.RequestTimeHeader,
                requestTime == "now" ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) : requestTime);
        }

        if (master is not null)
        {
            request.Headers.TryAddWithoutValidation(Protocol.MasterHeader, master);
        }

        using HttpResponseMessage response = await client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest);
        Assert.Null(await GetAcceptedMasterAsync(host, client));
    }

    [Fact]
    public async Task Mutation_NeedsTheProtocolHeaderToo()
    {
        await using AgentTestHost host = await AgentTestHost.StartAsync(_stateDir);
        using HttpClient client = CreateClient(_masterTls, _agentPin);
        using HttpRequestMessage request = Mutation(host, new MasterTriple("inst-a", 1, "boot-1"));
        request.Headers.Remove(Protocol.ProtocolHeader);

        using HttpResponseMessage response = await client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ProtocolMismatch);
        Assert.Null(await GetAcceptedMasterAsync(host, client));
    }

    [Fact]
    public async Task UnknownNonSafeRoute_IsTreatedAsAMutation_FailClosed()
    {
        await using AgentTestHost host = await AgentTestHost.StartAsync(_stateDir);
        using HttpClient client = CreateClient(_masterTls, _agentPin);

        using (HttpResponseMessage noMaster = await client.SendAsync(Request(host, HttpMethod.Post, "/test/unlisted")))
        {
            await AssertErrorAsync(noMaster, HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest);
        }

        using HttpRequestMessage request = Mutation(host, new MasterTriple("inst-a", 2, "boot-1"));
        request.RequestUri = new Uri(host.BaseUri, Protocol.BasePath.TrimStart('/') + "/test/unlisted");
        using HttpResponseMessage withMaster = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, withMaster.StatusCode);
    }

    [Fact]
    public async Task LargeBody_Over64MiB_IsAccepted()
    {
        // Kestrel's default (≈ 28.6 MiB) would answer 413; the agent allows 64 MiB + headroom (design §7.2).
        await using AgentTestHost host = await AgentTestHost.StartAsync(_stateDir);
        KestrelServerOptions kestrel = host.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
        Assert.Equal(AgentHostOptions.DefaultMaxRequestBodyBytes, kestrel.Limits.MaxRequestBodySize);
        Assert.True(kestrel.Limits.MaxRequestBodySize >= Protocol.MaxProxyRequestBodyBytes);
        using HttpClient client = CreateClient(_masterTls, _agentPin);
        int size = (int)Protocol.MaxProxyRequestBodyBytes + 1024;
        using HttpRequestMessage request = Mutation(host, new MasterTriple("inst-a", 1, "boot-1"));
        request.RequestUri = new Uri(host.BaseUri, Protocol.BasePath.TrimStart('/') + "/test/body");
        request.Content = new ByteArrayContent(new byte[size]);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(size.ToString(CultureInfo.InvariantCulture), await response.Content.ReadAsStringAsync());
    }

    // ---- Error shape ----

    [Fact]
    public async Task UnknownRoute_Is404NotFound_InTheAgentShape()
    {
        await using AgentTestHost host = await AgentTestHost.StartAsync(_stateDir);
        using HttpClient client = CreateClient(_masterTls, _agentPin);

        using HttpResponseMessage response = await client.SendAsync(Request(host, HttpMethod.Get, "/nothing/here"));

        await AssertErrorAsync(response, HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task HandlerErrors_UseTheAgentShape()
    {
        await using AgentTestHost host = await AgentTestHost.StartAsync(_stateDir);
        using HttpClient client = CreateClient(_masterTls, _agentPin);

        using (HttpResponseMessage agentError = await client.SendAsync(Request(host, HttpMethod.Get, "/test/agent-error")))
        {
            await AssertErrorAsync(agentError, HttpStatusCode.UnprocessableEntity, ErrorCodes.SpecRejected);
        }

        using HttpResponseMessage crash = await client.SendAsync(Request(host, HttpMethod.Get, "/test/crash"));
        await AssertErrorAsync(crash, HttpStatusCode.InternalServerError, ErrorCodes.InternalError);
        Assert.DoesNotContain("secret detail", await crash.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void Options_AreValidated()
    {
        Assert.Throws<ArgumentException>(() => new AgentHostOptions().Validate());
        Assert.Throws<ArgumentException>(() => new AgentHostOptions { StateDirectory = _stateDir, MaxRequestBodyBytes = 30_000_000 }.Validate());
        Assert.Throws<ArgumentException>(() => new AgentHostOptions { StateDirectory = _stateDir, ListenAddress = "not-an-ip" }.Validate());
        Assert.Throws<ArgumentException>(() => new AgentHostOptions { StateDirectory = _stateDir, Port = 70000 }.Validate());
        new AgentHostOptions { StateDirectory = _stateDir }.Validate();
        Assert.Equal(Protocol.DefaultAgentPort, new AgentHostOptions().Port);
    }

    [Fact]
    public void RouteCatalog_FindsEveryCatalogueRoute_ByPattern()
    {
        AgentRouteCatalog catalog = new();
        foreach (AgentRoute route in AgentRoutes.All)
        {
            Microsoft.AspNetCore.Routing.RouteEndpoint endpoint = new(
                _ => Task.CompletedTask,
                Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse(Protocol.BasePath + route.Path),
                0, EndpointMetadataCollection.Empty, route.Name);
            string method = route.Method == AgentHttpMethods.Any ? "POST" : route.Method;

            Assert.Same(route, catalog.Find(endpoint, method));
        }
    }

    // ---- helpers ----

    private string WriteAgentCredentials(string directory)
    {
        using ECDsa key = PkiDefaults.CreateKey();
        string csr = CertificateSigningRequests.CreatePem(key, NodeId);
        using IssuedCertificate certificate = _ca.IssueAgentCertificate(csr, NodeId, ["127.0.0.1"]);
        PkiFiles.WritePublic(Path.Combine(directory, FileAgentCredentials.CertificateFileName), certificate.CertificatePem);
        PkiFiles.WriteSecret(Path.Combine(directory, FileAgentCredentials.KeyFileName), key.ExportPkcs8PrivateKeyPem());
        PkiFiles.WritePublic(Path.Combine(directory, FileAgentCredentials.CaFileName), _ca.CertificatePem);
        PkiFiles.WritePublic(Path.Combine(directory, FileAgentCredentials.NodeUidFileName), NodeUid + "\n");
        return certificate.SpkiSha256;
    }

    private X509Certificate2 CustomClientCertificate(string[] uris, string[] ekus)
    {
        using ECDsa key = PkiDefaults.CreateKey();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 publicOnly = PkiTestSupport.SignCustom(
            _ca.Certificate, _ca.ExportPrivateKeyPem(), now.AddMinutes(-5), now.AddDays(1), uris, ekus, subjectKey: key);
        using X509Certificate2 withKey = publicOnly.CopyWithPrivateKey(key);
        return TlsCredentials.Create(withKey);
    }

    private HttpClient CreateClient(X509Certificate2? clientCertificate, string agentPin)
    {
        PeerValidatorSource validator = new(PeerValidator.ForAgent(NodeId, _trust, [agentPin]));
        SocketsHttpHandler handler = new()
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxResponseDrainSize = 0,
            SslOptions = PkiTls.CreateClientOptions(clientCertificate is null ? null : PkiTls.CreateContext(clientCertificate), validator),
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    private static HttpRequestMessage Request(AgentTestHost host, HttpMethod method, string path, bool protocol = true)
    {
        HttpRequestMessage request = new(method, new Uri(host.BaseUri, Protocol.BasePath.TrimStart('/') + path))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        if (protocol)
        {
            request.Headers.Add(Protocol.ProtocolHeader, Protocol.CurrentVersion.ToString(CultureInfo.InvariantCulture));
        }

        return request;
    }

    private static HttpRequestMessage Mutation(AgentTestHost host, MasterTriple master, DateTimeOffset? requestTime = null)
    {
        HttpRequestMessage request = Request(host, HttpMethod.Post, AgentRoutes.RuntimeStop.Expand("m1"));
        request.Headers.Add(Protocol.MasterHeader, master.ToHeaderValue());
        request.Headers.Add(Protocol.RequestTimeHeader, (requestTime ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture));
        return request;
    }

    private static async Task<MasterTriple?> GetAcceptedMasterAsync(AgentTestHost host, HttpClient client)
    {
        using HttpResponseMessage response = await client.SendAsync(Request(host, HttpMethod.Get, "/info", protocol: false));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<InfoResponse>(AgentJson.Options))!.AcceptedMaster;
    }

    private static async Task AssertRefusedAsync(HttpClient client, HttpRequestMessage request)
    {
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request);
            await AssertErrorAsync(response, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        }
        catch (HttpRequestException)
        {
            // Refused during the TLS handshake (the expected path).
        }
    }

    private static async Task<JsonElement> AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(AgentJson.JsonContentType, response.Content.Headers.ContentType?.MediaType);
        AgentErrorResponse body = (await response.Content.ReadFromJsonAsync<AgentErrorResponse>(AgentJson.Options))!;
        Assert.Equal(code, body.Error.Code);
        Assert.False(string.IsNullOrWhiteSpace(body.Error.Message));
        return body.Error.Details ?? default;
    }

    private sealed class FeatureContributor(string feature) : ICapabilityContributor
    {
        public Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default) =>
            Task.FromResult(current with { Features = [.. current.Features, feature] });
    }

    private sealed class ThrowingContributor : ICapabilityContributor
    {
        public Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default) =>
            throw new InvalidOperationException("broken subsystem");
    }

    private sealed class FixedMemoryProbe(MemorySnapshot snapshot) : IMemoryProbe
    {
        public Task<MemorySnapshot> GetAsync(CancellationToken ct = default) => Task.FromResult(snapshot);
    }
}

/// <summary>An agent host on 127.0.0.1:{free port} with the MN-014 pipeline, <c>/info</c>, <c>/capabilities</c> and test routes.</summary>
internal sealed class AgentTestHost : IAsyncDisposable
{
    private AgentTestHost(WebApplication app, Uri baseUri, CapturedLogs logs)
    {
        App = app;
        BaseUri = baseUri;
        Logs = logs;
    }

    public WebApplication App { get; }

    public Uri BaseUri { get; }

    public CapturedLogs Logs { get; }

    public IServiceProvider Services => App.Services;

    public static async Task<AgentTestHost> StartAsync(string stateDirectory, Action<IServiceCollection>? configureServices = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        CapturedLogs logs = new();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        configureServices?.Invoke(builder.Services);
        AgentHostBuilder.Configure(builder, new AgentHostOptions { StateDirectory = stateDirectory, ListenAddress = "127.0.0.1", Port = 0 });

        WebApplication app = builder.Build();
        AgentHostBuilder.UsePipeline(app);
        app.MapInfoEndpoints();

        // A catalogue mutation mapped without metadata (as an area may do): the pipeline must still fence it.
        app.MapPost(Protocol.BasePath + AgentRoutes.RuntimeStop.Path, (HttpContext context) => context.GetAcceptedMaster().ToHeaderValue());
        app.MapPost(Protocol.BasePath + "/test/unlisted", () => "ok");
        app.MapPost(Protocol.BasePath + "/test/body", async (HttpContext context) =>
        {
            long total = 0;
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                total += read;
            }

            return total.ToString(CultureInfo.InvariantCulture);
        });
        app.MapGet(Protocol.BasePath + "/test/agent-error", IResult () =>
            throw new AgentException(422, AgentErrorResults.Create(ErrorCodes.SpecRejected, "Refused.", new SpecRejectedDetails("test"))));
        app.MapGet(Protocol.BasePath + "/test/crash", IResult () => throw new InvalidOperationException("secret detail"));

        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
        Uri baseUri = new(new Uri(address).GetLeftPart(UriPartial.Authority) + "/");
        return new AgentTestHost(app, baseUri, logs);
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

/// <summary>Collects formatted log messages (hand-written provider).</summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => _messages;

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    public async Task WaitForAsync(string fragment)
    {
        for (int i = 0; i < 100; i++)
        {
            if (_messages.Any(m => m.Contains(fragment, StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"No log message contains '{fragment}'. Logged: {string.Join(" | ", _messages)}");
    }

    private sealed class Logger(CapturedLogs owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner._messages.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception.GetType().Name + ": " + exception.Message));
    }
}
