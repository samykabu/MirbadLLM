using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Events;

namespace MirbadLLM.Agent.Api;

/// <summary>
/// <c>GET /status</c> and <c>GET /events</c> (design §4.3, §4.7). Mapped relative to the route builder it is given:
/// the host (MN-014/MN-026) passes its <see cref="Protocol.BasePath"/> group, which also applies mTLS and the
/// <c>MirbadLLM-Protocol</c> check.
/// <para>Services: <see cref="IEventBus"/>, <see cref="AgentStatusView"/> (the one the bus applies events to),
/// <see cref="INodeMemoryReader"/>, every <see cref="IHealthContributor"/>, and optionally a <see cref="TimeProvider"/>.</para>
/// </summary>
public static class StatusEndpoints
{
    /// <summary>Maps <see cref="AgentRoutes.Status"/> and <see cref="AgentRoutes.Events"/>.</summary>
    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(AgentRoutes.Status.Path, (HttpContext http, CancellationToken ct) => GetStatusAsync(http.RequestServices, ct));
        endpoints.MapGet(AgentRoutes.Events.Path, StreamEventsAsync);
        return endpoints;
    }

    /// <summary>
    /// Builds the consistent snapshot: health and memory are probed first (they are not event-sourced), then runtimes,
    /// operations, downloads and reservations are read from the view under the state lock with the exact watermark.
    /// </summary>
    public static async Task<IResult> GetStatusAsync(IServiceProvider services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        var bus = services.GetRequiredService<IEventBus>();
        var view = services.GetRequiredService<AgentStatusView>();
        var memoryReader = services.GetRequiredService<INodeMemoryReader>();
        var time = services.GetService<TimeProvider>() ?? TimeProvider.System;

        var health = await CheckHealthAsync(services.GetServices<IHealthContributor>(), ct).ConfigureAwait(false);
        var memory = await memoryReader.ReadAsync(ct).ConfigureAwait(false);

        var snapshot = bus.Snapshot(seq => view.Build(bus.BootId, seq, time.GetUtcNow(), health, memory));
        return Results.Json(snapshot, AgentJson.Options);
    }

    /// <summary>The union of every contributor's reasons, in registration order, duplicates removed.</summary>
    public static async Task<NodeHealth> CheckHealthAsync(IEnumerable<IHealthContributor> contributors, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contributors);
        var reasons = new List<HealthReason>();
        foreach (var contributor in contributors)
        {
            foreach (var reason in await contributor.CheckAsync(ct).ConfigureAwait(false))
            {
                if (!reasons.Contains(reason))
                {
                    reasons.Add(reason);
                }
            }
        }

        return new NodeHealth { Reasons = reasons };
    }

    private static async Task StreamEventsAsync(HttpContext http)
    {
        var query = http.Request.Query;
        string? bootId = query.TryGetValue("bootId", out var bootValues) && !string.IsNullOrEmpty(bootValues.ToString())
            ? bootValues.ToString()
            : null;

        long? afterSeq = null;
        if (query.TryGetValue("afterSeq", out var seqValues) && !string.IsNullOrEmpty(seqValues.ToString()))
        {
            if (!long.TryParse(seqValues.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
            {
                await Results.Json(
                    new AgentErrorResponse(new AgentError { Code = ErrorCodes.InvalidRequest, Message = "afterSeq must be a non-negative integer." }),
                    AgentJson.Options,
                    statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(http).ConfigureAwait(false);
                return;
            }

            afterSeq = parsed;
        }

        var bus = http.RequestServices.GetRequiredService<IEventBus>();
        await using var subscription = bus.Subscribe(bootId, afterSeq);
        await AgentEventSse.WriteAsync(http.Response, subscription, http.RequestAborted).ConfigureAwait(false);
    }
}
