namespace MirbadLLM.Router;

/// <summary>
/// Drops <c>"reasoning_content":null</c> from a streamed answer. SGLang puts that field on every answer chunk, where vLLM
/// and OpenAI leave it out, and GitHub Copilot appends every reasoning field that isn't <c>undefined</c> to the reasoning
/// it keeps, so each chunk added the text "null". Works on whole lines: a line split across reads waits for its end.
/// The field can't match inside a JSON string, where its quotes would be escaped.
/// </summary>
public sealed class SseReasoningNullFilter
{
    private const int MaxPendingBytes = 1024 * 1024;
    private static readonly byte[] Field = "\"reasoning_content\":null"u8.ToArray();

    private byte[] _pending = new byte[4096];
    private int _count;

    /// <summary>The complete lines of what arrived so far, cleaned; a trailing partial line is held back.</summary>
    public byte[] Process(ReadOnlySpan<byte> input)
    {
        if (_count + input.Length > _pending.Length)
        {
            Array.Resize(ref _pending, Math.Max(_pending.Length * 2, _count + input.Length));
        }

        input.CopyTo(_pending.AsSpan(_count));
        _count += input.Length;

        int end = _pending.AsSpan(0, _count).LastIndexOf((byte)'\n') + 1;
        if (end == 0 && _count < MaxPendingBytes)
        {
            return [];
        }

        // shortcut: a line longer than MaxPendingBytes goes out uncleaned, so a stream without newlines never stalls.
        int take = end == 0 ? _count : end;
        byte[] ready = Clean(_pending.AsSpan(0, take));
        _pending.AsSpan(take, _count - take).CopyTo(_pending);
        _count -= take;
        return ready;
    }

    /// <summary>Whatever is left at the end of the stream, cleaned.</summary>
    public byte[] Flush()
    {
        byte[] rest = Clean(_pending.AsSpan(0, _count));
        _count = 0;
        return rest;
    }

    public static byte[] Clean(ReadOnlySpan<byte> text)
    {
        var output = new List<byte>(text.Length);
        while (true)
        {
            int at = text.IndexOf(Field);
            if (at < 0)
            {
                output.AddRange(text);
                return [.. output];
            }

            int start = at;
            int after = at + Field.Length;
            if (at > 0 && text[at - 1] == (byte)',')
            {
                start--; // {"a":1,"reasoning_content":null}
            }
            else if (after < text.Length && text[after] == (byte)',')
            {
                after++; // {"reasoning_content":null,"a":1}
            }

            output.AddRange(text[..start]);
            text = text[after..];
        }
    }
}
