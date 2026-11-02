using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.UnitTests.Multinode.Chaos;

/// <summary>
/// Test-only middleware in front of the real agent's pipeline: records the operation id of every mutation the agent
/// <b>answered</b> with a success status (2xx), whether or not the answer reached the master. The agent accepts an
/// operation durably before it answers (design §4.4), so every recorded id must survive agent crashes
/// (<c>GET /operations/{id}</c> knows it) and the master must close its reference once the operation ended. Survives agent
/// restarts (one instance per cluster).
/// </summary>
internal sealed class AgentRequestRecorder : IStartupFilter
{
    private readonly ConcurrentDictionary<string, string> _accepted = new(StringComparer.Ordinal);

    /// <summary>Operation id → "METHOD path status" of the agent's success answer.</summary>
    public IReadOnlyDictionary<string, string> Accepted => _accepted;

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, call) =>
        {
            string? operationId = await ReadOperationIdAsync(context.Request);
            await call(context);
            if (operationId is not null && context.Response.StatusCode is >= 200 and < 300)
            {
                _accepted[operationId] = $"{context.Request.Method} {context.Request.Path} {context.Response.StatusCode}";
            }
        });
        next(app);
    };

    private static async Task<string?> ReadOperationIdAsync(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || !request.Path.StartsWithSegments(Protocol.BasePath, StringComparison.Ordinal)
            || request.Path.Value!.Contains("/proxy/", StringComparison.Ordinal) || request.ContentLength is 0 or > 1 << 20
            || request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
        {
            return null;
        }

        request.EnableBuffering();
        try
        {
            using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
            return body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty("operationId", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            request.Body.Position = 0;
        }
    }
}
