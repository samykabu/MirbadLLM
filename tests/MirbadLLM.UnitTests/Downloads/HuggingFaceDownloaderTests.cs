using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

namespace MirbadLLM.UnitTests.Downloads;

/// <summary>
/// <see cref="HuggingFaceDownloader"/> against fake Docker Engine and Hugging Face HTTP endpoints: the
/// container it creates (env token, binds, labels, no token on the command line), progress-by-bytes-on-disk,
/// HF API 401/404 handling, cancel, and re-adoption of a container after a simulated controller restart.
/// </summary>
public sealed class HuggingFaceDownloaderTests : IDisposable
{
    [Fact]
    public void ADownloadRequestNeverPrintsItsToken() // W9 review MN-132 (design §10.4)
    {
        string text = new MirbadLLM.Core.DownloadRequest("org/gated", Token: "hf_secretvalue", Folder: "gated").ToString();
        Assert.DoesNotContain("hf_secretvalue", text, StringComparison.Ordinal);
        Assert.Contains("Token = ***", text, StringComparison.Ordinal);
        Assert.Contains("org/gated", text, StringComparison.Ordinal);
    }

    private readonly string _modelsRoot;

    public HuggingFaceDownloaderTests()
    {
        _modelsRoot = Path.Combine(Path.GetTempPath(), "mirbadllm-downloader-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_modelsRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_modelsRoot))
        {
            Directory.Delete(_modelsRoot, recursive: true);
        }
    }

    private HuggingFaceDownloader CreateDownloader(FakeHttpMessageHandler dockerHandler, FakeHttpMessageHandler hfHandler, string? helperUser = null) =>
        new(
            new DockerEngineClient(new HttpClient(dockerHandler) { BaseAddress = new Uri("http://docker/v1.47/") }),
            new HttpClient(hfHandler) { BaseAddress = new Uri("https://huggingface.co/") },
            Options.Create(new MirbadLLMOptions()),
            Options.Create(new ModelStorageOptions { ModelsLocalPath = _modelsRoot }),
            NullLogger<HuggingFaceDownloader>.Instance,
            helperUser);

    private static FakeHttpMessageHandler CreatingDockerHandler() => DockerHandlerFor(request => request.RequestUri!.AbsolutePath.EndsWith("containers/json", StringComparison.Ordinal)
        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }
        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"Id":"job-1"}""") });

    private static JsonElement CreateBody(FakeHttpMessageHandler dockerHandler)
    {
        var createRequest = dockerHandler.Requests.Single(r => r.RequestUri!.AbsolutePath.EndsWith("containers/create", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(dockerHandler.RequestBodies[dockerHandler.Requests.IndexOf(createRequest)]);
        return body.RootElement.Clone();
    }

    [Fact]
    public async Task StartAsync_HelperUser_RunsTheHelperAsThatUserWithAWritableHome()
    {
        // MN-126: the agent's own uid:gid, so the folder the helper writes stays deletable by the non-root agent.
        var dockerHandler = CreatingDockerHandler();
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning(RepoInfoJson), helperUser: "1654:1654");

        await downloader.StartAsync(new DownloadRequest("ornith-ai/Ornith-1.5-9B", Token: "t"));

        var root = CreateBody(dockerHandler);
        Assert.Equal("1654:1654", root.GetProperty("User").GetString());
        var env = root.GetProperty("Env").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("HOME=/tmp", env);
        Assert.Contains("HF_HOME=/tmp/huggingface", env);
        Assert.Contains("HF_TOKEN=t", env);
    }

    [Fact]
    public async Task StartAsync_NoHelperUser_KeepsTheImageDefaultUserAndEnvironment()
    {
        // Without a helper user the create body is the pre-MN-126 one: no User field, no HOME override.
        var dockerHandler = CreatingDockerHandler();
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning(RepoInfoJson));

        await downloader.StartAsync(new DownloadRequest("ornith-ai/Ornith-1.5-9B"));

        var root = CreateBody(dockerHandler);
        Assert.False(root.TryGetProperty("User", out _));
        Assert.Equal(["HF_HUB_ENABLE_HF_TRANSFER=0"], root.GetProperty("Env").EnumerateArray().Select(e => e.GetString()).ToList());
    }

    private static FakeHttpMessageHandler HfHandlerReturning(string repoJson, HttpStatusCode status = HttpStatusCode.OK) => new()
    {
        Handler = _ => new HttpResponseMessage(status) { Content = new StringContent(repoJson) },
    };

    private static FakeHttpMessageHandler DockerHandlerFor(Func<HttpRequestMessage, HttpResponseMessage> handler) => new() { Handler = handler };

    private const string RepoInfoJson = """
        {
          "siblings": [
            { "rfilename": "config.json", "size": 100 },
            { "rfilename": "model.safetensors", "size": 900 },
            { "rfilename": "README.md", "size": 50 }
          ]
        }
        """;

    [Fact]
    public async Task StartAsync_CreatesContainer_WithEnvTokenBindsLabelsAndNoTokenInCmd()
    {
        var dockerHandler = DockerHandlerFor(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("containers/json", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
            }

            if (path.EndsWith("containers/create", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"Id":"job-1"}""") };
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var hfHandler = HfHandlerReturning(RepoInfoJson);
        var downloader = CreateDownloader(dockerHandler, hfHandler);

        var status = await downloader.StartAsync(new DownloadRequest("ornith-ai/Ornith-1.5-9B", Token: "secret-token-123"));

        Assert.Equal("Ornith-1.5-9B", status.Folder);
        Assert.Equal(1000, status.TotalBytes);
        Assert.Equal(DownloadState.Queued, status.State);

        var createRequest = dockerHandler.Requests.Single(r => r.RequestUri!.AbsolutePath.EndsWith("containers/create", StringComparison.Ordinal));
        Assert.Contains("name=ss-dl-Ornith-1.5-9B", createRequest.RequestUri!.Query);

        int bodyIndex = dockerHandler.Requests.IndexOf(createRequest);
        using var body = JsonDocument.Parse(dockerHandler.RequestBodies[bodyIndex]);
        var root = body.RootElement;

        var env = root.GetProperty("Env").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("HF_TOKEN=secret-token-123", env);
        Assert.Contains("HF_HUB_ENABLE_HF_TRANSFER=0", env);

        var cmd = root.GetProperty("Cmd").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain(cmd, arg => arg is not null && arg.Contains("secret-token-123", StringComparison.Ordinal));

        var entrypoint = root.GetProperty("Entrypoint").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["python3"], entrypoint);

        var binds = root.GetProperty("HostConfig").GetProperty("Binds").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(binds, b => b == "/srv/mirbadllm/models:/models");

        var labels = root.GetProperty("Labels");
        Assert.Equal("true", labels.GetProperty("mirbadllm.download").GetString());
        Assert.Equal("Ornith-1.5-9B", labels.GetProperty("mirbadllm.download.folder").GetString());
        Assert.Equal("ornith-ai/Ornith-1.5-9B", labels.GetProperty("mirbadllm.download.repo").GetString());
        Assert.Equal("1000", labels.GetProperty("mirbadllm.download.total-bytes").GetString());

        // Labels must never carry the token either (only Env, read only inside the container).
        Assert.DoesNotContain("secret-token-123", labels.GetRawText(), StringComparison.Ordinal);

        // The Hugging Face API call itself must have carried the token as a bearer header.
        var hfRequest = Assert.Single(hfHandler.Requests);
        Assert.Equal("Bearer", hfRequest.Headers.Authorization?.Scheme);
        Assert.Equal("secret-token-123", hfRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task StartAsync_DefaultFolder_IsRepoNameAfterLastSlash()
    {
        var dockerHandler = DockerHandlerFor(request => request.RequestUri!.AbsolutePath.EndsWith("containers/json", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"Id":"job-1"}""") });
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning(RepoInfoJson));

        var status = await downloader.StartAsync(new DownloadRequest("ornith-ai/Ornith-1.5-9B"));

        Assert.Equal("Ornith-1.5-9B", status.Folder);
    }

    [Fact]
    public async Task StartAsync_ExplicitFolder_Overrides()
    {
        var dockerHandler = DockerHandlerFor(request => request.RequestUri!.AbsolutePath.EndsWith("containers/json", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"Id":"job-1"}""") });
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning(RepoInfoJson));

        var status = await downloader.StartAsync(new DownloadRequest("ornith-ai/Ornith-1.5-9B", Folder: "my-folder"));

        Assert.Equal("my-folder", status.Folder);
    }

    [Fact]
    public async Task StartAsync_Unauthorized_ThrowsHuggingFaceApiExceptionWithGatedMessage()
    {
        var dockerHandler = DockerHandlerFor(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning("{}", HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<HuggingFaceApiException>(
            () => downloader.StartAsync(new DownloadRequest("meta-llama/Llama-3.1-8B-Instruct")));

        Assert.Contains("gated", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(dockerHandler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("containers/create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_NotFound_ThrowsHuggingFaceApiExceptionWithNotFoundMessage()
    {
        var dockerHandler = DockerHandlerFor(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning("{}", HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<HuggingFaceApiException>(
            () => downloader.StartAsync(new DownloadRequest("no-such-org/no-such-repo")));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartAsync_NoFileMatchesTheAllowPatterns_ThrowsWithoutCreatingAContainer()
    {
        // orcarouter/Qwen3.8-Flash-Next-Uncensored-GGUF, in miniature: a GGUF-only repo against the
        // safetensors-shaped default allow-patterns. Nothing matches, so this must fail fast with a clear
        // message and never reach containers/create (the bug: it used to "complete" at 0/0 bytes).
        const string ggufOnlyRepoJson = """
            {
              "siblings": [
                { "rfilename": "model-Q4_K_M.gguf", "size": 900 },
                { "rfilename": "README.md", "size": 50 },
                { "rfilename": ".gitattributes", "size": 20 }
              ]
            }
            """;
        var dockerHandler = DockerHandlerFor(request => request.RequestUri!.AbsolutePath.EndsWith("containers/json", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"Id":"job-1"}""") });
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning(ggufOnlyRepoJson));

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => downloader.StartAsync(new DownloadRequest("orcarouter/Qwen3.8-Flash-Next-Uncensored-GGUF")));

        Assert.Contains("No files match", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1 .gguf file", ex.Message, StringComparison.Ordinal);
        Assert.Contains("GGUF", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(dockerHandler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("containers/create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_AlreadyRunning_Throws()
    {
        string listJson = ContainerListJson("Ornith-1.5-9B", "running");
        var dockerHandler = DockerHandlerFor(request => request.RequestUri!.AbsolutePath.EndsWith("containers/json", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listJson) }
            : new HttpResponseMessage(HttpStatusCode.OK));
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning(RepoInfoJson));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => downloader.StartAsync(new DownloadRequest("ornith-ai/Ornith-1.5-9B")));
    }

    [Fact]
    public async Task GetAsync_RunningContainer_IsDownloading()
    {
        string listJson = ContainerListJson("Ornith-1.5-9B", "running", totalBytes: 1000);
        var dockerHandler = DockerHandlerFor(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listJson) });
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning(RepoInfoJson));

        var status = await downloader.GetAsync("Ornith-1.5-9B");

        Assert.NotNull(status);
        Assert.Equal(DownloadState.Downloading, status!.State);
        Assert.Equal(1000, status.TotalBytes);
    }

    [Fact]
    public async Task GetAsync_CreatedContainer_IsQueued()
    {
        string listJson = ContainerListJson("Ornith-1.5-9B", "created");
        var dockerHandler = DockerHandlerFor(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listJson) });
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning(RepoInfoJson));

        var status = await downloader.GetAsync("Ornith-1.5-9B");

        Assert.Equal(DownloadState.Queued, status!.State);
    }

    [Fact]
    public async Task GetAsync_ExitedZero_WithBytesOnDisk_IsCompleted()
    {
        string folder = "Ornith-1.5-9B";
        string dir = Path.Combine(_modelsRoot, folder);
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "model.safetensors"), new byte[1000]);

        string listJson = ContainerListJson(folder, "exited", statusText: "Exited (0) 3 minutes ago", totalBytes: 1000);
        var dockerHandler = DockerHandlerFor(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listJson) });
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning(RepoInfoJson));

        var status = await downloader.GetAsync(folder);

        Assert.Equal(DownloadState.Completed, status!.State);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task GetAsync_ExitedZero_ButZeroBytesOnDisk_IsFailed_NeverCompleted()
    {
        // The container ran and exited 0 (e.g. an empty allow-pattern match slipped through, or a controller
        // restart lost the expected total), but nothing ended up on disk: this must never report Completed.
        string listJson = ContainerListJson("Ornith-1.5-9B", "exited", statusText: "Exited (0) 3 minutes ago", totalBytes: 1000);
        var dockerHandler = DockerHandlerFor(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listJson) });
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning(RepoInfoJson));

        var status = await downloader.GetAsync("Ornith-1.5-9B");

        Assert.Equal(DownloadState.Failed, status!.State);
        Assert.Contains("0 bytes", status.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_ExitedZero_ZeroExpectedTotal_IsFailed_NeverCompleted()
    {
        // totalBytes 0 (e.g. an old container adopted from before the zero-match pre-flight check existed):
        // never report Completed for a 0-byte expectation either.
        string listJson = ContainerListJson("Ornith-1.5-9B", "exited", statusText: "Exited (0) 3 minutes ago", totalBytes: 0);
        var dockerHandler = DockerHandlerFor(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listJson) });
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning(RepoInfoJson));

        var status = await downloader.GetAsync("Ornith-1.5-9B");

        Assert.Equal(DownloadState.Failed, status!.State);
        Assert.Contains("matched", status.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAsync_ExitedNonZero_IsFailedWithLogMessage()
    {
        string listJson = ContainerListJson("Ornith-1.5-9B", "exited", statusText: "Exited (1) 2 seconds ago");
        var dockerHandler = DockerHandlerFor(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/logs", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(LogFrame("HTTPError: 404 Client Error\n")) };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listJson) };
        });
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning(RepoInfoJson));

        var status = await downloader.GetAsync("Ornith-1.5-9B");

        Assert.Equal(DownloadState.Failed, status!.State);
        Assert.Contains("HTTPError", status.Error);
    }

    [Fact]
    public async Task GetAsync_UnknownFolder_ReturnsNull()
    {
        var dockerHandler = DockerHandlerFor(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning(RepoInfoJson));

        Assert.Null(await downloader.GetAsync("does-not-exist"));
    }

    [Fact]
    public async Task GetAsync_ComputesBytesDownloaded_FromFilesOnDisk()
    {
        string folder = "Ornith-1.5-9B";
        string dir = Path.Combine(_modelsRoot, folder);
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "config.json"), new byte[100]);
        await File.WriteAllBytesAsync(Path.Combine(dir, "model-00001.safetensors"), new byte[400]);

        string listJson = ContainerListJson(folder, "running", totalBytes: 1000);
        var dockerHandler = DockerHandlerFor(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listJson) });
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning(RepoInfoJson));

        var status = await downloader.GetAsync(folder);

        Assert.Equal(500, status!.BytesDownloaded);
    }

    [Fact]
    public async Task ListAsync_TwoSamples_ComputesPositiveRate()
    {
        string folder = "Ornith-1.5-9B";
        string dir = Path.Combine(_modelsRoot, folder);
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "part1"), new byte[100]);

        string listJson = ContainerListJson(folder, "running", totalBytes: 1000);
        var dockerHandler = DockerHandlerFor(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listJson) });
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning(RepoInfoJson));

        var first = await downloader.GetAsync(folder);
        Assert.Null(first!.BytesPerSecond);

        await File.WriteAllBytesAsync(Path.Combine(dir, "part2"), new byte[900]);
        await Task.Delay(600, TestContext.Current.CancellationToken);

        var second = await downloader.GetAsync(folder);
        Assert.NotNull(second!.BytesPerSecond);
        Assert.True(second.BytesPerSecond > 0);
    }

    [Fact]
    public async Task CancelAsync_StopsAndRemovesContainer_ReturnsTrue()
    {
        string listJson = ContainerListJson("Ornith-1.5-9B", "running");
        var stopped = false;
        var removed = false;
        var dockerHandler = DockerHandlerFor(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("containers/json", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listJson) };
            }

            if (path.Contains("/stop", StringComparison.Ordinal))
            {
                stopped = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Delete)
            {
                removed = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning(RepoInfoJson));

        bool cancelled = await downloader.CancelAsync("Ornith-1.5-9B");

        Assert.True(cancelled);
        Assert.True(stopped);
        Assert.True(removed);
    }

    [Fact]
    public async Task CancelAsync_UnknownFolder_ReturnsFalse()
    {
        var dockerHandler = DockerHandlerFor(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning(RepoInfoJson));

        Assert.False(await downloader.CancelAsync("does-not-exist"));
    }

    [Fact]
    public async Task GetAsync_AfterSimulatedRestart_ReadsTotalBytesBackFromLabel_WithoutCallingHuggingFaceApi()
    {
        // A fresh HuggingFaceDownloader instance (as after a controller restart) must recover TotalBytes
        // purely from the container's label, never re-hitting the Hugging Face API.
        string listJson = ContainerListJson("Ornith-1.5-9B", "running", totalBytes: 1000);
        var dockerHandler = DockerHandlerFor(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listJson) });
        var hfHandler = HfHandlerReturning(RepoInfoJson);
        var freshInstance = CreateDownloader(dockerHandler, hfHandler);

        var status = await freshInstance.GetAsync("Ornith-1.5-9B");

        Assert.Equal(1000, status!.TotalBytes);
        Assert.Empty(hfHandler.Requests);
    }

    [Fact]
    public async Task GetAsync_IgnoresHuggingFaceCacheFolder_AndNeverExceedsTotal()
    {
        string folder = "Ornith-1.5-9B";
        string dir = Path.Combine(_modelsRoot, folder);
        string cache = Path.Combine(dir, ".cache", "huggingface", "download");
        Directory.CreateDirectory(cache);
        await File.WriteAllBytesAsync(Path.Combine(dir, "config.json"), new byte[100]);
        await File.WriteAllBytesAsync(Path.Combine(dir, "model-00001.safetensors"), new byte[400]);
        await File.WriteAllBytesAsync(Path.Combine(cache, "model-00002.safetensors.incomplete"), new byte[900]);
        await File.WriteAllBytesAsync(Path.Combine(cache, "model-00001.safetensors.metadata"), new byte[50]);

        string listJson = ContainerListJson(folder, "running", totalBytes: 1000);
        var dockerHandler = DockerHandlerFor(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listJson) });
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning(RepoInfoJson));

        var status = await downloader.GetAsync(folder);
        Assert.Equal(500, status!.BytesDownloaded);

        // More bytes on disk than the repo listing promised (e.g. a re-download over old files): clamp to 100%.
        await File.WriteAllBytesAsync(Path.Combine(dir, "model-00002.safetensors"), new byte[900]);
        var clamped = await downloader.GetAsync(folder);
        Assert.Equal(1000, clamped!.BytesDownloaded);
    }

    [Fact]
    public async Task GetAsync_Completed_RemovesTheContainer_AndKeepsReportingCompleted()
    {
        string folder = "Ornith-1.5-9B";
        Directory.CreateDirectory(Path.Combine(_modelsRoot, folder));
        await File.WriteAllBytesAsync(Path.Combine(_modelsRoot, folder, "model.safetensors"), new byte[1000]);

        string listJson = ContainerListJson(folder, "exited", statusText: "Exited (0) 3 seconds ago", totalBytes: 1000);
        bool removed = false;
        var dockerHandler = DockerHandlerFor(request =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                removed = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(removed ? "[]" : listJson) };
        });
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning(RepoInfoJson));

        var first = await downloader.GetAsync(folder);
        Assert.Equal(DownloadState.Completed, first!.State);
        Assert.True(removed);

        // The container is gone; the outcome is still reported to the next poll and in the list.
        var second = await downloader.GetAsync(folder);
        Assert.Equal(DownloadState.Completed, second!.State);
        Assert.Equal(1000, second.TotalBytes);
        var listed = Assert.Single(await downloader.ListAsync());
        Assert.Equal(DownloadState.Completed, listed.State);
    }

    [Fact]
    public async Task GetAsync_Failed_ReadsTheLogBeforeRemovingTheContainer()
    {
        string folder = "Ornith-1.5-9B";
        string listJson = ContainerListJson(folder, "exited", statusText: "Exited (1) 2 seconds ago");
        var calls = new List<string>();
        bool removed = false;
        var dockerHandler = DockerHandlerFor(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/logs", StringComparison.Ordinal))
            {
                calls.Add("logs");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(LogFrame("HTTPError: 401 Client Error\n")) };
            }

            if (request.Method == HttpMethod.Delete)
            {
                calls.Add("delete");
                removed = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(removed ? "[]" : listJson) };
        });
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning(RepoInfoJson));

        var status = await downloader.GetAsync(folder);

        Assert.Equal(DownloadState.Failed, status!.State);
        Assert.Equal(["logs", "delete"], calls);
        var remembered = await downloader.GetAsync(folder);
        Assert.Equal(DownloadState.Failed, remembered!.State);
        Assert.Contains("HTTPError", remembered.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_Completed_RemovalFailure_StillReportsCompleted()
    {
        string folder = "Ornith-1.5-9B";
        Directory.CreateDirectory(Path.Combine(_modelsRoot, folder));
        await File.WriteAllBytesAsync(Path.Combine(_modelsRoot, folder, "model.safetensors"), new byte[1000]);

        string listJson = ContainerListJson(folder, "exited", statusText: "Exited (0) 3 seconds ago", totalBytes: 1000);
        var dockerHandler = DockerHandlerFor(request => request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{\"message\":\"removal in progress\"}") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listJson) });
        var downloader = CreateDownloader(dockerHandler, HfHandlerReturning(RepoInfoJson));

        var status = await downloader.GetAsync(folder);

        Assert.Equal(DownloadState.Completed, status!.State);
    }

    private static string ContainerListJson(string folder, string state, string? statusText = null, long totalBytes = 0) =>
        $$"""
        [
          {
            "Id": "job-{{folder}}",
            "Names": ["/ss-dl-{{folder}}"],
            "State": "{{state}}",
            "Status": "{{statusText ?? "irrelevant"}}",
            "Labels": {
              "mirbadllm.download": "true",
              "mirbadllm.download.folder": "{{folder}}",
              "mirbadllm.download.repo": "ornith-ai/{{folder}}",
              "mirbadllm.download.revision": "",
              "mirbadllm.download.total-bytes": "{{totalBytes}}"
            }
          }
        ]
        """;

    private static byte[] LogFrame(string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        var header = new byte[8];
        header[0] = 1;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), payload.Length);
        return [.. header, .. payload];
    }
}
