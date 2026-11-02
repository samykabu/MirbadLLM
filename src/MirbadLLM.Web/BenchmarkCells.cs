using MirbadLLM.Orchestration.Benchmarking;

namespace MirbadLLM.Web;

/// <summary>
/// The headline cells of a benchmark result in the history and tuning tables. A pooling (embedding) model has no
/// generation numbers: its cells show input tok/s, request latency and the vector check instead.
/// </summary>
public static class BenchmarkCells
{
    /// <summary>Column labels: generation or pooling.</summary>
    public static (string N1, string N16, string P95, string Check) Labels(bool pooling) => pooling
        ? ("N=1 input", "N=16 input", "Latency p95 N=16", "Vectors")
        : ("N=1", "N=16 aggregate", "TTFT p95 N=16", "Answers");

    /// <summary>Column help: generation or pooling.</summary>
    public static (string N1, string N16, string P95, string Check) Help(bool pooling) => pooling
        ? (BenchGlossary.EmbeddingN1, BenchGlossary.EmbeddingN16, BenchGlossary.EmbeddingP95, BenchGlossary.EmbeddingVectors)
        : (BenchGlossary.N1, BenchGlossary.N16, BenchGlossary.TtftP95, BenchGlossary.Answers);

    public static string N1(BenchmarkResult? r) => r switch
    {
        null => UiFormat.Missing,
        { Embeddings: { } e } => $"{UiFormat.Number(e.Level(1)?.InputTokS)} tok/s",
        _ => $"{UiFormat.Number(r.Level(1)?.PerRequestTokS)} tok/s",
    };

    public static string N16(BenchmarkResult? r) => r switch
    {
        null => UiFormat.Missing,
        { Embeddings: { } e } => $"{UiFormat.Number(e.Level(16)?.InputTokS)} tok/s",
        _ => $"{UiFormat.Number(r.Level(16)?.AggregateTokS)} tok/s",
    };

    public static string P95At16(BenchmarkResult? r) =>
        UiFormat.LatencyText(r?.Embeddings is { } e ? e.Level(16)?.LatencyP95 : r?.Level(16)?.TtftP95);

    /// <summary>"6/8" answers, or "Valid"/"Invalid" vectors for a pooling model.</summary>
    public static string Check(BenchmarkResult? r) => r switch
    {
        null => UiFormat.Missing,
        { Embeddings: { } e } => e.Check.Valid ? "Valid" : "Invalid",
        _ => $"{r.Guardrails.QaCorrect}/{r.Guardrails.QaTotal}",
    };
}
