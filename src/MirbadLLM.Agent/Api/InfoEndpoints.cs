using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;

namespace MirbadLLM.Agent.Api;

/// <summary>
/// <c>GET /agent/v1/info</c> and <c>GET /agent/v1/capabilities</c> (design §3.3, §3.6, §4.3): identity, protocol range,
/// boot and the accepted master triple (negotiation input; no <c>MirbadLLM-Protocol</c> header needed), and the
/// capability report.
/// </summary>
public static class InfoEndpoints
{
    /// <summary>Maps <see cref="AgentRoutes.Info"/> and <see cref="AgentRoutes.Capabilities"/>.</summary>
    public static IEndpointRouteBuilder MapInfoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapAgentRoute(AgentRoutes.Info, GetInfo);
        endpoints.MapAgentRoute(AgentRoutes.Capabilities, GetCapabilitiesAsync);
        return endpoints;
    }

    /// <summary>Builds the <c>/info</c> answer.</summary>
    internal static InfoResponse BuildInfo(IAgentCredentials credentials, AgentBootInfo boot, IFencingState fencing, TimeProvider time) => new()
    {
        NodeId = credentials.NodeId,
        NodeUid = credentials.NodeUid,
        AgentVersion = boot.AgentVersion,
        Protocol = Protocol.Range,
        BootId = boot.BootId,
        StartedAt = boot.StartedAt,
        Time = time.GetUtcNow(),
        AcceptedMaster = fencing.Accepted,
    };

    private static IResult GetInfo(IAgentCredentials credentials, AgentBootInfo boot, IFencingState fencing, TimeProvider time) =>
        Results.Json(BuildInfo(credentials, boot, fencing, time), AgentJson.Options);

    private static async Task<IResult> GetCapabilitiesAsync(AgentCapabilityReporter reporter, CancellationToken ct) =>
        Results.Json(await reporter.GetAsync(ct).ConfigureAwait(false), AgentJson.Options);
}
