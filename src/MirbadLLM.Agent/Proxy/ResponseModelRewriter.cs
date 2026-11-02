using System.Text;
using System.Text.Json;

namespace MirbadLLM.Agent.Proxy;

/// <summary>
/// Puts the served name back into an MLX server's responses (docs/macos/implementation-plan.md, Phase 5). The proxy sends
/// every MLX request with <c>model</c> = <c>default_model</c> (the folder the server was started with), and
/// <c>mlx_lm.server</c> echoes that name in each response and stream chunk, so clients saw <c>"model": "default_model"</c>
/// instead of the name they asked for. Line by line (an SSE event is one line), so a stream is never held back: every
/// complete line is written as soon as it arrives, with the property replaced. Only the exact JSON property
/// <c>"model": "default_model"</c> (with or without the space Python's <c>json</c> writes) is touched; text inside a
/// completion cannot contain it unescaped.
/// </summary>
public sealed class ResponseModelRewriter
{
    private readonly byte[][] _patterns;
    private readonly byte[] _replacement;
    private readonly List<byte> _pending = [];

    public ResponseModelRewriter(string upstreamModel, string servedName)
    {
        ArgumentException.ThrowIfNullOrEmpty(upstreamModel);
        ArgumentException.ThrowIfNullOrEmpty(servedName);
        string value = JsonSerializer.Serialize(upstreamModel);
        _patterns = [Encoding.UTF8.GetBytes("\"model\": " + value), Encoding.UTF8.GetBytes("\"model\":" + value)];
        _replacement = Encoding.UTF8.GetBytes("\"model\": " + JsonSerializer.Serialize(servedName));
    }

    /// <summary>The bytes that can go out now: every complete line of what arrived so far, rewritten.</summary>
    public byte[] Process(ReadOnlySpan<byte> chunk)
    {
        int lastNewline = chunk.LastIndexOf((byte)'\n');
        if (lastNewline < 0)
        {
            _pending.AddRange(chunk);
            return [];
        }

        var ready = new List<byte>(_pending.Count + lastNewline + 1);
        ready.AddRange(_pending);
        ready.AddRange(chunk[..(lastNewline + 1)]);
        _pending.Clear();
        _pending.AddRange(chunk[(lastNewline + 1)..]);
        return Replace([.. ready]);
    }

    /// <summary>The rest at the end of the body (a JSON response is usually one line without a newline).</summary>
    public byte[] Flush()
    {
        byte[] rest = Replace([.. _pending]);
        _pending.Clear();
        return rest;
    }

    private byte[] Replace(byte[] text)
    {
        var output = new List<byte>(text.Length + 16);
        int i = 0;
        while (i < text.Length)
        {
            byte[]? hit = null;
            foreach (byte[] pattern in _patterns)
            {
                if (text.AsSpan(i).StartsWith(pattern))
                {
                    hit = pattern;
                    break;
                }
            }

            if (hit is null)
            {
                output.Add(text[i]);
                i++;
            }
            else
            {
                output.AddRange(_replacement);
                i += hit.Length;
            }
        }

        return [.. output];
    }
}
