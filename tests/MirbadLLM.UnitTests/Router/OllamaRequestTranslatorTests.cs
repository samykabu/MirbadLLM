using System.Text.Json;
using System.Text.Json.Nodes;
using MirbadLLM.Router;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Router;

/// <summary>Ollama request bodies (field names from Ollama's API reference) as the OpenAI bodies the runtimes accept.</summary>
public sealed class OllamaRequestTranslatorTests
{
    private static JsonElement Translate(OllamaRoute route, string json, out OllamaTranslatedRequest translated, string model = "served")
    {
        translated = OllamaRequestTranslator.Translate(route, JsonNode.Parse(json)!.AsObject(), model);
        return JsonDocument.Parse(translated.Body.ToJsonString(OllamaRequestTranslator.Json)).RootElement;
    }

    [Fact]
    public void Chat_StreamsByDefault_AndAsksTheUpstreamForTheTokenCounts()
    {
        var body = Translate(OllamaRoute.Chat, """{"model":"served:latest","messages":[{"role":"user","content":"hi"}],"keep_alive":"5m"}""", out var translated);

        Assert.Equal("chat/completions", translated.Path);
        Assert.True(translated.Stream);
        Assert.Equal("served", body.GetProperty("model").GetString());
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal("user", body.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("hi", body.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.False(body.TryGetProperty("keep_alive", out _));
    }

    [Fact]
    public void Chat_StreamFalse_IsOneAnswer_WithoutStreamOptions()
    {
        var body = Translate(OllamaRoute.Chat, """{"model":"m","messages":[{"role":"user","content":"hi"}],"stream":false}""", out var translated);

        Assert.False(translated.Stream);
        Assert.False(body.GetProperty("stream").GetBoolean());
        Assert.False(body.TryGetProperty("stream_options", out _));
    }

    [Fact]
    public void Options_MapToTheirOpenAiNames_AndTheOnesWithoutAnEquivalentAreLeftOut()
    {
        var body = Translate(OllamaRoute.Chat, """
            {"model":"m","messages":[{"role":"user","content":"hi"}],
             "options":{"temperature":0.2,"top_p":0.9,"top_k":40,"min_p":0.05,"num_predict":128,"stop":["\n\n","END"],"seed":7,
                        "presence_penalty":0.5,"frequency_penalty":0.25,"num_ctx":8192,"num_gpu":99,"repeat_penalty":1.1,"mirostat":2}}
            """, out _);

        Assert.Equal(0.2, body.GetProperty("temperature").GetDouble());
        Assert.Equal(0.9, body.GetProperty("top_p").GetDouble());
        Assert.Equal(40, body.GetProperty("top_k").GetInt32());
        Assert.Equal(0.05, body.GetProperty("min_p").GetDouble());
        Assert.Equal(128, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal(["\n\n", "END"], body.GetProperty("stop").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(7, body.GetProperty("seed").GetInt32());
        Assert.Equal(0.5, body.GetProperty("presence_penalty").GetDouble());
        Assert.Equal(0.25, body.GetProperty("frequency_penalty").GetDouble());
        foreach (string dropped in new[] { "options", "num_ctx", "num_gpu", "repeat_penalty", "mirostat", "num_predict" })
        {
            Assert.False(body.TryGetProperty(dropped, out _), dropped);
        }
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("-2")]
    [InlineData("0")]
    public void NumPredict_WithoutALimit_SetsNoMaxTokens(string value)
    {
        var body = Translate(OllamaRoute.Chat, """{"model":"m","messages":[{"role":"user","content":"hi"}],"options":{"num_predict":N}}""".Replace("N", value, StringComparison.Ordinal), out _);

        Assert.False(body.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public void Images_BecomeDataUrlParts_AfterTheText()
    {
        var body = Translate(OllamaRoute.Chat, """
            {"model":"m","messages":[{"role":"user","content":"what is this?","images":["iVBORw0KGgoAAAA+/x==","/9j/4AAQSkZJRg==","data:image/gif;base64,R0lGODlh"]}]}
            """, out _);

        var parts = body.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(4, parts.GetArrayLength());
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        Assert.Equal("what is this?", parts[0].GetProperty("text").GetString());
        Assert.Equal("image_url", parts[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,iVBORw0KGgoAAAA+/x==", parts[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("data:image/jpeg;base64,/9j/4AAQSkZJRg==", parts[2].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("data:image/gif;base64,R0lGODlh", parts[3].GetProperty("image_url").GetProperty("url").GetString());
        Assert.False(body.GetProperty("messages")[0].TryGetProperty("images", out _));
    }

    [Fact]
    public void Base64_IsNotEscaped_SoAnImageKeepsItsSize()
    {
        var translated = OllamaRequestTranslator.Translate(
            OllamaRoute.Chat, JsonNode.Parse("""{"model":"m","messages":[{"role":"user","content":"x","images":["iVBOR+a+b/c=="]}]}""")!.AsObject(), "m");

        Assert.Contains("iVBOR+a+b/c==", JsonSerializer.Serialize(translated.Body, OllamaRequestTranslator.Json), StringComparison.Ordinal);
    }

    [Fact]
    public void Tools_PassThrough_AndToolCallsAndResultsGetOpenAiIds()
    {
        var body = Translate(OllamaRoute.Chat, """
            {"model":"m",
             "tools":[{"type":"function","function":{"name":"get_weather","description":"d","parameters":{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}}}],
             "messages":[
               {"role":"user","content":"weather in Toronto and Tokyo?"},
               {"role":"assistant","content":"","tool_calls":[
                 {"function":{"name":"get_weather","arguments":{"city":"Toronto"}}},
                 {"function":{"name":"get_time","arguments":{"zone":"JST"}}},
                 {"id":"call_given","function":{"index":2,"name":"get_weather","arguments":{"city":"Tokyo"}}}]},
               {"role":"tool","content":"09:00","tool_name":"get_time"},
               {"role":"tool","content":"11 degrees","tool_name":"get_weather"},
               {"role":"tool","content":"19 degrees","tool_call_id":"call_given"}]}
            """, out _);

        Assert.Equal("get_weather", body.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("city", body.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters").GetProperty("required")[0].GetString());

        var messages = body.GetProperty("messages");
        var calls = messages[1].GetProperty("tool_calls");
        Assert.Equal(3, calls.GetArrayLength());
        Assert.All(calls.EnumerateArray(), call => Assert.Equal("function", call.GetProperty("type").GetString()));
        // OpenAI carries the arguments as JSON text.
        Assert.Equal(JsonValueKind.String, calls[0].GetProperty("function").GetProperty("arguments").ValueKind);
        using (var arguments = JsonDocument.Parse(calls[0].GetProperty("function").GetProperty("arguments").GetString()!))
        {
            Assert.Equal("Toronto", arguments.RootElement.GetProperty("city").GetString());
        }

        string weather = calls[0].GetProperty("id").GetString()!;
        string time = calls[1].GetProperty("id").GetString()!;
        Assert.NotEqual(weather, time);
        Assert.Equal("call_given", calls[2].GetProperty("id").GetString());

        // A result finds its call by the tool's name (the first one not answered yet), or by the id the client sent.
        Assert.Equal(time, messages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal(weather, messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("call_given", messages[4].GetProperty("tool_call_id").GetString());
        Assert.Equal("11 degrees", messages[3].GetProperty("content").GetString());
        Assert.False(messages[3].TryGetProperty("tool_name", out _));
    }

    [Theory]
    [InlineData("true", true, null)]
    [InlineData("false", false, null)]
    [InlineData("\"high\"", true, "high")]
    [InlineData("\"max\"", true, null)]
    public void Think_SetsTheTemplateSwitch_AndAKnownLevelAlsoTheEffort(string think, bool enabled, string? effort)
    {
        var body = Translate(OllamaRoute.Chat, $$"""{"model":"m","messages":[{"role":"user","content":"hi"}],"think":{{think}}}""", out _);

        Assert.Equal(enabled, body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.Equal(effort, body.TryGetProperty("reasoning_effort", out var value) ? value.GetString() : null);
    }

    [Fact]
    public void WithoutThink_TheModelDecides()
    {
        var body = Translate(OllamaRoute.Chat, """{"model":"m","messages":[{"role":"user","content":"hi"}],"think":null}""", out _);

        Assert.False(body.TryGetProperty("chat_template_kwargs", out _));
    }

    [Fact]
    public void Format_Json_IsJsonMode_AndASchemaIsAJsonSchema()
    {
        var mode = Translate(OllamaRoute.Chat, """{"model":"m","messages":[{"role":"user","content":"hi"}],"format":"json"}""", out _);
        Assert.Equal("json_object", mode.GetProperty("response_format").GetProperty("type").GetString());

        var schema = Translate(OllamaRoute.Chat, """
            {"model":"m","messages":[{"role":"user","content":"hi"}],"format":{"type":"object","properties":{"age":{"type":"integer"}},"required":["age"]}}
            """, out _);
        var format = schema.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(format.GetProperty("json_schema").GetProperty("name").GetString()));
        Assert.Equal("integer", format.GetProperty("json_schema").GetProperty("schema").GetProperty("properties").GetProperty("age").GetProperty("type").GetString());

        var none = Translate(OllamaRoute.Chat, """{"model":"m","messages":[{"role":"user","content":"hi"}],"format":""}""", out _);
        Assert.False(none.TryGetProperty("response_format", out _));
    }

    [Fact]
    public void Generate_IsAChatCompletion_WithTheSystemPromptAndImages()
    {
        var body = Translate(OllamaRoute.Generate, """
            {"model":"m","prompt":"Why is the sky blue?","system":"Answer briefly.","images":["iVBORw0KGgo="],"options":{"temperature":0},"think":false,"format":"json"}
            """, out var translated);

        Assert.Equal("chat/completions", translated.Path);
        var messages = body.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("Answer briefly.", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("Why is the sky blue?", messages[1].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.StartsWith("data:image/png;base64,", messages[1].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, body.GetProperty("temperature").GetDouble());
        Assert.False(body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.Equal("json_object", body.GetProperty("response_format").GetProperty("type").GetString());
        Assert.False(body.TryGetProperty("prompt", out _));
    }

    [Fact]
    public void Generate_Raw_IsACompletion_WithThePromptAsItIs()
    {
        var body = Translate(OllamaRoute.Generate, """
            {"model":"m","prompt":"[INST] hi [/INST]","raw":true,"system":"ignored","stream":false,"options":{"num_predict":16,"stop":"</s>"}}
            """, out var translated);

        Assert.Equal("completions", translated.Path);
        Assert.False(translated.Stream);
        Assert.Equal("[INST] hi [/INST]", body.GetProperty("prompt").GetString());
        Assert.Equal(16, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal("</s>", body.GetProperty("stop").GetString());
        Assert.False(body.TryGetProperty("messages", out _));
        Assert.False(body.TryGetProperty("chat_template_kwargs", out _));
    }

    [Fact]
    public void Embed_SendsItsInputAndDimensions_AndTheOlderRouteItsPrompt()
    {
        var one = Translate(OllamaRoute.Embed, """{"model":"e","input":"Why is the sky blue?","truncate":true,"keep_alive":"1m"}""", out var translated);
        Assert.Equal("embeddings", translated.Path);
        Assert.False(translated.Stream);
        Assert.Equal("Why is the sky blue?", one.GetProperty("input").GetString());
        Assert.Equal(2, one.EnumerateObject().Count()); // model and input: nothing else has an OpenAI equivalent

        var many = Translate(OllamaRoute.Embed, """{"model":"e","input":["a","b"],"dimensions":256}""", out _);
        Assert.Equal(["a", "b"], many.GetProperty("input").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(256, many.GetProperty("dimensions").GetInt32());

        var older = Translate(OllamaRoute.Embeddings, """{"model":"e","prompt":"Here is an article"}""", out translated);
        Assert.Equal("embeddings", translated.Path);
        Assert.Equal("Here is an article", older.GetProperty("input").GetString());
    }

    [Theory]
    [InlineData(OllamaRoute.Generate, """{"model":"m"}""", true)]
    [InlineData(OllamaRoute.Generate, """{"model":"m","prompt":""}""", true)]
    [InlineData(OllamaRoute.Generate, """{"model":"m","prompt":"hi"}""", false)]
    [InlineData(OllamaRoute.Chat, """{"model":"m","messages":[]}""", true)]
    [InlineData(OllamaRoute.Chat, """{"model":"m"}""", true)]
    [InlineData(OllamaRoute.Chat, """{"model":"m","messages":[{"role":"user","content":"hi"}]}""", false)]
    [InlineData(OllamaRoute.Embed, """{"model":"m"}""", false)]
    public void ARequestWithoutPromptOrMessages_IsALoadRequest(OllamaRoute route, string json, bool load) =>
        Assert.Equal(load, OllamaRequestTranslator.IsLoad(route, JsonNode.Parse(json)!.AsObject()));

    [Theory]
    [InlineData("0", true)]
    [InlineData("\"0\"", true)]
    [InlineData("\"0s\"", true)]
    [InlineData("\"5m\"", false)]
    [InlineData("-1", false)]
    [InlineData("300", false)]
    public void KeepAliveZero_AsksToUnload(string keepAlive, bool unload) =>
        Assert.Equal(unload, OllamaRequestTranslator.AsksToUnload(JsonNode.Parse($$"""{"model":"m","keep_alive":{{keepAlive}}}""")!.AsObject()));
}
