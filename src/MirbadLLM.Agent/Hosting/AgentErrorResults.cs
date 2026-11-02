using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Hosting;

/// <summary>
/// The agent error shape (design §4.3): every non-2xx answer is <c>{"error": {"code", "message", "details"}}</c>
/// (<see cref="AgentErrorResponse"/>) serialized with <see cref="AgentJson.Options"/>. Handlers return
/// <see cref="Error(int, string, string, object?)"/> or throw <see cref="AgentException"/> (the pipeline writes it).
/// </summary>
public static class AgentErrorResults
{
    /// <summary>An error result; <paramref name="details"/> is serialized with the wire options when present.</summary>
    public static IResult Error(int statusCode, string code, string message, object? details = null) =>
        Error(statusCode, Create(code, message, details));

    /// <summary>An error result for an existing <see cref="AgentError"/>.</summary>
    public static IResult Error(int statusCode, AgentError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Results.Json(new AgentErrorResponse(error), AgentJson.Options, AgentJson.JsonContentType, statusCode);
    }

    /// <summary>Writes an error response directly (middleware). Does nothing useful once the response has started.</summary>
    public static async Task WriteAsync(HttpContext context, int statusCode, AgentError error)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(error);
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = AgentJson.JsonContentType;
        context.Response.Headers[Protocol.AgentErrorHeader] = error.Code;
        await JsonSerializer.SerializeAsync(context.Response.Body, new AgentErrorResponse(error), AgentJson.Options, context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>Builds an <see cref="AgentError"/>.</summary>
    public static AgentError Create(string code, string message, object? details = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentNullException.ThrowIfNull(message);
        return new AgentError
        {
            Code = code,
            Message = message,
            Details = details is null ? null : JsonSerializer.SerializeToElement(details, details.GetType(), AgentJson.Options),
        };
    }
}
