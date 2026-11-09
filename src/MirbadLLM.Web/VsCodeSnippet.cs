using System.Globalization;
using System.Text;
using MirbadLLM.Core;

namespace MirbadLLM.Web;

/// <summary>
/// Builds the VS Code (GitHub Copilot Chat "Custom Endpoint", <c>chatLanguageModels.json</c>) entry shown on the Connect
/// page. A model tagged Reasoning gets a second entry, "-nothink", the same model with thinking off (<see cref="NoThinking"/>).
/// </summary>
public static class VsCodeSnippet
{
    /// <summary>Output gets 16,384 tokens (a quarter of a short context); input gets the rest, so the two fit the context.</summary>
    public static (int Input, int Output) TokenLimits(int maxModelLen)
    {
        int output = Math.Min(16_384, maxModelLen / 4);
        return (maxModelLen - output, output);
    }

    /// <summary>One entry per model, named with where it runs ("node · runtime").</summary>
    public static string Build(string baseUrl, IReadOnlyList<ModelPlace> places, bool apiKeyRequired)
    {
        var entries = new List<(string Id, string Name, int MaxLen, bool Vision, bool Thinking)>();
        foreach (var place in places)
        {
            entries.Add((place.ServedName, $"{place.ServedName} ({place.Where})", place.MaxModelLen, place.AcceptsImages, place.Thinks));
            if (place.Thinks)
            {
                entries.Add((NoThinking.NameFor(place.ServedName), $"{place.ServedName}, no thinking ({place.Where})", place.MaxModelLen, place.AcceptsImages, false));
            }
        }

        if (entries.Count == 0)
        {
            entries.Add(("<served-name>", "<served-name>", ModelPlaces.DefaultContext, false, false));
        }

        string apiKey = apiKeyRequired ? "${input:mirbadApiKey}" : "not-required";
        var sb = new StringBuilder();
        sb.Append("[\n    {\n        \"name\": \"MirbadLLM\",\n        \"vendor\": \"customendpoint\",\n");
        sb.Append(CultureInfo.InvariantCulture, $"        \"apiKey\": \"{apiKey}\",\n        \"apiType\": \"chat-completions\",\n        \"models\": [\n");
        for (int i = 0; i < entries.Count; i++)
        {
            var (id, name, maxLen, vision, thinking) = entries[i];
            var (input, output) = TokenLimits(maxLen);
            sb.Append("            {\n");
            sb.Append(CultureInfo.InvariantCulture, $"                \"id\": \"{id}\",\n");
            sb.Append(CultureInfo.InvariantCulture, $"                \"name\": \"{name}\",\n");
            sb.Append(CultureInfo.InvariantCulture, $"                \"url\": \"{baseUrl}/chat/completions\",\n");
            sb.Append(CultureInfo.InvariantCulture, $"                \"toolCalling\": true,\n                \"vision\": {Bool(vision)},\n                \"thinking\": {Bool(thinking)},\n");
            sb.Append(CultureInfo.InvariantCulture, $"                \"maxInputTokens\": {input},\n");
            sb.Append(CultureInfo.InvariantCulture, $"                \"maxOutputTokens\": {output}\n");
            sb.Append(i < entries.Count - 1 ? "            },\n" : "            }\n");
        }

        sb.Append("        ]\n    }\n]");
        return sb.ToString();
    }

    private static string Bool(bool value) => value ? "true" : "false";
}
