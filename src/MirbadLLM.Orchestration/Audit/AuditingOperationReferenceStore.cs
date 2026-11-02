using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;

namespace MirbadLLM.Orchestration.Audit;

/// <summary>
/// <see cref="AgentOperationReference.Owner"/> values that name a system actor (design §6.7, §10.5): <c>{kind}:{id}</c>.
/// The P2 tasks that own jobs (MN-111), moves (MN-109) and restores (MN-113) pass these as the runner's <c>owner</c>.
/// </summary>
public static class AuditOwners
{
    /// <summary>Owner of a benchmark or tuning job's operations.</summary>
    public static string Job(string jobId) => "job:" + jobId;

    /// <summary>Owner of a move record's operations.</summary>
    public static string Move(string moveId) => "move:" + moveId;

    /// <summary>Owner of a restore job's operations.</summary>
    public static string Restore(string restoreId) => "restore:" + restoreId;

    /// <summary>Owner of boot or on-request auto-start operations.</summary>
    public static string AutoStart(string modelId) => "autostart:" + modelId;

    /// <summary>The system actor an owner names, or null for a plain (operator) action.</summary>
    public static string? ActorOf(string? owner)
    {
        if (string.IsNullOrEmpty(owner))
        {
            return null;
        }

        int colon = owner.IndexOf(':', StringComparison.Ordinal);
        return (colon < 0 ? owner : owner[..colon]) switch
        {
            "job" => AuditActors.SystemJob,
            "move" => AuditActors.SystemMove,
            "restore" => AuditActors.SystemRestore,
            "autostart" => AuditActors.SystemAutoStart,
            "pki" => AuditActors.SystemPki,
            _ => null,
        };
    }
}

/// <summary>
/// The operation-reference observer for <c>system:*</c> actors (design §10.5): an <see cref="IAgentOperationReferenceStore"/>
/// decorator that writes one audit line when an agent operation's reference is <b>closed</b> (its outcome known), with the
/// operation id so master and agent lines join.
/// <list type="bullet">
/// <item>Submitted inside a request <see cref="AuditScope"/> (an audited <c>/api</c> call): no line here; the id goes to
/// the scope and the request filter's line carries it.</item>
/// <item>Otherwise the actor is the owner's system actor (<see cref="AuditOwners"/>), else the open system scope's actor,
/// else <c>system</c> (an operation the master submitted on its own, e.g. resumed after a restart).</item>
/// </list>
/// Lines are written after the inner store succeeded and never fail the store call.
/// </summary>
public sealed partial class AuditingOperationReferenceStore : IAgentOperationReferenceStore
{
    /// <summary>Actor of a background operation with neither an owner nor a scope.</summary>
    public const string UnattributedSystemActor = "system";

    private readonly IAgentOperationReferenceStore _inner;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, string?> _submitted = new(StringComparer.Ordinal);

    /// <summary>Wraps <paramref name="inner"/>.</summary>
    public AuditingOperationReferenceStore(IAgentOperationReferenceStore inner, IAuditLog audit, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _inner = inner;
        _audit = audit;
        _time = timeProvider;
    }

    /// <inheritdoc />
    public async Task AddAsync(AgentOperationReference reference, CancellationToken ct = default)
    {
        await _inner.AddAsync(reference, ct).ConfigureAwait(false);
        AuditScope? scope = AuditScope.Current;
        if (scope is { IsRequest: true })
        {
            scope.AddOperation(reference.OperationId);
            _submitted[reference.OperationId] = null; // the request line covers it
        }
        else
        {
            _submitted[reference.OperationId] = AuditOwners.ActorOf(reference.Owner) ?? scope?.Actor ?? UnattributedSystemActor;
        }
    }

    /// <inheritdoc />
    public Task<AgentOperationReference?> GetAsync(string operationId, CancellationToken ct = default) => _inner.GetAsync(operationId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentOperationReference>> ListOpenAsync(CancellationToken ct = default) => _inner.ListOpenAsync(ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentOperationReference>> ListAsync(CancellationToken ct = default) => _inner.ListAsync(ct);

    /// <inheritdoc />
    public async Task<AgentOperationReference?> CloseAsync(string operationId, AgentOperationOutcome outcome, string? errorCode, CancellationToken ct = default)
    {
        AgentOperationReference? before = await _inner.GetAsync(operationId, ct).ConfigureAwait(false);
        AgentOperationReference? closed = await _inner.CloseAsync(operationId, outcome, errorCode, ct).ConfigureAwait(false);
        if (closed is null || before is null || before.Status == AgentOperationReferenceStatus.Closed)
        {
            return closed; // unknown id, or already closed: no second line
        }

        string? actor;
        if (_submitted.TryRemove(operationId, out string? known))
        {
            actor = known;
        }
        else
        {
            // Submitted before a master restart (or by another store instance): attribute by owner.
            actor = AuditOwners.ActorOf(closed.Owner) ?? UnattributedSystemActor;
        }

        if (actor is not null)
        {
            await _audit.WriteAsync(ToRecord(closed, actor), CancellationToken.None).ConfigureAwait(false);
        }

        return closed;
    }

    /// <summary>The audit line of a closed reference.</summary>
    internal AuditRecord ToRecord(AgentOperationReference reference, string actor)
    {
        (string action, bool modelTarget) = ActionOf(reference.Action);
        string? error = reference.Error;
        bool isCode = error is not null && ErrorCodePattern().IsMatch(error);
        string? detail = reference.Outcome switch
        {
            AgentOperationOutcome.Cancelled => "cancelled",
            AgentOperationOutcome.Expired => "expired before the outcome was known",
            AgentOperationOutcome.NotAccepted => "never reached the agent",
            AgentOperationOutcome.Lost => "node re-enrolled, revoked or deleted",
            _ => null,
        };
        if (error is not null && !isCode)
        {
            detail = detail is null ? error : detail + ": " + error;
        }

        return new AuditRecord
        {
            Time = reference.ClosedAt ?? _time.GetUtcNow(),
            Actor = actor,
            Action = action,
            NodeId = reference.NodeId,
            ModelId = modelTarget ? reference.Target : null,
            OperationId = reference.OperationId,
            Target = modelTarget ? null : reference.Target,
            Result = reference.Outcome switch
            {
                AgentOperationOutcome.Succeeded => AuditResults.Succeeded,
                AgentOperationOutcome.Refused => AuditResults.Refused,
                AgentOperationOutcome.Unknown => AuditResults.Unknown,
                _ => AuditResults.Failed,
            },
            ErrorCode = isCode ? error : null,
            Detail = detail,
        };
    }

    /// <summary>The audit action of an agent operation kind; true when its target is a model id.</summary>
    internal static (string Action, bool ModelTarget) ActionOf(string kind) => kind switch
    {
        OperationKinds.Launch => (AuditActions.ModelStart, true),
        OperationKinds.Stop => (AuditActions.ModelStop, true),
        OperationKinds.Download or OperationKinds.DownloadCancel => (AuditActions.Download, false),
        OperationKinds.FolderDelete => (AuditActions.FolderDelete, false),
        OperationKinds.ServiceStart or OperationKinds.ServiceStop or OperationKinds.ServiceRestart or OperationKinds.ServiceAutostart
            or OperationKinds.WhitelistAdd or OperationKinds.WhitelistUpdate or OperationKinds.WhitelistRemove
            or OperationKinds.ContainerStart or OperationKinds.ContainerStop or OperationKinds.ContainerRestart or OperationKinds.ContainerAutostart
            or OperationKinds.BackupMountSet or OperationKinds.BackupMountRemove => (AuditActions.HostService, false),
        OperationKinds.PkiRotationStart or OperationKinds.PkiRotationCert or OperationKinds.PkiRotationCommit or OperationKinds.PkiRotationAbort
            => (AuditActions.NodeRotateCert, false),
        OperationKinds.PkiTrustSet => (AuditActions.PkiCaRotation, false),
        _ => ("agent." + kind, false),
    };

    [GeneratedRegex("^[a-z][a-z0-9_]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodePattern();
}
