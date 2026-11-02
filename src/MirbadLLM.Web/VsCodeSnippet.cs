using System.Globalization;
using System.Text;

namespace MirbadLLM.Web;

/// <summary>Builds the VS Code (GitHub Copilot Chat "OpenAI Compatible") models entry shown on the Connect page.</summary>
public static class VsCodeSnippet
{
    /// <summary>Output gets 16,384 tokens (a quarter of a short context); input gets the rest, so the two fit the context.</summary>
    public static (int Input, int Output) TokenLimits(int maxModelLen)
    {
        int output = Math.Min(16_384, maxModelLen / 4);
        return (maxModelLen - output, output);
    }

    public static string Build(string baseUrl, IReadOnlyList<(string ServedName, int MaxModelLen)> models, bool apiKeyRequired) =>
        Build(baseUrl, models.Select(m => (m.ServedName, m.MaxModelLen, (string?)null)).ToList(), apiKeyRequired);

    /// <summary>The entry for <paramref name="places"/>: each model's display name says where it runs ("node · runtime").</summary>
    public static string Build(string baseUrl, IReadOnlyList<ModelPlace> places, bool apiKeyRequired) =>
        Build(baseUrl, places.Select(p => (p.ServedName, p.MaxModelLen, (string?)p.Where)).ToList(), apiKeyRequired);

    /// <summary>One entry per model; <c>Where</c> (e.g. "dev-b · llama.cpp") is appended to the display name when known.</summary>
    public static string Build(string baseUrl, IReadOnlyList<(string ServedName, int MaxModelLen, string? Where)> models, bool apiKeyRequired)
    {
        var list = models.Count > 0 ? models : [("<served-name>", 32_768, null)];
        string apiKey = apiKeyRequired ? "<paste your ssk_ key when VS Code asks>" : "not-required";

        var sb = new StringBuilder();
        sb.Append("[\n    {\n        \"name\": \"MirbadLLM\",\n        \"vendor\": \"customoai\",\n");
        sb.Append(CultureInfo.InvariantCulture, $"        \"apiKey\": \"{apiKey}\",\n        \"models\": [\n");
        for (int i = 0; i < list.Count; i++)
        {
            var (name, maxLen, where) = list[i];
            var (input, output) = TokenLimits(maxLen);
            sb.Append("            {\n");
            sb.Append(CultureInfo.InvariantCulture, $"                \"id\": \"{name}\",\n");
            string label = where is null ? name : $"{name} ({where})";
            sb.Append(CultureInfo.InvariantCulture, $"                \"name\": \"{label}\",\n");
            sb.Append(CultureInfo.InvariantCulture, $"                \"url\": \"{baseUrl}/chat/completions\",\n");
            sb.Append("                \"toolCalling\": true,\n                \"vision\": false,\n");
            sb.Append(CultureInfo.InvariantCulture, $"                \"maxInputTokens\": {input},\n");
            sb.Append(CultureInfo.InvariantCulture, $"                \"maxOutputTokens\": {output}\n");
            sb.Append(i < list.Count - 1 ? "            },\n" : "            }\n");
        }

        sb.Append("        ]\n    }\n]");
        return sb.ToString();
    }
}
