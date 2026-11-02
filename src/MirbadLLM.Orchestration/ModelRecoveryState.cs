using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Multinode;

namespace MirbadLLM.Orchestration;

/// <summary>Durable running-model intent, bound to a node's identity and agent boot. Explicit stops clear intent;
/// a new agent boot moves the previous running set into a pending, one-shot recovery queue.</summary>
public sealed partial class ModelRecoveryState
{
    private readonly string? _path;
    private readonly string? _pausePath;
    private readonly int _crashLimit;
    private readonly Dictionary<string, CrashRestart> _crashes = new(StringComparer.Ordinal);
    private readonly IMaintenanceMode? _maintenance;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _paused = new(StringComparer.Ordinal);
    private Dictionary<string, Entry> _nodes = new(StringComparer.Ordinal);
    private bool _loaded;
    private bool _pausesLoaded;
    private bool _disabled;

    public ModelRecoveryState(IOptions<MirbadLLMOptions> options, IMaintenanceMode? maintenance = null,
        ILogger<ModelRecoveryState>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _path = string.IsNullOrEmpty(options.Value.DataPath) ? null : Path.Combine(options.Value.DataPath, "running-models.json");
        _pausePath = string.IsNullOrEmpty(options.Value.DataPath) ? null : Path.Combine(options.Value.DataPath, "restart-pauses.json");
        _crashLimit = Math.Max(1, options.Value.CrashRestartLimit);
        _maintenance = maintenance;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Records a synchronized snapshot, or one runtime transition/acknowledged command. A missing runtime in
    /// a transition is an explicit stop. Offline views and temporary job activity never erase the running set.</summary>
    public void Observe(NodeLiveStatus node, string? modelId = null, ModelState? state = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.IsServing || string.IsNullOrEmpty(node.BootId))
        {
            return;
        }

        lock (_gate)
        {
            if (_maintenance?.ForNode(node.NodeId) is not null)
            {
                _paused.Add(node.NodeId);
                if (state is null || ModelWriteScope.CurrentOwnerRecordId is not null)
                {
                    return;
                }
            }

            if (!Load())
            {
                return;
            }

            var previous = _nodes.GetValueOrDefault(node.NodeId);
            bool sameNode = previous?.NodeUid == node.NodeUid;
            if (modelId is not null && previous is not null && (!sameNode || previous.BootId != node.BootId))
            {
                return; // Only a synchronized snapshot may advance the boot; old launch completions cannot change it.
            }

            var pending = sameNode ? new HashSet<string>(previous!.Pending, StringComparer.Ordinal) : [];
            var stopped = sameNode ? new HashSet<string>(previous!.Stopped, StringComparer.Ordinal) : [];
            if (sameNode && previous!.BootId != node.BootId)
            {
                pending.UnionWith(previous.Running);
            }

            var active = node.Runtimes.Where(r => !r.Tuning && IsRunning(r.State)).Select(r => r.ModelId).ToHashSet(StringComparer.Ordinal);
            active.ExceptWith(stopped);
            var running = modelId is not null && sameNode && previous!.BootId == node.BootId
                ? new HashSet<string>(previous.Running, StringComparer.Ordinal)
                : active;
            if (modelId is null)
            {
                pending.ExceptWith(stopped);
                pending.ExceptWith(active); // Instances adopted after an agent restart need no new launch.
                running.UnionWith(pending);
            }
            else
            {
                var instance = node.Runtimes.FirstOrDefault(r => r.ModelId == modelId);
                if (instance?.Tuning == true)
                {
                    return;
                }

                if (state is null && stopped.Contains(modelId))
                {
                    return; // A delayed Ready event must not undo an acknowledged Stop.
                }

                pending.Remove(modelId);
                if (IsRunning(state ?? instance?.State ?? ModelState.Stopped))
                {
                    stopped.Remove(modelId);
                    running.Add(modelId);
                }
                else
                {
                    if (state is not null)
                    {
                        stopped.Add(modelId);
                    }

                    running.Remove(modelId);
                }
            }

            var entry = new Entry(node.NodeUid, node.BootId, running, pending, stopped);
            if (previous is not null && previous.NodeUid == entry.NodeUid && previous.BootId == entry.BootId &&
                previous.Running.SetEquals(running) && previous.Pending.SetEquals(pending) && previous.Stopped.SetEquals(stopped))
            {
                return;
            }

            Save(new Dictionary<string, Entry>(_nodes, StringComparer.Ordinal) { [node.NodeId] = entry });
        }
    }

    /// <summary>Once a job releases its window, learn its final running set even if no later runtime event arrives.</summary>
    public void RefreshAfterMaintenance(NodeLiveStatus node)
    {
        ArgumentNullException.ThrowIfNull(node);
        lock (_gate)
        {
            if (!node.IsServing || _maintenance?.ForNode(node.NodeId) is not null || !_paused.Remove(node.NodeId))
            {
                return;
            }
        }

        Observe(node);
    }

    /// <summary>Recovery candidates for this synchronized node boot, in a stable order. Re-enrolled, draining and
    /// unreachable nodes cannot consume another identity's recovery record.</summary>
    public IReadOnlyList<string> Pending(NodeLiveStatus node)
    {
        ArgumentNullException.ThrowIfNull(node);
        lock (_gate)
        {
            return Load() && node.IsServing && !node.Draining && _maintenance?.ForNode(node.NodeId) is null &&
                _nodes.TryGetValue(node.NodeId, out var entry) && entry.NodeUid == node.NodeUid && entry.BootId == node.BootId
                ? [.. entry.Pending.Order(StringComparer.Ordinal)]
                : [];
        }
    }

    /// <summary>
    /// True when a model that died on <paramref name="node"/> may be relaunched by the health monitor: the node still
    /// serves under the boot the recovery record knows, nobody stopped the model (an acknowledged Stop), no window or
    /// tuning run holds it, and its automatic restarts are not paused.
    /// </summary>
    public bool CanAutoRestart(NodeLiveStatus node, string modelId)
    {
        ArgumentNullException.ThrowIfNull(node);
        lock (_gate)
        {
            return Load() && node.IsServing && !node.Draining && _maintenance?.ForNode(node.NodeId) is null &&
                CrashRestartOf(modelId)?.Paused != true &&
                node.Runtimes.FirstOrDefault(r => r.ModelId == modelId)?.Tuning != true &&
                _nodes.TryGetValue(node.NodeId, out var entry) && entry.NodeUid == node.NodeUid && entry.BootId == node.BootId &&
                !entry.Stopped.Contains(modelId);
        }
    }

    /// <summary>Counts one crash; once the model crashed <see cref="MirbadLLMOptions.CrashRestartLimit"/> times in a row
    /// the record is paused and persisted, so the pause survives a controller restart.</summary>
    public CrashRestart RecordCrash(string nodeId, string modelId, string? error)
    {
        lock (_gate)
        {
            int crashes = (CrashRestartOf(modelId)?.Crashes ?? 0) + 1;
            var record = new CrashRestart(modelId, nodeId, crashes, _crashLimit, crashes >= _crashLimit, error, DateTimeOffset.UtcNow);
            _crashes[modelId] = record;
            if (record.Paused)
            {
                SavePauses();
            }

            return record;
        }
    }

    /// <summary>The model's consecutive-crash record (paused or still retrying), or null when it has none.</summary>
    public CrashRestart? CrashRestartOf(string modelId)
    {
        lock (_gate)
        {
            LoadPauses();
            return _crashes.GetValueOrDefault(modelId);
        }
    }

    /// <summary>Forgets a model's crashes and any pause: a human started it, or it stayed Ready for the settle period.</summary>
    public void ResetCrashes(string modelId)
    {
        lock (_gate)
        {
            LoadPauses();
            if (_crashes.Remove(modelId, out var previous) && previous.Paused)
            {
                SavePauses();
            }
        }
    }

    /// <summary>The boot id a fleet recovery seeds with (<see cref="WriteSeed"/>); no agent ever reports it.</summary>
    public const string SeedBootId = "restored-from-backup";

    /// <summary>Running intent per node id (running and still-pending recoveries), for a backup. Empty while disabled.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> RunningModels()
    {
        lock (_gate)
        {
            return !Load()
                ? new Dictionary<string, IReadOnlyList<string>>()
                : _nodes.Where(n => n.Value.Running.Count > 0).ToDictionary(
                    n => n.Key, n => (IReadOnlyList<string>)[.. n.Value.Running.Order(StringComparer.Ordinal)], StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Fleet recovery: writes <c>running-models.json</c> into <paramref name="dataPath"/> with each node's backed-up running
    /// set under <see cref="SeedBootId"/>. The node's first synchronized snapshot carries a real boot id, so
    /// <see cref="Observe"/> treats the seed as a previous boot and queues every model that is not running for recovery.
    /// </summary>
    public static void WriteSeed(string dataPath, IEnumerable<(string NodeId, string NodeUid, IReadOnlyList<string> Models)> nodes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        ArgumentNullException.ThrowIfNull(nodes);
        var seed = nodes.Where(n => n.Models.Count > 0).ToDictionary(
            n => n.NodeId,
            n => new Entry(n.NodeUid, SeedBootId, n.Models.ToHashSet(StringComparer.Ordinal), [], []),
            StringComparer.Ordinal);
        if (seed.Count > 0)
        {
            WriteAtomically(Path.Combine(dataPath, "running-models.json"), seed);
        }
    }

    private static bool IsRunning(ModelState state) => state is ModelState.Starting or ModelState.Ready;

    private void LoadPauses()
    {
        if (_pausesLoaded)
        {
            return;
        }

        _pausesLoaded = true;
        try
        {
            if (_pausePath is not null && File.Exists(_pausePath))
            {
                var paused = JsonSerializer.Deserialize<Dictionary<string, CrashRestart>>(File.ReadAllText(_pausePath), MasterStateJson.Options) ?? [];
                foreach (var (id, record) in paused.Where(p => !string.IsNullOrWhiteSpace(p.Key) && p.Value is { Paused: true }))
                {
                    _crashes[id] = record;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            PausesUnavailable(_logger, ex, _pausePath); // Paused models are retried again; their next crashes rebuild the pause.
        }
    }

    private void SavePauses()
    {
        try
        {
            if (_pausePath is not null)
            {
                WriteAtomically(_pausePath, _crashes.Where(c => c.Value.Paused).ToDictionary(StringComparer.Ordinal));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PausesUnavailable(_logger, ex, _pausePath);
        }
    }

    private bool Load()
    {
        if (!_loaded)
        {
            _loaded = true;
            try
            {
                if (_path is not null && File.Exists(_path))
                {
                    _nodes = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path), MasterStateJson.Options)
                        ?? throw new InvalidDataException("Running-model recovery state is empty.");
                    if (_nodes.Any(n => string.IsNullOrWhiteSpace(n.Key) || n.Value is null ||
                        string.IsNullOrWhiteSpace(n.Value.NodeUid) || string.IsNullOrWhiteSpace(n.Value.BootId) ||
                        n.Value.Running is null || n.Value.Pending is null || n.Value.Stopped is null ||
                        n.Value.Running.Concat(n.Value.Pending).Concat(n.Value.Stopped).Any(string.IsNullOrWhiteSpace)))
                    {
                        throw new InvalidDataException("Running-model recovery state contains an invalid node or model.");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                Disable(ex);
            }
        }

        return !_disabled;
    }

    private void Save(Dictionary<string, Entry> nodes)
    {
        try
        {
            if (_path is not null)
            {
                WriteAtomically(_path, nodes);
            }

            _nodes = nodes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Disable(ex);
        }
    }

    private static void WriteAtomically<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, MasterStateJson.Options);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
        DirectorySync.FlushParent(path);
    }

    private void Disable(Exception exception)
    {
        _disabled = true;
        RecoveryUnavailable(_logger, exception, _path);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Automatic model recovery is disabled: cannot read or persist {Path}. Repair the recovery file and restart the controller.")]
    private static partial void RecoveryUnavailable(ILogger logger, Exception exception, string? path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cannot read or persist the paused automatic restarts in {Path}; paused models are retried again after a controller restart.")]
    private static partial void PausesUnavailable(ILogger logger, Exception exception, string? path);

    private sealed record Entry(string NodeUid, string BootId, HashSet<string> Running, HashSet<string> Pending, HashSet<string> Stopped);
}

/// <summary>A model's consecutive crashes since it last settled or was started by hand (<see cref="ModelRecoveryState"/>).</summary>
/// <param name="Limit">Crashes in a row that pause automatic restarts; restarts happen after crashes 1..Limit-1.</param>
/// <param name="Paused">True once <see cref="Crashes"/> reached <see cref="Limit"/>: the model stays Failed until a human starts it.</param>
/// <param name="LastError">The <see cref="ModelStatus.Error"/> of the last crash.</param>
public sealed record CrashRestart(string ModelId, string NodeId, int Crashes, int Limit, bool Paused, string? LastError, DateTimeOffset At);
