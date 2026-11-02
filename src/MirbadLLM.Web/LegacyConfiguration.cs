using MirbadLLM.Core;

namespace MirbadLLM.Web;

/// <summary>
/// Reads configuration written before the rename: every <c>SparkServe:*</c> key (<c>SparkServe__*</c> in the environment)
/// counts as its <c>MirbadLLM:*</c> twin at the precedence of the source that set it, so a legacy environment variable
/// still beats appsettings.json. Within one source the MirbadLLM key wins over its legacy twin.
/// </summary>
public static partial class LegacyConfiguration
{
    /// <summary>Adds the resolved legacy values as a last source; returns every legacy key found (for a startup warning).</summary>
    public static IReadOnlyList<string> Apply(IConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder is not IConfigurationRoot root)
        {
            return [];
        }

        var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IConfigurationProvider provider in root.Providers)
        {
            // A later source's MirbadLLM key overrides an earlier source's legacy one.
            foreach (string key in LeafKeys(provider, MirbadLLMOptions.SectionName))
            {
                resolved.Remove(key);
            }

            foreach (string legacy in LeafKeys(provider, LegacyNames.ConfigSection))
            {
                found.Add(legacy);
                string key = MirbadLLMOptions.SectionName + legacy[LegacyNames.ConfigSection.Length..];
                if (!provider.TryGet(key, out _) && provider.TryGet(legacy, out string? value))
                {
                    resolved[key] = value;
                }
            }
        }

        if (resolved.Count > 0)
        {
            builder.AddInMemoryCollection(resolved);
        }

        return [.. found];
    }

    /// <summary>Logs a warning naming the legacy keys in use, if any.</summary>
    public static void Warn(ILogger logger, IReadOnlyList<string> legacyKeys)
    {
        if (legacyKeys.Count > 0)
        {
            LogLegacyKeys(logger, string.Join(", ", legacyKeys));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Configuration uses pre-rename keys {Keys}; rename them to MirbadLLM:* (MirbadLLM__* in the environment). They are read as a fallback, and a MirbadLLM key set in the same place wins.")]
    private static partial void LogLegacyKeys(ILogger logger, string keys);

    private static IEnumerable<string> LeafKeys(IConfigurationProvider provider, string path)
    {
        foreach (string child in provider.GetChildKeys([], path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string key = path + ConfigurationPath.KeyDelimiter + child;
            if (provider.TryGet(key, out _))
            {
                yield return key;
            }

            foreach (string leaf in LeafKeys(provider, key))
            {
                yield return leaf;
            }
        }
    }
}
