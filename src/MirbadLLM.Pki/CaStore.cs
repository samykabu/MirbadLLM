namespace MirbadLLM.Pki;

/// <summary>
/// Where the master CA comes from (design §10.2): created on first boot in <c>{DataPath}/pki/</c> (<c>ca.key</c> 0600), or
/// supplied by configuration (<c>MirbadLLM__Pki__CaCertPem</c> / <c>MirbadLLM__Pki__CaKeyPem</c>, topology b). If both
/// exist and differ, the master refuses to start. Every load checks the certificate and key are a matching pair.
/// </summary>
public static class CaStore
{
    /// <summary>CA certificate file name inside the PKI directory.</summary>
    public const string CertificateFileName = "ca.crt";

    /// <summary>CA private key file name inside the PKI directory (0600).</summary>
    public const string KeyFileName = "ca.key";

    /// <summary>
    /// Loads or creates the CA.
    /// <list type="bullet">
    /// <item>Configured and on disk: both are loaded; if they differ → <see cref="PkiErrorKind.CaConflict"/>.</item>
    /// <item>Configured only: used as is; the key is not copied to disk.</item>
    /// <item>On disk only: loaded.</item>
    /// <item>Neither: a new CA is created and written (key first, 0600; then the certificate).</item>
    /// </list>
    /// Half-present material (only a certificate or only a key, in configuration or on disk) is refused with
    /// <see cref="PkiErrorKind.CaMaterialInvalid"/> rather than replaced.
    /// </summary>
    public static CertificateAuthority LoadOrCreate(string pkiDirectory, string? configuredCertificatePem, string? configuredKeyPem, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(pkiDirectory);
        bool hasConfiguredCert = !string.IsNullOrWhiteSpace(configuredCertificatePem);
        bool hasConfiguredKey = !string.IsNullOrWhiteSpace(configuredKeyPem);
        if (hasConfiguredCert != hasConfiguredKey)
        {
            throw new PkiException(PkiErrorKind.CaMaterialInvalid, "Both the configured CA certificate and the configured CA key must be set, or neither.");
        }

        string certPath = Path.Combine(pkiDirectory, CertificateFileName);
        string keyPath = Path.Combine(pkiDirectory, KeyFileName);
        string? diskCert = PkiFiles.ReadIfExists(certPath);
        string? diskKey = PkiFiles.ReadIfExists(keyPath);
        if ((diskCert is null) != (diskKey is null))
        {
            throw new PkiException(PkiErrorKind.CaMaterialInvalid,
                $"Only one of {CertificateFileName} and {KeyFileName} exists in '{pkiDirectory}'; restore the pair or remove both.");
        }

        CertificateAuthority? configured = hasConfiguredCert ? CertificateAuthority.Load(configuredCertificatePem!, configuredKeyPem!, time) : null;
        if (diskCert is not null)
        {
            CertificateAuthority onDisk;
            try
            {
                onDisk = CertificateAuthority.Load(diskCert, diskKey!, time);
            }
            catch
            {
                configured?.Dispose();
                throw;
            }

            if (configured is null)
            {
                return onDisk;
            }

            bool same = configured.Certificate.RawData.AsSpan().SequenceEqual(onDisk.Certificate.RawData);
            onDisk.Dispose();
            if (!same)
            {
                configured.Dispose();
                throw new PkiException(PkiErrorKind.CaConflict,
                    $"The configured CA and the CA in '{pkiDirectory}' differ; refusing to start. Remove one of them.");
            }

            return configured;
        }

        if (configured is not null)
        {
            return configured;
        }

        CertificateAuthority created = CertificateAuthority.Create(time);
        try
        {
            PkiFiles.EnsureDirectory(pkiDirectory);
            PkiFiles.WriteSecret(keyPath, created.ExportPrivateKeyPem());
            PkiFiles.WritePublic(certPath, created.CertificatePem);
            return created;
        }
        catch
        {
            created.Dispose();
            throw;
        }
    }
}
