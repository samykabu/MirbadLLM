using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.Router;
using MirbadLLM.UnitTests.Router.Fakes;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Router;

/// <summary>Maintenance mode: while a benchmark or tuning job runs, /v1 answers 503 (before the API-key check) and the browser chat is blocked.</summary>
public sealed class RouterMaintenanceTests
{
    private sealed class FakeMaintenance : IMaintenanceMode
    {
        public MaintenanceState? Current { get; set; }
    }

    private static MaintenanceState Window(TimeSpan left) =>
        new("job-1", "benchmark", "qwen", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow + left);

    [Fact]
    public async Task DuringMaintenance_V1Returns503_WithRetryAfter_AndTheReason()
    {
        var maintenance = new FakeMaintenance { Current = Window(TimeSpan.FromMinutes(7)) };
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("qwen", "Qwen", new Uri("http://upstream/"), ModelState.Ready));
        await using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime(),
            configureServices: s => s.AddSingleton<IMaintenanceMode>(maintenance));
        var client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri("/v1/models", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        int retryAfter = int.Parse(response.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 400, 421);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var error = body.RootElement.GetProperty("error");
        Assert.Equal("maintenance", error.GetProperty("code").GetString());
        Assert.StartsWith("mirbadllm is running a benchmark on qwen; API paused until ~", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Maintenance_PrecedesTheApiKeyCheck()
    {
        var maintenance = new FakeMaintenance { Current = Window(TimeSpan.FromMinutes(1)) };
        var keys = new FakeApiKeyStore();
        keys.AddActiveKey("ssk_secret-key", ApiKeyScope.Inference);
        await using var app = await RouterHostFactory.CreateAsync(new FakeModelEndpointResolver(), new FakeModelRuntime(),
            configureServices: s => s.AddSingleton<IMaintenanceMode>(maintenance), apiKeyStore: keys);
        var client = app.GetTestClient();

        using var noKey = await client.PostAsync(new Uri("/v1/chat/completions", UriKind.Relative),
            new StringContent("""{"model":"qwen"}"""), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, noKey.StatusCode);

        maintenance.Current = null;
        using var after = await client.GetAsync(new Uri("/v1/models", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task RetryAfter_IsAtLeast30Seconds_WhenTheEstimateHasPassed()
    {
        var maintenance = new FakeMaintenance { Current = Window(TimeSpan.FromMinutes(-2)) };
        await using var app = await RouterHostFactory.CreateAsync(new FakeModelEndpointResolver(), new FakeModelRuntime(),
            configureServices: s => s.AddSingleton<IMaintenanceMode>(maintenance));

        using var response = await app.GetTestClient().GetAsync(new Uri("/v1/models", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal("30", response.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task WithoutMaintenance_V1ServesNormally()
    {
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("qwen", "Qwen", new Uri("http://upstream/"), ModelState.Ready));
        await using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime(),
            configureServices: s => s.AddSingleton<IMaintenanceMode>(new FakeMaintenance()));

        using var response = await app.GetTestClient().GetAsync(new Uri("/v1/models", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ChatClient_IsBlockedDuringMaintenance_WithTheSameMessage()
    {
        var maintenance = new FakeMaintenance { Current = Window(TimeSpan.FromMinutes(3)) };
        var services = new ServiceCollection();
        services.AddMirbadLLMRouter();
        services.AddSingleton<IModelEndpointResolver>(new FakeModelEndpointResolver());
        services.AddSingleton<IMaintenanceMode>(maintenance);
        await using var provider = services.BuildServiceProvider();
        var chat = provider.GetRequiredService<IChatClient>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in chat.StreamAsync(new ChatRequest("Qwen", [new ChatMessage("user", "hi")]), TestContext.Current.CancellationToken))
            {
            }
        });

        Assert.StartsWith("mirbadllm is running a benchmark on qwen; API paused until ~", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_NamesTuningRuns()
    {
        var window = new MaintenanceState("j", "tuning", "qwen", DateTimeOffset.UtcNow, new DateTimeOffset(2026, 9, 29, 14, 5, 0, TimeSpan.Zero));
        string text = window.Describe(new DateTimeOffset(2026, 9, 29, 13, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
        Assert.Equal("mirbadllm is running a tuning run on qwen; API paused until ~14:05 UTC", text);
    }
}
