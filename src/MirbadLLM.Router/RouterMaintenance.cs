using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Router;

/// <summary>
/// The router's maintenance precedence switch (design §6.5 item 2, MN-110), bound from <c>MirbadLLM:Router:Maintenance</c>.
/// </summary>
public sealed class RouterMaintenanceOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "MirbadLLM:Router:Maintenance";

    /// <summary>The one configuration key of the switch, read here and by the composition for <c>ModelJobService</c>.</summary>
    public const string NodeScopedKey = SectionName + ":" + nameof(NodeScoped);

    /// <summary>
    /// False (default, P1): every window is treated as global (the P1 order and responses exactly), and
    /// <c>ModelJobService</c> opens global windows (no node) — composition reads this same key for it — so a job on the single
    /// DGX answers exactly as before P2. True (P2, MN-130 composition): the P2 order applies while every open window is node-scoped — 401 API key → 413 → 400
    /// <c>model_required</c> → 404 <c>model_not_found</c> → 503 <c>maintenance</c> (model's node in a job, model not exempt)
    /// → 503 node states → 503 <c>model_not_ready</c>, and <c>GET /v1/models</c> leaves paused models out instead of
    /// answering 503. A global window (<see cref="MaintenanceState.IsGlobal"/>: P1 jobs, the recovery-mode "restoring"
    /// window) keeps the P1 order whatever this says.
    /// </summary>
    public bool NodeScoped { get; set; }
}

/// <summary>The router's maintenance steps (design §6.5 item 2): the P1 gate in front of the API key and the P2 per-model check.</summary>
internal static class RouterMaintenance
{
    /// <summary>
    /// The window the P1 gate answers with before the API-key check, or null to let the request through (no window, or
    /// only node-scoped windows with <see cref="RouterMaintenanceOptions.NodeScoped"/> on: the P2 order then checks each
    /// model after resolution). A global window wins; otherwise the oldest (<see cref="IMaintenanceMode.Current"/>).
    /// </summary>
    public static MaintenanceState? GateWindow(IMaintenanceMode? maintenance, bool nodeScoped)
    {
        if (maintenance is null)
        {
            return null;
        }

        var active = maintenance.Active;
        if (active.Count == 0)
        {
            return null;
        }

        var global = active.FirstOrDefault(w => w.IsGlobal);
        if (global is not null)
        {
            return global;
        }

        return nodeScoped ? null : maintenance.Current ?? active[0];
    }

    /// <summary>
    /// The P2 per-model step: the window pausing <paramref name="modelId"/>, or null. Null whenever node-scoped maintenance
    /// is off (the P1 gate already answered for every window). With a registry the model's stored definition decides
    /// (placement and exemption as saved now); without one (router tests on a fake resolver) <see cref="IMaintenanceMode.ForModel"/>.
    /// </summary>
    public static async Task<MaintenanceState?> ForModelAsync(
        IMaintenanceMode? maintenance, bool nodeScoped, IModelRegistry? registry, string modelId, CancellationToken ct)
    {
        if (maintenance is null || !nodeScoped)
        {
            return null;
        }

        var active = maintenance.Active;
        if (active.Count == 0)
        {
            return null;
        }

        if (active.FirstOrDefault(w => w.IsGlobal) is { } global)
        {
            return global;
        }

        if (registry is null)
        {
            return maintenance.ForModel(modelId);
        }

        var model = await registry.GetAsync(modelId, ct).ConfigureAwait(false);
        return model is null
            ? MaintenanceScope.WindowFor(active, modelId, nodeId: null, keepServingDuringJobs: false)
            : MaintenanceScope.WindowFor(active, model);
    }

    /// <summary>
    /// For <c>GET /v1/models</c> in the P2 order: a predicate telling whether a model id is paused now. Null when nothing
    /// is paused per model (no window, or node-scoped maintenance off).
    /// </summary>
    public static async Task<Func<string, bool>?> PausedModelsAsync(
        IMaintenanceMode? maintenance, bool nodeScoped, IModelRegistry? registry, CancellationToken ct)
    {
        if (maintenance is null || !nodeScoped)
        {
            return null;
        }

        var active = maintenance.Active;
        if (active.Count == 0)
        {
            return null;
        }

        if (active.Any(w => w.IsGlobal))
        {
            return _ => true;
        }

        if (registry is null)
        {
            return id => maintenance.ForModel(id) is not null;
        }

        var models = (await registry.ListAsync(ct).ConfigureAwait(false)).ToDictionary(m => m.Id, StringComparer.Ordinal);
        return id => (models.TryGetValue(id, out var model)
            ? MaintenanceScope.WindowFor(active, model)
            : MaintenanceScope.WindowFor(active, id, nodeId: null, keepServingDuringJobs: false)) is not null;
    }

    /// <summary>Reads the switch from the request's services (default off, the P1 order, when the options are not registered).</summary>
    public static bool NodeScoped(IServiceProvider services) =>
        services.GetService<IOptions<RouterMaintenanceOptions>>()?.Value.NodeScoped ?? false;

    /// <summary>Writes today's 503 <c>maintenance</c> answer: <c>Retry-After</c> from the estimate and the window's text.</summary>
    public static Task WriteAsync(HttpContext context, MaintenanceState window)
    {
        var now = DateTimeOffset.UtcNow;
        context.Response.Headers.RetryAfter = window.RetryAfterSeconds(now).ToString(CultureInfo.InvariantCulture);
        return OpenAiError.WriteAsync(
            context, StatusCodes.Status503ServiceUnavailable,
            window.Describe(now), "service_unavailable_error", "maintenance");
    }
}
