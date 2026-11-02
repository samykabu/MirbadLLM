using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.UnitTests.Orchestration.Fakes;
using MirbadLLM.UnitTests.Multinode;
using MirbadLLM.Web;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707

public sealed class ModelStartServiceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static ModelDefinition Model(string id, string node = "pc", double budget = 4) => new()
    {
        Id = id, ServedName = id, ModelPath = "checkpoint", Placement = new ModelPlacement(node), MemoryBudgetGiB = budget,
    };

    [Fact]
    public async Task ClicksQueueInOrder_OneLoadPerNode_OtherNodesStartIndependently_AndDuplicateClicksCoalesce()
    {
        var models = new[] { Model("a"), Model("b"), Model("c"), Model("d", "mac") };
        var registry = new FakeModelRegistry();
        foreach (var model in models) registry.Add(model);
        var runtime = new LoadingRuntime(models);
        using var host = new HostBuilder().Build();
        var queue = new ModelStartService(registry, runtime, host.Services.GetRequiredService<IHostApplicationLifetime>());

        Assert.Equal(ModelState.Starting, (await queue.StartAsync("a", ct: Ct).WaitAsync(Wait, Ct)).State);
        var b = queue.StartAsync("b", ct: Ct);
        var duplicate = queue.StartAsync("b", ct: Ct);
        var c = queue.StartAsync("c", ct: Ct);
        await queue.StartAsync("d", ct: Ct).WaitAsync(Wait, Ct);
        Assert.True(queue.IsQueued("b"));
        Assert.True(queue.IsQueued("c"));
        Assert.Equal(["a", "d"], runtime.Starts.Select(s => s.Id));

        runtime.Set("a", ModelState.Ready);
        await Task.WhenAll(b, duplicate).WaitAsync(Wait, Ct);
        Assert.False(c.IsCompleted);
        runtime.Set("b", ModelState.Failed);
        await c.WaitAsync(Wait, Ct);
        Assert.Equal(["a", "d", "b", "c"], runtime.Starts.Select(s => s.Id));
        Assert.All(runtime.Starts, start => Assert.False(start.Force));
    }

    [Fact]
    public async Task CapacityIsCheckedAtDispatch_ARefusalDoesNotBlockTheNextModel()
    {
        var models = new[] { Model("a"), Model("b"), Model("small", budget: 1) };
        var registry = new FakeModelRegistry();
        foreach (var model in models) registry.Add(model);
        var runtime = new LoadingRuntime(models);
        using var host = new HostBuilder().Build();
        var queue = new ModelStartService(registry, runtime, host.Services.GetRequiredService<IHostApplicationLifetime>());
        await queue.StartAsync("a", ct: Ct).WaitAsync(Wait, Ct);
        var b = queue.StartAsync("b", ct: Ct);
        var small = queue.StartAsync("small", ct: Ct);
        runtime.AvailableGiB = 2;
        runtime.Set("a", ModelState.Ready);

        await Assert.ThrowsAsync<ModelMemoryAdmissionException>(() => b.WaitAsync(Wait, Ct));
        Assert.Equal(ModelState.Starting, (await small.WaitAsync(Wait, Ct)).State);
        Assert.Equal(["a", "small"], runtime.Starts.Select(s => s.Id));
    }

    [Fact]
    public async Task CancelingAQueuedStart_CompletesPromptly_AndNeverLaunchesIt()
    {
        var models = new[] { Model("a"), Model("b"), Model("c") };
        var registry = new FakeModelRegistry();
        foreach (var model in models) registry.Add(model);
        var runtime = new LoadingRuntime(models);
        using var host = new HostBuilder().Build();
        var queue = new ModelStartService(registry, runtime, host.Services.GetRequiredService<IHostApplicationLifetime>());
        await queue.StartAsync("a", ct: Ct).WaitAsync(Wait, Ct);
        var b = queue.StartAsync("b", ct: Ct);
        var c = queue.StartAsync("c", ct: Ct);
        Assert.True(queue.Cancel("c")); // c is behind b, which is waiting for a.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.WaitAsync(Wait, Ct));
        Assert.False(queue.IsPending("c"));
        runtime.Set("a", ModelState.Ready);
        await b.WaitAsync(Wait, Ct);
        Assert.Equal(["a", "b"], runtime.Starts.Select(s => s.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AQueuedModelMovedOrDeletedBeforeItsTurn_IsNotLaunched(bool deleted)
    {
        var a = Model("a");
        var b = Model("b");
        var registry = new FakeModelRegistry().Add(a).Add(b);
        var runtime = new LoadingRuntime([a, b]);
        using var host = new HostBuilder().Build();
        var queue = new ModelStartService(registry, runtime, host.Services.GetRequiredService<IHostApplicationLifetime>());
        await queue.StartAsync("a", ct: Ct).WaitAsync(Wait, Ct);
        var pending = queue.StartAsync("b", ct: Ct);
        if (deleted) await registry.DeleteAsync("b", Ct);
        else await registry.UpsertAsync(b with { Placement = new ModelPlacement("mac") }, Ct);
        runtime.Set("a", ModelState.Ready);

        if (deleted) await Assert.ThrowsAsync<KeyNotFoundException>(() => pending.WaitAsync(Wait, Ct));
        else await Assert.ThrowsAsync<ModelStartConflictException>(() => pending.WaitAsync(Wait, Ct));
        Assert.Equal(["a"], runtime.Starts.Select(s => s.Id));
    }

    [Fact]
    public async Task TheRealRemoteRuntime_ReceivesTheSecondLaunchOnlyAfterTheAgentFinishesTheFirstLoad()
    {
        var a = Model("a", "dgx");
        var b = Model("b", "dgx");
        var registry = new FakeModelRegistry().Add(a).Add(b);
        await using var env = await RemoteRuntimeEnv.StartAsync(registry);
        using var host = new HostBuilder().Build();
        var queue = new ModelStartService(registry, env.Runtime, host.Services.GetRequiredService<IHostApplicationLifetime>());
        await env.Agent.SetStartsHeldAsync(true);
        await queue.StartAsync("a", ct: Ct).WaitAsync(Wait, Ct);
        var pending = queue.StartAsync("b", ct: Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(1200), Ct);
        Assert.Single(env.Agent.LaunchRequests);
        Assert.True(queue.IsQueued("b"));

        await env.Agent.SetStartsHeldAsync(false);
        var deadline = DateTimeOffset.UtcNow + Wait;
        while (env.Cache.GetRuntime("a")?.State != ModelState.Ready && DateTimeOffset.UtcNow < deadline)
        {
            await env.Cache.RefreshNodeAsync("dgx", Ct);
            await Task.Delay(20, Ct);
        }

        Assert.Equal(ModelState.Ready, env.Cache.GetRuntime("a")?.State);
        await pending.WaitAsync(Wait, Ct);
        Assert.Equal(2, env.Agent.LaunchRequests.Count);
    }

    [Fact]
    public async Task AnAgentSlotConflict_IsQueuedAgain_AndShutdownCancelsWaitingRequests()
    {
        var model = Model("a");
        var runtime = new LoadingRuntime([model]) { ConflictsRemaining = 1 };
        using var host = new HostBuilder().Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var queue = new ModelStartService(new FakeModelRegistry().Add(model), runtime, lifetime);
        await queue.StartAsync("a", ct: Ct).WaitAsync(Wait, Ct);
        Assert.Single(runtime.Starts);

        var b = Model("b");
        var second = new ModelStartService(new FakeModelRegistry().Add(model).Add(b), runtime, lifetime);
        var waiting = second.StartAsync("b", ct: Ct);
        Assert.True(second.IsQueued("b"));
        lifetime.StopApplication();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Wait, Ct));
        Assert.Single(runtime.Starts);
    }

    [Fact]
    public async Task AJobAcquiredWhileWaiting_PreventsTheManualStart()
    {
        string path = Path.Combine(Path.GetTempPath(), "mirbadllm-start-queue-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var ownership = new OwnershipStore(Options.Create(new MirbadLLMOptions { DataPath = path }));
            var a = Model("a");
            var b = Model("b");
            var runtime = new LoadingRuntime([a, b]);
            using var host = new HostBuilder().Build();
            var queue = new ModelStartService(new FakeModelRegistry().Add(a).Add(b), runtime,
                host.Services.GetRequiredService<IHostApplicationLifetime>(), ownership);
            await queue.StartAsync("a", ct: Ct).WaitAsync(Wait, Ct);
            var pending = queue.StartAsync("b", ct: Ct);
            await ownership.TryAcquireAsync(new OwnershipRecord
            {
                Id = "job-1", ModelId = "b", Kind = OwnershipKind.Job, Stage = "tuning",
                StartedAt = DateTimeOffset.UtcNow, Description = "a tuning run",
            }, Ct);
            runtime.Set("a", ModelState.Ready);
            await Assert.ThrowsAsync<ModelBusyException>(() => pending.WaitAsync(Wait, Ct));
            Assert.Equal(["a"], runtime.Starts.Select(s => s.Id));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    internal sealed class LoadingRuntime(IReadOnlyList<ModelDefinition> models) : IModelRuntime
    {
        private readonly ConcurrentDictionary<string, ModelStatus> _states = new(StringComparer.Ordinal);
        public ConcurrentQueue<(string Id, bool Force)> Starts { get; } = new();
        public double AvailableGiB { get; set; } = 100;
        public int ConflictsRemaining { get; set; }
        public void Set(string id, ModelState state) => _states[id] = new(id, state);
        public Task<IReadOnlyList<ModelStatus>> ListStatusAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelStatus>>([.. _states.Values]);
        public Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default) => Task.FromResult(_states.GetValueOrDefault(id) ?? new(id, ModelState.Stopped));
        public Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (ConflictsRemaining-- > 0) throw new ModelStartConflictException("An automatic start took the slot.");
            var model = models.Single(m => m.Id == id);
            Assert.DoesNotContain(_states.Values, s => s.Id != id && s.State == ModelState.Starting && models.Single(m => m.Id == s.Id).Placement == model.Placement);
            if (!force && model.MemoryBudgetGiB > AvailableGiB) throw new ModelMemoryAdmissionException("The node no longer has capacity.");
            Starts.Enqueue((id, force));
            Set(id, ModelState.Starting);
            return GetStatusAsync(id, ct);
        }
        public Task StopAsync(string id, CancellationToken ct = default) { Set(id, ModelState.Stopped); return Task.CompletedTask; }
        public IAsyncEnumerable<string> StreamLogsAsync(string id, int tail = 200, bool follow = true, CancellationToken ct = default) => AsyncEnumerable.Empty<string>();
    }
}
