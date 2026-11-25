using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using MirbadLLM.Core;

namespace MirbadLLM.NodeRuntime.Downloads.Native;

/// <summary>Public Hub transport and per-operation clients for anonymous mirror traffic.</summary>
public sealed class HubHttpClients(HttpClient official, Func<IReadOnlyList<string>> anchors)
{
    public HttpClient Official { get; } = official;
    public IReadOnlyList<string> AnchorPems => anchors();

    public static HttpMessageHandler PublicHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    public HttpClient OpenMirror(HubOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (origin.IsOfficial)
        {
            throw new ArgumentException("OpenMirror requires a custom Hub origin.", nameof(origin));
        }

        return new HttpClient(new OriginHandler(origin, anchors)) { BaseAddress = origin.Endpoint, Timeout = TimeSpan.FromSeconds(30) };
    }

    private sealed class OriginHandler(HubOrigin origin, Func<IReadOnlyList<string>> anchors) : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private readonly List<HttpMessageInvoker> _retired = [];
        private HttpMessageInvoker? _invoker;
        private IReadOnlyList<string> _roots = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { } uri || !SecureDownloadTransport.SameOrigin(uri, origin.Endpoint) ||
                uri.UserInfo.Length != 0 || request.Headers.Authorization is not null || request.Headers.Host is not null)
            {
                throw new HttpRequestException("Mirror traffic must remain anonymous on the configured origin.");
            }

            var roots = origin.Trust == HubTrust.FleetCa ? anchors() : [];
            lock (_gate)
            {
                if (_invoker is null || !_roots.SequenceEqual(roots, StringComparer.Ordinal))
                {
                    if (_invoker is not null)
                    {
                        _retired.Add(_invoker);
                    }

                    var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
                    if (origin.Trust == HubTrust.FleetCa)
                    {
                        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                            certificate is not null && ValidateFleetLeaf(certificate, origin.Endpoint.IdnHost.Trim('[', ']'), errors, roots);
                    }

                    _roots = roots;
                    _invoker = new HttpMessageInvoker(handler);
                }

                return _invoker.SendAsync(request, cancellationToken);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _invoker?.Dispose();
                foreach (var retired in _retired)
                {
                    retired.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }

    public SecureDownloadTransport OfficialTransport(NativeDownloadOptions? options = null) =>
        new(Official, Official.BaseAddress ?? HubOrigin.OfficialEndpoint, options, destinationAllowed: IsPublicHub);

    public static bool ValidateFleetLeaf(X509Certificate2 certificate, string host, SslPolicyErrors errors, IReadOnlyList<string> roots)
    {
        if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None ||
            !certificate.MatchesHostname(host, allowCommonName: false))
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new("1.3.6.1.5.5.7.3.1"));
        try
        {
            foreach (var pem in roots)
            {
                chain.ChainPolicy.CustomTrustStore.Add(X509Certificate2.CreateFromPem(pem));
            }

            return chain.Build(certificate);
        }
        finally
        {
            foreach (var root in chain.ChainPolicy.CustomTrustStore)
            {
                root.Dispose();
            }
        }
    }

    private bool IsPublicHub(Uri uri)
    {
        if (SecureDownloadTransport.SameOrigin(uri, Official.BaseAddress ?? HubOrigin.OfficialEndpoint))
        {
            return true;
        }

        return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
            (uri.IdnHost.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase) ||
             uri.IdnHost.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase));
    }
}
