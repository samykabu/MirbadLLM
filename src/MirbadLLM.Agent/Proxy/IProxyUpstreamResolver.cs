using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.Agent.Proxy;

/// <summary>
/// Where the agent proxy sends a model's requests (design §7.2): <c>http://ss-{id}:8000/</c> on the <c>mirbadllm</c>
/// Docker network, else <c>http://127.0.0.1:{hostPort}/</c>. Model ports are never reachable from the LAN.
/// </summary>
public interface IProxyUpstreamResolver
{
    /// <summary>The base address of the model's running instance; null when it is not running (→ 503 <c>model_not_running</c>).</summary>
    ValueTask<Uri?> ResolveAsync(string modelId, CancellationToken ct);

    /// <summary>Called when a connection to <paramref name="upstream"/> failed before any response; drops cached state for it.</summary>
    void Invalidate(string modelId, Uri upstream);
}

/// <summary>
/// The production resolver: asks the runtime drivers (<see cref="IRuntimeDriver.GetUpstreamAsync"/>) and keeps a positive
/// answer for <see cref="AgentProxyOptions.UpstreamCacheDuration"/>, so a stream of requests does not cost a Docker
/// call each. A stale entry is harmless: a stopped instance refuses the connection, which answers
/// <c>model_not_running</c> and drops the entry.
/// <para><b>Per instance, not per driver</b> (MN-230, D130): a node may have both a container driver and a process driver
/// (a Windows PC could, and the dev setup does), and a model is owned by exactly one of them. Taking a single
/// <see cref="IRuntimeDriver"/> let DI hand over the last registration, so a process model's upstream resolved as "not
/// running". It now asks every driver — the one that answered for this model id last, first — and remembers the owner, so
/// the steady state is one call to one driver, as before. <see cref="AgentProxy"/>'s key lookup
/// (<see cref="IRuntimeDriver.GetUpstreamApiKeyAsync"/>) already walks every driver.</para>
/// <para><b>Invalidated when an instance is replaced</b> (MN-239 bug B): the TTL alone was wrong for process delivery.
/// A relaunch — a tuning candidate, an operator restart — lands on a new loopback port, and for up to
/// <see cref="AgentProxyOptions.UpstreamCacheDuration"/> the proxy kept sending requests to the old one, answered 503
/// <c>model_not_running</c> and made the tuning run record the candidate <c>Failed</c>
/// (observed: <c>AgentProxy[1902] … at http://127.0.0.1:22577/ did not answer … actively refused it</c>). Every entry
/// now carries the <see cref="UpstreamChanges"/> generation read <i>before</i> the driver was asked, and a generation
/// that has moved discards it, so a cached address can never outlive its instance. The TTL still does its job of not
/// costing a Docker call per request in the steady state.</para>
/// </summary>
internal sealed class RuntimeDriverUpstreamResolver(
    IEnumerable<IRuntimeDriver> drivers, IOptions<AgentProxyOptions> options, TimeProvider time, UpstreamChanges changes)
    : IProxyUpstreamResolver
{
    private readonly IRuntimeDriver[] _drivers = [.. drivers];
    private readonly ConcurrentDictionary<string, (Uri Upstream, DateTimeOffset Expires, long Generation)> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IRuntimeDriver> _owners = new(StringComparer.Ordinal);

    public async ValueTask<Uri?> ResolveAsync(string modelId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        TimeSpan ttl = options.Value.UpstreamCacheDuration;
        DateTimeOffset now = time.GetUtcNow();
        // Read before asking: a change that races the driver's answer leaves the entry stamped with the older
        // generation, so the next request re-asks rather than trusting an address that may already be gone.
        long generation = changes.Generation(modelId);
        if (ttl > TimeSpan.Zero && _cache.TryGetValue(modelId, out var entry) && entry.Expires > now)
        {
            if (entry.Generation == generation)
            {
                return entry.Upstream;
            }

            _cache.TryRemove(KeyValuePair.Create(modelId, entry));
        }

        Uri? upstream = await AskDriversAsync(modelId, ct).ConfigureAwait(false);
        if (upstream is null)
        {
            _cache.TryRemove(modelId, out _);
            _owners.TryRemove(modelId, out _);
        }
        else if (ttl > TimeSpan.Zero)
        {
            _cache[modelId] = (upstream, now + ttl, generation);
        }

        return upstream;
    }

    public void Invalidate(string modelId, Uri upstream)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        // Remove only the entry that failed: a concurrent request may already have cached a newer address.
        if (_cache.TryGetValue(modelId, out var entry) && entry.Upstream == upstream)
        {
            _cache.TryRemove(KeyValuePair.Create(modelId, entry));
        }
    }

    /// <summary>
    /// The remembered owner first, then every other driver; the first non-null answer wins and becomes the owner.
    /// <para>A driver that throws is skipped, not fatal (MN-234): on a node with two drivers — which is what this fold
    /// exists for, and what MN-236's Windows dev setup composes (the fake runtime next to the process driver) — a Docker
    /// outage makes the container driver throw, and that must not stop a healthy llama.cpp model's <c>/v1</c> traffic.
    /// With one driver the behaviour is unchanged: nothing else answers, so the resolve ends as "not running".</para>
    /// </summary>
    private async ValueTask<Uri?> AskDriversAsync(string modelId, CancellationToken ct)
    {
        _owners.TryGetValue(modelId, out var owner);
        if (owner is not null && await AskAsync(owner, modelId, ct).ConfigureAwait(false) is { } known)
        {
            return known;
        }

        foreach (var driver in _drivers)
        {
            if (ReferenceEquals(driver, owner))
            {
                continue; // just asked
            }

            if (await AskAsync(driver, modelId, ct).ConfigureAwait(false) is { } upstream)
            {
                _owners[modelId] = driver;
                return upstream;
            }
        }

        return null;
    }

    private static async ValueTask<Uri?> AskAsync(IRuntimeDriver driver, string modelId, CancellationToken ct)
    {
        try
        {
            return await driver.GetUpstreamAsync(modelId, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null; // this driver cannot say (an unreachable engine); the next one may own the model
        }
    }
}
