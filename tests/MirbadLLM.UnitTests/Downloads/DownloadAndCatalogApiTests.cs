using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.UnitTests.Downloads.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Api;

#pragma warning disable CA1707 // underscore test names.

namespace MirbadLLM.UnitTests.Downloads;

/// <summary>Hosts a minimal <see cref="WebApplication"/> mapping only <see cref="DownloadApiEndpoints.MapDownloadApi"/> and <see cref="CatalogApiEndpoints.MapCatalogApi"/>.</summary>
public sealed class DownloadAndCatalogApiTests : IAsyncDisposable, IDisposable
{
    private readonly string _modelsRoot;
    private readonly FakeHuggingFaceDownloader _downloader = new();
    private readonly FakeHuggingFaceRepoInspector _repoInspector = new();
    private readonly WebApplication _app;
    private readonly HttpClient _client;

    public DownloadAndCatalogApiTests()
    {
        _modelsRoot = Path.Combine(Path.GetTempPath(), "mirbadllm-download-api-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_modelsRoot);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IHuggingFaceDownloader>(_downloader);
        builder.Services.AddSingleton<IHuggingFaceRepoInspector>(_repoInspector);
        builder.Services.AddNodeSourcing(_modelsRoot);

        _app = builder.Build();
        var api = _app.MapGroup("/api");
        api.MapDownloadApi();
        api.MapCatalogApi();
        _app.StartAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        _client = _app.GetTestClient();
    }

    [Fact]
    public async Task PostDownloads_StartsDownload_Returns201()
    {
        var response = await _client.PostAsJsonAsync("/api/downloads", new DownloadRequest("ornith-ai/Ornith-1.5-9B"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<DownloadStatus>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Ornith-1.5-9B", status!.Folder);
        Assert.Single(_downloader.StartCalls);
    }

    [Fact]
    public async Task GetDownloads_ListsStartedDownloads()
    {
        _downloader.Seed(new DownloadStatus("f", "org/f", null, DownloadState.Downloading, 10, 100, 5, null));

        var response = await _client.GetFromJsonAsync<List<DownloadStatus>>("/api/downloads", TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.Equal("f", Assert.Single(response).Folder);
    }

    [Fact]
    public async Task DeleteDownloads_UnknownFolder_Returns404()
    {
        var response = await _client.DeleteAsync("/api/downloads/does-not-exist", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteDownloads_KnownFolder_Returns204()
    {
        _downloader.Seed(new DownloadStatus("f", "org/f", null, DownloadState.Downloading, 10, 100, null, null));

        var response = await _client.DeleteAsync("/api/downloads/f", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("f", _downloader.CancelCalls);
    }

    [Fact]
    public async Task GetDownloadsInspect_MissingRepoId_Returns400()
    {
        var response = await _client.GetAsync("/api/downloads/inspect", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetDownloadsInspect_KnownRepo_ReturnsInspection()
    {
        var variant = new GgufVariant("Q4_K_M", [new RepoFile("model-Q4_K_M.gguf", 900)], 900);
        _repoInspector.Seed("some-org/gguf-repo", new RepoInspection("some-org/gguf-repo", null, RepoFormat.Gguf, [], [variant], [], []));

        var inspection = await _client.GetFromJsonAsync<RepoInspection>(
            "/api/downloads/inspect?repoId=some-org/gguf-repo", TestContext.Current.CancellationToken);

        Assert.NotNull(inspection);
        Assert.Equal(RepoFormat.Gguf, inspection!.Format);
        Assert.Equal("Q4_K_M", Assert.Single(inspection.Variants).Name);
    }

    [Fact]
    public async Task GetDownloadsInspect_PassesTokenAsHeader_NotQueryString()
    {
        _repoInspector.Seed("gated-org/gated-repo", new RepoInspection("gated-org/gated-repo", null, RepoFormat.Safetensors, [], [], [], []));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/downloads/inspect?repoId=gated-org/gated-repo");
        request.Headers.Add("X-HF-Token", "secret-token-123");
        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(_repoInspector.InspectCalls);
        Assert.Equal("secret-token-123", call.Token);
    }

    [Fact]
    public async Task GetDownloadsInspect_GatedRepo_Returns422WithMessage()
    {
        _repoInspector.SeedError("meta-llama/Llama-3.1-8B-Instruct", new HuggingFaceApiException("looks like a gated or private model"));

        var response = await _client.GetAsync(
            "/api/downloads/inspect?repoId=meta-llama/Llama-3.1-8B-Instruct", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task GetCatalog_ReturnsFixedOptionLists()
    {
        var catalog = await _client.GetFromJsonAsync<VllmCatalog>("/api/catalog", TestContext.Current.CancellationToken);

        Assert.NotNull(catalog);
        Assert.Contains("qwen3_coder", catalog!.ToolCallParsers);
    }

    [Fact]
    public async Task GetModelFolders_EmptyModelsRoot_ReturnsEmptyList()
    {
        var folders = await _client.GetFromJsonAsync<List<ModelFolderSummary>>("/api/model-folders", TestContext.Current.CancellationToken);

        Assert.NotNull(folders);
        Assert.Empty(folders!);
    }

    [Fact]
    public async Task GetModelFolder_UnknownFolder_Returns404()
    {
        var response = await _client.GetAsync("/api/model-folders/does-not-exist", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        if (Directory.Exists(_modelsRoot))
        {
            Directory.Delete(_modelsRoot, recursive: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(TestContext.Current.CancellationToken);
        await _app.DisposeAsync();
    }
}
