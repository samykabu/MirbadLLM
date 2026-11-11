using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Router;

/// <summary>DI registration and endpoint mapping for the OpenAI-compatible <c>/v1/*</c> router.</summary>
public static class RouterExtensions
{
    /// <summary>Name of the typed <see cref="HttpClient"/> used to reach vLLM upstreams. Tests can point it at a
    /// fake upstream by calling <c>services.AddHttpClient(UpstreamHttpClientName).ConfigurePrimaryHttpMessageHandler(...)</c>
    /// again after <see cref="AddMirbadLLMRouter"/> — the later registration's handler wins.</summary>
    public const string UpstreamHttpClientName = "mirbadllm-upstream";

    /// <summary>Registers <see cref="IRouterStats"/> and the upstream <see cref="HttpClient"/>. Callers must
    /// separately register an <see cref="IApiKeyStore"/> (the composition root wires it from
    /// <c>MirbadLLM.Orchestration</c>; tests supply a fake) — this method only guarantees
    /// <see cref="AuthOptions"/> resolves (with its defaults) so the API-key filter never fails to resolve it.</summary>
    public static IServiceCollection AddMirbadLLMRouter(this IServiceCollection services)
    {
        services.AddSingleton<RouterStats>();
        services.AddSingleton<IRouterStats>(sp => sp.GetRequiredService<RouterStats>());
        services.AddSingleton<IChatClient, RouterChatClient>();
        services.AddOptions<AuthOptions>();
        services.AddOptions<RouterOptions>()
            .Configure<IServiceProvider>(static (options, sp) => sp.GetService<IConfiguration>()?.GetSection(RouterOptions.SectionName).Bind(options));
        services.AddOptions<RouterMaintenanceOptions>()
            .Configure<IServiceProvider>(static (options, sp) => sp.GetService<IConfiguration>()?.GetSection(RouterMaintenanceOptions.SectionName).Bind(options));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new UploadBudget(sp.GetRequiredService<IOptions<RouterOptions>>(), sp.GetRequiredService<TimeProvider>()));

        services.AddHttpClient(UpstreamHttpClientName, static client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(10),
                AutomaticDecompression = DecompressionMethods.None,
                UseProxy = false,
                Proxy = null,
            });

        return services;
    }

    /// <summary>
    /// Switches the router to the agent path (design §7): registers <see cref="AgentEndpointResolver"/> and makes it the
    /// <see cref="IModelEndpointResolver"/>, so <c>/v1/*</c>, <c>GET /v1/models</c> and <see cref="RouterChatClient"/> reach
    /// models through the placed node's agent data client (mTLS, HTTP/2) instead of <see cref="UpstreamHttpClientName"/>.
    /// Call after <see cref="AddMirbadLLMRouter"/>; the caller registers <see cref="IModelRegistry"/>,
    /// <see cref="INodeStatusCache"/> and <see cref="MirbadLLM.Agent.Contracts.IAgentClientFactory"/> (MN-040).
    /// </summary>
    public static IServiceCollection AddMirbadLLMAgentRouting(this IServiceCollection services)
    {
        services.AddSingleton<AgentEndpointResolver>();
        services.AddSingleton<IModelEndpointResolver>(sp => sp.GetRequiredService<AgentEndpointResolver>());
        return services;
    }

    /// <summary>Maps <c>GET /v1/models</c>, the catch-all <c>/v1/{**path}</c> proxy and the Ollama-compatible routes
    /// under <c>/api</c> (<see cref="OllamaApi"/>), under an API-key
    /// gate (see <see cref="ApiKeyAccess.CheckInferenceAsync"/>). Marked anonymous at the ASP.NET Core
    /// authorization level — it authenticates itself via API key, not the admin cookie — and is therefore
    /// unaffected by the app's cookie-authorization fallback policy.
    /// <para>Maintenance precedence (design §6.5 item 2, MN-110). P1 order — any global window, or any window while
    /// <see cref="RouterMaintenanceOptions.NodeScoped"/> is off: the maintenance gate answers 503 first, before the API key,
    /// the body and model resolution, <c>/v1/models</c> included (today's behaviour exactly). P2 order — only node-scoped
    /// windows open: the gate lets the request through, the API key is checked first, and the handlers check the resolved
    /// model (<c>ProxyHandler</c>: after 404, before the node states, not-ready and auto-start; <c>/v1/models</c> leaves
    /// paused models out and never answers 503 for a node-scoped window).</para></summary>
    public static IEndpointRouteBuilder MapMirbadLLMRouter(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1").AllowAnonymous();

        // Maintenance gate first (P1 order): while a global window is open every caller (keyed or not) gets 503.
        group.AddEndpointFilter(MaintenanceFilterAsync);
        group.AddEndpointFilter(ApiKeyFilterAsync);
        group.MapGet("/models", ModelsEndpoint.HandleAsync);
        group.Map("/{**path}", ProxyHandler.HandleAsync);

        // The Ollama-compatible routes (OllamaApi): exact paths under /api next to the admin API, anonymous to the admin
        // login like /v1 and behind the same two gates. Their translation filter is outermost, so the gates' answers
        // leave in Ollama's error shape too.
        var ollama = endpoints.MapGroup("/api").AllowAnonymous();
        ollama.AddEndpointFilter(OllamaApi.TranslateResponsesAsync);
        ollama.AddEndpointFilter(MaintenanceFilterAsync);
        ollama.AddEndpointFilter(ApiKeyFilterAsync);
        OllamaApi.Map(ollama);

        // Ollama's command line opens every command with HEAD / and gives up on anything but a success. The UI answers
        // only GET there, so without this the client never reaches /api. No key: the answer says nothing.
        endpoints.MapMethods("/", [HttpMethods.Head], () => Results.Ok()).AllowAnonymous();
        return endpoints;
    }

    private static async ValueTask<object?> MaintenanceFilterAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        if (RouterMaintenance.GateWindow(services.GetService<IMaintenanceMode>(), RouterMaintenance.NodeScoped(services)) is { } window)
        {
            await RouterMaintenance.WriteAsync(context.HttpContext, window);
            return Results.Empty;
        }

        return await next(context);
    }

    private static async ValueTask<object?> ApiKeyFilterAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        var options = services.GetRequiredService<IOptions<MirbadLLMOptions>>().Value;
        var authOptions = services.GetRequiredService<IOptions<AuthOptions>>().Value;
        var store = services.GetRequiredService<IApiKeyStore>();
        var ct = context.HttpContext.RequestAborted;

        var presentedKey = TryGetBearerToken(context.HttpContext.Request);
        var result = await ApiKeyAccess.CheckInferenceAsync(store, presentedKey, options.ApiKey, authOptions.RequireApiKey, ct);

        if (result == ApiKeyAccessResult.Denied)
        {
            await OpenAiError.WriteAsync(
                context.HttpContext, StatusCodes.Status401Unauthorized,
                "Missing or invalid API key.", "invalid_request_error", "invalid_api_key");
            return Results.Empty;
        }

        return await next(context);
    }

    private static string? TryGetBearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..] : null;
    }
}
