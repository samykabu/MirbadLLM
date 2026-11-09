using System.Text;

namespace MirbadLLM.Core;

/// <summary>A built-in tag: its slug, the label the UI shows, and one line on what it means.</summary>
public sealed record ModelTagInfo(string Id, string Label, string Description);

/// <summary>
/// The tag vocabulary for <see cref="ModelDefinition.Tags"/>: the built-in catalog, normalization of custom tags
/// (lowercase slug <c>[a-z0-9-]</c>, 1–<see cref="MaxLength"/> characters, at most <see cref="MaxTags"/> per model,
/// no duplicates), the filter rule (a model matches when it has <em>all</em> selected tags) and the suggestions
/// worked out from a definition and its checkpoint's <c>config.json</c>. Pure functions, no I/O.
/// </summary>
public static class ModelTags
{
    public const int MaxTags = 12;
    public const int MaxLength = 24;

    public const string Chat = "chat";
    public const string Reasoning = "reasoning";
    public const string ToolCalling = "tool-calling";
    public const string Vision = "vision";
    public const string Multimodal = "multimodal";
    public const string Embedding = "embedding";
    public const string Reranker = "reranker";
    public const string Code = "code";
    public const string Speculative = "speculative";
    public const string Planner = "planner";

    public static readonly IReadOnlyList<ModelTagInfo> BuiltIn =
    [
        new(Chat, "Chat", "Generates text for chat completions."),
        new(Reasoning, "Reasoning", "Thinks before it answers; a reasoning parser splits the thinking out."),
        new(ToolCalling, "Tool calling", "Returns OpenAI tool calls through a tool-call parser."),
        new(Vision, "Vision", "Reads images as well as text."),
        new(Multimodal, "Multimodal", "Takes more than one kind of input (text plus images or audio)."),
        new(Embedding, "Embedding", "Turns text into vectors (/v1/embeddings), not a chat model."),
        new(Reranker, "Reranker", "Scores how well documents match a query (/v1/score, /v1/rerank)."),
        new(Code, "Code", "Tuned for writing and editing code."),
        new(Speculative, "Speculative", "Decodes speculatively (MTP, n-gram or a draft model) for faster output."),
        new(Planner, "Planner", "Plans or routes work, e.g. intent detection for agents."),
    ];

    /// <summary>The built-in label for a slug, or the slug itself for a custom tag.</summary>
    public static string Label(string tag) =>
        BuiltIn.FirstOrDefault(t => string.Equals(t.Id, tag, StringComparison.Ordinal))?.Label ?? tag;

    public static bool IsBuiltIn(string tag) => BuiltIn.Any(t => string.Equals(t.Id, tag, StringComparison.Ordinal));

    /// <summary>
    /// Lowercase slug of <paramref name="raw"/>: letters and digits kept, every run of anything else becomes one
    /// hyphen, no leading or trailing hyphen ("Tool Calling" → <c>tool-calling</c>). Null when nothing is left or
    /// the slug is longer than <see cref="MaxLength"/>.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var slug = new StringBuilder(raw.Length);
        bool pendingHyphen = false;
        foreach (char c in raw.Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingHyphen && slug.Length > 0)
                {
                    slug.Append('-');
                }

                pendingHyphen = false;
                slug.Append(c);
            }
            else
            {
                pendingHyphen = true;
            }
        }

        return slug.Length is 0 or > MaxLength ? null : slug.ToString();
    }

    /// <summary>
    /// Normalizes and de-duplicates <paramref name="tags"/>, keeping first-seen order.
    /// </summary>
    /// <exception cref="ArgumentException">A tag is empty or longer than <see cref="MaxLength"/> once normalized, or there are more than <see cref="MaxTags"/>.</exception>
    public static IReadOnlyList<string> NormalizeAll(IEnumerable<string>? tags)
    {
        var result = new List<string>();
        foreach (string raw in tags ?? [])
        {
            string tag = Normalize(raw)
                ?? throw new ArgumentException(
                    $"Tag '{raw}' is invalid; tags are 1–{MaxLength} lowercase letters, digits or hyphens.", nameof(tags));
            if (!result.Contains(tag, StringComparer.Ordinal))
            {
                result.Add(tag);
            }
        }

        if (result.Count > MaxTags)
        {
            throw new ArgumentException($"A model can have at most {MaxTags} tags ({result.Count} given).", nameof(tags));
        }

        return result;
    }

    /// <summary>True when <paramref name="model"/> has every tag in <paramref name="required"/> (an empty filter matches everything).</summary>
    public static bool HasAll(ModelDefinition model, IEnumerable<string> required)
    {
        ArgumentNullException.ThrowIfNull(model);
        var tags = model.Tags ?? [];
        return required.All(tag => tags.Contains(tag, StringComparer.Ordinal));
    }

    /// <summary>Every tag in use across <paramref name="models"/> with how many models carry it, most used first, then by label.</summary>
    public static IReadOnlyList<(string Tag, int Count)> Counts(IEnumerable<ModelDefinition> models) =>
        models
            .SelectMany(m => (m.Tags ?? []).Distinct(StringComparer.Ordinal))
            .GroupBy(t => t, StringComparer.Ordinal)
            .Select(g => (Tag: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => Label(x.Tag), StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The built-in tags a model most likely deserves, in catalog order, from its definition and (when known) its
    /// checkpoint: <paramref name="architecture"/> is <c>config.json</c>'s first <c>architectures</c> entry and
    /// <paramref name="hasVisionConfig"/> whether it has a <c>vision_config</c>. Suggestions only — the editor shows
    /// them and never applies them on its own, except to prefill a brand-new model whose tags were not touched.
    /// </summary>
    private static bool HasFlag(IReadOnlyList<string> args, string flag) => args.Any(a => a.TrimStart().StartsWith(flag, StringComparison.Ordinal));

    public static IReadOnlyList<string> Suggest(ModelDefinition model, string? architecture = null, bool hasVisionConfig = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        // A container runtime keeps its flags in its own list, and the editor's lines can hold a flag with its value.
        IReadOnlyList<string> args = [.. model.ExtraArgs ?? [], .. model.Container?.ExtraArgs ?? []];
        string names = $"{model.ServedName} {model.ModelPath}";

        bool reranker = HasArg(args, "--convert", "classify") || HasArg(args, "--task", "score") ||
            Contains(architecture, "rerank") || Contains(names, "rerank");
        bool embedding = !reranker && (HasArg(args, "--convert", "embed") || HasArg(args, "--runner", "pooling"));
        bool pooling = reranker || embedding || HasArg(args, "--runner", "pooling");

        var suggested = new HashSet<string>(StringComparer.Ordinal);
        if (!pooling)
        {
            suggested.Add(Chat);
            if (!string.IsNullOrWhiteSpace(model.ToolCallParser) || HasFlag(args, "--tool-call-parser"))
            {
                suggested.Add(ToolCalling);
            }

            if (!string.IsNullOrWhiteSpace(model.ReasoningParser) || HasFlag(args, "--reasoning-parser"))
            {
                suggested.Add(Reasoning);
            }
        }

        if (embedding)
        {
            suggested.Add(Embedding);
        }

        if (reranker)
        {
            suggested.Add(Reranker);
        }

        if (model.Speculative is not null || HasFlag(args, "--speculative-config") || HasFlag(args, "--speculative-algorithm"))
        {
            suggested.Add(Speculative);
        }

        // "VL" is matched case-sensitively (Qwen2_5_VLForConditionalGeneration) so it can't hit an ordinary word.
        if (hasVisionConfig || (architecture?.Contains("VL", StringComparison.Ordinal) ?? false))
        {
            suggested.Add(Vision);
            suggested.Add(Multimodal);
        }

        if (Contains(names, "coder"))
        {
            suggested.Add(Code);
        }

        if (Contains(names, "intent") || Contains(names, "planner"))
        {
            suggested.Add(Planner);
        }

        return BuiltIn.Select(t => t.Id).Where(suggested.Contains).ToList();
    }

    private static bool Contains(string? text, string value) =>
        text is not null && text.Contains(value, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the raw vLLM args carry <paramref name="name"/> with <paramref name="value"/>, written as two
    /// entries, as <c>--name=value</c> or as one line <c>--name value</c> (the editor keeps one line per entry).
    /// </summary>
    internal static bool HasArg(IReadOnlyList<string> args, string name, string value)
    {
        for (int i = 0; i < args.Count; i++)
        {
            string[] parts = args[i].Split([' ', '='], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || !string.Equals(parts[0], name, StringComparison.Ordinal))
            {
                continue;
            }

            string? argValue = parts.Length == 2 ? parts[1] : i + 1 < args.Count ? args[i + 1].Trim() : null;
            if (string.Equals(argValue, value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
