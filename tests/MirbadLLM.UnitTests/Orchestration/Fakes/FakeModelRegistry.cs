using System.Collections.Concurrent;
using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Orchestration.Fakes;

/// <summary>In-memory <see cref="IModelRegistry"/> fake so runtime/metrics/resolver tests don't need disk I/O.</summary>
internal sealed class FakeModelRegistry : IModelRegistry
{
    private readonly ConcurrentDictionary<string, ModelDefinition> _models = new();

    public FakeModelRegistry Add(ModelDefinition model)
    {
        _models[model.Id] = model;
        return this;
    }

    public Task<IReadOnlyList<ModelDefinition>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ModelDefinition>>(_models.Values.ToArray());

    public Task<ModelDefinition?> GetAsync(string id, CancellationToken ct = default)
        => Task.FromResult(_models.GetValueOrDefault(id));

    /// <summary>Thrown by every save (a disk or validation failure).</summary>
    public Exception? UpsertThrows { get; set; }

    public Task UpsertAsync(ModelDefinition model, CancellationToken ct = default)
    {
        if (UpsertThrows is { } error)
        {
            throw error;
        }

        _models[model.Id] = model;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
        => Task.FromResult(_models.TryRemove(id, out _));
}
