using System.Diagnostics;

namespace MirbadLLM.HostAgent;

/// <summary>What a finished (or timed-out) child process produced.</summary>
internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut = false)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;

    /// <summary>A one-line reason for logs and API errors.</summary>
    public string Describe()
    {
        if (TimedOut)
        {
            return "timed out";
        }

        string err = StdErr.Trim();
        if (err.Length == 0)
        {
            err = StdOut.Trim();
        }

        string firstLine = err.Split('\n', 2)[0].Trim();
        return firstLine.Length == 0 ? $"exit code {ExitCode}" : $"exit code {ExitCode}: {firstLine}";
    }
}

/// <summary>Runs a program with an explicit argument vector. Never a shell, never a concatenated command line.</summary>
internal interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}

internal sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // systemctl must never page or ask for a password.
        startInfo.Environment["SYSTEMD_PAGER"] = "";
        startInfo.Environment["SYSTEMD_LESS"] = "";
        startInfo.Environment["LC_ALL"] = "C";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            return new ProcessResult(-1, "", "", TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}

/// <summary>Reads <c>/proc/&lt;pid&gt;/cgroup</c>; abstracted so GPU-to-unit mapping is testable off-Linux.</summary>
internal interface ICgroupReader
{
    /// <summary>The unified (v2) cgroup path of <paramref name="pid"/>, e.g. <c>/system.slice/comfyui.service</c>; null if unknown.</summary>
    string? Read(int pid);
}

internal sealed class ProcCgroupReader : ICgroupReader
{
    public string? Read(int pid)
    {
        try
        {
            return GpuCgroupMapper.ParseCgroupFile(File.ReadAllText($"/proc/{pid}/cgroup"));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
