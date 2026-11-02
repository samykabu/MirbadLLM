using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Audit;

namespace MirbadLLM.Web;

/// <summary>The server-resolved portal user and audit identity shared by HTTP and Blazor actions.</summary>
/// <param name="Actor">An <see cref="AuditActors"/> value: <c>admin:{session hash}</c> for a signed-in admin, else <c>api:{ip}</c>.</param>
/// <param name="ClientKey">The session hash or request IP; never the raw session id.</param>
public sealed record AdminCaller(string Actor, string ClientKey)
{
    public ClaimsPrincipal? User { get; init; }
}

/// <summary>Uses the circuit's current authentication state when present, otherwise the HTTP request.</summary>
public static class AdminCallers
{
    /// <summary>The shared last-resort client key.</summary>
    public const string UnknownClient = "unknown";

    /// <summary>The prefix of a session-derived client key.</summary>
    public const string SessionKeyPrefix = "sid:";

    /// <summary>
    /// The client key of a caller: the session hash of <paramref name="user"/>'s (else the request
    /// user's) <c>sid</c> claim, else the remote IP of <paramref name="http"/>, else <see cref="UnknownClient"/>.
    /// </summary>
    public static string ClientKey(HttpContext? http, ClaimsPrincipal? user = null)
    {
        ClaimsPrincipal? principal = user ?? http?.User;
        if (principal?.Identity?.IsAuthenticated == true && principal.FindFirst(ClaimTypes.Sid)?.Value is { Length: > 0 } sid)
        {
            return SessionKeyPrefix + AuditActorResolver.SessionHash(sid);
        }

        return http?.Connection.RemoteIpAddress?.ToString() ?? UnknownClient;
    }

    /// <summary>The caller of an HTTP request.</summary>
    public static AdminCaller FromRequest(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return new AdminCaller(AuditActorResolver.Resolve(http), ClientKey(http)) { User = http.User };
    }

    /// <summary>
    /// The caller of a Blazor page: the circuit's signed-in user (from <paramref name="state"/>, else the registered
    /// <see cref="AuthenticationStateProvider"/>) decides the actor and client key; the request, when one is at hand
    /// (prerender), adds its IP for an anonymous caller. Never throws for a missing authentication state.
    /// </summary>
    public static async Task<AdminCaller> ResolveAsync(IServiceProvider services, Task<AuthenticationState>? state = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        HttpContext? http = services.GetService<IHttpContextAccessor>()?.HttpContext;
        ClaimsPrincipal? user = null;
        try
        {
            if (state is not null)
            {
                user = (await state.ConfigureAwait(false)).User;
            }
            else if (services.GetService<AuthenticationStateProvider>() is { } provider)
            {
                user = (await provider.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // No authentication state in this render (prerender without a circuit, a test host without a provider): fall
            // back to the request. Resolving a caller never fails the page.
        }

        user ??= http?.User;
        string clientKey = ClientKey(http, user);
        if (user?.Identity?.IsAuthenticated == true)
        {
            string basis = user.FindFirst(ClaimTypes.Sid)?.Value ?? user.Identity.Name ?? "admin";
            return new AdminCaller(AuditActors.Admin(AuditActorResolver.SessionHash(basis)), clientKey) { User = user };
        }

        return new AdminCaller(AuditActors.ApiCaller(clientKey), clientKey) { User = user };
    }
}
