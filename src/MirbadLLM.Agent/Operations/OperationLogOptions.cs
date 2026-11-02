using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Operations;

/// <summary>Settings of the agent's <see cref="OperationLog"/> (design §4.4).</summary>
public sealed record OperationLogOptions
{
    /// <summary>
    /// The agent state directory (a Docker named volume on container nodes, see the MN-013 S4 note). The log lives in
    /// <see cref="OperationRetention.LogRelativePath"/> and <see cref="OperationRetention.CompactedRelativePath"/> under it.
    /// </summary>
    public required string StateDirectory { get; init; }

    /// <summary>This agent process's boot id, written on every log line.</summary>
    public required string BootId { get; init; }

    /// <summary>The node uid received at enrollment, written on accepted lines. Null before enrollment.</summary>
    public string? NodeUid { get; init; }

    /// <summary>Minimum number of most recent terminal records compaction always keeps (design §4.4 item 6).</summary>
    public int MinRetainedRecords { get; init; } = OperationRetention.MinRetainedRecords;

    /// <summary>Full path of <c>log.jsonl</c>.</summary>
    public string LogPath => Resolve(OperationRetention.LogRelativePath);

    /// <summary>Full path of <c>operations.json</c>.</summary>
    public string CompactedPath => Resolve(OperationRetention.CompactedRelativePath);

    private string Resolve(string relative) =>
        Path.Combine(StateDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
}
