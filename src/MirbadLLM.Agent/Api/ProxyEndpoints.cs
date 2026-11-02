using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Proxy;

namespace MirbadLLM.Agent.Api;

/// <summary>Maps the inference proxy route <c>* /agent/v1/proxy/{modelId}/{**path}</c> (design §7.2, <see cref="AgentRoutes.Proxy"/>).</summary>
public static class ProxyEndpoints
{
    /// <summary>
    /// Maps the proxy for every method. The route carries <see cref="AgentRoutes.Proxy"/> as endpoint metadata, so the
    /// host's protocol-header and listener middleware (MN-014) can recognise it. Needs <c>AddAgentProxy</c>.
    /// </summary>
    public static IEndpointConventionBuilder MapAgentProxyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints
            .Map(
                Protocol.BasePath + AgentRoutes.Proxy.Path,
                (HttpContext context, string modelId, string? path, AgentProxy proxy) => proxy.HandleAsync(context, modelId, path))
            .WithMetadata(AgentRoutes.Proxy)
            .WithDisplayName("Agent " + AgentRoutes.Proxy.Name);
    }
}
