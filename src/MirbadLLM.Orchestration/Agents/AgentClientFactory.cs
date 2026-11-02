using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Pki;

namespace MirbadLLM.Orchestration.Agents;

/// <summary>Tunables of <see cref="AgentClientFactory"/> (defaults = design §7.2).</summary>
public sealed class AgentClientFactoryOptions
{
    /// <summary>Old handlers are disposed at the latest this long after a rebuild (default 5 minutes).</summary>
    public TimeSpan DrainTimeout { get; init; } = AgentClientDefaults.DrainTimeout;

    /// <summary>Per-node bound of proxied requests in flight on the master (default 512).</summary>
    public int MaxDataInFlightPerNode { get; init; } = Protocol.DefaultMaxInFlightPerNode;
}

/// <summary>What every client of a factory shares: clock, master identity, negotiated protocol versions, options, logger.</summary>
internal sealed class AgentClientContext
{
    private readonly Func<string, int> _protocolVersion;

    public AgentClientContext(TimeProvider time, IMasterIdentity identity, Func<string, int> protocolVersion, AgentClientFactoryOptions options, ILogger logger)
    {
        Time = time;
        Identity = identity;
        _protocolVersion = protocolVersion;
        Options = options;
        Logger = logger;
    }

    public TimeProvider Time { get; }

    public IMasterIdentity Identity { get; }

    public AgentClientFactoryOptions Options { get; }

    public ILogger Logger { get; }

    public int ProtocolVersion(string nodeId) => _protocolVersion(nodeId);
}

/// <summary>
/// The master's <see cref="IAgentClientFactory"/> (design §7.2 N1, §10.2): per node two independently owned
/// <see cref="SocketsHttpHandler"/>s (control 2 / single HTTP/2 connection, data 16 / multiple HTTP/2 connections) with one
/// authentication policy: the master client certificate (one <see cref="System.Security.Cryptography.X509Certificates.X509Certificate2"/>
/// per credential, disposed after the last handler using it), and one <see cref="PeerValidatorSource"/> per node shared by
/// both handlers (custom root = the master's trust anchors, SAN <c>spiffe://mirbadllm/node/{id}</c>, EKU serverAuth,
/// SPKI = current or pending pin).
/// <para><b>Node data</b> comes from <see cref="INodeRegistry"/>: call <see cref="RefreshAsync"/> at startup and after
/// registry changes (enrollment, approval, revoke, pin changes, address edits), or <see cref="RefreshNodeAsync"/> for one
/// node. A refresh that only <b>adds</b> a pin (the pending pin of §10.2 step 2) updates the node's validator source and
/// keeps the handlers (MN-090); one that removes a pin, or changes the address or uid, rebuilds both handlers. Revoked,
/// deleted or unpinned nodes are removed (<see cref="GetClient"/> then throws <see cref="KeyNotFoundException"/>).</para>
/// <para><b>Rebuild and drain</b>: <see cref="RebuildHandlers"/> (rotation commit) and <see cref="UpdateMasterTls"/>
/// (master certificate re-issue, trust change) replace both handlers; the old generation finishes its in-flight requests
/// (streamed bodies included) and is disposed when idle or after 5 minutes.</para>
/// <para><b>Trust anchors</b> (design §10.2 dual trust, MN-116): built with an <see cref="IMasterTrustAnchors"/>, the factory
/// validates agents under <b>every</b> anchor it lists (the old and the new CA during a CA rotation); they are the one
/// source of the trust, so <see cref="UpdateMasterTls"/> refuses any other trust (<see cref="UpdateMasterClient"/> changes
/// the certificate alone, MN-126). When <see cref="IMasterTrustAnchors.Version"/> changes, the next call rebuilds
/// every node's handlers under the new anchors (old generations drain). During a CA rotation a node that does not trust the
/// new CA yet is reached with the <b>previous</b> master client certificate (<see cref="SetPreviousMasterClient"/>).</para>
/// <para>Not registered in DI here (MN-040).</para>
/// </summary>
public sealed class AgentClientFactory : IAgentClientFactory, IAsyncDisposable, IDisposable
{
    private readonly INodeRegistry _registry;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly AgentClientFactoryOptions _options;
    private readonly AgentClientContext _context;
    private readonly object _gate = new();
    private readonly Dictionary<string, NodeChannel> _nodes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _protocols = new(StringComparer.Ordinal);
    private readonly IMasterTrustAnchors? _anchors;
    private readonly HashSet<string> _previousNodes = new(StringComparer.Ordinal);
    private MasterTlsMaterial _material;
    private IssuedCredential _clientCredential;
    private MasterTlsMaterial? _previousMaterial;
    private IssuedCredential? _previousCredential;
    private long _trustVersion;
    private long _generationIds;
    private bool _disposed;

    /// <summary>Creates the factory with the master's client credential and its trust anchors for agent certificates.</summary>
    /// <param name="registry">Node registry (addresses, uids, pins).</param>
    /// <param name="identity">Master triple for <c>MirbadLLM-Master</c>.</param>
    /// <param name="masterClientCredential">The master client certificate + key (SAN <c>spiffe://mirbadllm/master</c>, EKU clientAuth). Not owned: the factory makes its own TLS copy.</param>
    /// <param name="trust">The CA(s) agent certificates must chain to. Not owned: the factory copies it.</param>
    /// <param name="timeProvider">Clock for timeouts, retries, drain, request time and validity checks.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="options">Tunables; defaults per design §7.2.</param>
    public AgentClientFactory(
        INodeRegistry registry,
        IMasterIdentity identity,
        IssuedCredential masterClientCredential,
        TrustAnchorSet trust,
        TimeProvider timeProvider,
        ILogger<AgentClientFactory>? logger = null,
        AgentClientFactoryOptions? options = null)
        : this(registry, identity, masterClientCredential, trust, anchors: null, timeProvider, logger, options)
    {
    }

    /// <summary>
    /// Creates the factory with the master's client credential, validating agents under the anchors of
    /// <paramref name="anchors"/> (<c>pki/trust.json</c>, MN-116) and following its changes.
    /// </summary>
    public AgentClientFactory(
        INodeRegistry registry,
        IMasterIdentity identity,
        IssuedCredential masterClientCredential,
        IMasterTrustAnchors anchors,
        TimeProvider timeProvider,
        ILogger<AgentClientFactory>? logger = null,
        AgentClientFactoryOptions? options = null)
        : this(registry, identity, masterClientCredential, trust: null, anchors ?? throw new ArgumentNullException(nameof(anchors)), timeProvider, logger, options)
    {
    }

    private AgentClientFactory(
        INodeRegistry registry,
        IMasterIdentity identity,
        IssuedCredential masterClientCredential,
        TrustAnchorSet? trust,
        IMasterTrustAnchors? anchors,
        TimeProvider timeProvider,
        ILogger<AgentClientFactory>? logger,
        AgentClientFactoryOptions? options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(masterClientCredential);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _registry = registry;
        _time = timeProvider;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _options = options ?? new AgentClientFactoryOptions();
        _anchors = anchors;
        _clientCredential = masterClientCredential;
        if (anchors is null)
        {
            ArgumentNullException.ThrowIfNull(trust);
            _material = new MasterTlsMaterial(masterClientCredential, trust);
        }
        else
        {
            _trustVersion = anchors.Version;
            using TrustAnchorSet fromAnchors = TrustAnchorSet.FromPem(anchors.AnchorPems);
            _material = new MasterTlsMaterial(masterClientCredential, fromAnchors);
        }

        _context = new AgentClientContext(timeProvider, identity, GetNegotiatedProtocol, _options, _logger);
    }

    /// <summary>SPKI pins of the CAs agents are validated under now (diagnostics and tests).</summary>
    public IReadOnlyList<string> TrustedCaSpkis
    {
        get
        {
            List<AgentHandlerGeneration> drain = [];
            IReadOnlyList<string> pins;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                EnsureTrustCurrent(drain);
                pins = _material.Trust.SpkiSha256s;
            }

            BeginDrain(drain);
            return pins;
        }
    }

    /// <summary>Node ids reached with the previous master client certificate (CA rotation; diagnostics and tests).</summary>
    public IReadOnlyList<string> PreviousClientNodeIds
    {
        get
        {
            lock (_gate)
            {
                return _previousCredential is null ? [] : [.. _previousNodes.Order(StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>Node ids that currently have a client.</summary>
    public IReadOnlyList<string> NodeIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _nodes.Keys.Order(StringComparer.Ordinal)];
            }
        }
    }

    /// <inheritdoc />
    public IAgentClient GetClient(string nodeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        List<AgentHandlerGeneration> drain = [];
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                EnsureTrustCurrent(drain);
                if (!_nodes.TryGetValue(nodeId, out NodeChannel? channel))
                {
                    throw new KeyNotFoundException($"No agent client for node '{nodeId}' (unknown, revoked or not enrolled; refresh the factory after registry changes).");
                }

                return channel.Current.Client;
            }
        }
        finally
        {
            BeginDrain(drain);
        }
    }

    /// <inheritdoc />
    public Task<IAgentClientLease> CreateFreshClientAsync(string nodeId, CancellationToken ct = default) =>
        CreateFreshClientAsync(nodeId, pins: null, anchorPems: null, ct);

    /// <summary>
    /// A client over brand-new handlers with no pooled connection (design §10.2 step 3), built from the node's registry
    /// record as it is now. With <paramref name="pins"/> the fresh handlers accept only those pins (MN-036: the commit
    /// proof is pinned to the pending SPKI only); without, the record's current and pending pins.
    /// </summary>
    public Task<IAgentClientLease> CreateFreshClientAsync(string nodeId, IReadOnlyCollection<string>? pins, CancellationToken ct = default) =>
        CreateFreshClientAsync(nodeId, pins, anchorPems: null, ct);

    /// <summary>
    /// A fresh client as above that validates the agent under <paramref name="anchorPems"/> only (CA PEMs) instead of the
    /// factory's anchors: the CA-rotation proof that a node's certificate chains to the <b>new</b> root (design §10.2 step 4).
    /// Such a client always presents the <b>current</b> master client certificate, also to a node that is otherwise reached
    /// with the previous one (<see cref="SetPreviousMasterClient"/>): the proof is that the node takes the new material on
    /// both sides (MN-126). Null = the factory's anchors and the node's usual client certificate.
    /// </summary>
    public async Task<IAgentClientLease> CreateFreshClientAsync(string nodeId, IReadOnlyCollection<string>? pins, IReadOnlyList<string>? anchorPems, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        NodeRecord record = await _registry.GetAsync(nodeId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Node '{nodeId}' is not in the registry.");
        using TrustAnchorSet? explicitTrust = anchorPems is null ? null : TrustAnchorSet.FromPem(anchorPems);
        List<AgentHandlerGeneration> drain = [];
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                EnsureTrustCurrent(drain);
                if (!TryDescribe(record, out AgentNodeEndpoint? endpoint, out string[] recordPins, out string? reason))
                {
                    throw new KeyNotFoundException($"Node '{nodeId}' has no usable agent endpoint: {reason}");
                }

                string[] accepted = pins is null ? recordPins : [.. pins];
                var validators = new PeerValidatorSource(PeerValidator.ForAgent(nodeId, explicitTrust ?? _material.Trust, accepted, _time));
                AgentHandlerGeneration generation = CreateGeneration(endpoint!, validators, new InFlightLimiter(_options.MaxDataInFlightPerNode), "fresh client",
                    explicitTrust is null ? null : _material);
                return new Lease(generation);
            }
        }
        finally
        {
            BeginDrain(drain);
        }
    }

    /// <inheritdoc />
    public void RebuildHandlers(string nodeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        AgentHandlerGeneration old;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_nodes.TryGetValue(nodeId, out NodeChannel? channel))
            {
                throw new KeyNotFoundException($"No agent client for node '{nodeId}'.");
            }

            old = Replace(channel, channel.Endpoint, "rebuild");
        }

        old.BeginDrain(_options.DrainTimeout);
    }

    /// <summary>
    /// CA rotation (design §10.2, MN-116): the nodes in <paramref name="nodeIds"/> (those that do not trust the new CA yet)
    /// are reached with <paramref name="previousCredential"/>, the master client certificate from before the re-issue, until
    /// they are removed from the set; every other node with the current one. Null (or an empty set) ends it. Nodes whose
    /// certificate choice changes get new handlers (old ones drain). Not owned: the factory makes its own TLS copy.
    /// </summary>
    public void SetPreviousMasterClient(IssuedCredential? previousCredential, IEnumerable<string> nodeIds)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        HashSet<string> wanted = previousCredential is null ? [] : new HashSet<string>(nodeIds, StringComparer.Ordinal);
        List<AgentHandlerGeneration> drain = [];
        MasterTlsMaterial? released = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureTrustCurrent(drain);
            bool credentialChanged = !ReferenceEquals(previousCredential, _previousCredential);
            if (credentialChanged)
            {
                released = _previousMaterial;
                _previousMaterial = previousCredential is null || wanted.Count == 0 ? null : new MasterTlsMaterial(previousCredential, _material.Trust);
                _previousCredential = _previousMaterial is null ? null : previousCredential;
            }
            else if (_previousMaterial is null && previousCredential is not null && wanted.Count > 0)
            {
                _previousMaterial = new MasterTlsMaterial(previousCredential, _material.Trust);
                _previousCredential = previousCredential;
            }

            _previousNodes.Clear();
            if (_previousMaterial is not null)
            {
                _previousNodes.UnionWith(wanted);
            }

            foreach (NodeChannel channel in _nodes.Values)
            {
                if (!ReferenceEquals(channel.Material, MaterialFor(channel.Endpoint.NodeId)))
                {
                    drain.Add(Replace(channel, channel.Endpoint, "master client certificate choice changed"));
                }
            }
        }

        released?.Release();
        BeginDrain(drain);
    }

    /// <inheritdoc />
    public void SetNegotiatedProtocol(string nodeId, int version)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        _protocols[nodeId] = version;
    }

    /// <summary>The protocol version sent to a node (<see cref="Protocol.CurrentVersion"/> until negotiated).</summary>
    public int GetNegotiatedProtocol(string nodeId) =>
        _protocols.TryGetValue(nodeId, out int version) ? version : Protocol.CurrentVersion;

    /// <summary>
    /// Swaps the master client certificate (re-issue at half life, or under the new CA at CA-rotation step 3, design §10.2)
    /// and rebuilds every node's handlers; old ones drain. The trust stays what it is: the registered anchors
    /// (<see cref="IMasterTrustAnchors"/>, re-read here) or the trust the factory holds. The previous certificate is released
    /// after the last handler that uses it.
    /// </summary>
    public void UpdateMasterClient(IssuedCredential masterClientCredential)
    {
        ArgumentNullException.ThrowIfNull(masterClientCredential);
        List<AgentHandlerGeneration> old = [];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_anchors is null)
            {
                SwapMaterials(masterClientCredential, _material.Trust, old, "master client certificate changed");
            }
            else
            {
                _trustVersion = _anchors.Version;
                using TrustAnchorSet fromAnchors = TrustAnchorSet.FromPem(_anchors.AnchorPems);
                SwapMaterials(masterClientCredential, fromAnchors, old, "master client certificate changed");
            }
        }

        BeginDrain(old);
    }

    /// <summary>
    /// Swaps the master client certificate and the trust anchors and rebuilds every node's handlers; old ones drain. Meant
    /// for a factory built without <see cref="IMasterTrustAnchors"/>. With registered anchors those are the one source of
    /// the trust (a CA rotation's second root must not be dropped by a caller holding one CA), so a
    /// <paramref name="trust"/> that is not exactly those anchors is refused (<see cref="ArgumentException"/>) rather than
    /// silently ignored (MN-126); <see cref="UpdateMasterClient"/> changes the certificate alone.
    /// </summary>
    public void UpdateMasterTls(IssuedCredential masterClientCredential, TrustAnchorSet trust)
    {
        ArgumentNullException.ThrowIfNull(masterClientCredential);
        ArgumentNullException.ThrowIfNull(trust);
        List<AgentHandlerGeneration> old = [];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_anchors is null)
            {
                SwapMaterials(masterClientCredential, trust, old, "master TLS material changed");
            }
            else
            {
                _trustVersion = _anchors.Version;
                using TrustAnchorSet fromAnchors = TrustAnchorSet.FromPem(_anchors.AnchorPems);
                if (!SameAnchors(fromAnchors, trust))
                {
                    throw new ArgumentException(
                        $"The trust ({string.Join(", ", trust.SpkiSha256s)}) is not the registered anchors ({string.Join(", ", fromAnchors.SpkiSha256s)}): change the anchors through IMasterTrustAnchors, or call UpdateMasterClient.",
                        nameof(trust));
                }

                SwapMaterials(masterClientCredential, fromAnchors, old, "master TLS material changed");
            }
        }

        BeginDrain(old);
    }

    private static bool SameAnchors(TrustAnchorSet a, TrustAnchorSet b) =>
        a.SpkiSha256s.Count == b.SpkiSha256s.Count && a.SpkiSha256s.All(pin => b.SpkiSha256s.Any(other => Spki.PinEquals(pin, other)));

    /// <summary>Synchronizes every node's client with the registry (see the class remarks).</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        IReadOnlyList<NodeRecord> records = await _registry.ListAsync(ct).ConfigureAwait(false);
        List<AgentHandlerGeneration> drain = [];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureTrustCurrent(drain);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (NodeRecord record in records)
            {
                seen.Add(record.Id);
                Apply(record, drain);
            }

            foreach (string nodeId in _nodes.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                drain.Add(Remove(nodeId, "not in the registry"));
            }
        }

        foreach (AgentHandlerGeneration generation in drain)
        {
            generation.BeginDrain(_options.DrainTimeout);
        }
    }

    /// <summary>Synchronizes one node's client with its registry record.</summary>
    public async Task RefreshNodeAsync(string nodeId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        NodeRecord? record = await _registry.GetAsync(nodeId, ct).ConfigureAwait(false);
        List<AgentHandlerGeneration> drain = [];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureTrustCurrent(drain);
            if (record is null)
            {
                if (_nodes.ContainsKey(nodeId))
                {
                    drain.Add(Remove(nodeId, "not in the registry"));
                }
            }
            else
            {
                Apply(record, drain);
            }
        }

        foreach (AgentHandlerGeneration generation in drain)
        {
            generation.BeginDrain(_options.DrainTimeout);
        }
    }

    /// <summary>The current generation of a node (tests and diagnostics).</summary>
    internal AgentHandlerGeneration GetGeneration(string nodeId)
    {
        lock (_gate)
        {
            return _nodes.TryGetValue(nodeId, out NodeChannel? channel)
                ? channel.Current
                : throw new KeyNotFoundException($"No agent client for node '{nodeId}'.");
        }
    }

    public void Dispose()
    {
        List<AgentHandlerGeneration> generations;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            generations = [.. _nodes.Values.Select(c => c.Current)];
            _nodes.Clear();
        }

        foreach (AgentHandlerGeneration generation in generations)
        {
            generation.DisposeNow();
        }

        _material.Release();
        _previousMaterial?.Release();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Must hold <see cref="_gate"/>. Adds, updates or removes the node's channel; old generations go to <paramref name="drain"/>.</summary>
    private void Apply(NodeRecord record, List<AgentHandlerGeneration> drain)
    {
        _nodes.TryGetValue(record.Id, out NodeChannel? existing);
        if (!TryDescribe(record, out AgentNodeEndpoint? endpoint, out string[] pins, out string? reason))
        {
            if (existing is not null)
            {
                drain.Add(Remove(record.Id, reason!));
            }
            else if (!record.Revoked)
            {
                AgentClientLog.NodeUnusable(_logger, record.Id, reason!);
            }

            return;
        }

        PeerValidator validator;
        try
        {
            validator = PeerValidator.ForAgent(record.Id, _material.Trust, pins, _time);
        }
        catch (Exception ex) when (ex is ArgumentException or PkiException)
        {
            if (existing is not null)
            {
                drain.Add(Remove(record.Id, ex.Message));
            }
            else
            {
                AgentClientLog.NodeUnusable(_logger, record.Id, ex.Message);
            }

            return;
        }

        if (existing is null)
        {
            var validators = new PeerValidatorSource(validator);
            var limiter = new InFlightLimiter(_options.MaxDataInFlightPerNode);
            var channel = new NodeChannel(endpoint!, validators, pins, limiter);
            channel.Current = CreateGeneration(endpoint!, validators, limiter, "node added");
            channel.Material = MaterialFor(record.Id);
            _nodes[record.Id] = channel;
            return;
        }

        bool pinRemoved = existing.Pins.Any(p => !pins.Contains(p, StringComparer.Ordinal));
        bool pinsChanged = pinRemoved || pins.Any(p => !existing.Pins.Contains(p, StringComparer.Ordinal));
        bool endpointChanged = existing.Endpoint != endpoint;
        if (pinsChanged)
        {
            // Read per handshake: a pending pin applies to the next new connection of the existing handlers (MN-090).
            existing.Validators.Set(validator);
            existing.Pins = pins;
        }

        if (endpointChanged || pinRemoved)
        {
            // Pooled connections authenticated against a pin that is no longer accepted (or to another address) must drain.
            drain.Add(Replace(existing, endpoint!, endpointChanged ? "address or uid changed" : "pin removed"));
        }
    }

    /// <summary>Must hold <see cref="_gate"/>.</summary>
    private AgentHandlerGeneration Replace(NodeChannel channel, AgentNodeEndpoint endpoint, string reason)
    {
        AgentHandlerGeneration old = channel.Current;
        channel.Endpoint = endpoint;
        channel.Current = CreateGeneration(endpoint, channel.Validators, channel.DataLimiter, reason);
        channel.Material = MaterialFor(endpoint.NodeId);
        return old;
    }

    /// <summary>Must hold <see cref="_gate"/>. The material a node's handlers present: the previous client certificate for a node in the CA-rotation set.</summary>
    private MasterTlsMaterial MaterialFor(string nodeId) =>
        _previousMaterial is not null && _previousNodes.Contains(nodeId) ? _previousMaterial : _material;

    /// <summary>Must hold <see cref="_gate"/>. Rebuilds every node under the registered anchors when they changed.</summary>
    private void EnsureTrustCurrent(List<AgentHandlerGeneration> drain)
    {
        if (_anchors is null || _anchors.Version == _trustVersion)
        {
            return;
        }

        _trustVersion = _anchors.Version;
        using TrustAnchorSet trust = TrustAnchorSet.FromPem(_anchors.AnchorPems);
        SwapMaterials(_clientCredential, trust, drain, "trust anchors changed");
        string pins = string.Join(", ", trust.SpkiSha256s);
        FactoryLog.TrustAnchorsChanged(_logger, pins);
    }

    /// <summary>Must hold <see cref="_gate"/>. New current (and previous) material under <paramref name="trust"/>; every node is rebuilt.</summary>
    private void SwapMaterials(IssuedCredential clientCredential, TrustAnchorSet trust, List<AgentHandlerGeneration> drain, string reason)
    {
        MasterTlsMaterial next = new(clientCredential, trust);
        MasterTlsMaterial? nextPrevious = null;
        try
        {
            nextPrevious = _previousCredential is null ? null : new MasterTlsMaterial(_previousCredential, trust);
        }
        catch
        {
            next.Release();
            throw;
        }

        MasterTlsMaterial current = _material;
        MasterTlsMaterial? previous = _previousMaterial;
        _material = next;
        _previousMaterial = nextPrevious;
        _clientCredential = clientCredential;
        foreach (NodeChannel channel in _nodes.Values)
        {
            channel.Validators.Set(PeerValidator.ForAgent(channel.Endpoint.NodeId, _material.Trust, channel.Pins, _time));
            drain.Add(Replace(channel, channel.Endpoint, reason));
        }

        current.Release();
        previous?.Release();
    }

    private void BeginDrain(List<AgentHandlerGeneration> generations)
    {
        foreach (AgentHandlerGeneration generation in generations)
        {
            generation.BeginDrain(_options.DrainTimeout);
        }
    }

    /// <summary>Must hold <see cref="_gate"/>.</summary>
    private AgentHandlerGeneration Remove(string nodeId, string reason)
    {
        NodeChannel channel = _nodes[nodeId];
        _nodes.Remove(nodeId);
        AgentClientLog.NodeRemoved(_logger, nodeId, reason);
        return channel.Current;
    }

    /// <summary>Must hold <see cref="_gate"/>. <paramref name="material"/> null = the node's (<see cref="MaterialFor"/>).</summary>
    private AgentHandlerGeneration CreateGeneration(AgentNodeEndpoint endpoint, PeerValidatorSource validators, InFlightLimiter limiter, string reason, MasterTlsMaterial? material = null)
    {
        long id = Interlocked.Increment(ref _generationIds);
        material = (material ?? MaterialFor(endpoint.NodeId)).Acquire();
        try
        {
            var generation = new AgentHandlerGeneration(id, endpoint, validators, material, _context, limiter);
            AgentClientLog.GenerationCreated(_logger, endpoint.NodeId, id, reason);
            return generation;
        }
        catch
        {
            material.Release();
            throw;
        }
    }

    /// <summary>A record's endpoint and pins, or why the master cannot talk to it.</summary>
    private static bool TryDescribe(NodeRecord record, out AgentNodeEndpoint? endpoint, out string[] pins, out string? reason)
    {
        endpoint = null;
        pins = [.. new[] { record.CertSpkiSha256, record.PendingCertSpkiSha256 }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.Ordinal)];
        if (record.Revoked)
        {
            reason = "revoked";
            return false;
        }

        if (!Protocol.IsValidNodeId(record.Id))
        {
            reason = "the node id breaks the node id rule";
            return false;
        }

        if (pins.Length == 0)
        {
            reason = "no certificate pin (not enrolled)";
            return false;
        }

        if (!TryParseAddress(record.Address, out Uri? baseUri))
        {
            reason = $"the address '{record.Address}' is not an https URL";
            return false;
        }

        endpoint = new AgentNodeEndpoint(record.Id, record.Uid, baseUri!);
        reason = null;
        return true;
    }

    /// <summary><c>https://host:port</c> (a bare <c>host:port</c> gets <c>https://</c>), normalized to end with <c>/</c>.</summary>
    internal static bool TryParseAddress(string? address, out Uri? baseUri)
    {
        baseUri = null;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        string text = address.Contains("://", StringComparison.Ordinal) ? address.Trim() : "https://" + address.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? parsed) || parsed.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        baseUri = new Uri(parsed.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/", UriKind.Absolute);
        return true;
    }

    private sealed class NodeChannel
    {
        public NodeChannel(AgentNodeEndpoint endpoint, PeerValidatorSource validators, string[] pins, InFlightLimiter dataLimiter)
        {
            Endpoint = endpoint;
            Validators = validators;
            Pins = pins;
            DataLimiter = dataLimiter;
        }

        public AgentNodeEndpoint Endpoint { get; set; }

        public PeerValidatorSource Validators { get; }

        public string[] Pins { get; set; }

        public InFlightLimiter DataLimiter { get; }

        public AgentHandlerGeneration Current { get; set; } = null!;

        /// <summary>The material <see cref="Current"/> was built with (reference only; the generation holds the count).</summary>
        public MasterTlsMaterial Material { get; set; } = null!;
    }

    private sealed class Lease : IAgentClientLease
    {
        private readonly AgentHandlerGeneration _generation;

        public Lease(AgentHandlerGeneration generation) => _generation = generation;

        public IAgentClient Client => _generation.Client;

        public ValueTask DisposeAsync() => _generation.DisposeAsync();
    }
}

/// <summary>Source-generated log messages of the factory's trust handling (CA rotation, MN-116). Pins only.</summary>
internal static partial class FactoryLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Agent trust anchors changed to {Pins}; every node's handlers are rebuilt.")]
    public static partial void TrustAnchorsChanged(ILogger logger, string pins);
}
