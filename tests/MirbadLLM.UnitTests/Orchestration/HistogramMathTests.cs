using MirbadLLM.Orchestration;

#pragma warning disable CA1707 // underscore test names.

namespace MirbadLLM.UnitTests.Orchestration;

public sealed class HistogramMathTests
{
    private static PrometheusHistogram Histogram(double count, params (double UpperBound, double CumulativeCount)[] buckets)
        => new(buckets, 0, count);

    [Fact]
    public void Quantile_InterpolatesLinearlyWithinBucket_KnownFixture()
    {
        // le=0.1: 0, le=0.5: 2, le=1.0: 8, +Inf: 10 (total 10 observations).
        var histogram = Histogram(10, (0.1, 0), (0.5, 2), (1.0, 8), (double.PositiveInfinity, 10));

        double? p50 = HistogramMath.Quantile(histogram, 0.50);
        double? p95 = HistogramMath.Quantile(histogram, 0.95);

        Assert.Equal(0.75, p50); // rank=5, bucket (0.5,1.0], (5-2)/(8-2)=0.5 -> 0.5+0.5*0.5=0.75
        Assert.Equal(1.0, p95); // rank=9.5 falls in the +Inf bucket -> previous finite bound (1.0)
    }

    [Fact]
    public void Quantile_PlusInfBucket_ReturnsPreviousFiniteBound()
    {
        // rank = 0.99 * 10 = 9.9; the finite bucket (le=1.0) only covers 5 of the 10 observations, so the
        // rank falls in the +Inf bucket, which cannot be interpolated -> returns the last finite bound.
        var histogram = Histogram(10, (1.0, 5), (double.PositiveInfinity, 10));

        Assert.Equal(1.0, HistogramMath.Quantile(histogram, 0.99));
    }

    [Fact]
    public void Quantile_NoFiniteBuckets_ReturnsZero()
    {
        var onlyInf = Histogram(3, (double.PositiveInfinity, 3));

        Assert.Equal(0.0, HistogramMath.Quantile(onlyInf, 0.5));
    }

    [Fact]
    public void Quantile_ZeroCount_ReturnsNull()
    {
        var histogram = Histogram(0, (1.0, 0), (double.PositiveInfinity, 0));

        Assert.Null(HistogramMath.Quantile(histogram, 0.5));
    }

    [Fact]
    public void Delta_SubtractsCumulativeCounts_PerBucket()
    {
        var previous = Histogram(10, (0.1, 1), (0.5, 4), (1.0, 9), (double.PositiveInfinity, 10));
        var current = Histogram(20, (0.1, 1), (0.5, 6), (1.0, 17), (double.PositiveInfinity, 20));

        var delta = HistogramMath.Delta(previous, current);

        Assert.NotNull(delta);
        Assert.Equal(10, delta!.Count);
        Assert.Equal(0, delta.Buckets.Single(b => b.UpperBound == 0.1).CumulativeCount);
        Assert.Equal(2, delta.Buckets.Single(b => b.UpperBound == 0.5).CumulativeCount);
        Assert.Equal(8, delta.Buckets.Single(b => b.UpperBound == 1.0).CumulativeCount);
        Assert.Equal(10, delta.Buckets.Single(b => double.IsPositiveInfinity(b.UpperBound)).CumulativeCount);
    }

    [Fact]
    public void Delta_CounterReset_ReturnsCurrentHistogramAsTheDelta()
    {
        var previous = Histogram(100, (1.0, 100), (double.PositiveInfinity, 100));
        var current = Histogram(5, (1.0, 5), (double.PositiveInfinity, 5)); // container restarted

        var delta = HistogramMath.Delta(previous, current);

        Assert.Same(current, delta);
    }

    [Fact]
    public void Delta_NoPreviousScrape_ReturnsNull()
    {
        var current = Histogram(5, (1.0, 5), (double.PositiveInfinity, 5));

        Assert.Null(HistogramMath.Delta(null, current));
    }
}
