using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Runtimes.Process.Windows;

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// MN-212: the Windows process host (design-p3p4 §A.1.3, D121; spike S4 / F2) against real child processes. The child is
/// a copy of Windows PowerShell 5.1 (<c>powershell.exe</c>, which runs from any folder) in a per-test temp folder, so
/// candidate listing by image folder sees only processes these tests started; its scripts use .NET calls only and every
/// child ends itself within two minutes even if a test dies. Nothing else on the machine is started, adopted or killed:
/// every kill goes through an identity (PID + start time + image path) of a process a test started.
/// Windows only; skipped elsewhere with a reason.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Windows")]
public sealed partial class WindowsProcessHostTests : IAsyncDisposable
{
    private const string WindowsOnly = "Windows process host (CreateProcessW, job objects): runs on Windows only.";
    private const string HelperVariable = "MIRBADLLM_MN212_HELPER_REQUEST";
    private const int ChildLifetimeSeconds = 120;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mn212-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<ProcessIdentity> _started = [];
    private readonly List<Process> _helpers = [];
    private string? _childExe;
    private int _logCounter;

    /// <summary>True on Windows (the <c>SkipUnless</c> condition).</summary>
    public static bool IsWindows => OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The child image: a private copy of <c>powershell.exe</c> under <c>{temp}\bin</c>.</summary>
    private string ChildExe
    {
        get
        {
            if (_childExe is null)
            {
                string source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe");
                Assert.True(File.Exists(source), $"{source} not found (Windows PowerShell 5.1 is part of Windows).");
                string bin = Path.Combine(_dir, "bin");
                Directory.CreateDirectory(bin);
                _childExe = Path.Combine(bin, "powershell.exe");
                File.Copy(source, _childExe);
            }

            return _childExe;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (Process helper in _helpers)
        {
            try
            {
                if (!helper.HasExited)
                {
                    helper.Kill();
                }
            }
            catch (InvalidOperationException)
            {
                // already gone
            }

            helper.Dispose();
        }

        if (OperatingSystem.IsWindows())
        {
            var host = new WindowsProcessHost();
            foreach (ProcessIdentity identity in _started)
            {
                try
                {
                    // Identity-checked: only a process these tests started can match.
                    await host.KillAsync(identity, CancellationToken.None);
                }
                catch (Win32Exception)
                {
                    // best effort; the child ends itself after ChildLifetimeSeconds
                }
            }
        }

        for (int attempt = 0; attempt < 20 && Directory.Exists(_dir); attempt++)
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100, CancellationToken.None);
            }
        }
    }

#pragma warning disable CA1861 // test data, built once per discovery
    public static TheoryData<string[]> QuotingCases => new()
    {
        { new[] { "plain", "two words", "" } },
        { new[] { "a\"b", "\"quoted\"", "\"", "\\\"" } },
        { new[] { "back\\slash", "trailing\\", "trailing two\\\\", "C:\\path with space\\", "\\\\server\\share\\" } },
        { new[] { "--alias", "org/Qwen3-Embedding-4B:Q8_0", "-m", "D:\\models\\hf\\org\\repo\\model.gguf" } },
        { new[] { "tab\there", "new\nline", "ünïcødé ✓", "a\\\\\"b", "\\" } },
        { new[] { "--chat-template", "{% if x %}\"y\"{% endif %}", "--port", "8101" } },
    };
#pragma warning restore CA1861

    [Theory(Skip = WindowsOnly, SkipUnless = nameof(IsWindows))]
    [MemberData(nameof(QuotingCases))]
    public void CommandLineRoundTripsThroughCommandLineToArgvW(string[] arguments)
    {
        const string exe = @"C:\Program Files\mirbadllm\runtimes\llama.cpp\b11476\llama-server.exe";
        string commandLine = WindowsCommandLine.Build(exe, arguments);

        IReadOnlyList<string> argv = WindowsCommandLine.Split(commandLine);

        Assert.Equal(exe, argv[0]);
        Assert.Equal(arguments, argv.Skip(1));
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(IsWindows))]
    public void CommandLineRoundTripsRandomArguments()
    {
        var random = new Random(212);
        const string alphabet = "ab \\\"\t:/=-";
        for (int round = 0; round < 2000; round++)
        {
            string[] arguments = new string[random.Next(0, 5)];
            for (int i = 0; i < arguments.Length; i++)
            {
                var sb = new StringBuilder();
                int length = random.Next(0, 8);
                for (int j = 0; j < length; j++)
                {
                    sb.Append(alphabet[random.Next(alphabet.Length)]);
                }

                arguments[i] = sb.ToString();
            }

            IReadOnlyList<string> argv = WindowsCommandLine.Split(WindowsCommandLine.Build(@"C:\x\y.exe", arguments));
            Assert.True(arguments.SequenceEqual(argv.Skip(1)),
                $"round {round}: [{string.Join("|", arguments)}] came back as [{string.Join("|", argv.Skip(1))}]");
        }
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(IsWindows))]
    public void CommandLineRefusesAQuoteInTheExecutableAndNulCharacters()
    {
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.Build("C:\\a\"b.exe", []));
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.Build("C:\\a.exe", ["x\0y"]));
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(IsWindows))]
    public void EnvironmentBlockIsSortedCaseInsensitivelyAndRefusesDuplicatesAndBadNames()
    {
        string block = WindowsProcessHost.BuildEnvironmentBlock(new Dictionary<string, string>
        {
            ["TEMP"] = "C:\\t",
            ["CUDA_VISIBLE_DEVICES"] = "GPU-1",
            ["b"] = "",
            ["SystemRoot"] = "C:\\Windows",
        });

        Assert.Equal("b=\0CUDA_VISIBLE_DEVICES=GPU-1\0SystemRoot=C:\\Windows\0TEMP=C:\\t\0\0", block);
        Assert.Equal("\0\0", WindowsProcessHost.BuildEnvironmentBlock(new Dictionary<string, string>()));
        Assert.Throws<ArgumentException>(() => WindowsProcessHost.BuildEnvironmentBlock(
            new Dictionary<string, string> { ["Path"] = "a", ["PATH"] = "b" }));
        Assert.Throws<ArgumentException>(() => WindowsProcessHost.BuildEnvironmentBlock(
            new Dictionary<string, string> { ["A=B"] = "x" }));
        Assert.Throws<ArgumentException>(() => WindowsProcessHost.BuildEnvironmentBlock(
            new Dictionary<string, string> { ["A"] = "x\0y" }));
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(IsWindows))]
    public async Task ReadArgumentsReturnsExactlyTheStartedArgumentsAndTheEnvironmentIsOnlyTheGivenBlock()
    {
        var host = new WindowsProcessHost();
        string[] tricky = ["a b", "q\"uote", "back\\slash\\", "two\\\\", "--alias", "org/model:Q8_0", "", "C:\\dir with space\\"];
        ProcessHostStart start = ChildStart(
            "[Console]::Out.WriteLine('mark=' + $env:SS_MARK); [Console]::Out.WriteLine('user=[' + $env:USERNAME + ']');"
            + " [Console]::Out.WriteLine('path=[' + $env:PATH + ']');" + SleepLoop(),
            tricky);

        await using IHostedProcess child = await StartTrackedAsync(host, start);
        await WaitForLogAsync(start.LogPath, "mark=" + start.Environment["SS_MARK"]);

        IReadOnlyList<string>? arguments = await host.ReadArgumentsAsync(child.Identity, Ct);

        Assert.NotNull(arguments);
        Assert.Equal(start.Arguments, arguments);
        string log = ReadLog(start.LogPath);
        Assert.Contains("user=[]", log, StringComparison.Ordinal); // nothing inherited from this process's environment
        Assert.Contains("path=[]", log, StringComparison.Ordinal);
        Assert.False(child.HasExited);
        Assert.Null(child.ExitCode);

        await child.KillTreeAsync(Ct);
        Assert.True(child.HasExited);
        Assert.Equal((int)WindowsHostedProcess.KilledExitCode, child.ExitCode);
        Assert.Null(await host.ReadArgumentsAsync(child.Identity, Ct));
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(IsWindows))]
    public async Task AnInheritableHandleOfTheAgentIsNotInheritedOnlyTheStdHandlesAre()
    {
        // An inheritable handle in this process (the client end of an anonymous pipe). An inherited handle keeps its value
        // in the child, so the child holds it exactly when its handle table has the same object at the same value.
        using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        nint inheritable = pipe.ClientSafePipeHandle.DangerousGetHandle();
        var host = new WindowsProcessHost();
        ProcessHostStart start = ChildStart("[Console]::Out.WriteLine('up');" + SleepLoop());

        await using IHostedProcess child = await StartTrackedAsync(host, start);
        await WaitForLogAsync(start.LogPath, "up");

        // Control: a child started the ordinary way (System.Diagnostics.Process, bInheritHandles without a handle list)
        // does inherit it, so the probe can see an inherited handle.
        using Process control = Process.Start(new ProcessStartInfo(ChildExe)
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", SleepLoop() },
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _dir,
        })!;
        _started.Add(WindowsProcessHost.TryQueryIdentity(control.Id)!);
        Assert.True(HandleProbe.Holds(control.Id, inheritable), "control: the ordinary child should hold the inheritable handle");

        Assert.False(HandleProbe.Holds(child.Identity.Pid, inheritable), "the host's child inherited a handle outside its handle list");
        Assert.False(child.HasExited);
        await child.KillTreeAsync(Ct);
        control.Kill();
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(IsWindows))]
    public async Task KillTreeEndsTheChildAndItsGrandchildThroughTheJob()
    {
        var host = new WindowsProcessHost();
        ProcessHostStart start = ChildStart(
            "$psi = [Diagnostics.ProcessStartInfo]::new([Diagnostics.Process]::GetCurrentProcess().MainModule.FileName,"
            + $" '-NoProfile -NonInteractive -Command [Threading.Thread]::Sleep({ChildLifetimeSeconds * 1000})');"
            + " $psi.UseShellExecute = $false; $g = [Diagnostics.Process]::Start($psi);"
            + " [Console]::Out.WriteLine('grandchild=' + $g.Id + '.');" + SleepLoop());

        await using IHostedProcess child = await StartTrackedAsync(host, start);
        string log = await WaitForLogAsync(start.LogPath, "grandchild=");
        int grandchildPid = int.Parse(GrandchildPid().Match(log).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        ProcessIdentity? grandchild = WindowsProcessHost.TryQueryIdentity(grandchildPid);
        Assert.NotNull(grandchild);
        _started.Add(grandchild);

        await child.KillTreeAsync(Ct);

        Assert.True(child.HasExited);
        Assert.True(await GoneAsync(grandchild), "the grandchild survived TerminateJobObject");
        Assert.Equal((int)WindowsHostedProcess.KilledExitCode, await child.WaitForExitAsync(Ct));
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(IsWindows))]
    public async Task DisposingReleasesTheHandlesWithoutKillingAndANewHostAdoptsIntoANewJob()
    {
        var host = new WindowsProcessHost();
        ProcessHostStart start = ChildStart("[Console]::Out.WriteLine('up');" + SleepLoop());
        IHostedProcess child = await StartTrackedAsync(host, start);
        Assert.True(((WindowsHostedProcess)child).IsInOwnJob());
        ProcessIdentity identity = child.Identity;

        // Closing the job's only handle (what an agent stop, upgrade or crash does) must not kill: no kill-on-close.
        await child.DisposeAsync();
        await Task.Delay(500, Ct);
        Assert.Equal(identity, WindowsProcessHost.TryQueryIdentity(identity.Pid));

        await using IHostedProcess? adopted = await new WindowsProcessHost().OpenAsync(identity, start.JobName, Ct);
        Assert.NotNull(adopted);
        Assert.True(((WindowsHostedProcess)adopted).IsInOwnJob());
        Assert.Equal(identity, adopted.Identity);
        Assert.Equal(start.JobName, adopted.JobName);

        await adopted.KillTreeAsync(Ct);
        Assert.True(await GoneAsync(identity));
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(IsWindows))]
    public async Task TheChildSurvivesItsParentAndIsAdoptedByASecondHostThatKillsIt()
    {
        // A helper parent (this test assembly, running HelperParentStartsAChildAndWaits) starts the child through its own
        // host; killing the parent closes the job's only handle. The child must keep running and keep logging.
        ProcessHostStart start = ChildStart(
            "[Console]::Out.WriteLine('out-before'); [Console]::Error.WriteLine('err-before');"
            + $" $deadline = [DateTime]::UtcNow.AddSeconds({ChildLifetimeSeconds});"
            + " while (-not [IO.File]::Exists('go') -and [DateTime]::UtcNow -lt $deadline) { [Threading.Thread]::Sleep(100) };"
            + " [Console]::Out.WriteLine('out-after-parent'); [Console]::Error.WriteLine('err-after-parent');"
            + " while ([DateTime]::UtcNow -lt $deadline) { [Threading.Thread]::Sleep(200) }");
        string request = Path.Combine(_dir, "helper-request.json");
        await File.WriteAllTextAsync(request, JsonSerializer.Serialize(start, Json), Ct);

        Process parent = StartHelperParent(request);
        ProcessIdentity identity = await WaitForHelperIdentityAsync(parent, request);
        _started.Add(identity);
        await WaitForLogAsync(start.LogPath, "err-before");

        parent.Kill();
        await parent.WaitForExitAsync(Ct);
        await Task.Delay(500, Ct);
        Assert.Equal(identity, WindowsProcessHost.TryQueryIdentity(identity.Pid));

        var secondHost = new WindowsProcessHost();
        await using IHostedProcess? adopted = await secondHost.OpenAsync(identity, start.JobName, Ct);
        Assert.NotNull(adopted);
        Assert.True(((WindowsHostedProcess)adopted).IsInOwnJob(), "adoption must assign the process to the new job");

        await File.WriteAllTextAsync(Path.Combine(_dir, "go"), "", Ct);
        await WaitForLogAsync(start.LogPath, "out-after-parent");
        await WaitForLogAsync(start.LogPath, "err-after-parent");

        await adopted.KillTreeAsync(Ct);
        Assert.True(adopted.HasExited);
        Assert.True(await GoneAsync(identity));
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(IsWindows))]
    public async Task OpenRefusesPidReuseAChangedImagePathAndAnExitedProcess()
    {
        var host = new WindowsProcessHost();
        ProcessHostStart start = ChildStart(SleepLoop());
        await using IHostedProcess child = await StartTrackedAsync(host, start);
        ProcessIdentity real = child.Identity;
        ProcessIdentity reused = real with { StartTime = real.StartTime.AddTicks(1) };
        ProcessIdentity moved = real with { ImagePath = Path.Combine(Path.GetDirectoryName(real.ImagePath)!, "other", "powershell.exe") };

        Assert.Null(await host.OpenAsync(reused, "mirbadllm-test-" + Guid.NewGuid().ToString("N"), Ct));
        Assert.Null(await host.OpenAsync(moved, "mirbadllm-test-" + Guid.NewGuid().ToString("N"), Ct));
        Assert.Null(await host.ReadArgumentsAsync(reused, Ct));
        Assert.Null(await host.ReadArgumentsAsync(moved, Ct));
        Assert.False(await host.KillAsync(reused, Ct));
        Assert.False(await host.KillAsync(moved, Ct));
        Assert.False(child.HasExited); // none of the refused calls touched it

        await child.KillTreeAsync(Ct);
        Assert.Null(await host.OpenAsync(real, "mirbadllm-test-" + Guid.NewGuid().ToString("N"), Ct));
        Assert.False(await host.KillAsync(real, Ct));
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(IsWindows))]
    public async Task CandidatesAreListedByImageFolderAndOwnerAndKilledByIdentity()
    {
        var host = new WindowsProcessHost();
        ProcessHostStart start = ChildStart(SleepLoop());
        await using IHostedProcess child = await StartTrackedAsync(host, start);
        string bin = Path.GetDirectoryName(ChildExe)!;
        string elsewhere = Directory.CreateDirectory(Path.Combine(_dir, "elsewhere")).FullName;

        IReadOnlyList<ProcessIdentity> candidates = await host.ListCandidatesAsync(bin, Ct);
        Assert.Equal([child.Identity], candidates);
        Assert.Contains(child.Identity, await host.ListCandidatesAsync(bin + Path.DirectorySeparatorChar, Ct));
        Assert.Empty(await host.ListCandidatesAsync(elsewhere, Ct));
        Assert.Empty(await host.ListCandidatesAsync(bin + "x", Ct)); // a sibling folder with the same prefix

        // Owned by another account (LocalSystem) → never a candidate.
        var otherOwner = new WindowsProcessHost(null, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        Assert.Empty(await otherOwner.ListCandidatesAsync(bin, Ct));

        Assert.True(await host.KillAsync(child.Identity, Ct));
        Assert.True(child.HasExited);
        Assert.Empty(await host.ListCandidatesAsync(bin, Ct));
        Assert.False(await host.KillAsync(child.Identity, Ct));
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(IsWindows))]
    public async Task AJobNameInUseIsNeverSharedSoKillingOneInstanceLeavesTheOther()
    {
        var host = new WindowsProcessHost();
        ProcessHostStart first = ChildStart(SleepLoop());
        ProcessHostStart second = ChildStart(SleepLoop()) with { JobName = first.JobName };

        await using IHostedProcess a = await StartTrackedAsync(host, first);
        await using IHostedProcess b = await StartTrackedAsync(host, second);
        await b.KillTreeAsync(Ct);

        Assert.True(b.HasExited);
        Assert.False(a.HasExited);
        await a.KillTreeAsync(Ct);
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(IsWindows))]
    public async Task AFailedCreateThrowsAndLeavesNoProcess()
    {
        var host = new WindowsProcessHost();
        ProcessHostStart start = ChildStart(SleepLoop()) with { ExecutablePath = Path.Combine(_dir, "missing", "llama-server.exe") };

        var ex = await Assert.ThrowsAsync<Win32Exception>(() => host.StartAsync(start, Ct));
        Assert.Contains("CreateProcessW", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => host.StartAsync(start with { LogPath = "relative.log" }, Ct));
        Assert.Empty(await host.ListCandidatesAsync(Path.Combine(_dir, "missing"), Ct));
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(IsWindows))]
    public async Task TheLogOfARunningChildRotatesByCopyAndTruncateAndItsNextWritesLandAtTheStart()
    {
        // MN-219 (D128): the driver rotates a running llama-server's log in place. The child holds the file with
        // FILE_APPEND_DATA, shared for writing, so the truncation works and its next line lands at offset 0 (no hole of
        // NULs up to the old length, which a plain write offset would leave).
        var host = new WindowsProcessHost();
        ProcessHostStart start = ChildStart(
            "for ($i = 0; $i -lt 600; $i++) { [Console]::Out.WriteLine('line ' + $i + ' ' + ('x' * 200)); [Threading.Thread]::Sleep(50) }");
        await using IHostedProcess child = await StartTrackedAsync(host, start);
        var logs = new MirbadLLM.NodeRuntime.Runtimes.Process.ProcessLogFiles(new MirbadLLM.NodeRuntime.Runtimes.Process.ProcessRuntimeOptions { StateDirectory = _dir, LogMaxBytes = 4096, LogFilesKept = 2 });
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((!File.Exists(start.LogPath) || new FileInfo(start.LogPath).Length < 4096) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        long before = new FileInfo(start.LogPath).Length;
        Assert.True(logs.RotateWhileRunning(start.LogPath), "rotation refused");
        Assert.True(new FileInfo(start.LogPath + ".1").Length >= before);
        await Task.Delay(600, Ct); // the child writes a few more lines

        byte[] after;
        using (var read = new FileStream(start.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            after = new byte[read.Length];
            read.ReadExactly(after);
        }

        Assert.False(child.HasExited);
        Assert.True(after.Length is > 0 and < 4096, $"{after.Length} bytes after the rotation");
        Assert.NotEqual(0, after[0]);
        Assert.StartsWith("line ", Encoding.UTF8.GetString(after).TrimStart(), StringComparison.Ordinal);
        await child.KillTreeAsync(Ct);
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(IsWindows))]
    public async Task TryQueryIdentityOfAnExitedProcessIsNull()
    {
        // MN-219 review guard: the helper opens the process without SYNCHRONIZE, so its exit wait fails, but the image-path
        // query of an exited process fails too, so it still reads as gone. The Process object keeps its own handle open,
        // so the PID cannot be reused while the test looks.
        using var exited = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        await exited.WaitForExitAsync(Ct);

        Assert.Null(WindowsProcessHost.TryQueryIdentity(exited.Id));
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(IsWindows))]
    public void TheHostLogsTheAgentsOwnJobMembershipWhenCreated()
    {
        var logger = new ListLogger();

        _ = new WindowsProcessHost(logger, owner: null);

        string line = Assert.Single(logger.Lines, l => l.StartsWith("Agent process job membership", StringComparison.Ordinal));
        Assert.Contains($"in a job = {WindowsProcessHost.IsAgentInJob()}", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// Not a test on its own: the helper parent of <see cref="TheChildSurvivesItsParentAndIsAdoptedByASecondHostThatKillsIt"/>,
    /// run in a separate process (<c>-method … -explicit only</c>) with <see cref="HelperVariable"/> naming the request.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task HelperParentStartsAChildAndWaits()
    {
        string? request = Environment.GetEnvironmentVariable(HelperVariable);
        if (string.IsNullOrEmpty(request) || !OperatingSystem.IsWindows())
        {
            Assert.Skip($"Helper process only: started by {nameof(TheChildSurvivesItsParentAndIsAdoptedByASecondHostThatKillsIt)}.");
        }

        ProcessHostStart start = JsonSerializer.Deserialize<ProcessHostStart>(await File.ReadAllTextAsync(request, Ct), Json)!;
        IHostedProcess child = await new WindowsProcessHost().StartAsync(start, Ct);
        string temp = request + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(child.Identity, Json), Ct);
        File.Move(temp, request + ".out");

        // Killed by the test long before this ends; the child ends itself after ChildLifetimeSeconds in any case.
        await Task.Delay(TimeSpan.FromSeconds(ChildLifetimeSeconds + 30), Ct);
    }

    private static string SleepLoop() =>
        $" [Threading.Thread]::Sleep({ChildLifetimeSeconds * 1000})";

    private ProcessHostStart ChildStart(string script, IReadOnlyList<string>? extraArguments = null)
    {
        int n = Interlocked.Increment(ref _logCounter);
        // With extra arguments the script ends in a comment: powershell joins everything after -Command into the script.
        string[] arguments = extraArguments is null
            ? ["-NoProfile", "-NonInteractive", "-Command", script]
            : ["-NoProfile", "-NonInteractive", "-Command", script + " #", .. extraArguments];
        return new ProcessHostStart
        {
            ExecutablePath = ChildExe,
            Arguments = arguments,
            Environment = new Dictionary<string, string>
            {
                ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows",
                ["TEMP"] = _dir,
                ["TMP"] = _dir,
                ["SS_MARK"] = "mark-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            WorkingDirectory = _dir,
            LogPath = Path.Combine(_dir, "logs", $"ss-{n}.log"),
            JobName = "mirbadllm-test-mn212-" + Guid.NewGuid().ToString("N"),
        };
    }

    private async Task<IHostedProcess> StartTrackedAsync(WindowsProcessHost host, ProcessHostStart start)
    {
        IHostedProcess child = await host.StartAsync(start, Ct);
        _started.Add(child.Identity);
        return child;
    }

    private Process StartHelperParent(string request)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "MirbadLLM.UnitTests.exe");
        var psi = File.Exists(exe)
            ? new ProcessStartInfo(exe)
            : new ProcessStartInfo("dotnet") { ArgumentList = { typeof(WindowsProcessHostTests).Assembly.Location } };
        foreach (string argument in new[]
                 {
                     "-method", $"{typeof(WindowsProcessHostTests).FullName}.{nameof(HelperParentStartsAChildAndWaits)}",
                     "-explicit", "only", "-noLogo", "-noColor",
                 })
        {
            psi.ArgumentList.Add(argument);
        }

        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.WorkingDirectory = AppContext.BaseDirectory;
        psi.Environment[HelperVariable] = request;
        Process parent = Process.Start(psi)!;
        _helpers.Add(parent);
        parent.OutputDataReceived += (_, _) => { };
        parent.ErrorDataReceived += (_, _) => { };
        parent.BeginOutputReadLine();
        parent.BeginErrorReadLine();
        return parent;
    }

    private static async Task<ProcessIdentity> WaitForHelperIdentityAsync(Process parent, string request)
    {
        string output = request + ".out";
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!File.Exists(output))
        {
            Assert.False(parent.HasExited, $"the helper parent exited with {(parent.HasExited ? parent.ExitCode : 0)} before starting the child");
            Assert.True(DateTime.UtcNow < deadline, "the helper parent did not start the child within 90 s");
            await Task.Delay(100, Ct);
        }

        return JsonSerializer.Deserialize<ProcessIdentity>(await File.ReadAllTextAsync(output, Ct), Json)!;
    }

    private static string ReadLog(string path)
    {
        if (!File.Exists(path))
        {
            return "";
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task<string> WaitForLogAsync(string path, string text)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            string log = ReadLog(path);
            if (log.Contains(text, StringComparison.Ordinal))
            {
                return log;
            }

            Assert.True(DateTime.UtcNow < deadline, $"'{text}' did not reach {path} within 60 s; log so far:\n{log}");
            await Task.Delay(100, Ct);
        }
    }

    private static async Task<bool> GoneAsync(ProcessIdentity identity)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (WindowsProcessHost.TryQueryIdentity(identity.Pid) == identity)
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(50, Ct);
        }

        return true;
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
            {
                Lines.Add(formatter(state, exception));
            }
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"grandchild=(\d+)\.")]
    private static partial System.Text.RegularExpressions.Regex GrandchildPid();

    /// <summary>Does process <c>pid</c> hold the same kernel object as this process's handle <c>value</c> at that value?</summary>
    private static class HandleProbe
    {
        private const uint ProcessDupHandle = 0x0040;
        private const uint DuplicateSameAccess = 0x2;

        public static bool Holds(int pid, nint value)
        {
            nint process = OpenProcess(ProcessDupHandle, false, pid);
            if (process == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenProcess({pid})");
            }

            try
            {
                if (!DuplicateHandle(process, value, GetCurrentProcess(), out nint copy, 0, false, DuplicateSameAccess))
                {
                    return false; // nothing at that value in the child
                }

                try
                {
                    return CompareObjectHandles(copy, value);
                }
                finally
                {
                    CloseHandle(copy);
                }
            }
            finally
            {
                CloseHandle(process);
            }
        }

#pragma warning disable SYSLIB1054 // the test project has no AllowUnsafeBlocks for LibraryImport's generated code
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern nint OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool DuplicateHandle(nint sourceProcess, nint sourceHandle, nint targetProcess, out nint target,
            uint access, bool inherit, uint options);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern nint GetCurrentProcess();

        [DllImport("kernelbase.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool CompareObjectHandles(nint first, nint second);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool CloseHandle(nint handle);
#pragma warning restore SYSLIB1054
    }
}
