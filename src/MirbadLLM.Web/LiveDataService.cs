using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;

namespace MirbadLLM.Web;

/// <summary>What a live subscriber reads from each shared refresh, so the service only asks Docker, the
/// registry and <c>/proc/meminfo</c> for what at least one open component shows.</summary>
[Flags]
public enum LiveNeeds
{
    None = 0,

    /// <summary>The registered model definitions (registry file).</summary>
    Models = 1,

    /// <summary>Every model's runtime state (one Docker list).</summary>
    Statuses = 2,

    /// <summary>A fresh <c>/proc/meminfo</c> reading.</summary>
    Memory = 4,
}

/// <summary>
/// One shared refresh of the headline data every live component on a page reads. Parts nobody asked for,
/// or that failed to load, are null; <see cref="Error"/> names the first failure.
/// <para><see cref="Maintenance"/> is the oldest open window (<see cref="IMaintenanceMode.Current"/>, P1's only one);
/// <see cref="MaintenanceWindows"/> is every open window (one per running job, P2), for readers that ask about a model or
/// a node (MN-126).</para>
/// </summary>
public sealed record LiveSnapshot(
    DateTimeOffset At,
    LiveNeeds Needs,
    IReadOnlyList<ModelDefinition>? Models,
    IReadOnlyDictionary<string, ModelStatus>? Statuses,
    MemorySnapshot? Memory,
    IReadOnlyDictionary<string, ModelKpis> LatestKpis,
    HostSample? LatestHost,
    MaintenanceState? Maintenance,
    string? Error,
    IReadOnlyList<MaintenanceState>? MaintenanceWindows = null)
{
    public bool Covers(LiveNeeds needs) => (Needs & needs) == needs;

    /// <summary>Every open window, oldest first (<see cref="Maintenance"/> alone when the list was not filled).</summary>
    public IReadOnlyList<MaintenanceState> Windows => MaintenanceWindows ?? (Maintenance is { } only ? [only] : []);

    /// <summary>The window of the job running on <paramref name="modelId"/> (the model under test), or null.</summary>
    public MaintenanceState? JobOn(string modelId) =>
        Windows.FirstOrDefault(w => string.Equals(w.ModelId, modelId, StringComparison.Ordinal));
}

/// <summary>
/// The single refresh source for one browser tab (a Blazor circuit shares one DI scope, so the status bar
/// and the page body get the same instance). One 1 s timer drives everything: every fifth tick it reloads
/// the union of what the subscribers need and publishes one <see cref="LiveSnapshot"/> to all of them;
/// every tick it publishes the clock to subscribers that render elapsed timers. The timer runs only while
/// someone is subscribed. Replaces the independent 5 s timers the status bar, Dashboard, Services and
/// Monitoring used to run side by side.
/// </summary>
public sealed class LiveDataService : IAsyncDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ClockInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(1);
    private const int TicksPerRefresh = 5;

    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private readonly List<Subscription> _subscriptions = [];
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public LiveDataService(IServiceProvider services, TimeProvider? time = null)
    {
        _services = services;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The last published snapshot (null until the first load).</summary>
    public LiveSnapshot? Current { get; private set; }

    /// <summary>How many times the timer loop has been started (tests: exactly one per active period).</summary>
    public int TimerStarts { get; private set; }

    /// <summary>How many loads actually hit the sources (tests: coalescing).</summary>
    public int Fetches { get; private set; }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _loop is not null;
            }
        }
    }

    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscriptions.Count;
            }
        }
    }

    /// <summary>
    /// The shared instance for this circuit, or a private one (owned by the caller, which must dispose it)
    /// when none is registered — e.g. a component rendered on its own in a test.
    /// </summary>
    public static LiveDataService Resolve(IServiceProvider services, out bool owned)
    {
        var shared = services.GetService<LiveDataService>();
        owned = shared is null;
        return shared ?? new LiveDataService(services);
    }

    /// <summary>
    /// Registers a subscriber. <paramref name="onRefresh"/> runs after every shared reload (every
    /// <see cref="RefreshInterval"/>, or when someone calls <see cref="RefreshAsync"/>); <paramref name="onClock"/>,
    /// when given, every <see cref="ClockInterval"/>. Both run off the renderer's thread: components marshal
    /// with InvokeAsync. Dispose the returned handle to unsubscribe; the timer stops with the last one.
    /// </summary>
    public IDisposable Subscribe(LiveNeeds needs, Func<LiveSnapshot, Task> onRefresh, Func<DateTimeOffset, Task>? onClock = null)
    {
        ArgumentNullException.ThrowIfNull(onRefresh);
        var subscription = new Subscription(this, needs, onRefresh, onClock);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscriptions.Add(subscription);
            if (_loop is null)
            {
                _cts = new CancellationTokenSource();
                TimerStarts++;
                // Off the caller's synchronization context: the loop must never queue work onto a circuit's
                // renderer (subscribers marshal their own updates with InvokeAsync).
                var token = _cts.Token;
                _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
            }
        }

        return subscription;
    }

    /// <summary>The current snapshot when it is under a second old and covers <paramref name="needs"/>;
    /// otherwise a fresh load (without publishing). Concurrent callers share one load.</summary>
    public async Task<LiveSnapshot> GetAsync(LiveNeeds needs, CancellationToken ct = default)
    {
        if (IsFresh(Current, needs))
        {
            return Current!;
        }

        await _fetchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsFresh(Current, needs))
            {
                return Current!;
            }

            var snapshot = await FetchAsync(needs | SubscribedNeeds(), ct).ConfigureAwait(false);
            Current = snapshot;
            return snapshot;
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    /// <summary>Reloads now (e.g. right after a start/stop) and publishes to every subscriber.</summary>
    public async Task<LiveSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        LiveSnapshot snapshot;
        await _fetchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            snapshot = await FetchAsync(SubscribedNeeds(), ct).ConfigureAwait(false);
            Current = snapshot;
        }
        finally
        {
            _fetchGate.Release();
        }

        await PublishAsync(snapshot).ConfigureAwait(false);
        return snapshot;
    }

    private bool IsFresh(LiveSnapshot? snapshot, LiveNeeds needs) =>
        snapshot is not null && snapshot.Covers(needs) && _time.GetUtcNow() - snapshot.At < FreshFor;

    private LiveNeeds SubscribedNeeds()
    {
        lock (_gate)
        {
            return _subscriptions.Aggregate(LiveNeeds.None, (all, s) => all | s.Needs);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(ClockInterval, _time);
        int tick = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                tick++;
                if (tick % TicksPerRefresh == 0)
                {
                    try
                    {
                        await RefreshAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A failed load leaves the last snapshot in place; its "Updated Xs ago" stamp ages.
                    }
                }

                await PublishClockAsync(_time.GetUtcNow()).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Last subscriber left, or the circuit closed.
        }
    }

    private async Task<LiveSnapshot> FetchAsync(LiveNeeds needs, CancellationToken ct)
    {
        Fetches++;
        string? error = null;
        IReadOnlyList<ModelDefinition>? models = null;
        IReadOnlyDictionary<string, ModelStatus>? statuses = null;
        MemorySnapshot? memory = null;

        if (needs.HasFlag(LiveNeeds.Models) && _services.GetService<IModelRegistry>() is { } registry)
        {
            try
            {
                models = await registry.ListAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error ??= $"Could not read the model list: {ex.Message}";
            }
        }

        if (needs.HasFlag(LiveNeeds.Statuses) && _services.GetService<IModelRuntime>() is { } runtime)
        {
            try
            {
                statuses = (await runtime.ListStatusAsync(ct).ConfigureAwait(false)).ToDictionary(s => s.Id, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error ??= $"Could not read model states: {ex.Message}";
            }
        }

        if (needs.HasFlag(LiveNeeds.Memory) && _services.GetService<IMemoryProbe>() is { } probe)
        {
            try
            {
                memory = await probe.GetAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error ??= $"Could not read memory: {ex.Message}";
            }
        }

        var history = _services.GetService<IMetricsHistory>();
        var latest = history?.Latest() ?? new Dictionary<string, ModelKpis>();
        var host = history?.GetHost(TimeSpan.FromMinutes(1));
        var windows = _services.GetService<IMaintenanceMode>()?.Active ?? [];
        return new LiveSnapshot(
            _time.GetUtcNow(),
            needs,
            models,
            statuses,
            memory,
            latest,
            host is { Count: > 0 } ? host[^1] : null,
            windows.Count > 0 ? windows[0] : null,
            error,
            windows);
    }

    private async Task PublishAsync(LiveSnapshot snapshot)
    {
        foreach (var subscription in Snapshot())
        {
            try
            {
                await subscription.OnRefresh(snapshot).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // One broken subscriber must not stop the others from updating.
            }
        }
    }

    private async Task PublishClockAsync(DateTimeOffset now)
    {
        foreach (var subscription in Snapshot())
        {
            if (subscription.OnClock is null)
            {
                continue;
            }

            try
            {
                await subscription.OnClock(now).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // Same as above.
            }
        }
    }

    private List<Subscription> Snapshot()
    {
        lock (_gate)
        {
            return [.. _subscriptions];
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        CancellationTokenSource? stop = null;
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
            if (_subscriptions.Count == 0 && _loop is not null)
            {
                stop = _cts;
                _cts = null;
                _loop = null;
            }
        }

        // Not disposed here: the loop may still be registering on its token; cancelling is enough.
        stop?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _subscriptions.Clear();
            loop = _loop;
            cts = _cts;
            _loop = null;
            _cts = null;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        cts?.Dispose();
        _fetchGate.Dispose();
    }

    private sealed class Subscription(LiveDataService owner, LiveNeeds needs, Func<LiveSnapshot, Task> onRefresh, Func<DateTimeOffset, Task>? onClock)
        : IDisposable
    {
        private int _disposed;

        public LiveNeeds Needs { get; } = needs;

        public Func<LiveSnapshot, Task> OnRefresh { get; } = onRefresh;

        public Func<DateTimeOffset, Task>? OnClock { get; } = onClock;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unsubscribe(this);
            }
        }
    }
}
