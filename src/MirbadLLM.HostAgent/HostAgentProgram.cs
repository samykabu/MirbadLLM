using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MirbadLLM.HostAgent;

/// <summary>
/// mirbadllm-hostd: runs as root under systemd and listens only on a Unix socket (no TCP). Access control
/// is the socket's filesystem permissions (directory root:docker 0750, socket 0660) — the same group that
/// already controls Docker — plus the compiled-in <see cref="DenyList"/> (extendable by a root-owned
/// <see cref="DenyList.DefaultExtraPath"/>) and the admin-managed <see cref="WhitelistStore"/>
/// (<c>$STATE_DIRECTORY/whitelist.json</c>, seeded from <see cref="HostdConfig.DefaultPath"/>).
/// <para>On macOS (docs/macos/implementation-plan.md M10) it runs as the root LaunchDaemon
/// <c>com.mirbadllm.hostd</c> and drives launchd (<see cref="LaunchdManager"/>). Its files live in
/// <see cref="HostdConfig.MacDirectory"/> (root-owned, apart from the agent's directory that <c>_mirbadllm</c> writes),
/// and it creates the socket's directory itself (root:<c>_mirbadllm</c> 0750, socket 0660), because nothing like
/// systemd's <c>RuntimeDirectory=</c> does it there. No backup mount on a Mac: backups live on the master.</para>
/// </summary>
internal static class HostAgentProgram
{
    public static async Task<int> Main(string[] args)
    {
        ServiceFlavor flavor = HostdConfig.HostFlavor;
        bool mac = flavor == ServiceFlavor.Launchd;
        string configPath = Environment.GetEnvironmentVariable("MIRBADLLM_HOSTD_CONFIG") ?? (mac ? HostdConfig.MacDefaultPath : HostdConfig.DefaultPath);
        // On a Mac everything lives next to hostd.json, so a host agent installed before the rename (its plist names the
        // pre-rename folder) keeps its deny list and whitelist.
        string macDirectory = Path.GetDirectoryName(configPath) ?? HostdConfig.MacDirectory;
        string denyPath = Environment.GetEnvironmentVariable("MIRBADLLM_HOSTD_DENY") ?? (mac ? Path.Combine(macDirectory, "hostd-deny.json") : DenyList.DefaultExtraPath);
        // systemd sets STATE_DIRECTORY from StateDirectory= (the only writable path under ProtectSystem=strict).
        string stateDir = Environment.GetEnvironmentVariable("STATE_DIRECTORY")?.Split(':')[0] is { Length: > 0 } sd
            ? sd
            : mac ? Path.Combine(macDirectory, "state") : WhitelistStore.DefaultDirectory;
        string whitelistPath = Path.Combine(stateDir, WhitelistStore.FileName);

        HostdConfig config;
        DenyList deny;
        try
        {
            if (await RefuseInsecureAsync(configPath, requireRootOwner: false).ConfigureAwait(false) is { } configProblem)
            {
                await Console.Error.WriteLineAsync(configProblem).ConfigureAwait(false);
                return 2;
            }

            config = HostdConfig.Parse(await File.ReadAllTextAsync(configPath).ConfigureAwait(false), flavor);

            IReadOnlyList<string> extra = [];
            if (File.Exists(denyPath))
            {
                if (await RefuseInsecureAsync(denyPath, requireRootOwner: true).ConfigureAwait(false) is { } denyProblem)
                {
                    await Console.Error.WriteLineAsync(denyProblem).ConfigureAwait(false);
                    return 2;
                }

                extra = DenyList.ParseExtra(await File.ReadAllTextAsync(denyPath).ConfigureAwait(false));
            }

            deny = new DenyList(extra, flavor);

            if (File.Exists(whitelistPath) && await RefuseInsecureAsync(whitelistPath, requireRootOwner: true).ConfigureAwait(false) is { } wlProblem)
            {
                await Console.Error.WriteLineAsync(wlProblem).ConfigureAwait(false);
                return 2;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
        {
            await Console.Error.WriteLineAsync($"Could not load the configuration: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddSystemdConsole();
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

        if (OperatingSystem.IsMacOS())
        {
            if (await PrepareMacSocketDirectoryAsync(config).ConfigureAwait(false) is { } socketProblem)
            {
                await Console.Error.WriteLineAsync(socketProblem).ConfigureAwait(false);
                return 2;
            }

            Directory.CreateDirectory(stateDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // A stale socket from an unclean stop would make the bind fail.
        if (File.Exists(config.Socket))
        {
            File.Delete(config.Socket);
        }

        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenUnixSocket(config.Socket));

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(deny);
        builder.Services.AddSingleton(sp => WhitelistStore.Open(whitelistPath, config, deny, sp.GetRequiredService<ILogger<WhitelistStore>>()));
        builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
        builder.Services.AddSingleton<ICgroupReader, ProcCgroupReader>();
        builder.Services.AddSingleton<GpuProcessInventory>();
        if (mac)
        {
            builder.Services.AddSingleton<LaunchdPaths>();
            builder.Services.AddSingleton<IUnitManager, LaunchdManager>();
        }
        else
        {
            builder.Services.AddSingleton<UnitManager>();
            builder.Services.AddSingleton<IUnitManager>(sp => sp.GetRequiredService<UnitManager>());
            builder.Services.AddSingleton(BackupMountPaths.ForStateDirectory(stateDir));
            builder.Services.AddSingleton<IBackupMountHost, LinuxBackupMountHost>();
            builder.Services.AddSingleton<BackupMountManager>();
        }

        var app = builder.Build();

        WhitelistStore whitelist;
        try
        {
            // Load (or seed) now, not on the first request: a broken whitelist must stop the agent at start.
            whitelist = app.Services.GetRequiredService<WhitelistStore>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
        {
            await Console.Error.WriteLineAsync($"Could not load {whitelistPath}: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        app.MapHostAgentApi();
        if (!mac)
        {
            app.MapBackupMountApi();
        }

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            // The unit's UMask already yields 0660; set it explicitly so a different umask can't widen it.
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(config.Socket,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            }

            // On a Mac the socket's group is the agent's (launchd has no Group= that would set it).
            if (mac && ChownAsync("root:" + config.SocketGroup, config.Socket).GetAwaiter().GetResult() is { } chownProblem)
            {
                Console.Error.WriteLine(chownProblem);
                app.Lifetime.StopApplication();
                return;
            }

            var logger = app.Services.GetRequiredService<ILogger<UnitManager>>();
            Log.Started(logger, config.Socket, whitelist.Units.Count, deny.Patterns.Count);
        });

        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// A file the agent trusts must not be writable by group or others (and, for the deny extension and the
    /// whitelist, must be owned by root). Returns the refusal message, or null when it is fine.
    /// </summary>
    private static async Task<string?> RefuseInsecureAsync(string path, bool requireRootOwner)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return null;
        }

        var mode = File.GetUnixFileMode(path);
        if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            return $"{path} is writable by group or others; refusing to start (chmod go-w it).";
        }

        if (requireRootOwner)
        {
            // GNU stat on Linux, BSD stat on macOS.
            IReadOnlyList<string> args = OperatingSystem.IsMacOS() ? ["-f", "%u", path] : ["-c", "%u", "--", path];
            var stat = await new SystemProcessRunner()
                .RunAsync("/usr/bin/stat", args, TimeSpan.FromSeconds(5), CancellationToken.None)
                .ConfigureAwait(false);
            if (!stat.Succeeded || !int.TryParse(stat.StdOut.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int uid) || uid != 0)
            {
                return $"{path} is not owned by root; refusing to start (chown root:{(OperatingSystem.IsMacOS() ? "wheel" : "root")} it).";
            }
        }

        return null;
    }

    /// <summary>
    /// macOS: makes the socket's directory root:<see cref="HostdConfig.SocketGroup"/> 0750 (creating it after a reboot
    /// emptied <c>/var/run</c>), so only root and the agent's group reach the socket. Refuses a directory that is a link.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static async Task<string?> PrepareMacSocketDirectoryAsync(HostdConfig config)
    {
        string directory = Path.GetDirectoryName(config.Socket) ?? "/";
        if (directory is "/" or "/var/run" or "/private/var/run" or "/tmp" or "/private/tmp")
        {
            return $"The socket {config.Socket} needs a directory of its own (e.g. /var/run/mirbadllm-hostd/hostd.sock).";
        }

        var existing = new DirectoryInfo(directory);
        if (existing.Exists && existing.LinkTarget is not null)
        {
            return $"{directory} is a link; refusing to start.";
        }

        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        return await ChownAsync("root:" + config.SocketGroup, directory).ConfigureAwait(false);
    }

    private static async Task<string?> ChownAsync(string owner, string path)
    {
        var run = await new SystemProcessRunner()
            .RunAsync("/usr/sbin/chown", [owner, path], TimeSpan.FromSeconds(5), CancellationToken.None)
            .ConfigureAwait(false);
        return run.Succeeded ? null : $"Could not chown {owner} {path}: {run.Describe()}";
    }
}
