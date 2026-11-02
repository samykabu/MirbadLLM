using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Pki;
using MirbadLLM.Pki;

namespace MirbadLLM.Orchestration.Recovery;

public static class BackupServiceCollectionExtensions
{
    /// <summary>
    /// Registers backups and restores: <see cref="BackupService"/> (schema v2: master identity, nodes, per-node Services
    /// lists, the CA in the encrypted section when asked), the scheduled-backup directory and daily
    /// <see cref="BackupScheduler"/> (bound from <c>MirbadLLM:Backup</c>), the one-time download tokens, the restore
    /// preview and the background <see cref="RestoreJobService"/>. Checkpoint folders are the bootstrap node's, read through
    /// its agent (<see cref="RemoteCheckpointFolders"/>); the backup destination mounts through it too. Call after
    /// <c>AddMirbadLLMOrchestration</c> and <c>AddMirbadLLMHostServices</c>.
    /// <para>P2 (MN-130): the restore planner and job get the node registry (the uid rule, the target picker), per-node
    /// folders and downloaders (<see cref="AgentRestoreNodeResources"/>), the audit log and the ownership store, and
    /// <see cref="FleetRecovery"/> is registered for the Restore tab (active only in recovery mode; its restart callback
    /// stops the host, whose restart policy starts it in normal mode).</para>
    /// </summary>
    public static IServiceCollection AddMirbadLLMBackup(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BackupOptions>()
            .Bind(configuration.GetSection(BackupOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ICheckpointFolders>(sp => new RemoteCheckpointFolders(
            ServiceCollectionExtensions.BootstrapNodeId(sp),
            sp.GetRequiredService<IAgentClientFactory>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<BackupDirectory>();
        // The backup destination (NAS share) is mounted by the bootstrap node's agent through hostd (topology a). Its applied
        // settings, password encrypted, are kept on the master so passphrase backups carry them.
        services.AddSingleton<IBackupDestinationManager>(sp => new BackupDestinationManager(
            sp.GetService<IHostAgentBackupMountClient>(),
            sp.GetRequiredService<BackupDirectory>(),
            sp.GetRequiredService<ILogger<BackupDestinationManager>>(),
            sp.GetService<IAdminReauthenticator>(),
            DataPath(sp) is { } data && sp.GetService<IDataProtectionProvider>() is { } protection
                ? new BackupDestinationSecretStore(data, protection)
                : null));
        services.AddSingleton(sp => new BackupService(
            sp.GetRequiredService<IModelRegistry>(),
            sp.GetRequiredService<IApiKeyStore>(),
            sp.GetRequiredService<IOptions<BackupOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<HostServicesStore>(),
            sp.GetService<IBackupDestinationManager>(),
            sp.GetService<IMasterIdentity>(),
            sp.GetService<INodeRegistry>(),
            sp.GetService<CertificateAuthority>(),
            ServiceCollectionExtensions.BootstrapNodeId(sp),
            // The CA a backup carries is the current issuing one (the new CA after a CA rotation's step 3, MN-126).
            sp.GetService<MasterCertificates>(),
            sp.GetService<ITokenStore>(),
            sp.GetService<ModelRecoveryState>(),
            sp.GetService<AgentUpdateSettingsStore>(),
            DataPath(sp),
            sp.GetService<StartupTimeTracker>(),
            sp.GetService<INodeHostServiceManagers>()));
        services.AddSingleton<BackupDownloadTokens>();
        services.AddSingleton(sp => new BackupScheduler(
            sp.GetRequiredService<BackupService>(),
            sp.GetRequiredService<BackupDirectory>(),
            sp.GetRequiredService<IOptions<BackupOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<BackupScheduler>>(),
            sp.GetService<IBackupDestinationManager>()));
        services.AddHostedService(sp => sp.GetRequiredService<BackupScheduler>());

        // Per-node folders and downloads of a configuration import; the bootstrap node keeps its registered instances.
        services.AddSingleton<IRestoreNodeResources>(sp => new AgentRestoreNodeResources(
            ServiceCollectionExtensions.BootstrapNodeId(sp),
            sp.GetRequiredService<ICheckpointFolders>(),
            sp.GetRequiredService<IHuggingFaceDownloader>(),
            sp.GetRequiredService<IAgentClientFactory>(),
            sp.GetRequiredService<AgentOperationRunner>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new RestorePlanner(
            sp.GetRequiredService<IModelRegistry>(),
            sp.GetRequiredService<IApiKeyStore>(),
            sp.GetRequiredService<ICheckpointFolders>(),
            sp.GetService<IHuggingFaceRepoInspector>(),
            ServiceCollectionExtensions.BootstrapNodeId(sp),
            sp.GetRequiredService<INodeRegistry>(),
            sp.GetRequiredService<IRestoreNodeResources>()));
        services.AddSingleton(new RestoreJobOptions());
        services.AddSingleton(sp => new RestoreJobService(
            sp.GetRequiredService<IModelRegistry>(),
            sp.GetRequiredService<IApiKeyStore>(),
            sp.GetRequiredService<IModelRuntime>(),
            sp.GetRequiredService<IHuggingFaceDownloader>(),
            sp.GetRequiredService<ICheckpointFolders>(),
            sp.GetRequiredService<RestoreJobOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<RestoreJobService>>(),
            sp.GetService<HostServicesStore>(),
            ServiceCollectionExtensions.BootstrapNodeId(sp),
            nodes: sp.GetRequiredService<INodeRegistry>(),
            nodeResources: sp.GetRequiredService<IRestoreNodeResources>(),
            audit: sp.GetService<IAuditLog>(),
            ownership: sp.GetService<IOwnershipStore>(),
            tokens: sp.GetService<ITokenStore>(),
            destinations: sp.GetService<IBackupDestinationManager>(),
            agentUpdates: sp.GetService<AgentUpdateSettingsStore>(),
            dataPath: DataPath(sp),
            startupTimes: sp.GetService<StartupTimeTracker>(),
            hostServiceManagers: sp.GetService<INodeHostServiceManagers>()));

        // Fleet disaster recovery (design §9.4, MN-113): active only when the master booted in recovery mode.
        services.AddSingleton(sp => new FleetRecovery(
            sp.GetRequiredService<IOptions<MirbadLLMOptions>>(),
            sp.GetService<IOptions<RecoveryOptions>>(),
            sp.GetService<IOptions<MasterPkiOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<FleetRecovery>>(),
            sp.GetService<IAuditLog>(),
            ServiceCollectionExtensions.BootstrapNodeId(sp),
            protection: sp.GetService<IDataProtectionProvider>(),
            requestRestart: sp.GetService<IHostApplicationLifetime>() is { } lifetime ? lifetime.StopApplication : null));

        // Restore records left by a restore that a restart interrupted are released at boot (W7 review MN-126).
        services.AddHostedService(sp => new RestoreOwnershipRecovery(
            sp.GetRequiredService<RestoreJobService>(), sp.GetRequiredService<ILogger<RestoreOwnershipRecovery>>()));

        return services;
    }

    private static string? DataPath(IServiceProvider sp) =>
        sp.GetService<IOptions<MirbadLLMOptions>>()?.Value.DataPath is { Length: > 0 } data ? data : null;
}
