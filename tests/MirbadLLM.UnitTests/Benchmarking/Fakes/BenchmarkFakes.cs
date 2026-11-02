using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Benchmarking;

namespace MirbadLLM.UnitTests.Benchmarking.Fakes;

/// <summary>
/// A scripted Docker host for job tests: one container per model, launched from any definition. A launch
/// reports Starting for <see cref="StartingPolls"/> status reads, then Ready — or Failed with
/// <see cref="FailWith"/>'s error when it returns one for the launched definition.
/// </summary>
internal sealed class FakeCluster : IModelRuntime, IModelLauncher
{
    private readonly ConcurrentDictionary<string, Container> _containers = new(StringComparer.Ordinal);

    public int StartingPolls { get; set; } = 1;

    public Func<ModelDefinition, string?> FailWith { get; set; } = _ => null;

    /// <summary>An exception a launch throws before touching anything (start conflict, Docker error); null = launch.</summary>
    public Func<ModelDefinition, Exception?> LaunchThrows { get; set; } = _ => null;

    /// <summary>Thrown by every stop (Docker unreachable).</summary>
    public Exception? StopThrows { get; set; }

    /// <summary>A stop that "succeeds" but leaves the container in place (stuck container).</summary>
    public bool StopKeepsContainer { get; set; }

    public List<(ModelDefinition Definition, LaunchOptions Options)> Launches { get; } = [];

    public List<string> Stops { get; } = [];

    /// <summary>Puts a Ready container in place, launched from <paramref name="definition"/> (labelled unless <paramref name="unlabelled"/>).</summary>
    public FakeCluster Running(ModelDefinition definition, bool unlabelled = false, bool tuning = false)
    {
        _containers[definition.Id] = new Container(definition, unlabelled ? null : Hash(definition), tuning, ModelState.Ready, 0, null);
        return this;
    }

    /// <summary>Puts a container that stays Starting (until <see cref="MarkReady"/>) in place for another model.</summary>
    public FakeCluster StartingForever(ModelDefinition definition)
    {
        _containers[definition.Id] = new Container(definition, Hash(definition), false, ModelState.Starting, int.MaxValue, null);
        return this;
    }

    public void MarkReady(string id)
    {
        if (_containers.TryGetValue(id, out var c))
        {
            _containers[id] = c with { State = ModelState.Ready, PollsLeft = 0 };
        }
    }

    public ModelDefinition? CurrentDefinition(string id) => _containers.TryGetValue(id, out var c) ? c.Definition : null;

    public static string Hash(ModelDefinition definition) => ModelConfigHash.Compute(definition, "default-image", 100);

    public Task<IReadOnlyList<ModelStatus>> ListStatusAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ModelStatus>>(_containers.Keys.Select(Status).ToList());

    public Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default)
    {
        if (_containers.TryGetValue(id, out var c) && c.State == ModelState.Starting)
        {
            _containers[id] = c.PollsLeft <= 0
                ? c with { State = c.Error is null ? ModelState.Ready : ModelState.Failed }
                : c with { PollsLeft = c.PollsLeft - 1 };
        }

        return Task.FromResult(Status(id));
    }

    public Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default) =>
        throw new NotSupportedException("Jobs launch through IModelLauncher.");

    /// <summary>What a restart does (the "Apply changes now?" dialog); null = relaunch <see cref="RestartDefinition"/>.</summary>
    public Func<string, bool, Task<ModelStatus>>? Restart { get; set; }

    /// <summary>The definition a default restart launches (the real runtime reads the saved one from the registry).</summary>
    public Func<string, ModelDefinition?> RestartDefinition { get; set; } = _ => null;

    public List<(string Id, bool Force)> Restarts { get; } = [];

    public Task<ModelStatus> RestartAsync(string id, bool force = false, CancellationToken ct = default)
    {
        Restarts.Add((id, force));
        if (Restart is { } restart)
        {
            return restart(id, force);
        }

        var definition = RestartDefinition(id) ?? CurrentDefinition(id) ?? throw new KeyNotFoundException($"Unknown model '{id}'.");
        return LaunchAsync(definition, new LaunchOptions(force), ct);
    }

    public Task StopAsync(string id, CancellationToken ct = default)
    {
        Stops.Add(id);
        if (StopThrows is { } error)
        {
            throw error;
        }

        if (!StopKeepsContainer)
        {
            _containers.TryRemove(id, out _);
        }

        return Task.CompletedTask;
    }

    /// <summary>The container log of the definition a model runs (vLLM's startup lines); empty by default.</summary>
    public Func<ModelDefinition, IEnumerable<string>> Logs { get; set; } = _ => [];

#pragma warning disable CS1998
    public async IAsyncEnumerable<string> StreamLogsAsync(string id, int tail = 200, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (CurrentDefinition(id) is { } definition)
        {
            foreach (string line in Logs(definition))
            {
                yield return line;
            }
        }
    }
#pragma warning restore CS1998

    public Task<ModelStatus> LaunchAsync(ModelDefinition definition, LaunchOptions options, CancellationToken ct = default)
    {
        if (LaunchThrows(definition) is { } thrown)
        {
            throw thrown;
        }

        Launches.Add((definition, options));
        string? error = FailWith(definition);
        _containers[definition.Id] = new Container(definition, Hash(definition), options.Tuning, ModelState.Starting, StartingPolls, error);
        return Task.FromResult(Status(definition.Id));
    }

    public Task<ModelDefinition?> GetLaunchedDefinitionAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(CurrentDefinition(id));

    public Task<string?> GetLaunchedConfigHashAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_containers.TryGetValue(id, out var c) ? c.Hash : null);

    public Task<string> ComputeConfigHashAsync(ModelDefinition definition, CancellationToken ct = default) =>
        Task.FromResult(Hash(definition));

    public Task<IReadOnlyList<string>> ListTuningContainersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(_containers.Where(kv => kv.Value.Tuning).Select(kv => kv.Key).ToList());

    private ModelStatus Status(string id) => _containers.TryGetValue(id, out var c)
        ? new ModelStatus(id, c.State, "container-" + id, 8101, DateTimeOffset.UtcNow, c.State == ModelState.Failed ? c.Error : null)
        : new ModelStatus(id, ModelState.Stopped);

    private sealed record Container(ModelDefinition Definition, string? Hash, bool Tuning, ModelState State, int PollsLeft, string? Error);
}

/// <summary>
/// A benchmark suite that scores whatever definition the cluster is running with <see cref="Score"/>. When
/// <see cref="Gate"/> is set, each run waits on it first (so tests can observe a job mid-flight).
/// </summary>
internal sealed class FakeSuite(FakeCluster cluster) : IBenchmarkSuite
{
    public Func<ModelDefinition, BenchmarkResult> Score { get; set; } = _ => Results.Make(10, 100);

    /// <summary>Called with the definition and target instead of <see cref="Score"/> when set.</summary>
    public Func<ModelDefinition, BenchmarkTarget, BenchmarkResult>? ScoreTarget { get; set; }

    public TaskCompletionSource? Gate { get; set; }

    public int Runs { get; private set; }

    public List<BenchmarkTarget> Targets { get; } = [];

    public int StepCount => 5;

    public async Task<BenchmarkResult> RunAsync(BenchmarkTarget target, IProgress<BenchmarkProgress>? progress, CancellationToken ct = default)
    {
        Runs++;
        Targets.Add(target);
        progress?.Report(new BenchmarkProgress("N=1, run 1 of 3", 1, StepCount));
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        ct.ThrowIfCancellationRequested();
        var definition = cluster.CurrentDefinition(target.ModelId) ?? throw new InvalidOperationException("No container.");
        return ScoreTarget is { } scoreTarget ? scoreTarget(definition, target) : Score(definition);
    }
}

internal sealed class FakeGpu(double? utilization) : IGpuProbe
{
    public Task<GpuSnapshot?> GetAsync(CancellationToken ct = default) =>
        Task.FromResult<GpuSnapshot?>(utilization is null ? null : new GpuSnapshot(utilization, null, null, null));
}

/// <summary>Builders for benchmark results with the numbers a test cares about.</summary>
internal static class Results
{
    public static BenchmarkResult Make(double n1, double n16, double ttftP95At16 = 1.0, int qa = 8, bool tool = true, double ttftP50At1 = 0.2) =>
        new(
            [
                Level(1, n1, n1, ttftP50At1, ttftP50At1),
                Level(16, n16 / 16, n16, 0.5, ttftP95At16),
            ],
            new PrefillResult(60_000, 30, 1),
            new GuardrailResult(tool, tool ? "get_time({\"city\":\"Riyadh\"})" : "No tool call", qa, 8, []),
            NoiseReport.Quiet);

    /// <summary>
    /// A workload-preset (schema v2) result: repetition i (0-based) of every metric is the median × (1 + spread × (i − 1)),
    /// so the median is exact and the range is ±spread.
    /// </summary>
    public static BenchmarkResult Workload(
        double n1, double n16, double long16 = 2.0, double shared = 0.3, double spread = 0.01, int reps = 3, int qa = 8,
        double ttftP95At1 = 0.2, double ttftP95At16 = 0.8, int errors = 0, int toolsValid = 3, double? nMax = null, double? long64 = null,
        TuningGoal preset = TuningGoal.InteractiveChat, double mixed = 1.0, int? preemptions = 0)
    {
        double F(double median, int i) => median * (1 + (spread * (i - 1)));
        LevelResult GenLevel(int n, double perRequest, double aggregate, double p95, int levelErrors) => new(
            n,
            Enumerable.Range(0, reps).Select(i => new RunMeasurement(n, 256 * n, 10, F(perRequest, i), F(aggregate, i), 0.1, p95, 0, n, i == 0 ? levelErrors : 0)).ToList(),
            perRequest, aggregate, 0.1, p95, false, 256, levelErrors);

        var levels = new List<LevelResult> { GenLevel(1, n1, n1, ttftP95At1, 0), GenLevel(16, n16 / 16, n16, ttftP95At16, errors) };
        if (nMax is { } max)
        {
            levels.Add(GenLevel(32, max / 32, max, ttftP95At16, 0));
        }

        var longs = new List<LongPromptResult> { new(16_384, 16_300, Enumerable.Range(0, reps).Select(i => F(long16, i)).ToList(), long16, 0) };
        if (long64 is { } l64)
        {
            longs.Add(new LongPromptResult(65_536, 65_400, Enumerable.Range(0, reps).Select(i => F(l64, i)).ToList(), l64, 0));
        }

        var sharedPrefix = new SharedPrefixResult(8192, 4, Enumerable.Range(0, reps).Select(i => F(shared, i)).ToList(), shared, shared * 1.2, 4000, true,
            "shared prefix (cache reuse observed)", 0);
        var tools = new ToolCheckResult(toolsValid, 3, Enumerable.Range(0, 3).Select(i => new ToolCaseResult("get_time", i < toolsValid, i < toolsValid ? "ok" : "$.city is required but missing")).ToList());
        var mixedWorkload = new MixedWorkloadResult(16_384, 16, Enumerable.Range(0, reps).Select(i => F(mixed, i)).ToList(), mixed, n16 / 16, [4.0, 4.1, 4.2], 4.1, 0);
        return new BenchmarkResult(levels, null, new GuardrailResult(toolsValid == 3, "tools", qa, 8, []), NoiseReport.Quiet, null,
            3, preset, reps, longs, sharedPrefix, tools, mixedWorkload, preemptions, 0.6);
    }

    /// <summary>An embeddings benchmark result: input tok/s at N=1 and N=16, latency p95 at N=16, and the vector check.</summary>
    public static BenchmarkResult Embeddings(double in1, double in16, double p95At16 = 0.5, bool valid = true, bool normalized = true) =>
        new(
            [],
            null,
            new GuardrailResult(valid, valid ? "ok" : "Invalid embeddings: 3 vectors for 4 inputs", 0, 0, []),
            NoiseReport.Quiet,
            new EmbeddingResult(4,
            [
                new EmbeddingLevelResult(1, [], in1, in1 / 1000, 0.1, 0.1, false),
                new EmbeddingLevelResult(16, [], in16, in16 / 1000, 0.3, p95At16, false),
            ],
            new EmbeddingCheck(valid, valid ? "ok" : "Invalid embeddings: 3 vectors for 4 inputs", 1024, normalized)));

    public static LevelResult Level(int n, double perRequest, double aggregate, double p50, double p95) =>
        new(n, [new RunMeasurement(n, 256 * n, 10, perRequest, aggregate, p50, p95, 0, n)], perRequest, aggregate, p50, p95, false);

    public static ModelKpis Kpis(double running, double waiting = 0) => new(
        new ModelMetrics(running, waiting, 0.1, 0, 0), 0, null, null, null, null, null, null, 0, new Dictionary<string, double>(), 0);
}

/// <summary>
/// The benchmark target for job tests without agents: the model's container on the container network
/// (<c>http://ss-{id}:8000/</c>, the pre-P1 direct rule), recorded as <see cref="BenchmarkDataPaths.Direct"/>; the suite's
/// own client. The master's resolver (<see cref="AgentJobTargets"/>) is covered by <c>RemoteJobTests</c>.
/// </summary>
internal sealed class ContainerNetworkTargets : IBenchmarkTargetResolver
{
    public Task<BenchmarkEndpoint> ResolveAsync(string modelId, ModelStatus status, CancellationToken ct = default) =>
        Task.FromResult(new BenchmarkEndpoint(new Uri($"http://ss-{modelId}:8000/"), BenchmarkDataPaths.Direct));

    public HttpClient? GetClient(Uri baseUri) => null;
}
