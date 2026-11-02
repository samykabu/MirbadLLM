using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Web;

/// <summary>Login attempts: 5 per minute per client key, with no queue. Portal actions use their authenticated session.</summary>
public sealed class AdminPasswordGuard : IDisposable
{
    public const int PermitLimit = 5;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = PermitLimit,
            Window = Window,
            QueueLimit = 0,
        }));

    /// <summary>
    /// The client key of a request (<see cref="AdminCallers.ClientKey"/>): the signed-in session's <c>sid:{hash}</c>, else
    /// the remote IP, else "unknown". The login form (no session yet) is keyed by IP.
    /// </summary>
    public static string ClientKey(HttpContext? http) => AdminCallers.ClientKey(http);

    /// <summary>Takes one attempt from <paramref name="clientKey"/>'s budget; false when it is used up.</summary>
    public bool TryAcquire(string clientKey)
    {
        using var lease = _limiter.AttemptAcquire(clientKey);
        return lease.IsAcquired;
    }

    /// <summary>The login policy's partition: a view onto the same per-client limiter.</summary>
    public RateLimitPartition<string> LoginPartition(HttpContext http)
    {
        string key = ClientKey(http);
        return RateLimitPartition.Get(key, k => new SharedLimiter(_limiter, k));
    }

    public void Dispose() => _limiter.Dispose();

    /// <summary>Forwards to one partition of the shared limiter; disposing it leaves the shared state alone.</summary>
    private sealed class SharedLimiter(PartitionedRateLimiter<string> inner, string key) : RateLimiter
    {
        public override TimeSpan? IdleDuration => null;

        public override RateLimiterStatistics? GetStatistics() => inner.GetStatistics(key);

        protected override RateLimitLease AttemptAcquireCore(int permitCount) => inner.AttemptAcquire(key, permitCount);

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            inner.AcquireAsync(key, permitCount, cancellationToken);
    }
}

/// <summary>Authorizes a sensitive portal action from its server-resolved admin session.</summary>
public sealed class AdminReauthenticator(IOptions<AuthOptions> authOptions) : IAdminReauthenticator
{
    public bool LoginEnabled => !string.IsNullOrEmpty(authOptions.Value.AdminPassword);

    public AdminReauthOutcome Verify(System.Security.Claims.ClaimsPrincipal? user)
    {
        if (!LoginEnabled) return AdminReauthOutcome.LoginDisabled;
        return user?.Identity?.IsAuthenticated == true && SessionCookieValidation.HasSessionId(user)
            ? AdminReauthOutcome.Verified : AdminReauthOutcome.LoginRequired;
    }
}

public static class AdminReauthenticationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AdminPasswordGuard"/>, <see cref="IAdminReauthenticator"/>, the HTTP context accessor and
    /// <see cref="SessionCookieValidation"/> (legacy cookies without a session id are signed out so every action has a distinct audit identity).
    /// </summary>
    public static IServiceCollection AddMirbadLLMAdminReauth(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<AdminPasswordGuard>();
        services.AddSingleton<IAdminReauthenticator, AdminReauthenticator>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>, SessionCookieValidation>());
        return services;
    }

    /// <summary>The login form's rate-limiter policy, drawing from the shared <see cref="AdminPasswordGuard"/> budget.</summary>
    public static RateLimiterOptions AddAdminPasswordPolicy(this RateLimiterOptions options, string policyName) =>
        options.AddPolicy(policyName, http => http.RequestServices.GetRequiredService<AdminPasswordGuard>().LoginPartition(http));
}
