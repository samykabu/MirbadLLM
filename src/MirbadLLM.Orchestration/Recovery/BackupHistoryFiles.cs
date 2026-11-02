using System.Text.Json;
using System.Text.RegularExpressions;

namespace MirbadLLM.Orchestration.Recovery;

/// <summary>
/// Benchmark and tuning results in a backup (<see cref="BackupHistory"/>): the files of <c>BenchmarkStore</c>
/// (<c>{DataPath}/benchmarks/{modelId}/{id}.json</c>, <c>{DataPath}/tuning/{modelId}/{id}.json</c>) copied as they are, so
/// every field survives. A restore writes only runs that are not here yet: a run id is its start time, so a file already
/// here is the same run or a newer local one, and is never overwritten.
/// </summary>
public static partial class BackupHistoryFiles
{
    /// <summary>
    /// Raw run JSON a backup carries at most (newest first): re-indented inside the backup it grows, and the whole file must
    /// stay well under <see cref="BackupService.MaxFileBytes"/> or no restore could read it.
    /// </summary>
    public const long MaxRunBytes = 6L * 1024 * 1024;

    private const string BenchmarksDirectory = "benchmarks";
    private const string TuningDirectory = "tuning";

    /// <summary>Reads every run under <paramref name="dataPath"/>, newest first up to <see cref="MaxRunBytes"/>; unreadable files are skipped.</summary>
    public static BackupHistory Export(string dataPath, IReadOnlyDictionary<string, double> startupTimes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        var runs = Read(dataPath, BenchmarksDirectory).Select(r => (Tuning: false, r.File, r.Bytes))
            .Concat(Read(dataPath, TuningDirectory).Select(r => (Tuning: true, r.File, r.Bytes)))
            .OrderByDescending(r => r.File.Id, StringComparer.Ordinal)
            .ToList();

        long total = 0;
        var benchmarks = new List<BackupRunFile>();
        var tuning = new List<BackupRunFile>();
        int omitted = 0;
        foreach (var (isTuning, file, bytes) in runs)
        {
            if (total + bytes > MaxRunBytes)
            {
                omitted++;
                continue;
            }

            total += bytes;
            (isTuning ? tuning : benchmarks).Add(file);
        }

        return new BackupHistory(benchmarks, tuning, startupTimes, omitted);
    }

    /// <summary>Writes the runs that are not under <paramref name="dataPath"/> yet. Returns how many were written.</summary>
    public static async Task<int> ImportAsync(string dataPath, BackupHistory history, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        ArgumentNullException.ThrowIfNull(history);
        int written = 0;
        foreach (var (directory, files) in new[] { (BenchmarksDirectory, history.Benchmarks), (TuningDirectory, history.Tuning) })
        {
            foreach (var run in files ?? [])
            {
                // The names come from an uploaded file: only plain slugs, never a path.
                if (run is null || !SafeName().IsMatch(run.ModelId ?? "") || !SafeName().IsMatch(run.Id ?? "") || run.Run.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string folder = Path.Combine(dataPath, directory, run.ModelId!);
                string path = Path.Combine(folder, run.Id + ".json");
                if (File.Exists(path))
                {
                    continue;
                }

                Directory.CreateDirectory(folder);
                string temp = path + ".tmp";
                await File.WriteAllTextAsync(temp, run.Run.GetRawText(), ct).ConfigureAwait(false);
                File.Move(temp, path, overwrite: false);
                written++;
            }
        }

        return written;
    }

    private static IEnumerable<(BackupRunFile File, long Bytes)> Read(string dataPath, string directory)
    {
        string root = Path.Combine(dataPath, directory);
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (string modelDirectory in Directory.EnumerateDirectories(root))
        {
            string modelId = Path.GetFileName(modelDirectory);
            foreach (string file in Directory.EnumerateFiles(modelDirectory, "*.json"))
            {
                JsonElement run;
                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(file);
                    using var json = JsonDocument.Parse(bytes);
                    run = json.RootElement.Clone();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    continue; // a partial or corrupt file is not worth failing the backup over
                }

                yield return (new BackupRunFile(modelId, Path.GetFileNameWithoutExtension(file), run), bytes.LongLength);
            }
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();
}
