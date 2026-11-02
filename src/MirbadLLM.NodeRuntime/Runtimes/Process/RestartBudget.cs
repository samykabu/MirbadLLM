namespace MirbadLLM.NodeRuntime.Runtimes.Process;

/// <summary>
/// The restart policy of a supervised process (design-p3p4 §A.1.3 "Restart policy"; <c>Agent:Process:RestartBudget</c> in
/// <c>RestartWindow</c>, D116): an exit after Ready is restarted on the same port while fewer than <see cref="Budget"/>
/// restarts happened in the last <see cref="Window"/>; the next exit is Failed. An exit before the instance was ever
/// Ready is Failed at once (a configuration or memory problem; a retry would fail the same way). Pure: the caller passes
/// the instance's recorded restart times and the clock.
/// </summary>
/// <param name="Budget">Restarts allowed in the window (2).</param>
/// <param name="Window">The window (10 minutes).</param>
public sealed record RestartBudget(int Budget, TimeSpan Window)
{
    /// <summary>The policy of <paramref name="options"/>.</summary>
    public static RestartBudget From(ProcessRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new RestartBudget(options.RestartBudget, options.RestartWindow);
    }

    /// <summary>
    /// Decides an unexpected exit at <paramref name="now"/>. <paramref name="wasReady"/>: the instance reached Ready at
    /// least once. Returns the restart times to record (the window's, plus <paramref name="now"/>) when a restart is
    /// allowed, or null when the exit is final (Failed).
    /// </summary>
    public IReadOnlyList<DateTimeOffset>? TryRestart(bool wasReady, IReadOnlyList<DateTimeOffset> restartTimes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(restartTimes);
        if (!wasReady)
        {
            return null;
        }

        var recent = restartTimes.Where(t => now - t < Window).Order().ToList();
        if (recent.Count >= Budget)
        {
            return null;
        }

        recent.Add(now);
        return recent;
    }
}
