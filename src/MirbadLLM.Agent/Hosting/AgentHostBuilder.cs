using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Fencing;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Hosting;

/// <summary>
/// Sets up the agent host (design §4.1, §4.2, §7.2): the Kestrel mTLS listener, the fencing state and the request
/// pipeline. Composition (MN-026) calls, in order:
/// <code>
/// var builder = WebApplication.CreateSlimBuilder(args);
/// AgentHostBuilder.Configure(builder, hostOptions);   // + every package's services
/// var app = builder.Build();
/// AgentHostBuilder.UsePipeline(app);                   // checks before any endpoint
/// app.MapInfoEndpoints(); app.MapStatusEndpoints(); …  // one extension per area (Api/*.cs)
/// </code>
/// </summary>
public static class AgentHostBuilder
{
    /// <summary>TLS handshake timeout of the listener.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Registers the host services and configures Kestrel:
    /// <list type="bullet">
    /// <item>one endpoint on <see cref="AgentHostOptions.ListenAddress"/>:<see cref="AgentHostOptions.Port"/>,
    /// <c>Http1AndHttp2</c>, TLS options built per handshake by <see cref="AgentListenerTls"/>
    /// (<c>PkiTls.CreateServerOptions</c>: client certificate required and validated against the agent's trust anchors,
    /// SAN <c>spiffe://mirbadllm/master</c>, EKU clientAuth; the server certificate context swapped on rotation);</item>
    /// <item><c>Limits.MaxRequestBodySize</c> = <see cref="AgentHostOptions.MaxRequestBodyBytes"/> (≥ 64 MiB);</item>
    /// <item>services: <see cref="TimeProvider"/> (system, unless registered), <see cref="AgentHostOptions"/>,
    /// <see cref="AgentBootInfo"/>, <see cref="IAgentCredentials"/> (<see cref="FileAgentCredentials"/> unless one is
    /// registered), <see cref="AgentListenerTls"/>, <see cref="IFencingState"/> (<see cref="FencingState"/> in the state
    /// directory), <see cref="AgentCapabilityReporter"/>.</item>
    /// </list>
    /// </summary>
    public static void Configure(WebApplicationBuilder builder, AgentHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        IPAddress? address = options.ParseListenAddress();

        IServiceCollection services = builder.Services;
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.TryAddSingleton(sp => new AgentBootInfo(sp.GetRequiredService<TimeProvider>(), options.AgentVersion));
        services.TryAddSingleton<IAgentCredentials>(sp =>
            new FileAgentCredentials(options, sp.GetRequiredService<ILogger<FileAgentCredentials>>()));
        services.TryAddSingleton<AgentListenerTls>();
        services.TryAddSingleton<FencingState>(sp => new FencingState(
            options.StateDirectory, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<FencingState>>()));
        services.TryAddSingleton<IFencingState>(sp => sp.GetRequiredService<FencingState>());
        services.TryAddSingleton(sp => new AgentCapabilityReporter(
            sp.GetRequiredService<AgentBootInfo>(),
            sp.GetServices<ICapabilityContributor>(),
            sp.GetRequiredService<ILogger<AgentCapabilityReporter>>(),
            sp.GetService<IMemoryProbe>()));
        // Explicit factory: DI would pick the IEnumerable<AgentRoute> constructor and resolve it as an empty sequence, which
        // leaves every route mapped without MapAgentRoute metadata (group-mapped areas) unknown to the pipeline (MN-026).
        services.TryAddSingleton(_ => new AgentRouteCatalog());

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes;
            AgentListenerTls tls = kestrel.ApplicationServices.GetRequiredService<AgentListenerTls>();

            void ConfigureEndpoint(ListenOptions listen)
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(new TlsHandshakeCallbackOptions
                {
                    HandshakeTimeout = HandshakeTimeout,
                    OnConnection = _ => ValueTask.FromResult(tls.CreateServerOptions()),
                });
            }

            if (address is null)
            {
                kestrel.ListenAnyIP(options.Port, ConfigureEndpoint);
            }
            else
            {
                kestrel.Listen(address, options.Port, ConfigureEndpoint);
            }
        });
    }

    /// <summary>
    /// Adds routing and the agent request checks (<see cref="AgentRequestMiddleware"/>: client certificate, protocol
    /// header, request time, master triple + fencing, error shape) ahead of every endpoint. Also loads the fencing state
    /// now, so a corrupt <c>fencing.json</c> stops the agent before it listens.
    /// </summary>
    public static void UsePipeline(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _ = app.Services.GetRequiredService<IFencingState>();
        app.UseRouting();
        app.UseMiddleware<AgentRequestMiddleware>();
    }
}
