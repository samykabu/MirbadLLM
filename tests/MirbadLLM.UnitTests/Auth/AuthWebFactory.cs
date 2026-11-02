using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Multinode.Fakes;

namespace MirbadLLM.UnitTests.Auth;

/// <summary>
/// Boots the real <c>MirbadLLM.Web</c> app (the composition in <c>Program.cs</c> — see its trailing
/// <c>public partial class Program;</c>, added specifically so <see cref="WebApplicationFactory{TEntryPoint}"/>
/// can host it in tests) against a throwaway <see cref="DataPath"/>, with configurable
/// <c>MirbadLLM:AdminPassword</c>. This exercises the actual cookie/fallback-policy/rate-limiter wiring in
/// <c>Program.cs</c> end to end, rather than a re-implementation of it.
/// <para>The master composition reaches models only through node agents (MN-040), so the factory starts a
/// <see cref="FakeAgent"/> and registers it as the enrolled bootstrap node <c>dgx</c> (same CA, its pin and uid in
/// <c>nodes.json</c>) before the host boots. Each instance has its own data directory (the master holds
/// <c>master.lock</c> on it), deleted on dispose together with the agent.</para>
/// </summary>
public sealed class AuthWebFactory : WebApplicationFactory<Program>
{
    private readonly bool _ownsDataDir = true;
    private CertificateAuthority? _ca;
    private FakeAgent? _agent;

    public AuthWebFactory()
    {
    }

    /// <summary>A second host on another factory's data directory (left as it is: not seeded, not deleted).</summary>
    internal AuthWebFactory(string sharedDataDir)
    {
        DataDir = sharedDataDir;
        _ownsDataDir = false;
    }

    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "mirbadllm-auth-tests-" + Guid.NewGuid().ToString("N"));

    public string? AdminPassword { get; set; } = "correct-password";

    /// <summary>
    /// Keep mirbadllm's own background services (node monitor, telemetry, auto-start, job recovery, …). Off by default:
    /// the shell and auth tests don't need them, and the seeded AutoStart model must never be started.
    /// </summary>
    public bool KeepBackgroundServices { get; set; }

    /// <summary>The agent registered as the bootstrap node <c>dgx</c> (started when the host is built).</summary>
    internal FakeAgent Agent => _agent ?? throw new InvalidOperationException("The host has not been built yet.");

    /// <summary>Extra configuration applied after the defaults.</summary>
    public Dictionary<string, string?> Settings { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        StartAgent();
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["MirbadLLM:DataPath"] = DataDir,
                ["MirbadLLM:AdminPassword"] = AdminPassword,
                // Never reach a real Docker daemon, host agent or model folder, even on the DGX itself: the master has no
                // such clients any more, and these legacy keys point into the throwaway data dir anyway.
                ["MirbadLLM:DockerEndpoint"] = "unix:///nonexistent/mirbadllm-tests/docker.sock",
                ["MirbadLLM:HostAgentSocket"] = Path.Combine(DataDir, "no-hostd.sock"),
                ["MirbadLLM:ModelsLocalPath"] = Path.Combine(DataDir, "models"),
                ["MirbadLLM:ModelsHostPath"] = Path.Combine(DataDir, "models"),
                ["MirbadLLM:CacheHostPath"] = Path.Combine(DataDir, "cache"),
                ["MirbadLLM:HealthPollInterval"] = "00:10:00",
                ["MirbadLLM:Backup:Directory"] = Path.Combine(DataDir, "backups"),
                // The master CA the fake agent trusts, and a bootstrap directory inside the data dir.
                ["MirbadLLM:Pki:CaCertPem"] = _ca!.CertificatePem,
                ["MirbadLLM:Pki:CaKeyPem"] = _ca.ExportPrivateKeyPem(),
                ["MirbadLLM:Enrollment:BootstrapDirectory"] = Path.Combine(DataDir, "bootstrap"),
            };
            foreach ((string key, string? value) in Settings)
            {
                settings[key] = value;
            }

            config.AddInMemoryCollection(settings);
        });

        if (KeepBackgroundServices)
        {
            return;
        }

        builder.ConfigureTestServices(services =>
        {
            var ours = services
                .Where(d => d.ServiceType == typeof(IHostedService) && IsMirbadLLM(d))
                .ToList();
            foreach (var descriptor in ours)
            {
                services.Remove(descriptor);
            }
        });
    }

    /// <summary>Starts the fake agent and writes it into <c>nodes.json</c> as the enrolled, approved bootstrap node.</summary>
    private void StartAgent()
    {
        if (_agent is not null)
        {
            return;
        }

        Directory.CreateDirectory(DataDir);
        _ca = CertificateAuthority.Create();
        _agent = FakeAgent.StartAsync(new FakeAgentOptions { Ca = _ca }).GetAwaiter().GetResult();
        if (!_ownsDataDir)
        {
            return;
        }

        using var registry = new JsonNodeRegistry(Options.Create(new MirbadLLMOptions { DataPath = DataDir }), TimeProvider.System);
        registry.UpsertAsync(new NodeRecord
        {
            Id = _agent.NodeId,
            Uid = _agent.NodeUid,
            Address = _agent.BaseAddress.AbsoluteUri,
            Approved = true,
            CertSpkiSha256 = _agent.CurrentSpki,
        }).GetAwaiter().GetResult();
    }

    private static bool IsMirbadLLM(ServiceDescriptor d)
    {
        var type = d.ImplementationType ?? d.ImplementationInstance?.GetType()
            ?? d.ImplementationFactory?.Method.ReturnType;
        string? ns = type?.Namespace;
        // Factory registrations (AddHostedService(sp => ...)) return IHostedService; their lambdas live in
        // mirbadllm's assemblies, so check where the factory was declared too.
        string? declaring = d.ImplementationFactory?.Method.DeclaringType?.Namespace;
        return (ns?.StartsWith("MirbadLLM", StringComparison.Ordinal) ?? false)
            || (declaring?.StartsWith("MirbadLLM", StringComparison.Ordinal) ?? false);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        // WebApplicationFactory may call this more than once (Dispose and DisposeAsync both end here).
        var agent = Interlocked.Exchange(ref _agent, null);
        agent?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Interlocked.Exchange(ref _ca, null)?.Dispose();
        if (_ownsDataDir && Directory.Exists(DataDir))
        {
            try
            {
                Directory.Delete(DataDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a lingering handle on Windows must not fail the test.
            }
        }
    }
}
