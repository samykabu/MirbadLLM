namespace MirbadLLM.Core;

/// <summary>Persistent store of <see cref="ModelDefinition"/>s.</summary>
public interface IModelRegistry
{
    Task<IReadOnlyList<ModelDefinition>> ListAsync(CancellationToken ct = default);
    Task<ModelDefinition?> GetAsync(string id, CancellationToken ct = default);
    Task UpsertAsync(ModelDefinition model, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}

/// <summary>Starts, stops and observes model containers.</summary>
public interface IModelRuntime
{
    Task<IReadOnlyList<ModelStatus>> ListStatusAsync(CancellationToken ct = default);
    Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default);

    /// <summary>Creates and starts the container. Returns once it is created (state <see cref="ModelState.Starting"/>).</summary>
    /// <exception cref="ModelMemoryAdmissionException">Budget exceeds available memory minus the reserve and <paramref name="force"/> is false.</exception>
    Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default);

    /// <summary>Stops and removes the container.</summary>
    Task StopAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Recreates the container with the saved definition (every vLLM setting is a launch argument). Admission
    /// counts the model's own current budget as released. The default implementation is stop, then start.
    /// </summary>
    /// <exception cref="ModelMemoryAdmissionException">The saved budget does not fit and <paramref name="force"/> is false.</exception>
    async Task<ModelStatus> RestartAsync(string id, bool force = false, CancellationToken ct = default)
    {
        await StopAsync(id, ct).ConfigureAwait(false);
        return await StartAsync(id, force, ct).ConfigureAwait(false);
    }

    IAsyncEnumerable<string> StreamLogsAsync(string id, int tail = 200, bool follow = true, CancellationToken ct = default);
}

public interface IMemoryProbe
{
    Task<MemorySnapshot> GetAsync(CancellationToken ct = default);
}

public interface IModelMetricsReader
{
    /// <summary>Null when the model is not ready or the scrape failed.</summary>
    Task<ModelMetrics?> GetAsync(string id, CancellationToken ct = default);
}

/// <summary>Used by the router to find the upstream for a served model name.</summary>
public interface IModelEndpointResolver
{
    Task<ModelEndpoint?> ResolveAsync(string servedName, CancellationToken ct = default);
    Task<IReadOnlyList<ModelEndpoint>> ListAsync(CancellationToken ct = default);
}

public interface IRouterStats
{
    IReadOnlyList<RouterModelStats> Snapshot();
}
