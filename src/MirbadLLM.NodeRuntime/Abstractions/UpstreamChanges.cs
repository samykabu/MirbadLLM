using System.Collections.Concurrent;

namespace MirbadLLM.NodeRuntime.Abstractions;

/// <summary>
/// A per-model generation counter a runtime driver bumps whenever a model's upstream address can have changed — an
/// instance was created, adopted, replaced or removed. The agent proxy's upstream cache
/// (<c>RuntimeDriverUpstreamResolver</c>) stamps every cached answer with the generation it read before asking the
/// driver and discards the entry when the generation has moved, so a replaced instance is never proxied to its old
/// address (MN-239 bug B).
/// <para><b>Why a counter and not a callback.</b> The resolver depends on every <see cref="IRuntimeDriver"/>, so a
/// driver that depended on the resolver would close a DI cycle. Both sides depend on this one plain singleton instead.
/// A missing registration is not fatal: a driver constructed without it simply never bumps, which is the behaviour
/// before this class existed.</para>
/// <para><b>Why the process driver needs it.</b> A container's upstream is <c>http://ss-{id}:8000/</c> whatever instance
/// serves it, so a replacement keeps the address; a native process is reached at <c>http://127.0.0.1:{port}/</c> and a
/// relaunch (a tuning candidate, an operator restart) usually lands on a different port. Within the
/// <c>UpstreamCacheDuration</c> the proxy then sent the request to the port nobody listens on any more, answered 503
/// <c>model_not_running</c>, and the tuning run recorded the candidate <c>Failed</c>.</para>
/// </summary>
public sealed class UpstreamChanges
{
    private readonly ConcurrentDictionary<string, long> _generations = new(StringComparer.Ordinal);

    /// <summary>The current generation of <paramref name="modelId"/>; 0 until something changed.</summary>
    public long Generation(string modelId)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        return _generations.TryGetValue(modelId, out long generation) ? generation : 0;
    }

    /// <summary>Records that <paramref name="modelId"/>'s upstream address may now be different.</summary>
    public void Changed(string modelId)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        _generations.AddOrUpdate(modelId, 1, static (_, generation) => generation + 1);
    }
}
