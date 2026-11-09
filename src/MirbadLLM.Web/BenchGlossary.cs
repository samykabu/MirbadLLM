namespace MirbadLLM.Web;

/// <summary>
/// The words behind the benchmark and tuning tables' column headers (shown through <c>ColumnHelp</c>) and
/// their one-line legends. One place, so Benchmark, Tune and the result view explain a column the same way.
/// </summary>
public static class BenchGlossary
{
    public const string Concurrent =
        "How many streaming requests run at the same time. Each level runs 3 times with exactly 256 output tokens per request; the median run is shown.";

    public const string PerRequest =
        "Tokens per second one stream sees at this level: what a single client experiences while the others run.";

    public const string Aggregate =
        "Tokens per second summed over every concurrent stream: the box's total throughput at this level.";

    public const string N1 =
        "Tokens per second with one request at a time (N = 1): the speed a single client sees. Median of 3 runs of 256 output tokens.";

    public const string N16 =
        "Tokens per second summed over 16 concurrent requests (N = 16): the throughput many agents get together. Median of 3 runs.";

    public const string TtftP50 =
        "Time to first token, median (p50): from sending a request to the first streamed token.";

    public const string TtftP95 =
        "Time to first token, 95th percentile (p95): the wait the slowest 5 % of requests see. Tuning rejects a candidate whose p95 at N = 16 more than doubles.";

    public const string Answers =
        "Correct answers out of 8 questions with known answers, plus one tool call that must come back valid. Fewer correct answers than the baseline, or a broken tool call, rejects a candidate.";

    public const string Score =
        "How the goal ranks candidates. Balanced: the geometric mean of the N = 1 and N = 16 gains over the baseline (×1.000 = the baseline). Many agents: N = 16 aggregate tok/s. Single user: N = 1 tok/s.";

    public const string Settings =
        "The settings measured. speculative: speculative decoding (MTP × 2 = the model's own multi-token-prediction head drafts 2 tokens a step; n-gram × k drafts from the prompt). graphs: CUDA graph mode (PIECEWISE, FULL, default). seqs: max concurrent sequences. batched: max tokens scheduled per step. KV: the KV-cache data type.";

    public const string OutputTokens =
        "Tokens each request generates (min_tokens = max_tokens): 256 for chat-sized answers, 1,024 for long answers where decode speed dominates.";

    public const string Errors =
        "Timed requests that failed (HTTP error or timeout) over every repetition. Any failure rejects a tuning candidate.";

    public const string EmbeddingConcurrent =
        "How many /v1/embeddings requests run at the same time, each a batch of distinct inputs of mixed lengths. Each level runs 3 times; the median run is shown.";

    public const string EmbeddingN1 =
        "Input tokens embedded per second with one request at a time (N = 1). Median of 3 runs.";

    public const string EmbeddingN16 =
        "Input tokens embedded per second with 16 concurrent requests (N = 16): the box's embedding throughput. Median of 3 runs.";

    public const string EmbeddingRequests = "Requests completed per second at this level.";

    public const string EmbeddingP50 = "Request latency, median (p50): from sending a batch to receiving its vectors.";

    public const string EmbeddingP95 =
        "Request latency, 95th percentile (p95): the wait the slowest 5 % of requests see. Tuning rejects a candidate whose p95 at N = 16 more than doubles.";

    public const string EmbeddingVectors =
        "Every response must return one finite, non-zero vector per input, all of one dimension, normalized like the baseline's; otherwise the candidate is rejected.";

    /// <summary>The one-line legend under the embedding benchmark tables.</summary>
    public const string EmbeddingLegend =
        "Embedding model · N = concurrent /v1/embeddings requests, each a batch of mixed-length inputs · input tok/s = prompt tokens embedded per second · latency p50 median, p95 slowest 5 %.";

    /// <summary>The one-line legend under the benchmark and tuning tables.</summary>
    public const string Legend =
        "N = concurrent streaming requests · N = 1 is per request, N = 16 is summed over all 16 · TTFT = time to first token, p50 median, p95 slowest 5 % · Settings: speculative decoding, graphs = CUDA graph mode, seqs = max sequences, batched = max batched tokens, KV = KV-cache type.";
}
