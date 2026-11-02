using MirbadLLM.Web;

namespace MirbadLLM.UnitTests.Web;

/// <summary>Unit tests for <see cref="ChatContentSplitter"/>: the safe fenced-code splitter used by the
/// Chat page so model output is never interpreted as markup.</summary>
public sealed class ChatContentSplitterTests
{
    [Fact]
    public void PlainTextReturnsSingleTextSegment()
    {
        var segments = ChatContentSplitter.Split("hello there, how are you?");

        var segment = Assert.Single(segments);
        Assert.False(segment.IsCode);
        Assert.Equal("hello there, how are you?", segment.Text);
    }

    [Fact]
    public void FencedCodeBlockExtractedWithLanguage()
    {
        var segments = ChatContentSplitter.Split("before\n```csharp\nvar x = 1;\n```\nafter");

        Assert.Equal(3, segments.Count);
        Assert.False(segments[0].IsCode);
        Assert.Equal("before\n", segments[0].Text);
        Assert.True(segments[1].IsCode);
        Assert.Equal("csharp", segments[1].Language);
        Assert.Equal("var x = 1;", segments[1].Text);
        Assert.False(segments[2].IsCode);
        Assert.Equal("\nafter", segments[2].Text);
    }

    [Fact]
    public void UnterminatedFenceStillStreamingIsRenderedAsCode()
    {
        var segments = ChatContentSplitter.Split("some text\n```python\nprint('partial");

        Assert.Equal(2, segments.Count);
        Assert.False(segments[0].IsCode);
        Assert.True(segments[1].IsCode);
        Assert.Equal("python", segments[1].Language);
        Assert.Equal("print('partial", segments[1].Text);
    }

    [Fact]
    public void FenceJustOpenedWithNoNewlineYetIsCodeWithEmptyText()
    {
        var segments = ChatContentSplitter.Split("text before\n```");

        Assert.Equal(2, segments.Count);
        Assert.True(segments[1].IsCode);
        Assert.Equal(string.Empty, segments[1].Text);
    }

    [Fact]
    public void ScriptTagInPlainTextIsPreservedVerbatimNotStripped()
    {
        var segments = ChatContentSplitter.Split("here is a tag: <script>alert(1)</script> end");

        var segment = Assert.Single(segments);
        Assert.False(segment.IsCode);
        Assert.Contains("<script>alert(1)</script>", segment.Text);
    }

    [Fact]
    public void ScriptTagInsideCodeBlockIsPreservedVerbatim()
    {
        var segments = ChatContentSplitter.Split("```html\n<script>alert(1)</script>\n```");

        var code = Assert.Single(segments);
        Assert.True(code.IsCode);
        Assert.Equal("<script>alert(1)</script>", code.Text);
    }

    [Fact]
    public void EmptyOrNullContentReturnsNoSegments()
    {
        Assert.Empty(ChatContentSplitter.Split(""));
        Assert.Empty(ChatContentSplitter.Split(null));
    }

    [Fact]
    public void FenceWithoutLanguageTagHasNullLanguage()
    {
        var segments = ChatContentSplitter.Split("```\nplain code\n```");

        var code = Assert.Single(segments);
        Assert.True(code.IsCode);
        Assert.Null(code.Language);
        Assert.Equal("plain code", code.Text);
    }
}
