using System.Text.Json;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;

namespace MirbadLLM.Agent.HostServices;

/// <summary>What the node shows now for a host-service mutation's target (design §4.4 item 8).</summary>
/// <param name="Matches">True when the current state already is the mutation's desired state (always false for restarts and mount PUTs).</param>
/// <param name="Current">The current target as the route's result type (<see cref="HostUnit"/>, <see cref="HostContainerInfo"/>, <see cref="BackupMountInfo"/>); null when it does not exist.</param>
/// <param name="Description">Plain-words observed state for the operator, e.g. <c>active (running), enabled</c>.</param>
public sealed record HostObservation(bool Matches, JsonElement? Current, string Description);

/// <summary>
/// Executes host-service mutations as operations (design §4.3 host services, §4.4 items 1–2 and 8): envelope and fencing
/// checks, durable acceptance through <see cref="IOperationLog"/>, the <see cref="OperationStages.Dispatched"/> stage
/// persisted immediately before hostd or Docker is called, then a terminal record: Succeeded with the route's result,
/// Failed with the agent error, or <see cref="OperationState.Unknown"/> when the call's outcome is ambiguous (timeout,
/// connection lost after sending). Every action is synchronous and bounded by hostd's own 60 s action timeout.
/// <para>The hostd/Docker call is never bound to the request's cancellation: once dispatched it runs to its own timeout,
/// so a master that gives up never leaves the action half-observed by the agent.</para>
/// <para><see cref="ObserveAsync"/> and <see cref="ExecuteAcceptedAsync"/> are the reconciliation hooks used by
/// <see cref="HostServiceRecoveryHandler"/> (and through it by the agent's startup reconciliation, MN-025).</para>
/// <para>The operator texts name the node's service manager (<see cref="AgentHostServicesOptions.Manager"/>,
/// <see cref="HostServiceWording"/>, MN-239 item 1): hostd and systemd on Linux, the Service Control Manager and a
/// Windows start type on the PC. The codes and the stored shape are the same on both.</para>
/// </summary>
public sealed class HostServiceOperations(
    IHostdClient hostd,
    HostContainers? containers,
    IOperationLog log,
    IFencingState fencing,
    AgentHostServicesOptions options,
    TimeProvider time,
    ILogger<HostServiceOperations> logger,
    IEventBus? events = null)
{
    /// <summary>How long the agent waits to read the observed state for an Unknown outcome.</summary>
    public static readonly TimeSpan ObserveTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Handles one mutating request: <paramref name="masterHeader"/> is the raw <c>MirbadLLM-Master</c> value and
    /// <paramref name="body"/> the request body as received (secrets included; they are redacted before it is stored).
    /// </summary>
    public async Task<HostCallResult> SubmitAsync(HostMutation mutation, string? masterHeader, JsonElement body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (Unsupported(mutation.Kind) is { } unsupported)
        {
            return HostCallResult.Fail(unsupported);
        }

        if (!MasterTriple.TryParse(masterHeader, out var master))
        {
            return HostCallResult.Fail(ErrorCodes.InvalidRequest, $"Missing or malformed {Protocol.MasterHeader} header.");
        }

        if (Validate(mutation) is { } invalid)
        {
            return HostCallResult.Fail(ErrorCodes.InvalidRequest, invalid);
        }

        var check = await fencing.CheckAsync(master, ct).ConfigureAwait(false);
        switch (check.Decision)
        {
            case FencingDecision.StaleMaster:
                return HostCallResult.Fail(ErrorCodes.StaleMaster, "A master with a higher epoch already drives this node.");
            case FencingDecision.CompetingMaster:
                return HostCallResult.Fail(ErrorCodes.CompetingMaster, "Another master instance already drives this node at this epoch.");
        }

        OperationAcceptance acceptance;
        try
        {
            acceptance = await log.AcceptAsync(new OperationSubmission
            {
                OperationId = mutation.Request.OperationId,
                Kind = mutation.Kind,
                Target = mutation.Target,
                IssuedAt = mutation.Request.IssuedAt,
                ExpiresAt = mutation.Request.ExpiresAt,
                Master = master,
                Payload = HostMutation.Redact(body),
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HostServicesLog.StateUnavailable(logger, mutation.Request.OperationId, "accept", ex);
            return StateUnavailable();
        }

        switch (acceptance.Outcome)
        {
            case OperationAcceptOutcome.Duplicate when acceptance.Status is { } stored:
                return ResultFor(stored);
            case OperationAcceptOutcome.Conflict:
                return HostCallResult.Fail(ErrorCodes.OperationIdConflict,
                    $"Operation id {mutation.Request.OperationId} was already used for another action, target or payload.");
            case OperationAcceptOutcome.Expired:
                return HostCallResult.Fail(ErrorCodes.OperationExpired,
                    $"Operation {mutation.Request.OperationId} expired at {mutation.Request.ExpiresAt:O}; nothing was done.");
            case OperationAcceptOutcome.IssuedInFuture:
                return HostCallResult.Fail(ErrorCodes.ClockSkew, "The operation's issuedAt is more than 5 minutes ahead of the agent clock.");
            case OperationAcceptOutcome.Accepted:
                break;
            default:
                return HostCallResult.Fail(ErrorCodes.InternalError, "The operation log returned no status.");
        }

        // Not bound to the request: once dispatched the action runs to its own timeout (see the class remarks).
        var final = await ExecuteAcceptedAsync(mutation, master, CancellationToken.None).ConfigureAwait(false);
        return final is null ? StateUnavailable() : ResultFor(final);
    }

    /// <summary>
    /// Runs an operation the log already holds (accepted, or recovered with an unmatched desired state): read-only checks,
    /// fencing re-check, the durable <see cref="OperationStages.Dispatched"/> stage, the hostd/Docker call and the terminal
    /// record. Returns the final status, or null when the log could not record a step (the action is then not sent, or its
    /// terminal record is missing and recovery decides).
    /// </summary>
    public async Task<OperationStatus?> ExecuteAcceptedAsync(HostMutation mutation, MasterTriple master, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(master);
        string id = mutation.Request.OperationId;

        if (Unsupported(mutation.Kind) is { } unsupported)
        {
            return await CompleteAsync(mutation, OperationState.Failed, unsupported, null).ConfigureAwait(false);
        }

        // Read-only checks first: a denied or missing container is never dispatched.
        ContainerDetails? details = null;
        if (mutation.IsContainer)
        {
            try
            {
                details = await containers!.InspectAllowedAsync(mutation.Target, ct).ConfigureAwait(false);
            }
            catch (HostServiceDeniedException ex)
            {
                return await CompleteAsync(mutation, OperationState.Failed, HostServiceErrors.Error(ErrorCodes.Denied, ex.Message), null).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Only a read failed: nothing was sent, so this is never ambiguous.
                var failure = HostServiceErrors.FromDocker(ex, "read", mutation.Target);
                var error = failure.Ambiguous
                    ? HostServiceErrors.Error(failure.Error.Code, $"Docker did not answer while reading '{mutation.Target}'; nothing was sent.")
                    : failure.Error;
                return await CompleteAsync(mutation, OperationState.Failed, error, null).ConfigureAwait(false);
            }

            if (details is null)
            {
                return await CompleteAsync(mutation, OperationState.Failed,
                    HostServiceErrors.Error(ErrorCodes.NotFound, $"No container named '{mutation.Target}'."), null).ConfigureAwait(false);
            }
        }

        // Re-check fencing right before the destructive step (design §4.2).
        if (!fencing.IsCurrent(master))
        {
            return await CompleteAsync(mutation, OperationState.Failed,
                HostServiceErrors.Error(ErrorCodes.Superseded, "A newer master took over this node; the action was not sent."), null).ConfigureAwait(false);
        }

        try
        {
            await log.RecordStageAsync(id, new OperationStageUpdate { Stage = OperationStages.Dispatched, State = OperationState.Running }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HostServicesLog.StateUnavailable(logger, id, "dispatched", ex);
            return null;
        }

        JsonElement? result;
        try
        {
            result = await ApplyAsync(mutation, details, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || HostdClient.IsTimeout(ex))
        {
            var failure = Classify(mutation, ex);
            if (!failure.Ambiguous)
            {
                return await CompleteAsync(mutation, OperationState.Failed, failure.Error, null).ConfigureAwait(false);
            }

            var observed = await TryObserveAsync(mutation).ConfigureAwait(false);
            var error = failure.Error with
            {
                // One code for every Unknown outcome (D42); the cause (timeout, lost connection) stays in the message.
                Code = ErrorCodes.OutcomeUnknown,
                Message = observed is null ? failure.Error.Message : $"{failure.Error.Message} Observed: {observed.Description}.",
            };
            return await CompleteAsync(mutation, OperationState.Unknown, error, observed?.Current).ConfigureAwait(false);
        }

        return await CompleteAsync(mutation, OperationState.Succeeded, null, result).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the target's current state and whether it already is the desired state of <paramref name="mutation"/>
    /// (design §4.4 item 8). Throws what hostd or Docker throw.
    /// </summary>
    public async Task<HostObservation> ObserveAsync(HostMutation mutation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (mutation.IsContainer)
        {
            var details = await containers!.InspectAllowedAsync(mutation.Target, ct).ConfigureAwait(false);
            if (details is null)
            {
                return new HostObservation(false, null, "no such container");
            }

            var info = await containers.ToInfoAsync(details, status: null, ct).ConfigureAwait(false);
            bool running = HostContainers.IsRunning(details.Status);
            bool matches = mutation.Request switch
            {
                HostContainerAutostartRequest a => HostContainers.NormalizePolicy(details.RestartPolicy) == a.RestartPolicy,
                _ => mutation.Kind switch
                {
                    OperationKinds.ContainerStart => running,
                    OperationKinds.ContainerStop => !running,
                    _ => false,
                },
            };
            return new HostObservation(matches, AgentJson.ToElement(info),
                $"{HostContainers.StatusText(details)}, restart policy {HostContainers.NormalizePolicy(details.RestartPolicy)}");
        }

        if (mutation.IsBackupMount)
        {
            var mount = await hostd.GetBackupMountAsync(ct).ConfigureAwait(false);
            bool matches = mutation.Kind == OperationKinds.BackupMountRemove && !mount.Configured && !mount.Mounted;
            string description = mount.Mounted
                ? $"mounted from {mount.MountedSource ?? mount.Source}"
                : mount.Configured ? "configured, not mounted" : "not configured";
            return new HostObservation(matches, AgentJson.ToElement(mount), description);
        }

        var units = await hostd.ListAsync(ct).ConfigureAwait(false);
        var unit = FindUnit(mutation, units);
        bool unitMatches = unit is null
            ? mutation.Kind == OperationKinds.WhitelistRemove
            : mutation.Request switch
            {
                HostServiceAutostartRequest a => a.Enabled
                    ? unit.UnitFileState is "enabled" or "enabled-runtime"
                    : unit.UnitFileState == "disabled",
                HostWhitelistUpdateRequest u => (u.DisplayName is null || string.Equals(unit.DisplayName, u.DisplayName.Trim(), StringComparison.Ordinal))
                    && (u.Protected is null || unit.Protected == u.Protected),
                _ => mutation.Kind switch
                {
                    OperationKinds.ServiceStart => unit.ActiveState is "active" or "activating" or "reloading",
                    OperationKinds.ServiceStop => unit.ActiveState is "inactive" or "failed" or "deactivating",
                    OperationKinds.WhitelistAdd => true,
                    _ => false,
                },
            };
        return new HostObservation(unitMatches, unit is null ? null : AgentJson.ToElement(unit),
            unit is null ? "not in the whitelist" : $"{unit.ActiveState} ({unit.SubState}), {HostServiceWording.Autostart(options.Manager, unit.UnitFileState)}");
    }

    /// <summary>The agent error when the node does not offer the kind's feature (501 <c>not_supported</c>); null when it does.</summary>
    public AgentError? Unsupported(string kind)
    {
        bool supported = HostMutation.IsUnitKind(kind) ? options.Systemd
            : HostMutation.IsContainerKind(kind) ? options.Containers && containers is not null
            : HostMutation.IsBackupMountKind(kind) && options.BackupMount;
        return supported ? null : HostServiceErrors.Error(ErrorCodes.NotSupported, $"This node's agent does not support '{kind}'.");
    }

    /// <summary>Maps a stored status to the route's answer: Succeeded → 200; in progress → 202; Failed/Unknown → its error.</summary>
    public static HostCallResult ResultFor(OperationStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.State switch
        {
            OperationState.Succeeded => HostCallResult.Ok(status),
            OperationState.Failed or OperationState.Cancelled => HostCallResult.Fail(status.Error
                ?? HostServiceErrors.Error(ErrorCodes.InternalError, "The operation failed.")),
            OperationState.Unknown => HostCallResult.Fail((status.Error
                ?? HostServiceErrors.Error(ErrorCodes.OutcomeUnknown, HostServiceErrors.OutcomeUnknownHint)) with { Code = ErrorCodes.OutcomeUnknown }),
            _ => new HostCallResult(202, status),
        };
    }

    /// <summary>Records the terminal state, logs it and publishes the operation event. Null when the log failed.</summary>
    internal async Task<OperationStatus?> CompleteAsync(HostMutation mutation, OperationState state, AgentError? error, JsonElement? result)
    {
        string id = mutation.Request.OperationId;
        OperationStatus status;
        try
        {
            status = await log.CompleteAsync(id, state, error, result, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HostServicesLog.StateUnavailable(logger, id, "completed", ex);
            return null;
        }

        switch (state)
        {
            case OperationState.Failed:
                HostServicesLog.Failed(logger, id, mutation.Kind, mutation.Target, error?.Code ?? "", error?.Message ?? "");
                break;
            case OperationState.Unknown:
                HostServicesLog.Unknown(logger, id, mutation.Kind, mutation.Target, error?.Code ?? "", error?.Message ?? "");
                break;
            default:
                HostServicesLog.Completed(logger, id, mutation.Kind, mutation.Target, state);
                break;
        }

        events?.Publish(new OperationEvent { Operation = status, At = time.GetUtcNow() });
        return status;
    }

    /// <summary>Reads the observed state for an Unknown outcome, bounded by <see cref="ObserveTimeout"/>; null when it cannot be read.</summary>
    internal async Task<HostObservation?> TryObserveAsync(HostMutation mutation)
    {
        using var cts = new CancellationTokenSource(ObserveTimeout);
        try
        {
            return await ObserveAsync(mutation, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            HostServicesLog.ProbeFailed(logger, ex.Message);
            return null;
        }
    }

    private async Task<JsonElement?> ApplyAsync(HostMutation mutation, ContainerDetails? details, CancellationToken ct)
    {
        switch (mutation.Request)
        {
            case HostServiceActionRequest action:
                return AgentJson.ToElement(await hostd.RunAsync(mutation.Target, ActionOf(mutation.Kind), action.Confirm, ct).ConfigureAwait(false));
            case HostServiceAutostartRequest autostart:
                return AgentJson.ToElement(await hostd.SetAutostartAsync(mutation.Target, autostart.Enabled, autostart.Confirm, ct).ConfigureAwait(false));
            case HostWhitelistAddRequest add:
                return AgentJson.ToElement(await hostd.AddToWhitelistAsync(add.Unit.Trim(), add.Scope, add.DisplayName, add.Protected, add.Confirm?.Trim(), ct)
                    .ConfigureAwait(false));
            case HostWhitelistUpdateRequest update:
                return AgentJson.ToElement(await hostd.UpdateWhitelistAsync(mutation.Target, update.DisplayName, update.Protected, update.Confirm?.Trim(), ct)
                    .ConfigureAwait(false));
            case HostWhitelistRemoveRequest:
                await hostd.RemoveFromWhitelistAsync(mutation.Target, ct).ConfigureAwait(false);
                return null;
            case BackupMountSetRequest set:
                return AgentJson.ToElement(await hostd.SetBackupMountAsync(set.Settings, ct).ConfigureAwait(false));
            case BackupMountRemoveRequest:
                return AgentJson.ToElement(await hostd.RemoveBackupMountAsync(ct).ConfigureAwait(false));
            case HostContainerActionRequest containerAction:
                switch (mutation.Kind)
                {
                    case OperationKinds.ContainerStart:
                        await containers!.StartAsync(details!, ct).ConfigureAwait(false);
                        break;
                    case OperationKinds.ContainerStop:
                        await containers!.StopAsync(details!, containerAction.TimeoutSeconds, ct).ConfigureAwait(false);
                        break;
                    default:
                        await containers!.RestartAsync(details!, containerAction.TimeoutSeconds, ct).ConfigureAwait(false);
                        break;
                }

                return await ReadContainerAfterAsync(mutation.Target, ct).ConfigureAwait(false);
            case HostContainerAutostartRequest policy:
                if (HostContainers.NormalizePolicy(details!.RestartPolicy) != policy.RestartPolicy)
                {
                    await containers!.SetRestartPolicyAsync(details, policy.RestartPolicy, ct).ConfigureAwait(false);
                }

                return await ReadContainerAfterAsync(mutation.Target, ct).ConfigureAwait(false);
            default:
                throw new InvalidOperationException($"'{mutation.Kind}' is not a host-service operation.");
        }
    }

    /// <summary>The container after a successful action; a failing read does not turn the action into a failure.</summary>
    private async Task<JsonElement?> ReadContainerAfterAsync(string name, CancellationToken ct)
    {
        try
        {
            var info = await containers!.GetAsync(name, ct).ConfigureAwait(false);
            return info is null ? null : AgentJson.ToElement(info);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or HostServiceException)
        {
            HostServicesLog.ProbeFailed(logger, ex.Message);
            return null;
        }
    }

    private HostActionFailure Classify(HostMutation mutation, Exception ex) =>
        mutation.IsContainer
            ? ex is HostServiceDeniedException
                ? new(HostServiceErrors.Error(ErrorCodes.Denied, ex.Message), false)
                : HostServiceErrors.FromDocker(ex, Verb(mutation.Kind), mutation.Target)
            : HostServiceErrors.FromHostd(ex, options.Manager);

    private static HostUnit? FindUnit(HostMutation mutation, IReadOnlyList<HostUnit> units)
    {
        if (mutation.Request is HostWhitelistAddRequest add)
        {
            string unit = add.Unit.Trim();
            return units.FirstOrDefault(u =>
                (string.Equals(u.Unit, unit, StringComparison.Ordinal) || string.Equals(u.Unit, unit + ".service", StringComparison.Ordinal))
                && string.Equals(u.Scope, add.Scope, StringComparison.Ordinal));
        }

        return units.FirstOrDefault(u => string.Equals(u.Name, mutation.Target, StringComparison.Ordinal));
    }

    private static string? Validate(HostMutation mutation)
    {
        var request = mutation.Request;
        if (string.IsNullOrWhiteSpace(request.OperationId))
        {
            return "operationId is required.";
        }

        if (request.ExpiresAt <= request.IssuedAt)
        {
            return "expiresAt must be after issuedAt.";
        }

        if (string.IsNullOrWhiteSpace(mutation.Target))
        {
            return "Name the service or container.";
        }

        return request switch
        {
            HostWhitelistAddRequest add when add.Scope is not ("system" or "user") => "scope must be 'system' or 'user'.",
            HostContainerAutostartRequest policy when !HostContainers.RestartPolicies.Contains(policy.RestartPolicy) =>
                $"restartPolicy must be one of {string.Join(", ", HostContainers.RestartPolicies)}.",
            HostContainerActionRequest action when action.TimeoutSeconds is < 0 or > 600 => "timeoutSeconds must be between 0 and 600.",
            BackupMountSetRequest set when set.Settings is null => "settings are required.",
            _ => null,
        };
    }

    private static HostServiceAction ActionOf(string kind) => kind switch
    {
        OperationKinds.ServiceStart => HostServiceAction.Start,
        OperationKinds.ServiceStop => HostServiceAction.Stop,
        _ => HostServiceAction.Restart,
    };

    private static string Verb(string kind) => kind switch
    {
        OperationKinds.ContainerStart => "start",
        OperationKinds.ContainerStop => "stop",
        OperationKinds.ContainerRestart => "restart",
        OperationKinds.ContainerAutostart => "change the restart policy of",
        _ => kind,
    };

    private static HostCallResult StateUnavailable() =>
        HostCallResult.Fail(ErrorCodes.StateUnavailable,
            "The agent could not durably record the operation. Read the operation (GET /operations/{id}) before acting again.");
}
