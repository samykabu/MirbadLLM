using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.NodeRuntime.Gguf;

/// <summary>
/// The pure .NET GGUF v2/v3 header reader (design-p3p4 §A.1.10, §A.8; MN-209, no package). It reads the metadata and the
/// tensor table of a GGUF and of its split parts (<c>{prefix}-0000k-of-0000N.gguf</c>, llama.cpp's
/// <c>llama_split_path</c> naming) and never the tensor data, so a header-only copy of a file reads exactly like the file.
/// <para>Hostile input: every length and count read from the file is checked against <see cref="GgufLimits"/> and against
/// the bytes left in the file <b>before</b> anything is read or skipped; metadata values are skipped, not materialised
/// (only the ~25 keys the summary needs are kept, compared as UTF-8 bytes against a stack buffer); arithmetic is checked.
/// Every malformed or out-of-bound header is a <see cref="GgufFormatException"/>, and one read allocates a few KiB plus the
/// stream buffer, far below <see cref="GgufLimits.MaxAllocationBytes"/>.</para>
/// <para>Rules taken from ggml's <c>gguf.cpp</c>: magic <c>GGUF</c>, version 2 or 3 (little-endian; a big-endian file shows
/// up as a huge version and is refused), no nested arrays, no duplicate kept keys, every tensor type known with
/// <c>ne[0]</c> a multiple of its block size, tensor offsets aligned to <c>general.alignment</c> (default 32, a power of
/// two) and contiguous. Tensor bytes are <c>ggml_nbytes</c> summed from the table (what llama.cpp reports as the model
/// size), not file sizes.</para>
/// <para>Split parts: the part named by the caller must be the first (<c>split.no = 0</c>); parts 2..N are found next to
/// it, must be plain files (a link is refused: the caller confined only the first part), must carry the same
/// <c>split.count</c> and their own <c>split.no</c>, and the tensors of all parts must add up to the first part's
/// <c>split.tensors.count</c> when it is present.</para>
/// </summary>
public sealed partial class GgufReader : IGgufReader
{
    /// <summary>Default <c>general.alignment</c>.</summary>
    internal const int DefaultAlignment = 32;

    /// <summary>Longest metadata key the reader compares; longer keys cannot be one it keeps and are skipped unread.</summary>
    internal const int MaxComparedKeyBytes = 128;

    /// <summary>Longest <c>general.architecture</c> accepted (it is used to build key names).</summary>
    internal const int MaxArchitectureBytes = 128;

    /// <summary>Bytes of <c>general.name</c> kept for the summary; the rest is skipped.</summary>
    internal const int MaxNameBytes = 1024;

    private const int StreamBufferBytes = 64 * 1024;

    /// <inheritdoc />
    public Task<GgufSummary> ReadAsync(string path, string folder, string file, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Read(path, folder, file, ct));
    }

    /// <summary>Synchronous <see cref="ReadAsync"/> (a header read is a few milliseconds of buffered file I/O).</summary>
    /// <exception cref="GgufFormatException">The file or a part is not a valid GGUF v2/v3 or breaks a bound.</exception>
    public static GgufSummary Read(string path, string folder, string file, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(file);

        GgufHeader first = ReadFile(path, ct);
        var parts = new List<GgufHeader> { first };
        int splitCount = first.SplitCount;
        if (splitCount > 1)
        {
            if (first.SplitNumber != 0)
            {
                throw new GgufFormatException(string.Create(CultureInfo.InvariantCulture,
                    $"'{Path.GetFileName(path)}' is part {first.SplitNumber + 1} of {splitCount}; inspect the first part."));
            }

            for (int number = 2; number <= splitCount; number++)
            {
                ct.ThrowIfCancellationRequested();
                string partPath = SplitPartPath(path, number, splitCount);
                parts.Add(ReadSplitPart(partPath, number, splitCount, ct));
            }

            long tensors = parts.Sum(p => p.TensorCount);
            if (first.SplitTensorCount is { } declared && declared != tensors)
            {
                throw new GgufFormatException(string.Create(CultureInfo.InvariantCulture,
                    $"split.tensors.count is {declared} but the {splitCount} parts hold {tensors} tensors."));
            }
        }

        try
        {
            return GgufSummaryBuilder.Build(folder, file, parts);
        }
        catch (OverflowException ex)
        {
            // Each part's sizes fit, their sum does not (a hostile split set; MN-219): only GgufFormatException escapes.
            throw new GgufFormatException("The split parts' sizes overflow when summed.", ex);
        }
    }

    /// <summary>
    /// The path of split part <paramref name="number"/> of <paramref name="count"/> next to <paramref name="firstPart"/>,
    /// whose name must be <c>{prefix}-00001-of-{count:D5}.gguf</c> (llama.cpp <c>llama_split_path</c>).
    /// </summary>
    internal static string SplitPartPath(string firstPart, int number, int count)
    {
        string name = Path.GetFileName(firstPart);
        Match match = SplitNameRegex().Match(name);
        if (!match.Success || int.Parse(match.Groups["k"].Value, CultureInfo.InvariantCulture) != 1
            || int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture) != count)
        {
            throw new GgufFormatException(string.Create(CultureInfo.InvariantCulture,
                $"'{name}' declares {count} split parts but is not named '<prefix>-00001-of-{count:D5}.gguf'."));
        }

        string partName = string.Create(CultureInfo.InvariantCulture,
            $"{match.Groups["prefix"].Value}-{number:D5}-of-{count:D5}{match.Groups["ext"].Value}");
        return Path.Combine(Path.GetDirectoryName(firstPart) ?? string.Empty, partName);
    }

    /// <summary>
    /// Parses the split numbering of a file name: <c>(k, n)</c> for <c>{prefix}-0000k-of-0000N.gguf</c>, null otherwise.
    /// </summary>
    public static (int Number, int Count)? ParseSplitName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        Match match = SplitNameRegex().Match(fileName);
        return match.Success
            ? (int.Parse(match.Groups["k"].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture))
            : null;
    }

    private static GgufHeader ReadSplitPart(string partPath, int number, int count, CancellationToken ct)
    {
        var info = new FileInfo(partPath);
        bool isLink;
        try
        {
            isLink = info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        catch (IOException ex)
        {
            throw new GgufFormatException($"Split part '{info.Name}' cannot be inspected: {ex.Message}", ex);
        }

        if (isLink)
        {
            throw new GgufFormatException($"Split part '{info.Name}' is a link; split parts must be plain files next to the first part.");
        }

        if (!info.Exists)
        {
            throw new GgufFormatException(string.Create(CultureInfo.InvariantCulture,
                $"Split part {number} of {count} ('{info.Name}') is missing."));
        }

        GgufHeader part = ReadFile(partPath, ct);
        if (part.SplitCount != count || part.SplitNumber != number - 1)
        {
            throw new GgufFormatException(string.Create(CultureInfo.InvariantCulture,
                $"'{info.Name}' says it is part {part.SplitNumber + 1} of {part.SplitCount}, expected part {number} of {count}."));
        }

        return part;
    }

    private static GgufHeader ReadFile(string path, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferBytes, FileOptions.SequentialScan);
        return ReadHeader(stream, ct);
    }

    /// <summary>
    /// Reads one GGUF header (metadata and tensor table) from a seekable stream positioned at the magic.
    /// <see cref="GgufHeader.FileBytes"/> is the stream length.
    /// </summary>
    /// <exception cref="GgufFormatException">The header is malformed or breaks a bound.</exception>
    internal static GgufHeader ReadHeader(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new ArgumentException("The GGUF stream must be readable and seekable.", nameof(stream));
        }

        try
        {
            var reader = new BoundedReader(stream);
            return Parse(reader, ct);
        }
        catch (OverflowException ex)
        {
            throw new GgufFormatException("A size or count in the GGUF header overflows.", ex);
        }
        catch (EndOfStreamException ex)
        {
            throw new GgufFormatException("The GGUF header is truncated.", ex);
        }
    }

    private static GgufHeader Parse(BoundedReader reader, CancellationToken ct)
    {
        if (reader.Remaining < 24)
        {
            throw new GgufFormatException("The file is too short to be a GGUF.");
        }

        uint magic = reader.ReadUInt32();
        if (magic != GgufLimits.Magic)
        {
            throw new GgufFormatException("The file does not start with the GGUF magic.");
        }

        uint version = reader.ReadUInt32();
        if (version is not (2 or 3))
        {
            throw new GgufFormatException(version is 1
                ? "GGUF version 1 is not supported (only 2 and 3)."
                : string.Create(CultureInfo.InvariantCulture,
                    $"GGUF version {version} is not supported (only 2 and 3, little-endian)."));
        }

        long tensorCount = ReadCount(reader, GgufLimits.MaxTensorCount, "tensor count", minBytesEach: 24);
        long metadataCount = ReadCount(reader, GgufLimits.MaxMetadataCount, "metadata count", minBytesEach: 12);
        long metadataStart = reader.Position;

        // Pass 1: the architecture (it names the keys pass 2 keeps; GGUF does not fix the key order).
        string? architecture = null;
        Span<byte> keyBuffer = stackalloc byte[MaxComparedKeyBytes];
        for (long i = 0; i < metadataCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> key = ReadKey(reader, keyBuffer);
            var type = (GgufValueType)reader.ReadUInt32();
            if (key.SequenceEqual(GgufKeys.ArchitectureUtf8))
            {
                if (type != GgufValueType.String)
                {
                    throw new GgufFormatException("general.architecture is not a string.");
                }

                if (architecture is not null)
                {
                    throw new GgufFormatException("general.architecture appears twice.");
                }

                architecture = ReadArchitecture(reader);
            }
            else
            {
                SkipValue(reader, type);
            }
        }

        // Pass 2: the kept keys (general.*, split.*, tokenizer.* and {arch}.*).
        reader.Seek(metadataStart);
        var wanted = GgufKeys.Wanted(architecture);
        var values = new Dictionary<string, GgufValue>(StringComparer.Ordinal);
        for (long i = 0; i < metadataCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> key = ReadKey(reader, keyBuffer);
            var type = (GgufValueType)reader.ReadUInt32();
            string? name = Match(wanted, key);
            if (name is null)
            {
                SkipValue(reader, type);
                continue;
            }

            if (values.ContainsKey(name))
            {
                throw new GgufFormatException($"Metadata key '{name}' appears twice.");
            }

            values[name] = ReadKeptValue(reader, type, name);
        }

        int alignment = DefaultAlignment;
        if (values.TryGetValue(GgufKeys.Alignment, out var alignValue))
        {
            long a = alignValue.AsInteger(GgufKeys.Alignment);
            if (a <= 0 || a > 1 << 20 || (a & (a - 1)) != 0)
            {
                throw new GgufFormatException(string.Create(CultureInfo.InvariantCulture,
                    $"general.alignment {a} is not a power of two up to 1 MiB."));
            }

            alignment = (int)a;
        }

        // Tensor table.
        if (tensorCount * 24 > reader.Remaining)
        {
            throw new GgufFormatException("The tensor table is longer than the file.");
        }

        long tensorBytes = 0;
        long parameters = 0;
        long expectedOffset = 0;
        Span<long> dims = stackalloc long[GgufLimits.MaxTensorDimensions];
        for (long i = 0; i < tensorCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            SkipString(reader, "tensor name");
            uint dimensions = reader.ReadUInt32();
            if (dimensions is 0 || dimensions > GgufLimits.MaxTensorDimensions)
            {
                throw new GgufFormatException(string.Create(CultureInfo.InvariantCulture,
                    $"Tensor {i} has {dimensions} dimensions (1..{GgufLimits.MaxTensorDimensions} allowed)."));
            }

            long elements = 1;
            for (int d = 0; d < dimensions; d++)
            {
                ulong ne = reader.ReadUInt64();
                if (ne > long.MaxValue)
                {
                    throw new GgufFormatException(string.Create(CultureInfo.InvariantCulture, $"Tensor {i} has a dimension of {ne}."));
                }

                dims[d] = (long)ne;
                elements = checked(elements * (long)ne);
            }

            uint typeId = reader.ReadUInt32();
            if (!GgmlTypes.TryGet(typeId, out var ggmlType))
            {
                throw new GgufFormatException(string.Create(CultureInfo.InvariantCulture,
                    $"Tensor {i} has the unknown ggml type {typeId}."));
            }

            if (dims[0] % ggmlType.BlockSize != 0)
            {
                throw new GgufFormatException(string.Create(CultureInfo.InvariantCulture,
                    $"Tensor {i}: its first dimension {dims[0]} is not a multiple of the {ggmlType.Name} block size {ggmlType.BlockSize}."));
            }

            ulong offset = reader.ReadUInt64();
            if (offset != (ulong)expectedOffset)
            {
                throw new GgufFormatException(string.Create(CultureInfo.InvariantCulture,
                    $"Tensor {i} has data offset {offset}, expected {expectedOffset} (offsets must be aligned and contiguous)."));
            }

            long nbytes = checked(elements / ggmlType.BlockSize * ggmlType.TypeSize);
            tensorBytes = checked(tensorBytes + nbytes);
            parameters = checked(parameters + elements);
            expectedOffset = checked(Pad(checked(expectedOffset + nbytes), alignment));
        }

        return new GgufHeader
        {
            SplitCount = values.TryGetValue(GgufKeys.SplitCount, out var count) ? SplitValue(count, GgufKeys.SplitCount, 1, GgufLimits.MaxSplitCount) : 1,
            SplitNumber = values.TryGetValue(GgufKeys.SplitNumber, out var number) ? SplitValue(number, GgufKeys.SplitNumber, 0, GgufLimits.MaxSplitCount - 1) : 0,
            SplitTensorCount = values.TryGetValue(GgufKeys.SplitTensorsCount, out var tensors) ? tensors.AsInteger(GgufKeys.SplitTensorsCount) : null,
            Version = (int)version,
            Architecture = architecture,
            Values = values,
            TensorCount = tensorCount,
            TensorBytes = tensorBytes,
            ParameterCount = parameters,
            DataOffset = Pad(reader.Position, alignment),
            FileBytes = reader.Length,
        };
    }

    /// <summary>A split value in <c>min..max</c>; <c>split.count = 0</c> counts as a single file.</summary>
    private static int SplitValue(GgufValue value, string key, int min, int max)
    {
        long n = value.AsInteger(key);
        if (key == GgufKeys.SplitCount && n == 0)
        {
            return 1;
        }

        if (n < min || n > max)
        {
            throw new GgufFormatException(string.Create(CultureInfo.InvariantCulture, $"'{key}' is {n} ({min}..{max} allowed)."));
        }

        return (int)n;
    }

    private static long Pad(long value, int alignment) => checked((value + alignment - 1) / alignment * alignment);

    private static long ReadCount(BoundedReader reader, long max, string what, int minBytesEach)
    {
        ulong count = reader.ReadUInt64();
        if (count > (ulong)max)
        {
            throw new GgufFormatException(string.Create(CultureInfo.InvariantCulture, $"The {what} {count} exceeds {max}."));
        }

        if ((long)count * minBytesEach > reader.Remaining)
        {
            throw new GgufFormatException(string.Create(CultureInfo.InvariantCulture, $"The {what} {count} cannot fit in the file."));
        }

        return (long)count;
    }

    private static long ReadStringLength(BoundedReader reader, string what)
    {
        ulong length = reader.ReadUInt64();
        if (length > GgufLimits.MaxStringBytes)
        {
            throw new GgufFormatException(string.Create(CultureInfo.InvariantCulture,
                $"A {what} of {length} bytes exceeds {GgufLimits.MaxStringBytes}."));
        }

        if ((long)length > reader.Remaining)
        {
            throw new GgufFormatException(string.Create(CultureInfo.InvariantCulture, $"A {what} of {length} bytes runs past the end of the file."));
        }

        return (long)length;
    }

    private static void SkipString(BoundedReader reader, string what) => reader.Skip(ReadStringLength(reader, what));

    /// <summary>Reads a key into <paramref name="buffer"/>; a key longer than the buffer is skipped and returned empty.</summary>
    private static ReadOnlySpan<byte> ReadKey(BoundedReader reader, Span<byte> buffer)
    {
        long length = ReadStringLength(reader, "metadata key");
        if (length > buffer.Length)
        {
            reader.Skip(length);
            return [];
        }

        Span<byte> key = buffer[..(int)length];
        reader.ReadExactly(key);
        return key;
    }

    private static string ReadArchitecture(BoundedReader reader)
    {
        long length = ReadStringLength(reader, "general.architecture");
        if (length is 0 || length > MaxArchitectureBytes)
        {
            throw new GgufFormatException(string.Create(CultureInfo.InvariantCulture,
                $"general.architecture has {length} bytes (1..{MaxArchitectureBytes} allowed)."));
        }

        Span<byte> bytes = stackalloc byte[(int)length];
        reader.ReadExactly(bytes);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new GgufFormatException("general.architecture is not valid UTF-8.", ex);
        }
    }

    private static string? Match(IReadOnlyList<(byte[] Utf8, string Name)> wanted, ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
        {
            return null;
        }

        for (int i = 0; i < wanted.Count; i++)
        {
            if (key.SequenceEqual(wanted[i].Utf8))
            {
                return wanted[i].Name;
            }
        }

        return null;
    }

    private static GgufValue ReadKeptValue(BoundedReader reader, GgufValueType type, string key)
    {
        if (IsInteger(type))
        {
            return ReadInteger(reader, type, out long value) ? GgufValue.Integer(value) : GgufValue.OutOfRange;
        }

        switch (type)
        {
            case GgufValueType.Float32: reader.Skip(4); return GgufValue.Other(type);
            case GgufValueType.Float64: reader.Skip(8); return GgufValue.Other(type);
            case GgufValueType.Bool: return GgufValue.Boolean(ReadBool(reader, key));
            case GgufValueType.String: return GgufValue.Text(ReadKeptString(reader, key));
            case GgufValueType.Array: return ReadKeptArray(reader, key);
            default: throw UnknownType(type);
        }
    }

    /// <summary>Reads an integer of <paramref name="type"/>; false for a <c>uint64</c> above <see cref="long.MaxValue"/>.</summary>
    private static bool ReadInteger(BoundedReader reader, GgufValueType type, out long value)
    {
        switch (type)
        {
            case GgufValueType.UInt8: value = reader.ReadByte(); return true;
            case GgufValueType.Int8: value = (sbyte)reader.ReadByte(); return true;
            case GgufValueType.UInt16: value = reader.ReadUInt16(); return true;
            case GgufValueType.Int16: value = (short)reader.ReadUInt16(); return true;
            case GgufValueType.UInt32: value = reader.ReadUInt32(); return true;
            case GgufValueType.Int32: value = (int)reader.ReadUInt32(); return true;
            case GgufValueType.Int64: value = (long)reader.ReadUInt64(); return true;
            case GgufValueType.UInt64:
                ulong u = reader.ReadUInt64();
                value = u > long.MaxValue ? 0 : (long)u;
                return u <= long.MaxValue;
            default: throw UnknownType(type);
        }
    }

    private static bool ReadBool(BoundedReader reader, string key)
    {
        byte value = reader.ReadByte();
        return value switch
        {
            0 => false,
            1 => true,
            _ => throw new GgufFormatException(string.Create(CultureInfo.InvariantCulture, $"'{key}' holds the bool byte {value}.")),
        };
    }

    private static string ReadKeptString(BoundedReader reader, string key)
    {
        long length = ReadStringLength(reader, key);
        int kept = (int)Math.Min(length, MaxNameBytes);
        Span<byte> bytes = stackalloc byte[kept];
        reader.ReadExactly(bytes);
        reader.Skip(length - kept);
        return Encoding.UTF8.GetString(bytes);
    }

    private static GgufValue ReadKeptArray(BoundedReader reader, string key)
    {
        var elementType = (GgufValueType)reader.ReadUInt32();
        long count = ReadArrayCount(reader, elementType);
        int size = FixedSize(elementType);
        if (elementType == GgufValueType.String)
        {
            for (long i = 0; i < count; i++)
            {
                SkipString(reader, "metadata string");
            }

            return GgufValue.Array(elementType, count, null);
        }

        if (!IsInteger(elementType))
        {
            reader.Skip(checked(count * size));
            return GgufValue.Array(elementType, count, null);
        }

        long? max = null;
        for (long i = 0; i < count; i++)
        {
            if (!ReadInteger(reader, elementType, out long element))
            {
                throw new GgufFormatException($"'{key}' holds a value out of range.");
            }

            max = max is null ? element : Math.Max(max.Value, element);
        }

        return GgufValue.Array(elementType, count, max);
    }

    private static long ReadArrayCount(BoundedReader reader, GgufValueType elementType)
    {
        if (elementType == GgufValueType.Array)
        {
            throw new GgufFormatException("Nested metadata arrays are not supported.");
        }

        int size = FixedSize(elementType); // throws for an unknown type
        ulong count = reader.ReadUInt64();
        if (count > (ulong)GgufLimits.MaxArrayLength)
        {
            throw new GgufFormatException(string.Create(CultureInfo.InvariantCulture,
                $"A metadata array of {count} elements exceeds {GgufLimits.MaxArrayLength}."));
        }

        if ((long)count * size > reader.Remaining)
        {
            throw new GgufFormatException(string.Create(CultureInfo.InvariantCulture, $"A metadata array of {count} elements runs past the end of the file."));
        }

        return (long)count;
    }

    private static void SkipValue(BoundedReader reader, GgufValueType type)
    {
        if (type == GgufValueType.String)
        {
            SkipString(reader, "metadata string");
            return;
        }

        if (type == GgufValueType.Array)
        {
            var elementType = (GgufValueType)reader.ReadUInt32();
            long count = ReadArrayCount(reader, elementType);
            if (elementType == GgufValueType.String)
            {
                for (long i = 0; i < count; i++)
                {
                    SkipString(reader, "metadata string");
                }
            }
            else
            {
                reader.Skip(checked(count * FixedSize(elementType)));
            }

            return;
        }

        reader.Skip(FixedSize(type));
    }

    /// <summary>
    /// Bytes of a fixed-size value; for <see cref="GgufValueType.String"/> the 8-byte length prefix (the least a string
    /// occupies), used to bound array counts against the file. Throws for an unknown type.
    /// </summary>
    private static int FixedSize(GgufValueType type) => type switch
    {
        GgufValueType.UInt8 or GgufValueType.Int8 or GgufValueType.Bool => 1,
        GgufValueType.UInt16 or GgufValueType.Int16 => 2,
        GgufValueType.UInt32 or GgufValueType.Int32 or GgufValueType.Float32 => 4,
        GgufValueType.UInt64 or GgufValueType.Int64 or GgufValueType.Float64 => 8,
        GgufValueType.String => 8,
        _ => throw UnknownType(type),
    };

    private static bool IsInteger(GgufValueType type) => type is GgufValueType.UInt8 or GgufValueType.Int8
        or GgufValueType.UInt16 or GgufValueType.Int16 or GgufValueType.UInt32 or GgufValueType.Int32
        or GgufValueType.UInt64 or GgufValueType.Int64;

    private static GgufFormatException UnknownType(GgufValueType type) =>
        new(string.Create(CultureInfo.InvariantCulture, $"Unknown GGUF metadata type {(uint)type}."));

    [GeneratedRegex(@"^(?<prefix>.+)-(?<k>\d{5})-of-(?<n>\d{5})(?<ext>\.gguf)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SplitNameRegex();

    /// <summary>
    /// Little-endian reads over a seekable stream that check the bytes left before every read or skip, so a claimed
    /// length never makes the reader allocate or seek past the end.
    /// </summary>
    private sealed class BoundedReader(Stream stream)
    {
        private readonly Stream _stream = stream;
        private readonly long _start = stream.Position;
        private readonly long _length = stream.Length;
        private long _position = stream.Position;

        public long Length => _length - _start;

        public long Position => _position - _start;

        public long Remaining => _length - _position;

        public void Seek(long position)
        {
            _position = _start + position;
            _stream.Position = _position;
        }

        public void Skip(long count)
        {
            if (count < 0 || count > Remaining)
            {
                throw new EndOfStreamException();
            }

            if (count == 0)
            {
                return;
            }

            _position += count;
            _stream.Seek(count, SeekOrigin.Current);
        }

        public void ReadExactly(Span<byte> buffer)
        {
            if (buffer.Length > Remaining)
            {
                throw new EndOfStreamException();
            }

            _stream.ReadExactly(buffer);
            _position += buffer.Length;
        }

        public byte ReadByte()
        {
            Span<byte> b = stackalloc byte[1];
            ReadExactly(b);
            return b[0];
        }

        public ushort ReadUInt16()
        {
            Span<byte> b = stackalloc byte[2];
            ReadExactly(b);
            return BinaryPrimitives.ReadUInt16LittleEndian(b);
        }

        public uint ReadUInt32()
        {
            Span<byte> b = stackalloc byte[4];
            ReadExactly(b);
            return BinaryPrimitives.ReadUInt32LittleEndian(b);
        }

        public ulong ReadUInt64()
        {
            Span<byte> b = stackalloc byte[8];
            ReadExactly(b);
            return BinaryPrimitives.ReadUInt64LittleEndian(b);
        }
    }
}

/// <summary>GGUF metadata value types (<c>gguf_type</c>).</summary>
internal enum GgufValueType : uint
{
    UInt8 = 0,
    Int8 = 1,
    UInt16 = 2,
    Int16 = 3,
    UInt32 = 4,
    Int32 = 5,
    Float32 = 6,
    Bool = 7,
    String = 8,
    Array = 9,
    UInt64 = 10,
    Int64 = 11,
    Float64 = 12,
}

/// <summary>What a kept metadata value is.</summary>
internal enum GgufValueKind
{
    Integer,
    OutOfRange,
    Boolean,
    Text,
    Array,
    Other,
}

/// <summary>One kept metadata value: a scalar, a bounded string, or an array's count (and its largest integer).</summary>
internal sealed record GgufValue(GgufValueKind Kind, long IntegerValue = 0, bool BoolValue = false, string? TextValue = null,
    GgufValueType ElementType = GgufValueType.UInt8, long ArrayCount = 0, long? ArrayMax = null, GgufValueType? OtherType = null)
{
    public static GgufValue OutOfRange { get; } = new(GgufValueKind.OutOfRange);

    public static GgufValue Integer(long value) => new(GgufValueKind.Integer, IntegerValue: value);

    public static GgufValue Boolean(bool value) => new(GgufValueKind.Boolean, BoolValue: value);

    public static GgufValue Text(string value) => new(GgufValueKind.Text, TextValue: value);

    public static GgufValue Array(GgufValueType elementType, long count, long? max) =>
        new(GgufValueKind.Array, ElementType: elementType, ArrayCount: count, ArrayMax: max);

    public static GgufValue Other(GgufValueType type) => new(GgufValueKind.Other, OtherType: type);

    /// <summary>The integer value (a scalar, or the largest element of an integer array); throws when it is not one.</summary>
    public long AsInteger(string key) => Kind switch
    {
        GgufValueKind.Integer => IntegerValue,
        GgufValueKind.Array when ArrayMax is { } max => max,
        GgufValueKind.OutOfRange => throw new GgufFormatException($"'{key}' holds a value out of range."),
        _ => throw new GgufFormatException($"'{key}' is not an integer."),
    };
}

/// <summary>One part's header as the summary needs it.</summary>
internal sealed record GgufHeader
{
    public required int Version { get; init; }

    public required string? Architecture { get; init; }

    public required IReadOnlyDictionary<string, GgufValue> Values { get; init; }

    public required long TensorCount { get; init; }

    public required long TensorBytes { get; init; }

    public required long ParameterCount { get; init; }

    /// <summary>Where the tensor data starts (the header length, padded to the alignment).</summary>
    public required long DataOffset { get; init; }

    public required long FileBytes { get; init; }

    /// <summary><c>split.count</c>; 1 when absent or 0.</summary>
    public required int SplitCount { get; init; }

    /// <summary><c>split.no</c> (0-based); 0 when absent.</summary>
    public required int SplitNumber { get; init; }

    /// <summary><c>split.tensors.count</c> when present.</summary>
    public required long? SplitTensorCount { get; init; }
}

/// <summary>ggml tensor types: block size and bytes per block (<c>ggml_type_traits</c> at llama.cpp <c>b11476</c>).</summary>
internal static class GgmlTypes
{
    private static readonly Dictionary<uint, GgmlType> Types = new()
    {
        [0] = new("F32", 1, 4),
        [1] = new("F16", 1, 2),
        [2] = new("Q4_0", 32, 18),
        [3] = new("Q4_1", 32, 20),
        [6] = new("Q5_0", 32, 22),
        [7] = new("Q5_1", 32, 24),
        [8] = new("Q8_0", 32, 34),
        [9] = new("Q8_1", 32, 36),
        [10] = new("Q2_K", 256, 84),
        [11] = new("Q3_K", 256, 110),
        [12] = new("Q4_K", 256, 144),
        [13] = new("Q5_K", 256, 176),
        [14] = new("Q6_K", 256, 210),
        [15] = new("Q8_K", 256, 292),
        [16] = new("IQ2_XXS", 256, 66),
        [17] = new("IQ2_XS", 256, 74),
        [18] = new("IQ3_XXS", 256, 98),
        [19] = new("IQ1_S", 256, 50),
        [20] = new("IQ4_NL", 32, 18),
        [21] = new("IQ3_S", 256, 110),
        [22] = new("IQ2_S", 256, 82),
        [23] = new("IQ4_XS", 256, 136),
        [24] = new("I8", 1, 1),
        [25] = new("I16", 1, 2),
        [26] = new("I32", 1, 4),
        [27] = new("I64", 1, 8),
        [28] = new("F64", 1, 8),
        [29] = new("IQ1_M", 256, 56),
        [30] = new("BF16", 1, 2),
        [34] = new("TQ1_0", 256, 54),
        [35] = new("TQ2_0", 256, 66),
        [39] = new("MXFP4", 32, 17),
    };

    public static bool TryGet(uint id, out GgmlType type) => Types.TryGetValue(id, out type!);
}

/// <summary>A ggml tensor type.</summary>
internal sealed record GgmlType(string Name, int BlockSize, int TypeSize);
