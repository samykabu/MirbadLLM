using System.Text.Json;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Core;
using MirbadLLM.Pki;

namespace MirbadLLM.Agent.Pki;

/// <summary>
/// Settles PKI operations a restart left non-terminal (design §4.4 item 5, "rotation pending → keep"), for the startup
/// reconciliation (MN-025) through <see cref="IOperationRecoveryHandler"/>. The rotation material itself was already
/// recovered when <see cref="RotatingAgentCredentials"/> was built (pending key and certificate kept, a cut-short commit
/// finished); here only the operation records are closed, from what the rotation files show: a step that took effect is
/// Succeeded with its result, one that did not is Failed (nothing changed; the master retries the step, which is
/// idempotent per rotation id). Nothing is replayed.
/// </summary>
public sealed class PkiRecoveryHandler(
    RotatingAgentCredentials credentials,
    AgentPkiService pki,
    IOperationLog log,
    ILogger<PkiRecoveryHandler> logger) : IOperationRecoveryHandler
{
    /// <inheritdoc/>
    public IReadOnlyList<string> Kinds => AgentPkiService.Kinds;

    /// <inheritdoc/>
    public async Task RecoverAsync(RecoverableOperation operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        OperationStatus status = operation.Status;
        AgentRotationEntry? entry = credentials.Store.Document.Find(status.Target);
        JsonElement? result = status.Kind switch
        {
            OperationKinds.PkiRotationStart when entry is not null && entry.Stage != PkiRotationStage.Aborted =>
                AgentJson.ToElement(new PkiRotationCsr(entry.RotationId, entry.CsrPem)),
            OperationKinds.PkiRotationCert when entry?.Stage is PkiRotationStage.CertInstalled or PkiRotationStage.Committed =>
                AgentJson.ToElement(pki.StatusOf(entry)),
            OperationKinds.PkiRotationCommit when entry?.Stage == PkiRotationStage.Committed =>
                AgentJson.ToElement(pki.StatusOf(entry)),
            OperationKinds.PkiRotationAbort when entry?.Stage == PkiRotationStage.Aborted =>
                AgentJson.ToElement(pki.StatusOf(entry)),
            OperationKinds.PkiTrustSet when TrustApplied(operation.Payload) =>
                AgentJson.ToElement(pki.GetTrust()),
            _ => null,
        };

        OperationState outcome = result is null ? OperationState.Failed : OperationState.Succeeded;
        AgentError? error = result is null
            ? AgentErrorResults.Create(ErrorCodes.InternalError, "The agent restarted before this PKI step took effect; nothing was changed. Retry the step.")
            : null;
        await log.CompleteAsync(status.Id, outcome, error, result, ct).ConfigureAwait(false);
        AgentPkiLog.OperationRecovered(logger, status.Id, status.Kind, status.State, outcome);
    }

    private bool TrustApplied(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body)
        {
            return false;
        }

        try
        {
            PkiTrustSetRequest? request = body.Deserialize<PkiTrustSetRequest>(AgentJson.Options);
            if (request?.Anchors is not { Count: > 0 } anchors)
            {
                return false;
            }

            using TrustAnchorSet wanted = TrustAnchorSet.FromPem(anchors);
            return wanted.Anchors.Count == credentials.TrustAnchors.Count &&
                   wanted.SpkiSha256s.All(pin => credentials.TrustAnchors.Any(a => Spki.Matches(a, pin)));
        }
        catch (Exception ex) when (ex is JsonException or PkiException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}
