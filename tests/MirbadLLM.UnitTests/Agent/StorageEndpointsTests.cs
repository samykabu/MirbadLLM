using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Api;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Storage;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707, CA2000, xUnit1051 // underscore test names; the app owns its services; hand-written fakes ignore the test token.

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// The agent storage package (MN-020): <c>/images</c>, <c>/model-folders</c> and <c>/downloads</c> over an in-process
/// host with hand-written fakes for the operation log, fencing, event bus, runtime driver, container helper and image
/// engine. Covers path confinement (<c>..</c>, absolute, symlinks, in-use), the HF token never reaching operation
/// records or logs (digest computed without it), download lifecycle/cancel/recovery, start-up removal of finished
/// helpers (real <c>HuggingFaceDownloader</c> over a fake Docker API) and image pulls as operations with progress.
/// </summary>
public sealed class StorageEndpointsTests : IAsyncDisposable
{
    private const string Token = "hf_SuperSecretToken1234567890";
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly MasterTriple Master = new("master-1", 3, "boot-a");
    private static readonly string[] RefusedIds = ["a", "b", "c", "d"];

    private readonly string _root;
    private readonly string _outside;
    private readonly FakeOperationLog _log = new();
    private readonly FakeFencing _fencing = new();
    private readonly FakeEventBus _events = new();
    private readonly FakeDriver _driver = new();
    private readonly FakeDownloader _downloader = new();
    private readonly FakeRepoInspector _inspector = new();
    private readonly FakeDisk _disk = new();
    private readonly FakeImageEngine _images = new();
    private readonly CapturingLoggerProvider _logs = new();
    private WebApplication? _app;
    private HttpClient? _client;

    public StorageEndpointsTests()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "mirbadllm-agent-storage-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "models");
        _outside = Path.Combine(baseDir, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        string baseDir = Path.GetDirectoryName(_root)!;
        if (Directory.Exists(baseDir))
        {
            try
            {
                Directory.Delete(baseDir, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    private async Task<HttpClient> StartAsync(Action<AgentStorageOptions>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(_logs);
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Services.AddSingleton<IOperationLog>(_log);
        builder.Services.AddSingleton<IFencingState>(_fencing);
        builder.Services.AddSingleton<IEventBus>(_events);
        builder.Services.AddSingleton<IRuntimeDriver>(_driver);
        builder.Services.AddSingleton<IHuggingFaceDownloader>(_downloader);
        builder.Services.AddSingleton<IHuggingFaceRepoInspector>(_inspector);
        builder.Services.AddSingleton<IDiskSpaceProbe>(_disk);
        builder.Services.AddSingleton<IContainerImageEngine>(_images);
        builder.Services.AddAgentStorage(options =>
        {
            options.ModelsRoot = _root;
            options.DownloadPollInterval = TimeSpan.FromMilliseconds(10);
            options.ProgressRecordInterval = TimeSpan.Zero;
            options.ApprovedImages.Add("local/vllm@" + Digest);
            configure?.Invoke(options);
        });

        _app = builder.Build();
        _app.MapGroup(Protocol.BasePath).MapStorageEndpoints();
        await _app.StartAsync();
        _client = _app.GetTestClient();
        return _client;
    }

    private T Service<T>()
        where T : notnull => _app!.Services.GetRequiredService<T>();

    private static Dictionary<string, object?> Envelope(string operationId, object extra, DateTimeOffset? issuedAt = null)
    {
        var issued = issuedAt ?? DateTimeOffset.UtcNow;
        var body = new Dictionary<string, object?>
        {
            ["operationId"] = operationId,
            ["issuedAt"] = issued,
            ["expiresAt"] = issued + OperationRetention.EnvelopeLifetime,
        };
        foreach (var property in JsonSerializer.SerializeToElement(extra, AgentJson.Options).EnumerateObject())
        {
            body[property.Name] = property.Value;
        }

        return body;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body, MasterTriple? master = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Protocol.BasePath + path)
        {
            Content = JsonContent.Create(body, options: AgentJson.Options),
        };
        if ((master ?? Master) is { } triple)
        {
            request.Headers.Add(Protocol.MasterHeader, triple.ToHeaderValue());
        }

        return client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(AgentJson.Options))!;

    private static async Task<AgentError> ReadErrorAsync(HttpResponseMessage response) =>
        (await ReadAsync<AgentErrorResponse>(response)).Error;

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting for " + what);
            }

            await Task.Delay(10);
        }
    }

    private string MakeFolder(string name, bool withConfig = true, int weightBytes = 1000)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        if (withConfig)
        {
            File.WriteAllText(Path.Combine(dir, "config.json"), """{"architectures":["Qwen3ForCausalLM"],"model_type":"qwen3","torch_dtype":"bfloat16","max_position_embeddings":4096}""");
        }

        File.WriteAllBytes(Path.Combine(dir, "model.safetensors"), new byte[weightBytes]);
        return dir;
    }

    private static bool TryCreateDirectorySymlink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- path confinement (unit)

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../etc")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("/etc")]
    [InlineData("C:\\Windows")]
    [InlineData("C:")]
    [InlineData(".hidden")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad\0name")]
    [InlineData("trailing.")]
    public void NameError_RefusesTraversalAbsoluteAndHiddenNames(string folder)
    {
        Assert.NotNull(ModelRootPaths.NameError(folder));
        var paths = new ModelRootPaths(_root);
        var ex = Assert.Throws<AgentException>(() => paths.Locate(folder));
        Assert.Equal(ErrorCodes.InvalidRequest, ex.Code);
        Assert.Equal(400, ex.StatusCode);
    }

    [Theory]
    [InlineData("Qwen3.6-35B-A3B-NVFP4")]
    [InlineData("gemma_3-27b")]
    [InlineData("model.partial")]
    public void NameError_AcceptsPlainFolderNames(string folder)
    {
        Assert.Null(ModelRootPaths.NameError(folder));
        Assert.Null(ModelRootPaths.DownloadNameError(folder));
        var location = new ModelRootPaths(_root).Locate(folder);
        Assert.Equal(Path.Combine(_root, folder), location.Path);
        Assert.False(location.Exists);
    }

    [Fact]
    public void DownloadNameError_RefusesCharactersTheHelperWouldRewrite() =>
        Assert.NotNull(ModelRootPaths.DownloadNameError("my model"));

    [Fact]
    public void Locate_LinkEscapingTheRoot_HasNoResolvedPath()
    {
        string link = Path.Combine(_root, "escape");
        if (!TryCreateDirectorySymlink(link, _outside))
        {
            Assert.Skip("Creating symbolic links needs privileges on this machine.");
        }

        var location = new ModelRootPaths(_root).Locate("escape");
        Assert.True(location.IsLink);
        Assert.Null(location.ResolvedPath);
    }

    [Fact]
    public void Locate_LinkInsideTheRoot_ResolvesToItsTarget()
    {
        string target = MakeFolder("real");
        string link = Path.Combine(_root, "alias");
        if (!TryCreateDirectorySymlink(link, target))
        {
            Assert.Skip("Creating symbolic links needs privileges on this machine.");
        }

        var location = new ModelRootPaths(_root).Locate("alias");
        Assert.True(location.IsLink);
        Assert.Equal(Path.GetFullPath(target), location.ResolvedPath);
    }

    [Fact]
    public void DeleteTree_RemovesLinksInsideWithoutTouchingTheirTargets()
    {
        string dir = MakeFolder("victim");
        string precious = Path.Combine(_outside, "precious.bin");
        File.WriteAllText(precious, "keep me");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        if (!TryCreateDirectorySymlink(Path.Combine(dir, "sub", "link-to-outside"), _outside))
        {
            Assert.Skip("Creating symbolic links needs privileges on this machine.");
        }

        ModelRootPaths.DeleteTree(dir);

        Assert.False(Directory.Exists(dir));
        Assert.True(File.Exists(precious));
        Assert.Equal("keep me", File.ReadAllText(precious));
    }

    [Fact]
    public void SizeOf_DoesNotCountBytesBehindLinks()
    {
        string dir = MakeFolder("sized", withConfig: false, weightBytes: 100);
        File.WriteAllBytes(Path.Combine(_outside, "big.bin"), new byte[10_000]);
        bool linked = TryCreateDirectorySymlink(Path.Combine(dir, "link"), _outside);

        Assert.Equal(100, ModelRootPaths.SizeOf(dir));
        if (!linked)
        {
            Assert.Skip("Creating symbolic links needs privileges on this machine (size without links verified).");
        }
    }

    // ---------------------------------------------------------------- redaction (unit)

    [Fact]
    public void PayloadRedaction_ReplacesSecretsAtAnyDepth()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            operationId = "op",
            token = Token,
            nested = new { settings = new { Password = "pw", user = "u" }, list = new[] { new { token = "x" } } },
            empty = (string?)null,
        });

        string redacted = PayloadRedaction.Redact(payload).GetRawText();

        Assert.DoesNotContain(Token, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("\"pw\"", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("\"x\"", redacted, StringComparison.Ordinal);
        Assert.Contains("\"u\"", redacted, StringComparison.Ordinal);
        Assert.Contains("\"empty\":null", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactSecrets_ScrubsTheTokenAndTokenShapedText()
    {
        string? text = StorageErrors.RedactSecrets($"401 for Bearer {Token}; HF_TOKEN=abc123 and plain {Token} hf_AnotherOne123456", "abc123");
        Assert.DoesNotContain(Token, text!, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", text!, StringComparison.Ordinal);
        Assert.DoesNotContain("hf_AnotherOne123456", text!, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- envelope / fencing

    [Fact]
    public async Task Mutation_WithoutMasterHeader_IsRefusedAndNothingIsAccepted()
    {
        var client = await StartAsync();
        MakeFolder("m1");
        var request = new HttpRequestMessage(HttpMethod.Post, Protocol.BasePath + "/model-folders/m1/delete")
        {
            Content = JsonContent.Create(Envelope("op-1", new { confirm = "m1" }), options: AgentJson.Options),
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidRequest, (await ReadErrorAsync(response)).Code);
        Assert.Empty(_log.Submissions);
        Assert.True(Directory.Exists(Path.Combine(_root, "m1")));
    }

    [Theory]
    [InlineData(FencingDecision.StaleMaster, ErrorCodes.StaleMaster)]
    [InlineData(FencingDecision.CompetingMaster, ErrorCodes.CompetingMaster)]
    public async Task Mutation_FromFencedMaster_IsRefused(FencingDecision decision, string code)
    {
        var client = await StartAsync();
        MakeFolder("m1");
        _fencing.Decision = decision;

        var response = await PostAsync(client, "/model-folders/m1/delete", Envelope("op-1", new { confirm = "m1" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(code, (await ReadErrorAsync(response)).Code);
        Assert.Empty(_log.Submissions);
        Assert.True(Directory.Exists(Path.Combine(_root, "m1")));
    }

    [Fact]
    public async Task Mutation_WithExpiredEnvelope_Is410AndNotRun()
    {
        var client = await StartAsync();
        MakeFolder("m1");

        var response = await PostAsync(client, "/model-folders/m1/delete",
            Envelope("op-old", new { confirm = "m1" }, DateTimeOffset.UtcNow - TimeSpan.FromHours(25)));

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal(ErrorCodes.OperationExpired, (await ReadErrorAsync(response)).Code);
        Assert.True(Directory.Exists(Path.Combine(_root, "m1")));
    }

    [Fact]
    public async Task Mutation_WhenTheLogCannotBeWritten_Is503AndNothingRuns()
    {
        var client = await StartAsync();
        MakeFolder("m1");
        _log.FailAccept = true;

        var response = await PostAsync(client, "/model-folders/m1/delete", Envelope("op-1", new { confirm = "m1" }));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ErrorCodes.StateUnavailable, (await ReadErrorAsync(response)).Code);
        Assert.True(Directory.Exists(Path.Combine(_root, "m1")));
    }

    [Fact]
    public async Task Mutation_MalformedBody_Is400()
    {
        var client = await StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, Protocol.BasePath + "/downloads")
        {
            Content = new StringContent("""{"operationId":"x"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(Protocol.MasterHeader, Master.ToHeaderValue());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidRequest, (await ReadErrorAsync(response)).Code);
    }

    // ---------------------------------------------------------------- folders

    [Fact]
    public async Task ListFolders_ReportsSizeConfigPartialAndSkipsHidden()
    {
        var client = await StartAsync();
        MakeFolder("ready-model", withConfig: true, weightBytes: 2048);
        MakeFolder("no-config", withConfig: false, weightBytes: 10);
        MakeFolder("half.partial", withConfig: false, weightBytes: 5);
        Directory.CreateDirectory(Path.Combine(_root, ".cache"));
        _downloader.Seed(new DownloadStatus("no-config", "org/x", null, DownloadState.Downloading, 10, 100, null, null));

        var folders = await ReadAsync<List<ModelFolderEntry>>(await client.GetAsync(Protocol.BasePath + "/model-folders"));

        Assert.Equal(["half.partial", "no-config", "ready-model"], folders.Select(f => f.Folder).ToArray());
        var ready = folders.Single(f => f.Folder == "ready-model");
        Assert.True(ready.HasConfig);
        Assert.False(ready.Partial);
        Assert.True(ready.SizeBytes >= 2048);
        Assert.Equal("Qwen3ForCausalLM", ready.Architecture);
        Assert.True(folders.Single(f => f.Folder == "half.partial").Partial);
        Assert.True(folders.Single(f => f.Folder == "no-config").Partial);
        Assert.False(folders.Single(f => f.Folder == "no-config").HasConfig);
    }

    [Fact]
    public async Task ListFolders_SkipsLinksThatLeaveTheRoot()
    {
        var client = await StartAsync();
        MakeFolder("inside");
        if (!TryCreateDirectorySymlink(Path.Combine(_root, "escape"), _outside))
        {
            Assert.Skip("Creating symbolic links needs privileges on this machine.");
        }

        var folders = await ReadAsync<List<ModelFolderEntry>>(await client.GetAsync(Protocol.BasePath + "/model-folders"));

        Assert.Equal(["inside"], folders.Select(f => f.Folder).ToArray());
    }

    [Fact]
    public async Task InspectFolder_ReturnsTodaysInspection_And404WithoutConfig()
    {
        var client = await StartAsync();
        MakeFolder("ready-model");
        MakeFolder("no-config", withConfig: false);

        var inspection = await ReadAsync<ModelFolderInspection>(await client.GetAsync(Protocol.BasePath + "/model-folders/ready-model"));
        var missing = await client.GetAsync(Protocol.BasePath + "/model-folders/no-config");
        var absent = await client.GetAsync(Protocol.BasePath + "/model-folders/nothing-here");
        var traversal = await client.GetAsync(Protocol.BasePath + "/model-folders/..%2F..%2Fetc");

        Assert.Equal("ready-model", inspection.Folder);
        Assert.Equal("Qwen3ForCausalLM", inspection.Architecture);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, traversal.StatusCode);
    }

    [Fact]
    public async Task DeleteFolder_Succeeds_AsADurableOperation()
    {
        var client = await StartAsync();
        MakeFolder("old-model");

        var response = await PostAsync(client, "/model-folders/old-model/delete", Envelope("del-1", new { confirm = "old-model" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await ReadAsync<OperationStatus>(response);
        Assert.Equal(OperationState.Succeeded, status.State);
        Assert.Equal(OperationKinds.FolderDelete, status.Kind);
        Assert.Equal("old-model", status.Target);
        Assert.False(Directory.Exists(Path.Combine(_root, "old-model")));
        Assert.Contains(OperationStages.Deleting, _log.StagesOf("del-1"));
        Assert.Contains(_events.Events.OfType<OperationEvent>(), e => e.Operation.Id == "del-1" && e.Operation.State == OperationState.Succeeded);
    }

    [Fact]
    public async Task DeleteFolder_Replay_ReturnsTheStoredResult_AndOtherPayloadConflicts()
    {
        var client = await StartAsync();
        MakeFolder("old-model");
        var body = Envelope("del-1", new { confirm = "old-model" });

        var first = await PostAsync(client, "/model-folders/old-model/delete", body);
        MakeFolder("old-model");
        var replay = await PostAsync(client, "/model-folders/old-model/delete", body);
        var conflict = await PostAsync(client, "/model-folders/other/delete", Envelope("del-1", new { confirm = "other" }));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(OperationState.Succeeded, (await ReadAsync<OperationStatus>(replay)).State);
        Assert.True(Directory.Exists(Path.Combine(_root, "old-model")), "a replay must not run the delete again");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(ErrorCodes.OperationIdConflict, (await ReadErrorAsync(conflict)).Code);
    }

    [Theory]
    [InlineData("..%2F..%2Fetc")]
    [InlineData("..%5C..%5Cetc")]
    [InlineData(".ssh")]
    public async Task DeleteFolder_RefusesTraversal(string encoded)
    {
        var client = await StartAsync();
        string decoded = Uri.UnescapeDataString(encoded);

        var response = await PostAsync(client, $"/model-folders/{encoded}/delete", Envelope("del-x", new { confirm = decoded }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidRequest, (await ReadErrorAsync(response)).Code);
        Assert.True(Directory.Exists(_outside));
        var stored = _log.Find("del-x");
        Assert.NotNull(stored);
        Assert.Equal(OperationState.Failed, stored.State);
    }

    [Fact]
    public async Task DeleteFolder_RefusesASymlinkedFolder_AndKeepsItsTarget()
    {
        var client = await StartAsync();
        string target = MakeFolder("real-model");
        if (!TryCreateDirectorySymlink(Path.Combine(_root, "alias"), target))
        {
            Assert.Skip("Creating symbolic links needs privileges on this machine.");
        }

        var response = await PostAsync(client, "/model-folders/alias/delete", Envelope("del-l", new { confirm = "alias" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("link", (await ReadErrorAsync(response)).Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(target, "config.json")));
        Assert.True(Directory.Exists(Path.Combine(_root, "alias")));
    }

    [Fact]
    public async Task DeleteFolder_RefusesAFolderUsedByAnInstance()
    {
        var client = await StartAsync();
        MakeFolder("live-model");
        _driver.FoldersInUse.Add("live-model");

        var response = await PostAsync(client, "/model-folders/live-model/delete", Envelope("del-u", new { confirm = "live-model" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ErrorCodes.InUse, (await ReadErrorAsync(response)).Code);
        Assert.True(Directory.Exists(Path.Combine(_root, "live-model")));
        Assert.Equal(OperationState.Failed, _log.Find("del-u")!.State);

        // The stored refusal answers a replay with the same error.
        var replay = await PostAsync(client, "/model-folders/live-model/delete", Envelope("del-u", new { confirm = "live-model" }, _log.Find("del-u")!.IssuedAt));
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
    }

    [Fact]
    public async Task DeleteFolder_RefusesAFolderADownloadWritesInto()
    {
        var client = await StartAsync();
        MakeFolder("incoming");
        _downloader.Seed(new DownloadStatus("incoming", "org/x", null, DownloadState.Downloading, 10, 100, null, null));

        var response = await PostAsync(client, "/model-folders/incoming/delete", Envelope("del-d", new { confirm = "incoming" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ErrorCodes.InUse, (await ReadErrorAsync(response)).Code);
        Assert.True(Directory.Exists(Path.Combine(_root, "incoming")));
    }

    [Fact]
    public async Task DeleteFolder_RefusesAWrongConfirmation_AndAMissingFolder()
    {
        var client = await StartAsync();
        MakeFolder("old-model");

        var wrong = await PostAsync(client, "/model-folders/old-model/delete", Envelope("del-c", new { confirm = "OLD-MODEL" }));
        var missing = await PostAsync(client, "/model-folders/gone/delete", Envelope("del-g", new { confirm = "gone" }));

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.True(Directory.Exists(Path.Combine(_root, "old-model")));
    }

    [Fact]
    public async Task DeleteFolder_SupersededBeforeTheDestructiveStage_IsRefused()
    {
        var client = await StartAsync();
        MakeFolder("old-model");
        _fencing.Current = false;

        var response = await PostAsync(client, "/model-folders/old-model/delete", Envelope("del-s", new { confirm = "old-model" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ErrorCodes.Superseded, (await ReadErrorAsync(response)).Code);
        Assert.True(Directory.Exists(Path.Combine(_root, "old-model")));
    }

    // ---------------------------------------------------------------- downloads

    private static Dictionary<string, object?> DownloadBody(string operationId, string folder = "Ornith-9B", string? token = Token, string existing = "refuse") =>
        Envelope(operationId, new { repoId = "ornith-ai/Ornith-1.5-9B", revision = (string?)null, folder, allowPatterns = (string[]?)null, token, existing });

    [Fact]
    public async Task StartDownload_Returns202_AndTheTokenNeverReachesTheOperationLogOrLogs()
    {
        var client = await StartAsync();
        var body = DownloadBody("dl-1");

        var response = await PostAsync(client, "/downloads", body);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var status = await ReadAsync<NodeDownloadStatus>(response);
        Assert.Equal("Ornith-9B", status.Folder);
        Assert.Equal("dl-1", status.OperationId);
        Assert.Equal(1000, status.TotalBytes);

        // The helper got the token (it puts it in HF_TOKEN), the pre-flight listing too…
        Assert.Equal(Token, Assert.Single(_downloader.StartCalls).Token);
        Assert.Equal(Token, Assert.Single(_inspector.Tokens));

        // …but no operation record, payload, digest, event or log line contains it.
        Assert.DoesNotContain(Token, _log.EverythingRecorded(), StringComparison.Ordinal);
        Assert.Contains(Protocol.RedactedValue, _log.Submissions.Single().Payload.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(_events.Events.Cast<object>().ToList(), AgentJson.Options), StringComparison.Ordinal);
        Assert.DoesNotContain(_logs.Lines, line => line.Contains(Token, StringComparison.Ordinal));
        Assert.Contains(_logs.Lines, line => line.Contains("dl-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartDownload_DigestIsComputedWithoutTheToken_SoARetryIsTheSameOperation()
    {
        var client = await StartAsync();
        var issued = DateTimeOffset.UtcNow;

        var first = await PostAsync(client, "/downloads", Envelope("dl-1", new { repoId = "ornith-ai/Ornith-1.5-9B", folder = "Ornith-9B", token = Token }, issued));
        var retryOtherToken = await PostAsync(client, "/downloads", Envelope("dl-1", new { repoId = "ornith-ai/Ornith-1.5-9B", folder = "Ornith-9B", token = "hf_rotatedTokenValue99" }, issued));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, retryOtherToken.StatusCode);
        Assert.Equal("dl-1", (await ReadAsync<NodeDownloadStatus>(retryOtherToken)).OperationId);
        Assert.Single(_downloader.StartCalls);
        Assert.Single(_log.Submissions.Select(s => s.OperationId).Distinct());
        string digest = _log.Find("dl-1")!.PayloadDigest;
        Assert.Equal(FakeOperationLog.DigestOf(PayloadRedaction.Redact(JsonSerializer.SerializeToElement(
            Envelope("dl-1", new { repoId = "ornith-ai/Ornith-1.5-9B", folder = "Ornith-9B", token = "anything" }, issued), AgentJson.Options))), digest);
    }

    [Fact]
    public async Task Download_Completes_WithProgressEventsAndTheFinalStatusAsResult()
    {
        var client = await StartAsync();
        var body = DownloadBody("dl-1");
        await PostAsync(client, "/downloads", body);

        _downloader.Set("Ornith-9B", DownloadState.Downloading, 400);
        await WaitUntilAsync(() => _events.Events.OfType<DownloadEvent>().Any(e => e.Download.BytesDownloaded == 400), "progress event");
        _downloader.Set("Ornith-9B", DownloadState.Completed, 1000);
        await WaitUntilAsync(() => _log.Find("dl-1")!.State == OperationState.Succeeded, "completion");

        var op = _log.Find("dl-1")!;
        var result = op.ReadResult<NodeDownloadStatus>()!;
        Assert.Equal(DownloadState.Completed, result.State);
        Assert.Equal(1000, result.BytesDownloaded);
        Assert.NotNull(result.EndedAt);
        Assert.Contains(OperationStages.Downloading, _log.StagesOf("dl-1"));
        Assert.Contains(_log.ProgressOf("dl-1"), p => p.BytesDone == 400 && p.BytesTotal == 1000 && p.Percent == 40);

        var listed = await ReadAsync<List<NodeDownloadStatus>>(await client.GetAsync(Protocol.BasePath + "/downloads"));
        Assert.Equal("dl-1", Assert.Single(listed).OperationId);
        var one = await ReadAsync<NodeDownloadStatus>(await client.GetAsync(Protocol.BasePath + "/downloads/Ornith-9B"));
        Assert.Equal(DownloadState.Completed, one.State);

        // A replay of the finished start answers with the stored result.
        var replay = await PostAsync(client, "/downloads", body);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(DownloadState.Completed, (await ReadAsync<NodeDownloadStatus>(replay)).State);
    }

    [Fact]
    public async Task Dispose_WaitsForAMonitorWritingItsTerminalRecord_BeforeTheLogCanBeDisposed()
    {
        // W8 review (MN-128): a monitor that saw its helper finish leaves _active, then writes the terminal record. Disposal
        // used to wait only for downloads in _active, so the container disposed the operation log under that write
        // (ObjectDisposedException at agent shutdown) and the operation stayed non-terminal for the next start.
        var client = await StartAsync();
        await PostAsync(client, "/downloads", DownloadBody("dl-1"));
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _log.HoldCompletion = hold;
        _downloader.Set("Ornith-9B", DownloadState.Completed, 1000);
        await _log.CompletionReached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var dispose = Service<AgentDownloadService>().DisposeAsync().AsTask();
        var first = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken));
        Assert.NotSame(dispose, first); // still waiting for the monitor's write
        hold.SetResult();
        await dispose.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _log.Disposed = true; // what the container does next

        Assert.Equal(OperationState.Succeeded, _log.Find("dl-1")!.State);
    }

    [Fact]
    public async Task Download_Failure_IsStoredWithTheTokenScrubbedFromHelperOutput()
    {
        var client = await StartAsync();
        await PostAsync(client, "/downloads", DownloadBody("dl-1"));

        _downloader.Set("Ornith-9B", DownloadState.Failed, 0, $"401 Client Error. Authorization: Bearer {Token} rejected");
        await WaitUntilAsync(() => _log.Find("dl-1")!.State == OperationState.Failed, "failure");

        var op = _log.Find("dl-1")!;
        Assert.Contains("401", op.Error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, _log.EverythingRecorded(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(_events.Events.Cast<object>().ToList(), AgentJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartDownload_Refusals()
    {
        var client = await StartAsync();
        MakeFolder("exists");
        _downloader.Seed(new DownloadStatus("busy", "org/x", null, DownloadState.Downloading, 1, 10, null, null));

        var exists = await PostAsync(client, "/downloads", DownloadBody("a", "exists"));
        var busy = await PostAsync(client, "/downloads", DownloadBody("b", "busy"));
        var badName = await PostAsync(client, "/downloads", DownloadBody("c", "../escape"));
        var spaces = await PostAsync(client, "/downloads", DownloadBody("d", "my model"));

        Assert.Equal(HttpStatusCode.Conflict, exists.StatusCode);
        Assert.Equal(ErrorCodes.FolderExists, (await ReadErrorAsync(exists)).Code);
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Equal(ErrorCodes.AlreadyRunning, (await ReadErrorAsync(busy)).Code);
        Assert.Equal(HttpStatusCode.BadRequest, badName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, spaces.StatusCode);
        Assert.Empty(_downloader.StartCalls);
        Assert.All(RefusedIds, id => Assert.Equal(OperationState.Failed, _log.Find(id)!.State));
    }

    [Fact]
    public async Task StartDownload_ExistingComplete_FetchesIntoAnExistingFolder()
    {
        var client = await StartAsync();
        MakeFolder("Ornith-9B", withConfig: true, weightBytes: 900);

        var response = await PostAsync(client, "/downloads", DownloadBody("dl-c", existing: "complete"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(_downloader.StartCalls);
    }

    [Fact]
    public async Task StartDownload_NoFilesMatch_Is422()
    {
        var client = await StartAsync();
        _inspector.Files = [new RepoFile("model-Q4_K_M.gguf", 500)];

        var response = await PostAsync(client, "/downloads", DownloadBody("dl-n"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ErrorCodes.NoFilesMatch, (await ReadErrorAsync(response)).Code);
        Assert.Empty(_downloader.StartCalls);
    }

    [Fact]
    public async Task StartDownload_NotEnoughDisk_Is507()
    {
        var client = await StartAsync();
        _disk.Space = new DiskSpace(FreeBytes: 1000, TotalBytes: 1_000_000);

        var response = await PostAsync(client, "/downloads", DownloadBody("dl-d"));

        Assert.Equal((HttpStatusCode)507, response.StatusCode);
        Assert.Equal(ErrorCodes.InsufficientDisk, (await ReadErrorAsync(response)).Code);
        Assert.Empty(_downloader.StartCalls);
    }

    [Fact]
    public async Task StartDownload_DockerDown_Is503()
    {
        var client = await StartAsync();
        _downloader.ThrowOnStart = new HttpRequestException("connection refused");

        var response = await PostAsync(client, "/downloads", DownloadBody("dl-x"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ErrorCodes.DockerUnavailable, (await ReadErrorAsync(response)).Code);
    }

    [Fact]
    public async Task StartDownload_IntoASymlinkedFolder_IsRefused()
    {
        var client = await StartAsync();
        if (!TryCreateDirectorySymlink(Path.Combine(_root, "Ornith-9B"), _outside))
        {
            Assert.Skip("Creating symbolic links needs privileges on this machine.");
        }

        var response = await PostAsync(client, "/downloads", DownloadBody("dl-l", existing: "complete"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_downloader.StartCalls);
    }

    [Fact]
    public async Task CancelDownload_StopsTheHelper_AndEndsTheDownloadCancelled()
    {
        var client = await StartAsync();
        await PostAsync(client, "/downloads", DownloadBody("dl-1"));
        _downloader.Set("Ornith-9B", DownloadState.Downloading, 300);

        var response = await PostAsync(client, "/downloads/Ornith-9B/cancel", Envelope("cancel-1", new { }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cancel = await ReadAsync<OperationStatus>(response);
        Assert.Equal(OperationState.Succeeded, cancel.State);
        Assert.Equal(DownloadState.Cancelled, cancel.ReadResult<NodeDownloadStatus>()!.State);
        Assert.Equal(OperationState.Cancelled, _log.Find("dl-1")!.State);
        Assert.Equal(["Ornith-9B"], _downloader.CancelCalls);

        var notFound = await PostAsync(client, "/downloads/never-started/cancel", Envelope("cancel-2", new { }));
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
    }

    [Fact]
    public async Task CancelDownload_AfterItFinished_SucceedsWithTheFinalState()
    {
        var client = await StartAsync();
        await PostAsync(client, "/downloads", DownloadBody("dl-1"));
        _downloader.Set("Ornith-9B", DownloadState.Completed, 1000);
        await WaitUntilAsync(() => _log.Find("dl-1")!.State == OperationState.Succeeded, "completion");

        var response = await PostAsync(client, "/downloads/Ornith-9B/cancel", Envelope("cancel-1", new { }));

        var cancel = await ReadAsync<OperationStatus>(response);
        Assert.Equal(OperationState.Succeeded, cancel.State);
        Assert.Equal(DownloadState.Completed, cancel.ReadResult<NodeDownloadStatus>()!.State);
        Assert.Empty(_downloader.CancelCalls);
    }

    [Fact]
    public async Task CancelByOperationId_CancelsTheDownload()
    {
        await StartAsync();
        var client = _client!;
        await PostAsync(client, "/downloads", DownloadBody("dl-1"));

        Assert.True(await Service<StorageOperationCanceller>().TryCancelAsync("dl-1"));
        Assert.False(await Service<StorageOperationCanceller>().TryCancelAsync("unknown"));
        Assert.Equal(OperationState.Cancelled, _log.Find("dl-1")!.State);
    }

    [Fact]
    public async Task Recovery_ResumesARunningHelper_AndFinishesIt()
    {
        await StartAsync();
        _downloader.Seed(new DownloadStatus("Ornith-9B", "ornith-ai/Ornith-1.5-9B", null, DownloadState.Downloading, 100, 1000, null, null));
        var op = _log.SeedActive("dl-r", OperationKinds.Download, "Ornith-9B", OperationStages.Downloading,
            JsonSerializer.SerializeToElement(new { repoId = "ornith-ai/Ornith-1.5-9B", folder = "Ornith-9B", token = Protocol.RedactedValue }));
        var handler = Service<IEnumerable<IOperationRecoveryHandler>>().Single(h => h.Kinds.Contains(OperationKinds.Download));

        await handler.RecoverAsync(op);
        Assert.True(await Service<IAgentDownloads>().IsFolderInUseAsync("Ornith-9B"));
        _downloader.Set("Ornith-9B", DownloadState.Completed, 1000);
        await WaitUntilAsync(() => _log.Find("dl-r")!.State == OperationState.Succeeded, "resumed completion");

        Assert.Equal("dl-r", _log.Find("dl-r")!.ReadResult<NodeDownloadStatus>()!.OperationId);
        Assert.Empty(_downloader.StartCalls);
    }

    [Fact]
    public async Task Recovery_MissingHelper_FailsTheDownload_OrCancelsItWhenACancelWasRecorded()
    {
        await StartAsync();
        var lost = _log.SeedActive("dl-lost", OperationKinds.Download, "lost-model", OperationStages.Downloading, null);
        var cancelled = _log.SeedActive("dl-cancelled", OperationKinds.Download, "cancelled-model", OperationStages.Downloading, null);
        _log.SeedActive("cancel-op", OperationKinds.DownloadCancel, "cancelled-model", null, null);
        var handler = Service<StorageRecoveryHandler>();

        await handler.RecoverAsync(lost);
        await handler.RecoverAsync(cancelled);

        Assert.Equal(OperationState.Failed, _log.Find("dl-lost")!.State);
        Assert.Contains("restart", _log.Find("dl-lost")!.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(OperationState.Cancelled, _log.Find("dl-cancelled")!.State);
    }

    [Fact]
    public async Task Recovery_FolderDelete_FinishesOnlyForTheCurrentMaster()
    {
        await StartAsync();
        MakeFolder("half-deleted");
        MakeFolder("other");
        var current = _log.SeedActive("del-r1", OperationKinds.FolderDelete, "half-deleted", OperationStages.Deleting, null);
        var gone = _log.SeedActive("del-r2", OperationKinds.FolderDelete, "already-gone", OperationStages.Deleting, null);
        var handler = Service<StorageRecoveryHandler>();

        await handler.RecoverAsync(current);
        await handler.RecoverAsync(gone);
        _fencing.Current = false;
        var superseded = _log.SeedActive("del-r3", OperationKinds.FolderDelete, "other", OperationStages.Deleting, null);
        await handler.RecoverAsync(superseded);

        Assert.Equal(OperationState.Succeeded, _log.Find("del-r1")!.State);
        Assert.False(Directory.Exists(Path.Combine(_root, "half-deleted")));
        Assert.Equal(OperationState.Succeeded, _log.Find("del-r2")!.State);
        Assert.Equal(ErrorCodes.Superseded, _log.Find("del-r3")!.Error!.Code);
        Assert.True(Directory.Exists(Path.Combine(_root, "other")));
    }

    [Fact]
    public async Task Startup_RemovesFinishedHelperContainers_WithTheRealDownloader()
    {
        // Two helpers left over from before the restart: one exited (token still in Docker's config), one running.
        var deleted = new ConcurrentBag<string>();
        var docker = new FakeHttpMessageHandler
        {
            Handler = request =>
            {
                string path = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Delete)
                {
                    deleted.Add(path);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }

                if (path.EndsWith("containers/json", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                            [
                              {"Id":"exited-1","Names":["/ss-dl-done"],"State":"exited","Status":"Exited (0) 5 minutes ago",
                               "Labels":{"mirbadllm.download":"true","mirbadllm.download.folder":"done","mirbadllm.download.repo":"org/done","mirbadllm.download.total-bytes":"10"}},
                              {"Id":"running-1","Names":["/ss-dl-busy"],"State":"running","Status":"Up 2 minutes",
                               "Labels":{"mirbadllm.download":"true","mirbadllm.download.folder":"busy","mirbadllm.download.repo":"org/busy","mirbadllm.download.total-bytes":"10"}}
                            ]
                            """),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
            },
        };
        Directory.CreateDirectory(Path.Combine(_root, "done"));
        File.WriteAllBytes(Path.Combine(_root, "done", "model.safetensors"), new byte[10]);
        var real = new HuggingFaceDownloader(
            new DockerEngineClient(new HttpClient(docker) { BaseAddress = new Uri("http://docker/v1.47/") }),
            new HttpClient(new FakeHttpMessageHandler()) { BaseAddress = new Uri("https://huggingface.co/") },
            Options.Create(new MirbadLLMOptions()),
            Options.Create(new ModelStorageOptions { ModelsLocalPath = _root }),
            NullLogger<HuggingFaceDownloader>.Instance);
        var service = new AgentDownloadService(real, _inspector, new ModelRootPaths(_root), _disk,
            new StorageOperations(_log, _fencing, _events), Options.Create(new AgentStorageOptions { ModelsRoot = _root }),
            TimeProvider.System, NullLogger<AgentDownloadService>.Instance);
        var cleanup = new DownloadHelperCleanup(service, NullLogger<DownloadHelperCleanup>.Instance);

        await cleanup.StartAsync(CancellationToken.None);

        Assert.Equal(["/v1.47/containers/exited-1"], deleted.ToArray());
        await service.DisposeAsync();
    }

    // ---------------------------------------------------------------- images

    [Fact]
    public async Task ListImages_ReturnsTheEngineImages()
    {
        var client = await StartAsync();
        _images.Images.Add(new ImageInfo { Ref = "local/vllm:1", Digest = Digest, Arch = "arm64", SizeBytes = 42 });

        var images = await ReadAsync<List<ImageInfo>>(await client.GetAsync(Protocol.BasePath + "/images"));

        Assert.Equal("local/vllm:1", Assert.Single(images).Ref);
    }

    [Fact]
    public async Task PullImage_ApprovedDigest_RunsAsAnOperationWithProgress()
    {
        var client = await StartAsync();
        _images.PullSteps =
        [
            new ImagePullProgress(0, 1000, 0, 2, "Pulling fs layer"),
            new ImagePullProgress(500, 1000, 1, 2, "Downloading"),
            new ImagePullProgress(1000, 1000, 2, 2, "Pull complete"),
        ];

        var response = await PostAsync(client, "/images/pull", Envelope("pull-1", new { image = "local/vllm", digest = Digest }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await ReadAsync<OperationStatus>(response);
        Assert.Equal(OperationKinds.ImagePull, accepted.Kind);
        Assert.Equal(OperationStages.Pulling, accepted.Stage);
        await WaitUntilAsync(() => _log.Find("pull-1")!.State == OperationState.Succeeded, "pull completion");

        Assert.Equal(("local/vllm", Digest), Assert.Single(_images.Pulls));
        var info = _log.Find("pull-1")!.ReadResult<ImageInfo>()!;
        Assert.Equal(Digest, info.Digest);
        Assert.Equal("local/vllm@" + Digest, info.Ref);
        var progressEvents = _events.Events.OfType<OperationEvent>()
            .Where(e => e.Operation.Id == "pull-1" && e.Operation.Progress?.BytesTotal == 1000).ToList();
        Assert.Contains(progressEvents, e => e.Operation.Progress!.Percent == 50);
        Assert.Contains(progressEvents, e => e.Operation.Progress!.Percent == 100);
    }

    [Fact]
    public async Task PullImage_RefusesTagsUnapprovedDigestsAndMismatches()
    {
        var client = await StartAsync();
        string other = "sha256:" + new string('f', 64);

        var tagOnly = await PostAsync(client, "/images/pull", Envelope("p1", new { image = "local/vllm:latest" }));
        var unapproved = await PostAsync(client, "/images/pull", Envelope("p2", new { image = "local/vllm", digest = other }));
        var mismatch = await PostAsync(client, "/images/pull", Envelope("p3", new { image = "local/vllm@" + other, digest = Digest }));
        var malformed = await PostAsync(client, "/images/pull", Envelope("p4", new { image = "local/vllm", digest = "sha256:XYZ" }));

        foreach (var response in new[] { tagOnly, unapproved, mismatch, malformed })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var error = await ReadErrorAsync(response);
            Assert.Equal(ErrorCodes.SpecRejected, error.Code);
            Assert.NotNull(error.ReadDetails<SpecRejectedDetails>());
        }

        Assert.Empty(_images.Pulls);
    }

    [Fact]
    public async Task PullImage_ByTag_WhereTheNodeAllowsIt()
    {
        var client = await StartAsync(o => o.AllowTagPulls = true);

        var response = await PostAsync(client, "/images/pull", Envelope("p-tag", new { image = "local/qwen38-vllm-dflash2:66e5414c6d75" }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await WaitUntilAsync(() => _log.Find("p-tag")!.State == OperationState.Succeeded, "tag pull");
        Assert.Equal(("local/qwen38-vllm-dflash2", "66e5414c6d75"), Assert.Single(_images.Pulls));
        Assert.Equal("local/qwen38-vllm-dflash2:66e5414c6d75", _log.Find("p-tag")!.ReadResult<ImageInfo>()!.Ref);
    }

    [Fact]
    public async Task PullImage_DockerError_FailsTheOperation()
    {
        var client = await StartAsync();
        _images.PullError = new ImagePullFailedException("manifest unknown");

        await PostAsync(client, "/images/pull", Envelope("pull-e", new { image = "local/vllm", digest = Digest }));
        await WaitUntilAsync(() => _log.Find("pull-e")!.State == OperationState.Failed, "pull failure");

        Assert.Contains("manifest unknown", _log.Find("pull-e")!.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullImage_CanBeCancelled()
    {
        var client = await StartAsync();
        _images.BlockPull = true;

        await PostAsync(client, "/images/pull", Envelope("pull-c", new { image = "local/vllm", digest = Digest }));
        await WaitUntilAsync(() => _images.PullStarted, "pull start");

        Assert.True(await Service<StorageOperationCanceller>().TryCancelAsync("pull-c"));
        Assert.Equal(OperationState.Cancelled, _log.Find("pull-c")!.State);
    }

    [Fact]
    public async Task PullImage_IsCancellableThroughTheSharedCancelRoute()
    {
        var client = await StartAsync();
        _images.BlockPull = true;

        await PostAsync(client, "/images/pull", Envelope("pull-x", new { image = "local/vllm", digest = Digest }));
        await WaitUntilAsync(() => _images.PullStarted, "pull start");

        var canceller = Assert.Single(_app!.Services.GetServices<MirbadLLM.Agent.Runtimes.IOperationCanceller>(),
            c => c.Kinds.Contains(OperationKinds.ImagePull));
        Assert.Contains(OperationKinds.Download, canceller.Kinds);
        var target = _log.Find("pull-x")!;
        Assert.Null(canceller.CheckCancellable(target));

        var after = await canceller.CancelAsync(target);

        Assert.Equal(OperationState.Cancelled, after.State);
        Assert.Equal(OperationState.Cancelled, _log.Find("pull-x")!.State);
    }

    [Fact]
    public async Task PullImage_RecoveryRunsThePullAgain()
    {
        await StartAsync();
        var op = _log.SeedActive("pull-r", OperationKinds.ImagePull, "local/vllm", OperationStages.Pulling,
            JsonSerializer.SerializeToElement(new { operationId = "pull-r", issuedAt = DateTimeOffset.UtcNow, expiresAt = DateTimeOffset.UtcNow.AddHours(1), image = "local/vllm", digest = Digest }, AgentJson.Options));

        await Service<StorageRecoveryHandler>().RecoverAsync(op);
        await WaitUntilAsync(() => _log.Find("pull-r")!.State == OperationState.Succeeded, "resumed pull");

        Assert.Single(_images.Pulls);
    }

    [Fact]
    public async Task DockerImageClient_ParsesListAndStreamsPullProgress()
    {
        var handler = new FakeHttpMessageHandler
        {
            Handler = request =>
            {
                string path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("images/json", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($$"""
                            [{"Id":"sha256:aaa","RepoTags":["local/vllm:1","local/vllm:latest"],"RepoDigests":["local/vllm@{{Digest}}"],"Size":42},
                             {"Id":"sha256:bbb","RepoTags":null,"RepoDigests":["other/img@{{Digest}}"],"Size":7},
                             {"Id":"sha256:ccc","RepoTags":["<none>:<none>"],"RepoDigests":[],"Size":1}]
                            """),
                    };
                }

                if (path.Contains("/images/sha256:", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"Architecture":"arm64"}""") };
                }

                if (path.EndsWith("images/create", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                            {"status":"Pulling from local/vllm","id":"1"}
                            {"status":"Pulling fs layer","progressDetail":{},"id":"l1"}
                            {"status":"Downloading","progressDetail":{"current":50,"total":100},"id":"l1"}
                            {"status":"Pull complete","progressDetail":{},"id":"l1"}
                            {"status":"Already exists","progressDetail":{},"id":"l2"}

                            """),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            },
        };
        var client = new DockerImageClient(new HttpClient(handler) { BaseAddress = new Uri("http://docker/v1.47/") });

        var images = await client.ListAsync();
        var progress = new List<ImagePullProgress>();
        await client.PullAsync("local/vllm", Digest, progress.Add);

        Assert.Equal(["local/vllm:1", "local/vllm:latest", "other/img@" + Digest], images.Select(i => i.Ref).ToArray());
        Assert.All(images, i => Assert.Equal(Digest, i.Digest));
        Assert.All(images, i => Assert.Equal("arm64", i.Arch));
        Assert.Contains(progress, p => p.BytesDone == 50 && p.BytesTotal == 100);
        Assert.Equal(2, progress[^1].LayersDone);
        Assert.Contains($"tag={Uri.EscapeDataString(Digest)}", handler.Requests.Last().RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DockerImageClient_PullErrorLine_Throws()
    {
        var handler = new FakeHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":"Pulling"}""" + "\n" + """{"error":"manifest unknown","errorDetail":{"message":"manifest unknown"}}""" + "\n"),
            },
        };
        var client = new DockerImageClient(new HttpClient(handler) { BaseAddress = new Uri("http://docker/v1.47/") });

        var ex = await Assert.ThrowsAsync<ImagePullFailedException>(() => client.PullAsync("local/vllm", "latest", _ => { }));
        Assert.Equal("manifest unknown", ex.Message);
    }

    [Theory]
    [InlineData("local/vllm:1", "local/vllm", "1", null)]
    [InlineData("registry:5000/team/img", "registry:5000/team/img", null, null)]
    [InlineData("registry:5000/team/img:2@" + Digest, "registry:5000/team/img", "2", Digest)]
    [InlineData("img@" + Digest, "img", null, Digest)]
    public void ImageReference_Parses(string reference, string repository, string? tag, string? digest) =>
        Assert.Equal(new ImageReference(repository, tag, digest), ImageReference.Parse(reference));

    // ---------------------------------------------------------------- capabilities and health

    [Fact]
    public async Task Capabilities_AddImagesRootsAndTheDownloadFeature_AndHealthFlagsLowSpace()
    {
        await StartAsync();
        MakeFolder("a");
        MakeFolder("b");
        _images.Images.Add(new ImageInfo { Ref = "local/vllm:1" });
        _disk.Space = new DiskSpace(FreeBytes: 10, TotalBytes: 1000);
        var contributor = Service<IEnumerable<ICapabilityContributor>>().Single();
        var baseReport = new NodeCapabilities
        {
            Protocol = Protocol.Range,
            AgentVersion = "test",
            Os = new NodeOsInfo("linux", "test"),
            Arch = "arm64",
            Memory = new NodeMemoryInfo(MemoryModel.Unified, 128),
            Features = [AgentFeatures.TelemetryV1],
        };

        var report = await contributor.ContributeAsync(baseReport);
        var health = await Service<IEnumerable<IHealthContributor>>().Single().CheckAsync();

        Assert.Contains(AgentFeatures.TelemetryV1, report.Features);
        Assert.Contains(AgentFeatures.DownloadsHfContainer, report.Features);
        Assert.Equal("local/vllm:1", Assert.Single(report.Images).Ref);
        var models = Assert.Single(report.ModelRoots);
        Assert.Equal(LogicalVolumes.Models, models.Name);
        Assert.Equal(2, models.FolderCount);
        Assert.Equal(10, models.FreeBytes);
        Assert.Equal(HealthReasonCodes.ModelRootLow, Assert.Single(health).Code);

        _images.ListError = new HttpRequestException("docker down");
        var withoutDocker = await contributor.ContributeAsync(baseReport);
        // A capability, not a health signal (W4 review MN-096): downloads answer docker_unavailable while Docker is down.
        Assert.Contains(AgentFeatures.DownloadsHfContainer, withoutDocker.Features);
        Assert.Empty(withoutDocker.Images);
    }

    // ================================================================ fakes

    private sealed class FakeOperationLog : IOperationLog
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly List<string> _recorded = [];

        public List<OperationSubmission> Submissions { get; } = [];

        public bool FailAccept { get; set; }

        public int TornLinesIgnored => 0;

        public static string DigestOf(JsonElement payload) =>
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(payload.GetRawText())));

        public string EverythingRecorded()
        {
            lock (_gate)
            {
                return string.Join('\n', _recorded);
            }
        }

        public List<string> StagesOf(string id)
        {
            lock (_gate)
            {
                return _entries[id].Stages.ToList();
            }
        }

        public List<OperationProgress> ProgressOf(string id)
        {
            lock (_gate)
            {
                return _entries[id].Progress.ToList();
            }
        }

        public RecoverableOperation SeedActive(string id, string kind, string target, string? stage, JsonElement? payload)
        {
            var now = DateTimeOffset.UtcNow;
            var status = new OperationStatus
            {
                Id = id,
                Kind = kind,
                Target = target,
                PayloadDigest = "seed",
                State = stage is null ? OperationState.Accepted : OperationState.Running,
                Stage = stage,
                IssuedAt = now,
                ExpiresAt = now.AddHours(24),
                StartedAt = now,
            };
            lock (_gate)
            {
                _entries[id] = new Entry(status, Master, payload);
            }

            return new RecoverableOperation { Status = status, Master = Master, Payload = payload };
        }

        public Task<OperationAcceptance> AcceptAsync(OperationSubmission submission, CancellationToken ct = default)
        {
            if (FailAccept)
            {
                throw new IOException("disk full");
            }

            lock (_gate)
            {
                Submissions.Add(submission);
                Record(submission);
                string digest = DigestOf(submission.Payload);
                if (_entries.TryGetValue(submission.OperationId, out var existing))
                {
                    var stored = existing.Status;
                    return Task.FromResult(stored.Kind == submission.Kind && stored.Target == submission.Target && stored.PayloadDigest == digest
                        ? new OperationAcceptance(OperationAcceptOutcome.Duplicate, stored)
                        : new OperationAcceptance(OperationAcceptOutcome.Conflict, null));
                }

                var now = DateTimeOffset.UtcNow;
                if (submission.ExpiresAt <= now)
                {
                    return Task.FromResult(new OperationAcceptance(OperationAcceptOutcome.Expired, null));
                }

                if (submission.IssuedAt > now + OperationRetention.MaxIssuedAtFutureSkew)
                {
                    return Task.FromResult(new OperationAcceptance(OperationAcceptOutcome.IssuedInFuture, null));
                }

                var status = new OperationStatus
                {
                    Id = submission.OperationId,
                    Kind = submission.Kind,
                    Target = submission.Target,
                    PayloadDigest = digest,
                    State = OperationState.Accepted,
                    IssuedAt = submission.IssuedAt,
                    ExpiresAt = submission.ExpiresAt,
                    StartedAt = now,
                };
                _entries[submission.OperationId] = new Entry(status, submission.Master, submission.Payload);
                Record(status);
                return Task.FromResult(new OperationAcceptance(OperationAcceptOutcome.Accepted, status));
            }
        }

        public Task<OperationStatus> RecordStageAsync(string operationId, OperationStageUpdate update, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var entry = _entries[operationId];
                var status = entry.Status with
                {
                    Stage = update.Stage ?? entry.Status.Stage,
                    State = update.State ?? (entry.Status.State == OperationState.Accepted ? OperationState.Running : entry.Status.State),
                    Progress = update.Progress ?? entry.Status.Progress,
                };
                entry.Status = status;
                if (update.Stage is not null)
                {
                    entry.Stages.Add(update.Stage);
                }

                if (update.Progress is not null)
                {
                    entry.Progress.Add(update.Progress);
                }

                Record(status);
                return Task.FromResult(status);
            }
        }

        /// <summary>When set, <see cref="CompleteAsync"/> signals <see cref="CompletionReached"/> and waits for it before writing.</summary>
        public TaskCompletionSource? HoldCompletion { get; set; }

        public TaskCompletionSource CompletionReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>True once the owner disposed the log: writes then throw, as <c>OperationLog</c> does.</summary>
        public bool Disposed { get; set; }

        public async Task<OperationStatus> CompleteAsync(string operationId, OperationState state, AgentError? failure = null, JsonElement? result = null, CancellationToken ct = default)
        {
            if (HoldCompletion is { } hold)
            {
                CompletionReached.TrySetResult();
                await hold.Task;
            }

            ObjectDisposedException.ThrowIf(Disposed, this);
            return CompleteCore(operationId, state, failure, result);
        }

        private OperationStatus CompleteCore(string operationId, OperationState state, AgentError? failure, JsonElement? result)
        {
            lock (_gate)
            {
                var entry = _entries[operationId];
                if (entry.Status.State is OperationState.Succeeded or OperationState.Failed or OperationState.Cancelled or OperationState.Unknown)
                {
                    return entry.Status;
                }

                entry.Status = entry.Status with { State = state, Error = failure, Result = result, EndedAt = DateTimeOffset.UtcNow };
                Record(entry.Status);
                return entry.Status;
            }
        }

        public OperationStatus? Find(string operationId)
        {
            lock (_gate)
            {
                return _entries.TryGetValue(operationId, out var entry) ? entry.Status : null;
            }
        }

        public IReadOnlyList<OperationStatus> ListActive()
        {
            lock (_gate)
            {
                return _entries.Values.Select(e => e.Status).Where(s => s.EndedAt is null).ToList();
            }
        }

        public IReadOnlyList<OperationStatus> ListRecent(int max)
        {
            lock (_gate)
            {
                return _entries.Values.Select(e => e.Status).Where(s => s.EndedAt is not null).Take(max).ToList();
            }
        }

        public IReadOnlyList<RecoverableOperation> ListRecoverable()
        {
            lock (_gate)
            {
                return _entries.Values.Where(e => e.Status.EndedAt is null)
                    .Select(e => new RecoverableOperation { Status = e.Status, Master = e.Master, Payload = e.Payload }).ToList();
            }
        }

        public Task CompactAsync(CancellationToken ct = default) => Task.CompletedTask;

        private void Record(object value) => _recorded.Add(JsonSerializer.Serialize(value, AgentJson.Options));

        private sealed class Entry(OperationStatus status, MasterTriple master, JsonElement? payload)
        {
            public OperationStatus Status { get; set; } = status;

            public MasterTriple Master { get; } = master;

            public JsonElement? Payload { get; } = payload;

            public List<string> Stages { get; } = [];

            public List<OperationProgress> Progress { get; } = [];
        }
    }

    private sealed class FakeFencing : IFencingState
    {
        public FencingDecision Decision { get; set; } = FencingDecision.Accepted;

        public bool Current { get; set; } = true;

        public MasterTriple? Accepted => Master;

        public Task<FencingCheck> CheckAsync(MasterTriple presented, CancellationToken ct = default) =>
            Task.FromResult(new FencingCheck(Decision, Master));

        public bool IsCurrent(MasterTriple operationMaster) => Current;
    }

    private sealed class FakeEventBus : IEventBus
    {
        private readonly object _gate = new();
        private readonly List<AgentEvent> _events = [];
        private long _seq;

        public string BootId => "agent-boot";

        public long CurrentSeq => Interlocked.Read(ref _seq);

        public IReadOnlyList<AgentEvent> Events
        {
            get
            {
                lock (_gate)
                {
                    return _events.ToList();
                }
            }
        }

        public AgentEvent Publish(AgentEvent evt) => Publish(() => { }, evt);

        public AgentEvent Publish(Action stateChange, AgentEvent evt)
        {
            lock (_gate)
            {
                stateChange();
                var stamped = evt with { BootId = BootId, Seq = ++_seq, At = DateTimeOffset.UtcNow };
                _events.Add(stamped);
                return stamped;
            }
        }

        public T Snapshot<T>(Func<long, T> build)
        {
            lock (_gate)
            {
                return build(_seq);
            }
        }

        public IEventSubscription Subscribe(string? bootId, long? afterSeq) => throw new NotSupportedException();
    }

    private sealed class FakeDriver : IRuntimeDriver
    {
        public HashSet<string> FoldersInUse { get; } = new(StringComparer.Ordinal);

        public LaunchDelivery Delivery => LaunchDelivery.Container;

        public Task<bool> IsFolderInUseAsync(string folder, CancellationToken ct = default) => Task.FromResult(FoldersInUse.Contains(folder));

        public Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RuntimeStatus?> GetAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RuntimeLaunchResult> LaunchAsync(RuntimeLaunch launch, IRuntimeStageSink stages, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RuntimeStatus> StartCreatedAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task StopAsync(string modelId, IRuntimeStageSink stages, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LaunchConfig?> GetLaunchConfigAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

        public IAsyncEnumerable<string> StreamLogsAsync(string modelId, int tail, bool follow, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<Uri?> GetUpstreamAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>A container helper whose per-folder state the test drives; finished helpers are "removed" when read, as today.</summary>
    private sealed class FakeDownloader : IHuggingFaceDownloader
    {
        private readonly ConcurrentDictionary<string, DownloadStatus> _helpers = new(StringComparer.Ordinal);

        public ConcurrentQueue<DownloadRequest> StartCallsQueue { get; } = new();

        public IReadOnlyList<DownloadRequest> StartCalls => StartCallsQueue.ToList();

        public List<string> CancelCalls { get; } = [];

        public Exception? ThrowOnStart { get; set; }

        public void Seed(DownloadStatus status) => _helpers[status.Folder] = status;

        public void Set(string folder, DownloadState state, long bytes, string? error = null) =>
            _helpers.AddOrUpdate(folder,
                _ => new DownloadStatus(folder, "ornith-ai/Ornith-1.5-9B", null, state, bytes, 1000, null, error),
                (_, current) => current with { State = state, BytesDownloaded = bytes, Error = error });

        public Task<DownloadStatus> StartAsync(DownloadRequest request, CancellationToken ct = default)
        {
            if (ThrowOnStart is { } ex)
            {
                throw ex;
            }

            StartCallsQueue.Enqueue(request);
            var status = new DownloadStatus(request.Folder!, request.RepoId, request.Revision, DownloadState.Queued, 0, 1000, null, null);
            _helpers[request.Folder!] = status;
            return Task.FromResult(status);
        }

        public Task<IReadOnlyList<DownloadStatus>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DownloadStatus>>(_helpers.Values.ToList());

        public Task<DownloadStatus?> GetAsync(string folder, CancellationToken ct = default) =>
            Task.FromResult(_helpers.TryGetValue(folder, out var status) ? status : null);

        public Task<bool> CancelAsync(string folder, CancellationToken ct = default)
        {
            lock (CancelCalls)
            {
                CancelCalls.Add(folder);
            }

            return Task.FromResult(_helpers.TryRemove(folder, out _));
        }
    }

    private sealed class FakeRepoInspector : IHuggingFaceRepoInspector
    {
        public List<RepoFile> Files { get; set; } = [new("config.json", 100), new("model.safetensors", 900), new("README.md", 50)];

        public ConcurrentQueue<string?> TokensQueue { get; } = new();

        public IReadOnlyList<string?> Tokens => TokensQueue.ToList();

        public Task<RepoInspection> InspectAsync(string repoId, string? revision, string? token, CancellationToken ct = default)
        {
            TokensQueue.Enqueue(token);
            return Task.FromResult(new RepoInspection(repoId, revision, RepoFormat.Safetensors, Files, [], [], Files));
        }
    }

    private sealed class FakeDisk : IDiskSpaceProbe
    {
        public DiskSpace Space { get; set; } = new(FreeBytes: 1L << 40, TotalBytes: 1L << 41);

        public DiskSpace? Probe(string path) => Space;
    }

    private sealed class FakeImageEngine : IContainerImageEngine
    {
        public List<ImageInfo> Images { get; } = [];

        public ConcurrentQueue<(string, string)> PullQueue { get; } = new();

        public IReadOnlyList<(string Repository, string TagOrDigest)> Pulls => PullQueue.ToList();

        public IReadOnlyList<ImagePullProgress> PullSteps { get; set; } = [];

        public Exception? PullError { get; set; }

        public Exception? ListError { get; set; }

        public bool BlockPull { get; set; }

        private volatile bool _pullStarted;

        public bool PullStarted => _pullStarted;

        public Task<IReadOnlyList<ImageInfo>> ListAsync(CancellationToken ct = default) =>
            ListError is { } ex ? Task.FromException<IReadOnlyList<ImageInfo>>(ex) : Task.FromResult<IReadOnlyList<ImageInfo>>(Images.ToList());

        public Task<ImageInfo?> InspectAsync(string reference, CancellationToken ct = default)
        {
            var parsed = ImageReference.Parse(reference);
            return Task.FromResult<ImageInfo?>(new ImageInfo { Ref = reference, Id = "sha256:img", Digest = parsed.Digest ?? Digest, Arch = "arm64", SizeBytes = 1000 });
        }

        public async Task PullAsync(string repository, string tagOrDigest, Action<ImagePullProgress> progress, CancellationToken ct = default)
        {
            PullQueue.Enqueue((repository, tagOrDigest));
            _pullStarted = true;
            if (BlockPull)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            foreach (var step in PullSteps)
            {
                progress(step);
            }

            if (PullError is { } error)
            {
                throw error;
            }
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => _lines.ToList();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner._lines.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
        }
    }
}
