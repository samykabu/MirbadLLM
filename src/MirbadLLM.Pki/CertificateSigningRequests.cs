using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Pki;

/// <summary>
/// PKCS#10 CSRs. The agent creates one from a key it generated (enrollment, design §3.2; rotation, §10.2 step 1); the master
/// verifies it (<see cref="VerifyAndExtractPublicKey"/>) and keeps only its public key.
/// </summary>
public static class CertificateSigningRequests
{
    /// <summary>
    /// Agent side: a CSR (PEM) for a P-256 key the agent generated and keeps. The subject is informational; the master
    /// ignores everything in the CSR except the public key.
    /// </summary>
    public static string CreatePem(ECDsa key, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!Protocol.IsValidNodeId(nodeId))
        {
            throw new PkiException(PkiErrorKind.InvalidNodeId, "The node id breaks the node id rule.");
        }

        if (!KeyPairs.IsP256Curve(key.ExportParameters(includePrivateParameters: false).Curve))
        {
            throw new PkiException(PkiErrorKind.UnsupportedKey, "Agent keys must be ECDSA P-256.");
        }

        X500DistinguishedNameBuilder subject = new();
        subject.AddOrganizationName(PkiDefaults.Organization);
        subject.AddCommonName(nodeId);
        CertificateRequest request = new(subject.Build(), key, PkiDefaults.SignatureHash);
        return request.CreateSigningRequestPem();
    }

    /// <summary>
    /// Master side: parses a CSR, verifies its signature with its own public key (proof of possession, design §3.2) and
    /// returns that public key, which must be ECDSA P-256. Subject, attributes and requested extensions are discarded.
    /// Throws <see cref="PkiException"/>: <see cref="PkiErrorKind.InvalidCsr"/>, <see cref="PkiErrorKind.CsrSignatureInvalid"/>
    /// or <see cref="PkiErrorKind.UnsupportedKey"/>.
    /// </summary>
    public static PublicKey VerifyAndExtractPublicKey(string csrPem)
    {
        if (string.IsNullOrWhiteSpace(csrPem))
        {
            throw new PkiException(PkiErrorKind.InvalidCsr, "The CSR is empty.");
        }

        if (csrPem.Length > PkiDefaults.MaxCsrPemLength)
        {
            throw new PkiException(PkiErrorKind.InvalidCsr, $"The CSR exceeds {PkiDefaults.MaxCsrPemLength} characters.");
        }

        CertificateRequest request;
        try
        {
            // Default options verify the signature and do not load requested extensions.
            request = CertificateRequest.LoadSigningRequestPem(csrPem, PkiDefaults.SignatureHash, CertificateRequestLoadOptions.Default);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw Classify(csrPem, ex);
        }

        PublicKey publicKey = request.PublicKey;
        if (!KeyPairs.IsP256(publicKey))
        {
            throw new PkiException(PkiErrorKind.UnsupportedKey, "The CSR key is not ECDSA P-256.");
        }

        // Re-create the PublicKey from its SPKI bytes so nothing else from the parsed request travels with it.
        return PublicKey.CreateFromSubjectPublicKeyInfo(publicKey.ExportSubjectPublicKeyInfo(), out _);
    }

    private static PkiException Classify(string csrPem, Exception original)
    {
        try
        {
            _ = CertificateRequest.LoadSigningRequestPem(csrPem, PkiDefaults.SignatureHash, CertificateRequestLoadOptions.SkipSignatureValidation);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return new PkiException(PkiErrorKind.InvalidCsr, "The CSR is not a valid PKCS#10 PEM.", original);
        }

        return new PkiException(PkiErrorKind.CsrSignatureInvalid, "The CSR signature does not verify with its public key (no proof of possession).", original);
    }
}
