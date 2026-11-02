using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Runtimes;

/// <summary>
/// HTTP helpers shared by the runtime and operation endpoints (design §4.3): the one agent error shape
/// <c>{"error": {...}}</c>, JSON bodies with <see cref="AgentJson.Options"/>, and the <c>MirbadLLM-Master</c> header.
/// </summary>
internal static class AgentHttp
{
    /// <summary>A JSON response with the wire options.</summary>
    public static IResult Json<T>(T value, int statusCode = StatusCodes.Status200OK) =>
        Results.Json(value, AgentJson.Options, AgentJson.JsonContentType, statusCode);

    /// <summary>An agent error response.</summary>
    public static IResult Error(int statusCode, AgentError error) =>
        Results.Json(new AgentErrorResponse(error), AgentJson.Options, AgentJson.JsonContentType, statusCode);

    /// <summary>An agent error response from an <see cref="AgentException"/>.</summary>
    public static IResult Error(AgentException exception) => Error(exception.StatusCode, exception.Error);

    /// <summary>An agent error response with a code and message.</summary>
    public static IResult Error(int statusCode, string code, string message) =>
        Error(statusCode, new AgentError { Code = code, Message = message });

    /// <summary>Builds an <see cref="AgentException"/> with a code and message.</summary>
    public static AgentException Failure(int statusCode, string code, string message) =>
        new(statusCode, new AgentError { Code = code, Message = message });

    /// <summary>
    /// Reads the mutation body as <typeparamref name="T"/> and keeps the raw JSON (the operation log digests and stores it
    /// redacted). Malformed or incomplete bodies yield 400 <c>invalid_request</c>.
    /// </summary>
    public static async Task<BodyRead<T>> ReadMutationAsync<T>(HttpRequest request, CancellationToken ct)
        where T : AgentMutation
    {
        JsonElement payload;
        T? value;
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct).ConfigureAwait(false);
            payload = document.RootElement.Clone();
            value = payload.Deserialize<T>(AgentJson.Options);
        }
        catch (JsonException ex)
        {
            return new BodyRead<T>(null, default, Failure(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest,
                "The request body is not a valid " + typeof(T).Name + ": " + ex.Message));
        }

        if (value is null || string.IsNullOrWhiteSpace(value.OperationId))
        {
            return new BodyRead<T>(null, default, Failure(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest,
                "The request body must carry an operationId."));
        }

        if (value.ExpiresAt <= value.IssuedAt)
        {
            return new BodyRead<T>(null, default, Failure(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest,
                "The envelope's expiresAt must be after its issuedAt."));
        }

        return new BodyRead<T>(value, payload, null);
    }

    /// <summary>Parses the <c>MirbadLLM-Master</c> header; 400 <c>invalid_request</c> when it is missing or malformed.</summary>
    public static bool TryReadMaster(HttpRequest request, out MasterTriple master, out AgentException? error)
    {
        string? value = request.Headers[Protocol.MasterHeader];
        if (MasterTriple.TryParse(value, out master))
        {
            error = null;
            return true;
        }

        error = Failure(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest,
            "Mutating requests must carry the " + Protocol.MasterHeader + " header ({instanceId}/{epoch}/{bootId}).");
        return false;
    }
}

/// <summary>A parsed mutation body.</summary>
/// <param name="Value">The request, or null when <paramref name="Error"/> is set.</param>
/// <param name="Payload">The raw JSON body.</param>
/// <param name="Error">Why the body was refused.</param>
internal sealed record BodyRead<T>(T? Value, JsonElement Payload, AgentException? Error)
    where T : AgentMutation;
