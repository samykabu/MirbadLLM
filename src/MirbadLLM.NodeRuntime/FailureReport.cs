using System.Text.RegularExpressions;

namespace MirbadLLM.NodeRuntime;

/// <summary>
/// Turns the tail of a failed vLLM container's log into a report whose first line is the root cause
/// (<c>Cause: ...</c>), followed by the last lines of output for context.
/// </summary>
public static partial class FailureReport
{
    public const string CausePrefix = "Cause: ";
    private const int ContextLines = 40;

    public static string? Build(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return null;
        }

        string? cause = FindRootCause(lines);
        var tail = lines.Skip(Math.Max(0, lines.Count - ContextLines));
        string context = string.Join('\n', tail);
        return cause is null ? context : $"{CausePrefix}{cause}\n\n{context}";
    }

    /// <summary>
    /// The last exception line that is not the generic wrapper vLLM's API server raises when its engine dies
    /// ("Engine core initialization failed"), with process and logger prefixes removed.
    /// </summary>
    public static string? FindRootCause(IReadOnlyList<string> lines)
    {
        string? wrapper = null;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            string line = StripPrefixes(lines[i]);
            var match = ExceptionLine().Match(line);
            if (!match.Success || IsNoise(line))
            {
                continue;
            }

            string candidate = line[match.Index..].Trim();
            if (candidate.Contains("Engine core initialization failed", StringComparison.Ordinal))
            {
                wrapper ??= candidate;
                continue;
            }

            return candidate;
        }

        return wrapper;
    }

    private static string StripPrefixes(string line)
    {
        string s = ProcessPrefix().Replace(line, "");
        return LoggerPrefix().Replace(s, "").Trim();
    }

    // Transformers prints "[ERROR] `x` is part of ... but not documented" for harmless docstring checks.
    private static bool IsNoise(string line) =>
        line.Contains("but not documented", StringComparison.Ordinal);

    [GeneratedRegex(@"\b[A-Za-z_.]*(Error|Exception)\b:|CUDA out of memory|out of memory", RegexOptions.CultureInvariant)]
    private static partial Regex ExceptionLine();

    [GeneratedRegex(@"^\s*\(\w+ pid=\d+\)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex ProcessPrefix();

    [GeneratedRegex(@"^(ERROR|WARNING|INFO|CRITICAL)\s+\d\d-\d\d \d\d:\d\d:\d\d\s+\[[^\]]*\]\s*", RegexOptions.CultureInvariant)]
    private static partial Regex LoggerPrefix();
}
