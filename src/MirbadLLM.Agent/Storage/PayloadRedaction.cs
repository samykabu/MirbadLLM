using System.Text.Json;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Storage;

/// <summary>
/// Recursive secret redaction of a request body before it reaches the operation log (design §4.4 item 1, §10.4): every
/// property named like a <see cref="Protocol.SecretFieldNames"/> entry (case-insensitive), at any depth, gets the value
/// <see cref="Protocol.RedactedValue"/> when it is not null. The storage package hands only redacted payloads to
/// <c>IOperationLog</c>, so the payload digest is computed without the HF token whatever the log does, and a retry that
/// resends the same request with or without the token is the same operation.
/// </summary>
/// <remarks>Delegates to the shared <see cref="OperationPayload.Redact"/> (W2 review MN-092, D38).</remarks>
public static class PayloadRedaction
{
    /// <summary>Returns <paramref name="payload"/> with every secret field replaced by <c>***</c>.</summary>
    public static JsonElement Redact(JsonElement payload) => OperationPayload.Redact(payload);
}
