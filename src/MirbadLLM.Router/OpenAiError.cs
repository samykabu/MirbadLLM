using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace MirbadLLM.Router;

/// <summary>Writes errors in the OpenAI API error shape: <c>{"error":{"message","type","code"}}</c>.</summary>
internal static class OpenAiError
{
    public static Task WriteAsync(HttpContext context, int statusCode, string message, string type, string? code)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(new ErrorResponse(new ErrorDetail(message, type, code)));
    }

    private sealed record ErrorResponse([property: JsonPropertyName("error")] ErrorDetail Error);

    private sealed record ErrorDetail(
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("code")] string? Code);
}
