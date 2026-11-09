using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;

namespace MirbadLLM.Router;

/// <summary>
/// <c>GET /v1/models</c>: OpenAI list shape containing only Ready models. In the P2 maintenance order (only node-scoped
/// windows open, design §6.5 item 2) it never answers 503: it lists the Ready models that are serving now, leaving out the
/// ones a window pauses, as not-Ready models are left out. A global window is answered 503 by the gate in front of it.
/// Besides the OpenAI fields each entry carries <c>max_model_len</c> (context per request) and <c>capabilities</c>
/// (<c>chat</c>, <c>embeddings</c>, <c>tool_calling</c>, <c>vision</c>, see <see cref="ModelCapabilities"/>) from the
/// model's definition, so a client can pick models without a hand-written list; both are omitted when the definition is
/// unknown. A model tagged Reasoning is listed a second time as "&lt;served&gt;-nothink" (<see cref="NoThinking"/>).
/// </summary>
internal static class ModelsEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, IModelEndpointResolver resolver, CancellationToken ct)
    {
        var endpoints = await resolver.ListAsync(ct);
        var services = context.RequestServices;
        var registry = services.GetService<IModelRegistry>();
        var paused = await RouterMaintenance.PausedModelsAsync(
            services.GetService<IMaintenanceMode>(), RouterMaintenance.NodeScoped(services), registry, ct);
        var definitions = registry is null ? [] : (await registry.ListAsync(ct)).ToDictionary(m => m.Id, StringComparer.Ordinal);
        var served = endpoints.Select(e => e.ServedName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var data = endpoints
            .Where(e => e.State == ModelState.Ready && paused?.Invoke(e.ModelId) != true)
            .SelectMany(e =>
            {
                var definition = definitions.GetValueOrDefault(e.ModelId);
                var entry = new ModelListEntry(
                    e.ServedName,
                    "model",
                    0,
                    "mirbadllm",
                    definition is null ? null : ModelCapabilities.MaxModelLen(definition),
                    definition is null ? null : new ModelListCapabilities(
                        ModelCapabilities.IsChat(definition),
                        PoolingDetection.Detect(definition) == PoolingMode.Embedding,
                        ModelCapabilities.ToolCalling(definition),
                        ModelCapabilities.Vision(definition)));
                string alias = NoThinking.NameFor(e.ServedName);
                return definition is not null && NoThinking.Offered(definition) && !served.Contains(alias)
                    ? [entry, entry with { Id = alias }]
                    : new[] { entry };
            })
            .ToArray();

        return Results.Json(new ModelListResponse("list", data));
    }

    private sealed record ModelListResponse(
        [property: JsonPropertyName("object")] string Object,
        [property: JsonPropertyName("data")] IReadOnlyList<ModelListEntry> Data);

    private sealed record ModelListEntry(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("object")] string Object,
        [property: JsonPropertyName("created")] long Created,
        [property: JsonPropertyName("owned_by")] string OwnedBy,
        [property: JsonPropertyName("max_model_len"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxModelLen,
        [property: JsonPropertyName("capabilities"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ModelListCapabilities? Capabilities);

    private sealed record ModelListCapabilities(
        [property: JsonPropertyName("chat")] bool Chat,
        [property: JsonPropertyName("embeddings")] bool Embeddings,
        [property: JsonPropertyName("tool_calling")] bool ToolCalling,
        [property: JsonPropertyName("vision")] bool Vision);
}
