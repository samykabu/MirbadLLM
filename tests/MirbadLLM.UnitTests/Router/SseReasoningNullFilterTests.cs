using System.Text;
using MirbadLLM.Router;

namespace MirbadLLM.UnitTests.Router;

public sealed class SseReasoningNullFilterTests
{
    // SGLang's own chunks, as read from the DGX.
    private const string First = """data: {"id":"x","choices":[{"index":0,"delta":{"reasoning_content":null,"role":"assistant","content":""},"finish_reason":null}]}""";
    private const string Answer = """data: {"id":"x","choices":[{"index":0,"delta":{"reasoning_content":null,"content":"Hello"},"finish_reason":null}]}""";
    private const string Last = """data: {"id":"x","choices":[{"index":0,"delta":{"reasoning_content":null},"finish_reason":"stop"}]}""";
    private const string Thinking = """data: {"id":"x","choices":[{"index":0,"delta":{"reasoning_content":"the \"reasoning_content\":null text"}}]}""";

    [Theory]
    [InlineData(First, """data: {"id":"x","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}""")]
    [InlineData(Answer, """data: {"id":"x","choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":null}]}""")]
    [InlineData(Last, """data: {"id":"x","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""")]
    [InlineData("""data: {"delta":{"content":"a","reasoning_content":null}}""", """data: {"delta":{"content":"a"}}""")]
    [InlineData(Thinking, Thinking)]
    [InlineData("data: [DONE]", "data: [DONE]")]
    public void Clean_DropsOnlyTheNullField(string line, string expected)
        => Assert.Equal(expected, Encoding.UTF8.GetString(SseReasoningNullFilter.Clean(Encoding.UTF8.GetBytes(line))));

    [Fact]
    public void Process_HoldsAPartialLineUntilItsEnd_AndFlushesTheRest()
    {
        var filter = new SseReasoningNullFilter();
        byte[] stream = Encoding.UTF8.GetBytes(Answer + "\n\n" + Last + "\n\ndata: [DONE]");
        var output = new List<byte>();

        // One byte at a time: the field is cut at every possible point.
        foreach (byte b in stream)
        {
            output.AddRange(filter.Process([b]));
        }

        output.AddRange(filter.Flush());
        string text = Encoding.UTF8.GetString([.. output]);
        Assert.DoesNotContain("reasoning_content", text, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]", text, StringComparison.Ordinal);
        Assert.Contains("\"content\":\"Hello\"", text, StringComparison.Ordinal);
    }
}
