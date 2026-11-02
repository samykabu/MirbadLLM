using System.Security.Cryptography;
using System.Text.Json;
namespace MirbadLLM.Agent.Contracts;

/// <summary>
/// Secret redaction and the payload digest of an operation (design §4.4 item 1, §10.4).
/// <para>Redaction is recursive: at every depth, the value of a property named like one of
/// <see cref="Protocol.SecretFieldNames"/> (<c>token</c>, <c>password</c>; case-insensitive) is replaced by
/// <see cref="Protocol.RedactedValue"/>, and in any object that carries a <c>secretEnvNames</c> string array (a
/// <see cref="LaunchSpec"/>) the listed entries of its sibling <c>env</c> object are replaced too. A JSON <c>null</c>
/// secret stays <c>null</c> (there is nothing to hide, and "no token" stays distinguishable from "a token").</para>
/// <para>The digest is the Base64 SHA-256 of the <b>canonical</b> form of the redacted body: object properties sorted by
/// ordinal name, no insignificant whitespace, strings re-encoded, numbers as sent. Property order and formatting do not
/// change it; secret values do not change it either (they are <c>***</c> before hashing). The envelope fields
/// (<c>operationId</c>, <c>issuedAt</c>, <c>expiresAt</c>) are part of the body and so of the digest.</para>
/// <para>This is the <b>one</b> canonical form shared by both sides (W2 review MN-092, D38): the agent digests the body it
/// received, the master digests the request it sends (<see cref="ComputeRequestDigest"/>, serialized with
/// <see cref="AgentJson.Options"/>), and both get the same value for the same request.</para>
/// </summary>
public static class OperationPayload
{
    private const string SecretEnvNamesProperty = "secretEnvNames";
    private const string EnvProperty = "env";

    /// <summary>Returns a copy of <paramref name="payload"/> with every secret replaced by <c>***</c>, keeping property order.</summary>
    public static JsonElement Redact(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Undefined)
        {
            // An absent body (default element) is stored and digested as JSON null.
            using var empty = JsonDocument.Parse("null");
            return empty.RootElement.Clone();
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteRedacted(writer, payload);
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>Base64 SHA-256 of the canonical form of <paramref name="redactedPayload"/> (call <see cref="Redact"/> first).</summary>
    public static string ComputeDigest(JsonElement redactedPayload) =>
        Convert.ToBase64String(SHA256.HashData(Canonicalize(redactedPayload)));

    /// <summary>
    /// The digest of a typed request as the agent will compute it from the wire bytes: serialize with
    /// <see cref="AgentJson.Options"/> as <paramref name="requestType"/>, redact, canonicalize, hash.
    /// </summary>
    public static string ComputeRequestDigest(object request, Type requestType)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(requestType);
        JsonElement element = JsonSerializer.SerializeToElement(request, requestType, AgentJson.Options);
        return ComputeDigest(Redact(element));
    }

    /// <summary>The redacted copy of <paramref name="payload"/> and the digest of it, in one call.</summary>
    public static (JsonElement Redacted, string Digest) RedactAndDigest(JsonElement payload)
    {
        JsonElement redacted = Redact(payload);
        return (redacted, ComputeDigest(redacted));
    }

    /// <summary>The canonical UTF-8 bytes the digest is computed over.</summary>
    public static byte[] Canonicalize(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonical(writer, element);
        }

        return buffer.ToArray();
    }

    /// <summary>True when <paramref name="propertyName"/> names a secret field (<see cref="Protocol.SecretFieldNames"/>).</summary>
    public static bool IsSecretField(string propertyName)
    {
        foreach (string name in Protocol.SecretFieldNames)
        {
            if (string.Equals(name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteRedacted(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                HashSet<string>? secretEnv = SecretEnvNames(element);
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (IsSecretField(property.Name))
                    {
                        WriteRedactedValue(writer, property.Value);
                    }
                    else if (secretEnv is not null && property.Value.ValueKind == JsonValueKind.Object &&
                             string.Equals(property.Name, EnvProperty, StringComparison.OrdinalIgnoreCase))
                    {
                        writer.WriteStartObject();
                        foreach (JsonProperty variable in property.Value.EnumerateObject())
                        {
                            writer.WritePropertyName(variable.Name);
                            if (secretEnv.Contains(variable.Name))
                            {
                                WriteRedactedValue(writer, variable.Value);
                            }
                            else
                            {
                                WriteRedacted(writer, variable.Value);
                            }
                        }

                        writer.WriteEndObject();
                    }
                    else
                    {
                        WriteRedacted(writer, property.Value);
                    }
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteRedacted(writer, item);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void WriteRedactedValue(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(Protocol.RedactedValue);
        }
    }

    /// <summary>The names listed in this object's <c>secretEnvNames</c> array, or null when it has none.</summary>
    private static HashSet<string>? SecretEnvNames(JsonElement obj)
    {
        HashSet<string>? names = null;
        foreach (JsonProperty property in obj.EnumerateObject())
        {
            if (!string.Equals(property.Name, SecretEnvNamesProperty, StringComparison.OrdinalIgnoreCase) ||
                property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement item in property.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
                {
                    // Case-insensitive: over-redacting a same-named variable is harmless, missing one is not.
                    names ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    names.Add(name);
                }
            }
        }

        return names;
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            default:
                writer.WriteNullValue();
                break;
        }
    }
}
