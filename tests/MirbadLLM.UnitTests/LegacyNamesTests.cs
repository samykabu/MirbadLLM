using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.HostServices;
using MirbadLLM.Agent.Updates;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;
using MirbadLLM.NodeRuntime.Downloads.Native;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Recovery;
using MirbadLLM.Pki;
using MirbadLLM.Web;
using MirbadLLM.Web.Api;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests;

/// <summary>
/// A deployment and its agents installed before the rename (SparkServe) keep working: configuration, labels, files,
/// backups, protocol headers and update orders written under the old names are still understood (LegacyNames).
/// </summary>
public sealed class LegacyNamesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mirbadllm-legacy-" + Guid.NewGuid().ToString("N"));

    public LegacyNamesTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ---- 1. encrypted data ----

    [Fact]
    public void DataProtectionNames_KeepTheirPreRenameValues()
    {
        // They key the ciphertext of every deployed tokens.json, backup-destination.json and login cookie.
        Assert.Equal("spark-serve", LegacyProtectionPurposes.ApplicationName);
        Assert.Equal("SparkServe.SavedTokens.v1", LegacyProtectionPurposes.SavedTokens);
        Assert.Equal("SparkServe.BackupDestination.v1", LegacyProtectionPurposes.BackupDestination);
    }

    [Fact]
    public async Task ASavedTokenEncryptedBeforeTheRename_StillDecrypts()
    {
        var keys = new DirectoryInfo(Path.Combine(_root, "keys"));
        // What the pre-rename master wrote: the same key ring, application name and purpose.
        var before = DataProtectionProvider.Create(keys, b => b.SetApplicationName("spark-serve"));
        var store = new JsonTokenStore(Options.Create(new MirbadLLMOptions { DataPath = _root }),
            DataProtectionProvider.Create(keys, b => b.SetApplicationName(LegacyProtectionPurposes.ApplicationName)), TimeProvider.System);
        await store.AddAsync("hf", "hf_secret_value_1234", TestContext.Current.CancellationToken);
        string file = await File.ReadAllTextAsync(Path.Combine(_root, "tokens.json"), TestContext.Current.CancellationToken);
        string cipher = System.Text.Json.Nodes.JsonNode.Parse(file)!["Tokens"]![0]!["Protected"]!.GetValue<string>();

        Assert.Equal("hf_secret_value_1234", before.CreateProtector("SparkServe.SavedTokens.v1").Unprotect(cipher));
        store.Dispose();
    }

    // ---- 2. configuration ----

    [Fact]
    public void LegacyKeys_AreRead_AtTheirSourcesPrecedence_AndTheNewKeyWinsInTheSameSource()
    {
        var config = new ConfigurationManager();
        // appsettings.json, then the environment.
        config.AddInMemoryCollection(new Dictionary<string, string?> { ["MirbadLLM:DataPath"] = "/data", ["MirbadLLM:VllmImage"] = "json-image" });
        config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SparkServe:DataPath"] = "/legacy-env-data",
            ["SparkServe:ApiKey"] = "legacy-key",
            ["MirbadLLM:ApiKey"] = "new-key",
            ["SparkServe:Backup:Directory"] = "/backups",
        });

        var legacy = LegacyConfiguration.Apply(config);

        Assert.Equal("/legacy-env-data", config["MirbadLLM:DataPath"]); // a legacy env var beats appsettings.json
        Assert.Equal("new-key", config["MirbadLLM:ApiKey"]);           // both in one source: the new key wins
        Assert.Equal("/backups", config["MirbadLLM:Backup:Directory"]); // nested sections too
        Assert.Equal("json-image", config["MirbadLLM:VllmImage"]);
        Assert.Equal(["SparkServe:ApiKey", "SparkServe:Backup:Directory", "SparkServe:DataPath"], legacy);
        Assert.Equal("/legacy-env-data", config.GetSection(MirbadLLMOptions.SectionName).Get<MirbadLLMOptions>()!.DataPath);
    }

    [Fact]
    public void ANewKeyInALaterSource_BeatsALegacyKeyInAnEarlierOne()
    {
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?> { ["SparkServe:DataPath"] = "/old" });
        config.AddInMemoryCollection(new Dictionary<string, string?> { ["MirbadLLM:DataPath"] = "/new" });

        LegacyConfiguration.Apply(config);

        Assert.Equal("/new", config["MirbadLLM:DataPath"]);
    }

    [Fact]
    public void NoLegacyKeys_NothingChanges()
    {
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?> { ["MirbadLLM:DataPath"] = "/data" });
        int sources = ((IConfigurationRoot)config).Providers.Count();
        Assert.Empty(LegacyConfiguration.Apply(config));
        Assert.Equal(sources, ((IConfigurationRoot)config).Providers.Count());
    }

    // ---- 3. containers ----

    [Fact]
    public void LegacyLabels_GetTheirCurrentNames_AndFiltersTheirLegacyTwin()
    {
        var labels = LegacyLabels.Normalize(new Dictionary<string, string>
        {
            ["spark-serve.model"] = "m1",
            ["spark-serve.download.folder"] = "f",
            ["mirbadllm.port"] = "8102",
            ["spark-serve.port"] = "9999",
            ["com.docker.compose.project"] = "x",
        });

        Assert.Equal("m1", labels[ContainerLabels.Model]);
        Assert.Equal("f", labels["mirbadllm.download.folder"]);
        Assert.Equal("8102", labels[ContainerLabels.Port]); // a current label wins
        Assert.Equal("x", labels["com.docker.compose.project"]);
        Assert.Equal("spark-serve.model=m1", LegacyLabels.ToLegacyFilter("mirbadllm.model=m1"));
        Assert.Null(LegacyLabels.ToLegacyFilter("com.docker.compose.project"));
    }

    [Theory]
    [InlineData("spark-serve", null, true)]
    [InlineData("spark-serve-agent", null, true)]
    [InlineData("ss-m1", "spark-serve.model", true)]
    [InlineData("ss-vllm", null, false)]
    [InlineData("openviking", null, false)]
    public void TheAgent_NeverManagesThePreRenameContainers(string name, string? label, bool denied)
    {
        var labels = LegacyLabels.Normalize(label is null ? null : new Dictionary<string, string> { [label] = "m1" });
        Assert.Equal(denied, HostContainerRules.IsDenied(name, labels, new AgentHostServicesOptions()));
    }

    // ---- 4. files ----

    [Fact]
    public void ABackupWrittenBeforeTheRename_IsListed_Parsed_AndItsSecretsDecrypt()
    {
        // Written by the pre-rename BackupService (commit d79f144) with the passphrase below.
        string fixture = HostAgent.HostAgentFixtures.FindRepoFile(Path.Combine("tests", "MirbadLLM.UnitTests", "Recovery", "Fixtures", "spark-serve-backup-dgx-20261102-195150.json"));
        string dir = Path.Combine(_root, "backups");
        Directory.CreateDirectory(dir);
        File.Copy(fixture, Path.Combine(dir, Path.GetFileName(fixture)));

        var directory = new BackupDirectory(Options.Create(new BackupOptions { Directory = dir }));
        var listed = Assert.Single(directory.List());
        Assert.Equal(Path.GetFileName(fixture), listed.Name);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 19, 51, 50, TimeSpan.Zero), listed.CreatedAt);
        Assert.NotNull(directory.Resolve(listed.Name));

        var document = BackupService.Parse(File.ReadAllBytes(fixture));
        Assert.Equal("alpha", Assert.Single(document.Models).Id);
        var secrets = BackupService.DecryptSecrets(document, "correct horse battery staple");
        Assert.Equal("laptop-cline", Assert.Single(secrets!.ApiKeys).Name);
        Assert.Throws<BackupPassphraseException>(() => BackupService.DecryptSecrets(document, "wrong"));
    }

    [Fact]
    public void ADownloadManifestWrittenBeforeTheRename_IsRead_AndReplacedOnTheNextWrite()
    {
        var manifest = new DownloadManifest
        {
            RepoId = "owner/repo",
            CommitSha = new string('a', 40),
            AllowPatterns = ["*.json"],
            Files = [],
        };
        manifest.Write(_root);
        File.Move(DownloadManifest.PathIn(_root), Path.Combine(_root, ".spark-serve-download.json"));

        Assert.Equal("owner/repo", DownloadManifest.TryRead(_root)!.RepoId);
        manifest.Write(_root);
        Assert.True(File.Exists(DownloadManifest.PathIn(_root)));
        Assert.False(File.Exists(Path.Combine(_root, ".spark-serve-download.json")));
    }

    // ---- 5. master <-> agent ----

    [Fact]
    public void TheMaster_SendsBothHeaderNames_AndReadsAPreRenameAgentsAnswer()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://agent/x");
        request.Headers.TryAddWithoutValidation(Protocol.MasterHeader, "m/1/b");
        request.Headers.TryAddWithoutValidation(Protocol.ProtocolHeader, "1");
        MirbadLLM.Orchestration.Agents.GenerationHandler.AddLegacyHeaders(request);
        Assert.Equal("m/1/b", request.Headers.GetValues("SparkServe-Master").Single());
        Assert.Equal("1", request.Headers.GetValues("SparkServe-Protocol").Single());

        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.Conflict);
        response.Headers.TryAddWithoutValidation("SparkServe-Agent-Error", ErrorCodes.ServerBusy);
        MirbadLLM.Orchestration.Agents.GenerationHandler.AcceptLegacyHeaders(response);
        Assert.Equal(ErrorCodes.ServerBusy, response.Headers.GetValues(Protocol.AgentErrorHeader).Single());
    }

    [Fact]
    public async Task TheAgent_ReadsAPreRenameMastersHeaders_AndAnswersUnderBothNames()
    {
        var context = new DefaultHttpContext();
        var started = new StartingFeature();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(started);
        context.Request.Headers["SparkServe-Master"] = "m/1/b";
        context.Request.Headers["SparkServe-Protocol"] = "1";
        context.Request.Headers[Protocol.RequestTimeHeader] = "now";
        context.Request.Headers["SparkServe-Request-Time"] = "ignored";

        MirbadLLM.Agent.Hosting.AgentRequestMiddleware.AcceptLegacyHeaders(context);
        Assert.Equal("m/1/b", context.Request.Headers[Protocol.MasterHeader].ToString());
        Assert.Equal("1", context.Request.Headers[Protocol.ProtocolHeader].ToString());
        Assert.Equal("now", context.Request.Headers[Protocol.RequestTimeHeader].ToString()); // a current header wins

        context.Response.Headers[Protocol.AgentErrorHeader] = ErrorCodes.ServerBusy;
        await started.StartAsync();
        Assert.Equal(ErrorCodes.ServerBusy, context.Response.Headers["SparkServe-Agent-Error"].ToString());
    }

    [Fact]
    public void ACertificateOfEitherTrustDomain_IsAccepted()
    {
        using var ca = CertificateAuthority.Create();
        using var issued = ca.IssueMasterClientCertificate();
        Assert.Equal(Protocol.IssuedMasterSpiffeId, CertificateSans.GetSingleUri(issued.PublicCertificate));
        var validator = PeerValidator.ForMasterClient(TrustAnchorSet.Create([ca.Certificate]));
        Assert.True(validator.Validate(issued.PublicCertificate).IsValid);
    }

    // ---- 6. agent updates ----

    [Fact]
    public async Task TheWindowsHelper_InstallsABundleWithOnlyThePreRenameFolder()
    {
        using var ca = CertificateAuthority.Create();
        string trust = Path.Combine(_root, "trust");
        string spool = Path.Combine(_root, "spool");
        Directory.CreateDirectory(trust);
        Directory.CreateDirectory(spool);
        File.WriteAllText(Path.Combine(trust, "ca.pem"), ca.CertificatePem);
        File.WriteAllText(Path.Combine(trust, "node-uid"), "uid-1");
        File.WriteAllText(Path.Combine(trust, "rid"), "win-x64");
        byte[] zip = Zip("spark-serve-agent-v9-win-x64/install-agent.ps1");
        var now = DateTimeOffset.UtcNow;
        string order = new AgentUpdateOrder("uid-1", "win-x64", "v9", Convert.ToHexStringLower(SHA256.HashData(zip)), zip.Length, now, now.AddHours(1)).Format();
        File.WriteAllBytes(Path.Combine(spool, "bundle.zip"), zip);
        File.WriteAllText(Path.Combine(spool, "order"), order);
        File.WriteAllText(Path.Combine(spool, "order.sig"), Convert.ToBase64String(ca.SignData(Encoding.UTF8.GetBytes(order))));
        string? ran = null;

        var result = await new UpdateHelperCore(spool, trust, TimeProvider.System).RunAsync((folder, _, _) =>
        {
            ran = Path.GetFileName(folder);
            return Task.FromResult((0, ""));
        }, TestContext.Current.CancellationToken);

        Assert.Equal(0, result!.ExitCode);
        Assert.Equal("spark-serve-agent-v9-win-x64", ran);
    }

    [Fact]
    public void TheMastersBundleCatalog_IgnoresThePreRenameAliasFolder()
    {
        string dir = Path.Combine(_root, "bundles");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "win-x64.zip"), Zip(
            "mirbadllm-agent-v9-win-x64/mirbadllm-agent.exe", "mirbadllm-agent-v9-win-x64/install-agent.ps1",
            "mirbadllm-agent-v9-win-x64/agent.windows.example.json", "mirbadllm-agent-v9-win-x64/llama-cpp.lock.json",
            "mirbadllm-agent-v9-win-x64/BUILD-INFO", "spark-serve-agent-v9-win-x64/install-agent.ps1",
            "spark-serve-agent-v9-win-x64/llama-cpp.lock.json", "spark-serve-agent-v9-win-x64/agent.windows.example.json"));

        var bundle = Assert.Single(new AgentBundleCatalog(dir, NullLogger<AgentBundleCatalog>.Instance).Bundles);
        Assert.Equal(("win-x64", "mirbadllm-agent-v9-win-x64"), (bundle.Rid, bundle.Folder));
    }

    private static byte[] Zip(params string[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string name in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(name.EndsWith("BUILD-INFO", StringComparison.Ordinal) ? "revision=v9\n" : "x");
            }
        }

        return stream.ToArray();
    }

    /// <summary>A response feature whose OnStarting callbacks the test runs.</summary>
    private sealed class StartingFeature : Microsoft.AspNetCore.Http.Features.HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _starting = [];

        public override void OnStarting(Func<object, Task> callback, object state) => _starting.Add((callback, state));

        public async Task StartAsync()
        {
            foreach (var (callback, state) in _starting)
            {
                await callback(state);
            }
        }
    }
}
