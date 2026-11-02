using MirbadLLM.Core;
using MirbadLLM.Web;
using MirbadLLM.Web.Components.Ui;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Web;

/// <summary>The "old → new" wording of the Apply dialog, and the before/after deltas of the benchmark comparison.</summary>
public sealed class SettingChangesTests
{
    private static readonly ModelDefinition Before = new()
    {
        Id = "qwen",
        ServedName = "Qwen",
        ModelPath = "Qwen-NVFP4",
        MemoryBudgetGiB = 44,
    };

    [Fact]
    public void SpeculativeAndGraphMode_UseHumanLabels()
    {
        var after = Before with { Speculative = new SpeculativeDecoding("mtp", 2), CudaGraphMode = "PIECEWISE" };

        var changes = SettingChanges.RestartNeeded(Before, after).Select(c => c.ToString()).ToList();

        Assert.Equal(["Speculative decoding: off → MTP × 2", "CUDA graph mode: default → PIECEWISE"], changes);
    }

    [Fact]
    public void AutoStartAlone_NeedsNoRestart()
    {
        Assert.Empty(SettingChanges.RestartNeeded(Before, Before with { AutoStart = true }));
    }

    [Fact]
    public void ServedName_NeedsARestart()
    {
        var change = Assert.Single(SettingChanges.RestartNeeded(Before, Before with { ServedName = "Qwen-2" }));
        Assert.Equal("Served name: Qwen → Qwen-2", change.ToString());
    }

    [Fact]
    public void Numbers_AreFormattedLikeTheRestOfTheUi()
    {
        var after = Before with { MemoryBudgetGiB = 48, MaxModelLen = 131_072, MaxNumBatchedTokens = 8192, MaxNumSeqs = 32 };

        var changes = SettingChanges.RestartNeeded(Before, after).Select(c => c.ToString()).ToList();

        Assert.Equal(
        [
            "Memory budget: 44.0 GiB → 48.0 GiB",
            "Max model len: 262,144 → 131,072",
            "Max num seqs: 16 → 32",
            "Max batched tokens: default → 8,192",
        ], changes);
    }

    [Fact]
    public void RestartEstimate_UsesTheLastMeasuredStart()
    {
        Assert.Equal(
            "Restarting takes about 5 min (last start took 4m 12s); clients get 503 'starting' until it is Ready.",
            SettingChanges.RestartEstimate(TimeSpan.FromSeconds(252)));
        Assert.StartsWith("Restarting usually takes 2–5 min", SettingChanges.RestartEstimate(null), StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_Deltas_KnowWhichWayIsBetter()
    {
        Assert.Equal(20, BenchmarkCompare.Delta(10, 12)!.Value, 9);
        Assert.Null(BenchmarkCompare.Delta(null, 12));
        Assert.Equal("better", BenchmarkCompare.Verdict(20, higherIsBetter: true));
        Assert.Equal("worse", BenchmarkCompare.Verdict(20, higherIsBetter: false));
        Assert.Equal("better", BenchmarkCompare.Verdict(-15, higherIsBetter: false));
        Assert.Equal("same", BenchmarkCompare.Verdict(0.4, higherIsBetter: true));
    }

    [Fact]
    public void Compare_Rows_CoverThroughputLatencyAndAnswers()
    {
        var a = Benchmarking.Fakes.Results.Make(10, 400, ttftP95At16: 2);
        var b = Benchmarking.Fakes.Results.Make(12, 380, ttftP95At16: 1);

        var rows = BenchmarkCompare.BuildRows(a, b).ToDictionary(r => r.Key);

        Assert.Equal(20, BenchmarkCompare.Delta(rows["n1-per-request"].A, rows["n1-per-request"].B)!.Value, 9);
        Assert.Equal(-5, BenchmarkCompare.Delta(rows["n16-aggregate"].A, rows["n16-aggregate"].B)!.Value, 9);
        Assert.Equal("better", BenchmarkCompare.Verdict(BenchmarkCompare.Delta(rows["n16-ttft-p95"].A, rows["n16-ttft-p95"].B), rows["n16-ttft-p95"].HigherIsBetter));
        Assert.Contains("qa", rows.Keys);
        Assert.Contains("prefill-cold", rows.Keys);
    }
}
