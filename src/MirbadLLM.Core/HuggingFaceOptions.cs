namespace MirbadLLM.Core;

public enum HubTrust
{
    FleetCa,
    System,
}

public sealed class HuggingFaceOptions
{
    public string? Endpoint { get; set; }
    public HubTrust? Trust { get; set; }

    public HubOrigin Resolve(string? fleetEndpoint = null, HubTrust? fleetTrust = null) =>
        Endpoint is not null || Trust is not null ? HubOrigin.Parse(Endpoint, Trust) : HubOrigin.Parse(fleetEndpoint, fleetTrust);
}

/// <summary>A validated, credential-free Hub origin and its TLS policy.</summary>
public sealed record HubOrigin
{
    public static readonly Uri OfficialEndpoint = new("https://huggingface.co/");
    public static readonly HubOrigin Official = new(OfficialEndpoint, HubTrust.System);

    private HubOrigin(Uri endpoint, HubTrust trust)
    {
        Endpoint = endpoint;
        Trust = trust;
    }

    public Uri Endpoint { get; }
    public HubTrust Trust { get; }
    public bool IsOfficial => Endpoint == OfficialEndpoint;

    public static HubOrigin Parse(string? endpoint, HubTrust? trust)
    {
        if (endpoint is null && trust is null)
        {
            return Official;
        }

        if (endpoint is null || !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            endpoint.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            endpoint[8..].TrimEnd('/').IndexOfAny(['/', '@', '?', '#', '\\', '%']) >= 0 ||
            endpoint.EndsWith("//", StringComparison.Ordinal) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/" ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            throw new ArgumentException("HuggingFace:Endpoint must be an absolute HTTPS origin with no credentials, query, fragment or path prefix.");
        }

        if (trust is { } selected && !Enum.IsDefined(selected))
        {
            throw new ArgumentException("HuggingFace:Trust must be FleetCa or System.");
        }

        if (uri == OfficialEndpoint)
        {
            if (trust == HubTrust.FleetCa)
            {
                throw new ArgumentException("The official Hugging Face origin requires System trust.");
            }

            return Official;
        }

        return new HubOrigin(uri, trust ?? HubTrust.FleetCa);
    }
}
