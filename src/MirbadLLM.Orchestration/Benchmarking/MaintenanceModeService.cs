using System.Text.Json;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>
/// Holds the router's maintenance windows (<see cref="IMaintenanceMode"/>), keyed by job id (design §6.5 items 2–3,
/// MN-110): one per running job, each global (<see cref="MaintenanceState.NodeId"/> null: P1, and the recovery-mode
/// "restoring" window) or scoped to the node the job holds. <see cref="Enter"/>, <see cref="UpdateEstimate"/> and
/// <see cref="Exit"/> keep their P1 signatures; entering again with an open job id replaces that job's window.
/// <para>The windows are persisted to <c>{DataPath}/maintenance.json</c> so an operator can see why the API was paused:
/// one window is today's single object, several are a JSON array, oldest first. A file found on construction (controller
/// boot) is stale by definition — jobs live in memory — and is cleared, so a crash never leaves the API paused.</para>
/// <para><see cref="ForModel(string)"/> needs the model's placement and exemption: with an <see cref="IModelRegistry"/> the
/// service keeps a snapshot of them, re-read in the background when a node-scoped window opens and when a lookup finds it
/// older than <see cref="ModelSnapshotMaxAge"/> (the router itself reads the stored definition per request, so its answer is
/// never stale). A model the snapshot does not know is paused only by a global window or as the model under test.</para>
/// </summary>
public sealed class MaintenanceModeService : IMaintenanceMode
{
    /// <summary>How old the model snapshot behind <see cref="ForModel(string)"/> may get before a lookup refreshes it.</summary>
    public static readonly TimeSpan ModelSnapshotMaxAge = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string? _filePath;
    private readonly IModelRegistry? _registry;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly List<MaintenanceState> _windows = [];
    private ModelSnapshot? _models;
    private Task? _refresh;

    public MaintenanceModeService(IOptions<MirbadLLMOptions> options)
        : this(options, registry: null)
    {
    }

    /// <summary>Creates the service with the model registry <see cref="ForModel(string)"/> reads placements and exemptions from.</summary>
    public MaintenanceModeService(IOptions<MirbadLLMOptions> options, IModelRegistry? registry)
        : this(options, registry, TimeProvider.System)
    {
    }

    internal MaintenanceModeService(IOptions<MirbadLLMOptions> options, IModelRegistry? registry, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _registry = registry;
        _time = time;
        _filePath = string.IsNullOrEmpty(options.Value.DataPath) ? null : Path.Combine(options.Value.DataPath, "maintenance.json");
        ClearedStaleFlagOnBoot = ClearPersisted();
    }

    /// <summary>In-memory only (tests).</summary>
    public MaintenanceModeService()
    {
        _time = TimeProvider.System;
    }

    /// <summary>True when a flag left by a previous controller process was found and removed at boot.</summary>
    public bool ClearedStaleFlagOnBoot { get; }

    /// <inheritdoc />
    public MaintenanceState? Current
    {
        get
        {
            lock (_gate)
            {
                return _windows.Count == 0 ? null : _windows[0];
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<MaintenanceState> Active
    {
        get
        {
            lock (_gate)
            {
                return [.. _windows];
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Placement and exemption come from the model snapshot (see the class remarks); <see cref="ForModel(ModelDefinition)"/>
    /// decides from a definition the caller already holds.
    /// </remarks>
    public MaintenanceState? ForModel(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        var windows = Active;
        if (windows.Count == 0)
        {
            return null;
        }

        var snapshot = Volatile.Read(ref _models);
        if (snapshot is null || _time.GetUtcNow() - snapshot.ReadAt > ModelSnapshotMaxAge)
        {
            StartRefresh();
        }

        return snapshot is not null && snapshot.Models.TryGetValue(modelId, out var model)
            ? MaintenanceScope.WindowFor(windows, model)
            : MaintenanceScope.WindowFor(windows, modelId, nodeId: null, keepServingDuringJobs: false);
    }

    /// <summary>The window pausing <paramref name="model"/> (same rule as <see cref="ForModel(string)"/>), or null.</summary>
    public MaintenanceState? ForModel(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return MaintenanceScope.WindowFor(Active, model);
    }

    /// <inheritdoc />
    public MaintenanceState? ForNode(string nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        var windows = Active;
        return windows.FirstOrDefault(w => w.IsGlobal) ??
               windows.FirstOrDefault(w => string.Equals(w.NodeId, nodeId, StringComparison.Ordinal));
    }

    /// <summary>
    /// The models that keep serving on <paramref name="window"/>'s node (design §6.5 item 3: placed there,
    /// <see cref="ModelDefinition.KeepServingDuringJobs"/>, not the model under test); empty for a global window. For the
    /// job API's <see cref="NodeJobSummary.ExemptModelIds"/> (MN-111).
    /// </summary>
    public static IReadOnlyList<string> ExemptModelIds(MaintenanceState window, IEnumerable<ModelDefinition> models) =>
        MaintenanceScope.ExemptModelIds(window, models);

    /// <summary>Opens <paramref name="state"/>'s window, or replaces the open window of the same job id.</summary>
    public void Enter(MaintenanceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            int index = _windows.FindIndex(w => w.JobId == state.JobId);
            if (index >= 0)
            {
                _windows[index] = state;
            }
            else
            {
                _windows.Add(state);
            }

            PersistUnlocked();
        }

        if (!state.IsGlobal)
        {
            StartRefresh();
        }
    }

    /// <summary>Moves the estimated end of <paramref name="jobId"/>'s window (and with it Retry-After and the "until ~HH:MM" text).</summary>
    public void UpdateEstimate(string jobId, DateTimeOffset estimatedEnd)
    {
        lock (_gate)
        {
            int index = _windows.FindIndex(w => w.JobId == jobId);
            if (index >= 0)
            {
                _windows[index] = _windows[index] with { EstimatedEnd = estimatedEnd };
                PersistUnlocked();
            }
        }
    }

    /// <summary>Closes <paramref name="jobId"/>'s window, if it holds one; the other windows stay open.</summary>
    public void Exit(string jobId)
    {
        lock (_gate)
        {
            if (_windows.RemoveAll(w => w.JobId == jobId) > 0)
            {
                PersistUnlocked();
            }
        }
    }

    /// <summary>Re-reads the placements and exemptions behind <see cref="ForModel(string)"/> now (a no-op without a registry).</summary>
    public async Task RefreshModelsAsync(CancellationToken ct = default)
    {
        if (_registry is null)
        {
            return;
        }

        var models = await _registry.ListAsync(ct).ConfigureAwait(false);
        var byId = new Dictionary<string, ModelDefinition>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            byId[model.Id] = model;
        }

        Volatile.Write(ref _models, new ModelSnapshot(byId, _time.GetUtcNow()));
    }

    private void StartRefresh()
    {
        if (_registry is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_refresh is { IsCompleted: false })
            {
                return;
            }

            _refresh = Task.Run(RefreshQuietlyAsync);
        }
    }

    private async Task RefreshQuietlyAsync()
    {
        try
        {
            await RefreshModelsAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // Keep the previous snapshot; the next lookup tries again.
        }
    }

    private void PersistUnlocked()
    {
        if (_windows.Count == 0)
        {
            ClearPersisted();
            return;
        }

        if (_filePath is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            string json = _windows.Count == 1
                ? JsonSerializer.Serialize(_windows[0], JsonOptions)
                : JsonSerializer.Serialize(_windows, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The in-memory windows are what the router reads; the file is informational.
        }
    }

    private bool ClearPersisted()
    {
        if (_filePath is null || !File.Exists(_filePath))
        {
            return false;
        }

        try
        {
            File.Delete(_filePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record ModelSnapshot(IReadOnlyDictionary<string, ModelDefinition> Models, DateTimeOffset ReadAt);
}
