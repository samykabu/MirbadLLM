using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.UnitTests.Orchestration.Fakes;

namespace MirbadLLM.UnitTests.Orchestration;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

public sealed class ModelHealthMonitorTests
{
    [Fact]
    public async Task AutoStart_DockerFailureForOneModel_IsLogged_AndTheOthersStillStart()
    {
        var registry = new FakeModelRegistry()
            .Add(new ModelDefinition { Id = "broken", ServedName = "Broken", ModelPath = "p", MemoryBudgetGiB = 10, AutoStart = true })
            .Add(new ModelDefinition { Id = "fine", ServedName = "Fine", ModelPath = "p", MemoryBudgetGiB = 10, AutoStart = true });
        var runtime = new ThrowingStatusRuntime("broken");
        using var monitor = new ModelHealthMonitor(
            registry, runtime, Options.Create(new MirbadLLMOptions { HealthPollInterval = TimeSpan.FromHours(1) }),
            NullLogger<ModelHealthMonitor>.Instance);

        await monitor.StartAsync(TestContext.Current.CancellationToken);
        var started = await runtime.FineStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await runtime.BrokenAsked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        // Before the fix the Docker error escaped ExecuteAsync and faulted it (which stops the host).
        await Task.WhenAny(monitor.ExecuteTask!, Task.Delay(300, TestContext.Current.CancellationToken));

        Assert.Equal("fine", started);
        Assert.False(monitor.ExecuteTask!.IsFaulted, "the monitor must survive a Docker error during auto-start");
        await monitor.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AutoStart_StartsModelsOneAtATime_WaitingForEachToLeaveStarting()
    {
        var registry = new FakeModelRegistry()
            .Add(new ModelDefinition { Id = "a", ServedName = "A", ModelPath = "p", MemoryBudgetGiB = 10, AutoStart = true })
            .Add(new ModelDefinition { Id = "b", ServedName = "B", ModelPath = "p", MemoryBudgetGiB = 10, AutoStart = true });
        var runtime = new SlowStartRuntime(startingPolls: 3);
        ModelHealthMonitor.AutoStartPollInterval = TimeSpan.FromMilliseconds(10);
        using var monitor = new ModelHealthMonitor(
            registry, runtime, Options.Create(new MirbadLLMOptions { HealthPollInterval = TimeSpan.FromHours(1) }),
            NullLogger<ModelHealthMonitor>.Instance);

        await monitor.StartAsync(TestContext.Current.CancellationToken);
        await runtime.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await monitor.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, runtime.StartOrder.Count);
        Assert.False(runtime.OverlapSeen, "the second model was started while the first was still Starting");
    }

    [Fact]
    public async Task AutoStart_ReadsTheStatusAgainAfterWaiting_AndSendsNoStartForAModelThatTurnedOutRunning()
    {
        // MN-044 note, MN-047: after a master restart the first read can precede the node's first snapshot (Stopped);
        // by the time the node's start slot is free the snapshot shows the model running.
        var registry = new FakeModelRegistry()
            .Add(new ModelDefinition { Id = "phi", ServedName = "Phi", ModelPath = "p", MemoryBudgetGiB = 10, AutoStart = true });
        var runtime = new RunningAfterFirstReadRuntime();
        using var monitor = new ModelHealthMonitor(
            registry, runtime, Options.Create(new MirbadLLMOptions { HealthPollInterval = TimeSpan.FromHours(1) }),
            NullLogger<ModelHealthMonitor>.Instance);

        await monitor.StartAsync(TestContext.Current.CancellationToken);
        await runtime.SecondRead.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await monitor.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, runtime.Starts);
    }

    /// <summary>The first status read says Stopped (not synchronized yet), every later one Ready.</summary>
    private sealed class RunningAfterFirstReadRuntime : IModelRuntime
    {
        private int _reads;
        private int _starts;

        public int Starts => Volatile.Read(ref _starts);

        public TaskCompletionSource SecondRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ModelStatus>> ListStatusAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ModelStatus>>([]);

        public Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                return Task.FromResult(new ModelStatus(id, ModelState.Stopped));
            }

            SecondRead.TrySetResult();
            return Task.FromResult(new ModelStatus(id, ModelState.Ready));
        }

        public Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _starts);
            return Task.FromResult(new ModelStatus(id, ModelState.Starting));
        }

        public Task StopAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

#pragma warning disable CS1998
        public async IAsyncEnumerable<string> StreamLogsAsync(string id, int tail = 200, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
#pragma warning restore CS1998
    }

    /// <summary>A started model reports Starting for a few ListStatusAsync polls, then Ready.</summary>
    private sealed class SlowStartRuntime(int startingPolls) : IModelRuntime
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, int> _remaining = [];

        public List<string> StartOrder { get; } = [];

        public bool OverlapSeen { get; private set; }

        public TaskCompletionSource BothStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ModelStatus>> ListStatusAsync(CancellationToken ct = default)
        {
            lock (_gate)
            {
                var list = _remaining.Select(kv => new ModelStatus(kv.Key, kv.Value > 0 ? ModelState.Starting : ModelState.Ready)).ToList();
                foreach (var key in _remaining.Keys.ToList())
                {
                    _remaining[key] = Math.Max(0, _remaining[key] - 1);
                }

                return Task.FromResult<IReadOnlyList<ModelStatus>>(list);
            }
        }

        public Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult(new ModelStatus(id, _remaining.TryGetValue(id, out int left)
                    ? (left > 0 ? ModelState.Starting : ModelState.Ready)
                    : ModelState.Stopped));
            }
        }

        public Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default)
        {
            lock (_gate)
            {
                OverlapSeen |= _remaining.Values.Any(left => left > 0);
                _remaining[id] = startingPolls;
                StartOrder.Add(id);
                if (StartOrder.Count == 2)
                {
                    BothStarted.TrySetResult();
                }

                return Task.FromResult(new ModelStatus(id, ModelState.Starting));
            }
        }

        public Task StopAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

#pragma warning disable CS1998
        public async IAsyncEnumerable<string> StreamLogsAsync(string id, int tail = 200, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
#pragma warning restore CS1998
    }

    /// <summary>GetStatusAsync throws for one model id, as the Docker client does when the daemon is unreachable.</summary>
    private sealed class ThrowingStatusRuntime(string brokenId) : IModelRuntime
    {
        public ConcurrentBag<string> Started { get; } = [];

        public TaskCompletionSource<string> FineStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource BrokenAsked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ModelStatus>> ListStatusAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ModelStatus>>([]);

        public Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default)
        {
            if (id == brokenId)
            {
                BrokenAsked.TrySetResult();
                throw new HttpRequestException("The operation has timed out. (docker:80)");
            }

            return Task.FromResult(new ModelStatus(id, ModelState.Stopped));
        }

        public Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default)
        {
            Started.Add(id);
            FineStarted.TrySetResult(id);
            return Task.FromResult(new ModelStatus(id, ModelState.Starting));
        }

        public Task StopAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

#pragma warning disable CS1998
        public async IAsyncEnumerable<string> StreamLogsAsync(string id, int tail = 200, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
#pragma warning restore CS1998
    }
}
