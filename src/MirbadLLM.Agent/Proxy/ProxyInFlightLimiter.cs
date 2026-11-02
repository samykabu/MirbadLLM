namespace MirbadLLM.Agent.Proxy;

/// <summary>
/// Per-node bound on proxied requests in flight (design §7.2 "Bounded resources"): a slot is held from admission until
/// the response is fully copied or aborted. No queueing: a request that finds no slot gets 503 <c>server_busy</c> at once,
/// and the master (which has its own limit) translates it for the client.
/// </summary>
internal sealed class ProxyInFlightLimiter(int limit)
{
    private int _inFlight;

    /// <summary>The configured limit.</summary>
    public int Limit { get; } = limit > 0 ? limit : throw new ArgumentOutOfRangeException(nameof(limit), limit, "The in-flight limit must be positive.");

    /// <summary>Requests currently holding a slot.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>Takes a slot; false when all are taken. A true result must be paired with exactly one <see cref="Release"/>.</summary>
    public bool TryAcquire()
    {
        while (true)
        {
            int current = Volatile.Read(ref _inFlight);
            if (current >= Limit)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _inFlight, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    /// <summary>Returns a slot taken by <see cref="TryAcquire"/>.</summary>
    public void Release() => Interlocked.Decrement(ref _inFlight);
}
