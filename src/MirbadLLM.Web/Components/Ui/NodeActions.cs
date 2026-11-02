using MirbadLLM.Web.Components.Pages;

namespace MirbadLLM.Web.Components.Ui;

/// <summary>The node actions behind the Nodes page's "⋯" menu and the node detail header (design §12.1).</summary>
public enum NodeAction
{
    /// <summary>Approve a node that enrolled (pins its pending key).</summary>
    Approve,

    /// <summary>Reject a node that is not approved (deletes its record and pending key).</summary>
    Reject,

    /// <summary>Set the draining flag.</summary>
    Drain,

    /// <summary>Clear the draining flag.</summary>
    Undrain,

    /// <summary>Rotate the agent certificate (design §10.2).</summary>
    RotateCertificate,

    /// <summary>Mint a rekey token and show the re-pair command (MN-114).</summary>
    Rekey,

    /// <summary>Revoke the node (typed id + signed-in session).</summary>
    Revoke,
}

/// <summary>An action the operator picked for a node; <see cref="NodeActionDialogs"/> confirms and runs it.</summary>
/// <param name="Action">What to do.</param>
/// <param name="Row">The node as the page showed it.</param>
public sealed record NodeActionRequest(NodeAction Action, Nodes.NodeRow Row);

/// <summary>A finished node action.</summary>
/// <param name="Action">What was done.</param>
/// <param name="NodeId">The node.</param>
/// <param name="Message">One sentence for the page's status line.</param>
/// <param name="Removed">True when the node is gone from the list (rejected or revoked).</param>
public sealed record NodeActionResult(NodeAction Action, string NodeId, string? Message, bool Removed);

/// <summary>Which actions a node offers, from its record and state (one rule for the row menu and the detail header).</summary>
public static class NodeActionRules
{
    /// <summary>True when the node enrolled and waits for approval.</summary>
    public static bool CanApprove(Nodes.NodeRow row) =>
        row.Record is { Approved: false, Revoked: false, PendingCertSpkiSha256: not null };

    /// <summary>True for a node that is not approved (pending, or only a join token was made for it).</summary>
    public static bool CanReject(Nodes.NodeRow row) => row.Record is { Approved: false, Revoked: false };

    /// <summary>Drain and undrain need an approved node.</summary>
    public static bool CanDrain(Nodes.NodeRow row) => row.Record is { Approved: true, Revoked: false };

    /// <summary>A rotation needs an approved node the master can reach.</summary>
    public static bool CanRotate(Nodes.NodeRow row) =>
        row.Record is { Approved: true, Revoked: false, CertSpkiSha256: not null } && row.IsServing;

    /// <summary>Re-pair: the registry's own eligibility rule (an enrolled, non-revoked node, approved or still pending).</summary>
    public static bool CanRekey(Nodes.NodeRow row) =>
        row.Record is { } record && MirbadLLM.Orchestration.Nodes.JsonNodeRegistry.RekeyRefusal(record) is null;

    /// <summary>Revoke: any approved node (a pending one is rejected instead).</summary>
    public static bool CanRevoke(Nodes.NodeRow row) => row.Record is { Approved: true, Revoked: false };
}
