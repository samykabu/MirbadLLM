using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Web.Fakes;

public sealed class FakeModelEndpointResolver(IReadOnlyList<ModelEndpoint> endpoints) : IModelEndpointResolver
{
    public Task<ModelEndpoint?> ResolveAsync(string servedName, CancellationToken ct = default) =>
        Task.FromResult(endpoints.FirstOrDefault(e => string.Equals(e.ServedName, servedName, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<ModelEndpoint>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(endpoints);
}
