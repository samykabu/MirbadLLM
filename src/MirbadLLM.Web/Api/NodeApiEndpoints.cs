using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Audit;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Orchestration.Pki;
using MirbadLLM.Pki;
using MirbadLLM.Web.Enrollment;

namespace MirbadLLM.Web.Api;

/// <summary>
/// Settings of the node admin API (MN-102, design §3.2, §5.6), section <see cref="SectionName"/>. Lifetimes are checked
/// against the registry's own bounds (<see cref="JsonNodeRegistry.MinJoinTokenLifetime"/> /
/// <see cref="JsonNodeRegistry.MaxJoinTokenLifetime"/>) when the options are first read.
/// </summary>
public sealed class NodeApiOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "MirbadLLM:NodeApi";

    /// <summary>
    /// The enroll listener URL a joining machine uses (design §3.2, e.g. <c>https://mirbad.example.com:8443</c>). Null =
    /// <c>https://{host of the admin request}:{MirbadLLM:Enrollment:Port}</c>.
    /// </summary>
    public Uri? EnrollUrl { get; set; }

    /// <summary>
    /// The master's main address (UI port) the join and rekey commands download the agent bundles from
    /// (<c>{AgentBundleUrl}/agent/bundles/{rid}.tar.gz</c>), e.g. <c>http://192.0.2.191:8080</c>: plain HTTP is fine, the
    /// command checks each bundle's SHA-256. Null = the scheme, host and port of the admin request (the address the
    /// operator's browser uses for the UI).
    /// </summary>
    public Uri? AgentBundleUrl { get; set; }

    /// <summary>
    /// Where the agent bundles (<c>linux-x64.tar.gz</c>, <c>linux-arm64.tar.gz</c>) are read from at startup. Null =
    /// <c>agent-bundles</c> next to the master's binaries (<c>/app/agent-bundles</c> in the image).
    /// </summary>
    public string? AgentBundleDirectory { get; set; }

    /// <summary>Lifetime of a join token when the request gives none (design §3.2: 60 minutes).</summary>
    public TimeSpan DefaultJoinTokenLifetime { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Used or expired join-token records older than this are pruned from <c>nodes.json</c> whenever a join token is
    /// created (W1 review MN-090 "prune after N days"). Zero or less = never prune.
    /// </summary>
    public TimeSpan JoinTokenRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How long a revoke with <c>unplaceModels</c> waits for each model to stop (best effort) before unplacing it.</summary>
    public TimeSpan RevokeStopTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Throws <see cref="OptionsValidationException"/> for a default lifetime outside the registry's bounds.</summary>
    public static void Validate(NodeApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.DefaultJoinTokenLifetime < JsonNodeRegistry.MinJoinTokenLifetime || options.DefaultJoinTokenLifetime > JsonNodeRegistry.MaxJoinTokenLifetime)
        {
            failures.Add($"{SectionName}:DefaultJoinTokenLifetime must be between {JsonNodeRegistry.MinJoinTokenLifetime} and {JsonNodeRegistry.MaxJoinTokenLifetime}.");
        }

        if (options.EnrollUrl is { } url && (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{SectionName}:EnrollUrl must be an absolute https:// URL.");
        }

        if (options.AgentBundleUrl is { } bundles
            && (!bundles.IsAbsoluteUri || (bundles.Scheme != Uri.UriSchemeHttp && bundles.Scheme != Uri.UriSchemeHttps)
                || !string.IsNullOrEmpty(bundles.Query) || !string.IsNullOrEmpty(bundles.Fragment)))
        {
            failures.Add($"{SectionName}:AgentBundleUrl must be an absolute http:// or https:// URL without a query.");
        }

        if (options.RevokeStopTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{SectionName}:RevokeStopTimeout must be positive.");
        }

        if (failures.Count > 0)
        {
            throw new OptionsValidationException(SectionName, typeof(NodeApiOptions), failures);
        }
    }
}

/// <summary>
/// A refusal of the node admin API: answered as ProblemDetails with <see cref="Code"/> (an <see cref="ErrorCodes"/> value)
/// in the <c>code</c> extension and <see cref="StatusCode"/>.
/// </summary>
public sealed class NodeApiException : Exception
{
    /// <summary>Creates the exception (prefer the code constructor).</summary>
    public NodeApiException()
        : this(ErrorCodes.InvalidRequest, "The request was refused.")
    {
    }

    /// <summary>Creates the exception with a message (code <c>invalid_request</c>).</summary>
    public NodeApiException(string message)
        : this(ErrorCodes.InvalidRequest, message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception (code <c>invalid_request</c>).</summary>
    public NodeApiException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = ErrorCodes.InvalidRequest;
        StatusCode = StatusCodes.Status400BadRequest;
        Extensions = new Dictionary<string, object?>();
    }

    /// <summary>Creates the refusal; the status is <see cref="ErrorCodes.ApiStatus"/> of the code unless given.</summary>
    public NodeApiException(string code, string message, int? statusCode = null, IReadOnlyDictionary<string, object?>? extensions = null)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode ?? ErrorCodes.ApiStatus(code);
        Extensions = extensions ?? new Dictionary<string, object?>();
    }

    /// <summary>The <see cref="ErrorCodes"/> value.</summary>
    public string Code { get; }

    /// <summary>HTTP status.</summary>
    public int StatusCode { get; }

    /// <summary>Extra ProblemDetails members (e.g. <c>modelIds</c> of <c>models_placed</c>).</summary>
    public IReadOnlyDictionary<string, object?> Extensions { get; }
}

/// <summary>Who called the node admin API, for re-authentication, audit lines and the enroll URL default.</summary>
/// <param name="Actor">An <see cref="AuditActors"/> value.</param>
/// <param name="ClientKey">The session hash or request IP (<see cref="AdminPasswordGuard.ClientKey"/>).</param>
/// <param name="RequestHost">Host name of the admin request (without port), for the default enroll URL; null when unknown.</param>
/// <param name="RequestBaseUrl">
/// The master's address as the admin reached it (<c>http://192.0.2.191:8080/</c>), for the default agent bundle URL; null
/// when unknown.
/// </param>
public sealed record NodeApiCaller(string Actor, string ClientKey, string? RequestHost = null, Uri? RequestBaseUrl = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public System.Security.Claims.ClaimsPrincipal? User { get; init; }

    /// <summary>The caller of <paramref name="http"/>: <c>admin:{session hash}</c> with a login cookie, else <c>api:{ip}</c>.</summary>
    public static NodeApiCaller From(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        string clientKey = AdminPasswordGuard.ClientKey(http);
        // One actor rule for every audit line (W7 review MN-126): the login's stable sid claim, hashed.
        string actor = AuditActorResolver.Resolve(http);
        string? host = http.Request.Host.HasValue ? http.Request.Host.Host : null;
        return new NodeApiCaller(actor, clientKey, host, host is null ? null : AgentBundleEndpoints.RequestBase(http)) { User = http.User };
    }
}

/// <summary>
/// Brings the master's connections in line with a node record right after the admin API wrote it (W4 review MN-096,
/// D61), instead of waiting for <see cref="NodeRegistrySync"/>'s poll.
/// </summary>
public interface INodeConnectionRefresher
{
    /// <summary>Refreshes the node's agent client and the node monitor. Never throws for refresh failures (the poll retries).</summary>
    Task RefreshAsync(string nodeId, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="INodeConnectionRefresher"/>: <see cref="AgentClientFactory.RefreshNodeAsync"/> then
/// <see cref="NodeMonitor.SyncRegistryAsync"/>, each only when registered; failures are logged and left to the poll.
/// </summary>
public sealed partial class NodeConnectionRefresher(IServiceProvider services, ILogger<NodeConnectionRefresher> logger) : INodeConnectionRefresher
{
    /// <inheritdoc />
    public async Task RefreshAsync(string nodeId, CancellationToken ct = default)
    {
        try
        {
            if (services.GetService<AgentClientFactory>() is { } clients)
            {
                await clients.RefreshNodeAsync(nodeId, ct);
            }

            if (services.GetService<NodeMonitor>() is { } monitor)
            {
                await monitor.SyncRegistryAsync(ct);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or ObjectDisposedException)
        {
            LogRefreshFailed(logger, ex, nodeId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refreshing the connection of node {NodeId} after an admin change failed; the registry poll retries.")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception, string nodeId);
}

/// <summary>
/// The node admin API's logic (design §3.2, §3.5, §5.6; MN-102), shared by <see cref="NodeApiEndpoints"/> and the Nodes
/// page (MN-117): join tokens (admin re-auth), approve / reject, drain / undrain, settings patch and revoke. Every write
/// goes through <see cref="INodeRegistry"/> (atomic, locked), is followed by a connection refresh, and leaves a
/// <c>node.*</c> audit line (succeeded or refused) through <see cref="IAuditLog"/>; nothing secret is logged or audited.
/// <para>Rekey tokens (<see cref="JoinTokenCreateRequest.Rekey"/>) are issued since MN-114's <c>/enroll/rekey</c> exists and
/// <c>/enroll</c> refuses them (the MN-101 decision held them back until then); the eligibility rule is
/// <see cref="JsonNodeRegistry.RekeyRefusal"/>, shared with the registry and the rekey route.</para>
/// </summary>
public sealed partial class NodeAdminService
{
    /// <summary>Reserved: <c>/api/nodes/join-tokens</c> would shadow a node of that id.</summary>
    public const string ReservedNodeId = "join-tokens";

    private const int MaxDisplayNameLength = 100;
    private const int MaxLabels = 32;
    private const int MaxLabelLength = 64;
    private const int MaxAltAddresses = 8;
    private const int MaxImageLength = 512;
    private const int MaxGpuReserves = 64;

    private readonly INodeRegistry _nodes;
    private readonly IModelRegistry _models;
    private readonly CertificateAuthority _ca;
    private readonly MasterCertificates? _master;
    private readonly AgentBundleCatalog? _agentBundles;
    private readonly IAdminReauthenticator? _reauth;
    private readonly IAuditLog _audit;
    private readonly INodeConnectionRefresher? _refresher;
    private readonly INodeStatusCache? _cache;
    private readonly IModelRuntime? _runtime;
    private readonly IOwnershipStore? _ownership;
    private readonly NodeApiOptions _options;
    private readonly int _enrollPort;
    private readonly TimeProvider _time;
    private readonly ILogger<NodeAdminService> _logger;

    // Display names asked for at join-token time for nodes without a record yet (applied at approval), kept across master
    // restarts in pending-node-names.json (W7 review MN-126).
    private readonly PendingNodeNames _pendingNames;

    /// <summary>Creates the service.</summary>
    [SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "Checked with ThrowIfNull.")]
    public NodeAdminService(
        INodeRegistry nodes,
        IModelRegistry models,
        CertificateAuthority ca,
        IOptions<NodeApiOptions> options,
        IOptions<EnrollmentOptions> enrollment,
        TimeProvider time,
        ILogger<NodeAdminService> logger,
        IAdminReauthenticator? reauth = null,
        IAuditLog? audit = null,
        INodeConnectionRefresher? refresher = null,
        INodeStatusCache? cache = null,
        IModelRuntime? runtime = null,
        IOwnershipStore? ownership = null,
        string? pendingNamesPath = null,
        MasterCertificates? masterCertificates = null,
        AgentBundleCatalog? agentBundles = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(ca);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(enrollment);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        NodeApiOptions.Validate(options.Value);
        _nodes = nodes;
        _models = models;
        _ca = ca;
        _master = masterCertificates;
        _agentBundles = agentBundles;
        _options = options.Value;
        _enrollPort = enrollment.Value.Port;
        _time = time;
        _logger = logger;
        _reauth = reauth;
        _audit = audit ?? NullAuditLog.Instance;
        _refresher = refresher;
        _cache = cache;
        _runtime = runtime;
        _ownership = ownership;
        _pendingNames = new PendingNodeNames(pendingNamesPath);
    }

    // ---- reads ----------------------------------------------------------------------------------------------------

    /// <summary><c>GET /api/nodes</c>: every node (revoked ones only with <paramref name="includeRevoked"/>), with live state and counts.</summary>
    public async Task<IReadOnlyList<NodeView>> ListAsync(bool includeRevoked = false, CancellationToken ct = default)
    {
        IReadOnlyList<NodeRecord> records = await _nodes.ListAsync(ct);
        IReadOnlyList<ModelDefinition> models = await _models.ListAsync(ct);
        return [.. records.Where(r => includeRevoked || !r.Revoked).OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => View(r, models))];
    }

    /// <summary><c>GET /api/nodes/{id}</c>; 404 <c>not_found</c> when unknown.</summary>
    public async Task<NodeView> GetAsync(string nodeId, CancellationToken ct = default)
    {
        NodeRecord record = await RequireAsync(nodeId, ct);
        return View(record, await _models.ListAsync(ct));
    }

    // ---- join tokens ----------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>POST /api/nodes/join-tokens</c> (design §3.2): checks the request, checks the signed-in session, and mints a
    /// join token bound to a node id that is new or only pending, or with <see cref="JoinTokenCreateRequest.Rekey"/> a rekey
    /// token for a node that can be re-paired (<see cref="JsonNodeRegistry.RekeyRefusal"/>; design §9.4 step 5, MN-114),
    /// whose command line carries the node's uid (<c>--node-uid</c>: the agent that lost its state cannot read it). The
    /// token is in the answer only.
    /// </summary>
    public Task<JoinTokenCreateResponse> CreateJoinTokenAsync(JoinTokenCreateRequest request, NodeApiCaller caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        return AuditedAsync(AuditActions.NodeJoinToken, request.NodeId, caller, async () =>
        {
            string nodeId = request.NodeId ?? "";
            if (!Protocol.IsValidNodeId(nodeId) || nodeId == ReservedNodeId)
            {
                throw Invalid($"Node id '{nodeId}' is invalid; it must match {Protocol.NodeIdPattern} (and not be '{ReservedNodeId}').");
            }

            TimeSpan lifetime = request.TtlMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : _options.DefaultJoinTokenLifetime;
            if (lifetime < JsonNodeRegistry.MinJoinTokenLifetime || lifetime > JsonNodeRegistry.MaxJoinTokenLifetime)
            {
                throw Invalid($"ttlMinutes must be between {JsonNodeRegistry.MinJoinTokenLifetime.TotalMinutes:0} and {JsonNodeRegistry.MaxJoinTokenLifetime.TotalMinutes:0}.");
            }

            string? displayName = NormalizeDisplayName(request.DisplayName);
            Reauthenticate(caller);

            NodeRecord? existing = await _nodes.GetAsync(nodeId, ct);
            if (existing is { Revoked: true })
            {
                throw new NodeApiException(ErrorCodes.NodeRevoked, $"Node '{nodeId}' is revoked; a revoked id is not reused.");
            }

            JoinTokenPurpose purpose = request.Rekey ? JoinTokenPurpose.Rekey : JoinTokenPurpose.Join;
            if (request.Rekey)
            {
                if (existing is null)
                {
                    throw NotFound(nodeId);
                }

                if (JsonNodeRegistry.RekeyRefusal(existing) is { } refusal)
                {
                    throw new NodeApiException(ErrorCodes.InvalidState,
                        $"Node '{nodeId}' cannot be re-paired ({refusal}); a rekey token is only for an enrolled node (give a new node a join token).");
                }
            }
            else if (existing is { Approved: true } or { CertSpkiSha256: not null })
            {
                throw new NodeApiException(ErrorCodes.InvalidState,
                    $"Node '{nodeId}' is enrolled and approved; a join token is only for a new or pending node (re-pair it with a rekey token).");
            }

            JoinTokenIssue issue;
            try
            {
                issue = await _nodes.CreateJoinTokenAsync(nodeId, lifetime, purpose, ct);
            }
            catch (InvalidOperationException ex)
            {
                // Revoked (or, for a rekey token, rejected) between the read and the locked write.
                NodeRecord? now = await _nodes.GetAsync(nodeId, ct);
                bool revoked = !request.Rekey || now is { Revoked: true };
                throw new NodeApiException(revoked ? ErrorCodes.NodeRevoked : ErrorCodes.InvalidState, ex.Message);
            }

            if (displayName is not null)
            {
                if (existing is null)
                {
                    _pendingNames.Set(nodeId, displayName);
                }
                else
                {
                    await _nodes.UpdateAsync(nodeId, n => n.Revoked ? n : n with { DisplayName = displayName }, ct);
                }
            }

            await PruneAsync(ct);
            Uri enrollUrl = EnrollUrl(caller);
            // The current issuing CA (the new one after a CA rotation's step 3, MN-126): the enroll listener chains to it.
            string caHash = Spki.ToJoinHash((_master?.Authority ?? _ca).SpkiSha256);
            AgentDownload? download = AgentBundleBase(caller) is { } bundleBase ? _agentBundles?.DownloadFrom(bundleBase) : null;
            var response = new JoinTokenCreateResponse
            {
                NodeId = nodeId,
                Token = issue.Token,
                CaHash = caHash,
                EnrollUrl = enrollUrl.GetLeftPart(UriPartial.Authority),
                ExpiresAt = issue.Record.ExpiresAt,
                Purpose = purpose,
                Commands = request.Rekey
                    ? RekeyCommands(nodeId, existing!.Uid, issue.Token, enrollUrl, caHash, download)
                    : Commands(nodeId, issue.Token, enrollUrl, caHash, download),
                AgentDownload = download,
            };
            return (response, request.Rekey ? $"rekey token, expires {issue.Record.ExpiresAt:O}" : $"expires {issue.Record.ExpiresAt:O}");
        }, ct);
    }

    /// <summary><c>DELETE /api/nodes/{id}/join-tokens</c>: expires the node's unused join tokens; returns how many.</summary>
    public Task<int> ExpireJoinTokensAsync(string nodeId, NodeApiCaller caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return AuditedAsync(AuditActions.NodeJoinToken, nodeId, caller, async () =>
        {
            if (!Protocol.IsValidNodeId(nodeId))
            {
                throw NotFound(nodeId);
            }

            int expired = await _nodes.ExpireUnusedJoinTokensAsync(nodeId, ct);
            _pendingNames.Remove(nodeId);

            return (expired, $"expired {expired} unused token(s)");
        }, ct);
    }

    // ---- approval -------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>POST /api/nodes/{id}/approve</c> (design §3.2): promotes the pending pin of a PendingApproval node (409
    /// <c>invalid_state</c> when it is not pending or the pin differs from <see cref="NodeApproveRequest.ExpectedSpkiSha256"/>),
    /// expires its other unused join tokens and refreshes the connection.
    /// </summary>
    public Task<NodeView> ApproveAsync(string nodeId, NodeApproveRequest? request, NodeApiCaller caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        request ??= new NodeApproveRequest();
        return AuditedAsync(AuditActions.NodeApprove, nodeId, caller, async () =>
        {
            string? address = request.Address is null ? null : NormalizeAddress(request.Address, "address");
            IReadOnlyList<string>? alt = request.AltAddresses is null ? null : NormalizeAltAddresses(request.AltAddresses);
            if (request.ExpectedSpkiSha256 is not null && !Spki.IsValidPin(request.ExpectedSpkiSha256))
            {
                throw Invalid("expectedSpkiSha256 is not a base64 SHA-256 pin.");
            }

            string? pendingName = _pendingNames.Get(nodeId);

            DateTimeOffset now = _time.GetUtcNow();
            NodeRecord updated = await UpdateExistingAsync(nodeId, n =>
            {
                RefuseRevoked(n);
                if (n.Approved || n.PendingCertSpkiSha256 is null)
                {
                    throw new NodeApiException(ErrorCodes.InvalidState,
                        n.Approved ? $"Node '{n.Id}' is already approved." : $"Node '{n.Id}' has not enrolled yet; nothing to approve.");
                }

                if (request.ExpectedSpkiSha256 is not null && !Spki.PinEquals(request.ExpectedSpkiSha256, n.PendingCertSpkiSha256))
                {
                    throw new NodeApiException(ErrorCodes.InvalidState,
                        $"Node '{n.Id}' enrolled again with another key since the dialog was opened; review the new fingerprint.");
                }

                return n with
                {
                    Approved = true,
                    ApprovedAt = now,
                    CertSpkiSha256 = n.PendingCertSpkiSha256,
                    PendingCertSpkiSha256 = null,
                    Address = address ?? n.Address,
                    AltAddresses = alt is null ? n.AltAddresses : [.. alt.Where(a => a != (address ?? n.Address))],
                    DisplayName = n.DisplayName ?? pendingName,
                };
            }, ct);

            _pendingNames.Remove(nodeId);

            // An unused join token left from before would let the node enroll again with another key (and drop the approval).
            int expired = await _nodes.ExpireUnusedJoinTokensAsync(nodeId, ct);
            await RefreshAsync(nodeId, ct);
            return (await ViewAsync(updated, ct), $"pin {Spki.ToDisplayFingerprint(updated.CertSpkiSha256!)}, expired {expired} unused token(s)");
        }, ct);
    }

    /// <summary>
    /// <c>POST /api/nodes/{id}/reject</c> (design §3.2): deletes a node that is not approved (its pending pin and record) and,
    /// in the same locked write, expires its unused join tokens. 409 <c>invalid_state</c> for an approved node (revoke it).
    /// </summary>
    public Task<bool> RejectAsync(string nodeId, NodeApiCaller caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return AuditedAsync(AuditActions.NodeReject, nodeId, caller, async () =>
        {
            NodeRecord record = await RequireAsync(nodeId, ct);
            RefuseRevoked(record);
            if (record.Approved)
            {
                throw new NodeApiException(ErrorCodes.InvalidState, $"Node '{nodeId}' is approved; revoke it instead of rejecting it.");
            }

            // Re-checked under the lock: approval between the read and the delete must not be undone by a reject.
            NodeRecord? stillPending = await _nodes.UpdateAsync(nodeId, n => n.Approved || n.Revoked
                ? throw new NodeApiException(ErrorCodes.InvalidState, $"Node '{nodeId}' changed state meanwhile; reload it.")
                : n, ct);
            if (stillPending is null || !await _nodes.DeleteAsync(nodeId, ct))
            {
                throw NotFound(nodeId);
            }

            _pendingNames.Remove(nodeId);

            await RefreshAsync(nodeId, ct);
            return (true, record.PendingCertSpkiSha256 is { } pin ? $"pending pin {Spki.ToDisplayFingerprint(pin)} removed" : "record removed");
        }, ct);
    }

    // ---- drain, settings ------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>POST /api/nodes/{id}/drain</c> / <c>/undrain</c> (design §3.5): sets the operator flag (idempotent). Refused for
    /// a node that is not approved (approval comes before any command) or is revoked.
    /// </summary>
    public Task<NodeView> SetDrainingAsync(string nodeId, bool draining, NodeApiCaller caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return AuditedAsync(draining ? AuditActions.NodeDrain : AuditActions.NodeUndrain, nodeId, caller, async () =>
        {
            NodeRecord updated = await UpdateExistingAsync(nodeId, n =>
            {
                RefuseRevoked(n);
                if (!n.Approved)
                {
                    throw new NodeApiException(ErrorCodes.InvalidState, $"Node '{n.Id}' is not approved yet; approve it first.");
                }

                return n.Draining == draining ? n : n with { Draining = draining };
            }, ct);
            await RefreshAsync(nodeId, ct);
            return (await ViewAsync(updated, ct), draining ? "draining" : "not draining");
        }, ct);
    }

    /// <summary>
    /// <c>PATCH /api/nodes/{id}</c> (design §5.6, §12.1 Settings): every present field replaces the stored one (see
    /// <see cref="NodePatchRequest"/> for the clearing rules); ids, uid, pins, approval, draining and revocation are not
    /// patchable. 400 <c>invalid_request</c> for an invalid value, 409 <c>node_revoked</c> for a revoked node.
    /// </summary>
    public Task<NodeView> PatchAsync(string nodeId, NodePatchRequest request, NodeApiCaller caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        return AuditedAsync(AuditActions.NodeUpdate, nodeId, caller, async () =>
        {
            Func<NodeRecord, NodeRecord> apply = BuildPatch(request, out List<string> changed);
            NodeRecord updated = await UpdateExistingAsync(nodeId, n =>
            {
                RefuseRevoked(n);
                return apply(n);
            }, ct);
            await RefreshAsync(nodeId, ct);
            return (await ViewAsync(updated, ct), changed.Count == 0 ? "no change" : "changed " + string.Join(", ", changed));
        }, ct);
    }

    // ---- revoke ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>DELETE /api/nodes/{id}</c> (design §3.5, §5.6): after the typed confirmation and the signed-in session, revokes the
    /// node (pins deleted, unused join tokens expired). While models are placed there it is refused with 409
    /// <c>models_placed</c> unless <see cref="NodeRevokeRequest.UnplaceModels"/>; then each is stopped (best effort, bounded
    /// by <see cref="NodeApiOptions.RevokeStopTimeout"/>) and unplaced through the registry before the revoke.
    /// <para>Race with new placements (W7 review MN-126): an approved node is set draining <b>before</b> its models are
    /// listed, so the registry's placement validator refuses every new placement on it (<c>node_draining</c>, checked under
    /// the registry lock) while the models are unplaced and until the revoked flag is written; nothing can land on the node
    /// between the last unplace and the revoke. A refusal or failure before the revoke puts the flag back. Each unplace is
    /// written only if the placement is still the one read (<see cref="ModelWriteScope.ExpectStoredPlacement"/>), so it
    /// never erases a move committed meanwhile. With an ownership store a model held by a move, job or restore is refused
    /// (409 <c>model_busy</c>) before anything is stopped.</para>
    /// </summary>
    public Task<NodeRevokeResult> RevokeAsync(string nodeId, NodeRevokeRequest request, NodeApiCaller caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        return AuditedAsync(AuditActions.NodeRevoke, nodeId, caller, async () =>
        {
            NodeRecord record = await RequireAsync(nodeId, ct);
            RefuseRevoked(record);
            if (!string.Equals(request.Confirm, nodeId, StringComparison.Ordinal))
            {
                throw new NodeApiException(ErrorCodes.ConfirmationRequired, $"Type the node id '{nodeId}' to confirm the revoke.");
            }

            Reauthenticate(caller);

            // The barrier first: once the node is draining no new placement can land on it, so the list below is complete.
            bool barrier = record.Approved && !record.Draining;
            if (barrier)
            {
                await _nodes.UpdateAsync(nodeId, n => n.Revoked || n.Draining ? n : n with { Draining = true }, ct);
            }

            List<string> unplaced;
            int expired;
            try
            {
                unplaced = await UnplaceForRevokeAsync(nodeId, request, caller, ct);
                expired = await _nodes.ExpireUnusedJoinTokensAsync(nodeId, ct);
                if (!await _nodes.RevokeAsync(nodeId, ct))
                {
                    throw NotFound(nodeId);
                }
            }
            catch
            {
                if (barrier)
                {
                    await LiftRevokeBarrierAsync(nodeId);
                }

                throw;
            }

            _pendingNames.Remove(nodeId);

            await RefreshAsync(nodeId, ct);
            var result = new NodeRevokeResult(nodeId, unplaced, expired);
            return (result, $"unplaced {unplaced.Count} model(s), expired {expired} unused token(s)");
        }, ct);
    }

    /// <summary>The revoke's unplace step (see <see cref="RevokeAsync"/>): every model placed on the node, stopped and unplaced.</summary>
    private async Task<List<string>> UnplaceForRevokeAsync(string nodeId, NodeRevokeRequest request, NodeApiCaller caller, CancellationToken ct)
    {
        List<ModelDefinition> placed = await PlacedOnAsync(nodeId, ct);
        if (placed.Count > 0 && !request.UnplaceModels)
        {
            throw new NodeApiException(ErrorCodes.ModelsPlaced,
                $"{placed.Count} model(s) are placed on node '{nodeId}': {string.Join(", ", placed.Select(m => m.Id))}. Move them, or revoke with unplaceModels=true.",
                extensions: new Dictionary<string, object?> { ["modelIds"] = placed.Select(m => m.Id).ToArray() });
        }

        // A held model would be refused by the registry after it was stopped; refuse before stopping anything.
        if (_ownership is not null)
        {
            foreach (ModelDefinition model in placed)
            {
                if (await _ownership.GetForModelAsync(model.Id, ct) is { } holder)
                {
                    await WriteAuditAsync(AuditActions.ModelUnplace, nodeId, caller, AuditResults.Refused, ErrorCodes.ModelBusy, null, model.Id);
                    throw new NodeApiException(ErrorCodes.ModelBusy,
                        $"Model '{model.Id}' is busy: {ModelBusyException.DescribeHolder(holder)} holds it; the node was not revoked. " +
                        "Wait for it to finish or cancel it, then revoke again.",
                        extensions: new Dictionary<string, object?> { ["unplacedModelIds"] = Array.Empty<string>() });
                }
            }
        }

        var unplaced = new List<string>();
        foreach (ModelDefinition model in placed)
        {
            await StopBestEffortAsync(model.Id, nodeId, ct);
            for (int attempt = 1; ; attempt++)
            {
                ModelDefinition? current = await _models.GetAsync(model.Id, ct);
                if (current?.Placement is not { } placement || placement.NodeId != nodeId)
                {
                    break; // moved or deleted meanwhile
                }

                try
                {
                    using (ModelWriteScope.ExpectStoredPlacement(placement))
                    {
                        await _models.UpsertAsync(current with { Placement = null }, ct);
                    }

                    unplaced.Add(model.Id);
                    await WriteAuditAsync(AuditActions.ModelUnplace, nodeId, caller, AuditResults.Succeeded, null, $"revoke of node {nodeId}", model.Id);
                    break;
                }
                catch (PlacementConflictException) when (attempt < 3)
                {
                    // The placement changed between the read and the write (a holder's GPU change or move): read it again.
                }
                catch (Exception ex) when (ex is ModelBusyException or PlacementConflictException)
                {
                    string code = ex is ModelBusyException ? ErrorCodes.ModelBusy : ErrorCodes.PlacementChanged;
                    await WriteAuditAsync(AuditActions.ModelUnplace, nodeId, caller, AuditResults.Refused, code, null, model.Id);
                    throw new NodeApiException(code,
                        $"Model '{model.Id}' could not be unplaced ({ex.Message}); the node was not revoked." + UnplacedSoFar(unplaced),
                        extensions: new Dictionary<string, object?> { ["unplacedModelIds"] = unplaced.ToArray() });
                }
            }
        }

        return unplaced;
    }

    /// <summary>Puts the draining flag back after a revoke that did not happen (best effort; the operator can undrain).</summary>
    private async Task LiftRevokeBarrierAsync(string nodeId)
    {
        try
        {
            await _nodes.UpdateAsync(nodeId, n => n.Revoked || !n.Draining ? n : n with { Draining = false }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            LogBarrierNotLifted(_logger, ex, nodeId);
        }
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private async Task<T> AuditedAsync<T>(string action, string? nodeId, NodeApiCaller caller, Func<Task<(T Result, string Detail)>> run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string? auditNode = Protocol.IsValidNodeId(nodeId) ? nodeId : null;
        try
        {
            (T result, string detail) = await run();
            await WriteAuditAsync(action, auditNode, caller, AuditResults.Succeeded, null, detail, null);
            return result;
        }
        catch (NodeApiException ex)
        {
            await WriteAuditAsync(action, auditNode, caller, AuditResults.Refused, ex.Code, null, null);
            throw;
        }
        catch (ArgumentException ex)
        {
            // The registry's own validation (Validate): a 400 with its message.
            await WriteAuditAsync(action, auditNode, caller, AuditResults.Refused, ErrorCodes.InvalidRequest, null, null);
            throw new NodeApiException(ErrorCodes.InvalidRequest, ex.Message, StatusCodes.Status400BadRequest);
        }
    }

    private async Task WriteAuditAsync(string action, string? nodeId, NodeApiCaller caller, string result, string? errorCode, string? detail, string? modelId)
    {
        try
        {
            await _audit.WriteAsync(new AuditRecord
            {
                Time = _time.GetUtcNow(),
                Actor = caller.Actor,
                Action = action,
                NodeId = nodeId,
                ModelId = modelId,
                Result = result,
                ErrorCode = errorCode,
                Detail = detail,
            }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // IAuditLog never fails the action it records.
            LogAuditFailed(_logger, ex, action);
        }
    }

    private void Reauthenticate(NodeApiCaller caller)
    {
        if (_reauth is null || !_reauth.LoginEnabled)
        {
            throw new NodeApiException(ErrorCodes.ReauthenticationFailed,
                "Admin login is disabled (no MirbadLLM:AdminPassword), so this action cannot be confirmed.");
        }

        switch (_reauth.Verify(caller.User))
        {
            case AdminReauthOutcome.Verified:
                return;
            case AdminReauthOutcome.RateLimited:
                throw new NodeApiException(ErrorCodes.RateLimited, "Too many password attempts. Try again in a minute.", StatusCodes.Status429TooManyRequests);
            case AdminReauthOutcome.LoginDisabled:
                throw new NodeApiException(ErrorCodes.ReauthenticationFailed, "Admin login is disabled, so this action cannot be confirmed.");
            default:
                throw new NodeApiException(ErrorCodes.ReauthenticationFailed, "Sign in to the portal to perform this action.");
        }
    }

    private async Task<NodeRecord> RequireAsync(string nodeId, CancellationToken ct) =>
        Protocol.IsValidNodeId(nodeId) && await _nodes.GetAsync(nodeId, ct) is { } record ? record : throw NotFound(nodeId);

    private async Task<NodeRecord> UpdateExistingAsync(string nodeId, Func<NodeRecord, NodeRecord> update, CancellationToken ct)
    {
        if (!Protocol.IsValidNodeId(nodeId))
        {
            throw NotFound(nodeId);
        }

        NodeRecord? updated;
        try
        {
            updated = await _nodes.UpdateAsync(nodeId, update, ct);
        }
        catch (InvalidOperationException ex)
        {
            // The registry refuses to un-revoke a record (MN-090).
            throw new NodeApiException(ErrorCodes.NodeRevoked, ex.Message);
        }

        return updated ?? throw NotFound(nodeId);
    }

    private static void RefuseRevoked(NodeRecord node)
    {
        if (node.Revoked)
        {
            throw new NodeApiException(ErrorCodes.NodeRevoked, $"Node '{node.Id}' is revoked.");
        }
    }

    private async Task<NodeView> ViewAsync(NodeRecord record, CancellationToken ct) => View(record, await _models.ListAsync(ct));

    private NodeView View(NodeRecord record, IReadOnlyList<ModelDefinition> models)
    {
        List<ModelDefinition> placed = [.. models.Where(m => m.Placement?.NodeId == record.Id)];
        NodeLiveStatus? live = record.Revoked ? null : _cache?.GetNode(record.Id);
        int ready = _cache is null ? 0 : placed.Count(m => _cache.GetRuntime(m.Id) is { State: ModelState.Ready } r && r.NodeId == record.Id);
        return new NodeView { Node = record, Live = live, PlacedModels = placed.Count, ReadyModels = ready };
    }

    private async Task<List<ModelDefinition>> PlacedOnAsync(string nodeId, CancellationToken ct) =>
        [.. (await _models.ListAsync(ct)).Where(m => m.Placement?.NodeId == nodeId).OrderBy(m => m.Id, StringComparer.Ordinal)];

    private async Task StopBestEffortAsync(string modelId, string nodeId, CancellationToken ct)
    {
        if (_runtime is null)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(_options.RevokeStopTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await _runtime.StopAsync(modelId, linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LogStopFailed(_logger, modelId, nodeId, "timed out");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort (§5.6): an offline node cannot stop it; once revoked the master never routes to it again.
            LogStopFailed(_logger, modelId, nodeId, ex.Message);
        }
    }

    private async Task RefreshAsync(string nodeId, CancellationToken ct)
    {
        if (_refresher is not null)
        {
            await _refresher.RefreshAsync(nodeId, ct);
        }
    }

    private async Task PruneAsync(CancellationToken ct)
    {
        if (_options.JoinTokenRetention <= TimeSpan.Zero)
        {
            return;
        }

        int pruned = await _nodes.PruneJoinTokensAsync(_options.JoinTokenRetention, ct);
        if (pruned > 0)
        {
            LogPruned(_logger, pruned);
        }
    }

    private Uri EnrollUrl(NodeApiCaller caller)
    {
        if (_options.EnrollUrl is { } configured)
        {
            return configured;
        }

        string host = string.IsNullOrEmpty(caller.RequestHost) ? "localhost" : caller.RequestHost;
        if (IPAddress.TryParse(host, out IPAddress? ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !host.StartsWith('['))
        {
            host = $"[{host}]";
        }

        return new UriBuilder(Uri.UriSchemeHttps, host, _enrollPort).Uri;
    }

    /// <summary>
    /// The master's main address the commands download the agent bundles from: <see cref="NodeApiOptions.AgentBundleUrl"/>,
    /// else the address of the admin request (the UI the operator has open); null when neither is known.
    /// </summary>
    private Uri? AgentBundleBase(NodeApiCaller caller) => _options.AgentBundleUrl ?? caller.RequestBaseUrl;

    /// <summary>
    /// The per-OS join commands (design §12.1). The token goes in the environment, never on the command line (MN-103). With
    /// <paramref name="download"/> the Linux line fetches and checks the agent bundle itself (no checkout, no Docker build),
    /// and, when it holds the <c>win-x64</c> zip, <see cref="JoinCommands.Windows"/> is the one-paste PowerShell line
    /// (design-p3p4 §A.4.5, MN-218; null without that bundle, as before).
    /// </summary>
    private static JoinCommands Commands(string nodeId, string token, Uri enrollUrl, string caHash, AgentDownload? download) =>
        new(LinuxInstallerLine.Join(nodeId, enrollUrl.GetLeftPart(UriPartial.Authority), caHash, download).WithToken(token),
            WindowsInstallerLine.Join(nodeId, enrollUrl.GetLeftPart(UriPartial.Authority), caHash, download)?.WithToken(token),
            MacosInstallerLine.Join(nodeId, enrollUrl.GetLeftPart(UriPartial.Authority), caHash, download)?.WithToken(token));

    // install-agent.sh --rekey on a node it installed (MN-103; W7 review MN-126): it stops the service, runs join --rekey as
    // the service user and starts the service again, on the old identity when the rekey fails (a raw stop && join && start
    // chain left the node stopped). The uid is on the line because an agent that lost its state directory cannot read it
    // (MN-114). The Windows line runs install-agent.ps1 rekey the same way (design-p3p4 §A.4.6, MN-218).
    private static JoinCommands RekeyCommands(string nodeId, string nodeUid, string token, Uri enrollUrl, string caHash, AgentDownload? download) =>
        new(LinuxInstallerLine.Rekey(nodeId, nodeUid, enrollUrl.GetLeftPart(UriPartial.Authority), caHash, download).WithToken(token),
            WindowsInstallerLine.Rekey(nodeId, nodeUid, enrollUrl.GetLeftPart(UriPartial.Authority), caHash, download)?.WithToken(token),
            MacosInstallerLine.Rekey(nodeId, nodeUid, enrollUrl.GetLeftPart(UriPartial.Authority), caHash, download)?.WithToken(token));

    private static Func<NodeRecord, NodeRecord> BuildPatch(NodePatchRequest request, out List<string> changed)
    {
        changed = [];
        string? displayName = null;
        bool setDisplayName = request.DisplayName is not null;
        if (setDisplayName)
        {
            displayName = NormalizeDisplayName(request.DisplayName);
            changed.Add("displayName");
        }

        string? address = request.Address is null ? null : NormalizeAddress(request.Address, "address");
        if (address is not null)
        {
            changed.Add("address");
        }

        IReadOnlyList<string>? alt = request.AltAddresses is null ? null : NormalizeAltAddresses(request.AltAddresses);
        if (alt is not null)
        {
            changed.Add("altAddresses");
        }

        if (request.MemoryReserveGiB is { } reserve)
        {
            RequireGiB(reserve, "memoryReserveGiB");
            changed.Add("memoryReserveGiB");
        }

        Dictionary<string, double>? gpuReserves = null;
        if (request.GpuReserveGiB is { } gpus)
        {
            if (gpus.Count > MaxGpuReserves)
            {
                throw Invalid($"gpuReserveGiB holds at most {MaxGpuReserves} GPUs.");
            }

            gpuReserves = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach ((string uuid, double value) in gpus)
            {
                if (string.IsNullOrWhiteSpace(uuid) || uuid.Length > 128 || uuid.Any(char.IsWhiteSpace))
                {
                    throw Invalid("gpuReserveGiB keys must be GPU UUIDs.");
                }

                RequireGiB(value, $"gpuReserveGiB[{uuid}]");
                gpuReserves[uuid] = value;
            }

            changed.Add("gpuReserveGiB");
        }

        if (request.MaxConcurrentStarts is < 0)
        {
            throw Invalid("maxConcurrentStarts must be 0 (default) or positive.");
        }

        if (request.MaxConcurrentStarts is not null)
        {
            changed.Add("maxConcurrentStarts");
        }

        string? vllmImage = Image(request.DefaultVllmImage, "defaultVllmImage", changed);
        string? vllmDigest = Digest(request.DefaultVllmImageDigest, "defaultVllmImageDigest", changed);
        string? llamaImage = Image(request.DefaultLlamaCppImage, "defaultLlamaCppImage", changed);
        string? llamaDigest = Digest(request.DefaultLlamaCppImageDigest, "defaultLlamaCppImageDigest", changed);

        IReadOnlyList<string>? labels = null;
        if (request.Labels is { } given)
        {
            labels = NormalizeLabels(given);
            changed.Add("labels");
        }

        return n =>
        {
            string effectiveAddress = address ?? n.Address;
            return n with
            {
                DisplayName = setDisplayName ? displayName : n.DisplayName,
                Address = effectiveAddress,
                AltAddresses = alt is null ? n.AltAddresses : [.. alt.Where(a => a != effectiveAddress)],
                MemoryReserveGiB = request.MemoryReserveGiB ?? n.MemoryReserveGiB,
                GpuReserveGiB = gpuReserves ?? n.GpuReserveGiB,
                MaxConcurrentStarts = request.MaxConcurrentStarts is { } starts ? (starts == 0 ? null : starts) : n.MaxConcurrentStarts,
                DefaultVllmImage = request.DefaultVllmImage is null ? n.DefaultVllmImage : vllmImage,
                DefaultVllmImageDigest = request.DefaultVllmImageDigest is null ? n.DefaultVllmImageDigest : vllmDigest,
                DefaultLlamaCppImage = request.DefaultLlamaCppImage is null ? n.DefaultLlamaCppImage : llamaImage,
                DefaultLlamaCppImageDigest = request.DefaultLlamaCppImageDigest is null ? n.DefaultLlamaCppImageDigest : llamaDigest,
                Labels = labels ?? n.Labels,
            };
        };
    }

    private static string? Image(string? value, string field, List<string> changed)
    {
        if (value is null)
        {
            return null;
        }

        changed.Add(field);
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.Length > MaxImageLength || trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw Invalid($"{field} must be an image reference without spaces.");
        }

        return trimmed;
    }

    private static string? Digest(string? value, string field, List<string> changed)
    {
        if (value is null)
        {
            return null;
        }

        changed.Add(field);
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (!DigestPattern().IsMatch(trimmed))
        {
            throw Invalid($"{field} must be a repository digest 'sha256:' followed by 64 lower-case hex digits.");
        }

        return trimmed;
    }

    private static void RequireGiB(double value, string field)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 65536)
        {
            throw Invalid($"{field} must be a finite number of GiB, 0 or more.");
        }
    }

    private static string? NormalizeDisplayName(string? value)
    {
        if (value is null)
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.Length > MaxDisplayNameLength || trimmed.Any(char.IsControl))
        {
            throw Invalid($"displayName must be at most {MaxDisplayNameLength} characters, without control characters.");
        }

        return trimmed.Length == 0 ? null : trimmed;
    }

    private static List<string> NormalizeLabels(IReadOnlyList<string> labels)
    {
        if (labels.Count > MaxLabels)
        {
            throw Invalid($"At most {MaxLabels} labels.");
        }

        var result = new List<string>();
        foreach (string? label in labels)
        {
            string trimmed = label?.Trim() ?? "";
            if (trimmed.Length is 0 or > MaxLabelLength || trimmed.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            {
                throw Invalid($"Labels must be 1–{MaxLabelLength} characters without spaces.");
            }

            if (!result.Contains(trimmed, StringComparer.Ordinal))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    private static List<string> NormalizeAltAddresses(IReadOnlyList<string> addresses)
    {
        if (addresses.Count > MaxAltAddresses)
        {
            throw Invalid($"altAddresses holds at most {MaxAltAddresses} addresses.");
        }

        var result = new List<string>();
        foreach (string? address in addresses)
        {
            string normalized = NormalizeAddress(address, "altAddresses");
            if (!result.Contains(normalized, StringComparer.Ordinal))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    /// <summary>An agent base address: an absolute <c>https://host[:port]</c> URL without path, query or user info.</summary>
    private static string NormalizeAddress(string? value, string field)
    {
        string trimmed = value?.Trim() ?? "";
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || string.IsNullOrEmpty(uri.Host))
        {
            throw Invalid($"{field}: '{trimmed}' is not an agent address (https://host:port).");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static string UnplacedSoFar(List<string> unplaced) =>
        unplaced.Count == 0 ? "" : $" Already unplaced: {string.Join(", ", unplaced)}.";

    private static NodeApiException Invalid(string message) => new(ErrorCodes.InvalidRequest, message, StatusCodes.Status400BadRequest);

    private static NodeApiException NotFound(string? nodeId) =>
        new(ErrorCodes.NotFound, $"Node '{nodeId}' does not exist.", StatusCodes.Status404NotFound);

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();

    [LoggerMessage(Level = LogLevel.Warning, Message = "A revoke of node {NodeId} did not happen, and its draining flag could not be put back; undrain it on the Nodes page.")]
    private static partial void LogBarrierNotLifted(ILogger logger, Exception exception, string nodeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stopping model {ModelId} on node {NodeId} before the revoke failed ({Reason}); unplacing it anyway.")]
    private static partial void LogStopFailed(ILogger logger, string modelId, string nodeId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Count} used or expired join token record(s).")]
    private static partial void LogPruned(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Writing the audit line for {Action} failed.")]
    private static partial void LogAuditFailed(ILogger logger, Exception exception, string action);
}

/// <summary>
/// The node admin API (design §5.6; MN-102), admin-cookie protected like the rest of <c>/api</c> (the fallback policy).
/// Refusals are ProblemDetails with an <see cref="ErrorCodes"/> value in <c>code</c>. Composed by the master (MN-130):
/// <c>builder.Services.AddMirbadLLMNodeApi(builder.Configuration)</c> and <c>app.MapNodeApi()</c>.
/// <list type="table">
/// <item><term><c>GET /api/nodes[?includeRevoked=true]</c>, <c>GET /api/nodes/{id}</c></term><description><see cref="NodeView"/>s.</description></item>
/// <item><term><c>POST /api/nodes/join-tokens</c></term><description><see cref="JoinTokenCreateRequest"/> → <see cref="JoinTokenCreateResponse"/> (admin re-auth).</description></item>
/// <item><term><c>DELETE /api/nodes/{id}/join-tokens</c></term><description>Expire the node's unused tokens.</description></item>
/// <item><term><c>POST /api/nodes/{id}/approve</c>, <c>/reject</c>, <c>/drain</c>, <c>/undrain</c></term><description>State changes.</description></item>
/// <item><term><c>PATCH /api/nodes/{id}</c></term><description><see cref="NodePatchRequest"/>.</description></item>
/// <item><term><c>DELETE /api/nodes/{id}</c></term><description><see cref="NodeRevokeRequest"/> → <see cref="NodeRevokeResult"/> (admin re-auth).</description></item>
/// </list>
/// </summary>
public static class NodeApiEndpoints
{
    /// <summary>Largest request body read (a patch with every field is well under 8 KiB).</summary>
    public const int MaxBodyBytes = 64 * 1024;

    private static readonly JsonSerializerOptions DefaultJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Binds <see cref="NodeApiOptions"/> (validated on first use) and registers <see cref="NodeAdminService"/> and the
    /// default <see cref="INodeConnectionRefresher"/>. <see cref="IAuditLog"/>, <see cref="INodeStatusCache"/>,
    /// <see cref="IModelRuntime"/> and <see cref="IAdminReauthenticator"/> are used when registered.
    /// </summary>
    public static IServiceCollection AddMirbadLLMNodeApi(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<NodeApiOptions>().Bind(configuration.GetSection(NodeApiOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<INodeConnectionRefresher, NodeConnectionRefresher>();
        services.AddMirbadLLMAgentBundles();
        services.TryAddSingleton(sp => new NodeAdminService(
            sp.GetRequiredService<INodeRegistry>(),
            sp.GetRequiredService<IModelRegistry>(),
            sp.GetRequiredService<CertificateAuthority>(),
            sp.GetRequiredService<IOptions<NodeApiOptions>>(),
            sp.GetRequiredService<IOptions<EnrollmentOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<NodeAdminService>>(),
            sp.GetService<IAdminReauthenticator>(),
            sp.GetService<IAuditLog>(),
            sp.GetService<INodeConnectionRefresher>(),
            sp.GetService<INodeStatusCache>(),
            sp.GetService<IModelRuntime>(),
            sp.GetService<IOwnershipStore>(),
            sp.GetService<IOptions<MirbadLLMOptions>>()?.Value.DataPath is { Length: > 0 } dataPath
                ? Path.Combine(dataPath, PendingNodeNames.FileName)
                : null,
            sp.GetService<MasterCertificates>(),
            sp.GetService<AgentBundleCatalog>())
        {
            UpdateSettings = sp.GetRequiredService<AgentUpdateSettingsStore>(),
        });
        services.TryAddSingleton(sp => new AgentUpdateSettingsStore(
            sp.GetService<IOptions<MirbadLLMOptions>>()?.Value.DataPath is { Length: > 0 } data ? Path.Combine(data, AgentUpdateSettingsStore.FileName) : null));
        // Automatic agent updates (docs/multinode/agent-updates.md U8), only where the master drives agents.
        services.AddHostedService(sp => new AgentAutoUpdater(
            sp.GetRequiredService<NodeAdminService>(), sp.GetService<AgentOperationRunner>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<AgentAutoUpdater>>()));
        return services;
    }

    /// <summary>Maps the routes listed on the class.</summary>
    public static IEndpointRouteBuilder MapNodeApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var api = endpoints.MapGroup("/api/nodes");

        api.MapGet("", (bool? includeRevoked, HttpContext http, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await Service(http).ListAsync(includeRevoked == true, ct))));

        api.MapGet("/{id}", (string id, HttpContext http, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await Service(http).GetAsync(id, ct))));

        var updates = endpoints.MapGroup("/api/agent-updates");
        updates.MapGet("", (HttpContext http, CancellationToken ct) => RunAsync(() =>
        {
            var service = Service(http);
            return Task.FromResult(Results.Ok(new
            {
                controller = ProductVersion.Controller,
                automatic = service.UpdateSettings?.Automatic == true,
                outdated = service.OutdatedAgents().Select(o => new { nodeId = o.Node.NodeId, agentVersion = o.Node.AgentVersion, blocker = o.Blocker }),
            }));
        }));
        updates.MapPost("", (HttpContext http, CancellationToken ct) => RunAsync(async () =>
        {
            var request = await ReadBodyAsync<AgentUpdateStartRequest>(http, required: true, ct);
            var runner = http.RequestServices.GetService<AgentOperationRunner>()
                ?? throw new NodeApiException(ErrorCodes.NotSupported, "This master has no agent connections.");
            return Results.Accepted(value: await Service(http).UpdateOutdatedAgentsAsync(request!, NodeApiCaller.From(http), runner, ct));
        }));
        updates.MapPut("/settings", (HttpContext http, CancellationToken ct) => RunAsync(async () =>
        {
            var request = await ReadBodyAsync<AgentUpdateSettingsRequest>(http, required: true, ct);
            return Results.Ok(new { automatic = await Service(http).SetAutomaticAgentUpdatesAsync(request!.Automatic, NodeApiCaller.From(http), ct) });
        }));

        api.MapPost("/{id}/agent-update", (string id, HttpContext http, CancellationToken ct) => RunAsync(async () =>
        {
            var request = await ReadBodyAsync<AgentUpdateStartRequest>(http, required: true, ct);
            var runner = http.RequestServices.GetService<AgentOperationRunner>()
                ?? throw new NodeApiException(ErrorCodes.NotSupported, "This master has no agent connections.");
            return Results.Accepted(value: await Service(http).StartAgentUpdateAsync(id, request!, NodeApiCaller.From(http), runner, ct));
        }));

        api.MapPost("/join-tokens", (HttpContext http, CancellationToken ct) => RunAsync(async () =>
        {
            var request = await ReadBodyAsync<JoinTokenCreateRequest>(http, required: true, ct);
            JoinTokenCreateResponse response = await Service(http).CreateJoinTokenAsync(request!, NodeApiCaller.From(http), ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(response);
        }));

        api.MapDelete("/{id}/join-tokens", (string id, HttpContext http, CancellationToken ct) => RunAsync(async () =>
            Results.Ok(new { nodeId = id, expired = await Service(http).ExpireJoinTokensAsync(id, NodeApiCaller.From(http), ct) })));

        api.MapPost("/{id}/approve", (string id, HttpContext http, CancellationToken ct) => RunAsync(async () =>
        {
            var request = await ReadBodyAsync<NodeApproveRequest>(http, required: false, ct);
            return Results.Ok(await Service(http).ApproveAsync(id, request, NodeApiCaller.From(http), ct));
        }));

        api.MapPost("/{id}/reject", (string id, HttpContext http, CancellationToken ct) => RunAsync(async () =>
        {
            await Service(http).RejectAsync(id, NodeApiCaller.From(http), ct);
            return Results.NoContent();
        }));

        api.MapPost("/{id}/drain", (string id, HttpContext http, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await Service(http).SetDrainingAsync(id, true, NodeApiCaller.From(http), ct))));

        api.MapPost("/{id}/undrain", (string id, HttpContext http, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await Service(http).SetDrainingAsync(id, false, NodeApiCaller.From(http), ct))));

        api.MapPatch("/{id}", (string id, HttpContext http, CancellationToken ct) => RunAsync(async () =>
        {
            var request = await ReadBodyAsync<NodePatchRequest>(http, required: true, ct);
            return Results.Ok(await Service(http).PatchAsync(id, request!, NodeApiCaller.From(http), ct));
        }));

        api.MapDelete("/{id}", (string id, HttpContext http, CancellationToken ct) => RunAsync(async () =>
        {
            var request = await ReadBodyAsync<NodeRevokeRequest>(http, required: true, ct);
            return Results.Ok(await Service(http).RevokeAsync(id, request!, NodeApiCaller.From(http), ct));
        }));

        return endpoints;
    }

    private static NodeAdminService Service(HttpContext http) => http.RequestServices.GetRequiredService<NodeAdminService>();

    private static async Task<IResult> RunAsync(Func<Task<IResult>> run)
    {
        try
        {
            return await run();
        }
        catch (NodeApiException ex)
        {
            var extensions = new Dictionary<string, object?>(ex.Extensions) { ["code"] = ex.Code };
            return Results.Problem(detail: ex.Message, statusCode: ex.StatusCode, extensions: extensions);
        }
    }

    /// <summary>Reads the JSON body with the API's serializer options; a missing (when required) or malformed body is 400 <c>invalid_request</c>.</summary>
    private static async Task<T?> ReadBodyAsync<T>(HttpContext http, bool required, CancellationToken ct)
        where T : class
    {
        JsonSerializerOptions options = http.RequestServices.GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()?.Value.SerializerOptions
            ?? DefaultJson;
        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length > MaxBodyBytes)
        {
            throw new NodeApiException(ErrorCodes.InvalidRequest, "The body is too large.", StatusCodes.Status400BadRequest);
        }

        T? body;
        try
        {
            buffer.Position = 0;
            body = buffer.Length == 0 ? null : await JsonSerializer.DeserializeAsync<T>(buffer, options, ct);
        }
        catch (JsonException)
        {
            // The parser's message can quote the body (a password); keep it out of the answer.
            throw new NodeApiException(ErrorCodes.InvalidRequest, $"The body is not a valid {typeof(T).Name}.", StatusCodes.Status400BadRequest);
        }

        if (body is null && required)
        {
            throw new NodeApiException(ErrorCodes.InvalidRequest, $"A {typeof(T).Name} body is required.", StatusCodes.Status400BadRequest);
        }

        return body;
    }
}
