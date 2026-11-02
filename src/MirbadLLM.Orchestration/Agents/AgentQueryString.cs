using System.Text;
using System.Text.Json;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Orchestration.Agents;

/// <summary>
/// Query strings of <see cref="AgentRouteInput.Query"/> routes (design §4.3): the request type's properties as camelCase
/// keys (<see cref="AgentJson.Options"/> names), lists comma-separated, nulls left out, values escaped.
/// </summary>
internal static class AgentQueryString
{
    /// <summary><c>""</c> or <c>?key=value&amp;…</c> for <paramref name="query"/> serialized as <paramref name="type"/>.</summary>
    public static string Build(object? query, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (query is null)
        {
            return "";
        }

        JsonElement element = JsonSerializer.SerializeToElement(query, type, AgentJson.Options);
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A query must serialize to a JSON object.", nameof(query));
        }

        var builder = new StringBuilder();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            string? value = Format(property.Value);
            if (value is null)
            {
                continue;
            }

            builder.Append(builder.Length == 0 ? '?' : '&')
                .Append(Uri.EscapeDataString(property.Name))
                .Append('=')
                .Append(Uri.EscapeDataString(value));
        }

        return builder.ToString();
    }

    private static string? Format(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.Array => string.Join(',', value.EnumerateArray().Select(Format).Where(v => v is not null)),
        _ => throw new ArgumentException("Nested objects are not supported in agent query strings."),
    };
}
