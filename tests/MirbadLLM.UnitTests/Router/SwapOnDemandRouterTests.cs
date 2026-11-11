using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.Router;
using MirbadLLM.UnitTests.Orchestration.Fakes;
using MirbadLLM.UnitTests.Router.Fakes;
using FakeModelRuntime = MirbadLLM.UnitTests.Router.Fakes.FakeModelRuntime;

#pragma warning disable CA1707 // underscore test names.

namespace MirbadLLM.UnitTests.Router;

/// <summary>The router's side of swap on demand: the per-model start on request, the one retry after room was made, and use tracking.</summary>
public sealed class SwapOnDemandRouterTests
{
    private const string ModelId = "chat-1";
    private const string ServedName = "chat-model";
    private static readonly Uri Upstream = new("http://upstream/");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ModelDefinition Model(bool startOnRequest) =>
        new() { Id = ModelId, ServedName = ServedName, ModelPath = "chat", StartOnRequest = startOnRequest };

    /// <summary>A router over a stopped model whose start makes it Ready, unless <paramref name="start"/> says otherwise.</summary>
    private static async Task<(WebApplication App, FakeModelRuntime Runtime)> StartAsync(
        ModelDefinition model, bool globalSwitch = false, Func<int, ModelStatus?>? start = null, IRoomMaker? rooms = null, TimeSpan? startupTimeout = null)
    {
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint(ModelId, ServedName, Upstream, ModelState.Stopped));
        var runtime = new FakeModelRuntime();
        runtime.StartHandler = (id, _) =>
        {
            if (start?.Invoke(runtime.StartCallCount) is { } answer)
            {
                return answer;
            }

            resolver.Set(new ModelEndpoint(id, ServedName, Upstream, ModelState.Ready));
            return new ModelStatus(id, ModelState.Ready);
        };
        var upstream = new FakeUpstreamHandler
        {
            Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }),
        };
        var app = await RouterHostFactory.CreateAsync(
            resolver,
            runtime,
            o =>
            {
                o.AutoStartOnRequest = globalSwitch;
                o.StartupTimeout = startupTimeout ?? TimeSpan.FromSeconds(5);
            },
            services =>
            {
                RouterProxyTests.ConfigureUpstream(services, upstream);
                services.AddSingleton<IModelRegistry>(new FakeModelRegistry().Add(model));
                if (rooms is not null)
                {
                    services.AddSingleton(rooms);
                }
            });
        return (app, runtime);
    }

    private static Task<HttpResponseMessage> PostAsync(WebApplication app) =>
        app.GetTestClient().PostAsJsonAsync("/v1/chat/completions", new { model = ServedName }, Ct);

    [Fact]
    public async Task StartOnRequest_OnTheModel_StartsItWithTheGlobalSwitchOff()
    {
        var (app, runtime) = await StartAsync(Model(startOnRequest: true));
        using var host = app;

        using var response = await PostAsync(app);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, runtime.StartCallCount);
    }

    [Fact]
    public async Task AStoppedModel_WithNeitherSwitch_IsNotStarted()
    {
        var (app, runtime) = await StartAsync(Model(startOnRequest: false));
        using var host = app;

        using var response = await PostAsync(app);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, runtime.StartCallCount);
    }

    [Fact]
    public async Task MemoryRefused_ThenRoomMade_TheStartIsTriedAgainAndTheRequestAnswered()
    {
        var rooms = new FakeRoomMaker(answer: true);
        var (app, runtime) = await StartAsync(
            Model(startOnRequest: true),
            start: call => call == 1 ? throw new ModelMemoryAdmissionException("not enough memory") : null,
            rooms: rooms);
        using var host = app;

        using var response = await PostAsync(app);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, runtime.StartCallCount);
        Assert.Equal([ModelId], rooms.Asked);
    }

    [Fact]
    public async Task MemoryRefused_AgainAfterRoomWasMade_IsTheUsualRefusal_WithNoThirdStart()
    {
        var rooms = new FakeRoomMaker(answer: true);
        var (app, runtime) = await StartAsync(
            Model(startOnRequest: true),
            start: _ => throw new ModelMemoryAdmissionException("not enough memory"),
            rooms: rooms);
        using var host = app;

        using var response = await PostAsync(app);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("model_not_ready", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(2, runtime.StartCallCount);
        Assert.Single(rooms.Asked);
    }

    [Fact]
    public async Task MemoryRefused_AndNoRoomMade_IsTheUsualRefusal_WithNoSecondStart()
    {
        var rooms = new FakeRoomMaker(answer: false);
        var (app, runtime) = await StartAsync(
            Model(startOnRequest: false),
            globalSwitch: true,
            start: _ => throw new ModelMemoryAdmissionException("not enough memory"),
            rooms: rooms);
        using var host = app;

        using var response = await PostAsync(app);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, runtime.StartCallCount);
        Assert.Single(rooms.Asked);
    }

    [Fact]
    public async Task ARequestWaitingForItsModel_CountsAsUse_UntilItEnds()
    {
        WebApplication? host = null;
        bool inUseWhileWaiting = false;
        var (app, _) = await StartAsync(
            Model(startOnRequest: true),
            start: _ =>
            {
                inUseWhileWaiting = host!.Services.GetRequiredService<IRouterStats>().InUse(ServedName);
                return new ModelStatus(ModelId, ModelState.Starting); // never becomes Ready
            },
            startupTimeout: TimeSpan.FromMilliseconds(300));
        using var disposable = host = app;

        using var response = await PostAsync(app);

        var stats = app.Services.GetRequiredService<IRouterStats>();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(inUseWhileWaiting);
        Assert.False(stats.InUse(ServedName));
        Assert.Empty(stats.Snapshot()); // waiting is not a counted request
    }

    [Fact]
    public void RouterStats_ReportUse_AndTheLastRequestTime()
    {
        var time = new ManualTimeProvider();
        var stats = new RouterStats(time);
        Assert.False(stats.InUse("m"));
        Assert.Null(stats.LastRequestAt("m"));

        stats.RequestStarted("m");
        var started = time.GetUtcNow();
        Assert.True(stats.InUse("M")); // served names are case-insensitive
        Assert.Equal(started, stats.LastRequestAt("m"));

        time.Advance(TimeSpan.FromMinutes(3));
        stats.RequestCompleted("m", failed: false);

        Assert.False(stats.InUse("m"));
        Assert.Equal(started.AddMinutes(3), stats.LastRequestAt("m"));
    }

    [Fact]
    public void RouterStats_AWaitingRequest_IsUse_ButNotARow()
    {
        var stats = new RouterStats();

        using (stats.BeginWaiting("m"))
        {
            Assert.True(stats.InUse("m"));
            Assert.Empty(stats.Snapshot());
        }

        Assert.False(stats.InUse("m"));
        Assert.Null(stats.LastRequestAt("m"));
    }

    private sealed class FakeRoomMaker(bool answer) : IRoomMaker
    {
        public List<string> Asked { get; } = [];

        public Task<bool> TryMakeRoomAsync(string modelId, ModelMemoryAdmissionException refusal, CancellationToken ct = default)
        {
            Asked.Add(modelId);
            return Task.FromResult(answer);
        }
    }
}
