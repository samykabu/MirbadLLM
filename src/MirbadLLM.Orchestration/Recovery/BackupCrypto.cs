using System.Security.Cryptography;
using System.Text;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Recovery;

/// <summary>
/// Passphrase encryption for a backup's secrets section: PBKDF2-SHA256 (<see cref="Iterations"/> rounds, a
/// random 16-byte salt) derives a 256-bit key; AES-GCM encrypts with a random 12-byte nonce and a 16-byte tag.
/// The document's schema version is bound in as associated data (<c>mirbadllm-backup/v{n}</c>). Every parameter but the passphrase is stored with the
/// ciphertext, and nothing here logs or keeps the passphrase or the derived key.
/// </summary>
public static class BackupCrypto
{
    public const string Algorithm = "AES-256-GCM";
    public const string Kdf = "PBKDF2-SHA256";

    /// <summary>OWASP's 2023 floor for PBKDF2-HMAC-SHA256.</summary>
    public const int Iterations = 600_000;

    /// <summary>Highest iteration count a file may ask for, so a crafted file can't pin the CPU for minutes.</summary>
    public const int MaxIterations = 10_000_000;

    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;

    /// <summary>Encrypts a version-1 secrets section (associated data <c>mirbadllm-backup/v1</c>).</summary>
    public static EncryptedSection Encrypt(ReadOnlySpan<byte> plaintext, string passphrase) =>
        Encrypt(plaintext, passphrase, BackupDocument.CurrentSchemaVersion);

    /// <summary>
    /// Encrypts the secrets section of a document of <paramref name="schemaVersion"/>; the version is the associated data
    /// (<c>mirbadllm-backup/v{n}</c>), so a section cannot be moved into a document of another version (W3 review
    /// MN-094: a version-2 section, which may hold the CA, never decrypts as part of a version-1 file).
    /// </summary>
    public static EncryptedSection Encrypt(ReadOnlySpan<byte> plaintext, string passphrase, int schemaVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        byte[] associatedData = AssociatedDataFor(schemaVersion);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        byte[] key = DeriveKey(passphrase, salt, Iterations);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagBytes];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return new EncryptedSection(
            Algorithm,
            Kdf,
            Iterations,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(tag),
            Convert.ToBase64String(ciphertext));
    }

    /// <exception cref="BackupFormatException">The section's parameters are not ones this version wrote.</exception>
    /// <exception cref="BackupPassphraseException">Wrong passphrase, or the ciphertext, tag, nonce or salt was changed.</exception>
    public static byte[] Decrypt(EncryptedSection section, string passphrase) =>
        Decrypt(section, passphrase, BackupDocument.CurrentSchemaVersion);

    /// <summary>Decrypts the secrets section of a document of <paramref name="schemaVersion"/> (see the encrypting overload).</summary>
    /// <exception cref="BackupFormatException">The section's parameters are not ones this version wrote.</exception>
    /// <exception cref="BackupPassphraseException">Wrong passphrase, or the ciphertext, tag, nonce, salt or version was changed.</exception>
    public static byte[] Decrypt(EncryptedSection section, string passphrase, int schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(section);
        byte[] associatedData = AssociatedDataFor(schemaVersion);
        if (string.IsNullOrEmpty(passphrase))
        {
            throw new BackupPassphraseException("This backup's secrets are encrypted; enter the passphrase it was made with.");
        }

        Validate(section);
        byte[] salt = Convert.FromBase64String(section.Salt);
        byte[] nonce = Convert.FromBase64String(section.Nonce);
        byte[] tag = Convert.FromBase64String(section.Tag);
        byte[] ciphertext = Convert.FromBase64String(section.Ciphertext);
        byte[] plaintext = new byte[ciphertext.Length];

        byte[] key = DeriveKey(passphrase, salt, section.Iterations);
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            try
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            }
            catch (AuthenticationTagMismatchException)
            {
                // A backup written before the rename was bound to the legacy associated data.
                aes.Decrypt(nonce, ciphertext, tag, plaintext, LegacyAssociatedDataFor(schemaVersion));
            }

            return plaintext;
        }
        catch (AuthenticationTagMismatchException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new BackupPassphraseException(
                "Wrong passphrase, or the backup file was changed after it was written. Nothing was imported.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Checks a section read from a file before any key derivation runs.</summary>
    /// <exception cref="BackupFormatException">Unknown algorithm or KDF, an iteration count out of range, or malformed fields.</exception>
    public static void Validate(EncryptedSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (!string.Equals(section.Algorithm, Algorithm, StringComparison.Ordinal) || !string.Equals(section.Kdf, Kdf, StringComparison.Ordinal))
        {
            throw new BackupFormatException($"The secrets section uses {section.Algorithm} / {section.Kdf}; this version reads {Algorithm} / {Kdf}.");
        }

        if (section.Iterations is < Iterations or > MaxIterations)
        {
            throw new BackupFormatException(
                $"The secrets section asks for {section.Iterations:N0} PBKDF2 iterations; it must be between {Iterations:N0} and {MaxIterations:N0}.");
        }

        if (DecodedLength(section.Salt) != SaltBytes || DecodedLength(section.Nonce) != NonceBytes ||
            DecodedLength(section.Tag) != TagBytes || DecodedLength(section.Ciphertext) < 0)
        {
            throw new BackupFormatException("The secrets section is damaged: its salt, nonce, tag or ciphertext is not valid base64 of the right size.");
        }
    }

    private static byte[] AssociatedDataFor(int schemaVersion) =>
        BackupDocument.ReadableSchemaVersions.Contains(schemaVersion)
            ? Encoding.UTF8.GetBytes(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"mirbadllm-backup/v{schemaVersion}"))
            : throw new BackupFormatException($"Backup schema version {schemaVersion} has no secrets format.");

    private static byte[] LegacyAssociatedDataFor(int schemaVersion) =>
        Encoding.UTF8.GetBytes(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{LegacyNames.BackupSchemaPrefix}{schemaVersion}"));

    private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, KeyBytes);

    /// <summary>Decoded byte length, or -1 when the text is not valid base64.</summary>
    private static int DecodedLength(string? base64)
    {
        if (base64 is null)
        {
            return -1;
        }

        byte[] buffer = new byte[(base64.Length * 3 / 4) + 3];
        return Convert.TryFromBase64String(base64, buffer, out int written) ? written : -1;
    }
}
