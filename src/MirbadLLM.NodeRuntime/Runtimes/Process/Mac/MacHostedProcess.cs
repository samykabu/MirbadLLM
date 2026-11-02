using System.Runtime.Versioning;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.NodeRuntime.Runtimes.Process.Mac;

/// <summary>
/// A process started or adopted by <see cref="MacProcessHost"/>. macOS has no job objects; the process was spawned as the
/// leader of its own session and process group (<c>POSIX_SPAWN_SETSID</c>), so the group id is its pid and stands in for
/// the job: <see cref="KillTreeAsync"/> signals the whole group. Disposing only stops watching; it never kills.
/// <para>A child this agent spawned is reaped with <c>waitpid</c> (exit code known). An adopted process belongs to launchd
/// after the previous agent exited, so it is watched by pid and start time (no exit code, as the contract allows).</para>
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacHostedProcess : IHostedProcess
{
    /// <summary>How often the exit is polled (macOS gives no wait handle for a pid; kqueue would, but polling is enough here).</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly bool _ownChild;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task<int?>? _watch;
    private bool _exited;
    private int? _exitCode;
    private bool _disposed;

    public MacHostedProcess(ProcessIdentity identity, string jobName, bool ownChild)
    {
        Identity = identity;
        JobName = jobName;
        _ownChild = ownChild;
    }

    public ProcessIdentity Identity { get; }

    public string JobName { get; }

    public bool HasExited
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Poll();
        }
    }

    public int? ExitCode => HasExited ? _exitCode : null;

    public Task<int?> WaitForExitAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _watch ??= WatchAsync(_stop.Token);
            return _watch.WaitAsync(ct);
        }
    }

    public async Task KillTreeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Poll())
        {
            return;
        }

        await MacProcessHost.TerminateAsync(Identity, Poll, _ownChild, ct).ConfigureAwait(false);
        await WaitForExitAsync(ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
        }

        _stop.Cancel();
        _stop.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<int?> WatchAsync(CancellationToken ct)
    {
        while (!Poll())
        {
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        return _exitCode;
    }

    /// <summary>True once the process has exited (reaping it when it is this agent's child).</summary>
    private bool Poll()
    {
        lock (_gate)
        {
            if (_exited)
            {
                return true;
            }

            if (_ownChild)
            {
                (bool reaped, int? code, int error) = MacNative.TryReap(Identity.Pid);
                if (reaped)
                {
                    _exitCode = code;
                    return _exited = true;
                }

                if (error != MacNative.ECHILD)
                {
                    return false;
                }

                // Not (or no longer) our child to reap: fall through to the identity check.
            }

            if (!MacProcessHost.IsAlive(Identity))
            {
                _exitCode = null;
                return _exited = true;
            }

            return false;
        }
    }
}
