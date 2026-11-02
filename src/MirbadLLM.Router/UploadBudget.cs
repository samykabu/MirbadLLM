using Microsoft.Extensions.Options;

namespace MirbadLLM.Router;

/// <summary>
/// The master's global in-flight upload budget (design §7.2): a byte semaphore over the request bodies the router holds
/// buffered. A request takes its share through a <see cref="Lease"/> before it buffers the bytes and gives it back when the
/// body was sent upstream (or the request ended). Waiting is bounded (<see cref="RouterOptions.UploadBudgetWait"/>); the
/// router answers 503 <c>server_busy</c> when the wait runs out. A request larger than the whole budget takes all of it.
/// </summary>
public sealed class UploadBudget
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _wait;
    private long _available;
    private TaskCompletionSource _released = NewSignal();

    /// <summary>Creates the budget from the router options.</summary>
    public UploadBudget(IOptions<RouterOptions> options, TimeProvider? timeProvider = null)
        : this(options?.Value.MaxBufferedBodyBytes ?? throw new ArgumentNullException(nameof(options)), options.Value.UploadBudgetWait, timeProvider)
    {
    }

    /// <summary>Creates a budget of <paramref name="capacityBytes"/> with the given wait.</summary>
    public UploadBudget(long capacityBytes, TimeSpan wait, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(wait, TimeSpan.Zero);
        Capacity = capacityBytes;
        _available = capacityBytes;
        _wait = wait;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Total budget in bytes.</summary>
    public long Capacity { get; }

    /// <summary>Bytes not held by any lease.</summary>
    public long Available
    {
        get
        {
            lock (_gate)
            {
                return _available;
            }
        }
    }

    /// <summary>A lease holding nothing yet; dispose it to give back what it took.</summary>
    public Lease CreateLease() => new(this);

    private async ValueTask<bool> AcquireAsync(long bytes, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(_wait, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        while (true)
        {
            Task released;
            lock (_gate)
            {
                if (_available >= bytes)
                {
                    _available -= bytes;
                    return true;
                }

                released = _released.Task;
            }

            try
            {
                await released.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    private void Release(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        TaskCompletionSource released;
        lock (_gate)
        {
            _available = Math.Min(Capacity, _available + bytes);
            released = _released;
            _released = NewSignal();
        }

        released.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The share of one request. Grows with <see cref="EnsureAsync"/>; <see cref="Dispose"/> gives it all back (idempotent).</summary>
    public sealed class Lease : IDisposable
    {
        private readonly UploadBudget _budget;
        private long _held;
        private bool _disposed;

        internal Lease(UploadBudget budget) => _budget = budget;

        /// <summary>Bytes this lease holds.</summary>
        public long Held => Volatile.Read(ref _held);

        /// <summary>
        /// Makes the lease hold at least <paramref name="totalBytes"/> (capped at the budget's capacity), waiting up to the
        /// configured time for the difference. False when the budget stayed short (the caller answers <c>server_busy</c>).
        /// </summary>
        public async ValueTask<bool> EnsureAsync(long totalBytes, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long target = Math.Min(Math.Max(totalBytes, 0), _budget.Capacity);
            long missing = target - _held;
            if (missing <= 0)
            {
                return true;
            }

            if (!await _budget.AcquireAsync(missing, ct).ConfigureAwait(false))
            {
                return false;
            }

            _held += missing;
            return true;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            long held = _held;
            _held = 0;
            _budget.Release(held);
        }
    }
}
