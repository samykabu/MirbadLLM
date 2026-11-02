using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>An expected (saved definition) and actual (running instance) value of one hash.</summary>
/// <param name="Expected">Computed from the saved definition; null when it cannot be computed (no definition).</param>
/// <param name="Actual">
/// Carried by / recomputed from the running instance; null when the instance has no such label (unknown).
/// </param>
public readonly record struct HashPair(string? Expected, string? Actual)
{
    /// <summary>Both sides are known and differ (ordinal).</summary>
    public bool Differs => Expected is not null && Actual is not null && !string.Equals(Expected, Actual, StringComparison.Ordinal);
}

/// <summary>Why a restart is pending (flags; <see cref="None"/> = not pending).</summary>
[Flags]
public enum RestartPendingReasons
{
    /// <summary>Not pending.</summary>
    None = 0,

    /// <summary>The legacy config hash (image + canonical vLLM argv) differs.</summary>
    LegacyConfigHash = 1,

    /// <summary>Both sides carry an execution fingerprint and it differs (placement, runtime, delivery, env, mounts…).</summary>
    ExecutionFingerprint = 2,
}

/// <summary>
/// The restart-pending rule of design §5.1 "two fingerprints" as a pure function:
/// <c>RestartPending</c> = (legacy hash differs, computed as today including the <c>Cmd</c> recheck) <b>or</b> (both
/// sides have an execution fingerprint and it differs).
/// <para>
/// A side that is null never makes a hash "differ": an instance without the legacy label is "unknown", never pending
/// (today's rule), and an instance without the fingerprint label (every container from before P1) is compared on the
/// legacy hash only — so P1 marks nothing restart-pending that today's code does not.
/// </para>
/// <para>
/// The caller passes the legacy <see cref="HashPair.Actual"/> after today's <c>Cmd</c> recheck (the label when it equals
/// the expected hash, otherwise the canonical hash recomputed from the container's command line, falling back to the
/// label; as the pre-P1 <c>DockerModelRuntime.LaunchedHashAsync</c> did, from <c>/launch-config</c> in P1).
/// </para>
/// </summary>
public static class RestartPendingRule
{
    /// <summary>Which of the two hashes make a restart pending.</summary>
    public static RestartPendingReasons Evaluate(HashPair legacyHashes, HashPair fingerprints)
    {
        var reasons = RestartPendingReasons.None;
        if (legacyHashes.Differs)
        {
            reasons |= RestartPendingReasons.LegacyConfigHash;
        }

        if (fingerprints.Differs)
        {
            reasons |= RestartPendingReasons.ExecutionFingerprint;
        }

        return reasons;
    }

    /// <summary>The hash comparison alone (no state/tuning gating).</summary>
    public static bool IsPending(HashPair legacyHashes, HashPair fingerprints) =>
        Evaluate(legacyHashes, fingerprints) != RestartPendingReasons.None;

    /// <summary>
    /// The full rule as the pre-P1 <c>DockerModelRuntime.IsRestartPendingAsync</c> applied it: only a Starting/Ready
    /// instance that is not a tuning candidate can be restart-pending, and only when the saved definition's legacy
    /// hash is known; then <see cref="IsPending(HashPair, HashPair)"/>.
    /// </summary>
    public static bool IsPending(ModelState state, bool tuning, HashPair legacyHashes, HashPair fingerprints) =>
        state is ModelState.Starting or ModelState.Ready
        && !tuning
        && legacyHashes.Expected is not null
        && IsPending(legacyHashes, fingerprints);
}
