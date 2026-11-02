namespace MirbadLLM.Core;

/// <summary>Why a command for a model could not be sent to its node (<see cref="NodeUnavailableException"/>).</summary>
public enum NodeUnavailableReason
{
    /// <summary>The model has no placement.</summary>
    Unplaced,

    /// <summary>The model is placed on a node the registry does not hold, or holds as revoked.</summary>
    NodeNotRegistered,

    /// <summary>The node is not serving: Offline, Incompatible, pending approval or takeover, or not connected yet.</summary>
    NotServing,

    /// <summary>The node is draining: it takes no new starts.</summary>
    Draining,
}

/// <summary>
/// Thrown by the multi-node <see cref="IModelRuntime"/> when a model's node cannot take the command (design §3.5): nothing
/// was sent to any agent, or the agent refused this master before acting (<c>stale_master</c>/<c>competing_master</c>: the
/// node is Pending takeover, D52; MN-047). Derives from <see cref="InvalidOperationException"/> so callers that already handle that keep
/// working; the admin API answers 409 (<see cref="NodeUnavailableReason.Unplaced"/>, <see cref="NodeUnavailableReason.NodeNotRegistered"/>,
/// <see cref="NodeUnavailableReason.Draining"/>) or 503 (<see cref="NodeUnavailableReason.NotServing"/>) instead of 500
/// (W3 review MN-094). Never thrown with today's single-host wiring.
/// </summary>
public sealed class NodeUnavailableException : InvalidOperationException
{
    public NodeUnavailableException()
    {
    }

    public NodeUnavailableException(string message)
        : base(message)
    {
    }

    public NodeUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Why the command was not sent.</summary>
    public NodeUnavailableReason Reason { get; init; }

    /// <summary>The model's node, when it has one.</summary>
    public string? NodeId { get; init; }

    /// <summary>True when retrying later can succeed without operator action (<see cref="NodeUnavailableReason.NotServing"/>).</summary>
    public bool IsTransient => Reason == NodeUnavailableReason.NotServing;
}
