using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Orchestration;

/// <summary>
/// The definitions running on the DGX Spark when the canonical configuration shipped (from
/// /srv/mirbadllm/data/models.json), the pre-redesign command-line builder that launched their containers, and its
/// raw hash — so migration and restart-pending tests compare against exactly what is deployed.
/// </summary>
internal static class LiveDefinitions
{
    public const string Image = "local/qwen38-vllm-dflash2:66e5414c6d75";
    public const double TotalGiB = 121.7;

    /// <summary>Typed MTP × 2, but the raw ExtraArgs config (MTP × 3, triton MoE) is what vLLM actually ran (last wins).</summary>
    public static readonly ModelDefinition Qwen35B = new()
    {
        Id = "qwen36-35b-a3b-nvfp4",
        ServedName = "qwen36-35b-a3b",
        ModelPath = "Qwen3.6-35B-A3B-NVFP4",
        MemoryBudgetGiB = 60,
        MaxNumSeqs = 8,
        MaxNumBatchedTokens = 8192,
        KvCacheDtype = "fp8",
        Speculative = new SpeculativeDecoding("mtp", 2),
        CudaGraphMode = "PIECEWISE",
        ExtraArgs =
        [
            "--trust-remote-code", "--attention-backend", "flashinfer", "--moe-backend", "marlin", "--enable-chunked-prefill",
            "--async-scheduling", "--load-format", "fastsafetensors",
            "--speculative-config", """{"method":"mtp","num_speculative_tokens":3,"moe_backend":"triton"}""",
        ],
    };

    public static readonly ModelDefinition Nemotron = new()
    {
        Id = "nemotron-3-5",
        ServedName = "nemotron-3.5",
        ModelPath = "Nemotron-3.5-Lightning-30B-A3B-NVFP4",
        MemoryBudgetGiB = 40,
        ToolCallParser = "qwen3_coder",
        ReasoningParser = "nemotron_v3",
        ExtraArgs =
        [
            "--moe-backend", "marlin", "--mamba-backend", "flashinfer", "--mamba-cache-mode", "align",
            "--speculative-config", """{"model":"/models/Nemotron-3.5-Lightning-30B-A3B-NVFP4-DSpark","num_speculative_tokens":3}""",
        ],
    };

    /// <summary>EnablePrefixCaching false used to emit nothing (vLLM's default applied); now it means explicit off.</summary>
    public static readonly ModelDefinition Embedding = new()
    {
        Id = "qwen3-embedding",
        ServedName = "qwen3-embedding-8b",
        ModelPath = "Qwen3-Embedding-8B",
        MemoryBudgetGiB = 24,
        MaxModelLen = 32_768,
        ToolCallParser = null,
        ReasoningParser = null,
        EnablePrefixCaching = false,
        ExtraArgs = ["--runner", "pooling"],
        Tags = ["embedding"],
    };

    /// <summary>The pre-redesign <c>VllmArgsBuilder.Build</c>, verbatim: what the running containers were launched with.</summary>
    public static List<string> LegacyBuild(ModelDefinition model, double totalGiB)
    {
        var args = new List<string> { "--model", $"/models/{model.ModelPath}", "--served-model-name", model.ServedName };
        if (!string.IsNullOrEmpty(model.Quantization))
        {
            args.Add("--quantization");
            args.Add(model.Quantization);
        }

        args.AddRange(["--kv-cache-dtype", model.KvCacheDtype, "--max-model-len", model.MaxModelLen.ToString(CultureInfo.InvariantCulture),
            "--max-num-seqs", model.MaxNumSeqs.ToString(CultureInfo.InvariantCulture)]);
        if (model.MaxNumBatchedTokens is { } batched)
        {
            args.Add("--max-num-batched-tokens");
            args.Add(batched.ToString(CultureInfo.InvariantCulture));
        }

        double utilization = totalGiB > 0 ? model.MemoryBudgetGiB / totalGiB : 0;
        args.Add("--gpu-memory-utilization");
        args.Add(utilization.ToString("F4", CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(model.ToolCallParser))
        {
            args.AddRange(["--enable-auto-tool-choice", "--tool-call-parser", model.ToolCallParser]);
        }

        if (!string.IsNullOrEmpty(model.ReasoningParser))
        {
            args.AddRange(["--reasoning-parser", model.ReasoningParser]);
        }

        if (model.EnablePrefixCaching == true)
        {
            args.Add("--enable-prefix-caching");
        }

        if (model.Speculative is { } speculative)
        {
            args.Add("--speculative-config");
            args.Add(JsonSerializer.Serialize(new LegacySpeculative(speculative.Method, speculative.NumSpeculativeTokens)));
        }

        if (!string.IsNullOrEmpty(model.CudaGraphMode))
        {
            args.Add("--compilation-config");
            args.Add(JsonSerializer.Serialize(new LegacyCompilation(model.CudaGraphMode)));
        }

        args.AddRange(["--disable-access-log-for-endpoints", "/health,/metrics"]);
        args.AddRange(model.ExtraArgs);
        return args;
    }

    /// <summary>The pre-redesign <c>ModelConfigHash.Compute(image, args)</c>: SHA-256 over the raw list (the label on running containers).</summary>
    public static string LegacyHash(string image, IReadOnlyList<string> args)
    {
        var text = new StringBuilder(image);
        foreach (string arg in args)
        {
            text.Append('\0').Append(arg);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())).AsSpan(0, 8));
    }

    /// <summary>A definition as the pre-redesign registry wrote it to models.json (PascalCase speculative, bool prefix caching).</summary>
    public static string LegacyJson(ModelDefinition model) =>
        JsonSerializer.Serialize(new
        {
            model.Id,
            model.ServedName,
            model.ModelPath,
            model.Quantization,
            model.KvCacheDtype,
            model.MaxModelLen,
            model.MaxNumSeqs,
            model.MaxNumBatchedTokens,
            model.MemoryBudgetGiB,
            model.ToolCallParser,
            model.ReasoningParser,
            EnablePrefixCaching = model.EnablePrefixCaching ?? true,
            Speculative = model.Speculative is { } s ? new { s.Method, s.NumSpeculativeTokens } : null,
            model.CudaGraphMode,
            model.Image,
            model.ExtraArgs,
            model.AutoStart,
            model.Tags,
            model.Sources,
        });

    private sealed record LegacySpeculative(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("num_speculative_tokens")] int NumSpeculativeTokens);

    private sealed record LegacyCompilation([property: JsonPropertyName("cudagraph_mode")] string CudagraphMode);
}
