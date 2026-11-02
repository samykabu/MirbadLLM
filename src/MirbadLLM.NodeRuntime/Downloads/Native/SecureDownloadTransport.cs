using System.Net;
using System.Net.Http.Headers;

namespace MirbadLLM.NodeRuntime.Downloads.Native;

/// <summary>
/// GET requests for downloads with the design-p3p4 §A.8 rules: HTTPS only (plain HTTP only to a loopback host when
/// <see cref="NativeDownloadOptions.AllowLoopbackHttp"/>), redirects followed here (at most
/// <see cref="NativeDownloadOptions.MaxRedirects"/>) and each target checked by the same rule, and the credential sent only
/// to its own origin (scheme, host and port of <see cref="CredentialOrigin"/>), never to a CDN a redirect points at.
/// <para>Works with an <see cref="HttpClient"/> that follows redirects itself too (then the final URI is checked; .NET
/// already drops <c>Authorization</c> on a redirect and refuses HTTPS → HTTP), but the agent registers its native client
/// without automatic redirects so every hop is checked before it is requested.</para>
/// <para>Reused by the Ollama-registry puller (MN-221) through <see cref="RangeDownloader"/>.</para>
/// </summary>
public sealed class SecureDownloadTransport
{
    private readonly HttpClient _http;
    private readonly NativeDownloadOptions _options;

    /// <summary>Creates the transport.</summary>
    /// <param name="http">Client used for every request.</param>
    /// <param name="credentialOrigin">Origin that may receive the credential (the Hugging Face endpoint); null = none.</param>
    /// <param name="options">Rules; defaults when null.</param>
    public SecureDownloadTransport(HttpClient http, Uri? credentialOrigin, NativeDownloadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _options = options ?? new NativeDownloadOptions();
        CredentialOrigin = credentialOrigin;
    }

    /// <summary>The origin that receives the credential.</summary>
    public Uri? CredentialOrigin { get; }

    /// <summary>The rules in use.</summary>
    public NativeDownloadOptions Options => _options;

    /// <summary>True when <paramref name="uri"/> may be requested: absolute HTTPS, or HTTP to loopback when allowed.</summary>
    public bool IsAllowed(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        return uri.Scheme == Uri.UriSchemeHttp && _options.AllowLoopbackHttp && IsLoopbackHost(uri);
    }

    /// <summary>Why <paramref name="uri"/> is refused, or null (host only: the query of a signed CDN URL is never printed).</summary>
    public string? RefusalReason(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return IsAllowed(uri)
            ? null
            : $"{(uri.IsAbsoluteUri ? uri.Scheme + "://" + uri.Authority : "a relative URL")} is not allowed: downloads use HTTPS only.";
    }

    /// <summary>
    /// Sends a GET to <paramref name="url"/>, following redirects under the rules. The caller disposes the response.
    /// Throws <see cref="DownloadRefusedException"/> for a refused target or too many redirects; transport errors
    /// (<see cref="HttpRequestException"/>, timeouts) propagate for the caller to classify.
    /// </summary>
    /// <param name="url">Absolute URL.</param>
    /// <param name="credential">Sent only to <see cref="CredentialOrigin"/>.</param>
    /// <param name="configure">Adds request headers (e.g. <c>Range</c>) on every hop.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<HttpResponseMessage> GetAsync(Uri url, AuthenticationHeaderValue? credential, Action<HttpRequestMessage>? configure, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        Uri current = url;
        for (int hop = 0; ; hop++)
        {
            if (RefusalReason(current) is { } refused)
            {
                throw new DownloadRefusedException(refused);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            configure?.Invoke(request);
            if (credential is not null && SameOrigin(current, CredentialOrigin))
            {
                request.Headers.Authorization = credential;
            }

            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                response.Dispose();
                if (hop >= _options.MaxRedirects)
                {
                    throw new DownloadRefusedException($"More than {_options.MaxRedirects} redirects for {Describe(url)}.");
                }

                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            // A client that followed redirects itself: check where it ended up.
            if (response.RequestMessage?.RequestUri is { } final && final != current && RefusalReason(final) is { } finalRefused)
            {
                response.Dispose();
                throw new DownloadRefusedException(finalRefused);
            }

            return response;
        }
    }

    /// <summary>Scheme, host and port of <paramref name="uri"/> (never the path or query).</summary>
    public static string Describe(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsAbsoluteUri ? uri.Scheme + "://" + uri.Authority : "(relative URL)";
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool SameOrigin(Uri target, Uri? origin) =>
        origin is not null && origin.IsAbsoluteUri &&
        string.Equals(target.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(target.IdnHost, origin.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        target.Port == origin.Port;

    private static bool IsLoopbackHost(Uri uri) =>
        uri.IsLoopback || (IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
}
