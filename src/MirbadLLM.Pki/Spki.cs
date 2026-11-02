using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MirbadLLM.Pki;

/// <summary>
/// SubjectPublicKeyInfo pins (design §3.1, §3.2): base64 SHA-256 of the DER SPKI, the format used by
/// <c>NodeRecord.CertSpkiSha256</c>, <c>TrustAnchorInfo.SpkiSha256</c> and <c>BootstrapBundle.CaSpkiSha256</c>.
/// The join command carries the CA pin as <c>sha256:{base64}</c> (<see cref="ToJoinHash"/>).
/// </summary>
public static class Spki
{
    /// <summary>Prefix of the CA hash in the join command (<c>--ca-hash sha256:H</c>, design §3.2).</summary>
    public const string JoinHashPrefix = "sha256:";

    /// <summary>Base64 SHA-256 of the certificate's SubjectPublicKeyInfo.</summary>
    public static string Sha256(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Sha256(certificate.PublicKey);
    }

    /// <summary>Base64 SHA-256 of a public key's SubjectPublicKeyInfo.</summary>
    public static string Sha256(PublicKey publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        return Sha256(publicKey.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Base64 SHA-256 of an asymmetric key's SubjectPublicKeyInfo (only its public part is read).</summary>
    public static string Sha256(AsymmetricAlgorithm key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Sha256(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Base64 SHA-256 of DER-encoded SubjectPublicKeyInfo bytes.</summary>
    public static string Sha256(ReadOnlySpan<byte> subjectPublicKeyInfoDer) =>
        Convert.ToBase64String(SHA256.HashData(subjectPublicKeyInfoDer));

    /// <summary>
    /// Constant-time comparison of two pins (compares the decoded hashes; malformed values never match). Use this rather
    /// than string equality.
    /// </summary>
    public static bool PinEquals(string? a, string? b)
    {
        if (!TryDecode(a, out byte[] left) || !TryDecode(b, out byte[] right))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    /// <summary>True when <paramref name="pin"/> is a canonical base64 SHA-256 value (44 characters, 32 bytes).</summary>
    public static bool IsValidPin(string? pin) => TryDecode(pin, out _);

    /// <summary>True when the certificate's SPKI hash equals <paramref name="pin"/> (constant time).</summary>
    public static bool Matches(X509Certificate2 certificate, string? pin) => PinEquals(Sha256(certificate), pin);

    /// <summary>True when the certificate's SPKI hash equals any of <paramref name="pins"/> (each compared in constant time).</summary>
    public static bool MatchesAny(X509Certificate2 certificate, IEnumerable<string> pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        string actual = Sha256(certificate);
        bool match = false;
        foreach (string pin in pins)
        {
            match |= PinEquals(actual, pin);
        }

        return match;
    }

    /// <summary>The join-command form <c>sha256:{base64}</c> of a pin.</summary>
    public static string ToJoinHash(string pin)
    {
        if (!TryDecode(pin, out _))
        {
            throw new ArgumentException("Not a base64 SHA-256 pin.", nameof(pin));
        }

        return JoinHashPrefix + pin;
    }

    /// <summary>
    /// Parses <c>sha256:{hash}</c> (or a bare hash) where the hash is base64, base64url or 64 hex digits, into the canonical
    /// base64 pin. False when it is not exactly a 32-byte SHA-256 value.
    /// </summary>
    public static bool TryParseJoinHash(string? value, out string pin)
    {
        pin = "";
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string text = value.Trim();
        if (text.StartsWith(JoinHashPrefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[JoinHashPrefix.Length..];
        }

        byte[]? bytes = null;
        if (text.Length == 64 && text.All(Uri.IsHexDigit))
        {
            bytes = Convert.FromHexString(text);
        }
        else
        {
            string base64 = text.Replace('-', '+').Replace('_', '/');
            int pad = (4 - (base64.Length % 4)) % 4;
            if (pad < 3)
            {
                base64 += new string('=', pad);
                byte[] buffer = new byte[base64.Length];
                if (Convert.TryFromBase64String(base64, buffer, out int written) && written == 32)
                {
                    bytes = buffer[..written];
                }
            }
        }

        if (bytes is not { Length: 32 })
        {
            return false;
        }

        pin = Convert.ToBase64String(bytes);
        return true;
    }

    /// <summary>The colon-separated hex fingerprint shown to the operator in the approval dialog (design §3.2).</summary>
    public static string ToDisplayFingerprint(string pin)
    {
        if (!TryDecode(pin, out byte[] bytes))
        {
            throw new ArgumentException("Not a base64 SHA-256 pin.", nameof(pin));
        }

        StringBuilder builder = new(bytes.Length * 3);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(':');
            }

            builder.Append(bytes[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static bool TryDecode(string? pin, out byte[] bytes)
    {
        bytes = [];
        if (pin is null || pin.Length != 44)
        {
            return false;
        }

        byte[] buffer = new byte[33];
        if (!Convert.TryFromBase64String(pin, buffer, out int written) || written != 32)
        {
            return false;
        }

        bytes = buffer[..32];
        return true;
    }
}
