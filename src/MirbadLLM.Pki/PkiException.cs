namespace MirbadLLM.Pki;

/// <summary>Why a PKI operation was refused. Stable values: callers map them to error codes and log them.</summary>
public enum PkiErrorKind
{
    /// <summary>The CSR is not a parseable PKCS#10 PEM, or is too large.</summary>
    InvalidCsr,

    /// <summary>The CSR's signature does not verify with its own public key (no proof of possession, design §3.2).</summary>
    CsrSignatureInvalid,

    /// <summary>The key is not ECDSA P-256 (design §3.1, §10.2).</summary>
    UnsupportedKey,

    /// <summary>The node id breaks <c>Protocol.NodeIdPattern</c>.</summary>
    InvalidNodeId,

    /// <summary>An address for a SAN is neither a literal IP nor a valid DNS name (wildcards are refused).</summary>
    InvalidAddress,

    /// <summary>The requested certificate lifetime is not positive or exceeds the maximum for that certificate kind.</summary>
    InvalidLifetime,

    /// <summary>A certificate and a private key do not form a pair.</summary>
    KeyMismatch,

    /// <summary>A certificate that must be a CA is not a self-signed CA with the certificate-signing key usage.</summary>
    NotACertificateAuthority,

    /// <summary>The CA is expired or not yet valid, so it cannot issue.</summary>
    CaNotValid,

    /// <summary>The CA in the data directory and the configured CA both exist and differ (design §10.2): the master refuses to start.</summary>
    CaConflict,

    /// <summary>The CA material on disk or in configuration is incomplete or unreadable.</summary>
    CaMaterialInvalid,

    /// <summary>A PEM certificate could not be parsed.</summary>
    InvalidCertificate,
}

/// <summary>
/// A refused PKI operation. Messages carry only public data (ids, subjects, SPKI hashes), never key material, tokens or
/// whole PEM blobs, so they are safe to log.
/// </summary>
public sealed class PkiException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PkiException(PkiErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>Unused; present for the standard exception constructors.</summary>
    public PkiException()
        : this(PkiErrorKind.InvalidCertificate, "PKI operation refused.")
    {
    }

    /// <summary>Unused; present for the standard exception constructors.</summary>
    public PkiException(string message)
        : this(PkiErrorKind.InvalidCertificate, message)
    {
    }

    /// <summary>Unused; present for the standard exception constructors.</summary>
    public PkiException(string message, Exception innerException)
        : this(PkiErrorKind.InvalidCertificate, message, innerException)
    {
    }

    /// <summary>Why it was refused.</summary>
    public PkiErrorKind Kind { get; }
}
