using MirbadLLM.Core;

namespace MirbadLLM.NodeRuntime;

/// <summary>
/// Containers created before the rename carry <c>spark-serve.*</c> labels. Docker reads show them under their
/// <c>mirbadllm.*</c> names too, and label filters also match the legacy names, so running models, download helpers and
/// host-service rules keep recognising them; new containers get only the new labels.
/// </summary>
public static class LegacyLabels
{
    private const string Prefix = "mirbadllm.";

    /// <summary>The labels plus a <c>mirbadllm.*</c> twin of each <c>spark-serve.*</c> one (a new label wins).</summary>
    public static IReadOnlyDictionary<string, string> Normalize(IReadOnlyDictionary<string, string>? labels)
    {
        if (labels is null || !labels.Keys.Any(IsLegacy))
        {
            return labels ?? new Dictionary<string, string>();
        }

        var result = new Dictionary<string, string>(labels, StringComparer.Ordinal);
        foreach (var (key, value) in labels)
        {
            if (IsLegacy(key))
            {
                result.TryAdd(Prefix + key[LegacyNames.LabelPrefix.Length..], value);
            }
        }

        return result;
    }

    /// <summary>The legacy form of a <c>label</c> filter (<c>mirbadllm.model</c>, <c>mirbadllm.model=x</c>); null for any other filter.</summary>
    public static string? ToLegacyFilter(string filter) =>
        filter.StartsWith(Prefix, StringComparison.Ordinal) ? LegacyNames.LabelPrefix + filter[Prefix.Length..] : null;

    private static bool IsLegacy(string key) => key.StartsWith(LegacyNames.LabelPrefix, StringComparison.Ordinal);
}
