using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Web.Enrollment;

namespace MirbadLLM.Web;

/// <summary>
/// Master-only hosting steps of the composition (MN-040) that are not plain service registrations: the enroll listener's
/// Kestrel endpoint (design §3.2) and the bootstrap node's record on first boot.
/// </summary>
public static partial class MasterHosting
{
    /// <summary>Default address of the DGX agent as the master container reaches it (MN-041 compose: service <c>mirbadllm-agent</c>).</summary>
    public static readonly string DefaultBootstrapAgentAddress = $"https://mirbadllm-agent:{Protocol.DefaultAgentPort}";

    /// <summary>
    /// Adds the enroll listener (<see cref="EnrollmentOptions.Port"/>, default 8443; 0 or less = none) as a TLS endpoint
    /// served by <see cref="EnrollListenerTls"/>. Kestrel ignores the configured URLs (<c>ASPNETCORE_URLS</c>,
    /// <c>ASPNETCORE_HTTP_PORTS</c>, <c>--urls</c>) once an endpoint is added in code, so those are re-added here first:
    /// the master keeps serving the UI, <c>/api</c> and <c>/v1</c> exactly where it did. Hosts without Kestrel (the test
    /// server) skip this. Kestrel still logs one "Overriding address(es) …" warning at start (the configured addresses are
    /// served, re-added here); an information line right before it says so (W4 review MN-096).
    /// </summary>
    public static void AddEnrollListener(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WebHost.ConfigureKestrel((context, kestrel) =>
        {
            var enrollment = context.Configuration.GetSection(EnrollmentOptions.SectionName).Get<EnrollmentOptions>() ?? new EnrollmentOptions();
            if (enrollment.Port <= 0)
            {
                return;
            }

            string addresses = ListenOnConfiguredUrls(kestrel, context.Configuration);
            kestrel.ListenAnyIP(enrollment.Port, listen => kestrel.ApplicationServices.GetRequiredService<EnrollListenerTls>().ConfigureEndpoint(listen));
            ILogger logger = kestrel.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(MasterHosting));
            Log.ListenersConfigured(logger, addresses, enrollment.Port);
        });
    }

    /// <summary>
    /// Writes the bootstrap node's record on first boot (design §3.2, P1): while
    /// <see cref="EnrollmentOptions.BootstrapEnabled"/> is on and <c>nodes.json</c> has no record for
    /// <c>MirbadLLM:Nodes:BootstrapNodeId</c>, a not-yet-enrolled record with the agent address
    /// (<see cref="EnrollmentOptions.BootstrapAgentAddress"/>, default <see cref="DefaultBootstrapAgentAddress"/>) and
    /// today's global memory reserve (<see cref="MirbadLLMOptions.MemoryReserveGiB"/>). The bootstrap bundle service then
    /// writes the bundle; the agent's enrollment fills in uid, pin and approval. Call after the master identity is resolved.
    /// </summary>
    public static async Task EnsureBootstrapNodeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var enrollment = services.GetRequiredService<IOptions<EnrollmentOptions>>().Value;
        if (!enrollment.BootstrapEnabled)
        {
            return;
        }

        string nodeId = ServiceCollectionExtensions.BootstrapNodeId(services);
        var registry = services.GetRequiredService<INodeRegistry>();
        if (await registry.GetAsync(nodeId, ct).ConfigureAwait(false) is not null)
        {
            return;
        }

        string address = string.IsNullOrWhiteSpace(enrollment.BootstrapAgentAddress)
            ? DefaultBootstrapAgentAddress
            : enrollment.BootstrapAgentAddress.Trim();
        await registry.UpsertAsync(new NodeRecord
        {
            Id = nodeId,
            // Replaced by the uid the master issues at enrollment (design §3.1).
            Uid = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            Address = address,
            MemoryReserveGiB = services.GetRequiredService<IOptions<MirbadLLMOptions>>().Value.MemoryReserveGiB,
            CreatedAt = services.GetRequiredService<TimeProvider>().GetUtcNow(),
        }, ct).ConfigureAwait(false);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(MasterHosting));
        Log.BootstrapNodeCreated(logger, nodeId, address);
    }

    /// <summary>
    /// Recovery mode (design §9.4, D76; MN-130): removes every background service the mirbadllm packages registered —
    /// node sync and monitor, boot auto-start, certificate and CA rotation, takeover, telemetry collection, job and restore
    /// recovery, moves, the backup scheduler, the bootstrap bundle and the dev join tokens — so the restricted host makes
    /// no agent connection and writes no seed, CA or bootstrap material. The framework's own services (the web host, Data
    /// Protection) stay. Returns how many were removed.
    /// </summary>
    public static int RemoveBackgroundServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var ours = services.Where(d => d.ServiceType == typeof(IHostedService) && !d.IsKeyedService && IsMirbadLLM(d)).ToList();
        foreach (var descriptor in ours)
        {
            services.Remove(descriptor);
        }

        return ours.Count;
    }

    /// <summary>
    /// The restricted host's request gate (recovery mode): the login, the Backup page (its Restore tab), <c>/healthz</c>,
    /// <c>/v1</c> (503 "restoring" from the router's global window), Blazor's framework paths and static files pass; any
    /// other <c>/api</c>, <c>/metrics</c> or <c>/agent/bundles</c> request answers 503 <c>recovery_mode</c>, and any other
    /// page redirects to the Backup page.
    /// </summary>
    public static void UseRecoveryHostGate(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (context, next) =>
        {
            PathString path = context.Request.Path;
            if (IsServedInRecovery(path))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            // Nor are the agent bundles (decided with the bundle downloads): no node joins or rekeys against a master
            // that is restoring, and a curl in a join command must fail plainly instead of following a redirect.
            if (path.StartsWithSegments("/api") || path.StartsWithSegments("/metrics") || path.StartsWithSegments(Api.AgentBundleCatalog.RoutePrefix))
            {
                context.Response.StatusCode = ErrorCodes.ApiStatus(ErrorCodes.RecoveryMode);
                context.Response.Headers.RetryAfter = "60";
                await context.Response.WriteAsJsonAsync(new
                {
                    title = "Recovery mode",
                    status = context.Response.StatusCode,
                    detail = "MirbadLLM runs in recovery mode: only the Restore tab is served until a fleet backup is installed.",
                    code = ErrorCodes.RecoveryMode,
                }).ConfigureAwait(false);
                return;
            }

            context.Response.Redirect("/backup");
        });
    }

    internal static bool IsServedInRecovery(PathString path)
    {
        foreach (string prefix in RecoveryPaths)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Static assets (styles, scripts, fonts, icons): a known asset extension, outside the API and the pages with an id
        // segment. W9 review (MN-132): "any extension" let /models/qwen2.5 (a model id with a dot) render the model editor.
        foreach (string prefix in GatedPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        string extension = Path.GetExtension(path.Value ?? "");
        return StaticAssetExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static readonly string[] RecoveryPaths =
        ["/backup", "/login", "/logout", "/healthz", "/v1", "/not-found", "/Error", "/_framework", "/_blazor", "/_content"];

    /// <summary>Never static assets: the API, the metrics, the agent bundles and the pages whose last segment is an id (<c>/models/{id}</c>, <c>/nodes/{id}</c>).</summary>
    private static readonly string[] GatedPrefixes = ["/api", "/metrics", "/models", "/nodes", "/agent"];

    /// <summary>The asset types the UI serves from <c>wwwroot</c> and the component bundles.</summary>
    private static readonly string[] StaticAssetExtensions =
        [".css", ".js", ".mjs", ".map", ".woff", ".woff2", ".ttf", ".otf", ".eot", ".ico", ".png", ".svg", ".webp", ".jpg", ".jpeg", ".gif"];

    private static bool IsMirbadLLM(ServiceDescriptor descriptor)
    {
        Type? type = descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType() ?? descriptor.ImplementationFactory?.Method.DeclaringType;
        return type?.Assembly.GetName().Name?.StartsWith("MirbadLLM.", StringComparison.Ordinal) == true;
    }

    /// <summary>
    /// Listens where Kestrel would have from configuration: <c>urls</c>, else <c>http_ports</c>/<c>https_ports</c> (the
    /// container image sets <c>ASPNETCORE_HTTP_PORTS=8080</c>), else localhost:5000. Returns what it bound, for the log.
    /// </summary>
    private static string ListenOnConfiguredUrls(KestrelServerOptions kestrel, IConfiguration configuration)
    {
        string? urls = configuration[WebHostDefaults.ServerUrlsKey];
        if (!string.IsNullOrWhiteSpace(urls))
        {
            foreach (string url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Listen(kestrel, BindingAddress.Parse(url));
            }

            return urls;
        }

        int[] http = Ports(configuration[WebHostDefaults.HttpPortsKey]);
        int[] https = Ports(configuration[WebHostDefaults.HttpsPortsKey]);
        if (http.Length == 0 && https.Length == 0)
        {
            kestrel.ListenLocalhost(5000);
            return "http://localhost:5000";
        }

        foreach (int port in http)
        {
            kestrel.ListenAnyIP(port);
        }

        foreach (int port in https)
        {
            kestrel.ListenAnyIP(port, listen => listen.UseHttps());
        }

        return string.Join(';', http.Select(p => $"http://*:{p}").Concat(https.Select(p => $"https://*:{p}")));
    }

    private static void Listen(KestrelServerOptions kestrel, BindingAddress address)
    {
        bool tls = string.Equals(address.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        void Configure(ListenOptions listen)
        {
            if (tls)
            {
                listen.UseHttps();
            }
        }

        if (address.IsUnixPipe)
        {
            kestrel.ListenUnixSocket(address.UnixPipePath, Configure);
        }
        else if (string.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            kestrel.ListenLocalhost(address.Port, Configure);
        }
        else if (IPAddress.TryParse(address.Host.Trim('[', ']'), out IPAddress? ip))
        {
            kestrel.Listen(ip, address.Port, Configure);
        }
        else
        {
            // "*", "+", "0.0.0.0" spelled as a name, or a host name: Kestrel binds every interface for these.
            kestrel.ListenAnyIP(address.Port, Configure);
        }
    }

    private static int[] Ports(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => int.Parse(p, System.Globalization.CultureInfo.InvariantCulture))];

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Serving {Addresses} and the enroll listener on port {EnrollPort} (TLS). The application addresses are bound in code next to the enroll listener, so Kestrel's \"Overriding address(es)\" warning that follows is expected.")]
        public static partial void ListenersConfigured(ILogger logger, string addresses, int enrollPort);

        [LoggerMessage(Level = LogLevel.Information, Message = "Created the record of bootstrap node {NodeId} (agent address {Address}); it is enrolled through the bootstrap bundle.")]
        public static partial void BootstrapNodeCreated(ILogger logger, string nodeId, string address);
    }
}
