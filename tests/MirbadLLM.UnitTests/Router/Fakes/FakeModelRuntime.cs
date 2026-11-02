using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Router.Fakes;

/// <summary>Hand-written fake of <see cref="IModelRuntime"/>. Only <see cref="StartAsync"/> and
/// <see cref="GetStatusAsync"/> are exercised by the router; the rest are trivial stubs.</summary>
internal sealed class FakeModelRuntime : IModelRuntime
{
    private readonly ConcurrentDictionary<string, ModelStatus> _statuses = new();

    /// <summary>Number of times <see cref="StartAsync"/> was called.</summary>
    public int StartCallCount { get; private set; }

    /// <summary>Overrides the default StartAsync behaviour (which just flips the status to Starting).</summary>
    public Func<string, bool, ModelStatus>? StartHandler { get; set; }

    public void SetStatus(string id, ModelStatus status) => _statuses[id] = status;

    public Task<IReadOnlyList<ModelStatus>> ListStatusAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ModelStatus>>(_statuses.Values.ToArray());

    public Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default)
        => Task.FromResult(_statuses.TryGetValue(id, out var status) ? status : new ModelStatus(id, ModelState.Stopped));

    public Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default)
    {
        StartCallCount++;
        var result = StartHandler is not null ? StartHandler(id, force) : new ModelStatus(id, ModelState.Starting);
        _statuses[id] = result;
        return Task.FromResult(result);
    }

    public Task StopAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

#pragma warning disable CS1998 // no async work needed; the signature must match IModelRuntime
    public async IAsyncEnumerable<string> StreamLogsAsync(string id, int tail = 200, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield break;
    }
#pragma warning restore CS1998
}
