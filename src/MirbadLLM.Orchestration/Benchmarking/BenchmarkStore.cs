using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>
/// Persists benchmark and tuning results as one JSON file per run:
/// <c>{DataPath}/benchmarks/{modelId}/{timestamp}.json</c> and <c>{DataPath}/tuning/{modelId}/{timestamp}.json</c>.
/// </summary>
public sealed class BenchmarkStore(IOptions<MirbadLLMOptions> options)
{
    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly string _root = options.Value.DataPath;

    /// <summary>Run id and file name for a start time: <c>yyyyMMddTHHmmssZ</c> (UTC, sorts chronologically).</summary>
    public static string NewId(DateTimeOffset startedAt) =>
        startedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public Task SaveBenchmarkAsync(BenchmarkRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        return WriteAsync(Path.Combine(_root, "benchmarks", run.ModelId), run.Id, run, ct);
    }

    public Task SaveTuningAsync(TuningRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        return WriteAsync(Path.Combine(_root, "tuning", run.ModelId), run.Id, run, ct);
    }

    /// <summary>Newest first.</summary>
    public Task<IReadOnlyList<BenchmarkRun>> ListBenchmarksAsync(string modelId, CancellationToken ct = default) =>
        ReadAllAsync<BenchmarkRun>(Path.Combine(_root, "benchmarks", Safe(modelId)), ct);

    /// <summary>Newest first.</summary>
    public Task<IReadOnlyList<TuningRun>> ListTuningAsync(string modelId, CancellationToken ct = default) =>
        ReadAllAsync<TuningRun>(Path.Combine(_root, "tuning", Safe(modelId)), ct);

    /// <summary>
    /// Newest first, only the runs measured on the same hardware and runtime as <paramref name="reference"/>
    /// (<see cref="BenchmarkComparisons.Comparable"/>; runs stored before MN-112 count as the bootstrap node's). Every run
    /// when <paramref name="reference"/> is null. For ETAs and "best" lists (design §6.5 item 5).
    /// </summary>
    public async Task<IReadOnlyList<BenchmarkRun>> ListComparableBenchmarksAsync(
        string modelId, BenchmarkProvenance? reference, string bootstrapNodeId, CancellationToken ct = default) =>
        BenchmarkComparisons.ComparableWith(await ListBenchmarksAsync(modelId, ct).ConfigureAwait(false), reference, bootstrapNodeId);

    /// <summary>As <see cref="ListComparableBenchmarksAsync"/> for tuning runs.</summary>
    public async Task<IReadOnlyList<TuningRun>> ListComparableTuningAsync(
        string modelId, BenchmarkProvenance? reference, string bootstrapNodeId, CancellationToken ct = default) =>
        BenchmarkComparisons.ComparableWith(await ListTuningAsync(modelId, ct).ConfigureAwait(false), reference, bootstrapNodeId);

    /// <summary>
    /// Finds a tuning run by id alone, without knowing which model it belongs to — run ids are UTC start-time
    /// stamps, so a collision across two models' directories is not expected in practice. Used to tell "the
    /// run exists, but for a different model" (still worth a specific message) apart from "no such run" when
    /// applying a candidate by run id and index.
    /// </summary>
    public async Task<TuningRun?> FindTuningRunAsync(string runId, CancellationToken ct = default)
    {
        string tuningRoot = Path.Combine(_root, "tuning");
        if (!Directory.Exists(tuningRoot))
        {
            return null;
        }

        foreach (string modelDir in Directory.EnumerateDirectories(tuningRoot))
        {
            string path = Path.Combine(modelDir, runId + ".json");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                await using var stream = File.OpenRead(path);
                return await JsonSerializer.DeserializeAsync<TuningRun>(stream, JsonOptions, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// camelCase, indented, PascalCase enums (the files' shape since before P1). Provenance (MN-112) follows its frozen
    /// contract fixture: the noise verdict camelCase (<c>quiet</c>), and empty members (null strings, an empty GPU list on
    /// a unified node) left out, so an additive block never writes noise. Reading is case-insensitive for enums, so files
    /// of every age load.
    /// </summary>
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { OmitEmptyProvenance } },
        };
        o.Converters.Add(new JsonStringEnumConverter<BenchmarkNoise>(JsonNamingPolicy.CamelCase));
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    private static void OmitEmptyProvenance(JsonTypeInfo info)
    {
        if (info.Type != typeof(BenchmarkProvenance))
        {
            return;
        }

        foreach (var property in info.Properties)
        {
            if (property.PropertyType == typeof(string))
            {
                property.ShouldSerialize = static (_, value) => value is not null;
            }
            else if (property.PropertyType == typeof(IReadOnlyList<ProvenanceGpu>))
            {
                property.ShouldSerialize = static (_, value) => value is IReadOnlyList<ProvenanceGpu> { Count: > 0 };
            }
        }
    }

    /// <summary>Model ids are validated slugs; this only guards against a path escaping the data folder.</summary>
    private static string Safe(string modelId) =>
        modelId.Contains("..", StringComparison.Ordinal) || modelId.Contains('/', StringComparison.Ordinal) || modelId.Contains('\\', StringComparison.Ordinal)
            ? "_invalid"
            : modelId;

    private static async Task WriteAsync<T>(string directory, string id, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, id + ".json");
        string temp = path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct).ConfigureAwait(false);
        }

        File.Move(temp, path, overwrite: true);
    }

    private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(string directory, CancellationToken ct)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var result = new List<T>();
        foreach (string file in Directory.EnumerateFiles(directory, "*.json").OrderDescending(StringComparer.Ordinal))
        {
            try
            {
                await using var stream = File.OpenRead(file);
                var item = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false);
                if (item is not null)
                {
                    result.Add(item);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A corrupt or partial file is skipped rather than hiding the whole history.
            }
        }

        return result;
    }
}
