namespace MirbadLLM.Orchestration;

/// <summary>Delta and quantile math over cumulative Prometheus histograms (Prometheus <c>histogram_quantile</c> semantics).</summary>
internal static class HistogramMath
{
    /// <summary>
    /// Delta histogram over the window between two scrapes. Null when there is no previous scrape. A
    /// counter reset (container restarted; <paramref name="current"/>'s count is lower than
    /// <paramref name="previous"/>'s) is treated as "start over": the current cumulative histogram is
    /// returned as-is rather than producing a negative delta.
    /// </summary>
    public static PrometheusHistogram? Delta(PrometheusHistogram? previous, PrometheusHistogram current)
    {
        if (previous is null)
        {
            return null;
        }

        if (current.Count < previous.Count)
        {
            return current;
        }

        var previousByBound = new Dictionary<double, double>(previous.Buckets.Count);
        foreach (var (upperBound, cumulativeCount) in previous.Buckets)
        {
            previousByBound[upperBound] = cumulativeCount;
        }

        var buckets = new List<(double UpperBound, double CumulativeCount)>(current.Buckets.Count);
        foreach (var (upperBound, cumulativeCount) in current.Buckets)
        {
            double previousCumulative = previousByBound.GetValueOrDefault(upperBound);
            buckets.Add((upperBound, Math.Max(0, cumulativeCount - previousCumulative)));
        }

        return new PrometheusHistogram(buckets, Math.Max(0, current.Sum - previous.Sum), current.Count - previous.Count);
    }

    /// <summary>
    /// Estimates the <paramref name="quantile"/> (0..1) via linear interpolation within the bucket that
    /// contains it, matching Prometheus' <c>histogram_quantile</c>. Null when the histogram has no
    /// observations. Falls back to the previous finite bucket's upper bound when the target rank falls in
    /// the <c>+Inf</c> bucket (it cannot be interpolated).
    /// </summary>
    public static double? Quantile(PrometheusHistogram histogram, double quantile)
    {
        if (histogram.Count <= 0 || histogram.Buckets.Count == 0)
        {
            return null;
        }

        double rank = quantile * histogram.Count;
        double previousBound = 0;
        double previousCumulative = 0;

        foreach (var (upperBound, cumulativeCount) in histogram.Buckets)
        {
            if (cumulativeCount >= rank)
            {
                if (double.IsPositiveInfinity(upperBound))
                {
                    return previousBound;
                }

                double bucketCount = cumulativeCount - previousCumulative;
                if (bucketCount <= 0)
                {
                    return upperBound;
                }

                double fraction = (rank - previousCumulative) / bucketCount;
                return previousBound + (fraction * (upperBound - previousBound));
            }

            previousBound = upperBound;
            previousCumulative = cumulativeCount;
        }

        return previousBound;
    }
}
