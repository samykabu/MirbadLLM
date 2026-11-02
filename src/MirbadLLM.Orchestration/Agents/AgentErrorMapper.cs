using System.Globalization;
using System.Net.Http;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Agents;

/// <summary>
/// Agent error → the exception today's master callers already handle (<see cref="ErrorMappings.MasterExceptions"/>, design
/// §4.8, plan §2 "exception mapping table"), and transport failure classification (design §7.2 "Error classification").
/// </summary>
public static class AgentErrorMapper
{
    /// <summary>
    /// The master-side exception for an agent error, with the agent's message (today's texts) plus the node: admission →
    /// <see cref="ModelMemoryAdmissionException"/>, start conflict → <see cref="ModelStartConflictException"/> (with
    /// <c>StartingModelId</c>), <c>not_found</c> → <see cref="KeyNotFoundException"/>, host-service codes →
    /// <see cref="HostServiceException"/> family, request/spec codes → <see cref="ArgumentException"/>, a fencing refusal
    /// (<c>stale_master</c>, <c>competing_master</c>) → <see cref="NodeUnavailableException"/> with
    /// <see cref="NodeUnavailableReason.NotServing"/> (the node is Pending takeover for this master, D52; 503 at the admin
    /// API, MN-047), everything else → <see cref="InvalidOperationException"/>. The agent exception is the inner exception.
    /// </summary>
    public static Exception ToMasterException(AgentException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string message = WithNode(exception.Error.Message, exception.NodeId);
        return exception.Code switch
        {
            ErrorCodes.AdmissionRefused => new ModelMemoryAdmissionException(message, exception),
            ErrorCodes.StartConflict => new ModelStartConflictException(message, exception)
            {
                StartingModelId = ReadStartingModel(exception.Error),
            },
            ErrorCodes.NotFound => new KeyNotFoundException(message, exception),
            ErrorCodes.StaleMaster or ErrorCodes.CompetingMaster => new NodeUnavailableException(message, exception)
            {
                Reason = NodeUnavailableReason.NotServing,
                NodeId = exception.NodeId,
            },
            ErrorCodes.ConfirmationRequired => new HostServiceConfirmationRequiredException(message, exception),
            ErrorCodes.Denied => new HostServiceDeniedException(message, exception),
            ErrorCodes.Conflict => new HostServiceConflictException(message, exception),
            ErrorCodes.HostdUnavailable or ErrorCodes.DockerUnavailable or ErrorCodes.Timeout or ErrorCodes.OutcomeUnknown =>
                new HostServiceException(message, exception),
            ErrorCodes.InvalidRequest or ErrorCodes.SpecRejected or ErrorCodes.GpuMissing or ErrorCodes.NoFilesMatch
                or ErrorCodes.ExecutableMissing or ErrorCodes.ExecutableUnverified or ErrorCodes.GgufInvalid
                or ErrorCodes.UnsupportedManifest => // P3 codes (MN-202, D116): refusals of the request, like spec_rejected
                new ArgumentException(message, exception),
            _ => new InvalidOperationException(message, exception),
        };
    }

    /// <summary>
    /// The agent's admission numbers behind a <see cref="ModelMemoryAdmissionException"/> (W8 review MN-128, for the
    /// "Start anyway?" dialog): need, free, available, reserve, released, held by others and, on a discrete node, the
    /// refusing GPU's UUID (<see cref="AdmissionRefusedDetails.Gpu"/>) or the host part (<see cref="AdmissionRefusedDetails.Host"/>).
    /// Null when <paramref name="exception"/> carries no agent admission refusal (e.g. a pre-P1 single-host refusal) or its
    /// details cannot be read.
    /// </summary>
    public static AdmissionRefusedDetails? AdmissionDetails(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AgentException { Code: ErrorCodes.AdmissionRefused, Error.Details: { } details })
            {
                try
                {
                    return System.Text.Json.JsonSerializer.Deserialize<AdmissionRefusedDetails>(details, AgentJson.Options);
                }
                catch (System.Text.Json.JsonException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Appends <c> (on node {id})</c> unless the message already names the node (the agent writes admission texts as
    /// today's text plus "on node {id}", design §4.8).
    /// </summary>
    public static string WithNode(string? message, string? nodeId)
    {
        string text = string.IsNullOrWhiteSpace(message) ? "Agent error." : message.TrimEnd();
        if (string.IsNullOrEmpty(nodeId) || text.Contains("on node " + nodeId, StringComparison.Ordinal))
        {
            return text;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{text} (on node {nodeId})");
    }

    /// <summary>
    /// Classifies a transport failure before any response (design §7.2): <see cref="HttpRequestError.SecureConnectionError"/>
    /// with no refusal by the master's validator during the attempt is "HTTP/2 not negotiated" (ALPN or protocol; the
    /// validator either accepted the agent or never ran because the agent's ALPN refused h2); a refusal by the validator is
    /// a certificate problem; anything else (including an agent that refuses the master's certificate, which TLS 1.3
    /// shows as an incomplete HTTP/2 handshake) is "unreachable".
    /// </summary>
    internal static AgentUnreachableException Classify(string nodeId, HttpRequestException exception, ValidationTracker tracker, long rejectionsBefore)
    {
        if (exception.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            if (tracker.Rejected != rejectionsBefore && tracker.LastRejection is { } rejection)
            {
                return new AgentUnreachableException(
                    nodeId,
                    $"The agent of node {nodeId} presented a certificate the master refused ({rejection.Failure}: {rejection.Detail}).",
                    exception);
            }

            return new AgentUnreachableException(
                nodeId,
                $"HTTP/2 not negotiated with the agent of node {nodeId} (no downgrade): {exception.Message}",
                exception)
            {
                Http2NotNegotiated = true,
            };
        }

        return new AgentUnreachableException(nodeId, $"The agent of node {nodeId} is unreachable: {exception.Message}", exception);
    }

    private static string? ReadStartingModel(AgentError error)
    {
        try
        {
            return error.ReadDetails<StartConflictDetails>()?.StartingModelId;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
