using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Recovery;
using MirbadLLM.UnitTests.Downloads.Fakes;
using MirbadLLM.UnitTests.Router.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

namespace MirbadLLM.UnitTests.Recovery;

/// <summary>The restore preview: how each model of a backup compares with this box and what has to be downloaded.</summary>
public sealed class RestorePlannerTests
{
    private static ModelDefinition Model(string id, string path, bool autoStart = false, params ModelSource[] sources) => new()
    {
        Id = id,
        ServedName = id.ToUpperInvariant(),
        ModelPath = path,
        MemoryBudgetGiB = 20,
        AutoStart = autoStart,
        Sources = sources,
    };

    [Fact]
    public async Task Preview_ClassifiesNewChangedIdentical_AndChecksFoldersAndSizes()
    {
        var identical = Model("same", "Same-1B", false, new ModelSource("o/Same-1B", "Same-1B"));
        var changedHere = Model("changed", "Changed-1B") with { MemoryBudgetGiB = 10 };
        var changedInBackup = Model("changed", "Changed-1B", false, new ModelSource("o/Changed-1B", "Changed-1B")) with { Tags = ["chat"] };
        var fresh = Model("fresh", "Fresh-9B", true, new ModelSource("o/Fresh-9B", "Fresh-9B"), new ModelSource("o/Draft", "Draft-0.6B"));
        var manual = Model("manual", "Manual-4B");

        var registry = new FakeModelRegistry([identical, changedHere]);
        var keys = new FakeApiKeyStore();
        var existingKey = keys.AddActiveKey("ssk_existing", ApiKeyScope.Inference);
        var inspector = new FakeHuggingFaceRepoInspector();
        RepoFile[] files = [new("model.safetensors", 5_000), new("config.json", 10), new("README.md", 99)];
        inspector.Seed("o/Fresh-9B", new RepoInspection("o/Fresh-9B", null, RepoFormat.Safetensors, files, [], [], files));
        inspector.SeedError("o/Draft", new HuggingFaceApiException("Gated repo."));
        var planner = new RestorePlanner(registry, keys, new FakeCheckpointFolders("Same-1B", "Changed-1B"), inspector);
        var backup = new BackupDocument
        {
            Models = [identical, changedInBackup, fresh, manual],
            Secrets = new EncryptedSection("", "", 0, "", "", "", ""),
        };
        var secrets = new BackupSecrets([
            existingKey,
            new ApiKeyRecord("new-key", "laptop", ApiKeyScope.Inference, new string('a', 64), "abcd1234", DateTimeOffset.UtcNow, null, null),
        ]);

        var preview = await planner.PreviewAsync(backup, secrets);

        var byId = preview.Models.ToDictionary(m => m.Model.Id);
        Assert.Equal(RestoreModelChange.Identical, byId["same"].Change);
        Assert.False(byId["same"].NeedsDownload);
        Assert.True(byId["same"].CheckpointPresent);

        Assert.Equal(RestoreModelChange.Changed, byId["changed"].Change);
        Assert.Equal(["memory budget", "tags", "sources"], byId["changed"].ChangedFields);

        var freshPreview = byId["fresh"];
        Assert.Equal(RestoreModelChange.New, freshPreview.Change);
        Assert.False(freshPreview.CheckpointPresent);
        Assert.False(freshPreview.Manual);
        Assert.True(freshPreview.NeedsDownload);
        Assert.Equal(5_010, freshPreview.Sources[0].DownloadBytes); // default allow-patterns skip README.md
        Assert.Null(freshPreview.Sources[1].DownloadBytes);         // o/Draft is gated: no size
        Assert.Equal("Gated repo.", freshPreview.Sources[1].EstimateError);
        Assert.True(freshPreview.DownloadSizeUnknown);
        Assert.Equal(5_010, freshPreview.DownloadBytes);

        Assert.True(byId["manual"].Manual);
        Assert.Equal(1, preview.ApiKeysNew);
        Assert.Equal(1, preview.ApiKeysExisting);
        Assert.True(preview.HasSecrets);
        Assert.True(preview.SecretsUnlocked);
    }

    [Fact]
    public async Task Preview_FlagsAServedNameClashAndInvalidDefinitions()
    {
        var here = new ModelDefinition { Id = "here", ServedName = "Shared", ModelPath = "Here" };
        var clash = new ModelDefinition { Id = "other", ServedName = "shared", ModelPath = "Other" };
        var invalid = new ModelDefinition { Id = "Bad Id", ServedName = "Bad", ModelPath = "Bad" };
        var planner = new RestorePlanner(new FakeModelRegistry([here]), new FakeApiKeyStore(), new FakeCheckpointFolders());

        var preview = await planner.PreviewAsync(new BackupDocument { Models = [clash, invalid] }, secrets: null);

        Assert.Contains("already used by model 'here'", preview.Models[0].Problem, StringComparison.Ordinal);
        Assert.Contains("invalid", preview.Models[1].Problem, StringComparison.Ordinal);
        Assert.False(preview.SecretsUnlocked);
        Assert.Equal(0, preview.ApiKeysNew);
    }
}

/// <summary>Applying a restore: definitions, keys, strictly sequential downloads, and auto-start only once everything is on disk.</summary>
public sealed class RestoreJobServiceTests
{
    private static readonly RestoreJobOptions FastOptions = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(1),
        StartRetryInterval = TimeSpan.FromMilliseconds(5),
        StartRetryTimeout = TimeSpan.FromSeconds(10),
    };

    private sealed record Harness(
        RestoreJobService Service,
        FakeModelRegistry Registry,
        FakeApiKeyStore Keys,
        SequencedDownloader Downloader,
        ScriptedRuntime Runtime,
        FakeCheckpointFolders Folders,
        List<string> Events);

    private static Harness Create(IEnumerable<ModelDefinition>? existing = null, RestoreJobOptions? options = null, params string[] present)
    {
        var events = new List<string>();
        var folders = new FakeCheckpointFolders(present);
        var downloader = new SequencedDownloader(folders);
        var runtime = new ScriptedRuntime(events);
        var registry = new FakeModelRegistry(existing ?? []);
        var keys = new FakeApiKeyStore();
        var service = new RestoreJobService(registry, keys, runtime, downloader, folders, options ?? FastOptions, TimeProvider.System,
            NullLogger<RestoreJobService>.Instance);
        return new Harness(service, registry, keys, downloader, runtime, folders, events);
    }

    private static ModelDefinition Model(string id, bool autoStart, params ModelSource[] sources) => new()
    {
        Id = id,
        ServedName = id,
        ModelPath = sources.Length > 0 ? sources[0].Folder : $"{id}-folder",
        MemoryBudgetGiB = 20,
        AutoStart = autoStart,
        Sources = sources,
    };

    private static async Task<RestoreJobSnapshot> RunAsync(Harness h, RestoreRequest request)
    {
        h.Service.Start(request);
        await h.Service.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        return h.Service.Current!;
    }

    private static RestoreModelProgress Row(RestoreJobSnapshot job, string id) => job.Models.Single(m => m.Id == id);

    [Fact]
    public async Task Apply_UpsertsMergesKeysDownloadsOneAtATime_AndStartsOnlyCompleteAutoStartModels()
    {
        var a = Model("a", true, new ModelSource("o/A", "A"), new ModelSource("o/A-draft", "A-draft"));
        var b = Model("b", false, new ModelSource("o/B", "B"));
        var c = Model("c", true, new ModelSource("o/C", "C"));
        var h = Create();
        h.Downloader.FailFolders.Add("C");
        var existingKey = h.Keys.AddActiveKey("ssk_here", ApiKeyScope.Inference);
        var newKey = new ApiKeyRecord("k-new", "laptop", ApiKeyScope.Inference, new string('b', 64), "bbbb", DateTimeOffset.UtcNow, null, null);
        var backup = new BackupDocument { Models = [a, b, c] };

        var job = await RunAsync(h, new RestoreRequest(backup, new BackupSecrets([existingKey, newKey]), ["a", "b", "c"], true, true, false));

        Assert.Equal(RestoreJobState.Completed, job.State);
        Assert.Equal(3, (await h.Registry.ListAsync()).Count);
        Assert.Equal(["k-new"], h.Keys.Imported.Select(k => k.Id));
        Assert.Contains(job.Notes, n => n.Contains("1 imported, 1 already here", StringComparison.Ordinal));

        // One download at a time, in order; a's model starts only after both of its folders are present.
        Assert.Equal(1, h.Downloader.MaxConcurrent);
        Assert.Equal(["A", "A-draft", "B", "C"], h.Downloader.Started);
        int downloadedDraft = h.Downloader.Events.IndexOf("downloaded:A-draft");
        Assert.True(downloadedDraft >= 0);
        Assert.Equal(["start:a"], h.Events);

        Assert.Equal(RestoreModelState.Started, Row(job, "a").State);
        Assert.Equal(RestoreModelState.Ready, Row(job, "b").State);
        Assert.Equal(RestoreModelState.Failed, Row(job, "c").State);
        Assert.Contains("HTTP 404", Row(job, "c").Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("c", h.Runtime.StartAttempts);
    }

    [Fact]
    public async Task Apply_RetriesAStartConflict_ThenStarts()
    {
        var h = Create(present: ["A"]);
        h.Runtime.ConflictsBeforeStart = 2;
        var backup = new BackupDocument { Models = [Model("a", true, new ModelSource("o/A", "A"))] };

        var job = await RunAsync(h, new RestoreRequest(backup, null, ["a"], true, false, false));

        Assert.Equal(["a", "a", "a"], h.Runtime.StartAttempts);
        Assert.Equal(RestoreModelState.Started, Row(job, "a").State);
        Assert.Empty(h.Downloader.Started); // already on disk
    }

    [Fact]
    public async Task Apply_MemoryAdmissionFailure_LeavesTheModelStoppedAndReportsIt()
    {
        var h = Create(present: ["A"]);
        h.Runtime.RefuseMemory.Add("a");
        var backup = new BackupDocument { Models = [Model("a", true, new ModelSource("o/A", "A"))] };

        var job = await RunAsync(h, new RestoreRequest(backup, null, ["a"], true, false, false));

        var row = Row(job, "a");
        Assert.Equal(RestoreModelState.NotStarted, row.State);
        Assert.Contains("90 GiB", row.Detail, StringComparison.Ordinal);
        Assert.Empty(h.Events);
    }

    [Fact]
    public async Task Apply_SkipsExistingWhenAsked_ReportsManualAndUnselectedModels()
    {
        var existing = Model("keep", false, new ModelSource("o/Keep", "Keep")) with { MemoryBudgetGiB = 99 };
        var h = Create([existing]);
        var backup = new BackupDocument
        {
            Models = [Model("keep", true, new ModelSource("o/Keep", "Keep")), Model("manual", true), Model("unselected", true, new ModelSource("o/U", "U"))],
        };

        var job = await RunAsync(h, new RestoreRequest(backup, null, ["keep", "manual"], ReplaceExisting: false, false, false));

        Assert.Equal(99, (await h.Registry.GetAsync("keep"))!.MemoryBudgetGiB);
        Assert.Equal(RestoreModelState.Skipped, Row(job, "keep").State);
        Assert.Equal(RestoreModelState.Manual, Row(job, "manual").State);
        Assert.Contains("/models/manual-folder", Row(job, "manual").Detail, StringComparison.Ordinal);
        Assert.Equal(RestoreModelState.Skipped, Row(job, "unselected").State);
        Assert.Null(await h.Registry.GetAsync("unselected"));
        Assert.Empty(h.Downloader.Started);
        Assert.Empty(h.Runtime.StartAttempts);
    }

    [Fact]
    public async Task Cancel_StopsTheDownloadInFlight_AndMarksTheRestCancelled()
    {
        var h = Create(options: new RestoreJobOptions { PollInterval = TimeSpan.FromMilliseconds(20) });
        h.Downloader.PollsToComplete = int.MaxValue;
        var backup = new BackupDocument { Models = [Model("a", true, new ModelSource("o/A", "A")), Model("b", true, new ModelSource("o/B", "B"))] };

        h.Service.Start(new RestoreRequest(backup, null, ["a", "b"], true, false, false));
        await WaitUntilAsync(() => h.Service.Current!.Models[0].State == RestoreModelState.Downloading);
        await h.Service.CancelAsync();
        var job = h.Service.Current!;

        Assert.Equal(RestoreJobState.Cancelled, job.State);
        Assert.Contains("cancel:A", h.Downloader.Events);
        Assert.All(job.Models, m => Assert.Equal(RestoreModelState.Cancelled, m.State));
        Assert.Empty(h.Runtime.StartAttempts);
    }

    [Fact]
    public async Task Start_RefusesASecondRestoreWhileOneRuns()
    {
        var h = Create(options: new RestoreJobOptions { PollInterval = TimeSpan.FromMilliseconds(20) });
        h.Downloader.PollsToComplete = int.MaxValue;
        var request = new RestoreRequest(new BackupDocument { Models = [Model("a", false, new ModelSource("o/A", "A"))] }, null, ["a"], true, false, false);

        h.Service.Start(request);
        Assert.Throws<InvalidOperationException>(() => h.Service.Start(request));
        await h.Service.CancelAsync();
        h.Service.Clear();
        Assert.Null(h.Service.Current);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met.");
            }

            await Task.Delay(5);
        }
    }
}
