using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MirbadLLM.Pki;

/// <summary>Subject Alternative Names of a certificate, read strictly (design §4.2: exact SAN URI matching).</summary>
/// <param name="Uris">URI names in certificate order.</param>
/// <param name="DnsNames">DNS names.</param>
/// <param name="IpAddresses">IP addresses.</param>
public sealed record SanEntries(IReadOnlyList<string> Uris, IReadOnlyList<string> DnsNames, IReadOnlyList<IPAddress> IpAddresses);

/// <summary>
/// SAN helpers shared by issuance (address validation) and validation (strict reading). A certificate with more than one
/// SAN extension, or a SAN that does not parse, has no usable SANs.
/// </summary>
public static class CertificateSans
{
    private static readonly Asn1Tag UriTag = new(TagClass.ContextSpecific, 6);

    /// <summary>Reads the SANs; null when the certificate has none, several SAN extensions, or a malformed one.</summary>
    public static SanEntries? Read(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        X509Extension? san = null;
        foreach (X509Extension extension in certificate.Extensions)
        {
            if (extension.Oid?.Value == PkiDefaults.SubjectAltNameOid)
            {
                if (san is not null)
                {
                    return null;
                }

                san = extension;
            }
        }

        if (san is null)
        {
            return null;
        }

        try
        {
            List<string> uris = [];
            List<string> dns = [];
            List<IPAddress> ips = [];
            AsnReader outer = new(san.RawData, AsnEncodingRules.DER);
            AsnReader names = outer.ReadSequence();
            outer.ThrowIfNotEmpty();
            while (names.HasData)
            {
                Asn1Tag tag = names.PeekTag();
                if (tag.HasSameClassAndValue(UriTag) && !tag.IsConstructed)
                {
                    uris.Add(names.ReadCharacterString(UniversalTagNumber.IA5String, UriTag));
                }
                else
                {
                    names.ReadEncodedValue();
                }
            }

            X509SubjectAlternativeNameExtension typed = new(san.RawData, san.Critical);
            dns.AddRange(typed.EnumerateDnsNames());
            ips.AddRange(typed.EnumerateIPAddresses());
            return new SanEntries(uris, dns, ips);
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// The single URI SAN (the SPIFFE id) of a certificate; null when there is not exactly one. Comparison against an
    /// expected id must be ordinal and exact.
    /// </summary>
    public static string? GetSingleUri(X509Certificate2 certificate)
    {
        SanEntries? entries = Read(certificate);
        return entries is { Uris.Count: 1 } ? entries.Uris[0] : null;
    }

    /// <summary>
    /// Splits operator-confirmed addresses into literal IPs and DNS names for a SAN. IPs must be complete literals
    /// (dotted-quad IPv4 or IPv6 without a scope id); DNS names must be valid host names (no wildcards) and are
    /// lower-cased. Duplicates are dropped. Throws <see cref="PkiException"/> (<see cref="PkiErrorKind.InvalidAddress"/>)
    /// for anything else.
    /// </summary>
    public static (IReadOnlyList<IPAddress> Ips, IReadOnlyList<string> DnsNames) ParseAddresses(IEnumerable<string>? addresses)
    {
        List<IPAddress> ips = [];
        List<string> dns = [];
        if (addresses is null)
        {
            return (ips, dns);
        }

        foreach (string raw in addresses)
        {
            string value = raw?.Trim() ?? "";
            if (TryParseIpLiteral(value, out IPAddress? ip))
            {
                if (!ips.Contains(ip))
                {
                    ips.Add(ip);
                }
            }
            else if (IsValidDnsName(value))
            {
                string lower = value.ToLowerInvariant();
                if (!dns.Contains(lower, StringComparer.Ordinal))
                {
                    dns.Add(lower);
                }
            }
            else
            {
                throw new PkiException(PkiErrorKind.InvalidAddress, $"'{Truncate(value)}' is not an IP literal or a valid DNS name.");
            }
        }

        if (ips.Count + dns.Count > PkiDefaults.MaxSanAddresses)
        {
            throw new PkiException(PkiErrorKind.InvalidAddress, $"At most {PkiDefaults.MaxSanAddresses} SAN addresses are allowed.");
        }

        return (ips, dns);
    }

    /// <summary>True for a complete IP literal: four dotted decimal octets, or IPv6 without a scope id.</summary>
    public static bool TryParseIpLiteral(string? value, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IPAddress? address)
    {
        address = null;
        if (string.IsNullOrEmpty(value) || !IPAddress.TryParse(value, out IPAddress? parsed))
        {
            return false;
        }

        if (parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            string[] parts = value.Split('.');
            if (parts.Length != 4 || parts.Any(p => p.Length is 0 or > 3 || !p.All(char.IsAsciiDigit)))
            {
                return false;
            }
        }
        else if (parsed.AddressFamily != AddressFamily.InterNetworkV6 || value.Contains('%', StringComparison.Ordinal) || parsed.ScopeId != 0)
        {
            return false;
        }

        address = parsed;
        return true;
    }

    /// <summary>True for a host name of LDH labels (1–63 chars, no leading/trailing hyphen), at most 253 chars, no wildcard, not all-numeric.</summary>
    public static bool IsValidDnsName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 253)
        {
            return false;
        }

        string[] labels = value.Split('.');
        foreach (string label in labels)
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (char c in label)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                {
                    return false;
                }
            }
        }

        // An all-numeric last label would be confused with an (incomplete) IPv4 literal.
        return !labels[^1].All(char.IsAsciiDigit);
    }

    private static string Truncate(string value) => value.Length <= 64 ? value : value[..64] + "…";
}
