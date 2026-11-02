using System.Reflection;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Hosting;

/// <summary>
/// Runs the agent under the Windows Service Control Manager (design-p3p4 §A.4.1; owner decision O1, D107; MN-215) with
/// <c>Microsoft.Extensions.Hosting.WindowsServices</c>. Outside the SCM (Linux, the DGX container, the dev setup, a
/// console run on Windows) nothing is registered and the host keeps its console lifetime.
/// <para>Under the SCM:</para>
/// <list type="bullet">
/// <item><c>AddWindowsService</c> with the service name <see cref="ServiceName"/>, and the agent's
/// <see cref="AgentServiceLifetime"/> in place of the package's lifetime. RUNNING is reported when <c>app.RunAsync</c>
/// starts the host, before any hosted service (S4 / F7), so only <c>Program.CreateAsync</c> counts against the SCM's
/// 30 s: on a Windows-configured agent that is file reads and adoption only (no Docker wait).</item>
/// <item>Stop and pre-shutdown report <c>SERVICE_STOP_PENDING</c> with wait hints while the host drains within
/// <c>Agent:ShutdownTimeout</c> (<see cref="HostOptions.ShutdownTimeout"/>).</item>
/// <item>Agent log lines go to the Windows Event Log (Application log, source <see cref="EventLogSource"/>; design §10.5):
/// Information and above from the agent, Warning and above from the framework.</item>
/// </list>
/// </summary>
public static class WindowsServiceHosting
{
    /// <summary>The service name a new install registers (<c>install-agent.ps1</c>) and the virtual account's name.</summary>
    public const string CurrentServiceName = "mirbadllm-agent";

    /// <summary>
    /// The service name of this install: <see cref="CurrentServiceName"/>, or <c>spark-serve-agent</c> for an install made
    /// before the rename. Updates keep such an install under its old name, because its virtual account's SID, and so
    /// every folder ACL it holds, derives from that name. Told apart by the install folder the executable runs from.
    /// </summary>
    public static string ServiceName { get; } = IsLegacyInstall(AppContext.BaseDirectory) ? LegacyNames.AgentName : CurrentServiceName;

    /// <summary>
    /// The Event Log source (Application log). The installer registers it (<c>New-EventLog -LogName Application -Source
    /// mirbadllm-agent</c>): the virtual account cannot create a source, and an unregistered source turns the provider off
    /// after one "Unable to log" entry under the <c>Application</c> source.
    /// </summary>
    public static string EventLogSource => ServiceName;

    /// <summary>True when <paramref name="baseDirectory"/> lies in a pre-rename install folder (<c>…\spark-serve-agent\…</c>).</summary>
    public static bool IsLegacyInstall(string baseDirectory) =>
        baseDirectory.Replace('\\', '/').Contains("/" + LegacyNames.AgentName + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The Windows log the agent writes to.</summary>
    public const string EventLogName = "Application";

    /// <summary><c>SERVICE_CONTROL_PRESHUTDOWN</c>.</summary>
    public const int PreshutdownControl = 0x0000000F;

    /// <summary><c>SERVICE_ACCEPT_PRESHUTDOWN</c>.</summary>
    public const int AcceptPreshutdown = 0x00000100;

    /// <summary>How often a stopping service renews its wait hint (the hint itself is twice this).</summary>
    public static readonly TimeSpan WaitHintInterval = TimeSpan.FromSeconds(5);

    /// <summary>True when this process was started by the SCM (false on every other OS).</summary>
    public static bool IsWindowsService => WindowsServiceHelpers.IsWindowsService();

    /// <summary>
    /// Registers the SCM lifetime and the Event Log when the process runs as a Windows service; does nothing otherwise
    /// (<c>AddWindowsService</c> is itself a no-op outside the SCM).
    /// </summary>
    public static void Configure(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!OperatingSystem.IsWindows() || !IsWindowsService)
        {
            return;
        }

        builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);
        AddAgentServiceLifetime(builder.Services);
    }

    /// <summary>
    /// Adds the Event Log to the loggers <c>Program.Main</c> uses before the host exists (start-up failures, the key sweep,
    /// bootstrap enrollment), when the process runs as a Windows service: under the SCM nobody reads stderr.
    /// </summary>
    public static void AddStartupEventLog(ILoggingBuilder logging)
    {
        ArgumentNullException.ThrowIfNull(logging);
        if (!OperatingSystem.IsWindows() || !IsWindowsService)
        {
            return;
        }

        logging.AddEventLog(ConfigureEventLog);
        AddEventLogFilters(logging);
    }

    /// <summary>
    /// The registrations <see cref="Configure"/> adds on top of <c>AddWindowsService</c>, which already registered the
    /// Event Log provider and its own lifetime (replaced here). Tests call this directly to compose the service host
    /// in-process, without the SCM.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static void AddAgentServiceLifetime(IServiceCollection services)
    {
        services.Configure<WindowsServiceLifetimeOptions>(o => o.ServiceName = ServiceName);
        services.Configure<EventLogSettings>(ConfigureEventLog);
        services.AddLogging(AddEventLogFilters);
        // Both the host's console lifetime and the package's lifetime are registered by now: keep only the agent's.
        services.RemoveAll<IHostLifetime>();
        services.AddSingleton<IHostLifetime, AgentServiceLifetime>();
    }

    [SupportedOSPlatform("windows")]
    private static void ConfigureEventLog(EventLogSettings settings)
    {
        settings.SourceName = EventLogSource;
        settings.LogName = EventLogName;
    }

    [SupportedOSPlatform("windows")]
    private static void AddEventLogFilters(ILoggingBuilder logging)
    {
        logging.AddFilter<EventLogLoggerProvider>(null, LogLevel.Information);
        logging.AddFilter<EventLogLoggerProvider>("Microsoft", LogLevel.Warning);
        logging.AddFilter<EventLogLoggerProvider>("System", LogLevel.Warning);
        logging.AddFilter<EventLogLoggerProvider>("Microsoft.Hosting.Lifetime", LogLevel.Information);
    }
}

/// <summary>
/// The package's <see cref="WindowsServiceLifetime"/> plus what the agent needs from the SCM (design-p3p4 §A.4.1):
/// <list type="bullet">
/// <item><b>Stop</b>: <c>ServiceBase</c> reports <c>SERVICE_STOP_PENDING</c> with a zero wait hint; this lifetime renews
/// the hint (<see cref="ServiceBase.RequestAdditionalTime(TimeSpan)"/>) every <see cref="WindowsServiceHosting.WaitHintInterval"/>
/// while the host drains (the base waits up to <see cref="HostOptions.ShutdownTimeout"/> for <c>ApplicationStopped</c>),
/// so a drain of in-flight operations is never mistaken for a hung service.</item>
/// <item><b>Pre-shutdown</b>: <c>ServiceBase</c> exposes no switch for <c>SERVICE_ACCEPT_PRESHUTDOWN</c>, so the bit is
/// set in its accepted-controls field (checked by a test against the framework in use); the control
/// (<c>SERVICE_CONTROL_PRESHUTDOWN</c>, delivered as a custom command) runs the same stop path, with wait hints, before
/// the system shuts down (the SCM then sends no <c>SHUTDOWN</c>). If the field is ever missing the service still stops
/// on <c>SHUTDOWN</c>, without hints (the SCM accepts none outside a pending state).</item>
/// <item><see cref="ServiceBase.AutoLog"/> is off: <c>ServiceBase</c>'s own entries would go to a source named after the
/// service in the Application log through a second path; the agent logs through the Event Log provider instead.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class AgentServiceLifetime : WindowsServiceLifetime
{
    private readonly ILogger _logger;
    private readonly TimeSpan _shutdownTimeout;
    private readonly TimeSpan _interval;
    private readonly Action<TimeSpan> _reportWaitHint;

    /// <summary>The lifetime DI builds under the SCM.</summary>
    public AgentServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor)
        : this(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor, null, WindowsServiceHosting.WaitHintInterval)
    {
    }

    /// <summary>Tests: a wait-hint sink in place of <see cref="ServiceBase.RequestAdditionalTime(TimeSpan)"/> and a shorter interval.</summary>
    internal AgentServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor,
        Action<TimeSpan>? reportWaitHint,
        TimeSpan waitHintInterval)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(optionsAccessor);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(waitHintInterval, TimeSpan.Zero);
        _logger = loggerFactory.CreateLogger<AgentServiceLifetime>();
        _shutdownTimeout = optionsAccessor.Value.ShutdownTimeout;
        _interval = waitHintInterval;
        _reportWaitHint = reportWaitHint ?? RequestAdditionalTime;
        AutoLog = false;
        CanStop = true;
        CanShutdown = true;
        PreshutdownAccepted = TryAcceptPreshutdown(this);
    }

    /// <summary>True when <c>SERVICE_ACCEPT_PRESHUTDOWN</c> is among the controls this service accepts.</summary>
    public bool PreshutdownAccepted { get; }

    /// <summary>The wait hint each renewal reports: twice <see cref="WindowsServiceHosting.WaitHintInterval"/>.</summary>
    internal TimeSpan RenewedWaitHint => _interval * 2;

    /// <summary>The first wait hint of a stop: the whole drain (<see cref="HostOptions.ShutdownTimeout"/>) plus one interval.</summary>
    internal TimeSpan InitialWaitHint => _shutdownTimeout + _interval;

    /// <summary>The accepted-controls field of <see cref="ServiceBase"/> (null when the framework no longer has it).</summary>
    internal static FieldInfo? AcceptedCommandsField { get; } =
        typeof(ServiceBase).GetField("_acceptedCommands", BindingFlags.Instance | BindingFlags.NonPublic) is { } field && field.FieldType == typeof(int)
            ? field
            : null;

    /// <inheritdoc />
    protected override void OnStart(string[] args)
    {
        base.OnStart(args);
        LogRunning(_logger, ServiceName, PreshutdownAccepted);
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
        LogStopping(_logger, _shutdownTimeout.TotalSeconds);
        using (new WaitHints(this))
        {
            base.OnStop();
        }
    }

    /// <inheritdoc />
    protected override void OnCustomCommand(int command)
    {
        if (command == WindowsServiceHosting.PreshutdownControl)
        {
            // The same path as a stop control: STOP_PENDING, wait hints, drain, STOPPED.
            LogPreshutdown(_logger);
            Stop();
            return;
        }

        base.OnCustomCommand(command);
    }

    /// <inheritdoc />
    protected override void OnShutdown()
    {
        // Reached only when pre-shutdown is not accepted (see the class remarks); no wait hint outside a pending state.
        LogShutdown(_logger);
        base.OnShutdown();
    }

    private static bool TryAcceptPreshutdown(ServiceBase service)
    {
        if (AcceptedCommandsField is not { } field)
        {
            return false;
        }

        int accepted = (int)field.GetValue(service)!;
        field.SetValue(service, accepted | WindowsServiceHosting.AcceptPreshutdown);
        return true;
    }

    private void Report(TimeSpan hint)
    {
        try
        {
            _reportWaitHint(hint);
        }
        catch (InvalidOperationException ex)
        {
            // Not in a pending state (the SCM has already been told STOPPED, or this is not a service): nothing to renew.
            LogWaitHintRefused(_logger, ex.Message);
        }
    }

    /// <summary>Reports the initial hint now and renews it every interval until disposed; never reports after disposal.</summary>
    private sealed class WaitHints : IDisposable
    {
        private readonly AgentServiceLifetime _owner;
        private readonly Lock _gate = new();
        private readonly Timer _timer;
        private bool _done;

        public WaitHints(AgentServiceLifetime owner)
        {
            _owner = owner;
            owner.Report(owner.InitialWaitHint);
            _timer = new Timer(_ => Renew(), null, owner._interval, owner._interval);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _done = true;
            }

            _timer.Dispose();
        }

        private void Renew()
        {
            lock (_gate)
            {
                if (!_done)
                {
                    _owner.Report(_owner.RenewedWaitHint);
                }
            }
        }
    }

    [LoggerMessage(EventId = 2670, Level = LogLevel.Information,
        Message = "Running as the Windows service {ServiceName} (pre-shutdown accepted: {Preshutdown})")]
    private static partial void LogRunning(ILogger logger, string serviceName, bool preshutdown);

    [LoggerMessage(EventId = 2671, Level = LogLevel.Information,
        Message = "The SCM asked the service to stop; draining within {Seconds:0} s and renewing the wait hint")]
    private static partial void LogStopping(ILogger logger, double seconds);

    [LoggerMessage(EventId = 2672, Level = LogLevel.Information, Message = "The system is shutting down (pre-shutdown); stopping the service")]
    private static partial void LogPreshutdown(ILogger logger);

    [LoggerMessage(EventId = 2673, Level = LogLevel.Warning,
        Message = "The system is shutting down without a pre-shutdown notification; stopping without wait hints")]
    private static partial void LogShutdown(ILogger logger);

    [LoggerMessage(EventId = 2674, Level = LogLevel.Debug, Message = "Wait hint not reported: {Reason}")]
    private static partial void LogWaitHintRefused(ILogger logger, string reason);
}
