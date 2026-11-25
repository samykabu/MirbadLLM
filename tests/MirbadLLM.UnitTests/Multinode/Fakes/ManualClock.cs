namespace MirbadLLM.UnitTests.Multinode.Fakes;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock and timers move only on <see cref="Advance"/>. One-shot timers are the
/// monitor's backoffs and polls (<c>Task.Delay</c>); the periodic one is its liveness loop.
/// </summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly long _startTicks = start.UtcTicks;
    private DateTimeOffset _now = start;
    private TaskCompletionSource _scheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _periodicFired = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => GetUtcNow().UtcTicks - _startTicks;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        TaskCompletionSource? periodic = null;
        lock (_lock)
        {
            _now += by;
            due = [.. _timers.Where(t => t.DueAt <= _now)];
            foreach (var timer in due)
            {
                if (timer.Period > TimeSpan.Zero)
                {
                    timer.DueAt = _now + timer.Period;
                    periodic = _periodicFired;
                }
                else
                {
                    _timers.Remove(timer);
                }
            }

            if (periodic is not null)
            {
                _periodicFired = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }

        periodic?.TrySetResult();
    }

    /// <summary>Advances to the earliest pending one-shot timer.</summary>
    public void AdvanceToNextOneShot()
    {
        TimeSpan by;
        lock (_lock)
        {
            var next = _timers.Where(t => t.Period == TimeSpan.Zero).OrderBy(t => t.DueAt).FirstOrDefault();
            if (next is null)
            {
                return;
            }

            by = next.DueAt > _now ? next.DueAt - _now : TimeSpan.Zero;
        }

        Advance(by);
    }

    /// <summary>Completes when a one-shot timer is pending.</summary>
    public Task WaitForOneShotAsync(CancellationToken ct) => WaitForOneShotAsync(TimeSpan.MaxValue, ct);

    public async Task WaitForOneShotAsync(TimeSpan within, CancellationToken ct)
    {
        while (true)
        {
            Task signal;
            lock (_lock)
            {
                if (_timers.Any(t => t.Period == TimeSpan.Zero && t.DueAt - _now <= within))
                {
                    return;
                }

                signal = _scheduled.Task;
            }

            await signal.WaitAsync(ct);
        }
    }

    /// <summary>Completes after the next advance that fires a periodic timer, once its callback ran.</summary>
    public Task WaitForPeriodicTickAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            return _periodicFired.Task.WaitAsync(ct);
        }
    }

    private void Schedule(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        TaskCompletionSource scheduled;
        lock (_lock)
        {
            _timers.Remove(timer);
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                return;
            }

            timer.DueAt = _now + dueTime;
            timer.Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
            _timers.Add(timer);
            scheduled = _scheduled;
            _scheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        scheduled.TrySetResult();
    }

    private void Remove(ManualTimer timer)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset DueAt { get; set; }

        public TimeSpan Period { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            clock.Schedule(this, dueTime, period);
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => clock.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
