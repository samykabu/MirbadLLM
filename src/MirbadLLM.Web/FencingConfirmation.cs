using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Web;

/// <summary>A refused takeover or continuation: the <see cref="ErrorCodes"/> value, the text and the HTTP status the API answers.</summary>
/// <param name="Code">An <see cref="ErrorCodes"/> value.</param>
/// <param name="Message">The refusal text (also the audit line's detail).</param>
/// <param name="Status">The status <c>/api/fencing</c> answers.</param>
/// <param name="Outcome">The session-check outcome when authorization refused it; null for a confirmation refusal.</param>
public sealed record FencingRefusal(string Code, string Message, int Status, AdminReauthOutcome? Outcome = null);

/// <summary>
/// The checks before a takeover or a continuation (design §4.2 step 3, MN-115/MN-121), shared by
/// <c>POST /api/fencing/takeover[/continue]</c> and the takeover banner so both refuse alike and write the same
/// <c>refused</c> audit line (W8 review MN-128): the typed confirmation first,
/// then the authenticated portal session. The coordinator writes
/// every other line.
/// </summary>
public static class FencingConfirmation
{
    /// <summary>The re-check refusal while admin login is disabled.</summary>
    public const string LoginDisabledMessage =
        "Admin login is disabled (no MirbadLLM:AdminPassword), so a signed-in session is required. Set an admin password, " +
        "or create the takeover flag file {DataPath}/takeover after confirming the previous master is stopped.";

    /// <summary>The takeover's typed confirmation (<see cref="TakeoverConfirmations.PreviousMasterStopped"/>, any case); null when it matches.</summary>
    public static FencingRefusal? CheckTakeoverConfirmation(string? typed) =>
        string.Equals(typed?.Trim(), TakeoverConfirmations.PreviousMasterStopped, StringComparison.OrdinalIgnoreCase)
            ? null
            : Confirmation($"Confirm the takeover by typing \"{TakeoverConfirmations.PreviousMasterStopped}\".");

    /// <summary>The continuation's typed confirmation (the node id, exact); null when it matches.</summary>
    public static FencingRefusal? CheckContinueConfirmation(string? typed, string nodeId) =>
        string.Equals(typed?.Trim(), nodeId, StringComparison.Ordinal)
            ? null
            : Confirmation($"Confirm the continuation by typing the node id \"{nodeId}\".");

    /// <summary>The authenticated-session check (403 <c>reauthentication_failed</c>); null when authorized.</summary>
    public static FencingRefusal? Reauthenticate(IAdminReauthenticator? reauth, System.Security.Claims.ClaimsPrincipal? user)
    {
        AdminReauthOutcome outcome = reauth?.Verify(user) ?? AdminReauthOutcome.LoginDisabled;
        return outcome switch
        {
            AdminReauthOutcome.Verified => null,
            AdminReauthOutcome.RateLimited => new FencingRefusal(ErrorCodes.ReauthenticationFailed,
                "Too many password attempts. Try again in a minute.", StatusCodes.Status429TooManyRequests, outcome),
            AdminReauthOutcome.LoginDisabled => new FencingRefusal(ErrorCodes.ReauthenticationFailed, LoginDisabledMessage,
                ErrorCodes.ApiStatus(ErrorCodes.ReauthenticationFailed), outcome),
            _ => new FencingRefusal(ErrorCodes.ReauthenticationFailed, "Sign in to the portal to perform this action.",
                ErrorCodes.ApiStatus(ErrorCodes.ReauthenticationFailed), outcome),
        };
    }

    /// <summary>Writes the <c>refused</c> audit line of <paramref name="refusal"/>; never fails (IAuditLog never fails the action it records).</summary>
    public static async Task AuditRefusedAsync(
        IAuditLog? audit, TimeProvider? time, string actor, string action, string? nodeId, string? target, FencingRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        try
        {
            await (audit ?? NullAuditLog.Instance).WriteAsync(new AuditRecord
            {
                Time = (time ?? TimeProvider.System).GetUtcNow(),
                Actor = actor,
                Action = action,
                NodeId = nodeId,
                Target = target,
                Result = AuditResults.Refused,
                ErrorCode = refusal.Code,
                Detail = refusal.Message,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // IAuditLog never fails the action it records.
        }
    }

    private static FencingRefusal Confirmation(string message) =>
        new(ErrorCodes.ConfirmationRequired, message, ErrorCodes.ApiStatus(ErrorCodes.ConfirmationRequired));
}
