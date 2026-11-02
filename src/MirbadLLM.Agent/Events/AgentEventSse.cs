using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Events;

/// <summary>
/// Server-Sent Events framing of <see cref="AgentEvent"/> (design §3.4, §4.7): <c>event: {type}</c>, one
/// <c>data: {json}</c> line (the JSON written as the base type, so it carries the <c>type</c> discriminator, with
/// <see cref="AgentJson.Options"/>), then a blank line. Like today's <c>/api/models/{id}/logs</c> stream, every frame is
/// flushed at once.
/// </summary>
public static class AgentEventSse
{
    /// <summary>One SSE frame for <paramref name="evt"/>.</summary>
    public static string Format(AgentEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        // Compact JSON has no raw line breaks (string contents are escaped), so one data line always suffices.
        string json = JsonSerializer.Serialize(evt, AgentJson.Options);
        return new StringBuilder(json.Length + 32)
            .Append("event: ").Append(evt.EventType).Append('\n')
            .Append("data: ").Append(json).Append('\n')
            .Append('\n')
            .ToString();
    }

    /// <summary>
    /// Streams <paramref name="subscription"/> to <paramref name="response"/> until it completes (after reset or overflow)
    /// or the client goes away. Sets the SSE headers and flushes them before the first event.
    /// </summary>
    public static async Task WriteAsync(HttpResponse response, IEventSubscription subscription, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(subscription);

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = AgentJson.EventStreamContentType;
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        await response.Body.FlushAsync(ct).ConfigureAwait(false);

        try
        {
            await foreach (var evt in subscription.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await response.WriteAsync(Format(evt), ct).ConfigureAwait(false);
                await response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The master disconnected: nothing more to send.
        }
    }
}
