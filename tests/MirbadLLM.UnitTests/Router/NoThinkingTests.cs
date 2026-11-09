using System.Text;
using System.Text.Json;
using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Router;

public sealed class NoThinkingTests
{
    [Theory]
    [InlineData("qwen-nothink", "qwen")]
    [InlineData("Qwen-NoThink", "Qwen")]
    [InlineData("qwen", null)]
    [InlineData("-nothink", null)]
    public void BaseName_StripsTheSuffixOnly(string requested, string? expected) => Assert.Equal(expected, NoThinking.BaseName(requested));

    [Fact]
    public void Rewrite_SetsTheModel_AndSwitchesThinkingOffOnlyForAChat()
    {
        byte[] body = Encoding.UTF8.GetBytes("""{"model":"q-nothink","chat_template_kwargs":{"enable_thinking":true},"stream":true}""");

        using var chat = JsonDocument.Parse(NoThinking.Rewrite(body, "q", chat: true));
        Assert.Equal("q", chat.RootElement.GetProperty("model").GetString());
        Assert.False(chat.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.True(chat.RootElement.GetProperty("stream").GetBoolean());

        using var other = JsonDocument.Parse(NoThinking.Rewrite(Encoding.UTF8.GetBytes("""{"model":"q-nothink"}"""), "q", chat: false));
        Assert.False(other.RootElement.TryGetProperty("chat_template_kwargs", out _));
    }

    [Fact]
    public void Offered_OnlyForChatModelsTaggedReasoning()
    {
        Assert.True(NoThinking.Offered(new ModelDefinition { Id = "a", ServedName = "a", ModelPath = "a", Tags = [ModelTags.Reasoning] }));
        Assert.False(NoThinking.Offered(new ModelDefinition { Id = "b", ServedName = "b", ModelPath = "b" }));
    }
}
