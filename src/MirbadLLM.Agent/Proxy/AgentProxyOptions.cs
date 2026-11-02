using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Proxy;

/// <summary>
/// Settings of the agent's inference proxy (<c>/agent/v1/proxy/{modelId}/{**path}</c>, design §7.2). Defaults are the
/// design values from <see cref="Protocol"/>; MN-026 binds them from <c>agent.json</c>.
/// </summary>
public sealed class AgentProxyOptions
{
    /// <summary>
    /// Allowed paths under <c>/proxy/{modelId}/</c>: an entry ending in <c>/*</c> allows every path below that prefix
    /// (at least one more segment), any other entry is an exact path. Anything else is 404 <c>not_found</c>, so vLLM's
    /// development endpoints stay out of reach. Ordinal (case-sensitive) matching, as vLLM routes are.
    /// </summary>
    public IList<string> AllowedPaths { get; } = [.. Protocol.DefaultProxyAllowedPaths];

    /// <summary>Requests proxied at the same time on this node; excess → 503 <c>server_busy</c> with <c>Retry-After: 5</c> (design §7.2).</summary>
    public int MaxInFlight { get; set; } = Protocol.DefaultMaxInFlightPerNode;

    /// <summary>Request body limit per proxied request (64 MiB); larger → 413 <c>invalid_request</c> before or while streaming.</summary>
    public long MaxRequestBodyBytes { get; set; } = Protocol.MaxProxyRequestBodyBytes;

    /// <summary>Connect timeout to the model (design §7.2: 10 s, no overall timeout).</summary>
    public TimeSpan UpstreamConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the model may send nothing (no response headers, or no body bytes between reads) before the request ends:
    /// 504 <c>timeout</c> before headers, an aborted stream after. A wedged engine otherwise holds clients forever while its
    /// <c>/health</c> stays green. Long enough for a long non-streamed answer under load; zero turns it off.
    /// </summary>
    public TimeSpan UpstreamIdleTimeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>Response copy buffer (16 KiB, design §7.2); each read is written and flushed before the next read.</summary>
    public int CopyBufferBytes { get; set; } = Protocol.ProxyCopyBufferBytes;

    /// <summary>
    /// How long a resolved upstream address is reused before the runtime driver is asked again (0 = ask on every
    /// request). Only positive answers are cached, and a failed connection drops the entry at once.
    /// </summary>
    public TimeSpan UpstreamCacheDuration { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary><c>Retry-After</c> seconds on 503 <c>server_busy</c> (design §7.4).</summary>
    public int BusyRetryAfterSeconds { get; set; } = 5;
}
