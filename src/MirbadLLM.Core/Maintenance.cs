using System.Globalization;

namespace MirbadLLM.Core;

/// <summary>
/// The router's maintenance window: while a benchmark or tuning job runs, every <c>/v1/*</c> call (and the
/// browser chat) is refused with 503 so the measurements run on a quiet box.
/// <para>From P2 (design §6.5 item 2, MN-110) a window is node-scoped: <see cref="NodeId"/> names the node the job holds,
/// and only models on that node that are not exempt are paused. A null <see cref="NodeId"/> is a global window (P1, and
/// the recovery-mode "restoring" window of <see cref="MaintenanceJobKinds.Restore"/>).</para>
/// </summary>
/// <param name="JobId">The job holding the window.</param>
/// <param name="JobKind">A <see cref="MaintenanceJobKinds"/> value: "benchmark", "tuning" or (recovery mode) "restore".</param>
/// <param name="ModelId">Model being measured (empty for a restore window).</param>
/// <param name="StartedAt">When the window opened.</param>
/// <param name="EstimatedEnd">Best estimate of when the job finishes; drives <c>Retry-After</c>.</param>
/// <param name="NodeId">The node the window holds (P2); null = global (every model, P1 behaviour).</param>
public sealed record MaintenanceState(
    string JobId,
    string JobKind,
    string ModelId,
    DateTimeOffset StartedAt,
    DateTimeOffset EstimatedEnd,
    string? NodeId = null)
{
    /// <summary>Seconds a client should wait before retrying: the estimated time left, at least 30.</summary>
    public int RetryAfterSeconds(DateTimeOffset now) =>
        (int)Math.Max(30, Math.Ceiling((EstimatedEnd - now).TotalSeconds));

    /// <summary>True when the window is global (no node), as every P1 window is.</summary>
    public bool IsGlobal => NodeId is null;

    /// <summary>
    /// "mirbadllm is running a benchmark on qwen; API paused until ~14:05 UTC". Used for the 503 body and
    /// other plain-text/non-interactive surfaces, which — unlike the Blazor UI — cannot reformat the time
    /// into the viewer's local time zone client-side, so it always states UTC explicitly rather than
    /// showing the controller's (also UTC) local time unlabelled. A node-scoped window adds "on node X" after the model;
    /// a restore window reads "mirbadllm is restoring a backup; API paused until ~HH:mm UTC".
    /// </summary>
    public string Describe(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var end = EstimatedEnd < now ? now : EstimatedEnd;
        var local = TimeZoneInfo.ConvertTime(end, zone ?? TimeZoneInfo.Utc);
        if (string.Equals(JobKind, MaintenanceJobKinds.Restore, StringComparison.Ordinal))
        {
            return string.Create(CultureInfo.InvariantCulture, $"mirbadllm is restoring a backup; API paused until ~{local:HH:mm} UTC");
        }

        string what = string.Equals(JobKind, MaintenanceJobKinds.Tuning, StringComparison.Ordinal) ? "a tuning run" : "a benchmark";
        string where = NodeId is null ? "" : " on node " + NodeId;
        return string.Create(CultureInfo.InvariantCulture,
            $"mirbadllm is running {what} on {ModelId}{where}; API paused until ~{local:HH:mm} UTC");
    }
}

/// <summary>
/// Read side of the maintenance window, consulted by the router and the chat client.
/// <para>P2 (design §6.5 items 2–3, MN-110): several windows may be open at once (one per node, at most
/// <see cref="JobOptions.MaxConcurrent"/>). The router asks <see cref="ForModel"/>, which applies node scope and the
/// <see cref="ModelDefinition.KeepServingDuringJobs"/> exemption (never for the model under test). The default members
/// keep P1's single global window, so a P1 implementation is a valid P2 one.</para>
/// </summary>
public interface IMaintenanceMode
{
    /// <summary>
    /// The open window, or null when the API is serving normally. With several node-scoped windows (P2) this is the
    /// oldest one; callers that care about a model use <see cref="ForModel"/>.
    /// </summary>
    MaintenanceState? Current { get; }

    /// <summary>Every open window, oldest first. Default: <see cref="Current"/> alone.</summary>
    IReadOnlyList<MaintenanceState> Active => Current is { } current ? [current] : [];

    /// <summary>
    /// The window that pauses <paramref name="modelId"/>: a global window, or the window of the model's node unless the
    /// model is exempt and is not the model under test. Null = the model serves. Default (P1): <see cref="Current"/>,
    /// which is global.
    /// </summary>
    MaintenanceState? ForModel(string modelId) => Current;

    /// <summary>The window holding <paramref name="nodeId"/> (or a global one), or null. Default: <see cref="Current"/> when global or on that node.</summary>
    MaintenanceState? ForNode(string nodeId) =>
        Current is { } current && (current.NodeId is null || string.Equals(current.NodeId, nodeId, StringComparison.Ordinal)) ? current : null;
}

/// <summary>
/// Which maintenance window pauses a model (design §6.5 items 2–3, D79): a global window pauses every model; a node-scoped
/// window pauses the model under test (never exempt, wherever it is placed) and every model placed on that node unless it
/// is <see cref="ModelDefinition.KeepServingDuringJobs"/>. The one rule shared by the router and
/// <c>MaintenanceModeService</c> (moved here from the router by the W7 review, MN-126).
/// </summary>
public static class MaintenanceScope
{
    /// <summary>True when <paramref name="window"/> pauses the model.</summary>
    /// <param name="window">An open window.</param>
    /// <param name="modelId">The model's id.</param>
    /// <param name="nodeId">The node the model is placed on; null when unplaced or unknown.</param>
    /// <param name="keepServingDuringJobs">The model's exemption flag.</param>
    public static bool Pauses(MaintenanceState window, string modelId, string? nodeId, bool keepServingDuringJobs)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(modelId);
        if (window.IsGlobal || string.Equals(window.ModelId, modelId, StringComparison.Ordinal))
        {
            return true;
        }

        return nodeId is not null && !keepServingDuringJobs && string.Equals(window.NodeId, nodeId, StringComparison.Ordinal);
    }

    /// <summary>
    /// The window pausing the model, or null when it serves. A global window wins over a node-scoped one (it blocks
    /// everything); otherwise the oldest matching window.
    /// </summary>
    public static MaintenanceState? WindowFor(IReadOnlyList<MaintenanceState> windows, string modelId, string? nodeId, bool keepServingDuringJobs)
    {
        ArgumentNullException.ThrowIfNull(windows);
        MaintenanceState? match = null;
        foreach (var window in windows)
        {
            if (window.IsGlobal)
            {
                return window;
            }

            if (match is null && Pauses(window, modelId, nodeId, keepServingDuringJobs))
            {
                match = window;
            }
        }

        return match;
    }

    /// <summary>The window pausing <paramref name="model"/>, or null.</summary>
    public static MaintenanceState? WindowFor(IReadOnlyList<MaintenanceState> windows, ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return WindowFor(windows, model.Id, model.Placement?.NodeId, model.KeepServingDuringJobs);
    }

    /// <summary>The models that keep serving on a node-scoped window's node: placed there, KeepServingDuringJobs, not the model under test; empty for a global window.</summary>
    public static IReadOnlyList<string> ExemptModelIds(MaintenanceState window, IEnumerable<ModelDefinition> models)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(models);
        return window.IsGlobal
            ? []
            : [.. models
                .Where(m => m.KeepServingDuringJobs && string.Equals(m.Placement?.NodeId, window.NodeId, StringComparison.Ordinal) && !string.Equals(m.Id, window.ModelId, StringComparison.Ordinal))
                .Select(m => m.Id)
                .Order(StringComparer.Ordinal)];
    }
}
