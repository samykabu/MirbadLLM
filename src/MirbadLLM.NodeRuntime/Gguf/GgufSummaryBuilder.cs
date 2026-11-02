using System.Globalization;
using System.Text;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.NodeRuntime.Gguf;

/// <summary>
/// Turns the headers of a GGUF's parts (<see cref="GgufReader"/>) into the wire <see cref="GgufSummary"/> (design-p3p4
/// §A.1.10; MN-209): the <c>{arch}.*</c> keys of the file's architecture, llama.cpp's file-type and pooling names, and
/// tensor, parameter and file bytes summed over all split parts. Metadata comes from the first part (llama.cpp's
/// <c>gguf-split</c> writes it there). A header without an architecture or a positive <c>{arch}.block_count</c>, or with
/// a kept value of the wrong type or out of range, is a <see cref="GgufFormatException"/>.
/// </summary>
internal static class GgufSummaryBuilder
{
    /// <summary>Builds the summary of <paramref name="parts"/> (first part first).</summary>
    public static GgufSummary Build(string folder, string file, IReadOnlyList<GgufHeader> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
        {
            throw new ArgumentException("At least one part is needed.", nameof(parts));
        }

        GgufHeader first = parts[0];
        string arch = first.Architecture ?? throw new GgufFormatException("The GGUF has no general.architecture.");
        var v = first.Values;

        int blockCount = Int(v, GgufKeys.Arch(arch, GgufKeys.BlockCount)) ??
            throw new GgufFormatException($"The GGUF has no {arch}.{GgufKeys.BlockCount}.");
        if (blockCount <= 0)
        {
            throw new GgufFormatException(string.Create(CultureInfo.InvariantCulture, $"{arch}.{GgufKeys.BlockCount} is {blockCount}."));
        }

        int? vocab = v.TryGetValue(GgufKeys.Tokens, out var tokens) && tokens is { Kind: GgufValueKind.Array, ElementType: GgufValueType.String }
            ? checked((int)tokens.ArrayCount)
            : Int(v, GgufKeys.Arch(arch, GgufKeys.VocabSize));

        return new GgufSummary
        {
            Folder = folder,
            File = file,
            GgufVersion = first.Version,
            Architecture = arch,
            Name = Text(v, GgufKeys.Name),
            FileType = FileTypeName(Long(v, GgufKeys.FileType)),
            ParameterCount = parts.Sum(p => p.ParameterCount),
            BlockCount = blockCount,
            ContextLength = Long(v, GgufKeys.Arch(arch, GgufKeys.ContextLength)),
            EmbeddingLength = Int(v, GgufKeys.Arch(arch, GgufKeys.EmbeddingLength)),
            HeadCount = Int(v, GgufKeys.Arch(arch, GgufKeys.HeadCount)),
            HeadCountKv = Int(v, GgufKeys.Arch(arch, GgufKeys.HeadCountKv)),
            KeyLength = Int(v, GgufKeys.Arch(arch, GgufKeys.KeyLength)),
            ValueLength = Int(v, GgufKeys.Arch(arch, GgufKeys.ValueLength)),
            VocabSize = vocab,
            FullAttentionInterval = Int(v, GgufKeys.Arch(arch, GgufKeys.FullAttentionInterval)),
            SsmConvKernel = Int(v, GgufKeys.Arch(arch, GgufKeys.SsmConvKernel)),
            SsmStateSize = Int(v, GgufKeys.Arch(arch, GgufKeys.SsmStateSize)),
            SsmInnerSize = Int(v, GgufKeys.Arch(arch, GgufKeys.SsmInnerSize)),
            SsmGroupCount = Int(v, GgufKeys.Arch(arch, GgufKeys.SsmGroupCount)),
            PoolingType = PoolingName(Long(v, GgufKeys.Arch(arch, GgufKeys.PoolingType))),
            CausalAttention = Bool(v, GgufKeys.Arch(arch, GgufKeys.Causal)),
            AddEos = Bool(v, GgufKeys.AddEos),
            HasChatTemplate = v.TryGetValue(GgufKeys.ChatTemplate, out var template)
                && template is { Kind: GgufValueKind.Text, TextValue.Length: > 0 },
            SplitCount = parts.Count,
            TensorBytes = parts.Sum(p => p.TensorBytes),
            FileBytes = parts.Sum(p => p.FileBytes),
        };
    }

    /// <summary>
    /// llama.cpp's name of a <c>general.file_type</c> (<c>llama_ftype</c>, as <c>llama-quantize</c> spells it);
    /// <c>ftype-{n}</c> for a value not in the table; null when absent.
    /// </summary>
    public static string? FileTypeName(long? fileType) => fileType switch
    {
        null => null,
        0 => "F32",
        1 => "F16",
        2 => "Q4_0",
        3 => "Q4_1",
        7 => "Q8_0",
        8 => "Q5_0",
        9 => "Q5_1",
        10 => "Q2_K",
        11 => "Q3_K_S",
        12 => "Q3_K_M",
        13 => "Q3_K_L",
        14 => "Q4_K_S",
        15 => "Q4_K_M",
        16 => "Q5_K_S",
        17 => "Q5_K_M",
        18 => "Q6_K",
        19 => "IQ2_XXS",
        20 => "IQ2_XS",
        21 => "Q2_K_S",
        22 => "IQ3_XS",
        23 => "IQ3_XXS",
        24 => "IQ1_S",
        25 => "IQ4_NL",
        26 => "IQ3_S",
        27 => "IQ3_M",
        28 => "IQ2_S",
        29 => "IQ2_M",
        30 => "IQ4_XS",
        31 => "IQ1_M",
        32 => "BF16",
        36 => "TQ1_0",
        37 => "TQ2_0",
        38 => "MXFP4_MOE",
        { } n => string.Create(CultureInfo.InvariantCulture, $"ftype-{n}"),
    };

    /// <summary><c>{arch}.pooling_type</c> (<c>llama_pooling_type</c>) as a <see cref="LlamaCppPooling"/> value; null when absent, unspecified (-1) or unknown.</summary>
    public static string? PoolingName(long? pooling) => pooling switch
    {
        0 => LlamaCppPooling.None,
        1 => LlamaCppPooling.Mean,
        2 => LlamaCppPooling.Cls,
        3 => LlamaCppPooling.Last,
        4 => LlamaCppPooling.Rank,
        _ => null,
    };

    private static long? Long(IReadOnlyDictionary<string, GgufValue> values, string key) =>
        values.TryGetValue(key, out var value) ? value.AsInteger(key) : null;

    private static int? Int(IReadOnlyDictionary<string, GgufValue> values, string key)
    {
        if (Long(values, key) is not { } n)
        {
            return null;
        }

        if (n < 0 || n > int.MaxValue)
        {
            throw new GgufFormatException(string.Create(CultureInfo.InvariantCulture, $"'{key}' is {n}, out of range."));
        }

        return (int)n;
    }

    private static bool? Bool(IReadOnlyDictionary<string, GgufValue> values, string key)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return null;
        }

        return value.Kind == GgufValueKind.Boolean ? value.BoolValue : throw new GgufFormatException($"'{key}' is not a bool.");
    }

    private static string? Text(IReadOnlyDictionary<string, GgufValue> values, string key)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return null;
        }

        return value.Kind == GgufValueKind.Text ? value.TextValue : throw new GgufFormatException($"'{key}' is not a string.");
    }
}

/// <summary>The GGUF metadata keys the reader keeps (llama.cpp <c>LLM_KV_*</c> names).</summary>
internal static class GgufKeys
{
    public const string Architecture = "general.architecture";
    public const string Name = "general.name";
    public const string FileType = "general.file_type";
    public const string Alignment = "general.alignment";
    public const string SplitCount = "split.count";
    public const string SplitNumber = "split.no";
    public const string SplitTensorsCount = "split.tensors.count";
    public const string Tokens = "tokenizer.ggml.tokens";
    public const string AddEos = "tokenizer.ggml.add_eos_token";
    public const string ChatTemplate = "tokenizer.chat_template";

    public const string BlockCount = "block_count";
    public const string ContextLength = "context_length";
    public const string EmbeddingLength = "embedding_length";
    public const string VocabSize = "vocab_size";
    public const string HeadCount = "attention.head_count";
    public const string HeadCountKv = "attention.head_count_kv";
    public const string KeyLength = "attention.key_length";
    public const string ValueLength = "attention.value_length";
    public const string Causal = "attention.causal";
    public const string FullAttentionInterval = "full_attention_interval";
    public const string SsmConvKernel = "ssm.conv_kernel";
    public const string SsmStateSize = "ssm.state_size";
    public const string SsmInnerSize = "ssm.inner_size";
    public const string SsmGroupCount = "ssm.group_count";
    public const string PoolingType = "pooling_type";

    public static readonly byte[] ArchitectureUtf8 = Encoding.UTF8.GetBytes(Architecture);

    private static readonly string[] General = [Name, FileType, Alignment, SplitCount, SplitNumber, SplitTensorsCount, Tokens, AddEos, ChatTemplate];

    private static readonly string[] PerArchitecture =
    [
        BlockCount, ContextLength, EmbeddingLength, VocabSize, HeadCount, HeadCountKv, KeyLength, ValueLength, Causal,
        FullAttentionInterval, SsmConvKernel, SsmStateSize, SsmInnerSize, SsmGroupCount, PoolingType,
    ];

    /// <summary><c>{arch}.{suffix}</c>.</summary>
    public static string Arch(string architecture, string suffix) => architecture + "." + suffix;

    /// <summary>Every key kept for <paramref name="architecture"/> (only the general ones when it is unknown), as UTF-8.</summary>
    public static IReadOnlyList<(byte[] Utf8, string Name)> Wanted(string? architecture)
    {
        var keys = new List<string>(General);
        if (architecture is not null)
        {
            keys.AddRange(PerArchitecture.Select(s => Arch(architecture, s)));
        }

        return [.. keys.Where(k => Encoding.UTF8.GetByteCount(k) <= GgufReader.MaxComparedKeyBytes).Select(k => (Encoding.UTF8.GetBytes(k), k))];
    }
}
