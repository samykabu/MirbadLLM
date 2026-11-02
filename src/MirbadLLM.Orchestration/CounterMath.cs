namespace MirbadLLM.Orchestration;

/// <summary>
/// Safe rate math over Prometheus counters: a container restart resets the counter to (near) zero, which
/// must never be read as a negative rate.
/// </summary>
internal static class CounterMath
{
    /// <summary>Delta between two counter readings, treating a decrease (counter reset) as "start over from current".</summary>
    public static double Delta(double previousValue, double currentValue)
        => currentValue < previousValue ? currentValue : currentValue - previousValue;

    /// <summary>Per-second rate; zero when there is no previous reading or no elapsed time.</summary>
    public static double PerSecond(double? previousValue, double currentValue, double elapsedSeconds)
    {
        if (previousValue is null || elapsedSeconds <= 0)
        {
            return 0;
        }

        return Math.Max(0, Delta(previousValue.Value, currentValue) / elapsedSeconds);
    }

    /// <summary>Per-minute rate; zero when there is no previous reading or no elapsed time.</summary>
    public static double PerMinute(double? previousValue, double currentValue, double elapsedSeconds)
        => PerSecond(previousValue, currentValue, elapsedSeconds) * 60.0;
}
