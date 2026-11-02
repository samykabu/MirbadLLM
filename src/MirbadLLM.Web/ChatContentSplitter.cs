namespace MirbadLLM.Web;

/// <summary>
/// Splits a chat message's raw text into plain-text and fenced-code segments so the Chat page can render
/// each safely — plain text with <c>white-space: pre-wrap</c>, code in <c>&lt;pre&gt;&lt;code&gt;</c> — by
/// Razor-encoding the segment text on output. Never produces markup itself: callers must not wrap the
/// result in <c>MarkupString</c>.
/// </summary>
public static class ChatContentSplitter
{
    public readonly record struct Segment(bool IsCode, string Text, string? Language);

    /// <summary>Splits on triple-backtick fences (<c>```lang\ncode\n```</c>). A fence still open at the end
    /// of the text (the model is mid-stream) is rendered as code too, so the block doesn't flash back to
    /// plain text while streaming.</summary>
    public static IReadOnlyList<Segment> Split(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        var segments = new List<Segment>();
        var index = 0;
        while (index < content.Length)
        {
            var fenceStart = content.IndexOf("```", index, StringComparison.Ordinal);
            if (fenceStart < 0)
            {
                AddText(segments, content[index..]);
                break;
            }

            if (fenceStart > index)
            {
                AddText(segments, content[index..fenceStart]);
            }

            var afterFence = fenceStart + 3;
            var lineEnd = content.IndexOf('\n', afterFence);
            if (lineEnd < 0)
            {
                // Fence just opened; the language tag (or code) hasn't reached a newline yet.
                var language = content[afterFence..].Trim();
                segments.Add(new Segment(true, string.Empty, language.Length == 0 ? null : language));
                break;
            }

            var languageTag = content[afterFence..lineEnd].Trim();
            var codeStart = lineEnd + 1;
            var fenceEnd = content.IndexOf("```", codeStart, StringComparison.Ordinal);
            if (fenceEnd < 0)
            {
                segments.Add(new Segment(true, content[codeStart..], languageTag.Length == 0 ? null : languageTag));
                break;
            }

            var code = content[codeStart..fenceEnd];
            if (code.EndsWith('\n'))
            {
                code = code[..^1];
            }

            segments.Add(new Segment(true, code, languageTag.Length == 0 ? null : languageTag));
            index = fenceEnd + 3;
        }

        return segments;
    }

    private static void AddText(List<Segment> segments, string text)
    {
        if (text.Length > 0)
        {
            segments.Add(new Segment(false, text, null));
        }
    }
}
