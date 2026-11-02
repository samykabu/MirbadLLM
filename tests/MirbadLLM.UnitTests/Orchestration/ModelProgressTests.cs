using MirbadLLM.Core;
using MirbadLLM.Orchestration;

namespace MirbadLLM.UnitTests.Orchestration;

public class ModelProgressTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FrozenEngineWithRequestsInFlightIsStalled()
    {
        // The 2026-10-17 shape: 5 running, no tokens, KV usage frozen at 11.1 %.
        var issue = ModelProgress.Assess(Series(_ => Load(running: 5, gen: 0, prompt: 0, kv: 0.111)), Now);

        Assert.NotNull(issue);
        Assert.True(issue.Stalled);
        Assert.StartsWith("Stalled: 5 requests running", issue.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void LongPrefillGrowingTheKvCacheIsNotStalled()
    {
        // vLLM counts prompt tokens when a prefill completes: KV growth is the only sign of progress meanwhile.
        var issue = ModelProgress.Assess(Series(i => Load(running: 8, gen: 0, prompt: 0, kv: 0.30 + (i * 0.01))), Now);

        Assert.NotNull(issue);
        Assert.False(issue.Stalled); // still slow: nothing generated for 5 minutes with work queued
    }

    [Fact]
    public void BusyModelGeneratingUnderTheThresholdIsSlow()
    {
        // The 2026-10-18 shape after the restart: 8 running, 4 waiting, about 2 tokens/s in total.
        var issue = ModelProgress.Assess(Series(_ => Load(running: 8, waiting: 4, gen: 2.2, prompt: 0, kv: 0.36)), Now);

        Assert.NotNull(issue);
        Assert.False(issue.Stalled);
        Assert.Equal("Slow: 2.2 tokens/s for 5 min with 8 running and 4 waiting.", issue.Text);
    }

    [Fact]
    public void OneFastSampleClearsSlow() =>
        Assert.Null(ModelProgress.Assess(Series(i => Load(running: 8, gen: i == 50 ? 40 : 2, prompt: 0, kv: 0.36)), Now));

    [Fact]
    public void OneIdleSampleClearsEverything() =>
        Assert.Null(ModelProgress.Assess(Series(i => Load(running: i == 50 ? 0 : 5, gen: 0, prompt: 0, kv: 0.1)), Now));

    [Fact]
    public void HealthyBusyModelHasNoIssue() =>
        Assert.Null(ModelProgress.Assess(Series(_ => Load(running: 3, gen: 150, prompt: 900, kv: 0.2)), Now));

    [Fact]
    public void SamplesNotCoveringTheWindowAreNotJudged()
    {
        // Ready for two minutes only: too early to call it stalled.
        var samples = Series(_ => Load(running: 5, gen: 0, prompt: 0, kv: 0.1)).Where(s => s.At >= Now.AddMinutes(-2)).ToList();

        Assert.Null(ModelProgress.Assess(samples, Now));
    }

    [Fact]
    public void FirstScrapeWithoutRatesIsNotJudged()
    {
        var samples = Series(_ => Load(running: 5, gen: 0, prompt: 0, kv: 0.1)).ToList();
        samples[^1] = samples[^1] with { HasRates = false };

        Assert.Null(ModelProgress.Assess(samples, Now));
    }

    [Fact]
    public void LlamaCppWithoutKvUsageCanStillStall() =>
        Assert.True(ModelProgress.Assess(Series(_ => Load(running: 1, gen: 0, prompt: 0, kv: double.NaN)), Now)?.Stalled);

    /// <summary>Every 5 s over the last 6 minutes, oldest first, as the history keeps them.</summary>
    private static List<ModelSample> Series(Func<int, ModelMetrics> load) =>
        Enumerable.Range(0, 73).Select(i => new ModelSample(Now.AddMinutes(-6).AddSeconds(i * 5), Kpis(load(i)))).ToList();

    private static ModelMetrics Load(double running, double gen, double prompt, double kv, double waiting = 0) =>
        new(running, waiting, kv, gen, prompt);

    private static ModelKpis Kpis(ModelMetrics load) =>
        new(load, 0, null, null, null, null, null, null, 0, new Dictionary<string, double>(), 0);
}
