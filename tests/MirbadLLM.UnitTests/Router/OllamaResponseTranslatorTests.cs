using System.Text;
using System.Text.Json;
using MirbadLLM.Router;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Router;

/// <summary>
/// <c>/v1</c> answers as Ollama's (field names from Ollama's API reference): event streams as newline-delimited JSON,
/// single objects, and errors. The counters must be the upstream's or measured, and absent when there is no source.
/// </summary>
public sealed class OllamaResponseTranslatorTests
{
    /// <summary>A clock that only moves when told to, so <c>total_duration</c> is exact.</summary>
    private sealed class Clock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 11, 9, 30, 0, TimeSpan.Zero).AddTicks(_ticks);

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private static OllamaResponseTranslator New(OllamaRoute route, Clock? clock = null, bool completions = false, string model = "qwen:latest")
    {
        clock ??= new Clock();
        return new OllamaResponseTranslator(route, completions, model, clock, clock.GetTimestamp());
    }

    private static List<JsonElement> Lines(params byte[][] outputs)
    {
        string text = string.Concat(outputs.Select(Encoding.UTF8.GetString));
        Assert.True(text.Length == 0 || text.EndsWith('\n'), "every object of a stream ends its line");
        return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement)];
    }

    private static byte[] Sse(params string[] events) => Encoding.UTF8.GetBytes(string.Concat(events.Select(e => $"data: {e}\n\n")));

    [Fact]
    public void AChatStream_BecomesOneObjectPerDelta_AndAFinalObjectWithTheUpstreamsCounts()
    {
        var clock = new Clock();
        var translator = New(OllamaRoute.Chat, clock);
        clock.Advance(TimeSpan.FromMilliseconds(1500));

        var lines = Lines(translator.Process(Sse(
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"reasoning_content":"The user greets."},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"reasoning":" Reply."},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"content":" there"},"finish_reason":"stop"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":26,"completion_tokens":9,"total_tokens":35,"prompt_tokens_details":{"cached_tokens":20}}}""",
            "[DONE]")), translator.Complete());

        Assert.Equal(5, lines.Count);
        Assert.All(lines, line => Assert.Equal("qwen:latest", line.GetProperty("model").GetString()));
        Assert.All(lines, line => Assert.Equal("assistant", line.GetProperty("message").GetProperty("role").GetString()));
        Assert.All(lines, line => Assert.EndsWith("Z", line.GetProperty("created_at").GetString(), StringComparison.Ordinal));
        Assert.All(lines.Take(4), line => Assert.False(line.GetProperty("done").GetBoolean()));

        Assert.Equal("The user greets.", lines[0].GetProperty("message").GetProperty("thinking").GetString());
        Assert.Equal("", lines[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(" Reply.", lines[1].GetProperty("message").GetProperty("thinking").GetString());
        Assert.Equal("Hello", lines[2].GetProperty("message").GetProperty("content").GetString());
        Assert.False(lines[2].GetProperty("message").TryGetProperty("thinking", out _));
        Assert.Equal(" there", lines[3].GetProperty("message").GetProperty("content").GetString());

        var last = lines[4];
        Assert.True(last.GetProperty("done").GetBoolean());
        Assert.Equal("stop", last.GetProperty("done_reason").GetString());
        Assert.Equal("", last.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(26, last.GetProperty("prompt_eval_count").GetInt64());
        Assert.Equal(20, last.GetProperty("prompt_eval_cached_count").GetInt64());
        Assert.Equal(9, last.GetProperty("eval_count").GetInt64());
        Assert.Equal(1_500_000_000, last.GetProperty("total_duration").GetInt64()); // nanoseconds since the request arrived
    }

    [Fact]
    public void NumbersWithoutASource_AreLeftOut_NotZeroed()
    {
        var translator = New(OllamaRoute.Chat);

        var last = Lines(translator.Process(Sse(
            """{"choices":[{"delta":{"content":"Hi"},"finish_reason":"stop"}]}""", "[DONE]")))[^1];

        Assert.True(last.GetProperty("done").GetBoolean());
        foreach (string unknown in new[] { "load_duration", "prompt_eval_count", "prompt_eval_duration", "eval_count", "eval_duration", "prompt_eval_cached_count", "context" })
        {
            Assert.False(last.TryGetProperty(unknown, out _), unknown);
        }
    }

    [Fact]
    public void LlamaCppTimings_GiveTheEvalDurations_InNanoseconds()
    {
        var translator = New(OllamaRoute.Chat);

        var last = Lines(translator.Process(Sse(
            """{"choices":[{"delta":{"content":"Hi"},"finish_reason":"stop"}],"timings":{"prompt_n":12,"prompt_ms":130.079,"predicted_n":259,"predicted_ms":4232.71}}""",
            """{"choices":[],"usage":{"prompt_tokens":12,"completion_tokens":259}}""",
            "[DONE]")))[^1];

        Assert.Equal(130_079_000, last.GetProperty("prompt_eval_duration").GetInt64());
        Assert.Equal(4_232_710_000, last.GetProperty("eval_duration").GetInt64());
        Assert.Equal(259, last.GetProperty("eval_count").GetInt64());
    }

    [Fact]
    public void AnEventSplitAcrossReads_WaitsForItsEnd()
    {
        var translator = New(OllamaRoute.Chat);
        byte[] sse = Sse("""{"choices":[{"delta":{"content":"Grüße"},"finish_reason":null}]}""");
        int cut = sse.Length / 2;

        Assert.Empty(translator.Process(sse.AsSpan(0, cut)));
        var line = Assert.Single(Lines(translator.Process(sse.AsSpan(cut))));

        Assert.Equal("Grüße", line.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public void StreamedToolCallFragments_BecomeWholeCalls_WithArgumentsAsAnObject()
    {
        var translator = New(OllamaRoute.Chat);

        var lines = Lines(translator.Process(Sse(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"get_weather","arguments":""}}]},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"city\":"}}]},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"Tokyo\"}"}},{"index":1,"id":"call_b","function":{"name":"get_time","arguments":"{}"}}]},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
            "[DONE]")));

        Assert.Equal(2, lines.Count);
        var calls = lines[0].GetProperty("message").GetProperty("tool_calls");
        Assert.False(lines[0].GetProperty("done").GetBoolean());
        Assert.Equal(2, calls.GetArrayLength());
        Assert.Equal("call_a", calls[0].GetProperty("id").GetString());
        Assert.Equal("get_weather", calls[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(0, calls[0].GetProperty("function").GetProperty("index").GetInt32());
        Assert.Equal("Tokyo", calls[0].GetProperty("function").GetProperty("arguments").GetProperty("city").GetString());
        Assert.Equal("get_time", calls[1].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(1, calls[1].GetProperty("function").GetProperty("index").GetInt32());
        Assert.Equal(JsonValueKind.Object, calls[1].GetProperty("function").GetProperty("arguments").ValueKind);
        // Ollama ends a tool-calling turn with "stop".
        Assert.True(lines[1].GetProperty("done").GetBoolean());
        Assert.Equal("stop", lines[1].GetProperty("done_reason").GetString());
    }

    [Fact]
    public void AnErrorEvent_BecomesOllamasErrorLine_AndEndsTheStream()
    {
        var translator = New(OllamaRoute.Chat);

        var lines = Lines(
            translator.Process(Sse(
                """{"choices":[{"delta":{"content":"Yes"},"finish_reason":null}]}""",
                """{"error":{"message":"CUDA out of memory","type":"internal_error","code":500}}""",
                """{"choices":[{"delta":{"content":"never sent"},"finish_reason":"stop"}]}""",
                "[DONE]")),
            translator.Complete());

        Assert.Equal(2, lines.Count);
        Assert.Equal("Yes", lines[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("CUDA out of memory", lines[1].GetProperty("error").GetString());
        Assert.False(lines[1].TryGetProperty("done", out _));
    }

    [Fact]
    public void AStreamThatEndsWithoutDone_StillEndsWithAFinalObject_WithoutAReasonNobodyGave()
    {
        var translator = New(OllamaRoute.Chat);

        var lines = Lines(
            translator.Process(Encoding.UTF8.GetBytes(": keep-alive\n\nevent: message\ndata: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\r\n\r\n")),
            translator.Complete(),
            translator.Complete());

        Assert.Equal(2, lines.Count);
        Assert.Equal("Hi", lines[0].GetProperty("message").GetProperty("content").GetString());
        Assert.True(lines[1].GetProperty("done").GetBoolean());
        Assert.False(lines[1].TryGetProperty("done_reason", out _));
    }

    [Fact]
    public void AGenerateStream_UsesResponseAndThinking_AndRawReadsTheCompletionText()
    {
        var generate = New(OllamaRoute.Generate);
        var lines = Lines(generate.Process(Sse(
            """{"choices":[{"delta":{"reasoning_content":"hm"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"content":"The"},"finish_reason":"length"}]}""",
            "[DONE]")));
        Assert.Equal("hm", lines[0].GetProperty("thinking").GetString());
        Assert.Equal("", lines[0].GetProperty("response").GetString());
        Assert.Equal("The", lines[1].GetProperty("response").GetString());
        Assert.False(lines[1].TryGetProperty("message", out _));
        Assert.Equal("length", lines[2].GetProperty("done_reason").GetString());
        Assert.Equal("", lines[2].GetProperty("response").GetString());

        var raw = New(OllamaRoute.Generate, completions: true);
        var rawLines = Lines(raw.Process(Sse(
            """{"choices":[{"index":0,"text":" sky","finish_reason":null}]}""",
            """{"choices":[{"index":0,"text":"","finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":1}}""",
            "[DONE]")));
        Assert.Equal(" sky", rawLines[0].GetProperty("response").GetString());
        Assert.Equal(1, rawLines[1].GetProperty("eval_count").GetInt64());
    }

    [Fact]
    public void ACompleteChatAnswer_BecomesOneObject_WithThinkingToolCallsAndCounts()
    {
        var clock = new Clock();
        var translator = New(OllamaRoute.Chat, clock);
        clock.Advance(TimeSpan.FromSeconds(2));

        byte[]? converted = translator.Convert(Encoding.UTF8.GetBytes("""
            {"id":"chatcmpl-1","object":"chat.completion","choices":[{"index":0,"finish_reason":"tool_calls","message":{
               "role":"assistant","content":null,"reasoning_content":"Need the weather.",
               "tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Tokyo\"}"}}]}}],
             "usage":{"prompt_tokens":169,"completion_tokens":18,"total_tokens":187}}
            """));

        Assert.NotNull(converted);
        Assert.DoesNotContain("\n", Encoding.UTF8.GetString(converted), StringComparison.Ordinal);
        var answer = JsonDocument.Parse(converted).RootElement;
        Assert.Equal("qwen:latest", answer.GetProperty("model").GetString());
        var message = answer.GetProperty("message");
        Assert.Equal("assistant", message.GetProperty("role").GetString());
        Assert.Equal("", message.GetProperty("content").GetString());
        Assert.Equal("Need the weather.", message.GetProperty("thinking").GetString());
        Assert.Equal("Tokyo", message.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetProperty("city").GetString());
        Assert.True(answer.GetProperty("done").GetBoolean());
        Assert.Equal("stop", answer.GetProperty("done_reason").GetString());
        Assert.Equal(169, answer.GetProperty("prompt_eval_count").GetInt64());
        Assert.Equal(18, answer.GetProperty("eval_count").GetInt64());
        Assert.Equal(2_000_000_000, answer.GetProperty("total_duration").GetInt64());
        Assert.False(answer.TryGetProperty("eval_duration", out _));
    }

    [Fact]
    public void ACompleteGenerateAnswer_HasTheTextInResponse()
    {
        var chat = JsonDocument.Parse(New(OllamaRoute.Generate).Convert(Encoding.UTF8.GetBytes(
            """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"Because of Rayleigh scattering.","reasoning":"physics"}}]}"""))!).RootElement;
        Assert.Equal("Because of Rayleigh scattering.", chat.GetProperty("response").GetString());
        Assert.Equal("physics", chat.GetProperty("thinking").GetString());
        Assert.True(chat.GetProperty("done").GetBoolean());

        var raw = JsonDocument.Parse(New(OllamaRoute.Generate, completions: true).Convert(Encoding.UTF8.GetBytes(
            """{"choices":[{"finish_reason":"length","text":" blue"}]}"""))!).RootElement;
        Assert.Equal(" blue", raw.GetProperty("response").GetString());
        Assert.Equal("length", raw.GetProperty("done_reason").GetString());
    }

    [Fact]
    public void Embeddings_KeepTheUpstreamsNumbersAsWritten()
    {
        const string OpenAi = """
            {"object":"list","data":[{"object":"embedding","index":0,"embedding":[0.010071029,-0.0017594862,1e-7]},
                                     {"object":"embedding","index":1,"embedding":[0.5,0.25,-0.125]}],
             "model":"bge","usage":{"prompt_tokens":8,"total_tokens":8}}
            """;

        string embed = Encoding.UTF8.GetString(New(OllamaRoute.Embed, model: "bge:latest").Convert(Encoding.UTF8.GetBytes(OpenAi))!);
        Assert.Contains("[[0.010071029,-0.0017594862,1e-7],[0.5,0.25,-0.125]]", embed, StringComparison.Ordinal);
        var answer = JsonDocument.Parse(embed).RootElement;
        Assert.Equal("bge:latest", answer.GetProperty("model").GetString());
        Assert.Equal(8, answer.GetProperty("prompt_eval_count").GetInt64());
        Assert.True(answer.TryGetProperty("total_duration", out _));
        Assert.False(answer.TryGetProperty("load_duration", out _));

        // The older route answers with the single vector only.
        string older = Encoding.UTF8.GetString(New(OllamaRoute.Embeddings).Convert(Encoding.UTF8.GetBytes(OpenAi))!);
        Assert.Equal("""{"embedding":[0.010071029,-0.0017594862,1e-7]}""", older);
    }

    [Theory]
    [InlineData(OllamaRoute.Chat, "not json")]
    [InlineData(OllamaRoute.Chat, """{"object":"something else"}""")]
    [InlineData(OllamaRoute.Chat, """{"choices":[]}""")]
    [InlineData(OllamaRoute.Embed, """{"data":[{"index":0}]}""")]
    [InlineData(OllamaRoute.Embeddings, """{"data":[]}""")]
    public void AnAnswerThatIsNotTheOpenAiShape_IsNotTranslated(OllamaRoute route, string body) =>
        Assert.Null(New(route).Convert(Encoding.UTF8.GetBytes(body)));

    [Theory]
    [InlineData("""{"error":{"message":"Model 'x' is not registered.","type":"invalid_request_error","code":"model_not_found"}}""", "Model 'x' is not registered.")]
    [InlineData("""{"error":"already ollama shaped"}""", "already ollama shaped")]
    [InlineData("""{"object":"error","message":"max_tokens is too large","type":"BadRequestError","code":400}""", "max_tokens is too large")]
    [InlineData("""{"detail":"Not Found"}""", "Not Found")]
    [InlineData("  Bad Gateway\n", "Bad Gateway")]
    [InlineData("", "The request failed with status 503.")]
    public void Errors_BecomeOneErrorString(string body, string message)
    {
        string error = Encoding.UTF8.GetString(OllamaResponseTranslator.Error(Encoding.UTF8.GetBytes(body), 503));

        using var document = JsonDocument.Parse(error);
        Assert.Equal(message, document.RootElement.GetProperty("error").GetString());
        Assert.Single(document.RootElement.EnumerateObject());
    }

    [Fact]
    public void ALoadAnswer_SaysLoad_AndCarriesNoCounters()
    {
        var chat = JsonDocument.Parse(New(OllamaRoute.Chat).Loaded()).RootElement;
        Assert.Equal("load", chat.GetProperty("done_reason").GetString());
        Assert.True(chat.GetProperty("done").GetBoolean());
        Assert.Equal("", chat.GetProperty("message").GetProperty("content").GetString());
        Assert.False(chat.TryGetProperty("total_duration", out _));

        var generate = JsonDocument.Parse(New(OllamaRoute.Generate).Loaded()).RootElement;
        Assert.Equal("", generate.GetProperty("response").GetString());
        Assert.Equal("load", generate.GetProperty("done_reason").GetString());
    }
}
