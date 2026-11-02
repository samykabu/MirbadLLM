using Microsoft.AspNetCore.Http;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Hosting;

/// <summary>
/// What the agent pipeline (<see cref="AgentRequestMiddleware"/>) established for one request, readable by handlers
/// through <see cref="AgentHttpContextExtensions.GetAgentRequest"/>.
/// </summary>
public sealed class AgentRequestFeature
{
    /// <summary>The catalogue route this request matched; null for a path outside <see cref="AgentRoutes.All"/>.</summary>
    public AgentRoute? Route { get; init; }

    /// <summary>The protocol version from <c>MirbadLLM-Protocol</c>; null on <c>/info</c> (no header needed).</summary>
    public int? ProtocolVersion { get; init; }

    /// <summary>
    /// The master triple of a mutating request, already accepted by <see cref="Abstractions.IFencingState.CheckAsync"/>.
    /// Record it with the operation and re-check it with <see cref="Abstractions.IFencingState.IsCurrent"/> before every
    /// destructive stage. Null on reads.
    /// </summary>
    public MasterTriple? Master { get; init; }

    /// <summary>The master's clock from <c>MirbadLLM-Request-Time</c> on a mutating request; null on reads.</summary>
    public DateTimeOffset? RequestTime { get; init; }
}

/// <summary>Accessors for <see cref="AgentRequestFeature"/>.</summary>
public static class AgentHttpContextExtensions
{
    /// <summary>The feature set by the agent pipeline; null when the request did not pass through it.</summary>
    public static AgentRequestFeature? GetAgentRequest(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<AgentRequestFeature>();
    }

    /// <summary>
    /// The accepted master triple of a mutating request. Throws when absent: a handler that needs it was reached without
    /// the fencing check, which is a wiring bug.
    /// </summary>
    public static MasterTriple GetAcceptedMaster(this HttpContext context) =>
        context.GetAgentRequest()?.Master
        ?? throw new InvalidOperationException("No accepted master triple: the request did not pass the agent fencing check.");
}
