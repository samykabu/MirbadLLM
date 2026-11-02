using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration;

/// <summary>
/// Remembers how long each model's last start took (container created → first healthy), so restart and tuning
/// dialogs can say "takes about N min" from a measurement instead of a guess. Persisted to
/// <c>{DataPath}/startup-times.json</c> so it survives controller restarts.
/// </summary>
public sealed class StartupTimeTracker
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string? _filePath;
    private readonly ConcurrentDictionary<string, double> _seconds = new(StringComparer.Ordinal);
    private readonly Lock _writeLock = new();

    public StartupTimeTracker(IOptions<MirbadLLMOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _filePath = string.IsNullOrEmpty(options.Value.DataPath) ? null : Path.Combine(options.Value.DataPath, "startup-times.json");
        Load();
    }

    /// <summary>In-memory only (tests).</summary>
    public StartupTimeTracker()
    {
    }

    /// <summary>The last measured start of <paramref name="modelId"/>, or null when none was observed.</summary>
    public TimeSpan? GetLast(string modelId) =>
        _seconds.TryGetValue(modelId, out double seconds) ? TimeSpan.FromSeconds(seconds) : null;

    /// <summary>
    /// Drops <paramref name="modelId"/>'s measurement: its next start is on other hardware (a move committed its placement on
    /// another node, design §6.5 item 8, MN-124), so the old node's start time would mislead.
    /// </summary>
    public void Forget(string modelId)
    {
        if (_seconds.TryRemove(modelId, out _))
        {
            Save();
        }
    }

    /// <summary>Every measurement in seconds by model id, for a backup.</summary>
    public IReadOnlyDictionary<string, double> Export() => new SortedDictionary<string, double>(_seconds, StringComparer.Ordinal);

    /// <summary>
    /// Adds the measurements of a backup for models that have none here; a local measurement is newer than any backup's,
    /// so it is kept. Returns how many were added.
    /// </summary>
    public int ImportMissing(IReadOnlyDictionary<string, double> seconds)
    {
        ArgumentNullException.ThrowIfNull(seconds);
        int added = seconds.Count(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value > 0 && kv.Value <= TimeSpan.FromHours(2).TotalSeconds &&
            _seconds.TryAdd(kv.Key, Math.Round(kv.Value)));
        if (added > 0)
        {
            Save();
        }

        return added;
    }

    public void Record(string modelId, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(2))
        {
            return;
        }

        _seconds[modelId] = Math.Round(duration.TotalSeconds);
        Save();
    }

    private void Load()
    {
        if (_filePath is null || !File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(_filePath));
            foreach (var (id, seconds) in data ?? [])
            {
                _seconds[id] = seconds;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable file only loses the estimates.
        }
    }

    private void Save()
    {
        if (_filePath is null)
        {
            return;
        }

        lock (_writeLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                string temp = _filePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new SortedDictionary<string, double>(_seconds, StringComparer.Ordinal), JsonOptions));
                File.Move(temp, _filePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Estimates are best effort.
            }
        }
    }
}
