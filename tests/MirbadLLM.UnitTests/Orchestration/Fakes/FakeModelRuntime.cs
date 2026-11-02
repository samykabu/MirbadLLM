using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Orchestration.Fakes;

/// <summary>Hand-written fake <see cref="IModelRuntime"/>: statuses are set directly, per model id.</summary>
internal sealed class FakeModelRuntime : IModelRuntime
{
    private readonly ConcurrentDictionary<string, ModelStatus> _statuses = new(StringComparer.Ordinal);

    public FakeModelRuntime Set(ModelStatus status)
    {
        _statuses[status.Id] = status;
        return this;
    }

    public Task<IReadOnlyList<ModelStatus>> ListStatusAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ModelStatus>>(_statuses.Values.ToArray());

    public Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default)
        => Task.FromResult(_statuses.GetValueOrDefault(id) ?? new ModelStatus(id, ModelState.Stopped));

    public Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default)
        => Task.FromResult(_statuses.GetValueOrDefault(id) ?? new ModelStatus(id, ModelState.Starting));

    public Task StopAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

#pragma warning disable CS1998
    public async IAsyncEnumerable<string> StreamLogsAsync(string id, int tail = 200, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield break;
    }
#pragma warning restore CS1998
}
