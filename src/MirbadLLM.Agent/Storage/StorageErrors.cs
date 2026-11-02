using System.Text.RegularExpressions;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Storage;

/// <summary>
/// Agent errors raised by the storage package (design §4.3 error shape) and the HTTP status each code answers with.
/// Every message that may carry text from a download helper is passed through <see cref="RedactSecrets"/> first.
/// </summary>
public static partial class StorageErrors
{
    /// <summary>HTTP status of an agent error code (storage routes and the mutation envelope rules).</summary>
    public static int StatusFor(string code) => ErrorMappings.AgentStatus(code);

    /// <summary>Creates an agent exception for <paramref name="code"/> with its status.</summary>
    public static AgentException Create(string code, string message, object? details = null) =>
        new(StatusFor(code), Error(code, message, details));

    /// <summary>Builds an <see cref="AgentError"/> (details serialized with the wire options).</summary>
    public static AgentError Error(string code, string message, object? details = null) => new()
    {
        Code = code,
        Message = message,
        Details = details is null ? null : AgentJson.ToElement(details),
    };

    /// <summary>400 <c>invalid_request</c>.</summary>
    public static AgentException InvalidRequest(string message) => Create(ErrorCodes.InvalidRequest, message);

    /// <summary>
    /// 400 <c>invalid_request</c> decided by the Hugging Face Hub (unknown or gated repo, bad token), marked with
    /// <see cref="UpstreamRefusalDetails"/> so the master answers it as today's 422 (W3 review MN-094).
    /// </summary>
    public static AgentException HuggingFaceRefused(string message) =>
        Create(ErrorCodes.InvalidRequest, message, new UpstreamRefusalDetails(UpstreamRefusalDetails.HuggingFace));

    /// <summary>404 <c>not_found</c>.</summary>
    public static AgentException NotFound(string message) => Create(ErrorCodes.NotFound, message);

    /// <summary>422 <c>spec_rejected</c> with <see cref="SpecRejectedDetails"/>.</summary>
    public static AgentException SpecRejected(string reason) =>
        Create(ErrorCodes.SpecRejected, reason, new SpecRejectedDetails(reason));

    /// <summary>503 <c>docker_unavailable</c>.</summary>
    public static AgentException DockerUnavailable(Exception? cause) =>
        Create(ErrorCodes.DockerUnavailable, "Docker is not reachable on this node" + (cause is null ? "." : $": {cause.Message}"));

    /// <summary>
    /// Removes secrets from text that may come from a helper container or Hugging Face (design §6.4, §10.4): every
    /// occurrence of the request's own token, and anything shaped like a Hugging Face token (<c>hf_…</c>) or an
    /// <c>HF_TOKEN=…</c> / <c>Bearer …</c> assignment.
    /// </summary>
    public static string? RedactSecrets(string? text, string? token = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        string result = text;
        if (!string.IsNullOrEmpty(token))
        {
            result = result.Replace(token, Protocol.RedactedValue, StringComparison.Ordinal);
        }

        result = HfTokenRegex().Replace(result, Protocol.RedactedValue);
        result = TokenAssignmentRegex().Replace(result, m => m.Groups[1].Value + Protocol.RedactedValue);
        return result;
    }

    [GeneratedRegex(@"\bhf_[A-Za-z0-9]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex HfTokenRegex();

    [GeneratedRegex(@"((?:HF_TOKEN|HUGGING_FACE_HUB_TOKEN|token)\s*[=:]\s*|Bearer\s+)[^\s""',;]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TokenAssignmentRegex();
}
