using System.Text.Json;
using System.Text.Json.Nodes;

namespace MirbadLLM.Core;

/// <summary>
/// "&lt;served name&gt;-nothink": the same running model with thinking switched off. The router sends such a request to the
/// model under its own name with <c>chat_template_kwargs.enable_thinking = false</c>, so a client that can't add body
/// fields (GitHub Copilot, Cline) switches thinking off by picking a model name. A real model with that name wins.
/// </summary>
public static class NoThinking
{
    public const string Suffix = "-nothink";

    public static string NameFor(string servedName) => servedName + Suffix;

    /// <summary>The served name a "-nothink" name stands for; null for any other name.</summary>
    public static string? BaseName(string requested) =>
        requested.Length > Suffix.Length && requested.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) ? requested[..^Suffix.Length] : null;

    /// <summary>Listed for chat models tagged Reasoning: their thinking is what the name switches off.</summary>
    public static bool Offered(ModelDefinition model) =>
        ModelCapabilities.IsChat(model) && model.Tags.Contains(ModelTags.Reasoning, StringComparer.Ordinal);

    /// <summary>
    /// The request body sent on: <c>model</c> set to <paramref name="baseName"/> and, for a chat completion, thinking
    /// switched off in <c>chat_template_kwargs</c>, keeping any other template switch the client sent.
    /// </summary>
    /// <param name="body">A JSON object (the router has already read its <c>model</c>).</param>
    public static byte[] Rewrite(ReadOnlySpan<byte> body, string baseName, bool chat)
    {
        var root = JsonNode.Parse(body)!.AsObject();
        root["model"] = baseName;
        if (chat)
        {
            if (root["chat_template_kwargs"] is JsonObject kwargs)
            {
                kwargs["enable_thinking"] = false;
            }
            else
            {
                root["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };
            }
        }

        return JsonSerializer.SerializeToUtf8Bytes(root);
    }
}
