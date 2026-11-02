using System.Security.Cryptography;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Orchestration.Agents;

/// <summary>
/// The mutation envelope a master operation carries (design §4.3): a fresh random id and <c>issuedAt</c> /
/// <c>expiresAt = issuedAt + 24 h</c>, set <b>once</b> when the operation reference is created. A retry resends the same
/// values (they are part of the agent's payload digest). Copy them into the request record:
/// <code>new StopRequest { OperationId = e.OperationId, IssuedAt = e.IssuedAt, ExpiresAt = e.ExpiresAt }</code>
/// </summary>
/// <param name="OperationId">Random id (32 hex), never reused.</param>
/// <param name="IssuedAt">Master clock at creation, whole milliseconds.</param>
/// <param name="ExpiresAt"><paramref name="IssuedAt"/> + <see cref="OperationRetention.EnvelopeLifetime"/>.</param>
public sealed record AgentEnvelope(string OperationId, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt)
{
    /// <summary>A new envelope at <paramref name="time"/>'s now.</summary>
    public static AgentEnvelope Create(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        DateTimeOffset now = time.GetUtcNow();
        now = new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
        return new AgentEnvelope(NewOperationId(), now, now + OperationRetention.EnvelopeLifetime);
    }

    /// <summary>A random operation id: 128 bits as 32 lowercase hex characters.</summary>
    public static string NewOperationId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}

/// <summary>
/// The master's payload digest of a mutation (design §4.4 item 1). Delegates to the shared canonical form
/// (<see cref="OperationPayload.ComputeRequestDigest"/>, W2 review D38), so the digest in <c>operations.json</c> equals the
/// one the agent stores for the same request; a secret never reaches <c>operations.json</c>.
/// </summary>
public static class AgentPayloadDigest
{
    /// <summary>The digest of <paramref name="request"/> serialized as <paramref name="requestType"/>.</summary>
    public static string Compute(object request, Type requestType) =>
        OperationPayload.ComputeRequestDigest(request, requestType);
}
