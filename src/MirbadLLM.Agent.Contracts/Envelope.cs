namespace MirbadLLM.Agent.Contracts;

/// <summary>
/// The mutation envelope (design §4.3 "Every mutation is an operation"): every request that changes anything on an
/// agent derives from this record and so carries <c>{operationId, issuedAt, expiresAt, …}</c>.
/// <para>Rules: <see cref="IssuedAt"/> and <see cref="ExpiresAt"/> are set once by the master when it writes the
/// operation reference (<c>expiresAt = issuedAt + 24 h</c>, <see cref="OperationRetention.EnvelopeLifetime"/>) and are
/// part of the payload digest, so a retry resends them unchanged. The agent answers 410 <c>operation_expired</c> when
/// <see cref="ExpiresAt"/> has passed and refuses an <see cref="IssuedAt"/> more than 5 minutes in the future. Ids are
/// never reused: the same id with another action, target or digest is 409 <c>operation_id_conflict</c> (§4.4).</para>
/// </summary>
public abstract record AgentMutation
{
    /// <summary>Operation id chosen by the master (random, never reused).</summary>
    public required string OperationId { get; init; }

    /// <summary>When the master created the operation reference.</summary>
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>After this instant the agent answers 410 <c>operation_expired</c> instead of executing.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// Marker for "no body": the request type of routes that take only path parameters (design §4.3). Never serialized.
/// </summary>
public sealed record NoBody
{
    /// <summary>The single instance.</summary>
    public static NoBody Instance { get; } = new();
}

/// <summary>
/// Marker for a raw HTTP body streamed byte for byte (the inference proxy, design §7.2). Never serialized as JSON.
/// </summary>
public sealed record RawBody
{
    /// <summary>The single instance.</summary>
    public static RawBody Instance { get; } = new();
}

/// <summary>
/// One line of a streamed log (<c>GET /runtimes/{modelId}/logs</c>, design §4.3): each SSE <c>data:</c> line carries one
/// log line as plain text, not JSON. Used as the route's response type in <see cref="AgentRoutes"/>.
/// </summary>
/// <param name="Text">The log line without its trailing newline.</param>
public sealed record LogLine(string Text);
