using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Enrollment;
using MirbadLLM.Agent.Fakes;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Agent.Pki;
using MirbadLLM.Agent.Recovery;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Runtimes.Process;
using MirbadLLM.Core;

namespace MirbadLLM.Agent;

/// <summary>
/// Entry point of <c>mirbadllm-agent</c> (design §2.5, §3.2, §4.4 item 5; MN-026):
/// <code>
/// mirbadllm-agent [--config &lt;agent.json&gt;] [--dev-fake-runtime]     run the agent (fake models: Development only, MN-042)
/// mirbadllm-agent join --enroll-url … --token … --ca-hash … --node-id …   enroll once and exit (JoinCommand)
/// </code>
/// Start order (W3 review MN-094): (0) on Windows under the service account, the sweep of CNG key files an unclean exit
/// left (MN-215, D122), before any credential is loaded; (1) bootstrap enrollment when configured, before any PKI service
/// exists (it removes a previous identity's rotation state); the agent refuses to start without certificates otherwise;
/// (2) credentials and PKI (rotation recovery, listener certificate); (3) a bounded wait for Docker (D56), skipped when
/// <c>Agent:Docker:Enabled</c> is false; (4) reconciliation with every recovery handler and canceller registered;
/// (5) routes, then the listener opens. Stopping drains running operations within <see cref="AgentOptions.ShutdownTimeout"/>.
/// <para>Under the Windows SCM (design-p3p4 §A.4.1, MN-215) <see cref="WindowsServiceHosting"/> reports RUNNING when the
/// host starts, after <c>CreateAsync</c> and before any hosted service: everything before <c>app.RunAsync</c> counts
/// against the SCM's 30 s and stays short (no Docker wait on a Windows-configured agent); long start work belongs in
/// <c>BackgroundService.ExecuteAsync</c>, never in a blocking <c>IHostedService.StartAsync</c> (it keeps the listener
/// closed). Outside the SCM nothing changes.</para>
/// <para>An explicit <c>Main</c> in a namespace (not top-level statements) keeps this assembly's <c>Program</c> out of
/// the global namespace, where the master's <c>Program</c> lives for <c>WebApplicationFactory&lt;Program&gt;</c>.</para>
/// </summary>
public static partial class Program
{
    /// <summary>Exit code: stopped normally.</summary>
    public const int ExitOk = 0;

    /// <summary>Exit code: the agent could not start (not enrolled, wrong identity, unusable state).</summary>
    public const int ExitStartupFailed = 1;

    /// <summary>Exit code: bad command line or configuration.</summary>
    public const int ExitConfiguration = 2;

    /// <summary>Usage text.</summary>
    public const string Usage =
        "usage: mirbadllm-agent [--config <agent.json>] [--dev-fake-runtime]\n" +
        "       mirbadllm-agent join --enroll-url <https://host:8443> --token <token> --ca-hash <sha256:hash> --node-id <id> [...]\n" +
        $"       the configuration path may also come from {AgentOptions.ConfigPathEnvironmentVariable}.";

    /// <summary>Runs the agent (or <c>join</c>) and returns the process exit code.</summary>
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        using ILoggerFactory consoleLoggers = LoggerFactory.Create(logging =>
        {
            logging.AddSimpleConsole(o => o.SingleLine = true);
            WindowsServiceHosting.AddStartupEventLog(logging);
        });
        if (args.Length > 0 && args[0] == MirbadLLM.Agent.Updates.WindowsUpdateHelper.Verb)
        {
            return await MirbadLLM.Agent.Updates.WindowsUpdateHelper.RunAsync(args[1..]).ConfigureAwait(false);
        }

        if (args.Length > 0 && args[0] == JoinCommand.Name)
        {
            return await JoinCommand.RunAsync(args[1..], Console.Out, Console.Error, consoleLoggers).ConfigureAwait(false);
        }

        if (!TryParseArguments(args, out string? configPath, out string[] hostArgs, out string? usageError))
        {
            await Console.Error.WriteLineAsync(usageError).ConfigureAwait(false);
            await Console.Error.WriteLineAsync(Usage).ConfigureAwait(false);
            return ExitConfiguration;
        }

        string path = Path.GetFullPath(configPath ?? Environment.GetEnvironmentVariable(AgentOptions.ConfigPathEnvironmentVariable)
            ?? Environment.GetEnvironmentVariable(LegacyNames.AgentConfigVariable) ?? DefaultConfigPath());
        if (!File.Exists(path))
        {
            await Console.Error.WriteLineAsync($"mirbadllm-agent: configuration file '{path}' not found.").ConfigureAwait(false);
            return ExitConfiguration;
        }

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(hostArgs);
        builder.Configuration.AddJsonFile(path, optional: false, reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables(LegacyNames.AgentEnvironmentPrefix); // pre-rename variables; the current ones win
        builder.Configuration.AddEnvironmentVariables(AgentOptions.EnvironmentPrefix);
        AgentOptions options;
        try
        {
            options = AgentOptions.FromConfiguration(builder.Configuration);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync($"mirbadllm-agent: {path}: {ex.Message}").ConfigureAwait(false);
            return ExitConfiguration;
        }

        // Until the host runs (it handles SIGTERM/Ctrl+C itself), a stop request cancels the startup phase: the bootstrap
        // wait, the Docker wait and reconciliation.
        using var startup = new CancellationTokenSource();
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            startup.Cancel();
        }

        Console.CancelKeyPress += OnCancel;
        PosixSignalRegistration? sigterm = TryRegisterSigterm(startup);
        WebApplication app;
        try
        {
            app = await CreateAsync(builder, options, consoleLoggers, configureServices: null, startup.Token).ConfigureAwait(false);
        }
        catch (AgentStartupException ex)
        {
            await Console.Error.WriteLineAsync($"mirbadllm-agent: {ex.Message}").ConfigureAwait(false);
            if (WindowsServiceHosting.IsWindowsService)
            {
                // Under the SCM nobody reads stderr: the reason goes to the Event Log.
                LogStartupFailed(consoleLoggers.CreateLogger("MirbadLLM.Agent.Startup"), ex.Message);
            }

            return ExitStartupFailed;
        }
        catch (OperationCanceledException) when (startup.IsCancellationRequested)
        {
            return ExitOk;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
            sigterm?.Dispose();
        }

        await using (app.ConfigureAwait(false))
        {
            await app.RunAsync().ConfigureAwait(false);
        }

        return ExitOk;
    }

    /// <summary>
    /// Builds the agent on <paramref name="builder"/> and brings it to the point where only the listener is missing:
    /// enrollment (bootstrap), credentials, Docker, reconciliation, routes. The caller starts it (<c>RunAsync</c> /
    /// <c>StartAsync</c>), which opens the listener. <paramref name="configureServices"/> runs before the packages are
    /// registered, so its registrations replace the packages' <c>TryAdd</c> defaults (tests, the dev setup).
    /// Throws <see cref="AgentStartupException"/> when the agent must not start.
    /// </summary>
    internal static Task<WebApplication> CreateAsync(
        WebApplicationBuilder builder,
        AgentOptions options,
        ILoggerFactory? startupLoggers = null,
        Action<IServiceCollection>? configureServices = null,
        CancellationToken ct = default) =>
        CreateAsync(builder, options, keySweep: null, startupLoggers, configureServices, ct);

    /// <summary>
    /// <see cref="CreateAsync(WebApplicationBuilder, AgentOptions, ILoggerFactory?, Action{IServiceCollection}?, CancellationToken)"/>
    /// with <paramref name="keySweep"/> in place of this process's key sweep (tests inject an identity and a profile
    /// folder; null = <see cref="ServiceProfileKeySweep.ForCurrentProcess"/>).
    /// </summary>
    internal static async Task<WebApplication> CreateAsync(
        WebApplicationBuilder builder,
        AgentOptions options,
        ServiceProfileKeySweep? keySweep,
        ILoggerFactory? startupLoggers,
        Action<IServiceCollection>? configureServices,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        // A process node needs Agent:CacheRoot (a native launch's argv may name it as {cache}). AddMirbadLLMAgent refuses
        // without it, but as an ArgumentException from a composition call Main does not catch: the service would die with an
        // unhandled exception and nothing in the Event Log, which under the SCM looks like error 1053 (MN-234). Named here
        // instead, so Main prints the reason and WindowsServiceHosting logs it. AgentOptions.Validate cannot carry the rule:
        // the frozen P3ContractTests bind a Docker-off agent.json without a cache root.
        if (!options.Docker.Enabled && string.IsNullOrWhiteSpace(options.CacheRoot))
        {
            throw new AgentStartupException(
                "Agent:CacheRoot is required on a node that runs native processes (Agent:Docker:Enabled = false): a launch's " +
                "argv may name it as {cache}.");
        }

        // The development fake runtime (MN-042) refuses to start outside Development or on a non-loopback listener.
        Action<IServiceCollection>? devFakeRuntime = DevFakeRuntime.Configure(builder.Configuration, builder.Environment, options);
        startupLoggers ??= NullLoggerFactory.Instance;
        AgentHostOptions host = options.ToHostOptions();
        ILogger startupLog = startupLoggers.CreateLogger("MirbadLLM.Agent.Startup");

        // (0) Before any credential is loaded (FileAgentCredentials, TightenKeyPermissions, the PKI services): delete the CNG
        // key files an unclean exit left in the service profile; a no-op unless this is NT SERVICE\mirbadllm-agent (D122).
        (keySweep ?? ServiceProfileKeySweep.ForCurrentProcess(startupLog)).Run();
        Directory.CreateDirectory(options.StateDirectory);

        // (1) Enrollment first: RotatingAgentCredentials reads pki-rotation.json when it is built, and a bootstrap
        // enrollment deletes the previous identity's rotation state (W3 review MN-094 item 1, D48).
        await EnsureEnrolledAsync(options, host, startupLoggers, ct).ConfigureAwait(false);
        TightenKeyPermissions(options.StateDirectory, startupLog);

        var boot = new AgentBootInfo(TimeProvider.System, options.AgentVersion);
        devFakeRuntime?.Invoke(builder.Services);
        configureServices?.Invoke(builder.Services);
        AgentHostBuilder.Configure(builder, host);
        builder.Services.AddMirbadLLMAgent(options, boot);
        WindowsServiceHosting.Configure(builder);
        WebApplication app = builder.Build();
        try
        {
            ILogger log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MirbadLLM.Agent.Startup");

            // (2) Credentials and PKI: rotation recovery runs here; a half-written identity loads as "not enrolled".
            IAgentCredentials credentials = app.Services.GetRequiredService<IAgentCredentials>();
            if (!credentials.IsEnrolled)
            {
                throw new AgentStartupException(
                    $"The agent is not enrolled: '{options.StateDirectory}' holds no valid agent.crt, agent.key, ca.crt and node-uid.");
            }

            if (!string.Equals(credentials.NodeId, options.NodeId, StringComparison.Ordinal))
            {
                throw new AgentStartupException(
                    $"The agent certificate is for node '{credentials.NodeId}', but agent.json says '{options.NodeId}'.");
            }

            app.Services.GetRequiredService<AgentListenerTls>().Refresh();
            _ = app.Services.GetRequiredService<AgentPkiService>();
            LogIdentity(log, credentials.NodeId, credentials.NodeUid, boot.BootId, boot.AgentVersion);

            // (3) Docker, bounded (D56: launches that need it fail docker_unavailable if it never answers). A node without
            // Docker (Agent:Docker:Enabled = false, the Windows PC) does not wait and never resolves the probe: under the
            // SCM this phase counts against its 30 s (design-p3p4 §A.4.1, §A.4.3).
            await app.Services.GetRequiredService<DockerAvailability>()
                .WaitForDockerAsync(app.Services, options.DockerStartupTimeout, ct).ConfigureAwait(false);

            // (3b) Supervised processes (design-p3p4 §A.4.1, D121/D125), after MN-215's key sweep and before the
            // reservations are rebuilt, so a rebuilt reservation sees the adopted instance: each record is re-opened by
            // OpenProcess (PID + start time + image path) and the orphan sweep ends what is provably ours and
            // unsupervised. Fast by design — a process listing and its command lines — and the Ready probes of the
            // adopted instances run in the driver's own background supervision, never here.
            IRuntimeDriver[] drivers = [.. app.Services.GetServices<IRuntimeDriver>()];
            if (drivers.OfType<ProcessRuntimeDriver>().FirstOrDefault() is { } processes)
            {
                ProcessRecoveryReport recovered = await processes.RecoverAsync(ct).ConfigureAwait(false);
                LogProcessesRecovered(log, recovered.Adopted.Count, recovered.Gone.Count, recovered.OrphansKilled.Count, recovered.Foreign.Count);
            }
            else if (!options.Docker.Enabled && drivers.Length == 0)
            {
                // Docker off and no process delivery either: nothing registered an IProcessHost and an IInstanceKeyProtector,
                // which off Windows the composition cannot supply itself (design-p3p4 §A.4.3, MN-234's ruling). The agent is
                // useful — it enrolls, reports, serves telemetry, downloads and host services — but it can start no model at
                // all, and the capability report shows an empty runtime list rather than a reason. Said once, here.
                LogNoRuntimeDelivery(log, DockerAvailability.ConfigurationKey);
            }

            // (4) Reconciliation, before the listener and the hosted services (watcher, sampler) start. Every recovery
            // handler (host services, storage, PKI) and canceller (launch, storage) is registered by AddMirbadLLMAgent.
            ReconciliationReport report = await app.Services.GetRequiredService<AgentReconciler>().RunAsync(ct).ConfigureAwait(false);
            LogReconciled(log, report.Operations.Count, report.ReservationsRebuilt, report.Adopted.Count);

            // (5) The feature list the request pipeline gates on (fixed for the life of the process), then the routes; the
            // listener opens when the caller starts the application.
            _ = await app.Services.GetRequiredService<AgentCapabilityReporter>().GetFeaturesAsync(ct).ConfigureAwait(false);
            app.MapMirbadLLMAgent();
            return app;
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Splits <c>--config &lt;path&gt;</c> from the arguments passed on to the host.</summary>
    internal static bool TryParseArguments(IReadOnlyList<string> args, out string? configPath, out string[] hostArgs, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        configPath = null;
        error = null;
        var rest = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] is "--config" or "-c")
            {
                if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    hostArgs = [];
                    error = $"{args[i]} needs a path.";
                    return false;
                }

                configPath = args[++i];
                continue;
            }

            if (args[i] == DevFakeRuntime.Switch)
            {
                rest.Add($"--{DevFakeRuntimeOptions.SectionName}:{nameof(DevFakeRuntimeOptions.Enabled)}=true");
                continue;
            }

            if (args[i] is "--help" or "-h")
            {
                hostArgs = [];
                error = "";
                return false;
            }

            rest.Add(args[i]);
        }

        hostArgs = [.. rest];
        return true;
    }

    /// <summary>How long an enrolled agent retries an unreachable master's enroll listener when a bootstrap bundle waits.</summary>
    internal static readonly TimeSpan EnrolledBootstrapWait = TimeSpan.FromMinutes(1);

    private static string DefaultConfigPath()
    {
        string path = DefaultConfigPath("mirbadllm-agent");
        string legacy = DefaultConfigPath(LegacyNames.AgentName);
        return !File.Exists(path) && File.Exists(legacy) ? legacy : path; // an install made before the rename
    }

    private static string DefaultConfigPath(string name) => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), name, "agent.json")
        : OperatingSystem.IsMacOS() ? $"/Library/Application Support/{name}/agent.json" : $"/etc/{name}/agent.json";

    /// <summary>
    /// Bootstrap mode enrolls from the master's bundle (replacing an older identity, design §3.2) and waits for the bundle
    /// while the agent has no identity; otherwise the agent must already be enrolled (<c>join</c>).
    /// </summary>
    private static async Task EnsureEnrolledAsync(AgentOptions options, AgentHostOptions host, ILoggerFactory loggers, CancellationToken ct)
    {
        ILogger log = loggers.CreateLogger("MirbadLLM.Agent.Startup");
        bool enrolled = new FileAgentCredentials(host, loggers.CreateLogger<FileAgentCredentials>()).IsEnrolled;
        if (options.BootstrapDirectory is not { Length: > 0 } bundleDirectory)
        {
            if (!enrolled)
            {
                throw new AgentStartupException(
                    $"The agent is not enrolled: '{options.StateDirectory}' holds no valid agent.crt, agent.key, ca.crt and node-uid. " +
                    "Run 'mirbadllm-agent join ...' with the command shown by \"Add node\", or set Agent:BootstrapDirectory on the bootstrap node.");
            }

            return;
        }

        var enrollment = new AgentEnrollment(TimeProvider.System, loggers.CreateLogger<AgentEnrollment>());
        var parameters = new BootstrapParameters
        {
            StateDirectory = options.StateDirectory,
            BundleDirectory = bundleDirectory,
            ExpectedNodeId = options.NodeId,
            Addresses = [.. options.Addresses],
        };
        // An agent that still has an identity waits only briefly for an unreachable master before it serves with that
        // identity (the bundle stays for the next start); one without an identity has nothing to serve with (W4 review).
        DateTimeOffset deadline = DateTimeOffset.UtcNow + (enrolled && options.BootstrapWait > EnrolledBootstrapWait ? EnrolledBootstrapWait : options.BootstrapWait);
        bool announced = false;
        while (true)
        {
            try
            {
                if (await enrollment.BootstrapAsync(parameters, ct).ConfigureAwait(false) is { } outcome)
                {
                    LogBootstrapped(log, outcome.NodeId, outcome.NodeUid, outcome.CertificateSpkiSha256);
                    return;
                }

                if (enrolled)
                {
                    return;
                }
            }
            catch (AgentEnrollmentException ex) when (ex.Failure is EnrollmentFailure.Unreachable or EnrollmentFailure.RateLimited
                && DateTimeOffset.UtcNow < deadline)
            {
                // The master writes the bundle before its enroll listener answers: try again shortly.
                LogBootstrapRetry(log, ex.Failure, ex.Message);
            }
            catch (AgentEnrollmentException ex)
            {
                if (enrolled)
                {
                    // The previous identity still loads; keep serving with it and leave the bundle for the operator.
                    LogBootstrapFailedKeepingIdentity(log, ex.Failure, ex.Message);
                    return;
                }

                throw new AgentStartupException($"Bootstrap enrollment failed ({ex.Failure}): {ex.Message}", ex);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new AgentStartupException(
                    $"The agent is not enrolled and no bootstrap bundle ({BootstrapBundle.FileName}) appeared in '{bundleDirectory}' within {options.BootstrapWait}.");
            }

            if (!announced)
            {
                LogWaitingForBundle(log, bundleDirectory);
                announced = true;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Private keys must be readable by the agent only (W1 review MN-090): on Unix a key with group or other bits is
    /// tightened to 0600 and logged. Windows ACLs belong to the P4 packaging.
    /// </summary>
    private static void TightenKeyPermissions(string stateDirectory, ILogger log)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const UnixFileMode Loose = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        foreach (string name in new[] { FileAgentCredentials.KeyFileName, AgentRotationStore.PendingKeyFileName })
        {
            string path = Path.Combine(stateDirectory, name);
            try
            {
                if (File.Exists(path) && (File.GetUnixFileMode(path) & Loose) != 0)
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    LogKeyTightened(log, path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogKeyNotTightened(log, ex, path);
            }
        }
    }

    private static PosixSignalRegistration? TryRegisterSigterm(CancellationTokenSource startup)
    {
        try
        {
            return PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                startup.Cancel();
            });
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    [LoggerMessage(EventId = 2620, Level = LogLevel.Information, Message = "Agent {NodeId} (uid {NodeUid}) boot {BootId}, version {Version}")]
    private static partial void LogIdentity(ILogger logger, string nodeId, string nodeUid, string bootId, string version);

    [LoggerMessage(EventId = 2621, Level = LogLevel.Information,
        Message = "Reconciled {Operations} open operation(s), rebuilt {Reservations} reservation(s), adopted {Adopted} instance(s); opening the listener")]
    private static partial void LogReconciled(ILogger logger, int operations, int reservations, int adopted);

    [LoggerMessage(EventId = 2622, Level = LogLevel.Information, Message = "Enrolled node {NodeId} (uid {NodeUid}) from the bootstrap bundle, key {Spki}")]
    private static partial void LogBootstrapped(ILogger logger, string nodeId, string nodeUid, string spki);

    [LoggerMessage(EventId = 2623, Level = LogLevel.Information, Message = "Not enrolled yet: waiting for the master's bootstrap bundle in {Directory}")]
    private static partial void LogWaitingForBundle(ILogger logger, string directory);

    [LoggerMessage(EventId = 2624, Level = LogLevel.Warning, Message = "Bootstrap enrollment failed ({Failure}): {Message}; retrying")]
    private static partial void LogBootstrapRetry(ILogger logger, EnrollmentFailure failure, string message);

    [LoggerMessage(EventId = 2625, Level = LogLevel.Error,
        Message = "Bootstrap enrollment failed ({Failure}): {Message}; starting with the identity already in the state directory")]
    private static partial void LogBootstrapFailedKeepingIdentity(ILogger logger, EnrollmentFailure failure, string message);

    [LoggerMessage(EventId = 2626, Level = LogLevel.Warning, Message = "{Path} was readable by group or others; tightened to 0600")]
    private static partial void LogKeyTightened(ILogger logger, string path);

    [LoggerMessage(EventId = 2627, Level = LogLevel.Warning, Message = "Could not check or tighten the permissions of {Path}")]
    private static partial void LogKeyNotTightened(ILogger logger, Exception exception, string path);

    [LoggerMessage(EventId = 2628, Level = LogLevel.Error, Message = "The agent cannot start: {Reason}")]
    private static partial void LogStartupFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 2629, Level = LogLevel.Information,
        Message = "Adopted {Adopted} supervised process(es), dropped {Gone} record(s) whose process is gone, ended {Killed} orphan(s), left {Foreign} foreign process(es) alone")]
    private static partial void LogProcessesRecovered(ILogger logger, int adopted, int gone, int killed, int foreign);

    [LoggerMessage(EventId = 2684, Level = LogLevel.Warning,
        Message = "{Key} is false and this node has no process delivery (no IProcessHost and IInstanceKeyProtector, which only Windows registers by itself): the agent runs but can start no model, and reports no runtime at all")]
    private static partial void LogNoRuntimeDelivery(ILogger logger, string key);
}

/// <summary>The agent must not start (not enrolled, another node's certificate, failed bootstrap); the message says why.</summary>
public sealed class AgentStartupException : Exception
{
    /// <summary>Creates the exception.</summary>
    public AgentStartupException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with its cause.</summary>
    public AgentStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Unused; present for the standard exception constructors.</summary>
    public AgentStartupException()
        : base("The agent cannot start.")
    {
    }
}
