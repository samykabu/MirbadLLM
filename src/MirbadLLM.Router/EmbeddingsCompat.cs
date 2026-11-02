using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using MirbadLLM.Core;

namespace MirbadLLM.Router;

/// <summary>
/// The router's <c>dimensions</c> emulation for llama.cpp embedding models (design-p3p4 §A.3, MN-217; rule frozen as Core
/// <see cref="EmbeddingDimensions"/>, D117; "only when sent" made explicit by MN-207, D119). llama-server ignores the
/// OpenAI <c>dimensions</c> field (spike S1), so for a model the rule <see cref="EmbeddingDimensions.Applies"/> to the
/// router strips the field from the request and, on a 200 JSON answer, keeps the first N values of every
/// <c>data[i].embedding</c> (float array or base64 little-endian float32) and L2-renormalises them.
/// <para>Everything else is byte for byte: a request that does not carry the field (OpenViking's path, spike S2 / C7), a
/// <c>"dimensions": null</c> (treated as not carried, as vLLM does), other paths and methods, vLLM models (the DGX vLLM
/// answers <c>dimensions</c> with its own 400, which reaches the client unchanged), non-200 or non-JSON answers. The
/// request <c>model</c> that llama-server echoes is never rewritten (S1 / C8).</para>
/// <para>Order of the checks in <c>ProxyHandler</c>: <see cref="Carries"/> (path, method, field present; no registry
/// access for any other request), then the model rule, then <see cref="Prepare"/> (validates and strips), then after the
/// answer <see cref="CanRewrite"/> and <see cref="Rewrite"/>. The native size is the length of the vectors llama-server
/// returned (the router keeps no per-model state).</para>
/// </summary>
internal static class EmbeddingsCompat
{
    /// <summary>Upper bound on an answer the router reads into memory to rewrite (the answer is bounded by the input).</summary>
    internal const int MaxResponseBytes = 256 * 1024 * 1024;

    private static readonly byte[] DimensionsUtf8 = "dimensions"u8.ToArray();

    /// <summary>The outcome of <see cref="Prepare"/>.</summary>
    internal readonly record struct PreparedRequest(int Dimensions, byte[]? Body, string? Error)
    {
        /// <summary>True when the request is refused with 400 before forwarding (<see cref="Error"/> is the message).</summary>
        public bool IsRefused => Error is not null;
    }

    /// <summary>The outcome of <see cref="Rewrite"/>: the new body, a client error (400), or a malformed answer (502).</summary>
    internal readonly record struct RewrittenResponse(byte[]? Body, string? ClientError, string? Malformed);

    /// <summary>
    /// True when a request is a candidate for the emulation: <c>POST /v1/embeddings</c> whose body is a JSON object with a
    /// top-level <c>dimensions</c> that is not <c>null</c>. Reads only; never throws on malformed JSON (false).
    /// </summary>
    public static bool Carries(string method, string path, ArraySegment<byte> body) =>
        HttpMethods.IsPost(method) &&
        string.Equals(path, EmbeddingDimensions.Path, StringComparison.Ordinal) &&
        body.Count > 0 &&
        FindDimensions(body, out var ranges) &&
        ranges.Exists(r => r.ValueKind != JsonTokenType.Null);

    /// <summary>
    /// Validates the carried <c>dimensions</c> (one integer ≥ 1) and returns the body with that property removed; every
    /// other byte of the body is kept as the client sent it. Refuses a non-integer, a value below 1 and a repeated field.
    /// </summary>
    public static PreparedRequest Prepare(ArraySegment<byte> body)
    {
        if (!FindDimensions(body, out var ranges) || ranges.Count == 0)
        {
            return new PreparedRequest(0, null, $"'{EmbeddingDimensions.DimensionsField}' could not be read from the request.");
        }

        if (ranges.Count > 1)
        {
            return new PreparedRequest(0, null, $"'{EmbeddingDimensions.DimensionsField}' is given more than once.");
        }

        var range = ranges[0];
        if (range.ValueKind != JsonTokenType.Number || range.Dimensions is not { } dimensions)
        {
            return new PreparedRequest(0, null, $"'{EmbeddingDimensions.DimensionsField}' must be an integer of at least 1.");
        }

        if (dimensions < 1)
        {
            return new PreparedRequest(0, null, $"'{EmbeddingDimensions.DimensionsField}' must be an integer of at least 1 (got {dimensions}).");
        }

        if (dimensions > int.MaxValue)
        {
            return new PreparedRequest(0, null, $"'{EmbeddingDimensions.DimensionsField}' ({dimensions}) is larger than any embedding size.");
        }

        return new PreparedRequest((int)dimensions, Splice(body, range.Start, range.End), null);
    }

    /// <summary>True when the agent's answer is one the emulation rewrites: 200, JSON media type, no content encoding.</summary>
    public static bool CanRewrite(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.OK &&
        response.Content.Headers.ContentType?.MediaType is { } mediaType &&
        (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
         mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) &&
        response.Content.Headers.ContentEncoding.Count == 0;

    /// <summary>
    /// Truncates every <c>data[i].embedding</c> to <paramref name="dimensions"/> values and L2-renormalises it. A vector
    /// shorter than <paramref name="dimensions"/> means the request asked for more than the model's native size (400);
    /// an answer without the OpenAI embeddings shape is malformed (502). Other fields (<c>model</c>, <c>usage</c>,
    /// <c>object</c>, <c>index</c>) are kept.
    /// </summary>
    public static RewrittenResponse Rewrite(ReadOnlySpan<byte> response, int dimensions)
    {
        try
        {
            return RewriteParsed(response, dimensions);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            // A non-finite number (1e400) or a duplicate key (MN-219): malformed (502), never a framework 500.
            return new RewrittenResponse(null, null, "The model's embeddings answer is not a valid embeddings document.");
        }
    }

    private static RewrittenResponse RewriteParsed(ReadOnlySpan<byte> response, int dimensions)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(response);
        }
        catch (JsonException)
        {
            return new RewrittenResponse(null, null, "The model's embeddings answer is not valid JSON.");
        }

        if (root is not JsonObject document || document["data"] is not JsonArray data)
        {
            return new RewrittenResponse(null, null, "The model's embeddings answer has no 'data' array.");
        }

        foreach (var item in data)
        {
            if (item is not JsonObject entry)
            {
                return new RewrittenResponse(null, null, "An item of the model's embeddings answer is not an object.");
            }

            var result = entry["embedding"] switch
            {
                JsonArray values => TruncateFloats(values, dimensions),
                JsonValue value when value.GetValueKind() == JsonValueKind.String => TruncateBase64(value.GetValue<string>(), dimensions),
                _ => Outcome.NotAnEmbedding(),
            };

            if (result.ClientError is not null || result.Malformed is not null)
            {
                return new RewrittenResponse(null, result.ClientError, result.Malformed);
            }

            entry["embedding"] = result.Node;
        }

        return new RewrittenResponse(Encoding.UTF8.GetBytes(document.ToJsonString()), null, null);
    }

    /// <summary>Writes the OpenAI-shaped 400 for a refused <c>dimensions</c> (type <see cref="EmbeddingDimensions.ErrorType"/>, param <c>dimensions</c>).</summary>
    public static Task WriteRefusalAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return context.Response.WriteAsJsonAsync(
            new RefusalResponse(new RefusalDetail(message, EmbeddingDimensions.ErrorType, EmbeddingDimensions.DimensionsField, null)));
    }

    /// <summary>Removes <c>[start, end)</c> and the comma that separated it from its neighbour.</summary>
    internal static byte[] Splice(ReadOnlySpan<byte> body, int start, int end)
    {
        int after = end;
        while (after < body.Length && IsWhitespace(body[after]))
        {
            after++;
        }

        if (after < body.Length && body[after] == (byte)',')
        {
            end = after + 1; // "dimensions": N, "next": … → "next": …
        }
        else
        {
            int before = start - 1;
            while (before >= 0 && IsWhitespace(body[before]))
            {
                before--;
            }

            if (before >= 0 && body[before] == (byte)',')
            {
                start = before; // …"prev": x, "dimensions": N } → …"prev": x }
            }
        }

        var spliced = new byte[body.Length - (end - start)];
        body[..start].CopyTo(spliced);
        body[end..].CopyTo(spliced.AsSpan(start));
        return spliced;
    }

    private static bool IsWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    /// <summary>A top-level <c>dimensions</c> property: <c>[Start, End)</c> spans the name through the value.</summary>
    private readonly record struct DimensionsRange(int Start, int End, JsonTokenType ValueKind, long? Dimensions);

    /// <summary>Finds every top-level <c>dimensions</c> of a JSON object body; false when the body is not one.</summary>
    private static bool FindDimensions(ReadOnlySpan<byte> body, out List<DimensionsRange> ranges)
    {
        ranges = [];
        try
        {
            var reader = new Utf8JsonReader(body);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                int start = (int)reader.TokenStartIndex;
                bool isDimensions = reader.ValueTextEquals(DimensionsUtf8);
                reader.Read();
                var kind = reader.TokenType;
                long? value = kind == JsonTokenType.Number && reader.TryGetInt64(out long parsed) ? parsed : null;
                reader.Skip();
                if (isDimensions)
                {
                    ranges.Add(new DimensionsRange(start, (int)reader.BytesConsumed, kind, value));
                }
            }

            // The whole body must be one well-formed object (trailing bytes make it not one).
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
            {
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private readonly record struct Outcome(JsonNode? Node, string? ClientError, string? Malformed)
    {
        public static Outcome NotAnEmbedding() => new(null, null, "An embedding in the model's answer is neither a float array nor a base64 string.");

        public static Outcome TooLarge(int dimensions, int native) => new(
            null,
            $"'{EmbeddingDimensions.DimensionsField}' ({dimensions}) is larger than the model's native embedding size ({native}).",
            null);
    }

    private static Outcome TruncateFloats(JsonArray values, int dimensions)
    {
        if (values.Count < dimensions)
        {
            return Outcome.TooLarge(dimensions, values.Count);
        }

        var kept = new double[dimensions];
        for (int i = 0; i < dimensions; i++)
        {
            if (values[i] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
            {
                return Outcome.NotAnEmbedding();
            }

            kept[i] = value.GetValue<double>();
        }

        double norm = Norm(kept);
        var array = new JsonArray();
        foreach (double v in kept)
        {
            array.Add(JsonValue.Create((float)(norm > 0 ? v / norm : v)));
        }

        return new Outcome(array, null, null);
    }

    private static Outcome TruncateBase64(string encoded, int dimensions)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return Outcome.NotAnEmbedding();
        }

        if (bytes.Length % sizeof(float) != 0)
        {
            return Outcome.NotAnEmbedding();
        }

        int native = bytes.Length / sizeof(float);
        if (native < dimensions)
        {
            return Outcome.TooLarge(dimensions, native);
        }

        var kept = new double[dimensions];
        for (int i = 0; i < dimensions; i++)
        {
            kept[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
        }

        double norm = Norm(kept);
        var output = new byte[dimensions * sizeof(float)];
        for (int i = 0; i < dimensions; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(i * sizeof(float)), (float)(norm > 0 ? kept[i] / norm : kept[i]));
        }

        return new Outcome(JsonValue.Create(Convert.ToBase64String(output)), null, null);
    }

    private static double Norm(double[] values)
    {
        double sum = 0;
        foreach (double v in values)
        {
            sum += v * v;
        }

        return Math.Sqrt(sum);
    }

    private sealed record RefusalResponse([property: JsonPropertyName("error")] RefusalDetail Error);

    private sealed record RefusalDetail(
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("param")] string Param,
        [property: JsonPropertyName("code")] string? Code);
}
