using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Abstractions;

namespace MirbadLLM.Agent.Storage;

/// <summary>
/// Registers the storage package (MN-020) for the agent host (MN-026). Expects from the host: <see cref="IOperationLog"/>,
/// <see cref="IFencingState"/>, <see cref="IEventBus"/>, the runtime driver(s), <c>IHuggingFaceDownloader</c> and
/// <c>IHuggingFaceRepoInspector</c> (NodeRuntime, as today), and an <see cref="IContainerImageEngine"/> (normally
/// <see cref="DockerImageClient"/> over the Docker <see cref="HttpClient"/>).
/// </summary>
public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// Adds the storage services, contributors, recovery handler and the start-up helper cleanup.
    /// <paramref name="docker"/> (MN-232) is the node's Docker switch: with Docker off the capability contributor does not
    /// report <c>downloads.hf-container</c> and <see cref="DownloadHelperCleanup"/> is <b>not</b> registered, because its
    /// <c>StartAsync</c> waits on the Docker Engine and would keep the listener closed (design-p3p4 §A.4.1, D122). Null
    /// (every P1/P2 composition and every test that does not pass one) = Docker on, exactly as before.
    /// </summary>
    public static IServiceCollection AddAgentStorage(
        this IServiceCollection services, Action<AgentStorageOptions>? configure = null, Hosting.DockerAvailability? docker = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<AgentStorageOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDiskSpaceProbe, DriveInfoDiskSpaceProbe>();
        services.TryAddSingleton<ModelRootPaths>();
        services.TryAddSingleton<StorageOperations>();
        services.TryAddSingleton<AgentDownloadService>();
        services.TryAddSingleton<IAgentDownloads>(sp => sp.GetRequiredService<AgentDownloadService>());
        services.TryAddSingleton<AgentImageService>();
        services.TryAddSingleton<ModelFolderService>();
        services.TryAddSingleton<StorageOperationCanceller>();
        services.AddSingleton<Runtimes.IOperationCanceller>(sp => sp.GetRequiredService<StorageOperationCanceller>());
        services.TryAddSingleton(sp => new StorageCapabilities(
            sp.GetRequiredService<IContainerImageEngine>(),
            sp.GetRequiredService<ModelRootPaths>(),
            sp.GetRequiredService<IDiskSpaceProbe>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentStorageOptions>>(),
            docker));
        services.AddSingleton<ICapabilityContributor>(sp => sp.GetRequiredService<StorageCapabilities>());
        services.AddSingleton<IHealthContributor>(sp => sp.GetRequiredService<StorageCapabilities>());
        services.TryAddSingleton<StorageRecoveryHandler>();
        services.AddSingleton<IOperationRecoveryHandler>(sp => sp.GetRequiredService<StorageRecoveryHandler>());
        if (docker?.Enabled ?? true)
        {
            services.AddHostedService<DownloadHelperCleanup>();
        }

        return services;
    }
}

/// <summary>
/// Removes finished download helper containers when the agent starts (design §6.4: an exited helper keeps its
/// <c>HF_TOKEN</c> in Docker's container configuration until it is removed). Failures are logged, never fatal.
/// </summary>
public sealed class DownloadHelperCleanup(AgentDownloadService downloads, ILogger<DownloadHelperCleanup> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await downloads.RemoveFinishedHelpersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (AgentImageService.IsDockerFailure(ex))
        {
            StorageLog.FinishedHelpersCleanupFailed(logger, ex);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
