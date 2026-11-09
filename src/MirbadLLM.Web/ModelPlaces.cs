using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;

namespace MirbadLLM.Web;

/// <summary>
/// Where one served model runs, for the pages that hand a model out or let you try it (Access, Playground): its
/// served name and state as the router sees it, plus the node it is placed on and the runtime that serves it.
/// </summary>
/// <param name="ModelId">The registered model's id.</param>
/// <param name="ServedName">What API clients send as <c>model</c>.</param>
/// <param name="State">The router's view: never Ready unless traffic can reach it.</param>
/// <param name="NodeId">The node it is placed on (an unplaced, pre-fleet model counts as the bootstrap node's).</param>
/// <param name="NodeName">That node's display name, or its id.</param>
/// <param name="Runtime">The runtime serving it; null when the definition is not known here.</param>
/// <param name="MaxModelLen">Context length in tokens (llama.cpp: per-request context), 32,768 when unknown.</param>
/// <param name="AcceptsImages">Tagged Vision or Multimodal, so the Playground offers to attach images.</param>
/// <param name="Thinks">Tagged Reasoning, so it is also served as "&lt;served&gt;-nothink" (<see cref="NoThinking"/>).</param>
public sealed record ModelPlace(
    string ModelId,
    string ServedName,
    ModelState State,
    string NodeId,
    string NodeName,
    RuntimeKind? Runtime,
    int MaxModelLen,
    bool AcceptsImages = false,
    bool Thinks = false)
{
    /// <summary>"vLLM", "llama.cpp", "MLX", or null when unknown.</summary>
    public string? RuntimeName => Runtime is { } kind ? LlamaCppEditing.RuntimeName(kind) : null;

    /// <summary>"dev-b · llama.cpp" (just the node when the runtime is unknown).</summary>
    public string Where => RuntimeName is { } runtime ? $"{NodeName} · {runtime}" : NodeName;

    public bool IsReady => State == ModelState.Ready;
}

public static class ModelPlaces
{
    /// <summary>Default context length when a model's definition is not available.</summary>
    public const int DefaultContext = 32_768;

    /// <summary>
    /// Every served model with its node and runtime, ordered by served name. The model registry and the node registry are
    /// optional (minimal hosts and tests register only the endpoint resolver); without them the node is the bootstrap
    /// node and the runtime is unknown.
    /// </summary>
    public static async Task<List<ModelPlace>> LoadAsync(IServiceProvider services, IModelEndpointResolver resolver, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(resolver);

        var endpoints = await resolver.ListAsync(ct);
        var registry = services.GetService<IModelRegistry>();
        var definitions = registry is null ? [] : await registry.ListAsync(ct);
        var byId = definitions.GroupBy(d => d.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        string defaultNode = services.GetService<IOptions<RegistryMigrationOptions>>()?.Value.EffectiveBootstrapNodeId
            ?? RegistryMigrationOptions.DefaultBootstrapNodeId;
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (services.GetService<INodeRegistry>() is { } nodes)
        {
            try
            {
                foreach (var node in await nodes.ListAsync(ct))
                {
                    names[node.Id] = string.IsNullOrWhiteSpace(node.DisplayName) ? node.Id : node.DisplayName;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Node names are a nicety: ids still identify the node.
            }
        }

        return [.. endpoints
            .Select(e =>
            {
                var definition = byId.GetValueOrDefault(e.ModelId);
                string nodeId = definition?.Placement?.NodeId ?? defaultNode;
                int context = definition is null ? DefaultContext : ModelCapabilities.MaxModelLen(definition);
                bool images = definition is not null && ModelCapabilities.Vision(definition);
                bool thinks = definition is not null && NoThinking.Offered(definition);
                return new ModelPlace(e.ModelId, e.ServedName, e.State, nodeId, names.GetValueOrDefault(nodeId, nodeId), definition?.Runtime, context, images, thinks);
            })
            .OrderBy(p => p.ServedName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>"Ready", "Starting", "Stopped"… as a short phrase for a model that cannot take a request now.</summary>
    public static string StateWord(ModelState state) => state switch
    {
        ModelState.Ready => "Ready",
        ModelState.Starting => "Starting",
        ModelState.Stopping => "Stopping",
        ModelState.Failed => "Failed",
        _ => "Stopped",
    };
}
