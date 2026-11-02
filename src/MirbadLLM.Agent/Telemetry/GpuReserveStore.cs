using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Telemetry;

/// <summary>
/// The last per-GPU reserves a launch's admission carried (design §6.1, design-p3p4 §A.4.3; MN-232, D129 (2)), kept in
/// <c>{state}/gpu-reserves.json</c>.
/// <para>Why: <see cref="ExternalGpuUseContributor"/> only judges a GPU it knows a reserve for, and the reserves come from
/// the master with every launch (<see cref="AdmissionRequest.GpuReservesGiB"/>). Without this file an agent that restarts
/// while models run would not judge the external VRAM use (<c>gpu_external_use_high</c>) until the next launch, which on
/// a desktop PC is exactly when it matters. <see cref="Remember"/> writes the file only when the reserves changed (an
/// idle agent writes nothing); the composition seeds
/// <see cref="ExternalGpuUseOptions.GpuReservesGiB"/> from <see cref="Load"/> at start.</para>
/// <para>The file holds GPU UUIDs and GiB figures only — nothing secret — and a damaged or foreign file is ignored (an
/// empty map, logged), never fatal: the reserves are re-learned at the next launch.</para>
/// </summary>
public sealed partial class GpuReserveStore
{
    /// <summary>The file name in the state directory.</summary>
    public const string FileName = "gpu-reserves.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private Dictionary<string, double>? _last;

    /// <summary>Creates the store over <paramref name="path"/> (<see cref="PathFor"/>).</summary>
    public GpuReserveStore(string path, ILogger<GpuReserveStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>The file this store reads and writes.</summary>
    public string FilePath => _path;

    /// <summary><see cref="FileName"/> in <paramref name="stateDirectory"/>.</summary>
    public static string PathFor(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        return Path.Combine(stateDirectory, FileName);
    }

    /// <summary>
    /// The reserves last learned, or an empty map when the file is absent, unreadable or damaged. Never throws.
    /// </summary>
    public IReadOnlyDictionary<string, double> Load()
    {
        Dictionary<string, double> reserves = Read();
        lock (_gate)
        {
            _last = new Dictionary<string, double>(reserves, StringComparer.OrdinalIgnoreCase);
        }

        return reserves;
    }

    /// <summary>
    /// Remembers <paramref name="admission"/>'s per-GPU reserves, writing the file only when they differ from what it
    /// already holds. A request without <see cref="AdmissionRequest.GpuReservesGiB"/> changes nothing. Never throws: a
    /// failed write is logged and the reserves stay in memory (this runs on the admission path of a launch).
    /// </summary>
    public void Remember(AdmissionRequest admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (admission.GpuReservesGiB is not { Count: > 0 } reserves)
        {
            return;
        }

        var wanted = Normalize(reserves);
        lock (_gate)
        {
            if (_last is { } known && Same(known, wanted))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
                File.WriteAllText(_path, JsonSerializer.Serialize(new GpuReserveDocument { GpuReservesGiB = wanted }, Json));
                // Only now: a failed write must leave the store believing it holds the *old* reserves, or the next launch
                // with the same reserves short-circuits at the Same() check above and the file is never written again —
                // one transient IOException would disable D129 (2) until the master's reserves happen to change (MN-234).
                _last = wanted;
                LogWritten(_logger, wanted.Count, _path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LogNotWritten(_logger, ex, _path);
            }
        }
    }

    private Dictionary<string, double> Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            }

            var document = JsonSerializer.Deserialize<GpuReserveDocument>(File.ReadAllText(_path), Json);
            return Normalize(document?.GpuReservesGiB ?? new Dictionary<string, double>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogNotRead(_logger, ex, _path);
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, double> Normalize(IReadOnlyDictionary<string, double> reserves) =>
        reserves.Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && double.IsFinite(kv.Value) && kv.Value >= 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    private static bool Same(Dictionary<string, double> left, Dictionary<string, double> right) =>
        left.Count == right.Count && left.All(kv => right.TryGetValue(kv.Key, out double other) && other.Equals(kv.Value));

    /// <summary><c>gpu-reserves.json</c>.</summary>
    private sealed record GpuReserveDocument
    {
        /// <summary>Reserve in GiB by GPU UUID, as the master's last launch sent it.</summary>
        public IReadOnlyDictionary<string, double> GpuReservesGiB { get; init; } = new Dictionary<string, double>();
    }

    [LoggerMessage(EventId = 2680, Level = LogLevel.Debug, Message = "Remembered the reserves of {Count} GPU(s) in {Path}")]
    private static partial void LogWritten(ILogger logger, int count, string path);

    [LoggerMessage(EventId = 2681, Level = LogLevel.Warning,
        Message = "The learned GPU reserves could not be written to {Path}; gpu_external_use_high may not be judged after the next restart")]
    private static partial void LogNotWritten(ILogger logger, Exception exception, string path);

    [LoggerMessage(EventId = 2682, Level = LogLevel.Warning,
        Message = "{Path} could not be read; the GPU reserves are re-learned from the next launch")]
    private static partial void LogNotRead(ILogger logger, Exception exception, string path);
}
