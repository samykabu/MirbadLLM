using System.Text.RegularExpressions;

namespace MirbadLLM.Core;

/// <summary>
/// The allow-pattern globs a Hugging Face snapshot download uses (<c>*</c> and <c>?</c>, case-insensitive), shared by the
/// node-side downloader and the master's restore planner. Lives in Core (MN-090) so the master does not need
/// <c>InternalsVisibleTo</c> into <c>MirbadLLM.NodeRuntime</c>, and keeps working after MN-040 removes the downloader
/// from the master's composition.
/// </summary>
public static class HuggingFacePatterns
{
    /// <summary>Default files fetched when a request does not specify its own allow-patterns.</summary>
    public static IReadOnlyList<string> DefaultAllowPatterns { get; } =
        ["*.json", "*.safetensors", "*.jinja", "*.txt", "tokenizer*", "*.model"];

    /// <summary>True when <paramref name="fileName"/> matches at least one glob in <paramref name="patterns"/>.</summary>
    public static bool MatchesAny(string fileName, IReadOnlyList<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(patterns);
        return patterns.Any(pattern => GlobRegex(pattern).IsMatch(fileName));
    }

    private static Regex GlobRegex(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
