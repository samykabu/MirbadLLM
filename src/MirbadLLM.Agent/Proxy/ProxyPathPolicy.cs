using System.Text;

namespace MirbadLLM.Agent.Proxy;

/// <summary>
/// The proxy path allow-list (design §7.2) and the safe rebuild of the upstream path. A path is accepted only when every
/// segment is a plain name (no empty, <c>.</c> or <c>..</c> segment, no <c>%</c>, <c>\</c> or control character), so
/// what is checked here is exactly what the model receives: no dot-segment or double-decoding trick can turn an
/// allowed path into <c>/dev/…</c> on the upstream.
/// </summary>
internal sealed class ProxyPathPolicy
{
    private readonly HashSet<string> _exact = new(StringComparer.Ordinal);
    private readonly List<string> _prefixes = [];

    public ProxyPathPolicy(IEnumerable<string> allowedPaths)
    {
        ArgumentNullException.ThrowIfNull(allowedPaths);
        foreach (string raw in allowedPaths)
        {
            string entry = raw.Trim().Trim('/');
            if (entry.Length == 0)
            {
                continue;
            }

            if (entry.EndsWith("/*", StringComparison.Ordinal))
            {
                _prefixes.Add(entry[..^1]); // "v1/*" → "v1/"
            }
            else
            {
                _exact.Add(entry);
            }
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> (the <c>{**path}</c> route value, without a leading slash) is allowed;
    /// <paramref name="upstreamPath"/> is then the escaped relative path to request from the model.
    /// </summary>
    public bool TryGetUpstreamPath(string? path, out string upstreamPath)
    {
        upstreamPath = "";
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var builder = new StringBuilder(path.Length + 8);
        foreach (string segment in path.Split('/'))
        {
            if (!IsPlainSegment(segment))
            {
                return false;
            }

            if (builder.Length > 0)
            {
                builder.Append('/');
            }

            builder.Append(Uri.EscapeDataString(segment));
        }

        if (!IsAllowed(path))
        {
            return false;
        }

        upstreamPath = builder.ToString();
        return true;
    }

    private bool IsAllowed(string path)
    {
        if (_exact.Contains(path))
        {
            return true;
        }

        foreach (string prefix in _prefixes)
        {
            if (path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPlainSegment(string segment)
    {
        if (segment.Length == 0 || segment == "." || segment == "..")
        {
            return false;
        }

        foreach (char c in segment)
        {
            if (c is '%' or '\\' || char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }
}
