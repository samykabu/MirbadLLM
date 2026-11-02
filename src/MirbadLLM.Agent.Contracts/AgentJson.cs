using System.Text.Json;
using System.Text.Json.Serialization;

namespace MirbadLLM.Agent.Contracts;

/// <summary>
/// The wire format of the master↔agent protocol (design §4.3): JSON web defaults (camelCase, case-insensitive reads),
/// enums as camelCase strings (<c>ensureStarted</c>, <c>unified</c>, <c>succeeded</c>), nulls written, and the
/// <c>type</c> discriminator of <see cref="AgentEvent"/> accepted in any position. Both sides use exactly these options.
/// </summary>
public static class AgentJson
{
    /// <summary>The shared, read-only options.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary><c>Content-Type</c> of JSON bodies.</summary>
    public const string JsonContentType = "application/json";

    /// <summary><c>Content-Type</c> of event and log streams (Server-Sent Events).</summary>
    public const string EventStreamContentType = "text/event-stream";

    /// <summary>
    /// Deserializes the stored result of an operation (<see cref="OperationStatus.Result"/>) as the route's
    /// <see cref="AgentRoute.ResultType"/>. Null when there is no result.
    /// </summary>
    public static T? ReadResult<T>(this OperationStatus status)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Result is { } element && element.ValueKind != JsonValueKind.Null
            ? element.Deserialize<T>(Options)
            : null;
    }

    /// <summary>Serializes <paramref name="value"/> to a <see cref="JsonElement"/> with the wire options (for results and details).</summary>
    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            AllowOutOfOrderMetadataProperties = true,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
