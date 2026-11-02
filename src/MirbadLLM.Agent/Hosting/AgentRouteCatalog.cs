using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Hosting;

/// <summary>
/// Finds the <see cref="AgentRoute"/> of a matched endpoint so the pipeline applies the right checks (protocol header,
/// mutation headers, fencing): first the route attached as endpoint metadata (<see cref="AgentEndpointExtensions.MapAgentRoute"/>),
/// otherwise the catalogue entry whose method and full pattern (<see cref="Protocol.BasePath"/> + <see cref="AgentRoute.Path"/>)
/// equal the endpoint's, so an area that maps <c>MapPost(Protocol.BasePath + route.Path, …)</c> directly is still covered.
/// </summary>
internal sealed class AgentRouteCatalog
{
    private readonly Dictionary<string, AgentRoute> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public AgentRouteCatalog()
        : this(AgentRoutes.All)
    {
    }

    // Internal on purpose (W4 review MN-096): with two public constructors DI picks this one and resolves an empty
    // IEnumerable<AgentRoute>, leaving group-mapped routes unknown to the pipeline (the MN-026 bug).
    internal AgentRouteCatalog(IEnumerable<AgentRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        foreach (AgentRoute route in routes)
        {
            _byKey[Key(route.Method, Protocol.BasePath + route.Path)] = route;
        }
    }

    public AgentRoute? Find(Endpoint? endpoint, string method)
    {
        if (endpoint is null)
        {
            return null;
        }

        if (endpoint.Metadata.GetMetadata<AgentRoute>() is { } attached)
        {
            return attached;
        }

        if (endpoint is not RouteEndpoint { RoutePattern.RawText: { } raw })
        {
            return null;
        }

        string pattern = Normalize(raw);
        return _byKey.TryGetValue(Key(method, pattern), out AgentRoute? route) ||
               _byKey.TryGetValue(Key(AgentHttpMethods.Any, pattern), out route)
            ? route
            : null;
    }

    private static string Key(string method, string pattern) => method.ToUpperInvariant() + " " + Normalize(pattern);

    private static string Normalize(string pattern)
    {
        string trimmed = pattern.Trim();
        if (!trimmed.StartsWith('/'))
        {
            trimmed = "/" + trimmed;
        }

        return trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;
    }
}

/// <summary>Maps agent routes from the <see cref="AgentRoutes"/> catalogue (one extension method per area calls this).</summary>
public static class AgentEndpointExtensions
{
    /// <summary>
    /// Maps <paramref name="route"/> at <see cref="Protocol.BasePath"/> + <see cref="AgentRoute.Path"/> with its method
    /// (any method for <see cref="AgentHttpMethods.Any"/>) and attaches the route as endpoint metadata, so the agent
    /// pipeline applies its protocol, freshness and fencing checks.
    /// </summary>
    public static RouteHandlerBuilder MapAgentRoute(this IEndpointRouteBuilder endpoints, AgentRoute route, Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(handler);
        string pattern = Protocol.BasePath + route.Path;
        RouteHandlerBuilder builder = route.Method == AgentHttpMethods.Any
            ? endpoints.Map(pattern, handler)
            : endpoints.MapMethods(pattern, [route.Method], handler);
        return builder.WithMetadata(route);
    }
}
