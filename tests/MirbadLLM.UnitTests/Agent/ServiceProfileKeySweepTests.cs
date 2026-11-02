using System.Security.Principal;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.UnitTests.HostAgent;
using AgentProgram = MirbadLLM.Agent.Program;

namespace MirbadLLM.UnitTests.Agent;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// MN-215 (design-p3p4 §A.4.7, D122; spike S4 F3): the start-up sweep of the CNG key files an unclean exit leaves in the
/// service profile. Every test injects the identity and a temporary profile folder; none touches a real key store (the
/// one test that reads this process's identity only checks that it is not the service account and never runs the sweep).
/// </summary>
public sealed class ServiceProfileKeySweepTests : IDisposable
{
    /// <summary>A real user's SID shape (a console or dev run): never the service account.</summary>
    private const string DeveloperSid = "S-1-5-21-1004336348-1177238915-682003330-1001";

    private readonly string _appData = Path.Combine(Path.GetTempPath(), "ss-mn215-sweep-" + Guid.NewGuid().ToString("N")[..12], "AppData", "Roaming");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string KeysFolder => Path.Combine(_appData, "Microsoft", "Crypto", "Keys");

    private string RsaFolder(string sid) => Path.Combine(_appData, "Microsoft", "Crypto", "RSA", sid);

    public void Dispose()
    {
        string root = Path.GetFullPath(Path.Combine(_appData, "..", ".."));
        if (Directory.Exists(root))
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ServiceSid_IsTheOneWindowsDerivesForTheAgentService()
    {
        // "sc.exe showsid mirbadllm-agent" (and spark-serve-agent, the name an install made before the rename keeps; S4
        // logged that one under the SCM on the owner's PC).
        Assert.Equal("S-1-5-80-3170306370-3866711971-977087225-1785589056-2812954755", ServiceProfileKeySweep.AgentServiceSid);
        Assert.Equal(ServiceProfileKeySweep.AgentServiceSid, ServiceProfileKeySweep.ServiceSidFor("MIRBADLLM-AGENT"));
        Assert.Equal("S-1-5-80-383857687-2312895033-837081144-4191421113-4061622124", ServiceProfileKeySweep.ServiceSidFor("spark-serve-agent"));
        Assert.NotEqual(ServiceProfileKeySweep.AgentServiceSid, ServiceProfileKeySweep.ServiceSidFor("mirbadllm-hostd"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\mirbadllm-agent\app\826bc75fc222\", false)]
    [InlineData(@"C:\Program Files\spark-serve-agent\app\826bc75fc222\", true)]
    [InlineData(@"C:\PROGRAM FILES\SPARK-SERVE-AGENT\app\1\", true)]
    [InlineData("/usr/local/lib/mirbadllm-agent/", false)]
    [InlineData(@"C:\src\MirbadLLM\artifacts\bin\MirbadLLM.Agent\debug\", false)]
    public void AnInstallMadeBeforeTheRename_KeepsItsServiceName(string baseDirectory, bool legacy)
    {
        Assert.Equal(legacy, WindowsServiceHosting.IsLegacyInstall(baseDirectory));
    }

    [Fact]
    public void UnderTheServiceAccount_DeletesEveryLeakedKeyFile_AndNothingElse()
    {
        string sid = ServiceProfileKeySweep.AgentServiceSid;
        string[] leaked =
        [
            Plant(KeysFolder, "6a1f0c3e9b_9f1c8d2e-1111-2222-3333-444455556666"),
            Plant(KeysFolder, "77c0ffee00_9f1c8d2e-1111-2222-3333-777788889999"),
            Plant(RsaFolder(sid), "f00dfeed_9f1c8d2e-aaaa-bbbb-cccc-ddddeeeeffff"),
        ];
        File.SetAttributes(leaked[1], FileAttributes.ReadOnly);

        // Left alone: another SID's RSA folder, DPAPI master keys, a sub-folder of Keys and a file next to the key folders.
        string otherRsa = Plant(RsaFolder(DeveloperSid), "other-user-key");
        string protect = Plant(Path.Combine(_appData, "Microsoft", "Protect", sid), "master-key");
        string nested = Plant(Path.Combine(KeysFolder, "nested"), "kept");
        string beside = Plant(Path.Combine(_appData, "Microsoft", "Crypto"), "not-a-key-folder.txt");
        var log = new CapturingLogger<ServiceProfileKeySweep>();

        KeySweepResult result = new ServiceProfileKeySweep(sid, _appData, log).Run();

        Assert.Equal(new KeySweepResult(true, 3, 0), result);
        Assert.All(leaked, path => Assert.False(File.Exists(path), path));
        Assert.All([otherRsa, protect, nested, beside], path => Assert.True(File.Exists(path), path));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("deleted 3 key file(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void UnderTheServiceAccount_AnEmptyOrMissingProfileIsFine()
    {
        Assert.Equal(new KeySweepResult(true, 0, 0), new ServiceProfileKeySweep(ServiceProfileKeySweep.AgentServiceSid, _appData, NullLogger.Instance).Run());
        Directory.CreateDirectory(KeysFolder);
        Assert.Equal(new KeySweepResult(true, 0, 0), new ServiceProfileKeySweep(ServiceProfileKeySweep.AgentServiceSid, _appData, NullLogger.Instance).Run());
    }

    [Theory]
    [InlineData(DeveloperSid)]
    [InlineData("S-1-5-18")] // LocalSystem: its profile is shared with the OS.
    [InlineData("S-1-5-80-0-0-0-0-0")] // another service's SID
    [InlineData(null)] // identity unknown
    public void UnderAnyOtherIdentity_NothingIsTouched(string? sid)
    {
        string key = Plant(KeysFolder, "developer-key");
        string rsa = Plant(RsaFolder(sid ?? DeveloperSid), "developer-rsa-key");
        var sweep = new ServiceProfileKeySweep(sid, _appData, NullLogger.Instance);

        Assert.False(sweep.IsServiceAccount);
        Assert.Equal(KeySweepResult.NotServiceAccount, sweep.Run());
        Assert.True(File.Exists(key));
        Assert.True(File.Exists(rsa));
    }

    [Fact]
    public void ThisProcess_IsNotTheServiceAccount_SoAConsoleRunNeverSweeps()
    {
        // Reads the identity only (no Run): a developer's or CI's key store is never at risk from this test.
        var sweep = ServiceProfileKeySweep.ForCurrentProcess(NullLogger.Instance);

        Assert.False(sweep.IsServiceAccount);
        if (OperatingSystem.IsWindows())
        {
            using WindowsIdentity me = WindowsIdentity.GetCurrent();
            Assert.NotEqual(ServiceProfileKeySweep.AgentServiceSid, me.User?.Value);
        }
        else
        {
            Assert.Equal(KeySweepResult.NotServiceAccount, sweep.Run());
        }
    }

    [Fact]
    public void ARedirectedKeyFolderIsLeftAlone()
    {
        string target = Path.Combine(_appData, "..", "elsewhere");
        string outside = Plant(target, "someone-elses-key");
        Directory.CreateDirectory(Path.GetDirectoryName(KeysFolder)!);
        try
        {
            Directory.CreateSymbolicLink(KeysFolder, Path.GetFullPath(target));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Cannot create a directory link here ({ex.Message}).");
        }

        try
        {
            KeySweepResult result = new ServiceProfileKeySweep(ServiceProfileKeySweep.AgentServiceSid, _appData, NullLogger.Instance).Run();

            Assert.Equal(0, result.Deleted);
            Assert.True(File.Exists(outside));
        }
        finally
        {
            Directory.Delete(KeysFolder);
        }
    }

    [Fact]
    public async Task CreateAsync_SweepsBeforeAnyCredentialIsLoaded()
    {
        // The node is not enrolled, so CreateAsync stops at the first credential check; the sweep has already run.
        await using var node = await ContractNode.CreateAsync(enroll: false, Ct);
        string leaked = Plant(KeysFolder, "leaked-by-a-crash");
        var sweep = new ServiceProfileKeySweep(ServiceProfileKeySweep.AgentServiceSid, _appData, NullLogger.Instance);

        var refused = await Assert.ThrowsAsync<AgentStartupException>(() =>
            AgentProgram.CreateAsync(NewBuilder(node), node.CreateOptions(), sweep, null, null, Ct));

        Assert.Contains("not enrolled", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(leaked));
    }

    [Fact]
    public async Task CreateAsync_UnderADeveloperIdentity_LeavesTheKeysAlone()
    {
        await using var node = await ContractNode.CreateAsync(enroll: false, Ct);
        string key = Plant(KeysFolder, "developer-key");
        var sweep = new ServiceProfileKeySweep(DeveloperSid, _appData, NullLogger.Instance);

        await Assert.ThrowsAsync<AgentStartupException>(() =>
            AgentProgram.CreateAsync(NewBuilder(node), node.CreateOptions(), sweep, null, null, Ct));

        Assert.True(File.Exists(key));
    }

    private static WebApplicationBuilder NewBuilder(ContractNode node)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = node.Root });
        builder.Logging.ClearProviders();
        return builder;
    }

    private static string Plant(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }
}
