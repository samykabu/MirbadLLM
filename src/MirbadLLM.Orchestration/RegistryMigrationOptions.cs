using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration;

/// <summary>
/// Settings of the one-time <c>models.json</c> v1 → v2 migration (design §5.4), bound from the <c>MirbadLLM</c>
/// section: <c>MirbadLLM:Nodes:BootstrapNodeId</c> and <c>MirbadLLM:Migration:KeepServingDuringJobs</c>.
/// Read by <see cref="JsonModelRegistry"/>; until the host binds them (<see cref="AddRegistryMigrationOptions"/>)
/// the defaults apply.
/// </summary>
public sealed partial class RegistryMigrationOptions
{
    /// <summary>The bootstrap node id the design decided on (§5.4 step 1).</summary>
    public const string DefaultBootstrapNodeId = "dgx";

    /// <summary>
    /// OpenViking's models on the DGX (design §5.4 step 2, §6.5): they keep serving while a benchmark or tuning job
    /// holds the node. Applied by the v1 migration only.
    /// </summary>
    public static IReadOnlyList<string> DefaultKeepServingDuringJobs { get; } =
        ["qwen3-embedding-4b", "qwen3-vl-embedding-2b", "ov-intent-analysis"];

    /// <summary><c>MirbadLLM:Nodes</c>.</summary>
    public NodesSettings Nodes { get; set; } = new();

    /// <summary><c>MirbadLLM:Migration</c>.</summary>
    public MigrationSettings Migration { get; set; } = new();

    /// <summary>The bootstrap node id to place migrated and seeded models on; validated against the node id rule (§3.1).</summary>
    public string EffectiveBootstrapNodeId
    {
        get
        {
            string id = string.IsNullOrWhiteSpace(Nodes.BootstrapNodeId) ? DefaultBootstrapNodeId : Nodes.BootstrapNodeId.Trim();
            if (!NodeIdRegex().IsMatch(id))
            {
                throw new InvalidOperationException(
                    $"MirbadLLM:Nodes:BootstrapNodeId '{id}' is not a valid node id ({Protocol.NodeIdPattern}).");
            }

            return id;
        }
    }

    /// <summary>
    /// The model ids that get <see cref="ModelDefinition.KeepServingDuringJobs"/> in the v1 migration: the configured
    /// list when one is bound (blank entries ignored, so a single blank entry means "none"), else the default.
    /// </summary>
    public IReadOnlySet<string> EffectiveKeepServingDuringJobs =>
        (Migration.KeepServingDuringJobs ?? [.. DefaultKeepServingDuringJobs])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Binds these options from the <c>MirbadLLM</c> section. Called by the master composition (MN-040); not by
    /// <c>AddMirbadLLMOrchestration</c> today, so the defaults apply until it is.
    /// </summary>
    public static IServiceCollection AddRegistryMigrationOptions(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<RegistryMigrationOptions>().Bind(configuration.GetSection(MirbadLLMOptions.SectionName));
        return services;
    }

    [GeneratedRegex(Protocol.NodeIdPattern, RegexOptions.CultureInvariant)]
    private static partial Regex NodeIdRegex();

    /// <summary><c>MirbadLLM:Nodes</c> settings used by the registry.</summary>
    public sealed class NodesSettings
    {
        /// <summary><c>MirbadLLM:Nodes:BootstrapNodeId</c>; null or blank = <see cref="DefaultBootstrapNodeId"/>.</summary>
        public string? BootstrapNodeId { get; set; }
    }

    /// <summary><c>MirbadLLM:Migration</c> settings.</summary>
    public sealed class MigrationSettings
    {
        /// <summary>
        /// <c>MirbadLLM:Migration:KeepServingDuringJobs</c>: model ids. Null (not configured) =
        /// <see cref="DefaultKeepServingDuringJobs"/>. Kept null by default because the configuration binder appends
        /// to a pre-filled list instead of replacing it.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Bound from configuration.")]
        public List<string>? KeepServingDuringJobs { get; set; }
    }
}
