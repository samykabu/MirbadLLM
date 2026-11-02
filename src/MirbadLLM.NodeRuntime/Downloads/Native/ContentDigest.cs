using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MirbadLLM.NodeRuntime.Downloads.Native;

/// <summary>
/// The expected digest of a downloaded file (design §6.4 "Integrity"): SHA-256 of the content (a Hugging Face LFS oid, an
/// Ollama blob digest), or the git blob SHA-1 of a small non-LFS file (<c>sha1("blob {size}\0" + content)</c>, the
/// <c>blobId</c> of the Hub listing).
/// </summary>
public sealed record ContentDigest
{
    private ContentDigest(HashAlgorithmName algorithm, string expectedHex, byte[] prefix)
    {
        Algorithm = algorithm;
        ExpectedHex = expectedHex;
        Prefix = prefix;
    }

    /// <summary>Hash algorithm.</summary>
    public HashAlgorithmName Algorithm { get; }

    /// <summary>Expected digest, lower-case hex.</summary>
    public string ExpectedHex { get; }

    /// <summary>Bytes hashed before the content (the git blob header); empty for a plain content hash.</summary>
    internal byte[] Prefix { get; }

    /// <summary>SHA-256 of the content; <paramref name="hex"/> is 64 hex digits (case-insensitive).</summary>
    public static ContentDigest Sha256(string hex) => new(HashAlgorithmName.SHA256, Normalize(hex, 64), []);

    /// <summary>Git blob SHA-1 of a file of <paramref name="size"/> bytes; <paramref name="hex"/> is 40 hex digits.</summary>
    public static ContentDigest GitBlobSha1(string hex, long size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        return new(HashAlgorithmName.SHA1, Normalize(hex, 40), Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"blob {size}\0")));
    }

    /// <summary>True when <paramref name="hex"/> is exactly <paramref name="length"/> hex digits.</summary>
    public static bool IsHex(string? hex, int length) =>
        hex is not null && hex.Length == length && hex.All(Uri.IsHexDigit);

    /// <summary>A new incremental hash for this digest, with <see cref="Prefix"/> already appended.</summary>
    internal IncrementalHash CreateHash()
    {
        var hash = IncrementalHash.CreateHash(Algorithm);
        if (Prefix.Length > 0)
        {
            hash.AppendData(Prefix);
        }

        return hash;
    }

    /// <summary>Short display form, e.g. <c>sha256:0123abcd…</c>.</summary>
    public override string ToString() => $"{Algorithm.Name!.ToLowerInvariant()}:{ExpectedHex[..Math.Min(12, ExpectedHex.Length)]}…";

    private static string Normalize(string hex, int length)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (!IsHex(hex, length))
        {
            throw new ArgumentException($"Expected {length} hex digits.", nameof(hex));
        }

        return hex.ToLowerInvariant();
    }
}
