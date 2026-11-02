using System.Net;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Hosting;

/// <summary>
/// Settings of the agent's mTLS listener and request pipeline (design §4.1, §4.2, §7.2), passed to
/// <see cref="AgentHostBuilder.Configure"/>. The agent's full configuration (<c>agent.json</c>, MN-026) carries these.
/// </summary>
public sealed class AgentHostOptions
{
    /// <summary>
    /// Default request body limit: today's 64 MiB proxy limit (<see cref="Protocol.MaxProxyRequestBodyBytes"/>) plus 8 MiB
    /// headroom for HTTP framing and JSON envelopes (design §7.2, MN-010: Kestrel's default ≈ 28.6 MiB would 413 bodies
    /// the master accepts).
    /// </summary>
    public const long DefaultMaxRequestBodyBytes = Protocol.MaxProxyRequestBodyBytes + (8L * 1024 * 1024);

    /// <summary>
    /// The agent's state directory (<c>/var/lib/mirbadllm-agent</c> or the DGX named volume): <c>agent.key</c>,
    /// <c>agent.crt</c>, <c>ca.crt</c>, <c>node-uid</c> and the accepted master triple (<c>fencing.json</c>). Required.
    /// </summary>
    public string StateDirectory { get; set; } = "";

    /// <summary>Address to listen on; null, empty or <c>*</c> = every address (IPv4 and IPv6).</summary>
    public string? ListenAddress { get; set; }

    /// <summary>mTLS port (design §4.1, default 9443). 0 picks a free port (tests).</summary>
    public int Port { get; set; } = Protocol.DefaultAgentPort;

    /// <summary>Kestrel <c>Limits.MaxRequestBodySize</c>; at least <see cref="Protocol.MaxProxyRequestBodyBytes"/>.</summary>
    public long MaxRequestBodyBytes { get; set; } = DefaultMaxRequestBodyBytes;

    /// <summary>Agent version reported by <c>/info</c> and <c>/capabilities</c>; null = the assembly's informational version.</summary>
    public string? AgentVersion { get; set; }

    /// <summary>
    /// Test hook only: when false, requests without a client certificate on the connection are not refused with 403 by
    /// the request pipeline (in-process <c>TestServer</c> hosts have no TLS). The TLS listener itself always requires and
    /// validates the master's certificate; this flag cannot weaken it.
    /// </summary>
    internal bool RequireClientCertificateOnRequests { get; set; } = true;

    /// <summary>Throws <see cref="ArgumentException"/> when a setting is unusable.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(StateDirectory))
        {
            throw new ArgumentException("The agent state directory is required.", nameof(StateDirectory));
        }

        if (Port is < 0 or > IPEndPoint.MaxPort)
        {
            throw new ArgumentException($"Port {Port} is out of range.", nameof(Port));
        }

        if (MaxRequestBodyBytes < Protocol.MaxProxyRequestBodyBytes)
        {
            throw new ArgumentException(
                $"MaxRequestBodyBytes must be at least {Protocol.MaxProxyRequestBodyBytes} (the 64 MiB proxy limit).",
                nameof(MaxRequestBodyBytes));
        }

        _ = ParseListenAddress();
    }

    /// <summary>The parsed <see cref="ListenAddress"/>; null = every address.</summary>
    internal IPAddress? ParseListenAddress()
    {
        if (string.IsNullOrWhiteSpace(ListenAddress) || ListenAddress.Trim() == "*")
        {
            return null;
        }

        return IPAddress.TryParse(ListenAddress.Trim(), out IPAddress? address)
            ? address
            : throw new ArgumentException($"Listen address '{ListenAddress}' is not an IP address.", nameof(ListenAddress));
    }
}
