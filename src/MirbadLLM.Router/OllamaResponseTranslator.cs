using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MirbadLLM.Router;

/// <summary>
/// Turns one <c>/v1</c> answer into Ollama's shape: an OpenAI event stream into newline-delimited JSON objects
/// (<see cref="Process"/>, <see cref="Complete"/>), one OpenAI object into one Ollama object (<see cref="Convert"/>), and an
/// error body into <c>{"error":"..."}</c> (<see cref="Error"/>).
/// <para>Numbers are the upstream's or measured here, never filled in: the token counts come from <c>usage</c>,
/// <c>prompt_eval_duration</c> and <c>eval_duration</c> from llama.cpp's <c>timings</c> when the answer carries them, and
/// <c>total_duration</c> is the time since the request reached the router. A field with no source is left out, as Ollama
/// leaves out its zero values (<c>load_duration</c> always: a model is loaded before a request reaches it).</para>
/// </summary>
public sealed class OllamaResponseTranslator
{
    private const int MaxLineBytes = 4 * 1024 * 1024;
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly OllamaRoute _route;
    private readonly bool _completions;
    private readonly string _model;
    private readonly TimeProvider _time;
    private readonly long _start;
    private readonly ArrayBufferWriter<byte> _out = new();
    private readonly List<ToolCall> _calls = [];
    private byte[] _pending = new byte[4096];
    private int _count;
    private bool _done;
    private string? _doneReason;
    private long? _promptTokens;
    private long? _cachedTokens;
    private long? _evalTokens;
    private long? _promptNanoseconds;
    private long? _evalNanoseconds;

    /// <summary>Creates the translator of one request.</summary>
    /// <param name="route">The Ollama route the client called.</param>
    /// <param name="completions">True when the upstream call is <c>/v1/completions</c> (<c>/api/generate</c> with <c>raw</c>).</param>
    /// <param name="model">The model name as the client sent it; Ollama answers with the same name.</param>
    /// <param name="time">Clock for <c>created_at</c> and <c>total_duration</c>.</param>
    /// <param name="startTimestamp"><see cref="TimeProvider.GetTimestamp"/> when the request arrived.</param>
    public OllamaResponseTranslator(OllamaRoute route, bool completions, string model, TimeProvider time, long startTimestamp)
    {
        ArgumentNullException.ThrowIfNull(time);
        _route = route;
        _completions = completions;
        _model = model;
        _time = time;
        _start = startTimestamp;
    }

    /// <summary>The Ollama lines for the complete event-stream lines that arrived so far; a partial line waits for its end.</summary>
    public byte[] Process(ReadOnlySpan<byte> input)
    {
        if (_done)
        {
            return [];
        }

        _out.Clear();
        if (_count + input.Length > _pending.Length)
        {
            Array.Resize(ref _pending, Math.Max(_pending.Length * 2, _count + input.Length));
        }

        input.CopyTo(_pending.AsSpan(_count));
        _count += input.Length;

        int start = 0;
        while (!_done && _pending.AsSpan(start, _count - start).IndexOf((byte)'\n') is >= 0 and int length)
        {
            Line(start, length);
            start += length + 1;
        }

        _pending.AsSpan(start, _count - start).CopyTo(_pending);
        _count -= start;
        if (_count > MaxLineBytes && !_done)
        {
            WriteError($"The upstream sent a line longer than {MaxLineBytes} bytes.");
            _done = true;
        }

        return _out.WrittenSpan.ToArray();
    }

    /// <summary>The end of the stream: the last object (<c>done: true</c>) unless <c>[DONE]</c> already produced it.</summary>
    public byte[] Complete()
    {
        if (_done)
        {
            return [];
        }

        _out.Clear();
        Line(0, _count);
        if (!_done)
        {
            Finish();
        }

        return _out.WrittenSpan.ToArray();
    }

    /// <summary>The Ollama object for a complete (not streamed) <c>/v1</c> answer; null when the answer is not the expected OpenAI shape.</summary>
    public byte[]? Convert(ReadOnlyMemory<byte> json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            _out.Clear();
            ReadUsage(root);
            if (_route is OllamaRoute.Embed or OllamaRoute.Embeddings)
            {
                return WriteEmbeddings(root) ? _out.WrittenSpan.ToArray() : null;
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                return null;
            }

            var choice = choices[0];
            string? content;
            string? thinking = null;
            if (_completions)
            {
                content = StringOf(choice, "text");
            }
            else
            {
                if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                content = StringOf(message, "content");
                thinking = StringOf(message, "reasoning_content") ?? StringOf(message, "reasoning");
                if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                {
                    Accumulate(calls);
                }
            }

            WriteObject(content, thinking, _calls, done: true, DoneReason(StringOf(choice, "finish_reason")), metrics: true, line: false);
            return _out.WrittenSpan.ToArray();
        }
    }

    /// <summary>Ollama's answer to a load request: <c>done</c> with <c>done_reason: "load"</c> and no counters.</summary>
    public byte[] Loaded()
    {
        _out.Clear();
        WriteObject(null, null, null, done: true, "load", metrics: false, line: false);
        return _out.WrittenSpan.ToArray();
    }

    /// <summary>
    /// <c>{"error":"..."}</c> from an error body: the message of an OpenAI error object (the router's and the runtimes'),
    /// else a top-level <c>message</c> or <c>detail</c>, else the body's text.
    /// </summary>
    public static byte[] Error(ReadOnlyMemory<byte> body, int statusCode)
    {
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            message = ErrorMessage(document.RootElement);
        }
        catch (JsonException)
        {
            // Not JSON (a proxy's HTML or plain text): the text itself is the message.
        }

        message ??= Encoding.UTF8.GetString(body.Span).Trim() is { Length: > 0 } text ? text : $"The request failed with status {statusCode}.";
        return ErrorObject(message, line: false);
    }

    /// <summary><c>{"error":"..."}</c> for a message.</summary>
    public static byte[] ErrorObject(string message, bool line)
    {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("error", message);
            writer.WriteEndObject();
        }

        if (line)
        {
            output.Write("\n"u8);
        }

        return output.WrittenSpan.ToArray();
    }

    private void Line(int start, int length)
    {
        var line = _pending.AsSpan(start, length).TrimEnd((byte)'\r');
        if (!line.StartsWith("data:"u8))
        {
            return; // comments, event names and the blank lines between events
        }

        int offset = 5;
        while (offset < line.Length && line[offset] == (byte)' ')
        {
            offset++;
        }

        if (line[offset..].SequenceEqual("[DONE]"u8))
        {
            Finish();
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(_pending.AsMemory(start + offset, line.Length - offset));
            Chunk(document.RootElement);
        }
        catch (JsonException)
        {
            // Not a JSON event: nothing to translate.
        }
    }

    private void Chunk(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // A runtime that fails after its headers reports it as an event; Ollama's form is an object with "error".
        if (root.TryGetProperty("error", out _) && ErrorMessage(root) is { } error)
        {
            WriteError(error);
            _done = true;
            return;
        }

        ReadUsage(root);
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            return;
        }

        var choice = choices[0];
        string? content = null;
        string? thinking = null;
        if (_completions)
        {
            content = StringOf(choice, "text");
        }
        else if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
        {
            content = StringOf(delta, "content");
            thinking = StringOf(delta, "reasoning_content") ?? StringOf(delta, "reasoning");
            if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            {
                Accumulate(calls);
            }
        }

        if (!string.IsNullOrEmpty(content) || !string.IsNullOrEmpty(thinking))
        {
            WriteObject(content, thinking, null, done: false, null, metrics: false, line: true);
        }

        if (StringOf(choice, "finish_reason") is { } reason)
        {
            _doneReason = DoneReason(reason);
            FlushToolCalls();
        }
    }

    /// <summary>OpenAI streams a tool call in pieces (the arguments as text fragments); Ollama sends each call whole.</summary>
    private void Accumulate(JsonElement calls)
    {
        foreach (var call in calls.EnumerateArray())
        {
            if (call.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? id = StringOf(call, "id");
            int index = call.TryGetProperty("index", out var position) && position.TryGetInt32(out int given) && given >= 0 && given <= _calls.Count
                ? given
                : id is not null || _calls.Count == 0 ? _calls.Count : _calls.Count - 1;
            if (index == _calls.Count)
            {
                _calls.Add(new ToolCall());
            }

            var target = _calls[index];
            target.Id ??= id;
            if (call.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
            {
                if (StringOf(function, "name") is { Length: > 0 } name)
                {
                    target.Name = name;
                }

                target.Arguments.Append(StringOf(function, "arguments"));
            }
        }
    }

    private void FlushToolCalls()
    {
        if (_calls.Count > 0)
        {
            WriteObject(null, null, _calls, done: false, null, metrics: false, line: true);
            _calls.Clear();
        }
    }

    private void Finish()
    {
        FlushToolCalls();
        WriteObject(null, null, null, done: true, _doneReason, metrics: true, line: true);
        _done = true;
    }

    private void ReadUsage(JsonElement root)
    {
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            _promptTokens = Int64Of(usage, "prompt_tokens") ?? _promptTokens;
            _evalTokens = Int64Of(usage, "completion_tokens") ?? _evalTokens;
            if (usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
            {
                _cachedTokens = Int64Of(details, "cached_tokens") ?? _cachedTokens;
            }
        }

        // llama.cpp reports how long the prompt and the answer took, in milliseconds.
        if (root.TryGetProperty("timings", out var timings) && timings.ValueKind == JsonValueKind.Object)
        {
            _promptNanoseconds = NanosecondsOf(timings, "prompt_ms") ?? _promptNanoseconds;
            _evalNanoseconds = NanosecondsOf(timings, "predicted_ms") ?? _evalNanoseconds;
        }
    }

    private bool WriteEmbeddings(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("embedding", out var vector) || vector.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
        }

        if (_route == OllamaRoute.Embeddings && data.GetArrayLength() == 0)
        {
            return false;
        }

        using var writer = new Utf8JsonWriter(_out, WriterOptions);
        writer.WriteStartObject();
        if (_route == OllamaRoute.Embeddings)
        {
            // The older route answers with the one vector and nothing else.
            writer.WritePropertyName("embedding");
            data[0].GetProperty("embedding").WriteTo(writer);
        }
        else
        {
            writer.WriteString("model", _model);
            writer.WriteStartArray("embeddings");
            foreach (var item in data.EnumerateArray())
            {
                item.GetProperty("embedding").WriteTo(writer);
            }

            writer.WriteEndArray();
            writer.WriteNumber("total_duration", ElapsedNanoseconds());
            if (_promptTokens is { } promptTokens)
            {
                writer.WriteNumber("prompt_eval_count", promptTokens);
            }
        }

        writer.WriteEndObject();
        return true;
    }

    private void WriteObject(string? content, string? thinking, List<ToolCall>? calls, bool done, string? doneReason, bool metrics, bool line)
    {
        using (var writer = new Utf8JsonWriter(_out, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("model", _model);
            writer.WriteString("created_at", _time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            if (_route == OllamaRoute.Chat)
            {
                writer.WriteStartObject("message");
                writer.WriteString("role", "assistant");
                writer.WriteString("content", content ?? "");
                if (!string.IsNullOrEmpty(thinking))
                {
                    writer.WriteString("thinking", thinking);
                }

                if (calls is { Count: > 0 })
                {
                    WriteToolCalls(writer, calls);
                }

                writer.WriteEndObject();
            }
            else
            {
                writer.WriteString("response", content ?? "");
                if (!string.IsNullOrEmpty(thinking))
                {
                    writer.WriteString("thinking", thinking);
                }
            }

            writer.WriteBoolean("done", done);
            if (doneReason is not null)
            {
                writer.WriteString("done_reason", doneReason);
            }

            if (metrics)
            {
                writer.WriteNumber("total_duration", ElapsedNanoseconds());
                WriteOptional(writer, "prompt_eval_count", _promptTokens);
                WriteOptional(writer, "prompt_eval_cached_count", _cachedTokens);
                WriteOptional(writer, "prompt_eval_duration", _promptNanoseconds);
                WriteOptional(writer, "eval_count", _evalTokens);
                WriteOptional(writer, "eval_duration", _evalNanoseconds);
            }

            writer.WriteEndObject();
        }

        if (line)
        {
            _out.Write("\n"u8);
        }
    }

    private static void WriteToolCalls(Utf8JsonWriter writer, List<ToolCall> calls)
    {
        writer.WriteStartArray("tool_calls");
        for (int i = 0; i < calls.Count; i++)
        {
            writer.WriteStartObject();
            if (calls[i].Id is { Length: > 0 } id)
            {
                writer.WriteString("id", id);
            }

            writer.WriteStartObject("function");
            writer.WriteNumber("index", i);
            writer.WriteString("name", calls[i].Name);
            writer.WritePropertyName("arguments");
            string arguments = calls[i].Arguments.ToString();
            JsonDocument? parsed = null;
            try
            {
                parsed = JsonDocument.Parse(arguments.Length == 0 ? "{}" : arguments);
            }
            catch (JsonException)
            {
                // The model wrote arguments that are not JSON: they go out as the text they are.
            }

            if (parsed is null)
            {
                writer.WriteStringValue(arguments);
            }
            else
            {
                using (parsed)
                {
                    parsed.RootElement.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private void WriteError(string message) => _out.Write(ErrorObject(message, line: true));

    private static void WriteOptional(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
    }

    private long ElapsedNanoseconds() => (long)_time.GetElapsedTime(_start).TotalNanoseconds;

    /// <summary>Ollama ends a turn that calls tools with <c>stop</c>; every other reason keeps the upstream's word.</summary>
    private static string? DoneReason(string? finishReason) => finishReason == "tool_calls" ? "stop" : finishReason;

    private static string? ErrorMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String)
            {
                return error.GetString();
            }

            if (error.ValueKind == JsonValueKind.Object && StringOf(error, "message") is { } message)
            {
                return message;
            }
        }

        return StringOf(root, "message") ?? StringOf(root, "detail");
    }

    private static string? StringOf(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Int64Of(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : null;

    private static long? NanosecondsOf(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double milliseconds) && milliseconds >= 0
            ? (long)(milliseconds * 1_000_000)
            : null;

    private sealed class ToolCall
    {
        public string? Id { get; set; }

        public string Name { get; set; } = "";

        public StringBuilder Arguments { get; } = new();
    }
}
