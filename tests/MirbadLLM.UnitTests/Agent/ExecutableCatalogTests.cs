using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Runtimes;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Runtimes.Process;
using MirbadLLM.UnitTests.Agent.Fakes;

#pragma warning disable CA1707, xUnit1051 // underscore test names; hand-written fakes ignore the token.

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// MN-222: <see cref="ExecutableCatalog"/> over a temp version folder with a written <c>manifest.json</c> (the shape MN-216's
/// installer writes into <c>runtimes\llama.cpp\{tag}\</c>, listing every file but not itself): every listed file hashed (the
/// 9-byte launcher and <c>llama-server-impl.dll</c> alike), an unlisted <c>.exe</c>/<c>.dll</c> is a mismatch, the manifest
/// is the one expected extra file, a tampered version is unusable (launch 422 <c>executable_unverified</c>) while others keep
/// working, the first verification is not done before <see cref="IExecutableCatalog.RefreshAsync"/> (Resolve answers 422
/// until then), and a change of size or mtime is re-hashed at the next Resolve. Plus the two agent contributors
/// (<c>runtime.llamacpp.process</c> with <c>RuntimeCapability.Versions</c>, <c>executable_unverified</c>).
/// </summary>
public sealed class ExecutableCatalogTests : IDisposable
{
    private const string Tag = "b11476";
    private readonly List<LlamaCppVersionFolder> _folders = [];

    public void Dispose()
    {
        foreach (var folder in _folders)
        {
            folder.Dispose();
        }
    }

    // ------------------------------------------------------------ verified, first verification in the background

    [Fact]
    public async Task VerifiedVersion_ResolvesItsPathFolderAndCuda_AsTheDefault_OnlyAfterTheFirstVerification()
    {
        var folder = NewFolder();
        var catalog = folder.Catalog();

        // Nothing is hashed at construction: the first launch before the background hash is 422, never a blocking hash.
        var early = Assert.Throws<AgentException>(() => catalog.Resolve(ExecutableNames.LlamaServer, Tag));
        Assert.Equal(422, early.StatusCode);
        Assert.Equal(ErrorCodes.ExecutableUnverified, early.Code);
        Assert.Contains(ExecutableCatalog.NotVerifiedYet, early.Message, StringComparison.Ordinal);
        await catalog.WhenIdleAsync(); // Resolve started the verification itself (nothing else had)

        await catalog.RefreshAsync(TestContext.Current.CancellationToken);
        var resolved = catalog.Resolve(ExecutableNames.LlamaServer, version: null);
        Assert.Equal(new ResolvedExecutable(ExecutableNames.LlamaServer, Tag, folder.ExecutablePath, folder.Folder, "13.4"), resolved);
        Assert.Equal(resolved, catalog.Resolve(ExecutableNames.LlamaServer, Tag));

        var state = Assert.Single(catalog.List());
        Assert.Equal(new ExecutableVersionState(ExecutableNames.LlamaServer, Tag, "13.4", Verified: true, IsDefault: true, Problem: null), state);
    }

    [Fact]
    public async Task Verifier_RunsTheFirstFullHashInTheBackground()
    {
        var folder = NewFolder();
        var catalog = folder.Catalog();
        using var verifier = new ExecutableCatalogVerifier(catalog);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await verifier.StartAsync(cts.Token);
        await verifier.ExecuteTask!.WaitAsync(cts.Token);

        Assert.True(Assert.Single(catalog.List()).Verified);
        await verifier.StopAsync(CancellationToken.None);
    }

    // ------------------------------------------------------------ tampering

    [Theory]
    [InlineData("llama-server-impl.dll")]
    [InlineData("llama.dll")]
    [InlineData("ggml-cuda.dll")]
    [InlineData("llama-server.exe")]
    public async Task TamperedListedFile_SameSize_MakesTheVersionUnusable_AtTheFirstVerification(string file)
    {
        var folder = NewFolder();
        folder.Tamper(file, keepTime: false);
        var catalog = folder.Catalog();

        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        var state = Assert.Single(catalog.List());
        Assert.False(state.Verified);
        Assert.Equal($"{file}: hash mismatch", state.Problem);
        AssertUnverified(() => catalog.Resolve(ExecutableNames.LlamaServer, Tag), file);
    }

    [Theory]
    [InlineData("llama-server-impl.dll")]
    [InlineData("cudart64_13.dll")]
    public async Task TamperingAfterVerification_IsReHashedAtTheNextResolve_AndRefused(string file)
    {
        var folder = NewFolder();
        var catalog = folder.Catalog();
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);
        catalog.Resolve(ExecutableNames.LlamaServer, Tag);

        folder.Tamper(file, keepTime: false); // size kept, mtime changes: the change check catches it

        AssertUnverified(() => catalog.Resolve(ExecutableNames.LlamaServer, Tag), file);
        Assert.Equal($"{file}: hash mismatch", Assert.Single(catalog.List()).Problem);

        // Restored (a reinstall): the next Resolve hashes it again and the version works again.
        folder.Restore(file);
        Assert.Equal(folder.ExecutablePath, catalog.Resolve(ExecutableNames.LlamaServer, Tag).Path);
    }

    [Fact]
    public async Task TamperingThatKeepsSizeAndTime_IsFoundByTheNextFullRefresh_NotByTheChangeCheck()
    {
        var folder = NewFolder();
        var catalog = folder.Catalog();
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        folder.Tamper("llama-server-impl.dll", keepTime: true);
        catalog.Resolve(ExecutableNames.LlamaServer, Tag); // metadata unchanged: no re-hash (the design's stated limit)

        await catalog.RefreshAsync(TestContext.Current.CancellationToken); // a full refresh hashes every file again
        AssertUnverified(() => catalog.Resolve(ExecutableNames.LlamaServer, Tag), "llama-server-impl.dll");
    }

    [Fact]
    public async Task ExtraUnlistedDll_OrExe_IsAMismatch_ButManifestAndOtherFilesAreNot()
    {
        var folder = NewFolder();
        File.WriteAllText(Path.Combine(folder.Folder, "README.txt"), "not an executable");
        var catalog = folder.Catalog();
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(catalog.List()).Verified); // manifest.json and README.txt are fine

        // A planted DLL next to the launcher would be loaded from the application folder first.
        string planted = Path.Combine(folder.Folder, "version.dll");
        File.WriteAllBytes(planted, [1, 2, 3]);
        AssertUnverified(() => catalog.Resolve(ExecutableNames.LlamaServer, Tag), "version.dll");
        Assert.Equal("version.dll: an executable file the manifest does not list", Assert.Single(catalog.List()).Problem);

        File.Delete(planted);
        catalog.Resolve(ExecutableNames.LlamaServer, Tag);

        // Also in a subfolder, also an .exe, in any letter case; found by the first verification too.
        Directory.CreateDirectory(Path.Combine(folder.Folder, "sub"));
        File.WriteAllBytes(Path.Combine(folder.Folder, "sub", "Helper.EXE"), [4]);
        var fresh = folder.Catalog();
        await fresh.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal("sub/Helper.EXE: an executable file the manifest does not list", Assert.Single(fresh.List()).Problem);
    }

    [Fact]
    public async Task MissingOrResizedListedFile_IsAMismatch()
    {
        var missing = NewFolder();
        File.Delete(Path.Combine(missing.Folder, "llama.dll"));
        var catalog = missing.Catalog();
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal("llama.dll: missing", Assert.Single(catalog.List()).Problem);

        var resized = NewFolder();
        File.AppendAllText(Path.Combine(resized.Folder, "ggml-cuda.dll"), "x");
        catalog = resized.Catalog();
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal("ggml-cuda.dll: size mismatch", Assert.Single(catalog.List()).Problem);
    }

    [Theory]
    [InlineData("lists-itself", "manifest.json lists itself")]
    [InlineData("other-version", "manifest.json describes llama-server b9999, not llama-server b11476")]
    [InlineData("exe-not-listed", "the executable llama-server.exe is not listed in manifest.json")]
    [InlineData("traversal", "manifest.json lists an invalid path '../outside.dll'")]
    [InlineData("stream", "manifest.json lists an invalid path 'llama.dll:evil'")]
    [InlineData("duplicate", "manifest.json lists 'llama.dll' twice")]
    [InlineData("bad-hash", "manifest.json has an invalid size or SHA-256 for 'llama.dll'")]
    public async Task InvalidManifest_LeavesTheVersionUnverified_WithTheReason(string variant, string problem)
    {
        var folder = NewFolder();
        folder.WriteManifest(m => variant switch
        {
            "lists-itself" => m with { Files = [.. m.Files, new ExecutableManifestFile("manifest.json", new string('0', 64), 1)] },
            "other-version" => m with { Version = "b9999" },
            "exe-not-listed" => m with { Files = [.. m.Files.Where(f => f.Path != "llama-server.exe")] },
            "traversal" => m with { Files = [.. m.Files, new ExecutableManifestFile("../outside.dll", new string('0', 64), 1)] },
            "stream" => m with { Files = [.. m.Files, new ExecutableManifestFile("llama.dll:evil", new string('0', 64), 1)] },
            "duplicate" => m with { Files = [.. m.Files, m.Files.Single(f => f.Path == "llama.dll")] },
            "bad-hash" => m with { Files = [.. m.Files.Select(f => f.Path == "llama.dll" ? f with { Sha256 = "zz" } : f)] },
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        });
        var catalog = folder.Catalog();
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(problem, Assert.Single(catalog.List()).Problem);
        AssertUnverified(() => catalog.Resolve(ExecutableNames.LlamaServer, Tag), problem);
    }

    [Fact]
    public async Task UnreadableManifest_LeavesTheVersionUnverified()
    {
        var folder = NewFolder();
        File.WriteAllText(folder.ManifestPath, "{ not json");
        var catalog = folder.Catalog();
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        var state = Assert.Single(catalog.List());
        Assert.False(state.Verified);
        Assert.StartsWith("manifest.json cannot be read", state.Problem, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ several versions, names

    [Fact]
    public async Task ATamperedVersion_IsUnusable_WhileTheOtherKeepsWorking_AndUnknownNamesOrVersionsAreMissing()
    {
        var good = NewFolder(Tag);
        var bad = NewFolder("b11500", root: good.Root);
        bad.Tamper("llama-server-impl.dll", keepTime: false);
        var catalog = new ExecutableCatalog([LlamaCppVersionFolder.Options(Tag, good, bad)], NullLogger<ExecutableCatalog>.Instance);
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(good.Folder, catalog.Resolve(ExecutableNames.LlamaServer, null).Folder); // the default
        AssertUnverified(() => catalog.Resolve(ExecutableNames.LlamaServer, "b11500"), "llama-server-impl.dll");
        Assert.Equal(
            [(Tag, true, true), ("b11500", false, false)],
            catalog.List().Select(s => (s.Version, s.Verified, s.IsDefault)));

        var version = Assert.Throws<AgentException>(() => catalog.Resolve(ExecutableNames.LlamaServer, "b9999"));
        Assert.Equal((422, ErrorCodes.ExecutableMissing), (version.StatusCode, version.Code));
        Assert.Contains("installed versions: b11476, b11500", version.Message, StringComparison.Ordinal);

        var name = Assert.Throws<AgentException>(() => catalog.Resolve("llama-cli", null));
        Assert.Equal((422, ErrorCodes.ExecutableMissing), (name.StatusCode, name.Code));
        Assert.Contains("installed executables: llama-server", name.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessDriver_WithTheRealCatalogue_RefusesATamperedVersionWith422_AndStartsNothing()
    {
        await using var node = new LlamaCppTestNode();
        await node.Catalog.RefreshAsync(TestContext.Current.CancellationToken);
        node.Driver.CheckSpec("m1", LlamaCppTestNode.Spec());

        node.Version.Tamper("llama-server-impl.dll", keepTime: false);

        var refused = Assert.Throws<AgentException>(() => node.Driver.CheckSpec("m1", LlamaCppTestNode.Spec()));
        Assert.Equal((422, ErrorCodes.ExecutableUnverified), (refused.StatusCode, refused.Code));
        await Assert.ThrowsAsync<AgentException>(() => node.LaunchDirectAsync());

        // POST /runtimes/m1/launch: a synchronous 422 before anything is reserved or recorded.
        var launch = await node.LaunchViaOperationsAsync("op-tampered");
        Assert.Equal(422, launch.StatusCode);
        Assert.Equal(ErrorCodes.ExecutableUnverified, launch.Error!.Code);
        Assert.Contains("llama-server-impl.dll: hash mismatch", launch.Error.Message, StringComparison.Ordinal);
        Assert.Null(launch.Status);
        Assert.DoesNotContain("op-tampered", node.ReadAgentStateFiles(), StringComparison.Ordinal);
        Assert.Empty(node.Host.Starts);

        // Reinstalled: the next launch is accepted.
        node.Version.Restore("llama-server-impl.dll");
        Assert.Equal(202, (await node.LaunchViaOperationsAsync("op-repaired")).StatusCode);
        await node.WaitReadyAsync();
        Assert.Single(node.Host.Starts);
    }

    // ------------------------------------------------------------ contributors

    [Fact]
    public async Task LlamaCppContributor_ReportsTheFeatureAndEveryVersion_AvailableWhileOneIsVerified()
    {
        var good = NewFolder(Tag);
        var bad = NewFolder("b11500", root: good.Root);
        bad.Tamper("llama.dll", keepTime: false);
        var catalog = new ExecutableCatalog([LlamaCppVersionFolder.Options(Tag, good, bad)]);
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);
        var contributor = new LlamaCppRuntimeContributor(catalog, [new ProcessDriverStub()]);
        var vllm = new RuntimeCapability { Kind = RuntimeKind.Vllm, Delivery = LaunchDelivery.Container, Available = false };
        var before = Capabilities(["telemetry.v1"], [vllm]);

        var after = await contributor.ContributeAsync(before);

        Assert.Equal(["telemetry.v1", AgentFeatures.RuntimeLlamaCppProcess], after.Features);
        Assert.Equal(vllm, after.Runtimes[0]);
        var llamacpp = after.Runtimes[1];
        Assert.Equal((RuntimeKind.LlamaCpp, LaunchDelivery.Process, true), (llamacpp.Kind, llamacpp.Delivery, llamacpp.Available));
        Assert.Equal([new RuntimeVersion(Tag, "13.4", true, true), new RuntimeVersion("b11500", "13.4", false, false)], llamacpp.Versions);

        // Idempotent; and with every version unverified the runtime is reported but unavailable.
        Assert.Equal(after.Features, (await contributor.ContributeAsync(after)).Features);
        good.Tamper("llama.dll", keepTime: false);
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);
        var none = (await contributor.ContributeAsync(before)).Runtimes[1];
        Assert.False(none.Available);
        Assert.All(none.Versions!, v => Assert.False(v.Verified));

        string json = JsonSerializer.Serialize(none, AgentJson.Options);
        Assert.Contains("\"versions\":[{\"version\":\"b11476\",\"cuda\":\"13.4\",\"verified\":false,\"default\":true}", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LlamaCppContributor_ChangesNothing_WithoutAProcessDriver_OrWithoutLlamaServerVersions()
    {
        var folder = NewFolder();
        var catalog = folder.Catalog();
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);
        var dgx = Capabilities(["runtime.vllm.container", "telemetry.v1"],
            [new RuntimeCapability { Kind = RuntimeKind.Vllm, Delivery = LaunchDelivery.Container, Available = true }]);

        Assert.Same(dgx, await new LlamaCppRuntimeContributor(catalog, []).ContributeAsync(dgx));
        Assert.Same(dgx, await new LlamaCppRuntimeContributor(new ExecutableCatalog([]), [new ProcessDriverStub()]).ContributeAsync(dgx));
        string dgxJson = JsonSerializer.Serialize(dgx, AgentJson.Options);
        Assert.DoesNotContain("versions", dgxJson, StringComparison.Ordinal); // container capabilities serialize as before
    }

    [Fact]
    public async Task HealthContributor_ReportsEachUnverifiedVersion_ButNotOneStillBeingHashed()
    {
        var good = NewFolder(Tag);
        var bad = NewFolder("b11500", root: good.Root);
        File.WriteAllBytes(Path.Combine(bad.Folder, "evil.dll"), [9]);
        var catalog = new ExecutableCatalog([LlamaCppVersionFolder.Options(Tag, good, bad)]);
        var health = new ExecutableHealthContributor(catalog);

        Assert.Empty(await health.CheckAsync()); // not verified yet: not known to be bad
        await catalog.WhenIdleAsync();
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        var reason = Assert.Single(await health.CheckAsync());
        Assert.Equal(HealthReasonCodes.ExecutableUnverified, reason.Code);
        Assert.Contains("llama-server b11500 failed verification: evil.dll: an executable file the manifest does not list", reason.Message, StringComparison.Ordinal);

        File.Delete(Path.Combine(bad.Folder, "evil.dll"));
        catalog.Resolve(ExecutableNames.LlamaServer, "b11500");
        Assert.Empty(await health.CheckAsync());
    }

    // ------------------------------------------------------------ helpers

    private LlamaCppVersionFolder NewFolder(string version = Tag, string? root = null)
    {
        var folder = LlamaCppVersionFolder.Create(version, root);
        _folders.Add(folder);
        return folder;
    }

    private static void AssertUnverified(Action resolve, string mentioned)
    {
        var ex = Assert.Throws<AgentException>(resolve);
        Assert.Equal(422, ex.StatusCode);
        Assert.Equal(ErrorCodes.ExecutableUnverified, ex.Code);
        Assert.Contains(mentioned, ex.Message, StringComparison.Ordinal);
    }

    private static NodeCapabilities Capabilities(IReadOnlyList<string> features, IReadOnlyList<RuntimeCapability> runtimes) => new()
    {
        Protocol = new ProtocolRange(1, 1),
        AgentVersion = "test",
        Os = new NodeOsInfo("windows", "Windows 11"),
        Arch = "x64",
        Memory = new NodeMemoryInfo(MemoryModel.Discrete, 64),
        Features = features,
        Runtimes = runtimes,
    };

    /// <summary>A registered process driver (only its delivery is read).</summary>
    internal sealed class ProcessDriverStub : IRuntimeDriver
    {
        public LaunchDelivery Delivery => LaunchDelivery.Process;

        public Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RuntimeStatus?> GetAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RuntimeLaunchResult> LaunchAsync(RuntimeLaunch launch, IRuntimeStageSink stages, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RuntimeStatus> StartCreatedAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task StopAsync(string modelId, IRuntimeStageSink stages, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LaunchConfig?> GetLaunchConfigAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<Uri?> GetUpstreamAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

        public IAsyncEnumerable<string> StreamLogsAsync(string modelId, int tail, bool follow, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> IsFolderInUseAsync(string folder, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

/// <summary>
/// A llama.cpp version folder as MN-216's installer leaves it (MN-222 tests): a 9-byte launcher, the server DLL, the other
/// DLLs, and <c>manifest.json</c> listing them all (not itself), in a temp runtimes root.
/// </summary>
internal sealed class LlamaCppVersionFolder : IDisposable
{
    private static readonly string[] Files = ["llama-server.exe", "llama-server-impl.dll", "llama.dll", "ggml-cuda.dll", "cudart64_13.dll"];
    private readonly Dictionary<string, byte[]> _original = new(StringComparer.Ordinal);
    private readonly bool _ownsRoot;

    private LlamaCppVersionFolder(string root, string version, bool ownsRoot)
    {
        Root = root;
        Version = version;
        _ownsRoot = ownsRoot;
        Folder = Path.Combine(root, "runtimes", "llama.cpp", version);
        Directory.CreateDirectory(Folder);
        foreach (string file in Files)
        {
            byte[] content = file == "llama-server.exe" ? "MZlaunch!"u8.ToArray() : RandomNumberGenerator.GetBytes(64 * 1024 + file.Length);
            _original[file] = content;
            File.WriteAllBytes(Path.Combine(Folder, file), content);
        }

        WriteManifest();
    }

    public string Root { get; }

    public string Version { get; }

    public string Folder { get; }

    public string ManifestPath => Path.Combine(Folder, ExecutableCatalog.ManifestFileName);

    public string ExecutablePath => Path.Combine(Folder, "llama-server.exe");

    public static LlamaCppVersionFolder Create(string version, string? root = null) =>
        new(root ?? Path.Combine(Path.GetTempPath(), "mn222-" + Guid.NewGuid().ToString("N")[..12]), version, ownsRoot: root is null);

    /// <summary><c>Agent:Executables</c> for <c>llama-server</c> with these folders' versions.</summary>
    public static ExecutableOptions Options(string? defaultVersion, params LlamaCppVersionFolder[] folders)
    {
        var options = new ExecutableOptions { Name = ExecutableNames.LlamaServer, Default = defaultVersion };
        foreach (var folder in folders)
        {
            options.Versions.Add(new ExecutableVersionOptions
            {
                Version = folder.Version,
                Path = folder.ExecutablePath,
                Manifest = folder.ManifestPath,
                Cuda = "13.4",
            });
        }

        return options;
    }

    public ExecutableCatalog Catalog() => new([Options(null, this)], NullLogger<ExecutableCatalog>.Instance);

    public void WriteManifest(Func<ExecutableManifest, ExecutableManifest>? edit = null)
    {
        var manifest = new ExecutableManifest
        {
            Name = ExecutableNames.LlamaServer,
            Version = Version,
            Cuda = "13.4",
            Sources = [$"llama-{Version}-bin-win-cuda-13.4-x64.zip", "cudart-llama-bin-win-cuda-13.4-x64.zip"],
            Files = [.. _original.Select(kv => new ExecutableManifestFile(kv.Key, Convert.ToHexStringLower(SHA256.HashData(kv.Value)), kv.Value.Length))],
        };
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(edit?.Invoke(manifest) ?? manifest, AgentJson.Options));
    }

    /// <summary>Flips one byte (size unchanged); the modification time moves on unless <paramref name="keepTime"/>.</summary>
    public void Tamper(string file, bool keepTime)
    {
        string path = Path.Combine(Folder, file);
        DateTime time = File.GetLastWriteTimeUtc(path);
        byte[] content = File.ReadAllBytes(path);
        content[content.Length / 2] ^= 0xFF;
        File.WriteAllBytes(path, content);
        File.SetLastWriteTimeUtc(path, keepTime ? time : time.AddSeconds(5));
    }

    /// <summary>Writes the original content back (a reinstall), with a new modification time.</summary>
    public void Restore(string file)
    {
        string path = Path.Combine(Folder, file);
        DateTime time = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, _original[file]);
        File.SetLastWriteTimeUtc(path, time.AddSeconds(5));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_ownsRoot ? Root : Folder, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
