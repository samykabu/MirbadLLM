using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;
using MirbadLLM.Orchestration.Agents;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>
/// A host-service action whose outcome the agent could not verify (design §4.4 item 8, W2 review D42): hostd or Docker
/// timed out or the connection was lost after the action was dispatched, or the agent restarted after dispatching a
/// restart or a backup-mount PUT. The agent answered 504 <c>outcome_unknown</c> (a same-id retry answers it again) and the
/// master keeps the operation reference open. Nothing is replayed: a new attempt is an explicit new action.
/// </summary>
public sealed class HostServiceOutcomeUnknownException : HostServiceException
{
    /// <summary>The text the UI shows (D42).</summary>
    public const string Hint = "Outcome unknown — check the service and run the action again if needed.";

    public HostServiceOutcomeUnknownException()
        : base(Hint)
    {
    }

    public HostServiceOutcomeUnknownException(string message)
        : base(message)
    {
    }

    public HostServiceOutcomeUnknownException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The operation whose outcome is unknown (its reference stays open on the master).</summary>
    public string? OperationId { get; init; }

    /// <summary>The node it ran on.</summary>
    public string? NodeId { get; init; }

    /// <summary>
    /// The UI message: <see cref="Hint"/> followed by the agent's explanation (cause and observed state), without
    /// repeating the hint when the agent already wrote it.
    /// </summary>
    public static string Compose(string? agentMessage)
    {
        string detail = (agentMessage ?? "").Replace(Hint, "", StringComparison.Ordinal).Trim();
        return detail.Length == 0 ? Hint : $"{Hint} {detail}";
    }
}

/// <summary>
/// Maps agent failures of the remote master-side implementations (MN-032) onto the exceptions today's callers already
/// handle, so the Services page, the backup destination and the download API behave as with the local sockets.
/// </summary>
internal static class RemoteAgentErrors
{
    /// <summary>
    /// A host-service failure (hostd units, containers, backup mount) → the <see cref="HostServiceException"/> family:
    /// <c>outcome_unknown</c> → <see cref="HostServiceOutcomeUnknownException"/>; <c>hostd_unavailable</c> /
    /// <c>not_supported</c> and an unreachable node on a read → <see cref="HostAgentUnavailableException"/>;
    /// <c>not_found</c> → <see cref="HostServiceNotFoundException"/> (as hostd's 404 today); the rest through
    /// <see cref="AgentErrorMapper"/>. An unreachable node during a mutation is an unknown outcome: the request may have
    /// reached the agent, and the reference stays open.
    /// </summary>
    public static HostServiceException ToHostServiceException(Exception exception, string nodeId, string? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        switch (exception)
        {
            case HostServiceException hostService:
                return hostService;
            case AgentException agent when agent.Code == ErrorCodes.OutcomeUnknown:
                return new HostServiceOutcomeUnknownException(HostServiceOutcomeUnknownException.Compose(AgentErrorMapper.WithNode(agent.Error.Message, nodeId)), agent)
                {
                    OperationId = operationId,
                    NodeId = nodeId,
                };
            case AgentException agent when agent.Code is ErrorCodes.HostdUnavailable or ErrorCodes.NotSupported:
                return new HostAgentUnavailableException(agent.Error.Message, agent);
            case AgentException agent when agent.Code == ErrorCodes.NotFound:
                return new HostServiceNotFoundException(agent.Error.Message, agent);
            case AgentException agent:
                return AgentErrorMapper.ToMasterException(agent) is HostServiceException mapped
                    ? mapped
                    : new HostServiceException(AgentErrorMapper.WithNode(agent.Error.Message, nodeId), agent);
            case AgentUnreachableException unreachable when operationId is not null:
                return new HostServiceOutcomeUnknownException(HostServiceOutcomeUnknownException.Compose(unreachable.Message), unreachable)
                {
                    OperationId = operationId,
                    NodeId = nodeId,
                };
            case AgentUnreachableException unreachable:
                return new HostAgentUnavailableException(unreachable.Message, unreachable);
            case KeyNotFoundException missing:
                return new HostAgentUnavailableException($"Node {nodeId} has no agent client: {missing.Message}", missing);
            default:
                return new HostServiceException(exception.Message, exception);
        }
    }

    /// <summary>True for the failures <see cref="ToHostServiceException"/> translates (never cancellation).</summary>
    public static bool IsAgentFailure(Exception exception) =>
        exception is AgentException or AgentUnreachableException or KeyNotFoundException;

    /// <summary>
    /// A storage failure (folders, downloads) → today's exceptions: request codes → <see cref="ArgumentException"/>,
    /// conflicts (<c>already_running</c>, <c>folder_exists</c>, <c>in_use</c>) and everything else →
    /// <see cref="InvalidOperationException"/> (<see cref="AgentErrorMapper"/>); an unreachable node, or Docker/hostd down
    /// on it → <see cref="NodeUnavailableException"/> (<see cref="NodeUnavailableReason.NotServing"/>, an
    /// <see cref="InvalidOperationException"/>) naming it, which the download and folder APIs answer 503 (D62, MN-047).
    /// </summary>
    public static Exception ToStorageException(Exception exception, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            // A Hugging Face refusal is today's HuggingFaceApiException (the download API answers 422), W3 review MN-094.
            AgentException agent when IsHuggingFaceRefusal(agent) => new HuggingFaceApiException(AgentErrorMapper.WithNode(agent.Error.Message, nodeId)),

            // A node dependency that is down is transient: 503 like a start or stop with Docker down (MN-047).
            AgentException agent when agent.Code is ErrorCodes.DockerUnavailable or ErrorCodes.HostdUnavailable =>
                new NodeUnavailableException(AgentErrorMapper.WithNode(agent.Error.Message, nodeId), agent)
                {
                    Reason = NodeUnavailableReason.NotServing,
                    NodeId = nodeId,
                },

            // timeout / outcome_unknown map to the host-service family in AgentErrorMapper; storage callers expect 409-style errors.
            AgentException agent => AgentErrorMapper.ToMasterException(agent) is HostServiceException
                ? new InvalidOperationException(AgentErrorMapper.WithNode(agent.Error.Message, nodeId), agent)
                : AgentErrorMapper.ToMasterException(agent),
            AgentUnreachableException unreachable => new NodeUnavailableException(unreachable.Message, unreachable)
            {
                Reason = NodeUnavailableReason.NotServing,
                NodeId = nodeId,
            },
            KeyNotFoundException missing => new InvalidOperationException($"Node {nodeId} has no agent client: {missing.Message}", missing),
            _ => exception,
        };
    }

    /// <summary>True for an <c>invalid_request</c> the agent marked as decided by the Hugging Face Hub.</summary>
    internal static bool IsHuggingFaceRefusal(AgentException agent) =>
        agent.Code == ErrorCodes.InvalidRequest
        && agent.Error.Details is { ValueKind: System.Text.Json.JsonValueKind.Object }
        && agent.Error.ReadDetails<UpstreamRefusalDetails>()?.Source == UpstreamRefusalDetails.HuggingFace;
}
