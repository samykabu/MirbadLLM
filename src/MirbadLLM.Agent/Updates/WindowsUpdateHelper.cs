using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Hosting;

namespace MirbadLLM.Agent.Updates;

/// <summary>
/// <c>mirbadllm-agent.exe update-helper</c>: the Windows update helper (docs/multinode/agent-updates.md U4, step C).
/// <c>install-agent.ps1</c> registers it as the manual-start SYSTEM service <see cref="ServiceName"/> whose security
/// descriptor lets only the agent's virtual account start it; the agent starts it (<c>sc.exe start</c>) after handing an
/// order over. It runs <see cref="UpdateHelperCore"/> once (the checks), then the bundle's own
/// <c>install-agent.ps1 upgrade -Bundle -BundleSha256</c>, writes the result and stops.
/// </summary>
public static partial class WindowsUpdateHelper
{
    public const string Verb = "update-helper";
    /// <summary>The helper service: <c>{agent service}-updater</c> (an install made before the rename keeps its old names).</summary>
    public static string ServiceName => WindowsServiceHosting.ServiceName + "-updater";

    /// <summary>The spool the agent writes (its state directory).</summary>
    public static string Spool => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), WindowsServiceHosting.ServiceName, "state", "update");

    /// <summary>The trust folder only administrators and SYSTEM can write.</summary>
    public static string Trust => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), WindowsServiceHosting.ServiceName, "update");

    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            await Console.Error.WriteLineAsync("update-helper runs on Windows; Linux and macOS use agent-update-helper.sh.").ConfigureAwait(false);
            return 2;
        }

        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);
        builder.Services.AddHostedService<Worker>();
        using var host = builder.Build();
        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>The installer run: Windows PowerShell 5.1 (the installer's own requirement), as SYSTEM, without a profile.</summary>
    public static async Task<(int ExitCode, string Output)> RunInstallerAsync(string folder, string bundle, string sha256)
    {
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(folder, "install-agent.ps1"),
                     "upgrade", "-Bundle", bundle, "-BundleSha256", sha256 })
        {
            start.ArgumentList.Add(arg);
        }

        // The installer keeps the trusted CA as it is when the helper runs it (the agent never chooses what it trusts).
        start.Environment["MIRBADLLM_UPDATE_HELPER"] = "1";
        start.Environment["SPARK_SERVE_UPDATE_HELPER"] = "1"; // the same for a pre-rename installer (legacy shim)
        using var process = Process.Start(start) ?? throw new InvalidOperationException("powershell.exe did not start.");
        var output = new StringBuilder();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return (124, "The installer did not finish within 30 minutes.");
        }

        output.Append(await stdout.ConfigureAwait(false)).Append(await stderr.ConfigureAwait(false));
        string text = output.ToString();
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return (process.ExitCode, string.Join('\n', lines.TakeLast(40)));
    }

    /// <summary>The agent's side: starts the helper service after the hand-over. False (logged) when it cannot.</summary>
    public static bool TryStart(ILogger logger)
    {
        try
        {
            using var sc = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), ["start", ServiceName])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (sc is null)
            {
                return false;
            }

            string output = sc.StandardOutput.ReadToEnd();
            sc.WaitForExit(15_000);
            if (sc.ExitCode != 0)
            {
                LogStartFailed(logger, sc.ExitCode, output.Trim());
            }

            return sc.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LogStartFailed(logger, -1, ex.Message);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not start the update helper service (sc.exe start, exit {ExitCode}): {Output}")]
    private static partial void LogStartFailed(ILogger logger, int exitCode, string output);

    private sealed partial class Worker(IHostApplicationLifetime lifetime, ILogger<Worker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                var result = await new UpdateHelperCore(Spool, Trust, TimeProvider.System).RunAsync(RunInstallerAsync, stoppingToken).ConfigureAwait(false);
                if (result is not null)
                {
                    LogDone(logger, result.OperationId ?? "", result.ExitCode ?? -1);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
            finally
            {
                lifetime.StopApplication();
            }
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Agent update {OperationId} finished with exit code {ExitCode}.")]
        private static partial void LogDone(ILogger logger, string operationId, int exitCode);

        [LoggerMessage(Level = LogLevel.Error, Message = "The agent update helper failed.")]
        private static partial void LogFailed(ILogger logger, Exception exception);
    }
}
