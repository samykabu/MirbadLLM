using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Agent.Operations;
using MirbadLLM.Agent.Updates;
using MirbadLLM.Core;
using MirbadLLM.Pki;

namespace MirbadLLM.UnitTests.Agent;

#pragma warning disable CA2000 // test fixtures own what they create

/// <summary>
/// Pushed agent updates (docs/multinode/agent-updates.md): the signed order's format, the agent's checks and hand-over to
/// the spool, the watcher that closes the operation, and the real root helper script (bash + openssl) installing a signed
/// bundle and refusing a forged or tampered one.
/// </summary>
public sealed class AgentUpdateTests : IDisposable
{
    private const string NodeUid = "pc-linux-uid";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mirbadllm-update-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    public AgentUpdateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            d.Dispose();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    // ---- The order ----

    [Fact]
    public void TheOrderTextRoundTripsAndIsExactlyTheShellReadableForm()
    {
        var order = new AgentUpdateOrder(NodeUid, "linux-x64", "826bc75fc222", new string('a', 64), 41234567,
            DateTimeOffset.FromUnixTimeSeconds(1792238400), DateTimeOffset.FromUnixTimeSeconds(1792242000));

        string text = order.Format();

        // The pre-rename header: the helper installed on a pre-rename node accepts only it (AgentUpdateOrder.WrittenHeader).
        Assert.Equal(
            "spark-serve-agent-update 1\nnode-uid=pc-linux-uid\nrid=linux-x64\nversion=826bc75fc222\nsha256=" + new string('a', 64) +
            "\nsize=41234567\nissued=1792238400\nexpires=1792242000\n", text);
        Assert.Equal(order, AgentUpdateOrder.Parse(text));
        Assert.Null(AgentUpdateOrder.Parse(text.Replace("size=", "size2=", StringComparison.Ordinal)));
        Assert.Null(AgentUpdateOrder.Parse(text + "extra=1\n"));
        Assert.Equal(order, AgentUpdateOrder.Parse(text.Replace("spark-serve-agent-update 1", AgentUpdateOrder.Header, StringComparison.Ordinal)));
        Assert.Null(AgentUpdateOrder.Parse(text.Replace("spark-serve-agent-update 1", "other-update 1", StringComparison.Ordinal)));
        Assert.Throws<ArgumentException>(() => (order with { Version = "x\nrid=evil" }).Format());
    }

    [Fact]
    public void TheCaSignsTheOrderSoTheHelpersOpensslCheckCanVerifyIt()
    {
        using var ca = CertificateAuthority.Create();
        byte[] data = Encoding.UTF8.GetBytes("mirbadllm-agent-update 1\n");

        byte[] signature = ca.SignData(data);

        using var key = ca.Certificate.GetECDsaPublicKey()!;
        Assert.True(key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        Assert.Equal(0x30, signature[0]); // DER SEQUENCE: what openssl dgst -verify expects
    }

    // ---- The agent: checks and hand-over ----

    [Fact]
    public async Task AValidOrderIsDownloadedFromTheSenderVerifiedAndHandedToTheSpoolSignatureLast()
    {
        byte[] bundle = Encoding.UTF8.GetBytes(new string('b', 4096));
        var (service, log, handler) = Service(bundle);
        var request = Request(bundle);

        var result = await service.SubmitAsync(request, Master.ToHeaderValue(), AgentJsonOf(request), IPAddress.Parse("192.0.2.191"), TestContext.Current.CancellationToken);

        Assert.Equal(202, result.StatusCode);
        await WaitForAsync(() => File.Exists(Path.Combine(Spool, AgentUpdateOptions.SignatureFile)));
        Assert.Equal("http://192.0.2.191:8080/agent/bundles/" + AgentUpdateService.ThisRid + (AgentUpdateService.ThisRid.StartsWith("win", StringComparison.Ordinal) ? ".zip" : ".tar.gz"),
            handler.Requested!.AbsoluteUri);
        Assert.Equal(bundle, File.ReadAllBytes(Path.Combine(Spool, AgentUpdateOptions.BundleFile(AgentUpdateService.ThisRid))));
        Assert.Equal(request.Order, File.ReadAllText(Path.Combine(Spool, AgentUpdateOptions.OrderFile)));
        Assert.Equal(request.OperationId, File.ReadAllText(Path.Combine(Spool, AgentUpdateOptions.OperationFile)).Trim());
        Assert.Equal(OperationStages.Dispatched, log.Find(request.OperationId)!.Stage);
    }

    [Fact]
    public async Task ABundleThatDoesNotMatchTheOrderFailsTheOperationAndNothingIsHandedOver()
    {
        byte[] bundle = Encoding.UTF8.GetBytes(new string('b', 4096));
        var (service, log, _) = Service(Encoding.UTF8.GetBytes(new string('c', 4096)));
        var request = Request(bundle);

        await service.SubmitAsync(request, Master.ToHeaderValue(), AgentJsonOf(request), IPAddress.Loopback, TestContext.Current.CancellationToken);

        await WaitForAsync(() => log.Find(request.OperationId)!.State == OperationState.Failed);
        Assert.Contains("SHA-256", log.Find(request.OperationId)!.Error!.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Spool, AgentUpdateOptions.SignatureFile)));
    }

    [Theory]
    [InlineData("other-node", null, null)]
    [InlineData(null, "linux-riscv", null)]
    [InlineData(null, null, "/etc/passwd")]
    public void AnOrderForAnotherNodeBundleOrPlaceIsRefusedBeforeAnything(string? uid, string? rid, string? path)
    {
        var (service, _, _) = Service([]);
        var request = Request(new byte[10], uid ?? NodeUid, rid ?? AgentUpdateService.ThisRid) with { BundlePath = path ?? "/agent/bundles/x.tar.gz" };

        Assert.NotNull(service.Validate(request, IPAddress.Loopback));
        Assert.NotNull(service.Validate(Request(new byte[10]), master: null));
        Assert.Null(service.Validate(Request(new byte[10]), IPAddress.Loopback));
    }

    [Fact]
    public async Task WithoutTheHelperTheNodeRefusesAndDoesNotReportTheFeature()
    {
        var (service, _, _) = Service([], helper: false);
        var request = Request(new byte[10]);

        var result = await service.SubmitAsync(request, Master.ToHeaderValue(), AgentJsonOf(request), IPAddress.Loopback, TestContext.Current.CancellationToken);
        var caps = await new AgentUpdateContributor(service.Options).ContributeAsync(Caps(), TestContext.Current.CancellationToken);

        Assert.Equal(501, result.StatusCode);
        Assert.DoesNotContain(AgentFeatures.AgentUpdate, caps.Features);
    }

    // ---- The watcher ----

    [Fact]
    public async Task AHelperFailureClosesTheUpdateWithTheInstallersLastLine()
    {
        var (service, log, _) = Service([]);
        var request = Request(new byte[10]);
        await AcceptAsync(log, request);
        Directory.CreateDirectory(Spool);
        File.WriteAllText(Path.Combine(Spool, AgentUpdateOptions.ResultFile),
            $"operation={request.OperationId}\nexit=1\nversion=826bc75fc222\nlog:\nThe installer failed.\n==> rolled back to the previous agent\n");

        await new AgentUpdateWatcher(service, log, TimeProvider.System, NullLogger<AgentUpdateWatcher>.Instance).CheckAsync(TestContext.Current.CancellationToken);

        var status = log.Find(request.OperationId)!;
        Assert.Equal(OperationState.Failed, status.State);
        Assert.Contains("rolled back to the previous agent", status.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheUpdateSucceedsWhenThisAgentRunsTheOrderedBuild()
    {
        var (service, log, _) = Service([], version: "1.0.0+826bc75fc222");
        var request = Request(new byte[10]);
        await AcceptAsync(log, request);

        await new AgentUpdateWatcher(service, log, TimeProvider.System, NullLogger<AgentUpdateWatcher>.Instance).CheckAsync(TestContext.Current.CancellationToken);

        var status = log.Find(request.OperationId)!;
        Assert.Equal(OperationState.Succeeded, status.State);
        Assert.Equal("1.0.0+826bc75fc222", status.Result!.Value.Deserialize<AgentUpdateResult>(AgentJson.Options)!.AgentVersion);
    }

    [Fact]
    public void TheHelpersReportIsReadLineByLine()
    {
        var result = HelperResult.Parse("operation=op-1\nexit=3\nversion=v\nlog:\nThe order is not signed by this node's master: refused.\n");

        Assert.Equal(("op-1", 3, "v"), (result.OperationId, result.ExitCode, result.Version));
        Assert.Equal("The order is not signed by this node's master: refused.", result.Log);
    }

    // ---- The root helper script, for real (Linux and macOS) ----

    [Fact]
    public async Task TheHelperInstallsASignedBundleAndRefusesAForgedOrderOrATamperedBundle()
    {
        if (OperatingSystem.IsWindows() || !Tool("openssl") || !Tool("tar"))
        {
            return; // the helper runs on Linux and macOS nodes
        }

        using var ca = CertificateAuthority.Create();
        using var forger = CertificateAuthority.Create();
        string trust = Path.Combine(_root, "trust");
        string spool = Path.Combine(_root, "spool");
        Directory.CreateDirectory(trust);
        File.WriteAllText(Path.Combine(trust, "ca.pem"), ca.CertificatePem);
        File.WriteAllText(Path.Combine(trust, "node-uid"), NodeUid + "\n");
        File.WriteAllText(Path.Combine(trust, "rid"), "linux-x64\n");

        // A bundle whose installer only records that it ran (and how): the real one would upgrade the agent.
        string marker = Path.Combine(_root, "installed");
        byte[] bundle = await BundleAsync("826bc75fc222", "linux-x64", $"echo \"$1 helper=$MIRBADLLM_UPDATE_HELPER\" > '{marker}'");

        var signed = await RunHelperAsync(ca, spool, trust, bundle, "op-good");
        Assert.Equal("upgrade helper=1", File.ReadAllText(marker).Trim());
        Assert.Equal((0, "op-good"), (signed.ExitCode, signed.OperationId));
        Assert.False(File.Exists(Path.Combine(spool, "order.sig"))); // consumed

        File.Delete(marker);
        var forged = await RunHelperAsync(forger, spool, trust, bundle, "op-forged");
        Assert.Equal(3, forged.ExitCode);
        Assert.Contains("not signed by this node's master", forged.Log, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));

        byte[] other = await BundleAsync("826bc75fc222", "linux-x64", "echo tampered");
        var tampered = await RunHelperAsync(ca, spool, trust, bundle, "op-tampered", bundleOnDisk: other);
        Assert.Equal(4, tampered.ExitCode);
        Assert.False(File.Exists(marker));

        var otherNode = await RunHelperAsync(ca, spool, trust, bundle, "op-other", uid: "someone-else");
        Assert.Equal(3, otherNode.ExitCode);
        Assert.Contains("another node", otherNode.Log, StringComparison.Ordinal);
    }

    // ---- The Windows helper's checks (UpdateHelperCore, pure .NET: runs on every OS) ----

    [Fact]
    public async Task TheWindowsHelperRunsTheInstallerOnlyForASignedMatchingOrder()
    {
        using var ca = CertificateAuthority.Create();
        using var forger = CertificateAuthority.Create();
        string trust = Path.Combine(_root, "wtrust");
        string spool = Path.Combine(_root, "wspool");
        Directory.CreateDirectory(trust);
        File.WriteAllText(Path.Combine(trust, "ca.pem"), ca.CertificatePem);
        File.WriteAllText(Path.Combine(trust, "node-uid"), NodeUid + Environment.NewLine);
        File.WriteAllText(Path.Combine(trust, "rid"), "win-x64");
        byte[] zip = Zip("826bc75fc222");
        var installs = new List<string>();
        Task<(int, string)> Install(string folder, string bundle, string sha)
        {
            installs.Add(Path.GetFileName(folder));
            return Task.FromResult((0, "==> Agent 826bc75fc222 is running."));
        }

        var core = new UpdateHelperCore(spool, trust, TimeProvider.System);

        var ok = await RunCoreAsync(core, ca, spool, zip, NodeUid, Install);
        Assert.Equal((0, "op-w"), (ok!.ExitCode, ok.OperationId));
        Assert.Equal(["mirbadllm-agent-826bc75fc222-win-x64"], installs);
        Assert.Contains("is running", File.ReadAllText(Path.Combine(spool, "result")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(spool, "order.sig")));

        Assert.Equal(3, (await RunCoreAsync(core, forger, spool, zip, NodeUid, Install))!.ExitCode);
        Assert.Equal(3, (await RunCoreAsync(core, ca, spool, zip, "someone-else", Install))!.ExitCode);
        Assert.Equal(4, (await RunCoreAsync(core, ca, spool, zip, NodeUid, Install, onDisk: Zip("other")))!.ExitCode);
        Assert.Single(installs); // none of the refused orders ran the installer
        Assert.Null(await core.RunAsync(Install, TestContext.Current.CancellationToken)); // no order: nothing to do
    }

    private static async Task<HelperResult?> RunCoreAsync(UpdateHelperCore core, CertificateAuthority signer, string spool, byte[] bundle, string uid,
        Func<string, string, string, Task<(int, string)>> install, byte[]? onDisk = null)
    {
        Directory.CreateDirectory(spool);
        var now = DateTimeOffset.UtcNow;
        string order = new AgentUpdateOrder(uid, "win-x64", "826bc75fc222", Convert.ToHexStringLower(SHA256.HashData(bundle)), bundle.Length, now, now.AddHours(1)).Format();
        File.WriteAllBytes(Path.Combine(spool, "bundle.zip"), onDisk ?? bundle);
        File.WriteAllText(Path.Combine(spool, "operation"), "op-w" + Environment.NewLine);
        File.WriteAllText(Path.Combine(spool, "order"), order);
        File.WriteAllText(Path.Combine(spool, "order.sig"), Convert.ToBase64String(signer.SignData(Encoding.UTF8.GetBytes(order))));
        return await core.RunAsync(install, TestContext.Current.CancellationToken);
    }

    private static byte[] Zip(string version)
    {
        using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry($"mirbadllm-agent-{version}-win-x64/install-agent.ps1");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("# fake installer " + version);
        }

        return stream.ToArray();
    }

    // ---- helpers ----

    private static readonly MasterTriple Master = new("master-1", 1, "boot-1");

    private string Spool => Path.Combine(_root, "state", "update");

    private (AgentUpdateService Service, OperationLog Log, BundleHandler Handler) Service(byte[] served, bool helper = true, string version = "1.0.0+2d71c7d97d84")
    {
        string marker = Path.Combine(_root, "helper");
        if (helper)
        {
            File.WriteAllText(marker, "");
        }

        var log = new OperationLog(new OperationLogOptions { StateDirectory = Path.Combine(_root, "state"), BootId = "boot-1", NodeUid = NodeUid }, TimeProvider.System);
        _disposables.Add(log);
        var handler = new BundleHandler(served);
        var http = new HttpClient(handler);
        _disposables.Add(http);
        var service = new AgentUpdateService(
            new AgentUpdateOptions { Spool = Spool, HelperMarker = marker, StartWindowsHelper = false },
            log,
            new FakeFencing(),
            new FakeCredentials(),
            new AgentBootInfo(TimeProvider.System, version),
            http,
            NullLogger<AgentUpdateService>.Instance);
        return (service, log, handler);
    }

    private static AgentUpdateRequest Request(byte[] bundle, string uid = NodeUid, string? rid = null)
    {
        rid ??= AgentUpdateService.ThisRid;
        string sha = Convert.ToHexStringLower(SHA256.HashData(bundle));
        var now = DateTimeOffset.UtcNow;
        var order = new AgentUpdateOrder(uid, rid, "826bc75fc222", sha, bundle.Length, now, now.AddHours(1));
        return new AgentUpdateRequest
        {
            OperationId = "op-" + Guid.NewGuid().ToString("N")[..8],
            IssuedAt = now,
            ExpiresAt = now.AddDays(1),
            Rid = rid,
            Version = "826bc75fc222",
            Sha256 = sha,
            Size = bundle.Length,
            BundlePath = "/agent/bundles/" + rid + (rid.StartsWith("win", StringComparison.Ordinal) ? ".zip" : ".tar.gz"),
            BundlePort = 8080,
            Order = order.Format(),
            Signature = "MEUCIQ==",
        };
    }

    private static JsonElement AgentJsonOf(AgentUpdateRequest request) => JsonSerializer.SerializeToElement(request, AgentJson.Options);

    private static async Task AcceptAsync(OperationLog log, AgentUpdateRequest request)
    {
        await log.AcceptAsync(new OperationSubmission
        {
            OperationId = request.OperationId,
            Kind = OperationKinds.AgentUpdate,
            Target = request.Version,
            IssuedAt = request.IssuedAt,
            ExpiresAt = request.ExpiresAt,
            Master = Master,
            Payload = AgentJsonOf(request),
        });
        await log.RecordStageAsync(request.OperationId, new OperationStageUpdate { Stage = OperationStages.Dispatched, State = OperationState.Running });
    }

    private static NodeCapabilities Caps() => new()
    {
        Protocol = Protocol.Range,
        AgentVersion = "1.0.0",
        Os = new NodeOsInfo("linux", "Ubuntu"),
        Arch = "amd64",
        Memory = new NodeMemoryInfo(MemoryModel.Discrete, 32),
    };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "timed out waiting");
    }

    private static bool Tool(string name)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("/usr/bin/env", ["which", name]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private async Task<byte[]> BundleAsync(string version, string rid, string installerBody)
    {
        string stage = Path.Combine(_root, "stage-" + Guid.NewGuid().ToString("N")[..6]);
        string folder = Path.Combine(stage, $"mirbadllm-agent-{version}-{rid}");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "install-agent.sh"), "#!/bin/bash\n" + installerBody + "\n");
        string tarball = stage + ".tar.gz";
        await RunAsync("tar", ["-czf", tarball, "-C", stage, Path.GetFileName(folder)]);
        return File.ReadAllBytes(tarball);
    }

    private static async Task<HelperResult> RunHelperAsync(CertificateAuthority signer, string spool, string trust, byte[] bundle, string operation,
        byte[]? bundleOnDisk = null, string uid = NodeUid)
    {
        Directory.CreateDirectory(spool);
        File.Delete(Path.Combine(spool, "result"));
        var now = DateTimeOffset.UtcNow;
        string order = new AgentUpdateOrder(uid, "linux-x64", "826bc75fc222", Convert.ToHexStringLower(SHA256.HashData(bundle)), bundle.Length, now, now.AddHours(1)).Format();
        File.WriteAllBytes(Path.Combine(spool, "bundle.tar.gz"), bundleOnDisk ?? bundle);
        File.WriteAllText(Path.Combine(spool, "operation"), operation + "\n");
        File.WriteAllText(Path.Combine(spool, "order"), order);
        File.WriteAllText(Path.Combine(spool, "order.sig"), Convert.ToBase64String(signer.SignData(Encoding.UTF8.GetBytes(order))) + "\n");

        string helper = MirbadLLM.UnitTests.HostAgent.HostAgentFixtures.FindRepoFile(Path.Combine("deploy", "agent", "agent-update-helper.sh"));
        await RunAsync("bash", [helper, spool, trust, Environment.UserName]);
        return HelperResult.Parse(File.ReadAllText(Path.Combine(spool, "result")));
    }

    private static async Task RunAsync(string file, string[] args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        string err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, $"{file} {string.Join(' ', args)} exited {p.ExitCode}: {err}");
    }

    private sealed class BundleHandler(byte[] body) : HttpMessageHandler
    {
        public Uri? Requested { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private sealed class FakeCredentials : IAgentCredentials
    {
        public bool IsEnrolled => true;

        public string NodeId => "pc-linux";

        public string NodeUid => AgentUpdateTests.NodeUid;

        public X509Certificate2 ServerCertificate => throw new NotSupportedException();

        public IReadOnlyList<X509Certificate2> TrustAnchors => [];

        public long Version => 1;
    }
}
