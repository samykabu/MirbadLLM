using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.NodeRuntime.Abstractions;

/// <summary>
/// Reads GGUF headers (design-p3p4 §A.1.10, §A.8 "GGUF parser bounds every length and count"; frozen by MN-202, D116;
/// implemented by MN-209's <c>GgufReader</c>, a pure .NET reader, no package). Hostile input must never crash the agent:
/// every failure is a <see cref="GgufFormatException"/> and no read allocates more than
/// <see cref="GgufLimits.MaxAllocationBytes"/>.
/// </summary>
public interface IGgufReader
{
    /// <summary>
    /// Reads the header of the GGUF at <paramref name="path"/> (already confined to the models root by the caller) and of
    /// its sibling split parts (<c>-0000k-of-0000N.gguf</c>), summing tensor and file bytes over all parts.
    /// <paramref name="folder"/> and <paramref name="file"/> are echoed into the summary.
    /// </summary>
    /// <exception cref="GgufFormatException">The file or a part is not a valid GGUF v2/v3 or breaks a bound.</exception>
    Task<GgufSummary> ReadAsync(string path, string folder, string file, CancellationToken ct = default);
}

/// <summary>Bounds the GGUF reader enforces (design-p3p4 §A.8; frozen by MN-202, D116). A header that exceeds one is invalid.</summary>
public static class GgufLimits
{
    /// <summary>The GGUF magic, <c>GGUF</c> little-endian.</summary>
    public const uint Magic = 0x46554747;

    /// <summary>Most memory one header read may allocate.</summary>
    public const long MaxAllocationBytes = 64L * 1024 * 1024;

    /// <summary>Longest metadata key or string value (bytes).</summary>
    public const int MaxStringBytes = 16 * 1024 * 1024;

    /// <summary>Most elements in one metadata array (the tokenizer's token list is the largest real one, ~250 k).</summary>
    public const long MaxArrayLength = 4L * 1024 * 1024;

    /// <summary>Most metadata key/value pairs.</summary>
    public const long MaxMetadataCount = 65_536;

    /// <summary>Most tensors in one part.</summary>
    public const long MaxTensorCount = 1_048_576;

    /// <summary>Most dimensions of one tensor.</summary>
    public const int MaxTensorDimensions = 8;

    /// <summary>Most split parts.</summary>
    public const int MaxSplitCount = 256;
}

/// <summary>A GGUF file is malformed or breaks a <see cref="GgufLimits"/> bound (the agent answers 422 <c>gguf_invalid</c>).</summary>
public sealed class GgufFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    public GgufFormatException()
        : base("The file is not a valid GGUF.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public GgufFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the underlying error.</summary>
    public GgufFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
