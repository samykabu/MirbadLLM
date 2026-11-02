using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Storage;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Gguf;

#pragma warning disable CA1707, CA2000, CA5394, xUnit1051 // underscore names; the app owns its services; seeded fuzz Random; sync reader.

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// MN-209: the pure .NET GGUF reader (design-p3p4 §A.1.10, §A.8) against real headers and hostile input.
/// <para>Fixtures under <c>Fixtures/gguf/</c> are headers only (metadata + tensor table, no weights):
/// <c>qwen3-embedding-4b-q8_0.header.bin</c> from <c>Qwen/Qwen3-Embedding-4B-GGUF</c> @ <c>f4602530</c> (the file spike S1
/// ran), <c>qwen35-planner-q8_0.header.bin</c> from the planner's Ollama blob <c>sha256:aa98adcc…d795</c> (copied once
/// from the PC's Ollama store; the planner stays on the DGX, O7). To keep them small, the strings of
/// <c>tokenizer.ggml.tokens</c> and <c>tokenizer.ggml.merges</c> are emptied (counts kept); every other byte is the
/// file's. <c>tiny-split-0000k-of-00003.bin</c> is a synthetic 3-part split header set. The expected values were taken
/// from an independent Python parse of the real files and agree with llama-server's <c>/v1/models</c> meta of S1
/// (<c>n_params 4021774336</c>, <c>size 4273711264</c>, <c>n_vocab 151665</c>).</para>
/// </summary>
public sealed class GgufReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mirbadllm-gguf-" + Guid.NewGuid().ToString("N"));

    public GgufReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    internal static string FixturePath(string name) => Path.Combine(FixtureDirectory(), name);

    private static string FixtureDirectory([CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures", "gguf");

    internal static readonly GgufSummary Qwen3Embedding = new()
    {
        Folder = "Qwen3-Embedding-4B-GGUF",
        File = "Qwen3-Embedding-4B-Q8_0.gguf",
        GgufVersion = 3,
        Architecture = "qwen3",
        Name = "Qwen3 Embedding 4B",
        FileType = "Q8_0",
        ParameterCount = 4_021_774_336,
        BlockCount = 36,
        ContextLength = 40_960,
        EmbeddingLength = 2560,
        HeadCount = 32,
        HeadCountKv = 8,
        KeyLength = 128,
        ValueLength = 128,
        VocabSize = 151_665,
        PoolingType = LlamaCppPooling.Last,
        CausalAttention = null,
        AddEos = true,
        HasChatTemplate = true,
        SplitCount = 1,
        TensorBytes = 4_273_711_264,
        FileBytes = 3_058_752, // the header-only fixture; the real file is 4 279 660 224 B
    };

    internal static readonly GgufSummary Qwen35Planner = new()
    {
        Folder = "ov-intent-analysis",
        File = "model.gguf",
        GgufVersion = 3,
        Architecture = "qwen35",
        Name = "Checkpoint 600",
        FileType = "Q8_0",
        ParameterCount = 752_393_024,
        BlockCount = 24,
        ContextLength = 262_144,
        EmbeddingLength = 1024,
        HeadCount = 8,
        HeadCountKv = 2,
        KeyLength = 256,
        ValueLength = 256,
        VocabSize = 248_320,
        FullAttentionInterval = 4,
        SsmConvKernel = 4,
        SsmStateSize = 128,
        SsmInnerSize = 2048,
        SsmGroupCount = 16,
        PoolingType = null,
        CausalAttention = null,
        AddEos = null,
        HasChatTemplate = true,
        SplitCount = 1,
        TensorBytes = 800_881_920,
        FileBytes = 4_988_672,
    };

    // ---------------------------------------------------------------- golden summaries

    [Fact]
    public async Task Qwen3EmbeddingHeader_GoldenSummary()
    {
        var summary = await new GgufReader().ReadAsync(FixturePath("qwen3-embedding-4b-q8_0.header.bin"), Qwen3Embedding.Folder, Qwen3Embedding.File);
        Assert.Equal(Qwen3Embedding, summary);
    }

    [Fact]
    public async Task Qwen35HybridHeader_GoldenSummary()
    {
        var summary = await new GgufReader().ReadAsync(FixturePath("qwen35-planner-q8_0.header.bin"), Qwen35Planner.Folder, Qwen35Planner.File);
        Assert.Equal(Qwen35Planner, summary);
    }

    [Fact]
    public void Summary_SerializesWithTheWireNames()
    {
        var json = JsonSerializer.SerializeToElement(Qwen3Embedding, AgentJson.Options);
        Assert.Equal("last", json.GetProperty("poolingType").GetString());
        Assert.Equal(4_273_711_264, json.GetProperty("tensorBytes").GetInt64());
    }

    [Fact]
    public async Task SplitModel_PartsAreSummed()
    {
        string first = CopySplit();
        var summary = await new GgufReader().ReadAsync(first, "tiny", "tiny-split-00001-of-00003.gguf");

        Assert.Equal(new GgufSummary
        {
            Folder = "tiny",
            File = "tiny-split-00001-of-00003.gguf",
            GgufVersion = 3,
            Architecture = "llama",
            Name = "Tiny Split",
            FileType = "F16",
            ParameterCount = 4608 + 6144 + 2560,
            BlockCount = 2,
            ContextLength = 2048,
            EmbeddingLength = 64,
            HeadCount = 4,
            HeadCountKv = 2,
            VocabSize = 8,
            AddEos = false,
            SplitCount = 3,
            TensorBytes = 17_408 + 6528 + 3200,
            FileBytes = 704 + 224 + 224,
        }, summary);
    }

    [Fact]
    public void SplitModel_MissingPart_IsInvalid()
    {
        string first = CopySplit();
        File.Delete(Path.Combine(_dir, "tiny-split-00003-of-00003.gguf"));
        var ex = Assert.Throws<GgufFormatException>(() => GgufReader.Read(first, "tiny", "x"));
        Assert.Contains("3 of 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitModel_PartsWhoseSizesOverflowOnlyWhenSummed_AreInvalid_NeverAnOverflowException()
    {
        // MN-219: each part's tensor bytes fit in a long (about 0.7 x 2^63 each), their sum does not; the sum ran outside
        // the reader's overflow guard, so an OverflowException escaped (the endpoint answered 500, not 422 gguf_invalid).
        // The last tensor of a part is rewritten, so the offsets stay contiguous: part 2's Q8_0 [d, 64], part 3's F16 [d, 8].
        string first = CopySplit();
        foreach ((string part, string tensor, ulong firstDim) in new[] { ("00002", "blk.1.attn_q.weight", 94946476849975616UL), ("00003", "output.weight", 403522526612396416UL) })
        {
            string path = Path.Combine(_dir, $"tiny-split-{part}-of-00003.gguf");
            File.WriteAllBytes(path, Rewrite(File.ReadAllBytes(path), tensor, firstDim));
        }

        var ex = Record.Exception(() => GgufReader.Read(first, "tiny", "x"));
        Assert.IsType<GgufFormatException>(ex);
    }

    [Fact]
    public void SplitModel_PartWithWrongNumber_IsInvalid()
    {
        string first = CopySplit();
        File.Copy(FixturePath("tiny-split-00003-of-00003.bin"), Path.Combine(_dir, "tiny-split-00002-of-00003.gguf"), overwrite: true);
        Assert.Throws<GgufFormatException>(() => GgufReader.Read(first, "tiny", "x"));
    }

    [Fact]
    public void SplitModel_SecondPartGivenFirst_IsInvalid()
    {
        CopySplit();
        Assert.Throws<GgufFormatException>(() => GgufReader.Read(Path.Combine(_dir, "tiny-split-00002-of-00003.gguf"), "tiny", "x"));
    }

    [Fact]
    public void SplitModel_FirstPartNotNamedAsSplit_IsInvalid()
    {
        string renamed = Path.Combine(_dir, "tiny.gguf");
        File.Copy(FixturePath("tiny-split-00001-of-00003.bin"), renamed);
        var ex = Assert.Throws<GgufFormatException>(() => GgufReader.Read(renamed, "tiny", "tiny.gguf"));
        Assert.Contains("00001-of-00003", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitModel_PartThatIsALink_IsRefused()
    {
        string first = CopySplit();
        string part = Path.Combine(_dir, "tiny-split-00002-of-00003.gguf");
        string elsewhere = Path.Combine(_dir, "elsewhere.bin");
        File.Move(part, elsewhere);
        try
        {
            File.CreateSymbolicLink(part, elsewhere);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("Creating symbolic links needs privileges on this machine.");
        }

        var error = Assert.Throws<GgufFormatException>(() => GgufReader.Read(first, "tiny", "x"));
        Assert.Contains("link", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("model-00001-of-00003.gguf", 1, 3)]
    [InlineData("Model-00002-of-00010.GGUF", 2, 10)]
    public void ParseSplitName_FindsTheNumbering(string name, int number, int count) =>
        Assert.Equal((number, count), GgufReader.ParseSplitName(name));

    [Theory]
    [InlineData("model.gguf")]
    [InlineData("model-1-of-3.gguf")]
    [InlineData("model-00001-of-00003.bin")]
    public void ParseSplitName_IgnoresOtherNames(string name) => Assert.Null(GgufReader.ParseSplitName(name));

    // ---------------------------------------------------------------- hostile input

    [Fact]
    public void Truncation_AnywhereInTheHeader_IsOnlyAFormatException()
    {
        byte[] header = File.ReadAllBytes(FixturePath("qwen3-embedding-4b-q8_0.header.bin"));
        const int tensorTableEnd = 3_058_729; // independent parse; the rest is alignment padding
        var cuts = new List<int>();
        cuts.AddRange(Enumerable.Range(0, 2048));
        var random = new Random(209);
        cuts.AddRange(Enumerable.Range(0, 60).Select(_ => random.Next(0, tensorTableEnd)));
        cuts.AddRange(Enumerable.Range(tensorTableEnd - 200, 200));
        foreach (int cut in cuts)
        {
            Assert.Throws<GgufFormatException>(() => Parse(header, cut));
        }

        Assert.Equal(Qwen3Embedding.TensorBytes, Parse(header, tensorTableEnd).TensorBytes);
    }

    [Fact]
    public void Fuzz_ByteFlips_OnlyFormatExceptionsOrValidSummaries()
    {
        var random = new Random(20_9);
        byte[] tiny = File.ReadAllBytes(FixturePath("tiny-split-00001-of-00003.bin"));
        int ok = 0;
        for (int i = 0; i < 4000; i++)
        {
            byte[] copy = Mutate(tiny, random);
            ok += ParseOrFormatError(copy) ? 1 : 0;
        }

        byte[] real = File.ReadAllBytes(FixturePath("qwen35-planner-q8_0.header.bin"));
        for (int i = 0; i < 60; i++)
        {
            byte[] copy = (byte[])real.Clone();
            for (int flips = random.Next(1, 4); flips > 0; flips--)
            {
                // Bias towards the header's start, where the counts, metadata keys and types are.
                int at = random.Next(0, 3) == 0 ? random.Next(0, copy.Length) : random.Next(0, 4096);
                copy[at] = (byte)random.Next(256);
            }

            ok += ParseOrFormatError(copy) ? 1 : 0;
        }

        Assert.InRange(ok, 1, 4059); // some mutations stay valid (a name byte), most break a rule
    }

    [Fact]
    public void Allocation_RealHeaderReadStaysFarBelowTheLimit()
    {
        string path = FixturePath("qwen35-planner-q8_0.header.bin");
        GgufReader.Read(path, "f", "model.gguf"); // warm up (JIT, regex)
        long before = GC.GetAllocatedBytesForCurrentThread();
        GgufReader.Read(path, "f", "model.gguf");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 1024 * 1024); // 5 MB header with 744 k strings: no per-string allocation
        Assert.True(allocated < GgufLimits.MaxAllocationBytes);
    }

    [Fact]
    public void Allocation_HostileLengthClaimsAllocateNothingLarge()
    {
        // 16 MiB key-value string claims, a 4 M-element string array of empty strings and long keys: all skipped unread.
        var w = Writer.Header(tensors: 0, kvs: 5);
        w.Str("general.architecture").U32(8).Str("llama");
        w.Str("llama.attention.head_count_kv").U32(9).U32(0).U64(4_000_000).Raw(new byte[4_000_000]); // a kept integer array
        w.Str(new string('k', 4000)).U32(8).Str(new string('v', 1_000_000));
        w.Str("llama.block_count").U32(4).U32(2);
        w.Str("tokenizer.ggml.tokens").U32(9).U32(8).U64(1_000_000);
        for (int i = 0; i < 1_000_000; i++)
        {
            w.U64(0);
        }

        string path = Path.Combine(_dir, "hostile.gguf");
        File.WriteAllBytes(path, w.ToArray());
        GgufReader.Read(path, "f", "x"); // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        var summary = GgufReader.Read(path, "f", "x");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(1_000_000, summary.VocabSize);
        Assert.InRange(allocated, 0, 1024 * 1024);
    }

    public static TheoryData<string> HostileHeaders() =>
    [
        "bad-magic", "version-1", "version-4", "big-endian", "tensor-count", "metadata-count", "string-too-long",
        "string-past-end", "array-too-long", "array-past-end", "nested-array", "unknown-kv-type", "unknown-array-type",
        "bool-byte", "duplicate-kept-key", "duplicate-architecture", "architecture-not-string", "architecture-too-long",
        "architecture-not-utf8", "no-architecture", "no-block-count", "block-count-zero", "block-count-string",
        "block-count-u64-huge", "dims-zero", "dims-too-many", "dim-huge", "dims-overflow", "unknown-tensor-type",
        "block-size-mismatch", "offset-misaligned", "offset-gap", "alignment-not-power-of-two", "split-count-huge",
        "embedding-length-negative",
    ];

    [Theory]
    [MemberData(nameof(HostileHeaders))]
    public void HostileHeader_IsAFormatException(string kind)
    {
        byte[] bytes = Hostile(kind);
        Assert.Throws<GgufFormatException>(() => Parse(bytes));
    }

    [Fact]
    public void MinimalHeader_Parses()
    {
        // The positive control of the hostile set: the same builder with nothing broken.
        var summary = Parse(Hostile("valid"));
        Assert.Equal("llama", summary.Architecture);
        Assert.Equal(2, summary.BlockCount);
        Assert.Equal(64 * 64 * 2, summary.TensorBytes);
    }

    [Fact]
    public void HeadCountKvPerLayerArray_TakesTheLargest()
    {
        var w = Writer.Header(tensors: 0, kvs: 3);
        w.Str("general.architecture").U32(8).Str("llama");
        w.Str("llama.block_count").U32(4).U32(3);
        w.Str("llama.attention.head_count_kv").U32(9).U32(5).U64(3).U32(0).U32(4).U32(2);
        Assert.Equal(4, Parse(w.ToArray()).HeadCountKv);
    }

    [Fact]
    public void CausalAttentionFalse_IsReported()
    {
        var w = Writer.Header(tensors: 0, kvs: 3);
        w.Str("llama.attention.causal").U32(7).U8(0);
        w.Str("general.architecture").U32(8).Str("llama"); // after its keys: the key order is not fixed
        w.Str("llama.block_count").U32(4).U32(3);
        var summary = Parse(w.ToArray());
        Assert.False(summary.CausalAttention);
    }

    [Theory]
    [InlineData(15L, "Q4_K_M")]
    [InlineData(1L, "F16")]
    [InlineData(32L, "BF16")]
    [InlineData(99L, "ftype-99")]
    public void FileTypeNames(long value, string name) => Assert.Equal(name, GgufSummaryBuilder.FileTypeName(value));

    // ---------------------------------------------------------------- helpers

    private static GgufSummary Parse(byte[] bytes, int? length = null)
    {
        using var stream = new MemoryStream(bytes, 0, length ?? bytes.Length, writable: false);
        return GgufSummaryBuilder.Build("f", "x.gguf", [GgufReader.ReadHeader(stream)]);
    }

    private static bool ParseOrFormatError(byte[] bytes)
    {
        try
        {
            Parse(bytes);
            return true;
        }
        catch (GgufFormatException)
        {
            return false;
        }
    }

    private static byte[] Mutate(byte[] source, Random random)
    {
        byte[] copy = (byte[])source.Clone();
        switch (random.Next(4))
        {
            case 0:
                copy[random.Next(copy.Length)] = (byte)random.Next(256);
                break;
            case 1:
                for (int i = random.Next(1, 8); i > 0; i--)
                {
                    copy[random.Next(copy.Length)] ^= (byte)(1 << random.Next(8));
                }

                break;
            case 2:
                // A length or count field set to an extreme value.
                int at = random.Next(copy.Length - 8);
                BitConverter.TryWriteBytes(copy.AsSpan(at, 8), random.Next(2) == 0 ? ulong.MaxValue : (ulong)random.NextInt64());
                break;
            default:
                return copy.AsSpan(0, random.Next(copy.Length)).ToArray();
        }

        return copy;
    }

    private string CopySplit()
    {
        for (int k = 1; k <= 3; k++)
        {
            string name = $"tiny-split-0000{k}-of-00003";
            File.Copy(FixturePath(name + ".bin"), Path.Combine(_dir, name + ".gguf"), overwrite: true);
        }

        return Path.Combine(_dir, "tiny-split-00001-of-00003.gguf");
    }

    private static byte[] Hostile(string kind)
    {
        uint version = kind switch { "version-1" => 1, "version-4" => 4, "big-endian" => 0x03000000, _ => 3 };
        ulong tensorCount = kind switch { "tensor-count" => (ulong)GgufLimits.MaxTensorCount + 1, _ => 1 };
        var w = new Writer();
        w.Raw(kind == "bad-magic" ? "GGUG"u8 : "GGUF"u8).U32(version).U64(tensorCount);
        var kvs = new Writer();
        int count = 0;
        void Kv(Action<Writer> write)
        {
            write(kvs);
            count++;
        }

        if (kind != "no-architecture")
        {
            Kv(k => k.Str("general.architecture").U32(kind == "architecture-not-string" ? 4u : 8u));
            if (kind == "architecture-not-string")
            {
                kvs.U32(1);
            }
            else if (kind == "architecture-too-long")
            {
                kvs.Str(new string('a', 129));
            }
            else if (kind == "architecture-not-utf8")
            {
                kvs.U64(2).Raw([0xC3, 0x28]);
            }
            else
            {
                kvs.Str("llama");
            }
        }

        if (kind == "duplicate-architecture")
        {
            Kv(k => k.Str("general.architecture").U32(8).Str("llama"));
        }

        switch (kind)
        {
            case "no-block-count":
                break;
            case "block-count-zero":
                Kv(k => k.Str("llama.block_count").U32(4).U32(0));
                break;
            case "block-count-string":
                Kv(k => k.Str("llama.block_count").U32(8).Str("2"));
                break;
            case "block-count-u64-huge":
                Kv(k => k.Str("llama.block_count").U32(10).U64(ulong.MaxValue));
                break;
            default:
                Kv(k => k.Str("llama.block_count").U32(4).U32(2));
                break;
        }

        switch (kind)
        {
            case "string-too-long":
                Kv(k => k.Str("general.name").U32(8).U64((ulong)GgufLimits.MaxStringBytes + 1));
                break;
            case "string-past-end":
                Kv(k => k.Str("general.description").U32(8).U64(1L << 20));
                break;
            case "array-too-long":
                Kv(k => k.Str("tokenizer.ggml.scores").U32(9).U32(6).U64((ulong)GgufLimits.MaxArrayLength + 1));
                break;
            case "array-past-end":
                Kv(k => k.Str("tokenizer.ggml.scores").U32(9).U32(6).U64(1_000_000));
                break;
            case "nested-array":
                Kv(k => k.Str("x.nested").U32(9).U32(9).U64(1));
                break;
            case "unknown-kv-type":
                Kv(k => k.Str("x.unknown").U32(13).U64(0));
                break;
            case "unknown-array-type":
                Kv(k => k.Str("x.unknown").U32(9).U32(42).U64(1));
                break;
            case "bool-byte":
                Kv(k => k.Str("tokenizer.ggml.add_eos_token").U32(7).U8(2));
                break;
            case "duplicate-kept-key":
                Kv(k => k.Str("llama.embedding_length").U32(4).U32(64));
                Kv(k => k.Str("llama.embedding_length").U32(4).U32(64));
                break;
            case "alignment-not-power-of-two":
                Kv(k => k.Str("general.alignment").U32(4).U32(24));
                break;
            case "split-count-huge":
                Kv(k => k.Str("split.count").U32(2).U16(GgufLimits.MaxSplitCount + 1));
                break;
            case "embedding-length-negative":
                Kv(k => k.Str("llama.embedding_length").U32(5).U32(unchecked((uint)-5)));
                break;
            case "metadata-count":
                break;
        }

        w.U64(kind == "metadata-count" ? (ulong)GgufLimits.MaxMetadataCount + 1 : (ulong)count).Raw(kvs.ToArray());

        // One tensor: blk.0.attn_q.weight, F16 64×64 at offset 0, or the broken variant.
        w.Str("blk.0.attn_q.weight");
        switch (kind)
        {
            case "dims-zero":
                w.U32(0);
                break;
            case "dims-too-many":
                w.U32((uint)GgufLimits.MaxTensorDimensions + 1);
                break;
            case "dim-huge":
                w.U32(2).U64(ulong.MaxValue).U64(1);
                break;
            case "dims-overflow":
                w.U32(3).U64(1UL << 40).U64(1UL << 40).U64(1UL << 40);
                break;
            default:
                w.U32(2).U64(64).U64(64);
                break;
        }

        if (kind is not ("dims-zero" or "dims-too-many"))
        {
            w.U32(kind switch { "unknown-tensor-type" => 31u, "block-size-mismatch" => 8u, _ => 1u }); // 8 = Q8_0 needs ne0 % 32 == 0
            w.U64(kind switch { "offset-misaligned" => 7u, "offset-gap" => 64u, _ => 0u });
        }

        if (kind == "block-size-mismatch")
        {
            // Rewrite the tensor's first dimension to 48 (not a multiple of 32).
            return Rewrite(w.ToArray(), "blk.0.attn_q.weight", 48);
        }

        w.Pad(32);
        return w.ToArray();
    }

    private static byte[] Rewrite(byte[] bytes, string tensorName, ulong firstDim)
    {
        byte[] name = Encoding.UTF8.GetBytes(tensorName);
        int at = bytes.AsSpan().LastIndexOf(name) + name.Length + 4; // after the name and n_dims
        BitConverter.TryWriteBytes(bytes.AsSpan(at, 8), firstDim);
        return bytes;
    }

    /// <summary>A little-endian GGUF byte builder for hand-made headers.</summary>
    internal sealed class Writer
    {
        private readonly List<byte> _bytes = [];

        public static Writer Header(ulong tensors, ulong kvs) => new Writer().Raw("GGUF"u8).U32(3).U64(tensors).U64(kvs);

        public Writer Raw(ReadOnlySpan<byte> bytes)
        {
            _bytes.AddRange(bytes);
            return this;
        }

        public Writer U8(byte value) => Raw([value]);

        public Writer U16(int value) => Raw(BitConverter.GetBytes((ushort)value));

        public Writer U32(uint value) => Raw(BitConverter.GetBytes(value));

        public Writer U32(int value) => U32((uint)value);

        public Writer U64(ulong value) => Raw(BitConverter.GetBytes(value));

        public Writer U64(long value) => U64((ulong)value);

        public Writer Str(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            return U64((ulong)bytes.Length).Raw(bytes);
        }

        public Writer Pad(int alignment)
        {
            while (_bytes.Count % alignment != 0)
            {
                _bytes.Add(0);
            }

            return this;
        }

        public byte[] ToArray() => [.. _bytes];
    }
}

/// <summary>
/// MN-209: <c>GET /model-folders/{folder}/gguf?file=</c> over an in-process host (the route's handler with the real
/// reader and models-root confinement), and the <c>gguf.inspect</c> contributor.
/// </summary>
public sealed class GgufEndpointsTests : IAsyncDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mirbadllm-gguf-ep-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _outside;
    private WebApplication? _app;

    public GgufEndpointsTests()
    {
        _root = Path.Combine(_base, "models");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private async Task<HttpClient> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IGgufReader, GgufReader>();
        builder.Services.AddSingleton(new ModelRootPaths(_root));
        _app = builder.Build();
        _app.MapGgufEndpoints();
        await _app.StartAsync();
        return _app.GetTestClient();
    }

    private string Folder(string name, params string[] fixtures)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        foreach (string pair in fixtures)
        {
            string[] parts = pair.Split('=');
            File.Copy(GgufReaderTests.FixturePath(parts[1]), Path.Combine(dir, parts[0]));
        }

        return dir;
    }

    private static string Url(string folder, string? file = null) =>
        Protocol.BasePath + AgentRoutes.GgufInspect.Expand(folder) + (file is null ? string.Empty : "?file=" + Uri.EscapeDataString(file));

    private static async Task<string> ErrorCode(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task Inspect_NamedFile_AnswersTheSummary()
    {
        Folder("Qwen3-Embedding-4B-GGUF", "Qwen3-Embedding-4B-Q8_0.gguf=qwen3-embedding-4b-q8_0.header.bin");
        var client = await StartAsync();
        var response = await client.GetAsync(Url("Qwen3-Embedding-4B-GGUF", "Qwen3-Embedding-4B-Q8_0.gguf"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = JsonSerializer.Deserialize<GgufSummary>(await response.Content.ReadAsStringAsync(), AgentJson.Options);
        Assert.Equal(GgufReaderTests.Qwen3Embedding, summary);
    }

    [Fact]
    public async Task Inspect_WithoutFile_ReadsTheOnlyModelGguf_IgnoringProjectorsAndLaterParts()
    {
        Folder("split", "tiny-split-00001-of-00003.gguf=tiny-split-00001-of-00003.bin",
            "tiny-split-00002-of-00003.gguf=tiny-split-00002-of-00003.bin", "tiny-split-00003-of-00003.gguf=tiny-split-00003-of-00003.bin",
            "mmproj-F16.gguf=tiny-split-00002-of-00003.bin");
        var client = await StartAsync();
        var response = await client.GetAsync(Url("split"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = JsonSerializer.Deserialize<GgufSummary>(await response.Content.ReadAsStringAsync(), AgentJson.Options)!;
        Assert.Equal("tiny-split-00001-of-00003.gguf", summary.File);
        Assert.Equal(3, summary.SplitCount);
    }

    [Fact]
    public async Task Inspect_WithoutFile_SeveralModels_Is400ListingThem()
    {
        Folder("two", "a.gguf=tiny-split-00002-of-00003.bin", "b.gguf=tiny-split-00002-of-00003.bin");
        var client = await StartAsync();
        var response = await client.GetAsync(Url("two"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("a.gguf, b.gguf", body, StringComparison.Ordinal);

        // The names also travel as structured details, so the editor's picker needs no sentence parsing (MN-230, D130).
        var error = JsonSerializer.Deserialize<AgentErrorResponse>(body, AgentJson.Options)!.Error;
        Assert.Equal(["a.gguf", "b.gguf"], error.ReadDetails<GgufChoiceDetails>()!.Candidates);
    }

    [Fact]
    public async Task Inspect_WithoutFile_SeveralModels_ListsNamesTheMessageCannotCarry()
    {
        // A comma in a file name makes the message's own list ambiguous; the details stay exact.
        Folder("commas", "a,b.gguf=tiny-split-00002-of-00003.bin", "c.gguf=tiny-split-00002-of-00003.bin");
        var client = await StartAsync();
        var response = await client.GetAsync(Url("commas"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonSerializer.Deserialize<AgentErrorResponse>(await response.Content.ReadAsStringAsync(), AgentJson.Options)!.Error;
        Assert.Equal(["a,b.gguf", "c.gguf"], error.ReadDetails<GgufChoiceDetails>()!.Candidates);
        Assert.Equal(["b.gguf", "c.gguf"], MirbadLLM.Web.LlamaCppEditing.CandidatesFrom(error.Message)); // what text parsing gave
    }

    [Fact]
    public async Task Inspect_Missing_Is404()
    {
        Folder("empty");
        var client = await StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url("nope"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url("empty"))).StatusCode);
        var missingFile = await client.GetAsync(Url("empty", "model.gguf"));
        Assert.Equal(HttpStatusCode.NotFound, missingFile.StatusCode);
        Assert.Equal(ErrorCodes.NotFound, await ErrorCode(missingFile));
    }

    [Fact]
    public async Task Inspect_NotAGguf_Is422GgufInvalid()
    {
        string dir = Folder("bad");
        await File.WriteAllTextAsync(Path.Combine(dir, "model.gguf"), "this is not a GGUF at all, just text");
        var client = await StartAsync();
        var response = await client.GetAsync(Url("bad", "model.gguf"));
        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal(ErrorCodes.GgufInvalid, await ErrorCode(response));
    }

    [Fact]
    public async Task Inspect_LaterSplitPart_Is400()
    {
        Folder("split", "tiny-split-00002-of-00003.gguf=tiny-split-00002-of-00003.bin");
        var client = await StartAsync();
        var response = await client.GetAsync(Url("split", "tiny-split-00002-of-00003.gguf"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("../model.gguf")]
    [InlineData("..\\model.gguf")]
    [InlineData("sub/model.gguf")]
    [InlineData("C:\\models\\model.gguf")]
    [InlineData("/etc/model.gguf")]
    [InlineData("model.gguf:stream")]
    [InlineData("model.gguf::$DATA")]
    [InlineData("NUL.gguf")]
    [InlineData("com1.gguf")]
    [InlineData("model.gguf.")]
    [InlineData("model.gguf ")]
    [InlineData(".hidden.gguf")]
    [InlineData("model.bin")]
    [InlineData(".gguf")]
    [InlineData("..")]
    public async Task Inspect_BadFileName_Is400(string file)
    {
        Folder("f", "model.gguf=tiny-split-00002-of-00003.bin");
        var ex = await Assert.ThrowsAsync<AgentException>(() =>
            GgufEndpoints.InspectAsync(new GgufReader(), new ModelRootPaths(_root), "f", file));
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(ErrorCodes.InvalidRequest, ex.Error.Code);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("C:")]
    [InlineData(".hidden")]
    public async Task Inspect_BadFolder_Is400(string folder)
    {
        var ex = await Assert.ThrowsAsync<AgentException>(() =>
            GgufEndpoints.InspectAsync(new GgufReader(), new ModelRootPaths(_root), folder, "model.gguf"));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task Inspect_FileLinkEscapingTheRoot_Is400()
    {
        string dir = Folder("f");
        string target = Path.Combine(_outside, "secret.gguf");
        File.Copy(GgufReaderTests.FixturePath("tiny-split-00002-of-00003.bin"), target);
        try
        {
            File.CreateSymbolicLink(Path.Combine(dir, "model.gguf"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("Creating symbolic links needs privileges on this machine.");
        }

        var error = await Assert.ThrowsAsync<AgentException>(() =>
            GgufEndpoints.InspectAsync(new GgufReader(), new ModelRootPaths(_root), "f", "model.gguf"));
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public async Task Inspect_FileLinkThroughALinkedFolderEscapingTheRoot_Is400()
    {
        // MN-219: root/f/model.gguf -> root/hop/model.gguf where root/hop is a directory link to a folder outside the
        // root. The link's final target reads as inside the root, so the textual check passed and the header outside was
        // read (a 200 summary before the fix).
        string dir = Folder("f");
        File.Copy(GgufReaderTests.FixturePath("qwen3-embedding-4b-q8_0.header.bin"), Path.Combine(_outside, "model.gguf"));
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "hop"), _outside);
            File.CreateSymbolicLink(Path.Combine(dir, "model.gguf"), Path.Combine(_root, "hop", "model.gguf"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("Creating symbolic links needs privileges on this machine.");
        }

        var error = await Assert.ThrowsAsync<AgentException>(() =>
            GgufEndpoints.InspectAsync(new GgufReader(), new ModelRootPaths(_root), "f", "model.gguf"));
        Assert.Equal(400, error.StatusCode);
        Assert.Contains("link", error.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspect_FolderLinkEscapingTheRoot_Is400()
    {
        File.Copy(GgufReaderTests.FixturePath("tiny-split-00002-of-00003.bin"), Path.Combine(_outside, "model.gguf"));
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "escape"), _outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("Creating symbolic links needs privileges on this machine.");
        }

        var error = await Assert.ThrowsAsync<AgentException>(() =>
            GgufEndpoints.InspectAsync(new GgufReader(), new ModelRootPaths(_root), "escape", "model.gguf"));
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public async Task Inspect_WithoutAReader_Is501()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.MapGgufEndpoints();
        await _app.StartAsync();
        var response = await _app.GetTestClient().GetAsync(Url("f", "model.gguf"));
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task Contributor_AddsGgufInspectOnce_AndKeepsTheRest()
    {
        var contributor = new GgufCapabilityContributor(new GgufReader());
        var current = new NodeCapabilities
        {
            Protocol = Protocol.Range,
            AgentVersion = "test",
            Os = new NodeOsInfo("windows", "Windows 11"),
            Arch = "x64",
            Memory = new NodeMemoryInfo(MemoryModel.Discrete, 64),
            Features = [AgentFeatures.TelemetryV1],
        };

        var once = await contributor.ContributeAsync(current);
        var twice = await contributor.ContributeAsync(once);
        Assert.Equal([AgentFeatures.TelemetryV1, AgentFeatures.GgufInspect], once.Features);
        Assert.Equal(once.Features, twice.Features);
    }
}
