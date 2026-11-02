namespace MirbadLLM.Agent.Contracts;

/// <summary>
/// Query of <c>GET /agent/v1/model-folders/{folder}/gguf?file=</c> (design-p3p4 §A.1.10; frozen by MN-202, D116; feature
/// <see cref="AgentFeatures.GgufInspect"/>, implemented by MN-209).
/// </summary>
public sealed record GgufInspectQuery
{
    /// <summary>
    /// GGUF file name inside the folder (the first part of a split model, e.g. <c>model-00001-of-00003.gguf</c>). Null =
    /// the folder's only model GGUF (<c>mmproj-*</c> files excluded); 400 <c>invalid_request</c> listing the candidates when
    /// there is more than one. A name with a directory part, <c>..</c>, a drive, a stream colon or a reserved device name
    /// is 400; a missing file is 404 <c>not_found</c>.
    /// </summary>
    public string? File { get; init; }
}

/// <summary>
/// The header of a GGUF model as the admission estimate needs it (design-p3p4 §A.1.10; frozen by MN-202, D116). Read by a
/// bounded, pure .NET reader (MN-209: only <c>GgufFormatException</c> on hostile input, at most 64 MiB allocated); a file
/// that is not a GGUF v2/v3 or breaks a bound answers 422 <c>gguf_invalid</c>. Keys are the GGUF metadata keys of the
/// file's <see cref="Architecture"/> (<c>{arch}.block_count</c>, …); values a file does not carry are null.
/// </summary>
public sealed record GgufSummary
{
    /// <summary>Folder under the models root.</summary>
    public required string Folder { get; init; }

    /// <summary>The file read (first part of a split model).</summary>
    public required string File { get; init; }

    /// <summary>GGUF format version (2 or 3).</summary>
    public required int GgufVersion { get; init; }

    /// <summary><c>general.architecture</c>, e.g. <c>qwen3</c>, <c>qwen35</c>.</summary>
    public required string Architecture { get; init; }

    /// <summary><c>general.name</c>, when present.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// <c>general.file_type</c> as llama.cpp's type name (<c>Q8_0</c>, <c>Q4_K_M</c>, <c>F16</c>, …); <c>ftype-{n}</c> for a
    /// value the reader does not know; null when absent.
    /// </summary>
    public string? FileType { get; init; }

    /// <summary>Parameter count from the tensor shapes (all parts).</summary>
    public long? ParameterCount { get; init; }

    /// <summary><c>{arch}.block_count</c>: transformer blocks (layers).</summary>
    public required int BlockCount { get; init; }

    /// <summary><c>{arch}.context_length</c>: training context.</summary>
    public long? ContextLength { get; init; }

    /// <summary><c>{arch}.embedding_length</c>: hidden size (also the native embedding dimension of embedding models).</summary>
    public int? EmbeddingLength { get; init; }

    /// <summary><c>{arch}.attention.head_count</c>.</summary>
    public int? HeadCount { get; init; }

    /// <summary><c>{arch}.attention.head_count_kv</c> (the largest value when the file stores one per layer).</summary>
    public int? HeadCountKv { get; init; }

    /// <summary><c>{arch}.attention.key_length</c>; null = <c>embedding_length / head_count</c>.</summary>
    public int? KeyLength { get; init; }

    /// <summary><c>{arch}.attention.value_length</c>; null = <c>embedding_length / head_count</c>.</summary>
    public int? ValueLength { get; init; }

    /// <summary>Vocabulary size (<c>tokenizer.ggml.tokens</c> count, else <c>{arch}.vocab_size</c>).</summary>
    public int? VocabSize { get; init; }

    /// <summary>
    /// <c>{arch}.full_attention_interval</c> of hybrid models (qwen35: every 4th block is attention, the rest recurrent);
    /// null for pure transformers.
    /// </summary>
    public int? FullAttentionInterval { get; init; }

    /// <summary><c>{arch}.ssm.conv_kernel</c> (hybrid/recurrent models).</summary>
    public int? SsmConvKernel { get; init; }

    /// <summary><c>{arch}.ssm.state_size</c>.</summary>
    public int? SsmStateSize { get; init; }

    /// <summary><c>{arch}.ssm.inner_size</c>.</summary>
    public int? SsmInnerSize { get; init; }

    /// <summary><c>{arch}.ssm.group_count</c>.</summary>
    public int? SsmGroupCount { get; init; }

    /// <summary><c>{arch}.pooling_type</c> as a <c>LlamaCppPooling</c> value (<c>last</c> for Qwen3-Embedding); null when absent.</summary>
    public string? PoolingType { get; init; }

    /// <summary>
    /// <c>{arch}.attention.causal</c>: false for non-causal (BERT-style) embedding models, whose whole input must fit one
    /// micro-batch; null when absent, which llama.cpp treats as causal (Qwen3-Embedding). Drives the embedding micro-batch
    /// default (<c>LlamaCppSettings.EmbeddingMicroBatch</c>) in the editor and the estimate (spike S1 / C2; added by
    /// MN-207, D118).
    /// </summary>
    public bool? CausalAttention { get; init; }

    /// <summary><c>tokenizer.ggml.add_eos_token</c>.</summary>
    public bool? AddEos { get; init; }

    /// <summary>True when the file embeds <c>tokenizer.chat_template</c>.</summary>
    public bool HasChatTemplate { get; init; }

    /// <summary>Number of split parts (<c>split.count</c>; 1 for a single file).</summary>
    public int SplitCount { get; init; } = 1;

    /// <summary>Bytes of tensor data over all parts (the weights term of the estimate).</summary>
    public required long TensorBytes { get; init; }

    /// <summary>Size of all parts on disk.</summary>
    public required long FileBytes { get; init; }
}
