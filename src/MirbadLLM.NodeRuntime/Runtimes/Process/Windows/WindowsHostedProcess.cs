using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.NodeRuntime.Runtimes.Process.Windows;

/// <summary>
/// A process started or adopted by <see cref="WindowsProcessHost"/>: its process handle and the handle of the per-instance
/// job it is in (design-p3p4 §A.1.3). The job is never kill-on-close, so disposing (closing both handles) leaves the
/// process running; only <see cref="KillTreeAsync"/> ends it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsHostedProcess : IHostedProcess
{
    /// <summary>Exit code given to <c>TerminateJobObject</c> / <c>TerminateProcess</c>.</summary>
    public const uint KilledExitCode = 1;

    private readonly SafeProcessHandle _process;
    private readonly WindowsNative.SafeKernelHandle _job;
    private readonly object _gate = new();
    private TaskCompletionSource<int?>? _exit;
    private RegisteredWaitHandle? _registration;
    private ProcessExitWaitHandle? _waitHandle;
    private bool _disposed;

    public WindowsHostedProcess(SafeProcessHandle process, WindowsNative.SafeKernelHandle job, ProcessIdentity identity, string jobName)
    {
        _process = process;
        _job = job;
        Identity = identity;
        JobName = jobName;
    }

    public ProcessIdentity Identity { get; }

    public string JobName { get; }

    public bool HasExited
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return WindowsNative.WaitForSingleObject(_process, 0) == WindowsNative.WAIT_OBJECT_0;
        }
    }

    public int? ExitCode => HasExited && WindowsNative.GetExitCodeProcess(_process, out uint code) ? unchecked((int)code) : null;

    /// <summary>True when the process is in the job this instance holds (<c>IsProcessInJob</c>; tests and diagnostics).</summary>
    internal bool IsInOwnJob()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return WindowsNative.IsProcessInJob(_process, _job, out bool inJob) ? inJob : throw WindowsNative.LastError("IsProcessInJob");
    }

    public Task<int?> WaitForExitAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_exit is null)
            {
                _exit = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waitHandle = new ProcessExitWaitHandle(_process);
                _registration = ThreadPool.RegisterWaitForSingleObject(_waitHandle, static (state, _) =>
                {
                    var self = (WindowsHostedProcess)state!;
                    self._exit!.TrySetResult(self.ReadExitCode());
                }, this, Timeout.Infinite, executeOnlyOnce: true);
            }

            return _exit.Task.WaitAsync(ct);
        }
    }

    public async Task KillTreeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (HasExited)
        {
            return;
        }

        if (!WindowsNative.TerminateJobObject(_job, KilledExitCode))
        {
            var jobError = WindowsNative.LastError("TerminateJobObject");
            // The process itself at least; a process that is already exiting refuses both, so give it a moment.
            if (!WindowsNative.TerminateProcess(_process, KilledExitCode)
                && !await WindowsProcessHost.WaitSignaledAsync(_process, WindowsProcessHost.ExitGrace, ct).ConfigureAwait(false))
            {
                throw jobError;
            }
        }

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
            _registration?.Unregister(null);
            _waitHandle?.Dispose();
            _exit?.TrySetException(new ObjectDisposedException(nameof(WindowsHostedProcess)));
        }

        _job.Dispose();
        _process.Dispose();
        return ValueTask.CompletedTask;
    }

    private int? ReadExitCode()
    {
        try
        {
            return WindowsNative.GetExitCodeProcess(_process, out uint code) ? unchecked((int)code) : null;
        }
        catch (ObjectDisposedException)
        {
            // Disposed while the wait callback ran; the task already faulted with ObjectDisposedException.
            return null;
        }
    }

    /// <summary>A wait handle over the process handle (not owned: the process handle is closed by its own SafeHandle).</summary>
    private sealed class ProcessExitWaitHandle : WaitHandle
    {
        private readonly SafeProcessHandle _owner;
        private bool _addedRef;

        public ProcessExitWaitHandle(SafeProcessHandle process)
        {
            _owner = process;
            process.DangerousAddRef(ref _addedRef);
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
        }

        protected override void Dispose(bool explicitDisposing)
        {
            base.Dispose(explicitDisposing);
            if (_addedRef)
            {
                _addedRef = false;
                _owner.DangerousRelease();
            }
        }
    }
}
