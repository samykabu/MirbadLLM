using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MirbadLLM.Router;

/// <summary>The Ollama routes that are served by a <c>/v1</c> request.</summary>
public enum OllamaRoute
{
    /// <summary><c>POST /api/chat</c>.</summary>
    Chat,

    /// <summary><c>POST /api/generate</c>.</summary>
    Generate,

    /// <summary><c>POST /api/embed</c>.</summary>
    Embed,

    /// <summary><c>POST /api/embeddings</c> (the older single-input route).</summary>
    Embeddings,
}

/// <summary>An Ollama request as its <c>/v1</c> request.</summary>
/// <param name="Path">The path under <c>/v1</c>.</param>
/// <param name="Body">The OpenAI-shaped body.</param>
/// <param name="Stream">Whether the client gets a stream (Ollama's default) or one object.</param>
public sealed record OllamaTranslatedRequest(string Path, JsonObject Body, bool Stream);

/// <summary>
/// Turns an Ollama request body into the OpenAI body the runtimes accept (field names from Ollama's API reference,
/// <c>docs/api.md</c> and <c>docs/openapi.yaml</c> in its repository). Fields with no OpenAI equivalent (<c>keep_alive</c>,
/// <c>num_ctx</c> and the other load-time options, <c>template</c>, <c>context</c>, <c>truncate</c>) are left out, never
/// refused. Pure: no I/O, and the request object is consumed (its nodes move into the result).
/// </summary>
public static class OllamaRequestTranslator
{
    /// <summary><c>/v1/chat/completions</c>.</summary>
    public const string ChatPath = "chat/completions";

    /// <summary><c>/v1/completions</c>, for <c>/api/generate</c> with <c>raw</c>.</summary>
    public const string CompletionsPath = "completions";

    /// <summary><c>/v1/embeddings</c>.</summary>
    public const string EmbeddingsPath = "embeddings";

    /// <summary>Writes '+' and non-ASCII text as they are: base64 images would otherwise grow by a twelfth.</summary>
    public static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Sampling options that mean the same thing under the same name in the runtimes' OpenAI-compatible APIs.
    private static readonly string[] SharedOptions =
        ["temperature", "top_p", "top_k", "min_p", "seed", "stop", "presence_penalty", "frequency_penalty"];

    /// <summary>The <c>/v1</c> path a route uses unless the body says otherwise (<c>raw</c>).</summary>
    public static string PathOf(OllamaRoute route) => route is OllamaRoute.Embed or OllamaRoute.Embeddings ? EmbeddingsPath : ChatPath;

    /// <summary>The <c>/v1</c> request for <paramref name="request"/>, naming <paramref name="model"/> as the model.</summary>
    public static OllamaTranslatedRequest Translate(OllamaRoute route, JsonObject request, string model)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new JsonObject { ["model"] = model };
        switch (route)
        {
            case OllamaRoute.Embed:
                body["input"] = Take(request, "input");
                if (Take(request, "dimensions") is JsonValue dimensions)
                {
                    body["dimensions"] = dimensions;
                }

                return new OllamaTranslatedRequest(EmbeddingsPath, body, Stream: false);

            case OllamaRoute.Embeddings:
                body["input"] = Take(request, "prompt") ?? "";
                return new OllamaTranslatedRequest(EmbeddingsPath, body, Stream: false);
        }

        string path = ChatPath;
        if (route == OllamaRoute.Chat)
        {
            body["messages"] = Messages(request["messages"] as JsonArray);
            if (Take(request, "tools") is JsonArray { Count: > 0 } tools)
            {
                body["tools"] = tools;
            }

            Think(request, body);
        }
        else if (IsTrue(request["raw"]))
        {
            path = CompletionsPath;
            body["prompt"] = Text(request["prompt"]) ?? "";
        }
        else
        {
            var messages = new JsonArray();
            if (Text(request["system"]) is { Length: > 0 } system)
            {
                messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
            }

            messages.Add(new JsonObject { ["role"] = "user", ["content"] = Content(request, "prompt") });
            body["messages"] = messages;
            Think(request, body);
        }

        Options(request["options"] as JsonObject, body);
        Format(request, body);

        // Ollama streams unless the client says otherwise.
        bool stream = !IsFalse(request["stream"]);
        body["stream"] = stream;
        if (stream)
        {
            // The final Ollama object carries the token counts, so the upstream is asked to report them.
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        return new OllamaTranslatedRequest(path, body, stream);
    }

    /// <summary>True when the request only asks to load the model: no prompt (generate) or no messages (chat).</summary>
    public static bool IsLoad(OllamaRoute route, JsonObject request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return route switch
        {
            OllamaRoute.Chat => request["messages"] is not JsonArray { Count: > 0 },
            OllamaRoute.Generate => string.IsNullOrEmpty(Text(request["prompt"])),
            _ => false,
        };
    }

    /// <summary>True when <c>keep_alive</c> is zero: with an empty prompt or message list, Ollama's request to unload the model.</summary>
    public static bool AsksToUnload(JsonObject request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request["keep_alive"] is JsonValue value &&
            (value.GetValueKind() == JsonValueKind.Number
                ? value.TryGetValue(out double seconds) && seconds == 0
                : Text(value)?.Trim() is "0" or "0s" or "0m" or "0h");
    }

    /// <summary>The string a node holds; null for anything else.</summary>
    public static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static JsonArray Messages(JsonArray? messages)
    {
        var result = new JsonArray();
        // OpenAI ties a tool result to its call by id; Ollama clients name the tool instead, and may leave ids out.
        var unanswered = new List<(string Id, string Name)>();
        int nextId = 0;
        foreach (var node in messages ?? new JsonArray())
        {
            if (node is not JsonObject message)
            {
                continue;
            }

            string role = (Text(message["role"]) ?? "user").ToLowerInvariant();
            var converted = new JsonObject { ["role"] = role, ["content"] = Content(message, "content") };
            if (message["tool_calls"] is JsonArray { Count: > 0 } calls)
            {
                var convertedCalls = new JsonArray();
                foreach (var call in calls)
                {
                    if (call is not JsonObject callObject || callObject["function"] is not JsonObject function)
                    {
                        continue;
                    }

                    string name = Text(function["name"]) ?? "";
                    string id = Text(callObject["id"]) is { Length: > 0 } given ? given : $"call_{nextId++}";
                    unanswered.Add((id, name));
                    convertedCalls.Add(new JsonObject
                    {
                        ["id"] = id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = name,
                            // Ollama carries the arguments as an object, OpenAI as that object's JSON text.
                            ["arguments"] = Text(function["arguments"]) ?? function["arguments"]?.ToJsonString(Json) ?? "{}",
                        },
                    });
                }

                converted["tool_calls"] = convertedCalls;
            }

            if (role == "tool")
            {
                string? id = Text(message["tool_call_id"]);
                string? name = Text(message["tool_name"]);
                int at = id is not null ? unanswered.FindIndex(c => c.Id == id)
                    : name is not null ? unanswered.FindIndex(c => c.Name == name)
                    : -1;
                if (at < 0 && id is null && unanswered.Count > 0)
                {
                    at = 0;
                }

                if (at >= 0)
                {
                    id = unanswered[at].Id;
                    unanswered.RemoveAt(at);
                }

                converted["tool_call_id"] = id ?? $"call_{nextId++}";
            }

            result.Add(converted);
        }

        return result;
    }

    /// <summary>The message content: the text, or text and image parts when the message carries base64 <c>images</c>.</summary>
    private static JsonNode Content(JsonObject message, string textField)
    {
        var textNode = Take(message, textField);
        if (message["images"] is not JsonArray { Count: > 0 } images)
        {
            return textNode ?? "";
        }

        var parts = new JsonArray();
        if (Text(textNode) is { Length: > 0 } text)
        {
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }

        foreach (var image in images)
        {
            if (Text(image) is { Length: > 0 } data)
            {
                string url = data.StartsWith("data:", StringComparison.Ordinal) ? data : $"data:{ImageType(data)};base64,{data}";
                parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } });
            }
        }

        return parts;
    }

    /// <summary>The media type a data URL needs, from the first bytes of the base64 text (JPEG when none is recognised).</summary>
    private static string ImageType(string base64) =>
        base64.StartsWith("iVBOR", StringComparison.Ordinal) ? "image/png"
        : base64.StartsWith("R0lGOD", StringComparison.Ordinal) ? "image/gif"
        : base64.StartsWith("UklGR", StringComparison.Ordinal) ? "image/webp"
        : "image/jpeg";

    private static void Options(JsonObject? options, JsonObject body)
    {
        if (options is null)
        {
            return;
        }

        foreach (string name in SharedOptions)
        {
            if (Take(options, name) is { } value)
            {
                body[name] = value;
            }
        }

        // -1 (no limit) and -2 (fill the context) have no OpenAI form; the runtime's own limit applies.
        if (options["num_predict"] is JsonValue predict && predict.TryGetValue(out double limit) && limit > 0)
        {
            options.Remove("num_predict");
            body["max_tokens"] = predict;
        }
    }

    private static void Format(JsonObject request, JsonObject body)
    {
        var format = Take(request, "format");
        if (format is JsonObject schema)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "response", ["schema"] = schema },
            };
        }
        else if (Text(format) == "json")
        {
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }
    }

    private static void Think(JsonObject request, JsonObject body)
    {
        if (request["think"] is not JsonValue think)
        {
            return;
        }

        // A level ("low", "medium", "high") switches thinking on; the three OpenAI knows also go out as reasoning_effort.
        string? level = Text(think);
        if (level is null && !think.TryGetValue(out bool _))
        {
            return;
        }

        body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = level is not null || think.GetValue<bool>() };
        if (level is "low" or "medium" or "high")
        {
            body["reasoning_effort"] = level;
        }
    }

    private static JsonNode? Take(JsonObject parent, string name)
    {
        if (!parent.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        parent.Remove(name);
        return node;
    }

    private static bool IsTrue(JsonNode? node) => node is JsonValue value && value.TryGetValue(out bool flag) && flag;

    private static bool IsFalse(JsonNode? node) => node is JsonValue value && value.TryGetValue(out bool flag) && !flag;
}
