using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.ServiceProcess;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Agent.Storage;

namespace MirbadLLM.UnitTests.Agent;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// MN-215 (design-p3p4 §A.4.1, D107, D122; spike S4 F7): the agent under the Windows SCM. S4 already installed and ran a
/// real service on the owner's PC (RUNNING in 0.5 s, stop in 0.3 s, exit code 0), so no test installs one: the SCM lifetime
/// is composed and driven in-process (its stop and pre-shutdown paths go through <c>ServiceBase.Stop</c>, exactly what
/// the SCM's control handler queues), and the pre-Run phase that counts against the SCM's 30 s is bounded with the real
/// <c>Program.CreateAsync</c>. Console runs (Linux, the DGX container, the dev setup) register nothing.
/// </summary>
public sealed class WindowsServiceHostingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------- console runs are unchanged

    [Fact]
    public async Task OutsideTheScm_NothingIsRegistered_AndTheConsoleLifetimeStays()
    {
        Assert.False(WindowsServiceHosting.IsWindowsService);
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        int before = builder.Services.Count;

        WindowsServiceHosting.Configure(builder);
        Assert.Equal(before, builder.Services.Count);
        await using WebApplication app = builder.Build();

        Assert.Equal("ConsoleLifetime", app.Services.GetRequiredService<IHostLifetime>().GetType().Name);
        Assert.DoesNotContain(app.Services.GetServices<ILoggerProvider>(), p => p.GetType().Name == "EventLogLoggerProvider");
    }

    [Fact]
    public void OutsideTheScm_TheStartupLoggersGetNoEventLog()
    {
        using ILoggerFactory loggers = LoggerFactory.Create(logging =>
        {
            int before = logging.Services.Count;
            WindowsServiceHosting.AddStartupEventLog(logging);
            Assert.Equal(before, logging.Services.Count);
        });
    }

    // ---------------------------------------------------------------- the pre-Run phase (what counts against the SCM's 30 s)

    [Fact]
    public async Task APreRunPhaseWithoutDocker_DoesNotWaitForDocker_AndFinishesInAFewSeconds()
    {
        // A Windows-configured agent (Agent:Docker:Enabled = false) whose Docker endpoint never answers: before MN-215 the
        // bounded Docker wait (here 60 s) ran before app.RunAsync and alone exceeded the SCM's 30 s.
        await using var node = await ContractNode.CreateAsync(enroll: true, Ct);
        node.Docker.Down = true;
        var watch = Stopwatch.StartNew();

        await using WebApplication app = await node.BuildAgentAsync(o =>
        {
            o.Docker.Enabled = false;
            o.DockerStartupTimeout = TimeSpan.FromSeconds(60);
        }, Ct);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"The pre-Run phase took {watch.Elapsed.TotalSeconds:0.0} s.");
    }

    [Fact]
    public async Task WithDockerEnabled_TheBoundedDockerWaitIsUnchanged()
    {
        // The DGX and every Linux agent (Docker:Enabled defaults to true) still wait for Docker before reconciling (D56).
        await using var node = await ContractNode.CreateAsync(enroll: true, Ct);
        node.Docker.Down = true;
        var watch = Stopwatch.StartNew();

        await using WebApplication app = await node.BuildAgentAsync(o => o.DockerStartupTimeout = TimeSpan.FromSeconds(1.5), Ct);

        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1.5), $"Docker was waited for only {watch.Elapsed.TotalSeconds:0.0} s.");
    }

    [Fact]
    public async Task NoHostedServiceBlocksTheStartOfTheHost()
    {
        // S4 / F7: hosted services start in order and Kestrel after them, so a blocking IHostedService.StartAsync keeps 9443
        // closed. Long start work belongs in BackgroundService.ExecuteAsync. The plain IHostedService implementations the
        // agent registers are audited here; a new one fails this test until it is reviewed and listed.
        await using var node = await ContractNode.CreateAsync(enroll: true, Ct);
        await using WebApplication app = await node.BuildAgentAsync(ct: Ct);

        var reviewed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Kestrel itself.
            ["Microsoft.AspNetCore.Hosting.GenericWebHostService"] = "the listener",
            // StartAsync returns a completed task; it only drains on stop.
            ["MirbadLLM.Agent.AgentDrain"] = "no start work",
            // Waits on Docker in StartAsync; MN-232 does not register it with Agent:Docker:Enabled = false (D122).
            [typeof(DownloadHelperCleanup).FullName!] = "Docker only",
        };
        var unreviewed = app.Services.GetServices<IHostedService>()
            .Select(s => s.GetType())
            .Where(t => !typeof(BackgroundService).IsAssignableFrom(t) && !reviewed.ContainsKey(t.FullName!))
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(unreviewed);
        IHostedService drain = app.Services.GetServices<IHostedService>().Single(s => s.GetType().FullName == "MirbadLLM.Agent.AgentDrain");
        Assert.True(drain.StartAsync(Ct).IsCompletedSuccessfully);
    }

    // ---------------------------------------------------------------- the SCM lifetime, in-process (Windows only)

    [Fact]
    [Trait("Os", "Windows")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task UnderTheScm_TheAgentLifetimeReplacesThePackagesAndLogsToTheEventLog()
    {
        SkipUnlessWindows();
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(42));

        // What AddWindowsService registers under the SCM (it is a no-op here), then the agent's part.
        builder.Services.AddSingleton<IHostLifetime, WindowsServiceLifetime>();
        WindowsServiceHosting.AddAgentServiceLifetime(builder.Services);
        await using WebApplication app = builder.Build();

        var lifetime = Assert.IsType<AgentServiceLifetime>(app.Services.GetRequiredService<IHostLifetime>());
        Assert.Single(app.Services.GetServices<IHostLifetime>());
        Assert.Equal(WindowsServiceHosting.ServiceName, lifetime.ServiceName);
        Assert.False(lifetime.AutoLog);
        Assert.True(lifetime.CanStop);
        Assert.True(lifetime.CanShutdown);
        Assert.Equal(TimeSpan.FromSeconds(42) + WindowsServiceHosting.WaitHintInterval, lifetime.InitialWaitHint);
        Assert.Equal(WindowsServiceHosting.WaitHintInterval * 2, lifetime.RenewedWaitHint);

        EventLogSettings eventLog = app.Services.GetRequiredService<IOptions<EventLogSettings>>().Value;
        Assert.Equal("mirbadllm-agent", eventLog.SourceName);
        Assert.Equal("Application", eventLog.LogName);
        LoggerFilterOptions filters = app.Services.GetRequiredService<IOptions<LoggerFilterOptions>>().Value;
        Assert.Contains(filters.Rules, r => r.ProviderName == typeof(EventLogLoggerProvider).FullName && r.CategoryName == "Microsoft" && r.LogLevel == LogLevel.Warning);
        Assert.Contains(filters.Rules, r => r.ProviderName == typeof(EventLogLoggerProvider).FullName && r.CategoryName is null && r.LogLevel == LogLevel.Information);
    }

    [Fact]
    [Trait("Os", "Windows")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void PreshutdownIsAccepted_ThroughServiceBasesAcceptedControls()
    {
        SkipUnlessWindows();
        using var rig = new LifetimeRig();

        // If a framework update renames the field, this fails (and the service would fall back to SHUTDOWN without hints).
        FieldInfo field = Assert.IsAssignableFrom<FieldInfo>(AgentServiceLifetime.AcceptedCommandsField);
        int accepted = (int)field.GetValue(rig.Lifetime)!;

        Assert.True(rig.Lifetime.PreshutdownAccepted);
        Assert.Equal(WindowsServiceHosting.AcceptPreshutdown, accepted & WindowsServiceHosting.AcceptPreshutdown);
        Assert.Equal(0x1, accepted & 0x1); // SERVICE_ACCEPT_STOP
        Assert.Equal(0x4, accepted & 0x4); // SERVICE_ACCEPT_SHUTDOWN
    }

    [Fact]
    [Trait("Os", "Windows")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task AStopControl_DrainsTheHostWithinTheShutdownTimeout_RenewingTheWaitHint()
    {
        SkipUnlessWindows();
        using var rig = new LifetimeRig(shutdownTimeout: TimeSpan.FromSeconds(2), drain: TimeSpan.FromMilliseconds(500));
        await rig.StartAsync();
        var watch = Stopwatch.StartNew();

        // ServiceBase.Stop is what the SCM's stop control queues (DeferredStop: STOP_PENDING, OnStop, STOPPED).
        await Task.Run(rig.Lifetime.Stop, Ct);
        watch.Stop();
        await rig.Run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        int hintsWhenStopped = rig.Hints.Count;
        await Task.Delay(rig.Interval * 4, Ct);

        Assert.True(rig.Drained.Task.IsCompletedSuccessfully, "The host did not drain its hosted services.");
        Assert.True(watch.Elapsed < rig.ShutdownTimeout + TimeSpan.FromSeconds(2), $"The stop took {watch.Elapsed.TotalSeconds:0.0} s.");
        TimeSpan[] hints = [.. rig.Hints];
        Assert.Equal(rig.ShutdownTimeout + rig.Interval, hints[0]);
        Assert.True(hints.Length >= 4, $"Only {hints.Length} wait hint(s) during a {watch.Elapsed.TotalSeconds:0.0} s stop.");
        Assert.All(hints[1..], h => Assert.Equal(rig.Interval * 2, h));
        Assert.Equal(hintsWhenStopped, rig.Hints.Count); // nothing after STOPPED
    }

    [Fact]
    [Trait("Os", "Windows")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task APreshutdownControl_StopsThroughTheSamePath()
    {
        SkipUnlessWindows();
        using var rig = new LifetimeRig(shutdownTimeout: TimeSpan.FromSeconds(1), drain: TimeSpan.FromMilliseconds(200));
        await rig.StartAsync();

        await Task.Run(() => rig.SendControl(WindowsServiceHosting.PreshutdownControl), Ct);
        await rig.Run.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(rig.Drained.Task.IsCompletedSuccessfully);
        Assert.NotEmpty(rig.Hints);
        Assert.Equal(rig.ShutdownTimeout + rig.Interval, rig.Hints.First());
    }

    [Fact]
    [Trait("Os", "Windows")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task AnotherCustomControl_DoesNotStopTheService()
    {
        SkipUnlessWindows();
        using var rig = new LifetimeRig();
        await rig.StartAsync();

        rig.SendControl(200);
        await Task.Delay(300, Ct);

        Assert.False(rig.Run.IsCompleted);
        Assert.Empty(rig.Hints);
        await rig.Host.StopAsync(Ct);
    }

    private static void SkipUnlessWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The Windows service lifetime exists only on Windows.");
        }
    }

    /// <summary>
    /// A real host (console lifetime, one hosted service that drains for a while) and an <see cref="AgentServiceLifetime"/>
    /// bound to its application lifetime, with a recording wait-hint sink. The SCM dispatcher is not involved, so the
    /// base lifetime's wait for <c>ApplicationStopped</c> (registered in <c>WaitForStartAsync</c>, which only the SCM can
    /// run) lasts its full <see cref="HostOptions.ShutdownTimeout"/>.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class LifetimeRig : IDisposable
    {
        public LifetimeRig(TimeSpan? shutdownTimeout = null, TimeSpan? drain = null)
        {
            ShutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(2);
            HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = ShutdownTimeout);
            builder.Services.AddHostedService(_ => new DrainingService(drain ?? TimeSpan.Zero, Drained));
            Host = builder.Build();
            Lifetime = new AgentServiceLifetime(
                Host.Services.GetRequiredService<IHostEnvironment>(),
                Host.Services.GetRequiredService<IHostApplicationLifetime>(),
                NullLoggerFactory.Instance,
                Host.Services.GetRequiredService<IOptions<HostOptions>>(),
                Options.Create(new WindowsServiceLifetimeOptions { ServiceName = WindowsServiceHosting.ServiceName }),
                Hints.Enqueue,
                Interval);
        }

        public TimeSpan ShutdownTimeout { get; }

        public TimeSpan Interval { get; } = TimeSpan.FromMilliseconds(100);

        public IHost Host { get; }

        public AgentServiceLifetime Lifetime { get; }

        public ConcurrentQueue<TimeSpan> Hints { get; } = new();

        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Run { get; private set; } = Task.CompletedTask;

        public async Task StartAsync()
        {
            Run = Host.RunAsync();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (Host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() => started.TrySetResult()))
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        /// <summary>What the SCM's control handler does for a control it has no case for: <c>OnCustomCommand</c>.</summary>
        public void SendControl(int control) =>
            typeof(AgentServiceLifetime).GetMethod("OnCustomCommand", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Lifetime, [control]);

        public void Dispose()
        {
            Lifetime.Dispose();
            Host.Dispose();
        }
    }

    private sealed class DrainingService(TimeSpan drain, TaskCompletionSource drained) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(drain, cancellationToken);
            drained.TrySetResult();
        }
    }
}
