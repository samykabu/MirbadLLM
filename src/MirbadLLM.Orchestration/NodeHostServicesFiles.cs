using System.Text.RegularExpressions;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration;

/// <summary>
/// Each node's Services manager (the Services page's <c>MirbadLLM.Web.INodeHostServices</c>), seen from Orchestration so
/// a backup can read every node's managed systemd/SCM units and a restore can add them back through the same manager, with
/// the same admin session check as the Services page.
/// </summary>
public interface INodeHostServiceManagers
{
    /// <summary>The manager for <paramref name="nodeId"/>, or null when this master cannot reach that node's services.</summary>
    Task<IHostServiceManager?> ForNodeAsync(string nodeId, CancellationToken ct = default);
}

/// <summary>
/// The per-node Services whitelist files on the master (design §9.1 "Services page per node", MN-120):
/// <c>{DataPath}/host-services.{nodeId}.json</c> for every node but the bootstrap node, whose lists stay in the P1 file
/// <c>host-services.json</c>. Shared by the Services page (<c>MirbadLLM.Web.NodeHostServices</c>), the backup
/// (<c>BackupService</c>, design §5.5 per-node <c>hostServices</c>) and both restore modes, so the name rule lives once.
/// </summary>
public static partial class NodeHostServicesFiles
{
    /// <summary>The bootstrap node's file (P1 name, unchanged).</summary>
    public const string BootstrapFileName = "host-services.json";

    private const string Prefix = "host-services.";
    private const string Suffix = ".json";

    /// <summary><c>host-services.{nodeId}.json</c>; <see cref="ArgumentException"/> for an invalid node id.</summary>
    public static string FileName(string nodeId)
    {
        if (!IsNodeId(nodeId))
        {
            throw new ArgumentException($"'{nodeId}' is not a node id.", nameof(nodeId));
        }

        return Prefix + nodeId + Suffix;
    }

    /// <summary>The node ids that have a per-node file in <paramref name="dataPath"/>, sorted; none when the directory is missing.</summary>
    public static IReadOnlyList<string> ListNodeIds(string dataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        if (!Directory.Exists(dataPath))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(dataPath, Prefix + "*" + Suffix)
            .Select(path => Path.GetFileName(path))
            .Where(name => name.Length > Prefix.Length + Suffix.Length) // never host-services.json itself (Windows wildcard rules)
            .Select(name => name[Prefix.Length..^Suffix.Length])
            .Where(IsNodeId)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Writes <paramref name="lists"/> into <paramref name="nodeId"/>'s file under <paramref name="dataPath"/> (the import
    /// rules of <see cref="HostServicesStore.ImportAsync"/>). A file that did not exist starts with an empty container list,
    /// so a backup entry without a list never lets the first read seed the DGX's default containers. Returns the containers
    /// now listed.
    /// </summary>
    public static async Task<int> ImportAsync(string dataPath, string nodeId, HostServicesBackup lists, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lists);
        using var store = new HostServicesStore(Path.Combine(dataPath, FileName(nodeId)));
        if (!File.Exists(store.FilePath))
        {
            await store.UpdateContainersAsync([], _ => [], ct).ConfigureAwait(false);
        }

        await store.ImportAsync(lists, ct).ConfigureAwait(false);
        return (await store.ExportAsync(ct).ConfigureAwait(false)).Containers?.Count ?? 0;
    }

    /// <summary>True for a node id slug (<c>^[a-z0-9][a-z0-9-]{0,39}$</c>).</summary>
    public static bool IsNodeId(string? nodeId) => nodeId is not null && NodeIdPattern().IsMatch(nodeId);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex NodeIdPattern();
}
