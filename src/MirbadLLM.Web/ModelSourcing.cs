using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Multinode;

namespace MirbadLLM.Web;

/// <summary>
/// Checkpoint-folder inspection and the vLLM option catalog for the model editor and <c>/api/catalog</c>,
/// <c>/api/model-folders</c>, through the agent-backed services the master composition (MN-040) registers
/// (<see cref="RemoteModelFolders"/>, <see cref="RemoteVllmCatalogService"/>): the node's folders and images are read
/// through its agent. The pre-P1 local fallbacks (the master's own models root and Docker socket) were deleted by MN-047
/// (D60).
/// <para>A folder read that cannot reach the node (not serving, agent unreachable, no client yet) throws
/// <see cref="NodeUnavailableException"/> (<see cref="NodeUnavailableReason.NotServing"/>) instead of reading as "no
/// folders" (W4 review MN-096): the API answers 503 and the editor says why the list is empty. A node the status cache
/// knows is not serving fails at once, without waiting for a transport timeout. The catalog never fails: an unreachable
/// node gets its last reported images.</para>
/// </summary>
public static class ModelSourcing
{
    /// <summary>The inspection of <paramref name="folder"/> on <paramref name="nodeId"/> (null = the bootstrap node); null when not found.</summary>
    public static async Task<ModelFolderInspection?> InspectAsync(IServiceProvider services, string? nodeId, string folder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var remote = services.GetRequiredService<RemoteModelFolders>();
        string node = NodeOrBootstrap(services, nodeId);
        EnsureServing(services, node);
        try
        {
            return await remote.InspectAsync(node, folder, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            return null; // no such folder, or not a name the node accepts: not found
        }
        catch (InvalidOperationException ex) when (ex is not NodeUnavailableException)
        {
            throw Unavailable(node, ex);
        }
    }

    /// <summary>The checkpoint folders (with a readable <c>config.json</c>) on <paramref name="nodeId"/> (null = the bootstrap node).</summary>
    public static async Task<IReadOnlyList<ModelFolderSummary>> ListFoldersAsync(IServiceProvider services, string? nodeId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var remote = services.GetRequiredService<RemoteModelFolders>();
        string node = NodeOrBootstrap(services, nodeId);
        EnsureServing(services, node);
        try
        {
            return await remote.ListFoldersAsync(node, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex is not NodeUnavailableException)
        {
            throw Unavailable(node, ex);
        }
    }

    /// <summary>
    /// Every finished folder under the models root of <paramref name="nodeId"/> (null = the bootstrap node), with or without a
    /// <c>config.json</c>: a GGUF folder has none, so the llama.cpp editor lists these (design-p3p4 §A.7, MN-226). Unfinished
    /// downloads are left out.
    /// </summary>
    public static async Task<IReadOnlyList<ModelFolderEntry>> ListFolderEntriesAsync(IServiceProvider services, string? nodeId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var remote = services.GetRequiredService<RemoteModelFolders>();
        string node = NodeOrBootstrap(services, nodeId);
        EnsureServing(services, node);
        try
        {
            var entries = await remote.ListEntriesAsync(node, ct).ConfigureAwait(false);
            return [.. entries.Where(e => !e.Partial).OrderBy(e => e.Folder, StringComparer.OrdinalIgnoreCase)];
        }
        catch (InvalidOperationException ex) when (ex is not NodeUnavailableException)
        {
            throw Unavailable(node, ex);
        }
    }

    /// <summary>
    /// The GGUF header of <paramref name="file"/> in <paramref name="folder"/> on <paramref name="nodeId"/> (null file = the
    /// folder's only model GGUF), through <see cref="IAgentClient.InspectGgufAsync"/> (route <c>gguf.inspect</c>, MN-209). Never
    /// throws for an agent answer: a refusal comes back as <see cref="GgufRead.Error"/> in the agent's words, with the files
    /// to choose from when the folder holds several.
    /// </summary>
    public static async Task<GgufRead> InspectGgufAsync(IServiceProvider services, string? nodeId, string folder, string? file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string node = NodeOrBootstrap(services, nodeId);
        if (services.GetService<INodeStatusCache>()?.GetNode(node) is { IsServing: false } live)
        {
            return GgufRead.Failed($"Node {node} is not serving (state: {live.State}); the GGUF header cannot be read now.");
        }

        if (services.GetService<IAgentClientFactory>() is not { } clients)
        {
            return GgufRead.Failed("This master has no agent client, so the GGUF header cannot be read.");
        }

        try
        {
            var summary = await clients.GetClient(node).InspectGgufAsync(folder, string.IsNullOrWhiteSpace(file) ? null : file.Trim(), ct).ConfigureAwait(false);
            return new GgufRead(summary, null, []);
        }
        catch (AgentException ex)
        {
            string message = ex.Code switch
            {
                ErrorCodes.NotSupported => $"Node {node} cannot read GGUF headers (its agent does not offer {AgentFeatures.GgufInspect}).",
                ErrorCodes.GgufInvalid => $"Not a GGUF the reader accepts: {ex.Error.Message}",
                _ => ex.Error.Message,
            };
            // The structured list when the agent sends one (MN-230), else the message's own (an older agent).
            var candidates = ex.Error.ReadDetails<GgufChoiceDetails>()?.Candidates;
            return new GgufRead(null, message, candidates ?? LlamaCppEditing.CandidatesFrom(ex.Error.Message));
        }
        catch (Exception ex) when (ex is AgentUnreachableException or KeyNotFoundException or NotSupportedException or HttpRequestException)
        {
            return GgufRead.Failed($"The GGUF header on node {node} could not be read: {ex.Message}");
        }
    }

    /// <summary>The vLLM catalog for <paramref name="nodeId"/> (null = the bootstrap node).</summary>
    public static Task<VllmCatalog> GetCatalogAsync(IServiceProvider services, string? nodeId, string? currentImage, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetRequiredService<RemoteVllmCatalogService>().GetAsync(NodeOrBootstrap(services, nodeId), currentImage, ct);
    }

    /// <summary>Fails at once when the status cache knows <paramref name="nodeId"/> and it is not serving.</summary>
    private static void EnsureServing(IServiceProvider services, string nodeId)
    {
        if (services.GetService<INodeStatusCache>()?.GetNode(nodeId) is { IsServing: false } live)
        {
            throw new NodeUnavailableException($"Node {nodeId} is not serving (state: {live.State}); its model folders cannot be read now.")
            {
                Reason = NodeUnavailableReason.NotServing,
                NodeId = nodeId,
            };
        }
    }

    /// <summary>A folder read the node's agent could not answer (unreachable, no client, Docker or storage failure).</summary>
    private static NodeUnavailableException Unavailable(string nodeId, Exception cause) =>
        new($"The model folders on node {nodeId} could not be read: {cause.Message}", cause)
        {
            Reason = NodeUnavailableReason.NotServing,
            NodeId = nodeId,
        };

    private static string NodeOrBootstrap(IServiceProvider services, string? nodeId) =>
        !string.IsNullOrWhiteSpace(nodeId)
            ? nodeId
            : services.GetService<IOptions<RegistryMigrationOptions>>()?.Value.EffectiveBootstrapNodeId ?? RegistryMigrationOptions.DefaultBootstrapNodeId;
}

/// <summary>A GGUF header read for the editor (<see cref="ModelSourcing.InspectGgufAsync"/>).</summary>
/// <param name="Summary">The header; null when it could not be read.</param>
/// <param name="Error">Why not, in words for the page; null on success.</param>
/// <param name="Candidates">The model GGUFs of a folder that holds several (pick one); empty otherwise.</param>
public sealed record GgufRead(GgufSummary? Summary, string? Error, IReadOnlyList<string> Candidates)
{
    /// <summary>A read that failed with <paramref name="error"/>.</summary>
    public static GgufRead Failed(string error) => new(null, error, []);
}
