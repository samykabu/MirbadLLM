using System.Globalization;

namespace MirbadLLM.Core;

/// <summary>
/// The master triple <c>(instanceId, epoch, bootId)</c> (design §4.2 layer 2). Every mutating agent request carries it
/// in the <c>MirbadLLM-Master</c> header as <c>{instanceId}/{epoch}/{bootId}</c> (<see cref="ToHeaderValue"/>), and
/// agents persist the highest accepted one. Agents decide: lower epoch → <c>stale_master</c>; equal epoch with another
/// instance or boot → <c>competing_master</c>; higher epoch → accepted and persisted.
/// </summary>
/// <param name="InstanceId">Random id created once per <c>/data</c> (<c>master.json</c>); never contains <c>/</c>.</param>
/// <param name="Epoch">Incremented and persisted at every master boot, before the first agent call.</param>
/// <param name="BootId">Random id generated at every master boot; never contains <c>/</c>.</param>
public sealed record MasterTriple(string InstanceId, long Epoch, string BootId)
{
    /// <summary>The <c>MirbadLLM-Master</c> header value: <c>{instanceId}/{epoch}/{bootId}</c>, epoch in invariant digits.</summary>
    public string ToHeaderValue() => string.Create(CultureInfo.InvariantCulture, $"{InstanceId}/{Epoch}/{BootId}");

    /// <summary>
    /// Parses a <c>MirbadLLM-Master</c> header value. Exactly three non-empty <c>/</c>-separated parts; the epoch is a
    /// non-negative invariant integer.
    /// </summary>
    public static bool TryParse(string? value, out MasterTriple triple)
    {
        triple = new MasterTriple("", 0, "");
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] parts = value.Trim().Split('/');
        if (parts.Length != 3 || parts[0].Length == 0 || parts[2].Length == 0 ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long epoch))
        {
            return false;
        }

        triple = new MasterTriple(parts[0], epoch, parts[2]);
        return true;
    }

    /// <summary>True when both triples name the same master boot (same instance, epoch and boot id).</summary>
    public bool SameBoot(MasterTriple? other) =>
        other is not null &&
        Epoch == other.Epoch &&
        string.Equals(InstanceId, other.InstanceId, StringComparison.Ordinal) &&
        string.Equals(BootId, other.BootId, StringComparison.Ordinal);
}

/// <summary>
/// The master's fencing identity (design §4.2 layers 1–3, master side), implemented by
/// <c>MirbadLLM.Orchestration.Fencing.MasterIdentity</c> (MN-007).
/// <para>Invariants:</para>
/// <list type="bullet">
/// <item><c>/data/master.lock</c> is held exclusively (<c>FileShare.None</c>) for the process lifetime; a second process
/// on the same volume fails to start with a clear message.</item>
/// <item><see cref="Epoch"/> was incremented and persisted in <c>master.json</c> before this object became available,
/// so no agent call ever carries an epoch that is not on disk.</item>
/// <item><see cref="InstanceId"/> is created once per <c>/data</c>; <see cref="BootId"/> is new every boot.</item>
/// <item>Takeover is never automatic: <see cref="TakeoverRequested"/> only reports the operator's flag
/// (<c>/data/takeover</c> or <c>MirbadLLM__Fencing__Takeover=true</c>).</item>
/// </list>
/// </summary>
public interface IMasterIdentity
{
    /// <summary>Random id created once per <c>/data</c>.</summary>
    string InstanceId { get; }

    /// <summary>The persisted epoch of this boot (raised by takeover).</summary>
    long Epoch { get; }

    /// <summary>Random id of this boot.</summary>
    string BootId { get; }

    /// <summary>The triple sent in <c>MirbadLLM-Master</c>; reflects the latest raised epoch.</summary>
    MasterTriple Current { get; }

    /// <summary>True when the operator asked for a takeover (flag file or option) and it has not completed yet.</summary>
    bool TakeoverRequested { get; }

    /// <summary>Takeover records persisted in <c>master.json</c>, oldest first.</summary>
    IReadOnlyList<TakeoverRecord> Takeovers { get; }

    /// <summary>
    /// Raises the epoch to at least <paramref name="epoch"/> and persists it (atomic write, flushed) before returning.
    /// A lower or equal value leaves the epoch unchanged. Returns the current triple.
    /// </summary>
    Task<MasterTriple> RaiseEpochToAsync(long epoch, CancellationToken ct = default);

    /// <summary>
    /// Completes a takeover (design §4.2 step 3): raises the epoch to <see cref="TakeoverRecord.Epoch"/>, persists the
    /// record together with it, then deletes the takeover flag. Also used to append a continuation to an existing record
    /// (same <see cref="TakeoverRecord.TakeoverId"/> replaces the stored one).
    /// </summary>
    Task<MasterTriple> CompleteTakeoverAsync(TakeoverRecord record, CancellationToken ct = default);
}

/// <summary>
/// <c>{DataPath}/master.json</c> (design §4.2 layer 2), written atomically (temp + move) with
/// <see cref="MasterStateJson.Options"/>.
/// </summary>
public sealed record MasterStateDocument
{
    /// <summary>Schema version of this file format.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>File name under the data path.</summary>
    public const string FileName = "master.json";

    /// <summary>Lock file name under the data path, held with <c>FileShare.None</c> for the process lifetime.</summary>
    public const string LockFileName = "master.lock";

    /// <summary>Takeover flag file name under the data path (design §4.2 step 2).</summary>
    public const string TakeoverFlagFileName = "takeover";

    /// <summary>Schema version.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Random id created once.</summary>
    public required string MasterInstanceId { get; init; }

    /// <summary>Last persisted epoch.</summary>
    public required long Epoch { get; init; }

    /// <summary>When the instance id was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the epoch was last written.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Takeover records, oldest first.</summary>
    public IReadOnlyList<TakeoverRecord> Takeovers { get; init; } = [];
}

/// <summary>
/// An authorized takeover (design §4.2 step 3): the epoch the master moved to, who did it, which agents answered with
/// which accepted epoch, which were unreachable (they become Pending takeover, P2), and any later continuations.
/// </summary>
public sealed record TakeoverRecord
{
    /// <summary>Random id of this takeover.</summary>
    public required string TakeoverId { get; init; }

    /// <summary>The epoch after the takeover (≥ max(restored, all reachable agents' epochs) + 1).</summary>
    public required long Epoch { get; init; }

    /// <summary>The epoch the master had before.</summary>
    public long PreviousEpoch { get; init; }

    /// <summary>When the takeover was persisted.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>Who confirmed it (admin session hash or <c>config</c> for the option).</summary>
    public string? Operator { get; init; }

    /// <summary>Agents that answered <c>/info</c>, with the epoch each had accepted.</summary>
    public IReadOnlyList<TakeoverNodeEpoch> Answered { get; init; } = [];

    /// <summary>Node ids that did not answer.</summary>
    public IReadOnlyList<string> Unreachable { get; init; } = [];

    /// <summary>Explicit continuations for returning agents that held a higher epoch (P2, MN-115).</summary>
    public IReadOnlyList<TakeoverContinuation> Continuations { get; init; } = [];
}

/// <summary>An agent's accepted epoch as read during a takeover.</summary>
/// <param name="NodeId">Node id.</param>
/// <param name="NodeUid">Node uid reported by <c>/info</c>.</param>
/// <param name="AcceptedEpoch">Epoch the agent had accepted; null when it had accepted none.</param>
public sealed record TakeoverNodeEpoch(string NodeId, string NodeUid, long? AcceptedEpoch);

/// <summary>An operator-confirmed continuation of a takeover for one returning agent (design §4.2 step 3).</summary>
/// <param name="NodeId">Node id.</param>
/// <param name="AgentEpoch">The epoch the returning agent held.</param>
/// <param name="NewEpoch">The master's epoch after continuing (<c>AgentEpoch + 1</c> or higher).</param>
/// <param name="At">When it was persisted.</param>
/// <param name="Operator">Who confirmed it.</param>
public sealed record TakeoverContinuation(string NodeId, long AgentEpoch, long NewEpoch, DateTimeOffset At, string? Operator);
