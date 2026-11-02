using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Enrollment;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Orchestration.Pki;
using MirbadLLM.Orchestration.Recovery;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Auth;
using MirbadLLM.Web.Api;
using MirbadLLM.Web.Enrollment;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-126 (W7 review): everything that hands out or stores the master CA follows the <b>current</b> issuing CA
/// (<see cref="MasterCertificates.Authority"/>), so after a CA rotation's step 3 (<see cref="MasterCertificates.ReissueUnder"/>)
/// enrollment, rekey, join commands, the bootstrap bundle and backups use the new CA without a master restart: over real
/// TLS for enroll and rekey, in the real master composition for the rest.
/// </summary>
public sealed class CaRotationConsumerTests : IDisposable
{
    private const string NodeId = "pc-office";
    private const string Passphrase = "correct horse battery staple";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mirbadllm-ca-follow-" + Guid.NewGuid().ToString("N"));
    private readonly CertificateAuthority _boot = CertificateAuthority.Create();
    private readonly CertificateAuthority _next = CertificateAuthority.Create();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _boot.Dispose();
        _next.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task TheMasterComposition_UsesTheCurrentCa_ForEnrollmentRekeyJoinCommandsBundleAndBackups()
    {
        using var next = CertificateAuthority.Create();
        using var factory = new AuthWebFactory();
        _ = factory.Server;
        IServiceProvider services = factory.Services;
        MasterCertificates master = services.GetRequiredService<MasterCertificates>();
        CertificateAuthority boot = services.GetRequiredService<CertificateAuthority>();

        // P1 (no rotation): every consumer uses the boot CA, as before.
        Assert.Same(boot, master.Authority);
        Assert.Same(boot, services.GetRequiredService<EnrollmentService>().IssuingAuthority);
        Assert.Same(boot, services.GetRequiredService<RekeyService>().IssuingAuthority);
        Assert.Same(boot, services.GetRequiredService<BootstrapBundleWriter>().Authority);
        Assert.Same(boot, services.GetRequiredService<BackupService>().CurrentCertificateAuthority);

        // CA rotation step 3: the new CA issues from now on.
        master.ReissueUnder(next);

        Assert.Same(next, services.GetRequiredService<EnrollmentService>().IssuingAuthority);
        Assert.Same(next, services.GetRequiredService<RekeyService>().IssuingAuthority);
        Assert.Same(next, services.GetRequiredService<BootstrapBundleWriter>().Authority);
        Assert.Same(next, services.GetRequiredService<BackupService>().CurrentCertificateAuthority);

        // The join command pins the new CA (the enroll listener now chains to it).
        JoinTokenCreateResponse join = await services.GetRequiredService<NodeAdminService>().CreateJoinTokenAsync(
            new JoinTokenCreateRequest { NodeId = "pc-new", Password = factory.AdminPassword! }, new NodeApiCaller("admin:test", "test-client") { User = TestAdmin.User }, Ct);
        Assert.Equal(Spki.ToJoinHash(next.SpkiSha256), join.CaHash);
        Assert.Contains("--ca-hash " + join.CaHash, join.Commands.Linux, StringComparison.Ordinal);

        // A backup taken now carries the new CA, never the replaced key.
        BackupDocument backup = await services.GetRequiredService<BackupService>().CreateAsync(Passphrase, Ct);
        BackupPkiSecrets pki = BackupService.DecryptSecrets(BackupService.Parse(BackupService.Serialize(backup)), Passphrase)!.Pki!;
        Assert.Equal(next.CertificatePem, pki.CaCertPem);
        Assert.Equal(next.ExportPrivateKeyPem(), pki.CaKeyPem);
        Assert.NotEqual(boot.ExportPrivateKeyPem(), pki.CaKeyPem);
    }

    [Fact]
    public async Task AJoinAfterTheCaSwitch_IsIssuedByAndPinnedToTheNewCa_AndTheOldHashNeverSendsTheToken()
    {
        await using EnrollHost host = await EnrollHost.StartAsync(Path.Combine(_root, "data"), _boot);
        host.Master.ReissueUnder(_next);
        JoinTokenIssue token = await host.Registry.CreateJoinTokenAsync(NodeId, TimeSpan.FromHours(1), JoinTokenPurpose.Join, Ct);

        // The old CA's hash (a join command printed before the switch): the listener no longer chains to it, so the agent
        // aborts before the token is sent, and the token stays usable.
        string oldState = Path.Combine(_root, "state-old");
        await Assert.ThrowsAsync<AgentEnrollmentException>(() => Agent().JoinAsync(Join(host, token.Token, _boot, oldState), Ct));
        Assert.Null((await host.Registry.ListJoinTokensAsync(Ct)).Single().UsedAt);

        // The new CA's hash: enrolled under the new CA, which is what the agent stores as its trust.
        string state = Path.Combine(_root, "state");
        EnrollmentOutcome outcome = await Agent().JoinAsync(Join(host, token.Token, _next, state), Ct);
        AssertIdentityUnder(_next, state, outcome.CertificateSpkiSha256);
        Assert.Equal(outcome.CertificateSpkiSha256, (await host.Registry.GetAsync(NodeId, Ct))!.PendingCertSpkiSha256);
    }

    [Fact]
    public async Task ARekeyAfterTheCaSwitch_IsIssuedByAndPinnedToTheNewCa()
    {
        await using EnrollHost host = await EnrollHost.StartAsync(Path.Combine(_root, "data"), _boot);
        string state = Path.Combine(_root, "state");

        // Enrolled and approved under the old CA.
        JoinTokenIssue join = await host.Registry.CreateJoinTokenAsync(NodeId, TimeSpan.FromHours(1), JoinTokenPurpose.Join, Ct);
        EnrollmentOutcome enrolled = await Agent().JoinAsync(Join(host, join.Token, _boot, state), Ct);
        AssertIdentityUnder(_boot, state, enrolled.CertificateSpkiSha256);
        await host.Registry.UpdateAsync(NodeId, n => n with { Approved = true, CertSpkiSha256 = n.PendingCertSpkiSha256, PendingCertSpkiSha256 = null }, Ct);

        // An early-closed CA rotation left the node behind: it is re-paired after the switch.
        host.Master.ReissueUnder(_next);
        JoinTokenIssue rekey = await host.Registry.CreateJoinTokenAsync(NodeId, TimeSpan.FromHours(1), JoinTokenPurpose.Rekey, Ct);
        EnrollmentOutcome rekeyed = await Agent().RekeyAsync(new RekeyParameters
        {
            EnrollUrl = host.Https,
            Token = rekey.Token,
            CaHash = Spki.ToJoinHash(_next.SpkiSha256),
            NodeId = NodeId,
            StateDirectory = state,
        }, Ct);

        Assert.Equal(enrolled.NodeUid, rekeyed.NodeUid);
        AssertIdentityUnder(_next, state, rekeyed.CertificateSpkiSha256);
        NodeRecord record = (await host.Registry.GetAsync(NodeId, Ct))!;
        Assert.Equal(rekeyed.CertificateSpkiSha256, record.CertSpkiSha256);
        Assert.True(record.Approved);
    }

    [Fact]
    public async Task AnUnusedBootstrapBundle_IsRewrittenForTheNewCa_AndItsOldTokenExpires()
    {
        using var registry = new JsonNodeRegistry(Options.Create(new MirbadLLMOptions { DataPath = Path.Combine(_root, "data") }), TimeProvider.System);
        using MasterCertificates master = MasterCertificates.LoadOrIssue(_boot, directory: null);
        string bundleDir = Path.Combine(_root, "bootstrap");
        using var writer = new BootstrapBundleWriter(
            registry,
            _boot,
            Options.Create(new EnrollmentOptions { BootstrapEnabled = true, BootstrapDirectory = bundleDir }),
            Options.Create(new RegistryMigrationOptions()),
            TimeProvider.System,
            NullLogger<BootstrapBundleWriter>.Instance,
            master);

        Assert.Equal(BootstrapBundleState.Written, await writer.EnsureAsync(Ct));
        BootstrapBundle first = ReadBundle(writer.BundlePath);
        Assert.Equal(_boot.SpkiSha256, first.CaSpkiSha256);
        Assert.Equal(BootstrapBundleState.Current, await writer.EnsureAsync(Ct));

        master.ReissueUnder(_next);

        Assert.Equal(BootstrapBundleState.Written, await writer.EnsureAsync(Ct));
        BootstrapBundle second = ReadBundle(writer.BundlePath);
        Assert.Equal(_next.SpkiSha256, second.CaSpkiSha256);
        Assert.Equal(_next.CertificatePem, second.CaCertPem);
        Assert.Equal(JoinTokenConsumeOutcome.Expired, (await registry.TryConsumeJoinTokenAsync(first.Token, "dgx", Ct)).Outcome);
        Assert.Equal(JoinTokenConsumeOutcome.Consumed, (await registry.TryConsumeJoinTokenAsync(second.Token, "dgx", Ct)).Outcome);
    }

    private static AgentEnrollment Agent() => new(TimeProvider.System, NullLogger<AgentEnrollment>.Instance);

    private static JoinParameters Join(EnrollHost host, string token, CertificateAuthority expected, string state) => new()
    {
        EnrollUrl = host.Https,
        Token = token,
        CaHash = Spki.ToJoinHash(expected.SpkiSha256),
        NodeId = NodeId,
        StateDirectory = state,
        Addresses = ["192.0.2.50"],
    };

    private static BootstrapBundle ReadBundle(string path) =>
        JsonSerializer.Deserialize<BootstrapBundle>(File.ReadAllText(path), AgentJson.Options)!;

    /// <summary>The agent's stored CA is <paramref name="ca"/> alone and its certificate chains to it.</summary>
    private static void AssertIdentityUnder(CertificateAuthority ca, string state, string pin)
    {
        using TrustAnchorSet stored = TrustAnchorSet.FromPem([File.ReadAllText(Path.Combine(state, FileAgentCredentials.CaFileName))]);
        Assert.Equal([ca.SpkiSha256], stored.SpkiSha256s);
        using X509Certificate2 certificate = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(state, FileAgentCredentials.CertificateFileName)));
        Assert.Equal(pin, Spki.Sha256(certificate));
        using TrustAnchorSet trust = TrustAnchorSet.Create([ca.Certificate]);
        Assert.True(PeerValidator.ForAgent(NodeId, trust, [pin]).Validate(certificate).IsValid);
    }

    /// <summary>The enroll listener on loopback with the real enrollment composition and one <see cref="MasterCertificates"/>.</summary>
    private sealed class EnrollHost : IAsyncDisposable
    {
        private EnrollHost(JsonNodeRegistry registry, WebApplication app, Uri https)
        {
            Registry = registry;
            App = app;
            Https = https;
        }

        public JsonNodeRegistry Registry { get; }

        public WebApplication App { get; }

        public Uri Https { get; }

        public MasterCertificates Master => App.Services.GetRequiredService<MasterCertificates>();

        public static async Task<EnrollHost> StartAsync(string dataDir, CertificateAuthority boot)
        {
            var registry = new JsonNodeRegistry(Options.Create(new MirbadLLMOptions { DataPath = dataDir }), TimeProvider.System);
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls();
            builder.Services.AddSingleton(boot);
            builder.Services.AddSingleton<INodeRegistry>(registry);
            builder.Services.AddMirbadLLMEnrollment(builder.Configuration);
            builder.Services.PostConfigure<EnrollmentOptions>(o =>
            {
                o.BootstrapEnabled = false;
                o.RateLimitPermits = 100;
            });

            // As Program.cs: one MasterCertificates with the listener's names (memory only here).
            builder.Services.AddSingleton(sp => MasterCertificates.LoadOrIssue(
                sp.GetRequiredService<CertificateAuthority>(), directory: null, sp.GetRequiredService<IOptions<EnrollmentOptions>>().Value.EffectiveListenerAddresses));
            int port = TestPorts.Reserve();
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Listen(IPAddress.Loopback, port, listen => kestrel.ApplicationServices.GetRequiredService<EnrollListenerTls>().ConfigureEndpoint(listen)));
            WebApplication app = builder.Build();
            app.UseEnrollListenerGate();
            app.MapEnrollEndpoint();
            await app.StartAsync();
            return new EnrollHost(registry, app, new Uri($"https://127.0.0.1:{port}/"));
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
            Registry.Dispose();
        }
    }
}
