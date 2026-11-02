using System.Text.Json;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Web.Enrollment;

/// <summary>
/// <c>POST /enroll</c> (design §3.2, <see cref="AgentRoutes.Enroll"/>), <c>POST /enroll/rekey</c> (<see cref="AgentRoutes.Rekey"/>,
/// <see cref="RekeyEndpoint"/>) and the enrollment services. MN-040 composes them:
/// <code>
/// builder.Services.AddMirbadLLMEnrollment(builder.Configuration);   // needs CertificateAuthority + INodeRegistry
/// kestrel.ListenAnyIP(enrollOptions.Port, kestrel.ApplicationServices.GetRequiredService&lt;EnrollListenerTls&gt;().ConfigureEndpoint);
/// app.UseEnrollListenerGate();                                        // before routing: only POST /enroll(/rekey) on that port
/// app.MapEnrollEndpoint();                                            // POST /enroll and POST /enroll/rekey (MN-114)
/// </code>
/// </summary>
public static class EnrollEndpoints
{
    /// <summary>Connection item set by <see cref="EnrollListenerTls.ConfigureEndpoint"/> on every enroll-listener connection.</summary>
    public const string ListenerConnectionMarker = "MirbadLLM.EnrollListener";

    /// <summary>
    /// Keeps the enroll listener to its routes (<see cref="AgentRoutes.EnrollListener"/>: <c>POST /enroll</c> and
    /// <c>POST /enroll/rekey</c>): any other request on an enroll-listener connection gets 404, so the
    /// admin UI, <c>/api</c> and <c>/v1</c> are not reachable on the enroll port when it shares the master's pipeline.
    /// MN-040 calls it before routing.
    /// </summary>
    public static IApplicationBuilder UseEnrollListenerGate(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use((context, next) =>
        {
            if (IsEnrollListenerConnection(context)
                && !(HttpMethods.IsPost(context.Request.Method) && IsEnrollListenerPath(context.Request.Path.Value)))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }

            return next(context);
        });
    }

    private static bool IsEnrollListenerPath(string? path)
    {
        foreach (AgentRoute route in AgentRoutes.EnrollListener)
        {
            if (string.Equals(path, route.Path, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the request arrived on a connection of the enroll listener (TLS, <see cref="EnrollListenerTls"/>).</summary>
    public static bool IsEnrollListenerConnection(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Request.IsHttps
            && context.Features.Get<IConnectionItemsFeature>() is { } connection
            && connection.Items.TryGetValue(ListenerConnectionMarker, out object? marker)
            && marker is true;
    }

    /// <summary>
    /// Registers <see cref="EnrollmentOptions"/> (<c>MirbadLLM:Enrollment</c>), <see cref="EnrollRateLimiter"/>,
    /// <see cref="EnrollListenerTls"/>, <see cref="EnrollmentService"/>, <see cref="RekeyService"/>, <see cref="BootstrapBundleWriter"/> and the
    /// <see cref="BootstrapBundleService"/> that keeps the bundle current. The host registers the
    /// <see cref="MirbadLLM.Pki.CertificateAuthority"/> and <see cref="MirbadLLM.Core.INodeRegistry"/>.
    /// </summary>
    public static IServiceCollection AddMirbadLLMEnrollment(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<EnrollmentOptions>().Bind(configuration.GetSection(EnrollmentOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<EnrollRateLimiter>();
        services.TryAddSingleton<EnrollListenerTls>();
        services.TryAddSingleton<EnrollmentService>();
        services.TryAddSingleton<RekeyService>();
        services.TryAddSingleton<BootstrapBundleWriter>();
        services.AddHostedService<BootstrapBundleService>();
        return services;
    }

    /// <summary>
    /// Maps <c>POST /enroll</c>, anonymous (the token is the credential). The handler answers only on enroll-listener
    /// connections (<see cref="IsEnrollListenerConnection"/>; the master's other ports get 404), applies the per-IP rate limit before reading the body, limits
    /// the body to <see cref="EnrollmentOptions.MaxRequestBodyBytes"/>, and answers 200 <see cref="EnrollResponse"/>,
    /// 400 <c>invalid_request</c>, 403 <c>enrollment_refused</c> (one generic answer, never echoing the token record) or
    /// 429 <c>rate_limited</c>. Also maps <c>POST /enroll/rekey</c> (<see cref="RekeyEndpoint"/>, MN-114) with the same
    /// handling and the same rate-limit budget, so the host's composition is unchanged. Returns the <c>/enroll</c> builder.
    /// </summary>
    public static RouteHandlerBuilder MapEnrollEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapRekeyEndpoint();
        return endpoints.MapPost(AgentRoutes.Enroll.Path, (HttpContext http, EnrollmentService service, EnrollRateLimiter limiter, ILoggerFactory loggers, CancellationToken ct) =>
                HandleEnrollListenerAsync<EnrollRequest>(http, limiter, loggers, (request, client, token) => service.EnrollAsync(request, client, token), ct))
            .AllowAnonymous()
            .DisableAntiforgery();
    }

    /// <summary>
    /// The shared handling of an enroll-listener route: listener connections only, the per-IP budget (one for both
    /// routes) before the body is read, the body limit, JSON of <typeparamref name="TRequest"/>, and one generic answer
    /// per <see cref="EnrollmentStatus"/>.
    /// </summary>
    internal static async Task<IResult> HandleEnrollListenerAsync<TRequest>(
        HttpContext http,
        EnrollRateLimiter limiter,
        ILoggerFactory loggers,
        Func<TRequest?, System.Net.IPAddress?, CancellationToken, Task<EnrollmentResult>> decide,
        CancellationToken ct)
        where TRequest : class
    {
        if (!IsEnrollListenerConnection(http))
        {
            return Results.NotFound();
        }

        string client = AdminPasswordGuard.ClientKey(http);
        if (!limiter.TryAcquire(client))
        {
            EnrollmentLog.RateLimited(loggers.CreateLogger(typeof(EnrollEndpoints)), client);
            return Error(StatusCodes.Status429TooManyRequests, ErrorCodes.RateLimited, "Too many enroll attempts. Try again in a minute.");
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        {
            size.MaxRequestBodySize = EnrollmentOptions.MaxRequestBodyBytes;
        }

        if (http.Request.ContentLength > EnrollmentOptions.MaxRequestBodyBytes)
        {
            return Error(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, "The enroll request is too large.");
        }

        TRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<TRequest>(http.Request.Body, AgentJson.Options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException or NotSupportedException)
        {
            return Error(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, "The enroll request is not valid JSON of the expected shape.");
        }

        var result = await decide(request, http.Connection.RemoteIpAddress, ct).ConfigureAwait(false);
        return result.Status switch
        {
            EnrollmentStatus.Enrolled => Results.Json(result.Response, AgentJson.Options, AgentJson.JsonContentType, StatusCodes.Status200OK),
            EnrollmentStatus.Invalid => Error(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, "The enroll request is malformed."),
            _ => Error(StatusCodes.Status403Forbidden, ErrorCodes.EnrollmentRefused, "Enrollment refused."),
        };
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new AgentErrorResponse(new AgentError { Code = code, Message = message }), AgentJson.Options, AgentJson.JsonContentType, status);
}
