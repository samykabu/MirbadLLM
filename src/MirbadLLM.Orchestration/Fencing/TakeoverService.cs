using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Nodes;

namespace MirbadLLM.Orchestration.Fencing;

/// <summary>Tunables of <see cref="TakeoverService"/>.</summary>
public sealed class TakeoverServiceOptions
{
    /// <summary>How long a takeover waits for one agent's <c>/info</c> before recording it unreachable.</summary>
    public TimeSpan InfoTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the hosted loop checks the takeover flag file and option (design §4.2 step 2).</summary>
    public TimeSpan RequestPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Longest wait between two automatic attempts of a takeover that keeps failing (MN-130): the wait starts at
    /// <see cref="RequestPollInterval"/> and doubles after each failure up to this, so a failing option or flag-file takeover
    /// writes one Failed audit line per attempt at a falling rate instead of one every 5 s. A success, or the request going
    /// away, resets it.
    /// </summary>
    public TimeSpan MaxRetryInterval { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Takeover completion on the master (design §4.2 step 3, §3.5 Pending takeover; MN-115), the
/// <see cref="IFencingCoordinator"/> behind <c>/api/fencing</c> and the takeover banner (MN-121).
/// <list type="number">
/// <item><see cref="TakeOverAsync"/> (the operator confirmed the previous master is stopped): reads the accepted epoch of
/// every non-revoked node's agent from <c>/info</c> (bounded by <see cref="TakeoverServiceOptions.InfoTimeout"/> each; a
/// node without a client, a transport failure or an error answer is unreachable), computes
/// <c>max(current epoch, every answering agent's epoch) + 1</c> (<see cref="TakeoverRules"/>), persists the epoch with the
/// <see cref="TakeoverRecord"/> before any agent sees it (<see cref="IMasterIdentity.CompleteTakeoverAsync"/>, which also
/// deletes the flag file), marks the unreachable nodes Pending takeover in <see cref="NodeStatusCache"/> and re-synchronizes
/// the answering ones. The record takes the one-shot <see cref="FencingOptions.TakeoverId"/> while the option still asks
/// for a takeover, so the option is ignored on later boots.</item>
/// <item><see cref="ContinueAsync"/>: for a node the cache shows <see cref="NodeFencingState.NeedsContinuation"/> (it
/// returned holding an epoch not below the master's) and only with the takeover id and agent epoch the operator saw: raises
/// the epoch to <c>agentEpoch + 1</c> and records the continuation in the latest record. Never automatic.</item>
/// <item>As a hosted service it runs <see cref="TakeOverAsync"/> as <see cref="AuditActors.Config"/> whenever
/// <see cref="IMasterIdentity.TakeoverRequested"/> (the flag file <c>{DataPath}/takeover</c> or the option, design §4.2
/// step 2: the operator's own action), re-checked once the serializing gate is taken so a request another takeover served
/// meanwhile does not run twice. Register it after the client factory's registry sync so the agents are known.</item>
/// </list>
/// Takeovers and continuations are serialized. Every attempt writes one audit line (<see cref="AuditActions.FencingTakeover"/>
/// or <see cref="AuditActions.FencingContinue"/>, target = takeover id) through <see cref="IAuditLog"/>.
/// </summary>
public sealed partial class TakeoverService : BackgroundService, IFencingCoordinator
{
    private readonly IMasterIdentity _identity;
    private readonly NodeStatusCache _cache;
    private readonly INodeRegistry _registry;
    private readonly IAgentClientFactory _clients;
    private readonly TimeProvider _time;
    private readonly IAuditLog _audit;
    private readonly FencingOptions _fencing;
    private readonly TakeoverServiceOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _versionGate = new();
    private string? _fingerprint;
    private long _version;

    /// <summary>Creates the service.</summary>
    /// <param name="identity">The master identity (epoch, records, takeover request).</param>
    /// <param name="cache">The node status cache: Pending takeover marks and every node's fencing state.</param>
    /// <param name="registry">Node registry (the nodes a takeover asks).</param>
    /// <param name="clients">Agent clients (<c>/info</c>).</param>
    /// <param name="timeProvider">Clock of the records.</param>
    /// <param name="audit">Audit log; null = <see cref="NullAuditLog"/>.</param>
    /// <param name="fencing">Fencing options (the takeover option and its one-shot id); null = none set.</param>
    /// <param name="options">Tunables; null = defaults.</param>
    /// <param name="logger">Logger.</param>
    public TakeoverService(
        IMasterIdentity identity,
        NodeStatusCache cache,
        INodeRegistry registry,
        IAgentClientFactory clients,
        TimeProvider timeProvider,
        IAuditLog? audit = null,
        FencingOptions? fencing = null,
        TakeoverServiceOptions? options = null,
        ILogger<TakeoverService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _identity = identity;
        _cache = cache;
        _registry = registry;
        _clients = clients;
        _time = timeProvider;
        _audit = audit ?? NullAuditLog.Instance;
        _fencing = fencing ?? new FencingOptions();
        _options = options ?? new TakeoverServiceOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public long Version
    {
        get
        {
            Observe(BuildStatus());
            return Interlocked.Read(ref _version);
        }
    }

    /// <inheritdoc />
    public FencingStatus GetStatus()
    {
        FencingStatus status = BuildStatus();
        Observe(status);
        return status;
    }

    /// <inheritdoc />
    public async Task<TakeoverRecord> TakeOverAsync(string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await TakeOverLockedAsync(actor, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The hosted loop's takeover: runs <see cref="TakeOverAsync"/> only if <see cref="IMasterIdentity.TakeoverRequested"/>
    /// still holds once the gate is taken. A takeover that completed while this one waited (the API's, or another pass)
    /// already deleted the flag file or consumed the option, so the request it saw is spent and nothing runs (null).
    /// </summary>
    internal async Task<TakeoverRecord?> TakeOverIfRequestedAsync(string actor, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _identity.TakeoverRequested ? await TakeOverLockedAsync(actor, ct).ConfigureAwait(false) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The takeover itself; the caller holds <see cref="_gate"/>.</summary>
    private async Task<TakeoverRecord> TakeOverLockedAsync(string actor, CancellationToken ct)
    {
        long previous = _identity.Epoch;
        string? takeoverId = null;
        try
        {
            IReadOnlyList<NodeRecord> records = await _registry.ListAsync(ct).ConfigureAwait(false);
            NodeRecord[] nodes = [.. records.Where(r => !r.Revoked).OrderBy(r => r.Id, StringComparer.Ordinal)];
            TakeoverNodeEpoch?[] probes = await Task.WhenAll(nodes.Select(n => ProbeAsync(n, ct))).ConfigureAwait(false);
            List<TakeoverNodeEpoch> answered = [.. probes.OfType<TakeoverNodeEpoch>()];
            List<string> unreachable = [.. nodes.Where((_, i) => probes[i] is null).Select(n => n.Id)];

            previous = _identity.Epoch;
            TakeoverRecord record = TakeoverRules.CreateRecord(
                previous, answered, unreachable, actor, _time.GetUtcNow(), OptionTakeoverId());
            takeoverId = record.TakeoverId;
            await _identity.CompleteTakeoverAsync(record, ct).ConfigureAwait(false);
            TakeoverRecord stored = Stored(record.TakeoverId);
            _cache.MarkPendingTakeover(stored);
            _cache.EpochRaised();
            TakeoverLog.TakeoverDone(_logger, stored.TakeoverId, previous, _identity.Epoch, actor, answered.Count, string.Join(", ", stored.Unreachable));

            await RefreshPersistedAsync(answered.Select(a => a.NodeId), ct).ConfigureAwait(false);
            await AuditAsync(actor, AuditActions.FencingTakeover, null, stored.TakeoverId, AuditResults.Succeeded, null,
                Detail(previous, _identity.Epoch, answered, stored.Unreachable)).ConfigureAwait(false);
            return stored;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            TakeoverLog.TakeoverFailed(_logger, ex, actor);
            await AuditAsync(actor, AuditActions.FencingTakeover, null, takeoverId, AuditResults.Failed,
                ex is InvalidOperationException ? ErrorCodes.InvalidState : ErrorCodes.InternalError,
                string.Create(CultureInfo.InvariantCulture, $"epoch {previous}: {ex.GetType().Name}")).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<TakeoverRecord> ContinueAsync(
        string nodeId, string takeoverId, long agentEpoch, string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(takeoverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        long previous = _identity.Epoch;
        try
        {
            TakeoverRecord latest = _identity.Takeovers.Count > 0
                ? _identity.Takeovers[^1]
                : throw Refuse("No takeover has been performed on this master; there is nothing to continue.");
            if (!string.Equals(latest.TakeoverId, takeoverId, StringComparison.Ordinal))
            {
                throw Refuse($"Takeover {takeoverId} is not the latest takeover ({latest.TakeoverId}); reload and confirm again.");
            }

            // Read the agent's epoch now (best effort: an unreachable node keeps its last view).
            await RefreshAsync([nodeId], ct).ConfigureAwait(false);
            NodeFencingStatus node = _cache.ListFencing().FirstOrDefault(n => string.Equals(n.NodeId, nodeId, StringComparison.Ordinal))
                ?? throw Refuse($"Node '{nodeId}' is not monitored (unknown or revoked).");
            if (node.State != NodeFencingState.NeedsContinuation)
            {
                throw Refuse($"Node '{nodeId}' does not need a takeover continuation (fencing state {node.State}).");
            }

            if (node.AgentEpoch != agentEpoch)
            {
                throw Refuse(string.Create(CultureInfo.InvariantCulture,
                    $"Node '{nodeId}' now reports epoch {node.AgentEpoch?.ToString(CultureInfo.InvariantCulture) ?? "none"}, not {agentEpoch}; reload and confirm again."));
            }

            previous = _identity.Epoch;
            TakeoverRecord continued = TakeoverRules.AddContinuation(latest, nodeId, agentEpoch, previous, actor, _time.GetUtcNow());
            await _identity.CompleteTakeoverAsync(continued, ct).ConfigureAwait(false);
            TakeoverRecord stored = Stored(latest.TakeoverId);
            _cache.EpochRaised(); // its block predates the raise: Pending takeover, not NeedsContinuation, until its /info
            TakeoverLog.ContinuationDone(_logger, stored.TakeoverId, nodeId, agentEpoch, _identity.Epoch, actor);

            // The node now holds a lower epoch: its next /info clears the mark and it connects.
            await RefreshPersistedAsync([nodeId], ct).ConfigureAwait(false);
            await AuditAsync(actor, AuditActions.FencingContinue, nodeId, stored.TakeoverId, AuditResults.Succeeded, null,
                string.Create(CultureInfo.InvariantCulture, $"agent epoch {agentEpoch}; epoch {previous} → {_identity.Epoch}")).ConfigureAwait(false);
            return stored;
        }
        catch (TakeoverRefusedException ex)
        {
            await AuditAsync(actor, AuditActions.FencingContinue, nodeId, takeoverId, AuditResults.Refused, ErrorCodes.InvalidState, ex.Message)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            TakeoverLog.ContinuationFailed(_logger, ex, nodeId, actor);
            await AuditAsync(actor, AuditActions.FencingContinue, nodeId, takeoverId, AuditResults.Failed,
                ex is InvalidOperationException ? ErrorCodes.InvalidState : ErrorCodes.InternalError,
                string.Create(CultureInfo.InvariantCulture, $"epoch {previous}: {ex.GetType().Name}")).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    /// <summary>Runs the takeover the flag file or option asks for (design §4.2 step 2), then keeps watching for the flag.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Backoff of a failing automatic takeover (MN-130): only the attempts are spaced out; the status and Version the
            // banner reads are untouched by it, so they stay stable between retries.
            int failures = 0;
            DateTimeOffset nextAttempt = DateTimeOffset.MinValue;
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!_identity.TakeoverRequested)
                {
                    failures = 0;
                    nextAttempt = DateTimeOffset.MinValue;
                }
                else if (_time.GetUtcNow() >= nextAttempt)
                {
                    try
                    {
                        await TakeOverIfRequestedAsync(AuditActors.Config, stoppingToken).ConfigureAwait(false);
                        failures = 0;
                        nextAttempt = DateTimeOffset.MinValue;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Logged and audited by TakeOverAsync; retried while still requested, after a growing wait.
                        failures++;
                        TimeSpan wait = RetryDelay(_options, failures);
                        nextAttempt = _time.GetUtcNow() + wait;
                        TakeoverLog.RetryScheduled(_logger, failures, wait.TotalSeconds);
                    }
                }

                await Task.Delay(_options.RequestPollInterval, _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    private static TakeoverRefusedException Refuse(string message) => new(message);

    /// <summary>The wait after <paramref name="failures"/> consecutive failed automatic attempts (1 → one poll interval).</summary>
    internal static TimeSpan RetryDelay(TakeoverServiceOptions options, int failures)
    {
        ArgumentNullException.ThrowIfNull(options);
        TimeSpan poll = options.RequestPollInterval > TimeSpan.Zero ? options.RequestPollInterval : TimeSpan.FromSeconds(5);
        TimeSpan max = options.MaxRetryInterval > poll ? options.MaxRetryInterval : poll;
        double factor = Math.Pow(2, Math.Clamp(failures - 1, 0, 30));
        return TimeSpan.FromTicks((long)Math.Min(max.Ticks, poll.Ticks * factor));
    }

    private static string Detail(long previous, long epoch, List<TakeoverNodeEpoch> answered, IReadOnlyList<string> unreachable)
    {
        var detail = new StringBuilder();
        detail.Append(CultureInfo.InvariantCulture, $"epoch {previous} → {epoch}; answered {answered.Count}");
        if (answered.Count > 0)
        {
            detail.Append(" (")
                .AppendJoin(", ", answered.Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.NodeId}@{a.AcceptedEpoch?.ToString(CultureInfo.InvariantCulture) ?? "none"}")))
                .Append(')');
        }

        detail.Append(CultureInfo.InvariantCulture, $"; unreachable {unreachable.Count}");
        if (unreachable.Count > 0)
        {
            detail.Append(" (").AppendJoin(", ", unreachable).Append(')');
        }

        return detail.ToString();
    }

    /// <summary>The option's one-shot id while it still asks for a takeover (never an id already on file).</summary>
    private string? OptionTakeoverId()
    {
        if (!_fencing.Takeover || string.IsNullOrWhiteSpace(_fencing.TakeoverId))
        {
            return null;
        }

        string id = _fencing.TakeoverId.Trim();
        return _identity.Takeovers.Any(r => string.Equals(r.TakeoverId, id, StringComparison.Ordinal)) ? null : id;
    }

    private TakeoverRecord Stored(string takeoverId) =>
        _identity.Takeovers.LastOrDefault(r => string.Equals(r.TakeoverId, takeoverId, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"Takeover {takeoverId} was not persisted.");

    /// <summary>One agent's accepted epoch, or null when it did not answer in time.</summary>
    private async Task<TakeoverNodeEpoch?> ProbeAsync(NodeRecord node, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.InfoTimeout);
        try
        {
            InfoResponse info = await _clients.GetClient(node.Id).GetInfoAsync(timeout.Token).ConfigureAwait(false);
            return new TakeoverNodeEpoch(node.Id, info.NodeUid, info.AcceptedMaster?.Epoch);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is AgentUnreachableException or AgentException
            or KeyNotFoundException or HttpRequestException or IOException or OperationCanceledException or TimeoutException)
        {
            TakeoverLog.NodeUnreachable(_logger, node.Id, ex.Message);
            return null;
        }
    }

    /// <summary>Re-synchronizes nodes after an epoch change (best effort; the monitor retries on its own).</summary>
    private async Task RefreshAsync(IEnumerable<string> nodeIds, CancellationToken ct)
    {
        await Task.WhenAll(nodeIds.Select(async nodeId =>
        {
            try
            {
                await _cache.RefreshNodeAsync(nodeId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                TakeoverLog.RefreshFailed(_logger, nodeId, ex.Message);
            }
        })).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="RefreshAsync"/> after the epoch is persisted: a cancelled caller (an API client that went away) no longer
    /// skips the audit line of a takeover or continuation that already happened (MN-126); the monitor re-synchronizes.
    /// </summary>
    private async Task RefreshPersistedAsync(IEnumerable<string> nodeIds, CancellationToken ct)
    {
        try
        {
            await RefreshAsync(nodeIds, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Persisted already; nothing to undo.
        }
    }

    private async Task AuditAsync(
        string actor, string action, string? nodeId, string? target, string result, string? errorCode, string? detail)
    {
        try
        {
            await _audit.WriteAsync(new AuditRecord
            {
                Time = _time.GetUtcNow(),
                Actor = actor,
                Action = action,
                NodeId = nodeId,
                Target = target,
                Result = result,
                ErrorCode = errorCode,
                Detail = detail,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // IAuditLog never fails the action it records.
            TakeoverLog.AuditFailed(_logger, ex, action);
        }
    }

    private FencingStatus BuildStatus()
    {
        IReadOnlyList<NodeFencingStatus> nodes = _cache.ListFencing();
        IReadOnlyList<TakeoverRecord> takeovers = _identity.Takeovers;
        return new FencingStatus
        {
            InstanceId = _identity.InstanceId,
            Epoch = _identity.Epoch,
            TakeoverRequested = _identity.TakeoverRequested,
            TakeoverOptionSet = _fencing.Takeover,
            ActionNeeded = nodes.Any(n => n.State is NodeFencingState.Stale or NodeFencingState.Competing or NodeFencingState.NeedsContinuation),
            LastTakeover = takeovers.Count > 0 ? takeovers[^1] : null,
            Nodes = nodes,
        };
    }

    /// <summary>Bumps <see cref="Version"/> when anything the banner shows changed since the last look.</summary>
    private void Observe(FencingStatus status)
    {
        var fingerprint = new StringBuilder();
        fingerprint.Append(CultureInfo.InvariantCulture,
            $"{status.InstanceId}|{status.Epoch}|{status.TakeoverRequested}|{status.TakeoverOptionSet}|{status.ActionNeeded}|{status.LastTakeover?.TakeoverId}|{status.LastTakeover?.Continuations.Count}");
        foreach (NodeFencingStatus node in status.Nodes)
        {
            fingerprint.Append(CultureInfo.InvariantCulture, $"|{node.NodeId}:{node.State}:{node.AgentEpoch}");
        }

        string value = fingerprint.ToString();
        lock (_versionGate)
        {
            if (!string.Equals(value, _fingerprint, StringComparison.Ordinal))
            {
                _fingerprint = value;
                Interlocked.Increment(ref _version);
            }
        }
    }

    /// <summary>A continuation refused because of the current state (409 <c>invalid_state</c>).</summary>
    private sealed class TakeoverRefusedException(string message) : InvalidOperationException(message);
}

/// <summary>DI helper for the takeover service (registered by the P2 composition, MN-130).</summary>
public static class TakeoverServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TakeoverService"/> as the singleton <see cref="IFencingCoordinator"/> and as a hosted service.
    /// Needs the master composition (<see cref="IMasterIdentity"/>, <see cref="NodeStatusCache"/>, <see cref="INodeRegistry"/>,
    /// <see cref="IAgentClientFactory"/>, <see cref="FencingOptions"/>); uses <see cref="IAuditLog"/> when registered. Call it
    /// after the registry sync's hosted service so the agent clients exist when a requested takeover runs at boot.
    /// Idempotent: a later call only replaces the <paramref name="options"/>. A second hosted-service registration would
    /// start the same instance twice, and two loops polling the flag file run two takeovers for one request (MN-115).
    /// </summary>
    public static IServiceCollection AddTakeoverService(this IServiceCollection services, TakeoverServiceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.RemoveAll<TakeoverServiceOptions>();
        services.AddSingleton(options ?? new TakeoverServiceOptions());
        if (services.Any(d => d.ServiceType == typeof(TakeoverService)))
        {
            return services;
        }

        services.AddSingleton(sp => new TakeoverService(
            sp.GetRequiredService<IMasterIdentity>(),
            sp.GetRequiredService<NodeStatusCache>(),
            sp.GetRequiredService<INodeRegistry>(),
            sp.GetRequiredService<IAgentClientFactory>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetService<IAuditLog>(),
            sp.GetService<IOptions<FencingOptions>>()?.Value,
            sp.GetRequiredService<TakeoverServiceOptions>(),
            sp.GetService<ILogger<TakeoverService>>()));
        services.AddSingleton<IFencingCoordinator>(sp => sp.GetRequiredService<TakeoverService>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<TakeoverService>());
        return services;
    }
}

/// <summary>Source-generated log messages of the takeover (MN-115).</summary>
internal static partial class TakeoverLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "The requested takeover failed {Failures} time(s) in a row; next attempt in {Seconds:0} s.")]
    public static partial void RetryScheduled(ILogger logger, int failures, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Takeover {TakeoverId} by {Actor}: epoch {Previous} -> {Epoch}; {Answered} agents answered; unreachable (Pending takeover): [{Unreachable}].")]
    public static partial void TakeoverDone(ILogger logger, string takeoverId, long previous, long epoch, string actor, int answered, string unreachable);

    [LoggerMessage(Level = LogLevel.Error, Message = "Takeover by {Actor} failed.")]
    public static partial void TakeoverFailed(ILogger logger, Exception exception, string actor);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Takeover {TakeoverId} continued for node {NodeId} by {Actor}: the agent held epoch {AgentEpoch}; the master now runs epoch {Epoch}.")]
    public static partial void ContinuationDone(ILogger logger, string takeoverId, string nodeId, long agentEpoch, long epoch, string actor);

    [LoggerMessage(Level = LogLevel.Error, Message = "Takeover continuation for node {NodeId} by {Actor} failed.")]
    public static partial void ContinuationFailed(ILogger logger, Exception exception, string nodeId, string actor);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Takeover: node {NodeId} did not answer /info ({Reason}); it is recorded unreachable.")]
    public static partial void NodeUnreachable(ILogger logger, string nodeId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Takeover: re-synchronizing node {NodeId} failed ({Reason}); the node monitor retries.")]
    public static partial void RefreshFailed(ILogger logger, string nodeId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Writing the {Action} audit line failed.")]
    public static partial void AuditFailed(ILogger logger, Exception exception, string action);

    [LoggerMessage(Level = LogLevel.Error, Message = "MirbadLLM__Fencing__Takeover is still set, but its takeover {TakeoverId} already completed; the option is ignored (one-shot). Remove MirbadLLM__Fencing__Takeover and MirbadLLM__Fencing__TakeoverId.")]
    public static partial void OptionAlreadyUsed(ILogger logger, string takeoverId);
}
