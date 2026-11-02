using System.Runtime.CompilerServices;
using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Web.Fakes;

/// <summary>Hand-written fake <see cref="IChatClient"/>: yields a preset sequence of chunks (or throws a
/// preset exception before yielding anything), and records the last request it was asked to stream.</summary>
public sealed class FakeChatClient : IChatClient
{
    public List<ChatChunk> ChunksToYield { get; } = [];

    public Exception? ThrowBeforeYielding { get; set; }

    public ChatRequest? LastRequest { get; private set; }

    public async IAsyncEnumerable<ChatChunk> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        LastRequest = request;
        if (ThrowBeforeYielding is not null)
        {
            throw ThrowBeforeYielding;
        }

        foreach (var chunk in ChunksToYield)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return chunk;
        }
    }
}
