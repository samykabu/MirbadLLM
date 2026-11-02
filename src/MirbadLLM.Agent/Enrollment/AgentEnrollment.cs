using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Pki;

namespace MirbadLLM.Agent.Enrollment;

/// <summary>
/// The agent side of enrollment (design §3.2): <c>join</c> with a token and the CA hash, and the DGX bootstrap mode
/// that reads the bundle the master wrote. In both:
/// <list type="number">
/// <item>a P-256 key is generated here and never leaves the node; the request carries only its CSR;</item>
/// <item>the token is sent only over a TLS connection whose server certificate passed
/// <see cref="PeerValidator.ForEnrollListener(string, IEnumerable{X509Certificate2}?, TimeProvider?)"/>: chain to the
/// CA whose SPKI hash is the expected one (found in the listener certificate's private extension, D35), SAN
/// <c>spiffe://mirbadllm/master</c>, EKU serverAuth. The handler is built with <see cref="PkiTls.CreateClientOptions"/>,
/// no proxy, no redirects, no cookies; a refused certificate fails the handshake before any request byte is sent;</item>
/// <item>the answer is checked before anything is stored: the returned CA set must contain the expected hash (and only
/// that CA is stored), the certificate must chain to it, carry this node's SPIFFE id and this key, and the uid must be
/// well-formed;</item>
/// <item>the identity is written with <see cref="EnrollmentStateFiles"/> (key first, certificate last).</item>
/// </list>
/// Nothing is retried automatically (design §4.5, <see cref="AgentCallClass.Enroll"/>). The running agent picks the new
/// files up with <c>FileAgentCredentials.Reload</c> (or by starting after this returns).
/// </summary>
public sealed class AgentEnrollment
{
    /// <summary>Largest answer accepted from the enroll listener.</summary>
    public const int MaxResponseBytes = 256 * 1024;

    private const int MaxBundleBytes = 64 * 1024;
    private const int MaxNodeUidLength = 64;

    private readonly TimeProvider _time;
    private readonly ILogger<AgentEnrollment> _logger;

    /// <summary>Creates the enrollment client.</summary>
    public AgentEnrollment(TimeProvider time, ILogger<AgentEnrollment> logger)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _time = time;
        _logger = logger;
    }

    /// <summary>The enrollment log (shared with <see cref="AgentRekey"/>).</summary>
    internal ILogger Logger => _logger;

    /// <summary>
    /// <c>mirbadllm-agent join</c>: enrolls with an operator token. Refuses to replace an identity already in the state
    /// directory unless <see cref="JoinParameters.Force"/>. Throws <see cref="AgentEnrollmentException"/>.
    /// </summary>
    public async Task<EnrollmentOutcome> JoinAsync(JoinParameters parameters, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!Spki.TryParseJoinHash(parameters.CaHash, out string caPin))
        {
            throw new AgentEnrollmentException(EnrollmentFailure.InvalidArguments, "The CA hash is not a SHA-256 value (expected sha256:<base64>).");
        }

        CheckCommon(parameters.EnrollUrl, parameters.Token, parameters.NodeId, parameters.StateDirectory);
        if (!parameters.Force && EnrollmentStateFiles.HasIdentity(parameters.StateDirectory))
        {
            throw new AgentEnrollmentException(EnrollmentFailure.AlreadyEnrolled,
                $"'{parameters.StateDirectory}' already holds an agent identity; pass --force to replace it.");
        }

        return await EnrollAsync(parameters.EnrollUrl, parameters.Token, parameters.NodeId, caPin, knownCa: null,
            parameters.StateDirectory, parameters.Addresses, parameters.Capabilities, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Bootstrap mode: when <c>{BundleDirectory}/bootstrap.json</c> exists, enrolls with it and deletes it; returns null
    /// when there is no bundle. The bundle's CA certificate must hash to the bundle's <c>caSpkiSha256</c>; that hash is
    /// the one the enroll listener and the answer are checked against. An existing identity is replaced: the master
    /// writes a bundle only while it has no enrollment for the node. Throws <see cref="AgentEnrollmentException"/>; the
    /// bundle is kept on failure (the master replaces it with a fresh token).
    /// </summary>
    public async Task<EnrollmentOutcome?> BootstrapAsync(BootstrapParameters parameters, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        string path = Path.Combine(parameters.BundleDirectory, BootstrapBundle.FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        BootstrapBundle bundle = ReadBundle(path);
        if (parameters.ExpectedNodeId is not null && !string.Equals(bundle.NodeId, parameters.ExpectedNodeId, StringComparison.Ordinal))
        {
            throw new AgentEnrollmentException(EnrollmentFailure.BundleInvalid,
                $"The bootstrap bundle is for node '{bundle.NodeId}', not '{parameters.ExpectedNodeId}'.");
        }

        if (!Spki.TryParseJoinHash(bundle.CaSpkiSha256, out string caPin))
        {
            throw new AgentEnrollmentException(EnrollmentFailure.BundleInvalid, "The bootstrap bundle's CA hash is not a SHA-256 value.");
        }

        CheckCommon(bundle.EnrollEndpoint, bundle.Token, bundle.NodeId, parameters.StateDirectory);
        using X509Certificate2 bundleCa = LoadBundleCa(bundle.CaCertPem, caPin);
        EnrollmentOutcome outcome = await EnrollAsync(bundle.EnrollEndpoint, bundle.Token, bundle.NodeId, caPin, bundleCa,
            parameters.StateDirectory, parameters.Addresses, parameters.Capabilities, ct).ConfigureAwait(false);

        try
        {
            File.Delete(path);
            EnrollmentLog.BundleDeleted(_logger, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EnrollmentLog.BundleDeleteFailed(_logger, ex, path);
        }

        return outcome;
    }

    private async Task<EnrollmentOutcome> EnrollAsync(
        Uri endpoint,
        string token,
        string nodeId,
        string caPin,
        X509Certificate2? knownCa,
        string stateDirectory,
        IReadOnlyList<string> addresses,
        NodeCapabilities? capabilities,
        CancellationToken ct)
    {
        EnrollmentLog.Enrolling(_logger, nodeId, endpoint, caPin);
        using ECDsa key = PkiDefaults.CreateKey();
        string keyPin = Spki.Sha256(key);
        EnrollRequest request = new()
        {
            Token = token,
            NodeId = nodeId,
            CsrPem = CertificateSigningRequests.CreatePem(key, nodeId),
            Addresses = addresses ?? [],
            Capabilities = capabilities,
        };

        EnrollResponse response = await SendAsync(endpoint, AgentRoutes.Enroll.Path, RefusedMessage, caPin, knownCa, request, ct).ConfigureAwait(false);
        (string caPem, string certificatePem, DateTimeOffset notAfter) = CheckResponse(response, nodeId, caPin, keyPin);

        EnrollmentStateFiles.Write(stateDirectory, key.ExportPkcs8PrivateKeyPem(), caPem, response.NodeUid, certificatePem);
        EnrollmentLog.Enrolled(_logger, nodeId, response.NodeUid, keyPin, notAfter, response.PendingApproval);
        return new EnrollmentOutcome(nodeId, response.NodeUid, keyPin, notAfter, response.PendingApproval);
    }

    private const string RefusedMessage =
        "The master refused the enrollment (token unknown, expired, already used or for another node). Ask for a new join command.";

    /// <summary>
    /// Posts <paramref name="request"/> to <paramref name="path"/> on the enroll listener over the CA-hash-checked TLS
    /// connection described above (shared by <c>/enroll</c> and <c>/enroll/rekey</c>); <paramref name="refusedMessage"/> is
    /// the message of a 403.
    /// <para>The policy's connect timeout bounds the TCP connect only (<see cref="ConnectTcpAsync"/>): a master that does
    /// not accept the connection is unreachable after 3 s. The TLS handshake, which includes the CA-hash check of the
    /// listener certificate (a custom-root <see cref="X509Chain"/> build: ~0.1 s on a quiet Windows machine, seconds when
    /// other processes build chains at the same time), is bounded by the call's total instead, so a slow but reachable
    /// master is not reported as unreachable.</para>
    /// </summary>
    internal async Task<EnrollResponse> SendAsync<TRequest>(
        Uri endpoint,
        string path,
        string refusedMessage,
        string caPin,
        X509Certificate2? knownCa,
        TRequest request,
        CancellationToken ct)
    {
        PeerValidationResult? refusal = null;
        PeerValidator validator = PeerValidator
            .ForEnrollListener(caPin, knownCa is null ? null : [knownCa], _time)
            .WithRejectionObserver(result => refusal = result);
        AgentCallPolicy policy = AgentCallPolicies.For(AgentCallClass.Enroll);
        TimeSpan total = policy.Total ?? TimeSpan.FromSeconds(30);

        using SocketsHttpHandler handler = new()
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = total,
            ConnectCallback = (context, token) => ConnectTcpAsync(context.DnsEndPoint, policy.Connect, token),
            SslOptions = PkiTls.CreateClientOptions(null, new PeerValidatorSource(validator), endpoint.IdnHost),
        };
        using HttpClient client = new(handler, disposeHandler: false)
        {
            Timeout = total,
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
        using HttpRequestMessage message = new(HttpMethod.Post, new Uri(endpoint, path))
        {
            Content = JsonContent.Create(request, options: AgentJson.Options),
        };

        HttpResponseMessage answer;
        try
        {
            answer = await client.SendAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            if (refusal is { } refused)
            {
                EnrollmentLog.ServerRefused(_logger, refused.Failure, refused.Detail ?? "", refused.Subject ?? "-", refused.SpkiSha256 ?? "-");
                throw new AgentEnrollmentException(EnrollmentFailure.ServerNotTrusted,
                    $"The enroll listener at {endpoint} is not the master with CA {Spki.ToJoinHash(caPin)} ({refused.Failure}); the token was not sent.", ex);
            }

            throw new AgentEnrollmentException(EnrollmentFailure.Unreachable, $"The enroll listener at {endpoint} could not be reached: {ex.Message}", ex);
        }

        using (answer)
        {
            switch (answer.StatusCode)
            {
                case HttpStatusCode.OK:
                    break;
                case HttpStatusCode.Forbidden:
                    throw new AgentEnrollmentException(EnrollmentFailure.Refused, refusedMessage);
                case HttpStatusCode.TooManyRequests:
                    throw new AgentEnrollmentException(EnrollmentFailure.RateLimited, "Too many enroll attempts from this address. Try again in a minute.");
                default:
                    throw new AgentEnrollmentException(EnrollmentFailure.Rejected, $"The master answered {(int)answer.StatusCode} to the enroll request.");
            }

            try
            {
                byte[] body = await answer.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                return JsonSerializer.Deserialize<EnrollResponse>(body, AgentJson.Options)
                    ?? throw new AgentEnrollmentException(EnrollmentFailure.InvalidResponse, "The master's enroll answer is empty.");
            }
            catch (JsonException ex)
            {
                throw new AgentEnrollmentException(EnrollmentFailure.InvalidResponse, "The master's enroll answer is not valid JSON of the expected shape.", ex);
            }
        }
    }

    /// <summary>
    /// The TCP connect of <see cref="SendAsync"/>, bounded by <paramref name="timeout"/> (the handler's own connect timeout
    /// would also cover the TLS handshake). A timeout is a <see cref="TimeoutException"/>, which the handler reports as an
    /// <see cref="HttpRequestException"/> (unreachable).
    /// </summary>
    private static async ValueTask<Stream> ConnectTcpAsync(DnsEndPoint endpoint, TimeSpan timeout, CancellationToken ct)
    {
        using CancellationTokenSource connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connect.CancelAfter(timeout);
        Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, connect.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TimeoutException($"No TCP connection to {endpoint.Host}:{endpoint.Port} within {timeout.TotalSeconds:0.#} s.", ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Checks the answer; returns the CA PEM to store (only the expected CA), the certificate PEM and its expiry.</summary>
    internal (string CaPem, string CertificatePem, DateTimeOffset NotAfter) CheckResponse(EnrollResponse response, string nodeId, string caPin, string keyPin)
    {
        if (!Protocol.SpiffeIdMatches(response.MasterSpiffeId, Protocol.MasterSpiffeId))
        {
            throw Invalid("it names another master identity");
        }

        if (!IsWellFormedUid(response.NodeUid))
        {
            throw Invalid("the node uid is missing or malformed");
        }

        try
        {
            using TrustAnchorSet returned = TrustAnchorSet.FromPem([response.CaCertPem ?? ""]);
            if (!returned.ContainsSpki(caPin))
            {
                throw Invalid($"its CA is not {Spki.ToJoinHash(caPin)}");
            }

            // Only the expected CA becomes the agent's trust anchor (a second CA in the answer is not trusted).
            X509Certificate2 expected = returned.Anchors.First(a => Spki.Matches(a, caPin));
            using TrustAnchorSet trust = TrustAnchorSet.Create([expected]);
            using X509Certificate2 certificate = X509Certificate2.CreateFromPem(response.AgentCertPem ?? "");
            PeerValidationResult check = PeerValidator.ForAgent(nodeId, trust, [keyPin], _time).Validate(certificate);
            if (!check.IsValid)
            {
                throw Invalid($"the certificate fails validation ({check.Failure}: {check.Detail})");
            }

            return (expected.ExportCertificatePem() + "\n", certificate.ExportCertificatePem() + "\n",
                new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero));
        }
        catch (Exception ex) when (ex is PkiException or CryptographicException or ArgumentException)
        {
            throw new AgentEnrollmentException(EnrollmentFailure.InvalidResponse, "The master's enroll answer was refused: its CA or certificate does not parse.", ex);
        }
    }

    internal static AgentEnrollmentException Invalid(string reason) =>
        new(EnrollmentFailure.InvalidResponse, $"The master's enroll answer was refused: {reason}. Nothing was stored.");

    internal static bool IsWellFormedUid(string? uid) =>
        !string.IsNullOrEmpty(uid) && uid.Length <= MaxNodeUidLength && uid.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    internal static void CheckCommon(Uri? endpoint, string? token, string? nodeId, string? stateDirectory)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new AgentEnrollmentException(EnrollmentFailure.InvalidArguments, "The enroll URL must be an absolute https:// URL.");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new AgentEnrollmentException(EnrollmentFailure.InvalidArguments, "The join token is empty.");
        }

        if (!Protocol.IsValidNodeId(nodeId))
        {
            throw new AgentEnrollmentException(EnrollmentFailure.InvalidArguments, $"The node id must match {Protocol.NodeIdPattern}.");
        }

        if (string.IsNullOrWhiteSpace(stateDirectory))
        {
            throw new AgentEnrollmentException(EnrollmentFailure.InvalidArguments, "The state directory is required.");
        }
    }

    private static BootstrapBundle ReadBundle(string path)
    {
        try
        {
            FileInfo info = new(path);
            if (info.Length > MaxBundleBytes)
            {
                throw new AgentEnrollmentException(EnrollmentFailure.BundleInvalid, "The bootstrap bundle is too large.");
            }

            return JsonSerializer.Deserialize<BootstrapBundle>(File.ReadAllBytes(path), AgentJson.Options)
                ?? throw new AgentEnrollmentException(EnrollmentFailure.BundleInvalid, "The bootstrap bundle is empty.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new AgentEnrollmentException(EnrollmentFailure.BundleInvalid, $"The bootstrap bundle '{path}' could not be read.", ex);
        }
    }

    private static X509Certificate2 LoadBundleCa(string? caPem, string caPin)
    {
        try
        {
            using TrustAnchorSet set = TrustAnchorSet.FromPem([caPem ?? ""]);
            if (set.Anchors.Count != 1 || !set.ContainsSpki(caPin))
            {
                throw new AgentEnrollmentException(EnrollmentFailure.BundleInvalid, "The bootstrap bundle's CA certificate does not match its CA hash.");
            }

            return X509CertificateLoader.LoadCertificate(set.Anchors[0].RawData);
        }
        catch (Exception ex) when (ex is PkiException or CryptographicException or ArgumentException)
        {
            throw new AgentEnrollmentException(EnrollmentFailure.BundleInvalid, "The bootstrap bundle's CA certificate is invalid.", ex);
        }
    }
}
