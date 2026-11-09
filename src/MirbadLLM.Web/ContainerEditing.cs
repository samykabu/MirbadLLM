namespace MirbadLLM.Web;

/// <summary>The <c>NAME=value</c> text fields of the container section of the model editor.</summary>
public static class ContainerEditing
{
    /// <summary>The variables in <paramref name="text"/>, or null when there are none. A line that isn't <c>NAME=value</c> is skipped; see <see cref="FirstBadLine"/>.</summary>
    public static IReadOnlyDictionary<string, string>? Parse(string? text)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in Lines(text))
        {
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                variables[line[..equals].Trim()] = line[(equals + 1)..].Trim();
            }
        }

        return variables.Count == 0 ? null : variables;
    }

    /// <summary>The first line that isn't <c>NAME=value</c>, or null.</summary>
    public static string? FirstBadLine(string? text) => Lines(text).FirstOrDefault(line => line.IndexOf('=', StringComparison.Ordinal) <= 0);

    public static string Format(IReadOnlyDictionary<string, string>? variables) =>
        variables is null ? "" : string.Join('\n', variables.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={v.Value}"));

    private static IEnumerable<string> Lines(string? text) =>
        (text ?? "").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0);
}
