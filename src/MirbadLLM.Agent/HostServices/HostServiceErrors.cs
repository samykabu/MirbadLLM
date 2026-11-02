using System.Net.Http;
using System.Net.Sockets;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;

namespace MirbadLLM.Agent.HostServices;

/// <summary>What a failed host-service call means for its operation (design §4.4 item 8).</summary>
/// <param name="Error">The agent error.</param>
/// <param name="Ambiguous">
/// True when the action may have run (timeout, connection lost after the request was sent): the operation ends
/// <see cref="OperationState.Unknown"/> instead of Failed.
/// </param>
public sealed record HostActionFailure(AgentError Error, bool Ambiguous);

/// <summary>
/// The answer of a host-service route: an HTTP status and its JSON body (<see cref="OperationStatus"/>, a read DTO or an
/// <see cref="AgentErrorResponse"/>). Kept free of ASP.NET types so the logic is testable without a server.
/// </summary>
/// <param name="StatusCode">HTTP status.</param>
/// <param name="Body">JSON body (serialized with <see cref="AgentJson.Options"/>).</param>
public sealed record HostCallResult(int StatusCode, object? Body)
{
    /// <summary>200 with <paramref name="body"/>.</summary>
    public static HostCallResult Ok(object? body) => new(200, body);

    /// <summary>The error's HTTP status (<see cref="HostServiceErrors.StatusFor"/>) with <c>{"error": …}</c>.</summary>
    public static HostCallResult Fail(AgentError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(HostServiceErrors.StatusFor(error.Code), new AgentErrorResponse(error));
    }

    /// <summary>An error built from a code and message.</summary>
    public static HostCallResult Fail(string code, string message) => Fail(HostServiceErrors.Error(code, message));
}

/// <summary>
/// The operator words of the host-service package per service manager (MN-239 item 1, D130). The package is shared: on
/// the Windows PC <see cref="Windows.ScmHostClient"/> serves the very same routes, so the Linux texts ("mirbadllm-hostd",
/// "no socket", "masked" — a systemd word) were reachable there. Only the words change: the error codes stay
/// <c>hostd_unavailable</c>, <c>timeout</c> and <c>not_supported</c>, which <see cref="AgentRoute.ErrorCodes"/>,
/// <see cref="ErrorMappings"/> and operations.md pin, and which the master maps back to
/// <see cref="HostServiceException"/> as today.
/// </summary>
public static class HostServiceWording
{
    /// <summary>True when <paramref name="manager"/> is the Windows Service Control Manager.</summary>
    public static bool IsScm(string? manager) => string.Equals(manager, HostServiceManagers.Scm, StringComparison.Ordinal);

    /// <summary>"mirbadllm-hostd did not answer in time." / "The Windows Service Control Manager did not answer in time."</summary>
    public static string NoAnswerInTime(string? manager) =>
        IsScm(manager) ? "The Windows Service Control Manager did not answer in time." : "mirbadllm-hostd did not answer in time.";

    /// <summary>Why the manager cannot be reached at all: a missing hostd socket, or an SCM that will not open.</summary>
    public static string NotInstalled(string? manager) =>
        IsScm(manager) ? "The Windows Service Control Manager could not be opened." : "The host agent is not installed (no socket).";

    /// <summary>True when <paramref name="manager"/> is launchd through the macOS hostd.</summary>
    public static bool IsLaunchd(string? manager) => string.Equals(manager, HostServiceManagers.Launchd, StringComparison.Ordinal);

    /// <summary>Why GPU processes are not in the answer (the Windows node reports them through its own GPU probe).</summary>
    public static string NoGpuProcesses(string? manager) => IsScm(manager)
        ? "The Windows Service Control Manager does not report GPU processes; this node's GPU telemetry does."
        : IsLaunchd(manager)
            ? "macOS has no per-process GPU accounting; this Mac's GPU utilisation comes from IOKit."
            : "mirbadllm-hostd did not report GPU processes (not answering, too old, or no nvidia-smi).";

    /// <summary>
    /// The autostart clause of an observed unit (<see cref="HostServiceOperations.ObserveAsync"/>): systemd's own
    /// <c>UnitFileState</c>, or the Windows start type the frozen mapping of
    /// <see cref="HostServiceManagers"/> came from (enabled → automatic, disabled → manual, masked → disabled).
    /// </summary>
    public static string Autostart(string? manager, string? unitFileState) => IsScm(manager)
        ? "start type " + (unitFileState switch
        {
            "enabled" => "automatic",
            "disabled" => "manual",
            "masked" => "disabled",
            _ => "unknown",
        })
        : "autostart " + unitFileState;
}

/// <summary>Error helpers of the host-service package: exception classification and code → HTTP status.</summary>
public static class HostServiceErrors
{
    /// <summary>Message of an operation whose outcome cannot be verified (design §4.4 item 8, the UI text).</summary>
    public const string OutcomeUnknownHint = "Outcome unknown — check the service and run the action again if needed.";

    /// <summary>Builds an <see cref="AgentError"/>.</summary>
    public static AgentError Error(string code, string message) => new() { Code = code, Message = message };

    /// <summary>HTTP status of an agent error code (design §4.3, <see cref="ErrorCodes"/>).</summary>
    public static int StatusFor(string code) => ErrorMappings.AgentStatus(code);

    /// <summary>
    /// Classifies a failed hostd call. hostd's own refusals keep their meaning (deny → <c>denied</c>, typed name →
    /// <c>confirmation_required</c>, unknown name → <c>not_found</c>, taken → <c>conflict</c>); a timeout or a connection
    /// lost after sending is ambiguous (<c>timeout</c> / <c>hostd_unavailable</c>, Unknown); a socket that is missing or
    /// refuses the connection is <c>hostd_unavailable</c> (Failed: nothing was sent); any other hostd error keeps hostd's
    /// message under <c>hostd_unavailable</c>, which the master maps back to <see cref="HostServiceException"/> as today.
    /// <paramref name="manager"/> picks the words only (MN-239 item 1): the same path serves the SCM on Windows.
    /// </summary>
    public static HostActionFailure FromHostd(Exception ex, string? manager = null)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (HostdClient.IsTimeout(ex))
        {
            return new(Error(ErrorCodes.Timeout, HostServiceWording.NoAnswerInTime(manager) + " " + OutcomeUnknownHint), Ambiguous: true);
        }

        return ex switch
        {
            HostServiceDeniedException => new(Error(ErrorCodes.Denied, ex.Message), false),
            HostServiceConfirmationRequiredException => new(Error(ErrorCodes.ConfirmationRequired, ex.Message), false),
            HostServiceNotFoundException => new(Error(ErrorCodes.NotFound, ex.Message), false),
            HostServiceConflictException => new(Error(ErrorCodes.Conflict, ex.Message), false),
            HostAgentUnavailableException { InnerException: { } inner } when !NotSent(inner) =>
                new(Error(ErrorCodes.HostdUnavailable, ex.Message + " " + OutcomeUnknownHint), Ambiguous: true),
            _ => new(Error(ErrorCodes.HostdUnavailable, ex.Message), false),
        };
    }

    /// <summary>
    /// Classifies a failed Docker call for a non-model container: timeouts and connections lost after sending are
    /// ambiguous; an unreachable socket is <c>docker_unavailable</c>; a Docker API error keeps today's text
    /// (<c>Docker could not {verb} {name}: …</c>).
    /// </summary>
    public static HostActionFailure FromDocker(Exception ex, string verb, string name)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex switch
        {
            TaskCanceledException or TimeoutException =>
                new(Error(ErrorCodes.Timeout, $"Docker did not answer in time ({verb} {name}). {OutcomeUnknownHint}"), Ambiguous: true),
            DockerApiException { StatusCode: System.Net.HttpStatusCode.NotFound } =>
                new(Error(ErrorCodes.NotFound, $"No container named '{name}'."), false),
            DockerApiException api =>
                new(Error(ErrorCodes.DockerUnavailable, $"Docker could not {verb} {name}: {api.ResponseBody.Trim()}"), false),
            _ when NotSent(ex) =>
                new(Error(ErrorCodes.DockerUnavailable, $"Docker is unreachable: {ex.Message}"), false),
            HttpRequestException or IOException =>
                new(Error(ErrorCodes.DockerUnavailable, $"Docker connection failed during {verb} {name}: {ex.Message} {OutcomeUnknownHint}"), Ambiguous: true),
            _ => new(Error(ErrorCodes.InternalError, ex.Message), false),
        };
    }

    /// <summary>True when the request certainly never reached the other side (connect failed).</summary>
    private static bool NotSent(Exception ex) => ex switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } => true,
        HttpRequestException { InnerException: SocketException } => true,
        SocketException => true,
        _ => false,
    };
}
