using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Runtimes.Process;
using MirbadLLM.NodeRuntime.Runtimes.Process.Mac;

namespace MirbadLLM.UnitTests.Agent;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// macOS process delivery, Phase 2 (docs/macos/implementation-plan.md M2/M3): the <c>proc_bsdinfo</c> and
/// <c>KERN_PROCARGS2</c> parsers and the file key protector everywhere; on a Mac (the CI's mirbadllm-mac runner) the real
/// <see cref="MacProcessHost"/> — start in a new session, identity, arguments, log binding, the exact environment and
/// working directory, exit codes, adoption, killing the whole group, the orphan candidates' owner filter.
/// </summary>
public sealed class MacProcessHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ss-macproc-" + Guid.NewGuid().ToString("N")[..12]);

    public MacProcessHostTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    // ---- parsers and key protector (any OS) ---------------------------------------------------------------------------

    [Fact]
    public void BsdInfo_ReadsOwnerGroupStartAndZombie()
    {
        byte[] info = new byte[136];
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(4), 5); // SZOMB
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(20), 499);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(100), 4242);
        BinaryPrimitives.WriteUInt64LittleEndian(info.AsSpan(120), 1_792_000_000);
        BinaryPrimitives.WriteUInt64LittleEndian(info.AsSpan(128), 123_456);

        MacProcFormats.BsdInfo parsed = MacProcFormats.ParseBsdInfo(info);

        Assert.Equal(499u, parsed.Uid);
        Assert.Equal(4242, parsed.ProcessGroup);
        Assert.True(parsed.Zombie);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_792_000_000).AddTicks(1_234_560), parsed.Start);
    }

    [Fact]
    public void ProcArgs_ReadsArgvAfterThePathAndItsPadding_NotTheEnvironment()
    {
        byte[] buffer = ProcArgs(["/opt/llama-server", "-m", "/models/a b.gguf", "--port", "8101"], exec: "/opt/llama-server", env: ["SECRET=1"]);

        Assert.Equal(["/opt/llama-server", "-m", "/models/a b.gguf", "--port", "8101"], MacProcFormats.ParseProcArgs(buffer));
    }

    [Fact]
    public void ProcArgs_Truncated_IsNull()
    {
        byte[] buffer = ProcArgs(["/bin/x", "a", "b"], exec: "/bin/x", env: []);

        Assert.Null(MacProcFormats.ParseProcArgs(buffer.AsSpan(0, buffer.Length - 4)));
        Assert.Null(MacProcFormats.ParseProcArgs([1, 0]));
    }

    [Fact]
    public void Environment_IsSortedNameValuePairs_AndRefusesNamesThatOnlyDifferInCase()
    {
        IReadOnlyList<string> env = MacProcFormats.BuildEnvironment(new Dictionary<string, string> { ["PATH"] = "/x", ["A"] = "1=2" });

        Assert.Equal(["A=1=2", "PATH=/x"], env);
        Assert.Throws<ArgumentException>(() => MacProcFormats.BuildEnvironment(new Dictionary<string, string>(StringComparer.Ordinal) { ["Path"] = "a", ["PATH"] = "b" }));
        Assert.Throws<ArgumentException>(() => MacProcFormats.BuildEnvironment(new Dictionary<string, string> { ["A=B"] = "x" }));
    }

    [Fact]
    public void KeyProtector_RoundTrips_NeverStoresTheClearKey_AndRefusesATamperedBlob()
    {
        var protector = new FileInstanceKeyProtector(_dir);
        string key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        string blob = protector.Protect(key);

        Assert.StartsWith("v1:", blob, StringComparison.Ordinal);
        Assert.DoesNotContain(key, blob, StringComparison.Ordinal);
        Assert.NotEqual(blob, protector.Protect(key)); // fresh nonce each time
        Assert.Equal(key, new FileInstanceKeyProtector(_dir).Unprotect(blob)); // a new instance reads the same key file

        char[] tampered = blob.ToCharArray();
        tampered[^3] = tampered[^3] == 'A' ? 'B' : 'A';
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(new string(tampered)));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("plain"));

        string keyFile = Path.Combine(_dir, FileInstanceKeyProtector.KeyFileName);
        Assert.Equal(32, new FileInfo(keyFile).Length);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyFile));
        }
    }

    [Fact]
    public void KeyProtector_AnotherNodesKeyFile_DoesNotUnprotect()
    {
        string other = Path.Combine(_dir, "other");
        string blob = new FileInstanceKeyProtector(_dir).Protect("k");

        Assert.ThrowsAny<CryptographicException>(() => new FileInstanceKeyProtector(other).Unprotect(blob));
    }

    // ---- the real host (macOS) -----------------------------------------------------------------------------------------

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task Start_BindsTheLog_TheExactEnvironment_TheWorkingDirectory_AndReportsTheExitCode()
    {
        RequireMac();
        var host = new MacProcessHost();
        string log = Path.Combine(_dir, "logs", "ss-test.log");
        string work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(work);

        await using IHostedProcess process = await host.StartAsync(new ProcessHostStart
        {
            ExecutablePath = "/bin/sh",
            Arguments = ["-c", "echo out; echo err >&2; pwd; /usr/bin/env | /usr/bin/sort; exit 3"],
            Environment = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["MARK"] = "x y" },
            WorkingDirectory = work,
            LogPath = log,
            JobName = "mirbadllm-ss-test",
        }, Ct);

        Assert.Equal(3, await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct));
        Assert.True(process.HasExited);
        Assert.Equal(3, process.ExitCode);
        string[] lines = await File.ReadAllLinesAsync(log, Ct);
        Assert.Contains("out", lines);
        Assert.Contains("err", lines);
        Assert.Contains(lines, l => l.EndsWith("/work", StringComparison.Ordinal));
        string[] env = [.. lines.Where(l => l.Contains('=', StringComparison.Ordinal))];
        Assert.Contains("MARK=x y", env);
        Assert.Contains("PATH=/usr/bin:/bin", env);
        Assert.DoesNotContain(env, l => l.StartsWith("HOME=", StringComparison.Ordinal) || l.StartsWith("DOTNET_", StringComparison.Ordinal));
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task Identity_Arguments_Adoption_AndKillTree_StopEveryProcessOfTheGroup()
    {
        RequireMac();
        var host = new MacProcessHost();
        string marker = "ss-" + Guid.NewGuid().ToString("N")[..8];
        string log = Path.Combine(_dir, "tree.log");
        IHostedProcess process = await host.StartAsync(new ProcessHostStart
        {
            ExecutablePath = "/bin/sh",
            // A grandchild in the background: KillTreeAsync must take it down too (the process group).
            Arguments = ["-c", "/bin/sleep 300 & echo child=$!; /bin/sleep 300", marker],
            Environment = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" },
            WorkingDirectory = _dir,
            LogPath = log,
            JobName = "mirbadllm-ss-" + marker,
        }, Ct);

        // macOS's /bin/sh re-executes itself as another shell: the recorded identity is the settled one, so it adopts.
        ProcessIdentity identity = process.Identity;
        Assert.Equal(MacProcessHost.TryQueryIdentity(identity.Pid), identity);
        Assert.False(process.HasExited);
        Assert.Equal(["-c", "/bin/sleep 300 & echo child=$!; /bin/sleep 300", marker], await host.ReadArgumentsAsync(identity, Ct));

        // A recorded identity with another start time is never adopted (pid reuse); the real one is.
        Assert.Null(await host.OpenAsync(identity with { StartTime = identity.StartTime.AddSeconds(-1) }, "j", Ct));
        await process.DisposeAsync(); // disposing never kills
        Assert.NotNull(MacProcessHost.TryQueryIdentity(identity.Pid));
        IHostedProcess adopted = (await new MacProcessHost().OpenAsync(identity, "mirbadllm-ss-" + marker, Ct))!;
        Assert.Equal(identity, adopted.Identity);

        int grandchild = await ReadChildPidAsync(log);
        Assert.NotNull(MacProcessHost.TryQueryIdentity(grandchild));

        await adopted.KillTreeAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.True(adopted.HasExited);
        Assert.Null(MacProcessHost.TryQueryIdentity(identity.Pid));
        await WaitGoneAsync(grandchild);
        Assert.Null(await host.ReadArgumentsAsync(identity, Ct));
        Assert.False(await host.KillAsync(identity, Ct));
        await adopted.DisposeAsync();
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task Candidates_AreTheAgentAccountsProcessesUnderTheFolder_AndKillAsyncStopsOne()
    {
        RequireMac();
        string folder = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(folder);
        string sleeper = Path.Combine(folder, "sleeper");
        File.Copy("/bin/sleep", sleeper);
        File.SetUnixFileMode(sleeper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var host = new MacProcessHost();
        await using IHostedProcess process = await host.StartAsync(new ProcessHostStart
        {
            ExecutablePath = sleeper,
            Arguments = ["300"],
            Environment = new Dictionary<string, string>(),
            WorkingDirectory = _dir,
            LogPath = Path.Combine(_dir, "c.log"),
            JobName = "mirbadllm-ss-c",
        }, Ct);

        IReadOnlyList<ProcessIdentity> mine = await host.ListCandidatesAsync(folder, Ct);
        IReadOnlyList<ProcessIdentity> someoneElses = await new MacProcessHost(null, uid: 4_000_000).ListCandidatesAsync(folder, Ct);

        Assert.Contains(mine, c => c.Pid == process.Identity.Pid);
        Assert.Empty(someoneElses);
        Assert.Equal(["300"], await host.ReadArgumentsAsync(process.Identity, Ct));
        Assert.True(await host.KillAsync(process.Identity, Ct));
        Assert.True(process.HasExited);
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task AMissingExecutable_FailsTheStart()
    {
        RequireMac();
        await Assert.ThrowsAnyAsync<Exception>(() => new MacProcessHost().StartAsync(new ProcessHostStart
        {
            ExecutablePath = Path.Combine(_dir, "nope"),
            Arguments = [],
            Environment = new Dictionary<string, string>(),
            WorkingDirectory = _dir,
            LogPath = Path.Combine(_dir, "n.log"),
            JobName = "j",
        }, Ct));
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    private static void RequireMac()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Runs the real macOS process host.");
        }
    }

    private static async Task<int> ReadChildPidAsync(string log)
    {
        for (int i = 0; i < 100; i++)
        {
            if (File.Exists(log) && (await File.ReadAllLinesAsync(log, Ct)).FirstOrDefault(l => l.StartsWith("child=", StringComparison.Ordinal)) is { } line)
            {
                return int.Parse(line["child=".Length..], System.Globalization.CultureInfo.InvariantCulture);
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException("The shell did not print its child's pid.");
    }

    [SupportedOSPlatform("macos")]
    private static async Task WaitGoneAsync(int pid)
    {
        for (int i = 0; i < 100 && MacProcessHost.TryQueryIdentity(pid) is not null; i++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.Null(MacProcessHost.TryQueryIdentity(pid));
    }

    private static byte[] ProcArgs(string[] argv, string exec, string[] env)
    {
        using var stream = new MemoryStream();
        stream.Write(BitConverter.GetBytes(argv.Length));
        stream.Write(Encoding.UTF8.GetBytes(exec));
        stream.Write(new byte[5]); // NUL terminator plus padding
        foreach (string arg in argv.Concat(env))
        {
            stream.Write(Encoding.UTF8.GetBytes(arg));
            stream.WriteByte(0);
        }

        return stream.ToArray();
    }
}
