namespace MirbadLLM.Core;

public sealed record ChatMessage(string Role, string Content);

public sealed record ChatRequest(
    string ServedName,
    IReadOnlyList<ChatMessage> Messages,
    double Temperature = 0.7,
    int MaxTokens = 1024,
    bool EnableThinking = false);

/// <summary>One streamed piece of a reply. Exactly one of the text fields is set, except the final chunk, which carries <see cref="Usage"/>.</summary>
public sealed record ChatChunk(string? Content = null, string? Reasoning = null, ChatUsage? Usage = null, string? FinishReason = null);

public sealed record ChatUsage(int PromptTokens, int CompletionTokens);

/// <summary>Streams a chat completion from a Ready model through the router path (counts in router stats).</summary>
public interface IChatClient
{
    IAsyncEnumerable<ChatChunk> StreamAsync(ChatRequest request, CancellationToken ct = default);
}
