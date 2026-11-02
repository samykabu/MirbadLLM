using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Multinode;

namespace MirbadLLM.Web;

/// <summary>
/// The Services page's <see cref="IHostServiceManager"/> per node (design §9.1 "Services page per node"; MN-120): units and
/// containers of the selected node go through that node's agent, while admin re-authentication, the typed confirmations
/// and the container whitelist stay on the master, exactly as for the bootstrap node.
/// <see cref="INodeHostServiceManagers.ForNodeAsync"/> returns null when this master cannot reach that node's services (no
/// agent clients registered, e.g. a test host); the bootstrap node gets the registered <see cref="IHostServiceManager"/>.
/// Backups and restores use the same managers through <see cref="INodeHostServiceManagers"/>.
/// </summary>
public interface INodeHostServices : INodeHostServiceManagers;

/// <summary>
/// The master's <see cref="INodeHostServices"/>, a DI singleton (W7 review MN-126; registered by the composition with
/// <c>services.AddSingleton&lt;INodeHostServices, NodeHostServices&gt;()</c>). The bootstrap node keeps the registered
/// manager and its <c>host-services.json</c> (backups read that file). Every other node gets one
/// <see cref="HostServiceManager"/> over <see cref="RemoteHostUnitBackend"/>/<see cref="RemoteHostContainerBackend"/>,
/// created on first use and kept for the life of this instance, so the container backend's memory of seen containers and
/// the store's lock are shared by every browser tab, with its own whitelist file <c>{DataPath}/host-services.{nodeId}.json</c>.
/// That file starts with an empty container list: the built-in defaults (OpenViking, n8n, …) describe the DGX, not a new
/// node. Disposing the instance (the container does, at shutdown) disposes those stores.
/// </summary>
/// <param name="services">The root provider (optional services: agent clients, the operation runner, options).</param>
public sealed partial class NodeHostServices(IServiceProvider services) : INodeHostServices, IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<Created>>> _managers = new(StringComparer.Ordinal);

    /// <summary>The container whitelist file of a non-bootstrap node, relative to <c>DataPath</c>.</summary>
    public static string StoreFileName(string nodeId) => NodeHostServicesFiles.FileName(nodeId ?? "");

    /// <inheritdoc />
    public async Task<IHostServiceManager?> ForNodeAsync(string nodeId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        string bootstrap = services.GetService<IOptions<RegistryMigrationOptions>>()?.Value.EffectiveBootstrapNodeId
            ?? RegistryMigrationOptions.DefaultBootstrapNodeId;
        if (string.Equals(nodeId, bootstrap, StringComparison.Ordinal))
        {
            return services.GetService<IHostServiceManager>();
        }

        var clients = services.GetService<IAgentClientFactory>();
        var runner = services.GetService<AgentOperationRunner>();
        var options = services.GetService<IOptions<MirbadLLMOptions>>();
        if (clients is null || runner is null || options is null || !NodeIdPattern().IsMatch(nodeId))
        {
            return null;
        }

        var lazy = _managers.GetOrAdd(nodeId, id => new Lazy<Task<Created>>(() => CreateAsync(id, clients, runner, options.Value.DataPath)));
        try
        {
            return (await lazy.Value.WaitAsync(ct).ConfigureAwait(false)).Manager;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed creation (unwritable data directory) is not cached: the next refresh tries again.
            _managers.TryRemove(new KeyValuePair<string, Lazy<Task<Created>>>(nodeId, lazy));
            throw;
        }
    }

    /// <summary>Disposes the per-node whitelist stores created so far.</summary>
    public void Dispose()
    {
        foreach (Lazy<Task<Created>> lazy in _managers.Values)
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
            {
                lazy.Value.Result.Store.Dispose();
            }
        }

        _managers.Clear();
    }

    private async Task<Created> CreateAsync(string nodeId, IAgentClientFactory clients, AgentOperationRunner runner, string dataPath)
    {
        var store = new HostServicesStore(Path.Combine(dataPath, StoreFileName(nodeId)));
        try
        {
            if (!File.Exists(store.FilePath))
            {
                // Persist an empty list now, so the first read does not seed the DGX's default containers.
                await store.UpdateContainersAsync([], _ => [], CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            store.Dispose();
            throw;
        }

        return new Created(store, new HostServiceManager(
            new RemoteHostUnitBackend(nodeId, clients, runner),
            new RemoteHostContainerBackend(nodeId, clients, runner),
            store,
            services.GetService<IOptions<HostServicesOptions>>() ?? Options.Create(new HostServicesOptions()),
            services.GetService<ILogger<HostServiceManager>>() ?? NullLogger<HostServiceManager>.Instance,
            services.GetService<IAdminReauthenticator>()));
    }

    private sealed record Created(HostServicesStore Store, IHostServiceManager Manager);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex NodeIdPattern();
}
