using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MirbadLLM.Core;

/// <summary>
/// An immutable JSON object with value semantics: keys in ordinal order, each value kept as compact JSON text.
/// Holds vLLM's JSON-valued arguments (<c>--speculative-config</c>, <c>--compilation-config</c>) so keys
/// mirbadllm has no typed field for round-trip unchanged, and two objects with the same keys and values are
/// equal however they were written.
/// </summary>
[JsonConverter(typeof(JsonFieldsConverter))]
public sealed class JsonFields : IEquatable<JsonFields>
{
    private readonly SortedDictionary<string, string> _fields;

    private JsonFields(SortedDictionary<string, string> fields)
    {
        _fields = fields;
    }

    public static JsonFields Empty { get; } = new(new SortedDictionary<string, string>(StringComparer.Ordinal));

    public int Count => _fields.Count;

    public bool IsEmpty => _fields.Count == 0;

    public IEnumerable<string> Keys => _fields.Keys;

    /// <summary>The fields as a read-only map: key → compact JSON text of the value.</summary>
    public IReadOnlyDictionary<string, string> AsDictionary() => _fields;

    /// <summary>The compact JSON text of the value at <paramref name="key"/>.</summary>
    public string this[string key] => _fields[key];

    /// <summary>Parses a JSON object. Throws <see cref="FormatException"/> when <paramref name="json"/> is not one.</summary>
    public static JsonFields Parse(string json)
    {
        if (!TryParse(json, out var fields))
        {
            throw new FormatException("Expected a JSON object.");
        }

        return fields;
    }

    public static bool TryParse(string? json, out JsonFields fields)
    {
        fields = Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            fields = FromElement(doc.RootElement);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The fields of a JSON object element (a repeated key keeps its last value, as Python's json does).</summary>
    public static JsonFields FromElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Expected a JSON object.");
        }

        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            fields[property.Name] = Compact(property.Value);
        }

        return new JsonFields(fields);
    }

    /// <summary>JSON text for a command-line value: itself when it is valid JSON (<c>3</c>, <c>true</c>, <c>{…}</c>), else a JSON string.</summary>
    public static string RawFromCliValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            using var doc = JsonDocument.Parse(value);
            return Compact(doc.RootElement);
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(value);
        }
    }

    public bool ContainsKey(string key) => _fields.ContainsKey(key);

    public bool TryGetValue(string key, out string value) => _fields.TryGetValue(key, out value!);

    /// <summary>The value at <paramref name="key"/> when it is a JSON string.</summary>
    public string? GetString(string key) => TryGetElement(key, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    /// <summary>The value at <paramref name="key"/> when it is a JSON integer.</summary>
    public int? GetInt(string key) => TryGetElement(key, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int n) ? n : null;

    /// <summary>A copy with <paramref name="key"/> set to the JSON text <paramref name="rawJson"/> (replacing any value).</summary>
    public JsonFields With(string key, string rawJson)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(rawJson);
        var copy = new SortedDictionary<string, string>(_fields, StringComparer.Ordinal)
        {
            [key] = CompactRaw(rawJson),
        };
        return new JsonFields(copy);
    }

    public JsonFields WithString(string key, string value) => With(key, JsonSerializer.Serialize(value));

    public JsonFields WithInt(string key, int value) => With(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public JsonFields Without(params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (!keys.Any(_fields.ContainsKey))
        {
            return this;
        }

        var copy = new SortedDictionary<string, string>(_fields, StringComparer.Ordinal);
        foreach (string key in keys)
        {
            copy.Remove(key);
        }

        return new JsonFields(copy);
    }

    /// <summary>
    /// Sets a nested key (<c>pass_config.fuse_norm_quant</c> → <c>["pass_config","fuse_norm_quant"]</c>), creating
    /// objects on the way and merging into existing ones, the way vLLM applies a dotted argument.
    /// </summary>
    public JsonFields SetPath(IReadOnlyList<string> path, string rawJson)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count == 0)
        {
            throw new ArgumentException("The path is empty.", nameof(path));
        }

        string key = path[0];
        if (path.Count == 1)
        {
            return With(key, _fields.TryGetValue(key, out string? existing) ? MergeRaw(existing, rawJson) : rawJson);
        }

        var inner = _fields.TryGetValue(key, out string? current) && TryParse(current, out var parsed) ? parsed : Empty;
        return With(key, inner.SetPath(path.Skip(1).ToList(), rawJson).ToJson());
    }

    /// <summary>A deep merge: keys of <paramref name="other"/> win; two objects under the same key merge recursively.</summary>
    public JsonFields MergedWith(JsonFields other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var result = this;
        foreach (var (key, value) in other._fields)
        {
            result = result.With(key, result._fields.TryGetValue(key, out string? existing) ? MergeRaw(existing, value) : value);
        }

        return result;
    }

    /// <summary>Compact JSON object text, keys in ordinal order.</summary>
    public string ToJson()
    {
        var text = new StringBuilder("{");
        bool first = true;
        foreach (var (key, value) in _fields)
        {
            if (!first)
            {
                text.Append(',');
            }

            first = false;
            text.Append(JsonSerializer.Serialize(key)).Append(':').Append(value);
        }

        return text.Append('}').ToString();
    }

    public override string ToString() => ToJson();

    public bool Equals(JsonFields? other) =>
        other is not null && (ReferenceEquals(this, other) || (_fields.Count == other._fields.Count && _fields.All(kv =>
            other._fields.TryGetValue(kv.Key, out string? value) && string.Equals(kv.Value, value, StringComparison.Ordinal))));

    public override bool Equals(object? obj) => Equals(obj as JsonFields);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ToJson());

    private static string MergeRaw(string existing, string incoming) =>
        TryParse(existing, out var a) && TryParse(incoming, out var b) ? a.MergedWith(b).ToJson() : incoming;

    private bool TryGetElement(string key, out JsonElement element)
    {
        element = default;
        if (!_fields.TryGetValue(key, out string? raw))
        {
            return false;
        }

        using var doc = JsonDocument.Parse(raw);
        element = doc.RootElement.Clone();
        return true;
    }

    private static string CompactRaw(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        return Compact(doc.RootElement);
    }

    /// <summary>Re-serializes an element without whitespace; objects are rewritten with sorted keys so equal values compare equal.</summary>
    private static string Compact(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => FromElement(element).ToJson(),
        JsonValueKind.Array => "[" + string.Join(',', element.EnumerateArray().Select(Compact)) + "]",
        _ => JsonSerializer.Serialize(element),
    };
}

/// <summary>Reads and writes <see cref="JsonFields"/> as a plain JSON object.</summary>
public sealed class JsonFieldsConverter : JsonConverter<JsonFields>
{
    public override JsonFields? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        return JsonFields.FromElement(doc.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, JsonFields value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteRawValue(value.ToJson());
    }
}
