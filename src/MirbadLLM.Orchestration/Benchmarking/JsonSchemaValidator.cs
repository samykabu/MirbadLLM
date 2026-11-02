using System.Globalization;
using System.Text.Json;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>
/// Validates tool-call arguments against the JSON-schema subset tool definitions use: <c>type</c> (object, string,
/// integer, number, boolean, array, null), <c>properties</c>, <c>required</c>, <c>enum</c>, <c>items</c> and
/// <c>additionalProperties: false</c>. Returns the first violation, or null when the value is valid.
/// </summary>
public static class JsonSchemaValidator
{
    public static string? Validate(JsonElement schema, JsonElement value, string path = "$")
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && !HasType(value, type.GetString()!))
        {
            return $"{path} should be {type.GetString()}, got {Describe(value)}";
        }

        if (schema.TryGetProperty("enum", out var options) && options.ValueKind == JsonValueKind.Array &&
            !options.EnumerateArray().Any(o => JsonElementEquals(o, value)))
        {
            return $"{path} is {value.GetRawText()}, not one of {options.GetRawText()}";
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in required.EnumerateArray().Select(r => r.GetString()).OfType<string>())
                {
                    if (!value.TryGetProperty(name, out _))
                    {
                        return $"{path}.{name} is required but missing";
                    }
                }
            }

            bool hasProperties = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object;
            bool closed = schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False;
            foreach (var property in value.EnumerateObject())
            {
                if (hasProperties && properties.TryGetProperty(property.Name, out var propertySchema))
                {
                    if (Validate(propertySchema, property.Value, $"{path}.{property.Name}") is { } error)
                    {
                        return error;
                    }
                }
                else if (closed)
                {
                    return $"{path}.{property.Name} is not allowed";
                }
            }
        }

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            int i = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (Validate(items, item, string.Create(CultureInfo.InvariantCulture, $"{path}[{i++}]")) is { } error)
                {
                    return error;
                }
            }
        }

        return null;
    }

    private static bool HasType(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal d) && decimal.Truncate(d) == d,
        _ => true,
    };

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => $"number {value.GetRawText()}",
        JsonValueKind.String => $"string {value.GetRawText()}",
        _ => value.ValueKind.ToString().ToLowerInvariant(),
    };

    private static bool JsonElementEquals(JsonElement a, JsonElement b) =>
        a.ValueKind == b.ValueKind && string.Equals(a.GetRawText(), b.GetRawText(), StringComparison.Ordinal);
}
