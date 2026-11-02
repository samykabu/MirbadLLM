using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Runtimes.Process;

namespace MirbadLLM.Agent.Runtimes;

/// <summary>
/// The Degraded reason <see cref="HealthReasonCodes.ExecutableUnverified"/> (design-p3p4 §A.1.2; MN-222): one reason per
/// installed executable version whose files no longer match its install manifest (a tampered or missing listed file, an
/// unlisted <c>.exe</c>/<c>.dll</c>, an unreadable manifest), naming the version and the first problem. Launches naming
/// that version get 422 <c>executable_unverified</c>; other versions keep working. A version still being hashed (the first
/// background verification after start, or a re-check after a change) is not a reason: it is not known to be bad, and
/// <c>Resolve</c> already answers 422 until it is done. Reads the catalogue's last results (metadata checks only, never a
/// hash) and never throws.
/// </summary>
public sealed class ExecutableHealthContributor(IExecutableCatalog catalog) : IHealthContributor
{
    /// <inheritdoc />
    public Task<IReadOnlyList<HealthReason>> CheckAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ExecutableVersionState> states;
        try
        {
            states = catalog.List();
        }
#pragma warning disable CA1031 // never throws: an unreadable catalogue is itself the reason
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Task.FromResult<IReadOnlyList<HealthReason>>(
                [new HealthReason(HealthReasonCodes.ExecutableUnverified, "The executable catalogue cannot be read: " + ex.Message)]);
        }

        IReadOnlyList<HealthReason> reasons = [.. states
            .Where(s => !s.Verified && !IsPending(s.Problem))
            .Select(s => new HealthReason(
                HealthReasonCodes.ExecutableUnverified,
                $"{s.Name} {s.Version} failed verification: {s.Problem ?? "unknown problem"}. Launches naming it get 422 " +
                $"{ErrorCodes.ExecutableUnverified}; reinstall it with install-agent.ps1 -LlamaCpp."))];
        return Task.FromResult(reasons);
    }

    private static bool IsPending(string? problem) =>
        string.Equals(problem, ExecutableCatalog.NotVerifiedYet, StringComparison.Ordinal) ||
        string.Equals(problem, ExecutableCatalog.ReVerifying, StringComparison.Ordinal);
}
