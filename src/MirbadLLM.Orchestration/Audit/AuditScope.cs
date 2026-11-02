using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Audit;

/// <summary>
/// The ambient actor of the code running now (design §10.5), flowing with the async context:
/// <list type="bullet">
/// <item><see cref="LegacyApiAuditFilter"/> opens a <b>request</b> scope around an audited pre-P2 <c>/api</c> call; agent
/// operations submitted inside it are collected (<see cref="OperationIds"/>) and the filter's line carries the first one,
/// so master and agent lines join on the operation id. The operation-reference observer writes no second line for them.</item>
/// <item>A background service opens a <b>system</b> scope (<see cref="Begin"/>, e.g. <c>using var _ = AuditScope.Begin(AuditActors.SystemAutoStart);</c>
/// around boot auto-start): the observer then writes one line per agent operation with that actor.</item>
/// </list>
/// Work that outlives its scope (a job task started by a request) sees no scope once it was disposed.
/// </summary>
public sealed class AuditScope : IDisposable
{
    private static readonly AsyncLocal<AuditScope?> Ambient = new();

    private readonly AuditScope? _previous;
    private readonly ConcurrentQueue<string> _operationIds = new();
    private int _disposed;

    private AuditScope(string actor, bool isRequest)
    {
        Actor = actor;
        IsRequest = isRequest;
        _previous = Ambient.Value;
        Ambient.Value = this;
    }

    /// <summary>The open scope of the current async flow, or null.</summary>
    public static AuditScope? Current
    {
        get
        {
            for (AuditScope? scope = Ambient.Value; scope is not null; scope = scope._previous)
            {
                if (!scope.IsDisposed)
                {
                    return scope;
                }
            }

            return null;
        }
    }

    /// <summary>The actor (<see cref="AuditActors"/>).</summary>
    public string Actor { get; }

    /// <summary>True for a request scope: the request filter writes the line.</summary>
    public bool IsRequest { get; }

    /// <summary>Agent operation ids submitted inside the scope, in order.</summary>
    public IReadOnlyList<string> OperationIds => [.. _operationIds];

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Opens a system scope (background work) for <paramref name="actor"/>.</summary>
    public static AuditScope Begin(string actor)
    {
        ArgumentException.ThrowIfNullOrEmpty(actor);
        return new AuditScope(actor, isRequest: false);
    }

    /// <summary>Opens a request scope (the request filter).</summary>
    internal static AuditScope BeginRequest(string actor) => new(actor, isRequest: true);

    /// <summary>Records an agent operation submitted inside the scope.</summary>
    internal void AddOperation(string operationId) => _operationIds.Enqueue(operationId);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && ReferenceEquals(Ambient.Value, this))
        {
            Ambient.Value = _previous;
        }
    }
}

/// <summary>
/// Who made an HTTP request, as an audit actor (design §10.5): <c>admin:{12 hex}</c> for a signed-in admin (a SHA-256 of
/// the session: the <c>sid</c> claim when the login sets one, else the authentication cookie), <c>api:{ip}</c> otherwise.
/// The session value itself is never written. Shared by the request filter and the P2 API tasks.
/// </summary>
public static class AuditActorResolver
{
    private const string AuthCookiePrefix = ".AspNetCore.Cookies";

    /// <summary>The actor of <paramref name="http"/>.</summary>
    public static string Resolve(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.User.Identity?.IsAuthenticated == true)
        {
            string basis = http.User.FindFirst(ClaimTypes.Sid)?.Value
                ?? SessionCookie(http)
                ?? http.User.Identity.Name
                ?? "admin";
            return AuditActors.Admin(SessionHash(basis));
        }

        return AuditActors.ApiCaller(http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    /// <summary>The first 12 hex characters of the SHA-256 of a session value.</summary>
    public static string SessionHash(string session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(session)))[..12];
    }

    private static string? SessionCookie(HttpContext http)
    {
        StringBuilder? value = null;
        foreach (KeyValuePair<string, string> cookie in http.Request.Cookies.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            if (cookie.Key.StartsWith(AuthCookiePrefix, StringComparison.Ordinal))
            {
                (value ??= new StringBuilder()).Append(cookie.Value);
            }
        }

        return value?.ToString();
    }
}
