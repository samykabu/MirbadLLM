using System.Collections.Concurrent;
using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Router.Fakes;

/// <summary>Hand-written fake of <see cref="IModelEndpointResolver"/> keyed by served name (case-insensitive).</summary>
internal sealed class FakeModelEndpointResolver : IModelEndpointResolver
{
    private readonly ConcurrentDictionary<string, ModelEndpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);

    public void Set(ModelEndpoint endpoint) => _endpoints[endpoint.ServedName] = endpoint;

    public Task<ModelEndpoint?> ResolveAsync(string servedName, CancellationToken ct = default)
        => Task.FromResult(_endpoints.TryGetValue(servedName, out var endpoint) ? endpoint : null);

    public Task<IReadOnlyList<ModelEndpoint>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ModelEndpoint>>(_endpoints.Values.ToArray());
}
