using System.Text;

namespace MirbadLLM.Orchestration.Agents;

/// <summary>One Server-Sent Events message: the <c>event:</c> name (null = default) and its <c>data:</c> lines.</summary>
internal sealed record SseMessage(string? Event, IReadOnlyList<string> DataLines)
{
    /// <summary>The data lines joined with <c>\n</c> (the SSE rule).</summary>
    public string Data => string.Join('\n', DataLines);
}

/// <summary>
/// Minimal SSE reader for agent streams (<c>/events</c>, <c>/runtimes/{id}/logs</c>, design §4.3, §4.7): <c>event:</c>
/// and <c>data:</c> fields, comments (<c>:</c>) and other fields ignored, a blank line dispatches. No reconnect logic;
/// the caller owns it.
/// </summary>
internal sealed class SseReader : IDisposable
{
    private readonly StreamReader _reader;

    public SseReader(Stream stream)
    {
        _reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 16 * 1024, leaveOpen: true);
    }

    /// <summary>The next message, or null at the end of the stream.</summary>
    public async Task<SseMessage?> ReadAsync(CancellationToken ct)
    {
        string? eventName = null;
        List<string> data = [];
        bool any = false;
        while (true)
        {
            string? line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                return any ? new SseMessage(eventName, data) : null;
            }

            if (line.Length == 0)
            {
                if (any)
                {
                    return new SseMessage(eventName, data);
                }

                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            string field = colon < 0 ? line : line[..colon];
            string value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            switch (field)
            {
                case "event":
                    eventName = value;
                    any = true;
                    break;
                case "data":
                    data.Add(value);
                    any = true;
                    break;
                default:
                    break;
            }
        }
    }

    public void Dispose() => _reader.Dispose();
}
