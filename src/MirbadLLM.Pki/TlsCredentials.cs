using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MirbadLLM.Pki;

/// <summary>
/// Turns certificate + key material into an <see cref="X509Certificate2"/> the TLS stacks accept on every platform. On
/// Windows, SChannel refuses an in-memory (ephemeral CNG) key for server and client authentication, so the pair goes
/// through an in-memory PKCS#12 round trip; on Linux the key stays ephemeral (never written to a key store). macOS
/// cannot load a PKCS#12 with ephemeral keys, and a default load creates a temporary keychain (~360 ms each on an M2),
/// so there the certificate is bound to an in-memory copy of its ECDSA key instead, which Apple's TLS stack accepts
/// for server and client authentication (measured on macOS 27: ~4 ms, mutual TLS handshakes succeed).
/// </summary>
public static class TlsCredentials
{
    /// <summary>A TLS-usable copy of a certificate that already has its private key. The caller owns the result.</summary>
    public static X509Certificate2 Create(X509Certificate2 certificateWithKey)
    {
        ArgumentNullException.ThrowIfNull(certificateWithKey);
        if (!certificateWithKey.HasPrivateKey)
        {
            throw new PkiException(PkiErrorKind.KeyMismatch, "The certificate has no private key.");
        }

        if (OperatingSystem.IsMacOS())
        {
            using ECDsa? key = certificateWithKey.GetECDsaPrivateKey();
            if (key is not null)
            {
                using X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(certificateWithKey.RawData);
                return publicOnly.CopyWithPrivateKey(key);
            }
        }

        byte[] pfx = certificateWithKey.Export(X509ContentType.Pkcs12);
        try
        {
            X509KeyStorageFlags flags = OperatingSystem.IsLinux() ? X509KeyStorageFlags.EphemeralKeySet : X509KeyStorageFlags.DefaultKeySet;
            return X509CertificateLoader.LoadPkcs12(pfx, password: null, flags);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    /// <summary>
    /// Loads a certificate PEM and its private-key PEM (PKCS#8 or SEC1 "EC PRIVATE KEY"; unencrypted), checks they are a
    /// matching ECDSA P-256 pair, and returns a TLS-usable certificate. Error messages never include the key.
    /// </summary>
    public static X509Certificate2 FromPem(string certificatePem, string privateKeyPem)
    {
        using X509Certificate2 certificate = PemReader.LoadCertificate(certificatePem);
        using ECDsa key = PemReader.LoadP256PrivateKey(privateKeyPem);
        KeyPairs.EnsureMatches(certificate, key);
        using X509Certificate2 withKey = certificate.CopyWithPrivateKey(key);
        return Create(withKey);
    }

    /// <summary>Loads a certificate + key PEM pair as an <see cref="IssuedCredential"/> after checking it is a matching P-256 pair.</summary>
    public static IssuedCredential LoadCredential(string certificatePem, string privateKeyPem)
    {
        X509Certificate2 certificate = PemReader.LoadCertificate(certificatePem);
        ECDsa? key = null;
        try
        {
            key = PemReader.LoadP256PrivateKey(privateKeyPem);
            KeyPairs.EnsureMatches(certificate, key);
            return new IssuedCredential(certificate, key);
        }
        catch
        {
            certificate.Dispose();
            key?.Dispose();
            throw;
        }
    }
}

/// <summary>Strict PEM loading. Error messages never echo the input (it may be key material).</summary>
internal static class PemReader
{
    public static X509Certificate2 LoadCertificate(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);
        try
        {
            return X509Certificate2.CreateFromPem(pem);
        }
        catch (CryptographicException ex)
        {
            throw new PkiException(PkiErrorKind.InvalidCertificate, "The certificate PEM could not be parsed.", ex);
        }
    }

    public static List<X509Certificate2> LoadCertificates(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);
        X509Certificate2Collection collection = [];
        try
        {
            collection.ImportFromPem(pem);
        }
        catch (CryptographicException ex)
        {
            throw new PkiException(PkiErrorKind.InvalidCertificate, "The certificate PEM could not be parsed.", ex);
        }

        if (collection.Count == 0)
        {
            throw new PkiException(PkiErrorKind.InvalidCertificate, "The PEM holds no certificate.");
        }

        return [.. collection];
    }

    public static ECDsa LoadP256PrivateKey(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);
        ECDsa key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            key.Dispose();
            throw new PkiException(PkiErrorKind.KeyMismatch, "The private key PEM is not an unencrypted ECDSA key.", ex);
        }

        try
        {
            ECParameters parameters = key.ExportParameters(includePrivateParameters: false);
            if (!KeyPairs.IsP256Curve(parameters.Curve))
            {
                throw new PkiException(PkiErrorKind.UnsupportedKey, "The private key is not ECDSA P-256.");
            }

            // A PEM that holds only a public key imports fine; make sure there is a private part.
            _ = key.ExportParameters(includePrivateParameters: true);
            return key;
        }
        catch (CryptographicException ex)
        {
            key.Dispose();
            throw new PkiException(PkiErrorKind.KeyMismatch, "The PEM does not hold an ECDSA private key.", ex);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }
}

/// <summary>Certificate/key pair checks (design §10.2: "on every load the CA certificate and key are checked to be a matching pair").</summary>
internal static class KeyPairs
{
    public static void EnsureMatches(X509Certificate2 certificate, ECDsa key)
    {
        byte[] certSpki = certificate.PublicKey.ExportSubjectPublicKeyInfo();
        byte[] keySpki = key.ExportSubjectPublicKeyInfo();
        if (!CryptographicOperations.FixedTimeEquals(certSpki, keySpki))
        {
            throw new PkiException(PkiErrorKind.KeyMismatch, $"The private key does not belong to certificate '{certificate.Subject}'.");
        }

        // Prove the key works and that the certificate's key verifies what it signs.
        byte[] probe = RandomNumberGenerator.GetBytes(32);
        byte[] signature = key.SignData(probe, HashAlgorithmName.SHA256);
        using ECDsa? publicKey = certificate.GetECDsaPublicKey();
        if (publicKey is null || !publicKey.VerifyData(probe, signature, HashAlgorithmName.SHA256))
        {
            throw new PkiException(PkiErrorKind.KeyMismatch, $"The private key does not belong to certificate '{certificate.Subject}'.");
        }
    }

    public static bool IsP256(PublicKey publicKey)
    {
        if (publicKey.Oid.Value != PkiDefaults.EcPublicKeyOid)
        {
            return false;
        }

        using ECDsa? ecdsa = publicKey.GetECDsaPublicKey();
        if (ecdsa is null)
        {
            return false;
        }

        return IsP256Curve(ecdsa.ExportParameters(includePrivateParameters: false).Curve);
    }

    public static bool IsP256Curve(ECCurve curve) =>
        curve.IsNamed
        && (curve.Oid.Value == PkiDefaults.NistP256Oid
            || string.Equals(curve.Oid.FriendlyName, "nistP256", StringComparison.OrdinalIgnoreCase)
            || string.Equals(curve.Oid.FriendlyName, "ECDSA_P256", StringComparison.OrdinalIgnoreCase));
}
