using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace MirbadLLM.Web.Enrollment;

/// <summary>
/// The per-IP budget of <c>/enroll</c> (design §3.2: the same 5/minute per IP policy as <c>/login/submit</c>): a fixed
/// window per client IP, no queue. It is its own budget, so enroll attempts cannot lock the operator out of the login
/// form. Applied inside the handler, so it holds whichever pipeline the enroll listener is composed into.
/// </summary>
public sealed class EnrollRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public EnrollRateLimiter(IOptions<EnrollmentOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        int permits = Math.Max(1, options.Value.RateLimitPermits);
        var window = options.Value.RateLimitWindow > TimeSpan.Zero ? options.Value.RateLimitWindow : AdminPasswordGuard.Window;
        _limiter = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = window,
                QueueLimit = 0,
            }));
    }

    /// <summary>Takes one attempt from <paramref name="clientKey"/>'s budget; false when it is used up.</summary>
    public bool TryAcquire(string clientKey)
    {
        using var lease = _limiter.AttemptAcquire(clientKey);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
