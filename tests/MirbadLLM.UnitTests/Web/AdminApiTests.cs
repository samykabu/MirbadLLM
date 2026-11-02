using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.UnitTests.Downloads.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Api;
using FakeHttpMessageHandler = MirbadLLM.UnitTests.Orchestration.Fakes.FakeHttpMessageHandler;

namespace MirbadLLM.UnitTests.Web;

/// <summary>
/// Hosts a minimal <see cref="WebApplication"/> on <see cref="TestServer"/> with hand-written fakes for
/// every Core interface and maps only <see cref="AdminApiEndpoints.MapAdminApi"/> — no dependency on the
/// Orchestration or Router workstreams' real implementations.
/// </summary>
public sealed class AdminApiTests
{
    // The client-side default JsonSerializerOptions is case-sensitive/PascalCase, but the admin API
    // (see Program.cs) serializes camelCase with string enums, so response deserialization needs to
    // opt into the same conventions explicitly.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly ModelDefinition SampleModel = new()
    {
        Id = "test-model",
        ServedName = "Test-Model",
        ModelPath = "test-model-checkpoint",
        MemoryBudgetGiB = 20,
    };

    private sealed class TestHost : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required FakeModelRegistry Registry { get; init; }
        public required FakeModelRuntime Runtime { get; init; }
        public required FakeModelMetricsReader Metrics { get; init; }
        public required FakeRouterStats RouterStats { get; init; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync(TestContext.Current.CancellationToken);
            await App.DisposeAsync();
        }
    }

    private static async Task<TestHost> StartAsync(
        IEnumerable<ModelDefinition>? seedModels = null,
        MemorySnapshot? memory = null,
        IReadOnlyList<RouterModelStats>? routerSnapshot = null)
    {
        var registry = new FakeModelRegistry(seedModels ?? []);
        var runtime = new FakeModelRuntime();
        var metrics = new FakeModelMetricsReader();
        var routerStats = new FakeRouterStats(routerSnapshot ?? []);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureApiJson();
        builder.Services.AddSingleton<IModelRegistry>(registry);
        builder.Services.AddSingleton<IModelRuntime>(runtime);
        builder.Services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(memory ?? new MemorySnapshot(128, 64)));
        builder.Services.AddSingleton<IModelMetricsReader>(metrics);
        builder.Services.AddSingleton<IRouterStats>(routerStats);

        // MapAdminApi also maps the download/catalog endpoints (Section 1/2 of the "add model" work); they need
        // *some* implementation registered even though this host's own tests don't exercise them.
        builder.Services.AddSingleton<IHuggingFaceDownloader>(new FakeHuggingFaceDownloader());
        builder.Services.AddSingleton<IHuggingFaceRepoInspector>(new FakeHuggingFaceRepoInspector());
        builder.Services.AddNodeSourcing(Path.Combine(Path.GetTempPath(), "mirbadllm-tests-no-such-folder"));

        var app = builder.Build();
        app.MapAdminApi();
        await app.StartAsync(TestContext.Current.CancellationToken);

        return new TestHost
        {
            App = app,
            Client = app.GetTestClient(),
            Registry = registry,
            Runtime = runtime,
            Metrics = metrics,
            RouterStats = routerStats,
        };
    }

    [Fact]
    public async Task GetModelsReturnsSeededDefinitions()
    {
        await using var host = await StartAsync([SampleModel]);

        var models = await host.Client.GetFromJsonAsync<List<ModelDefinition>>("/api/models", JsonOptions, TestContext.Current.CancellationToken);

        Assert.NotNull(models);
        Assert.Equal(SampleModel.Id, Assert.Single(models).Id);
    }

    [Fact]
    public async Task GetModelsFiltersByTagWithAllSemanticsAndCarriesTagsAndSources()
    {
        var chat = SampleModel with { Id = "chat", ServedName = "Chat", Tags = ["chat", "vision"], Sources = [new ModelSource("o/Chat", "Chat")] };
        var embed = SampleModel with { Id = "embed", ServedName = "Embed", Tags = ["embedding"] };
        var plain = SampleModel with { Id = "plain", ServedName = "Plain", Tags = ["chat"] };
        await using var host = await StartAsync([chat, embed, plain]);
        var ct = TestContext.Current.CancellationToken;

        var both = await host.Client.GetFromJsonAsync<List<ModelDefinition>>("/api/models?tag=chat&tag=Vision", JsonOptions, ct);
        var one = await host.Client.GetFromJsonAsync<List<ModelDefinition>>("/api/models?tag=chat", JsonOptions, ct);
        var none = await host.Client.GetFromJsonAsync<List<ModelDefinition>>("/api/models?tag=chat&tag=embedding", JsonOptions, ct);
        var all = await host.Client.GetFromJsonAsync<List<ModelDefinition>>("/api/models", JsonOptions, ct);

        var match = Assert.Single(both!);
        Assert.Equal("chat", match.Id);
        Assert.Equal(["chat", "vision"], match.Tags);
        Assert.Equal("o/Chat", Assert.Single(match.Sources).RepoId);
        Assert.Equal(["chat", "plain"], one!.Select(m => m.Id).Order());
        Assert.Empty(none!);
        Assert.Equal(3, all!.Count);
    }

    [Fact]
    public async Task PostModelWithInvalidTagReturns400()
    {
        await using var host = await StartAsync();
        var bad = SampleModel with { Tags = ["not valid because it is far too long for a tag"] };

        using var response = await host.Client.PostAsJsonAsync("/api/models", bad, JsonOptions, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetModelByIdReturnsModel()
    {
        await using var host = await StartAsync([SampleModel]);

        var model = await host.Client.GetFromJsonAsync<ModelDefinition>($"/api/models/{SampleModel.Id}", JsonOptions, TestContext.Current.CancellationToken);

        Assert.NotNull(model);
        Assert.Equal(SampleModel.ServedName, model.ServedName);
    }

    [Fact]
    public async Task GetUnknownModelReturns404()
    {
        await using var host = await StartAsync();

        var response = await host.Client.GetAsync("/api/models/missing", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostModelsCreatesAndReturns201()
    {
        await using var host = await StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/models", SampleModel, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(await host.Registry.GetAsync(SampleModel.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PostModelsWithInvalidIdReturns400ProblemDetails()
    {
        await using var host = await StartAsync();
        var invalid = SampleModel with { Id = "Not_Valid_Id" };

        var response = await host.Client.PostAsJsonAsync("/api/models", invalid, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using var problem = await JsonDocument.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("Id", problem.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task PutModelsUpdatesExistingModel()
    {
        await using var host = await StartAsync([SampleModel]);
        var updated = SampleModel with { MemoryBudgetGiB = 42 };

        var response = await host.Client.PutAsJsonAsync($"/api/models/{SampleModel.Id}", updated, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await host.Registry.GetAsync(SampleModel.Id, TestContext.Current.CancellationToken);
        Assert.Equal(42, stored!.MemoryBudgetGiB);
    }

    [Fact]
    public async Task PutUnknownModelReturns404()
    {
        await using var host = await StartAsync();

        var response = await host.Client.PutAsJsonAsync("/api/models/missing", SampleModel, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteModelRemovesIt()
    {
        await using var host = await StartAsync([SampleModel]);

        var response = await host.Client.DeleteAsync($"/api/models/{SampleModel.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await host.Registry.GetAsync(SampleModel.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteUnknownModelReturns404()
    {
        await using var host = await StartAsync();

        var response = await host.Client.DeleteAsync("/api/models/missing", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StartModelReturnsStartingStatus()
    {
        await using var host = await StartAsync([SampleModel]);

        var response = await host.Client.PostAsync($"/api/models/{SampleModel.Id}/start", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<ModelStatus>(JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(ModelState.Starting, status!.State);
    }

    [Fact]
    public async Task StartUnknownModelReturns404()
    {
        await using var host = await StartAsync();

        var response = await host.Client.PostAsync("/api/models/missing/start", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StartWithoutForceReturns409WhenMemoryInsufficient()
    {
        await using var host = await StartAsync([SampleModel]);
        host.Runtime.RefuseUnlessForce = true;
        host.Runtime.RefusalMessage = "Model needs 64 GiB but only 40 GiB is available.";

        var response = await host.Client.PostAsync($"/api/models/{SampleModel.Id}/start", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using var problem = await JsonDocument.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(host.Runtime.RefusalMessage, problem.RootElement.GetProperty("detail").GetString());
    }

    public static TheoryData<string, int> NodeRefusals => new()
    {
        { nameof(NodeUnavailableReason.NotServing), 503 },
        { nameof(NodeUnavailableReason.Unplaced), 409 },
        { nameof(NodeUnavailableReason.Draining), 409 },
        { "unreachable", 503 },
        { "interrupted", 409 },

        // MN-047: agent refusals keep the agent's status when it is the operator's to act on (the master's own faults,
        // invalid_request or forbidden, stay unhandled: 500).
        { "agent:" + MirbadLLM.Agent.Contracts.ErrorCodes.StaleMaster, 503 },
        { "agent:" + MirbadLLM.Agent.Contracts.ErrorCodes.DockerUnavailable, 503 },
        { "agent:" + MirbadLLM.Agent.Contracts.ErrorCodes.HostdUnavailable, 503 },
        { "agent:" + MirbadLLM.Agent.Contracts.ErrorCodes.StateUnavailable, 503 },
        { "agent:" + MirbadLLM.Agent.Contracts.ErrorCodes.Timeout, 504 },
        { "agent:" + MirbadLLM.Agent.Contracts.ErrorCodes.OutcomeUnknown, 504 },
        { "agent:" + MirbadLLM.Agent.Contracts.ErrorCodes.SpecRejected, 422 },
        { "agent:" + MirbadLLM.Agent.Contracts.ErrorCodes.InvalidState, 409 },
        { "agent:" + MirbadLLM.Agent.Contracts.ErrorCodes.OperationExpired, 410 },
        { "stop-timeout", 504 },
    };

    [Theory]
    [MemberData(nameof(NodeRefusals))]
    public async Task StartAndStopOnANodeThatCannotTakeTheCommandAnswerAProblemInsteadOf500(string refusal, int status)
    {
        // Multi-node wiring only (W3 review MN-094): today's runtime never throws these.
        await using var host = await StartAsync([SampleModel]);
        host.Runtime.Failure = refusal switch
        {
            "unreachable" => new MirbadLLM.Agent.Contracts.AgentUnreachableException("dgx", "The agent of node dgx is unreachable.", null),
            // A start that another action ended before its container existed (W4 review MN-096).
            "interrupted" => new ModelStartInterruptedException("The start of model 'x' ended cancelled before its container was created. (on node dgx)"),
            "stop-timeout" => new TimeoutException("Model 'x' did not stop within 120 s; the stop continues. (on node dgx)"),
            _ when refusal.StartsWith("agent:", StringComparison.Ordinal) => MirbadLLM.Orchestration.Agents.AgentErrorMapper.ToMasterException(
                new MirbadLLM.Agent.Contracts.AgentException(
                    MirbadLLM.Agent.Contracts.ErrorMappings.AgentStatus(refusal[6..]),
                    new MirbadLLM.Agent.Contracts.AgentError { Code = refusal[6..], Message = "Refused." },
                    "dgx")),
            _ => new NodeUnavailableException($"Node 'dgx' refused ({refusal}).") { Reason = Enum.Parse<NodeUnavailableReason>(refusal), NodeId = "dgx" },
        };

        foreach (string action in new[] { "start", "stop" })
        {
            var response = await host.Client.PostAsync($"/api/models/{SampleModel.Id}/{action}", content: null, TestContext.Current.CancellationToken);

            Assert.Equal(status, (int)response.StatusCode);
            using var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
            using var problem = await JsonDocument.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(host.Runtime.Failure.Message, problem.RootElement.GetProperty("detail").GetString());
        }
    }

    [Fact]
    public async Task StartWithForceSucceedsAfterRefusal()
    {
        await using var host = await StartAsync([SampleModel]);
        host.Runtime.RefuseUnlessForce = true;

        var response = await host.Client.PostAsync($"/api/models/{SampleModel.Id}/start?force=true", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task StopModelReturns204()
    {
        await using var host = await StartAsync([SampleModel]);
        host.Runtime.SetStatus(new ModelStatus(SampleModel.Id, ModelState.Ready));

        var response = await host.Client.PostAsync($"/api/models/{SampleModel.Id}/stop", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var status = await host.Runtime.GetStatusAsync(SampleModel.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ModelState.Stopped, status.State);
    }

    [Fact]
    public async Task StopUnknownModelReturns404()
    {
        await using var host = await StartAsync();

        var response = await host.Client.PostAsync("/api/models/missing/stop", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task LogsStreamAsServerSentEvents()
    {
        await using var host = await StartAsync([SampleModel]);
        host.Runtime.SetLogs(SampleModel.Id, "line one", "line two");

        var response = await host.Client.GetAsync(
            $"/api/models/{SampleModel.Id}/logs",
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("data: line one\n\ndata: line two\n\n", body);
    }

    [Fact]
    public async Task LogsForUnknownModelReturns404()
    {
        await using var host = await StartAsync();

        var response = await host.Client.GetAsync("/api/models/missing/logs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StatusReturnsMemoryModelsAndRouterSnapshot()
    {
        var memory = new MemorySnapshot(128, 90);
        var routerSnapshot = new List<RouterModelStats> { new(SampleModel.ServedName, 10, 1, 2) };
        await using var host = await StartAsync([SampleModel], memory, routerSnapshot);
        host.Runtime.SetStatus(new ModelStatus(SampleModel.Id, ModelState.Ready));
        host.Metrics.Set(SampleModel.Id, new ModelMetrics(1, 0, 0.5, 12.3, 4.5));

        var response = await host.Client.GetAsync("/api/status", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using var body = await JsonDocument.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
        var root = body.RootElement;

        Assert.Equal(128, root.GetProperty("memory").GetProperty("totalGiB").GetDouble());
        var models = root.GetProperty("models");
        Assert.Equal(1, models.GetArrayLength());
        Assert.Equal(SampleModel.Id, models[0].GetProperty("definition").GetProperty("id").GetString());
        Assert.Equal("Ready", models[0].GetProperty("status").GetProperty("state").GetString());
        Assert.Equal(12.3, models[0].GetProperty("metrics").GetProperty("generationTokensPerSecond").GetDouble());
        var router = root.GetProperty("router");
        Assert.Equal(1, router.GetArrayLength());
        Assert.Equal(2, router[0].GetProperty("inFlight").GetInt32());
    }

    [Fact]
    public async Task StatusWritesALlamaCppModelsMissingKvCacheAsNullInsteadOfFailing()
    {
        // ModelKpiCalculator reports NaN for numbers llama.cpp has no source for (KV-cache usage); MN-241 found /api/status
        // answering 500 with a truncated body once such a model was Ready.
        await using var host = await StartAsync([SampleModel]);
        host.Runtime.SetStatus(new ModelStatus(SampleModel.Id, ModelState.Ready));
        host.Metrics.Set(SampleModel.Id, new ModelMetrics(1, 0, double.NaN, double.PositiveInfinity, 4.5));

        var response = await host.Client.GetAsync("/api/status", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken);
        var metrics = body.RootElement.GetProperty("models")[0].GetProperty("metrics");
        Assert.Equal(JsonValueKind.Null, metrics.GetProperty("kvCacheUsage").ValueKind);
        Assert.Equal(JsonValueKind.Null, metrics.GetProperty("generationTokensPerSecond").ValueKind);
        Assert.Equal(4.5, metrics.GetProperty("promptTokensPerSecond").GetDouble());
    }

    [Fact]
    public void TheConverterReadsNullBackAsNaN()
    {
        var options = new JsonSerializerOptions();
        ApiJson.Configure(options);

        Assert.True(double.IsNaN(JsonSerializer.Deserialize<double>("null", options)));
        Assert.Equal(0.25, JsonSerializer.Deserialize<double>("0.25", options));
        Assert.Equal("[null,1.5]", JsonSerializer.Serialize(new[] { double.NegativeInfinity, 1.5 }, options));
    }
}
