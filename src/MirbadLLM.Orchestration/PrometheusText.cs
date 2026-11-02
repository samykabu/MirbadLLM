using System.Globalization;
using System.Text;

namespace MirbadLLM.Orchestration;

/// <summary>One labeled sample line from a Prometheus text-exposition scrape.</summary>
internal readonly record struct PrometheusSample(string Name, IReadOnlyDictionary<string, string> Labels, double Value);

/// <summary>
/// A histogram family (<c>{base}_bucket{le=...}</c>, <c>{base}_sum</c>, <c>{base}_count</c>) collapsed to
/// cumulative buckets ordered ascending by upper bound (<see cref="double.PositiveInfinity"/> last).
/// </summary>
internal sealed record PrometheusHistogram(IReadOnlyList<(double UpperBound, double CumulativeCount)> Buckets, double Sum, double Count);

/// <summary>Parses Prometheus text exposition format and aggregates samples for KPI computation.</summary>
internal static class PrometheusText
{
    private static readonly IReadOnlyDictionary<string, string> EmptyLabels = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Parses every non-comment, non-NaN sample line, keeping its labels.</summary>
    public static List<PrometheusSample> Parse(string text)
    {
        var samples = new List<PrometheusSample>();

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string name;
            IReadOnlyDictionary<string, string> labels;
            string rest;

            int braceIndex = line.IndexOf('{');
            if (braceIndex >= 0)
            {
                int closeIndex = line.IndexOf('}', braceIndex);
                if (closeIndex < 0)
                {
                    continue;
                }

                name = line[..braceIndex];
                labels = ParseLabels(line[(braceIndex + 1)..closeIndex]);
                rest = line[(closeIndex + 1)..].Trim();
            }
            else
            {
                int spaceIndex = line.IndexOf(' ');
                if (spaceIndex < 0)
                {
                    continue;
                }

                name = line[..spaceIndex];
                labels = EmptyLabels;
                rest = line[(spaceIndex + 1)..].Trim();
            }

            string[] valueParts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (valueParts.Length == 0 || string.Equals(valueParts[0], "NaN", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!double.TryParse(valueParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || double.IsNaN(value))
            {
                continue;
            }

            samples.Add(new PrometheusSample(name, labels, value));
        }

        return samples;
    }

    /// <summary>Sums every sample with the given metric name, across all label sets.</summary>
    public static double Sum(IReadOnlyList<PrometheusSample> samples, string name)
    {
        double total = 0;
        foreach (var sample in samples)
        {
            if (string.Equals(sample.Name, name, StringComparison.Ordinal))
            {
                total += sample.Value;
            }
        }

        return total;
    }

    /// <summary>True when at least one sample with the given metric name is present (metric family exists in this scrape).</summary>
    public static bool HasMetric(IReadOnlyList<PrometheusSample> samples, string name)
    {
        foreach (var sample in samples)
        {
            if (string.Equals(sample.Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Sums a metric's values grouped by one label's value (e.g. <c>finished_reason</c>), across the other labels.</summary>
    public static Dictionary<string, double> SumByLabel(IReadOnlyList<PrometheusSample> samples, string name, string labelName)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            if (!string.Equals(sample.Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            if (!sample.Labels.TryGetValue(labelName, out string? labelValue))
            {
                continue;
            }

            result[labelValue] = result.GetValueOrDefault(labelValue) + sample.Value;
        }

        return result;
    }

    /// <summary>
    /// Collapses a histogram family (<paramref name="baseName"/> + <c>_bucket</c>/<c>_sum</c>/<c>_count</c>)
    /// into cumulative buckets, summing across all label sets per <c>le</c>. Null when none of the three
    /// sample kinds are present.
    /// </summary>
    public static PrometheusHistogram? Histogram(IReadOnlyList<PrometheusSample> samples, string baseName)
    {
        string bucketName = baseName + "_bucket";
        string sumName = baseName + "_sum";
        string countName = baseName + "_count";

        var buckets = new Dictionary<double, double>();
        double sum = 0;
        double count = 0;
        bool any = false;

        foreach (var sample in samples)
        {
            if (string.Equals(sample.Name, bucketName, StringComparison.Ordinal))
            {
                if (!sample.Labels.TryGetValue("le", out string? leText))
                {
                    continue;
                }

                double le = ParseLe(leText);
                buckets[le] = buckets.GetValueOrDefault(le) + sample.Value;
                any = true;
            }
            else if (string.Equals(sample.Name, sumName, StringComparison.Ordinal))
            {
                sum += sample.Value;
                any = true;
            }
            else if (string.Equals(sample.Name, countName, StringComparison.Ordinal))
            {
                count += sample.Value;
                any = true;
            }
        }

        if (!any || buckets.Count == 0)
        {
            return null;
        }

        var ordered = buckets
            .OrderBy(kv => kv.Key)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();

        return new PrometheusHistogram(ordered, sum, count);
    }

    private static double ParseLe(string text)
        => text is "+Inf" or "+inf" or "Inf" ? double.PositiveInfinity : double.Parse(text, CultureInfo.InvariantCulture);

    private static Dictionary<string, string> ParseLabels(string text)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        int i = 0;
        while (i < text.Length)
        {
            int eq = text.IndexOf('=', i);
            if (eq < 0)
            {
                break;
            }

            string key = text[i..eq].Trim();
            i = eq + 1;
            if (i >= text.Length || text[i] != '"')
            {
                break;
            }

            i++; // skip opening quote
            var value = new StringBuilder();
            while (i < text.Length && text[i] != '"')
            {
                if (text[i] == '\\' && i + 1 < text.Length)
                {
                    i++;
                    value.Append(text[i] switch { 'n' => '\n', '"' => '"', '\\' => '\\', var c => c });
                }
                else
                {
                    value.Append(text[i]);
                }

                i++;
            }

            labels[key] = value.ToString();
            i++; // skip closing quote

            while (i < text.Length && (text[i] == ',' || text[i] == ' '))
            {
                i++;
            }
        }

        return labels;
    }
}
