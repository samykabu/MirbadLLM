using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Proxy;
using MirbadLLM.Agent.Runtimes;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Runtimes;
using MirbadLLM.NodeRuntime.Runtimes.Process;
using MirbadLLM.UnitTests.Agent.Fakes;
using MirbadLLM.UnitTests.Multinode;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// macOS Phase 3 (docs/macos/implementation-plan.md): the agent's MLX side. The launch validator takes only the flags
/// the pinned <c>mlx_lm.server</c> knows, spelled exactly (argparse would expand an abbreviation), confines the folders,
/// puts the interpreter arguments in front and expects <c>/v1/models</c> to list the model folder's real path; the
/// environment cannot touch Python, Hugging Face or the loader; the catalogue's "allFiles" manifests refuse any unlisted
/// file; the proxy rewrites <c>model</c> and refuses a body it cannot rewrite safely; and the runtime report names the
/// installed mlx-lm versions.
/// </summary>
public sealed class MlxAgentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mirbadllm-mlx-agent-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly string _models;
    private readonly FakeExecutableCatalog _catalog;
    private readonly ProcessRuntimeOptions _options;

    public MlxAgentTests()
    {
        _models = Path.Combine(_root, "models");
        Directory.CreateDirectory(Path.Combine(_models, "Qwen3-0.6B-4bit"));
        File.WriteAllText(Path.Combine(_models, "Qwen3-0.6B-4bit", "config.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_models, "draft"));
        File.WriteAllText(Path.Combine(_models, "weights.safetensors"), "not a folder");
        _catalog = new FakeExecutableCatalog(Path.Combine(_root, "runtimes"));
        _catalog.Add("0.32.2", name: ExecutableNames.MlxLmServer);
        _options = new ProcessRuntimeOptions { ModelsRoot = _models, CacheRoot = Path.Combine(_root, "cache"), StateDirectory = Path.Combine(_root, "state") };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---------------------------------------------------------------- launch validator

    [Fact]
    public void Validate_PutsTheInterpreterArgsFirst_SubstitutesTheFolder_AndExpectsItsRealPath()
    {
        var plan = ProcessLaunchValidator.Validate("qwen3-mlx", Spec(Argv("--temp", "0.2")), _options, _catalog);

        string folder = Path.Combine(PathConfinement.RealPath(_models)!, "Qwen3-0.6B-4bit");
        Assert.Equal(RuntimeKind.Mlx, plan.Runtime);
        Assert.Equal(ExecutableNames.MlxLmServer, plan.Executable.Name);
        Assert.Equal(
            [.. ExecutableNames.MlxInterpreterArgs, "--model", folder, "--host", "127.0.0.1", "--port", "{port}", "--decode-concurrency", "4", "--temp", "0.2"],
            plan.Arguments);
        Assert.Equal(["-I", "-u", "-B", "-m", "mlx_lm.server"], ExecutableNames.MlxInterpreterArgs);
        Assert.Equal(folder, plan.Alias); // what /v1/models lists: Path(--model).resolve()
        Assert.Equal("54321", plan.ArgumentsFor(54321)[Array.IndexOf([.. plan.Arguments], "--port") + 1]);
    }

    [Fact]
    public void AnAdoptedMlxRecord_GetsTheSameReadinessAliasAsAtLaunch_AndLlamaCppKeepsItsAliasFlag()
    {
        // An MLX launch has no --alias: the alias is the model folder's real path. Adoption read argv alone, got "", and
        // every MLX model failed supervision after an agent restart or a pushed update (mac-1, 2026-10-17). The derivation
        // needs no catalogue: right after an agent start the MLX folder is still being hashed (not verified yet), and a
        // derivation through the full validator failed the same way.
        var plan = ProcessLaunchValidator.Validate("qwen3-mlx", Spec(Argv()), _options, _catalog);
        var record = new ProcessRecord
        {
            ModelId = "qwen3-mlx",
            OperationId = "op-1",
            Spec = Spec(Argv()),
            Process = new ProcessIdentity(4242, DateTimeOffset.UnixEpoch, "/usr/local/lib/python"),
            ExecutableName = ExecutableNames.MlxLmServer,
            ExecutableVersion = "0.32.2",
            Port = 8101,
            ArgvDigest = "x",
            JobName = "mirbadllm-ss-qwen3-mlx",
            LogPath = "/tmp/qwen3-mlx.log",
            ProtectedApiKey = "",
            StartedAt = DateTimeOffset.UnixEpoch,
        };

        Assert.False(string.IsNullOrEmpty(plan.Alias));
        Assert.Equal(plan.Alias, ProcessRuntimeDriver.AdoptedAlias(record, _options));
        var llama = record with { Spec = record.Spec with { Runtime = RuntimeKind.LlamaCpp, Argv = ["-m", "{models}/x.gguf", "--alias", "chat"] } };
        Assert.Equal("chat", ProcessRuntimeDriver.AdoptedAlias(llama, _options));
    }

    [Fact]
    public void ResponsesReportTheServedName_LineByLine_EvenWhenALineIsSplitAcrossChunks()
    {
        var rewriter = new MirbadLLM.Agent.Proxy.ResponseModelRewriter("default_model", "Qwen3-0.6B-mlx");
        // Two SSE events: Python's json writes "model": "default_model" with a space, the second without; the second's
        // content mentions the property, escaped, as text in a completion always is.
        string events = """
            data: {"id": "c1", "model": "default_model", "choices": []}

            data: {"model":"default_model", "choices": [{"delta": {"content": "say \"model\": \"default_model\""}}]}


            """.Replace("\r\n", "\n", StringComparison.Ordinal);
        byte[] sse = System.Text.Encoding.UTF8.GetBytes(events);

        var output = new List<byte>();
        output.AddRange(rewriter.Process(sse.AsSpan(0, 30))); // ends mid-line: nothing may go out yet
        Assert.Empty(rewriter.Process([]));
        output.AddRange(rewriter.Process(sse.AsSpan(30)));
        output.AddRange(rewriter.Flush());
        string text = System.Text.Encoding.UTF8.GetString([.. output]);

        Assert.Equal(2, text.Split("\"model\": \"Qwen3-0.6B-mlx\"").Length - 1);
        Assert.Contains("say \\\"model\\\": \\\"default_model\\\"", text, StringComparison.Ordinal); // escaped text in the content stays
        Assert.DoesNotContain("\"model\": \"default_model\"", text, StringComparison.Ordinal);

        var json = new MirbadLLM.Agent.Proxy.ResponseModelRewriter("default_model", "Qwen3-0.6B-mlx");
        Assert.Empty(json.Process(System.Text.Encoding.UTF8.GetBytes("{\"object\": \"chat.completion\", \"model\": \"default_model\"}")));
        Assert.Equal("{\"object\": \"chat.completion\", \"model\": \"Qwen3-0.6B-mlx\"}", System.Text.Encoding.UTF8.GetString(json.Flush()));
    }

    [Theory]
    [InlineData("--hos")] // abbreviations argparse would expand
    [InlineData("--mo")]
    [InlineData("--adapter")]
    [InlineData("--pipeline")]
    [InlineData("--help")]
    [InlineData("--unknown-flag")]
    public void Validate_RefusesFlagsNotSpelledExactlyAsAKnownOne(string flag)
    {
        var ex = Assert.Throws<AgentException>(() => ProcessLaunchValidator.Validate("m", Spec(Argv(flag, "x")), _options, _catalog));
        Assert.Equal(422, ex.StatusCode);
        Assert.Equal(ErrorCodes.SpecRejected, ex.Code);
        Assert.Contains($"'{flag}' is not a flag an MLX launch may pass", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "--model", "{models}/Qwen3-0.6B-4bit", "--host", "0.0.0.0", "--port", "{port}" }, "'--host' must be followed by '127.0.0.1'")]
    [InlineData(new[] { "--model", "{models}/Qwen3-0.6B-4bit", "--host", "127.0.0.1", "--port", "8080" }, "'--port' must be followed by '{port}'")]
    [InlineData(new[] { "--model", "{models}/Qwen3-0.6B-4bit", "--host", "127.0.0.1" }, "exactly one '--host 127.0.0.1' and one '--port {port}'")]
    [InlineData(new[] { "--host", "127.0.0.1", "--port", "{port}" }, "must name the model folder")]
    [InlineData(new[] { "--model", "{models}/../etc", "--host", "127.0.0.1", "--port", "{port}" }, "'--model' path '{models}/../etc'")]
    [InlineData(new[] { "--model", "/Users/Shared/x", "--host", "127.0.0.1", "--port", "{port}" }, "takes a folder under {models}/")]
    [InlineData(new[] { "--model", "{models}/weights.safetensors", "--host", "127.0.0.1", "--port", "{port}" }, "is not a folder")]
    [InlineData(new[] { "--model", "{models}/missing", "--host", "127.0.0.1", "--port", "{port}" }, "does not resolve to an existing folder")]
    [InlineData(new[] { "--model", "{models}/Qwen3-0.6B-4bit", "--model", "{models}/draft", "--host", "127.0.0.1", "--port", "{port}" }, "'--model' is given twice")]
    [InlineData(new[] { "--model={models}/Qwen3-0.6B-4bit", "--host", "127.0.0.1", "--port", "{port}" }, "must take its value as the next argument")]
    [InlineData(new[] { "--model", "{models}/Qwen3-0.6B-4bit", "--host", "127.0.0.1", "--port", "{port}", "--temp", "{cache}/x" }, "uses a token outside the flag")]
    public void Validate_EnforcesTheMlxArgvRules(string[] argv, string reason)
    {
        var ex = Assert.Throws<AgentException>(() => ProcessLaunchValidator.Validate("m", Spec(argv), _options, _catalog));
        Assert.Equal(ErrorCodes.SpecRejected, ex.Code);
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ConfinesTheDraftAndAdapterFolders()
    {
        var plan = ProcessLaunchValidator.Validate("m", Spec(Argv("--draft-model", "{models}/draft", "--num-draft-tokens", "3")), _options, _catalog);
        Assert.Contains(Path.Combine(PathConfinement.RealPath(_models)!, "draft"), plan.Arguments);

        var ex = Assert.Throws<AgentException>(() =>
            ProcessLaunchValidator.Validate("m", Spec(Argv("--adapter-path", "/tmp/lora")), _options, _catalog));
        Assert.Contains("'--adapter-path' takes a folder under {models}/", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PYTHONPATH")]
    [InlineData("PYTHONSTARTUP")]
    [InlineData("HF_HUB_OFFLINE")]
    [InlineData("HF_ENDPOINT")]
    [InlineData("TRANSFORMERS_OFFLINE")]
    [InlineData("DYLD_LIBRARY_PATH")]
    [InlineData("home")]
    public void Validate_RefusesPythonHuggingFaceAndLoaderEnvironment(string name)
    {
        var spec = Spec(Argv()) with { Env = new Dictionary<string, string> { [name] = "x" } };
        var ex = Assert.Throws<AgentException>(() => ProcessLaunchValidator.Validate("m", spec, _options, _catalog));
        Assert.Contains($"environment variable '{name}'", ex.Message, StringComparison.Ordinal);

        // The same names stay a llama.cpp spec's business (unchanged rules there).
        Assert.False(ProcessEnvironment.IsReserved(name) && name != "home");
    }

    [Fact]
    public void Validate_RefusesGpuSelection_AndTheWrongExecutable()
    {
        var gpus = Spec(Argv()) with { Gpus = new GpuSelection { Mode = GpuSelectionMode.Uuids, Uuids = ["GPU-1"] } };
        Assert.Contains("unified memory", Assert.Throws<AgentException>(() => ProcessLaunchValidator.Validate("m", gpus, _options, _catalog)).Message,
            StringComparison.Ordinal);

        var llama = Spec(Argv()) with { Executable = ExecutableNames.LlamaServer };
        Assert.Contains("an MLX process runs 'mlx-lm-server', not 'llama-server'",
            Assert.Throws<AgentException>(() => ProcessLaunchValidator.Validate("m", llama, _options, _catalog)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_StillRefusesVllmProcesses()
    {
        var vllm = Spec(Argv()) with { Runtime = RuntimeKind.Vllm };
        Assert.Contains("native processes run llama.cpp or MLX",
            Assert.Throws<AgentException>(() => ProcessLaunchValidator.Validate("m", vllm, _options, _catalog)).Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- catalogue: complete listings

    [Fact]
    public async Task AllFilesManifest_Verifies_ThenAnyUnlistedFileOfAnyKind_IsAMismatch()
    {
        var folder = VenvFolder();
        var catalog = folder.Catalog();
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(catalog.List()).Verified);
        Assert.Equal(folder.Python, catalog.Resolve(ExecutableNames.MlxLmServer, null).Path);

        // A .pth runs at interpreter start; an unlisted .py shadows a module. Neither is an "executable" by extension.
        foreach (string planted in new[] { "venv/lib/python3.12/site-packages/evil.pth", "venv/lib/python3.12/site-packages/json.py" })
        {
            folder.Plant(planted, "import os"u8.ToArray());
            var ex = Assert.Throws<AgentException>(() => catalog.Resolve(ExecutableNames.MlxLmServer, null));
            Assert.Equal(ErrorCodes.ExecutableUnverified, ex.Code);
            Assert.Contains($"{planted}: a file the manifest does not list", ex.Message, StringComparison.Ordinal);
            Assert.Contains("install-agent.sh --update-mlx", ex.Message, StringComparison.Ordinal);
            File.Delete(Path.Combine(folder.Root, planted));
        }

        Assert.Equal(folder.Python, catalog.Resolve(ExecutableNames.MlxLmServer, null).Path);
    }

    [Fact]
    public async Task AllFilesManifest_ATamperedPythonFile_IsAHashMismatch()
    {
        var folder = VenvFolder();
        File.WriteAllText(Path.Combine(folder.Root, "venv/lib/python3.12/site-packages/mlx_lm/server.py"), "print('changed')");
        var catalog = folder.Catalog();

        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("venv/lib/python3.12/site-packages/mlx_lm/server.py: size mismatch", Assert.Single(catalog.List()).Problem);
    }

    [Fact]
    public async Task WithoutAllFiles_TheSameUnlistedPythonFile_IsIgnored_AsForLlamaCpp()
    {
        var folder = VenvFolder(allFiles: false);
        folder.Plant("venv/lib/python3.12/site-packages/extra.py", "x = 1"u8.ToArray());
        var catalog = folder.Catalog();

        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(catalog.List()).Verified);
    }

    // ---------------------------------------------------------------- proxy body rewrite

    [Fact]
    public async Task RewriteBody_SetsModel_AndRemovesTheFieldsThatWouldLoadOtherFolders()
    {
        var rewrite = new UpstreamBodyRewrite(MlxFlags.DefaultModel, MlxFlags.RemovedRequestFields);
        byte[] body = Encoding.UTF8.GetBytes("""
            {"model":"qwen3-0.6b-mlx","messages":[{"role":"user","content":"hi"}],"draft_model":"/etc","adapters":"x","stream":true}
            """);

        byte[]? result = await AgentProxy.RewriteBodyAsync(new MemoryStream(body), rewrite, TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(result!);
        Assert.Equal("default_model", json.RootElement.GetProperty("model").GetString());
        Assert.False(json.RootElement.TryGetProperty("draft_model", out _));
        Assert.False(json.RootElement.TryGetProperty("adapters", out _));
        Assert.True(json.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("hi", json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("""{"model":"a","model":"/Users/Shared/other"}""")] // Python keeps the last value
    [InlineData("""{"model":"a","temperature":NaN}""")] // Python reads NaN; .NET refuses it
    [InlineData("[1,2,3]")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task RewriteBody_RefusesWhatItCannotRewriteSafely(string body)
    {
        var rewrite = new UpstreamBodyRewrite(MlxFlags.DefaultModel, MlxFlags.RemovedRequestFields);
        Assert.Null(await AgentProxy.RewriteBodyAsync(new MemoryStream(Encoding.UTF8.GetBytes(body)), rewrite, TestContext.Current.CancellationToken));
    }

    // ---------------------------------------------------------------- capability report

    [Fact]
    public async Task MlxRuntimeContributor_ReportsTheMlxVersions_AndTheFeature()
    {
        var catalog = new FakeExecutableCatalog(Path.Combine(_root, "runtimes2"));
        catalog.Add("b11476");
        catalog.Add("0.32.2", name: ExecutableNames.MlxLmServer, isDefault: false);
        var contributor = new MlxRuntimeContributor(catalog, [new ExecutableCatalogTests.ProcessDriverStub()]);

        var report = await contributor.ContributeAsync(Bare(), TestContext.Current.CancellationToken);

        Assert.Equal([AgentFeatures.TelemetryV1, AgentFeatures.RuntimeMlxProcess], report.Features);
        var mlx = Assert.Single(report.Runtimes);
        Assert.Equal(RuntimeKind.Mlx, mlx.Kind);
        Assert.Equal(LaunchDelivery.Process, mlx.Delivery);
        Assert.True(mlx.Available);
        Assert.Equal("0.32.2", Assert.Single(mlx.Versions!).Version);
    }

    [Fact]
    public async Task MlxRuntimeContributor_WithoutMlxInstalled_ReportsNothingNew()
    {
        var catalog = new FakeExecutableCatalog(Path.Combine(_root, "runtimes3"));
        catalog.Add("b11476");
        var current = Bare();

        var report = await new MlxRuntimeContributor(catalog, [new ExecutableCatalogTests.ProcessDriverStub()]).ContributeAsync(current, TestContext.Current.CancellationToken);

        Assert.Same(current, report);
    }

    // ---------------------------------------------------------------- helpers

    private static NodeCapabilities Bare() => MlxFixtures.Mac().LastCapabilities! with { Features = [AgentFeatures.TelemetryV1], Runtimes = [] };

    private static string[] Argv(params string[] extra) =>
        ["--model", "{models}/Qwen3-0.6B-4bit", "--host", "127.0.0.1", "--port", "{port}", "--decode-concurrency", "4", .. extra];

    private static LaunchSpec Spec(IReadOnlyList<string> argv) => new()
    {
        Kind = LaunchDelivery.Process,
        Runtime = RuntimeKind.Mlx,
        Executable = ExecutableNames.MlxLmServer,
        ExecutableVersion = "0.32.2",
        Argv = argv,
        Env = new Dictionary<string, string>(),
        SecretEnvNames = [],
        Mounts = [],
        Gpus = new GpuSelection { Mode = GpuSelectionMode.All },
        HealthPath = "/health",
        Labels = new Dictionary<string, string> { [ContainerLabels.ServedName] = "qwen3-0.6b-mlx" },
    };

    private VenvVersionFolder VenvFolder(bool allFiles = true) => new(Path.Combine(_root, "mlx-lm", Guid.NewGuid().ToString("N")[..8]), allFiles);

    /// <summary>A tiny MLX version folder shaped like <c>install-agent.sh stage-mlx</c> leaves it, with its manifest.</summary>
    private sealed class VenvVersionFolder
    {
        public VenvVersionFolder(string root, bool allFiles)
        {
            Root = root;
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["python/cpython-3.12.15-macos-aarch64-none/bin/python3.12"] = [0xCF, 0xFA, 0xED, 0xFE, .. RandomNumberGenerator.GetBytes(64)],
                ["python/cpython-3.12.15-macos-aarch64-none/lib/python3.12/os.py"] = "import sys"u8.ToArray(),
                ["venv/bin/python"] = [0xCF, 0xFA, 0xED, 0xFE, .. RandomNumberGenerator.GetBytes(64)], // a link on the Mac; a copy here
                ["venv/pyvenv.cfg"] = "home = /x\n"u8.ToArray(),
                ["venv/lib/python3.12/site-packages/mlx_lm/server.py"] = "def main(): pass\n"u8.ToArray(),
                ["venv/lib/python3.12/site-packages/mlx/core.cpython-312-darwin.so"] = [0xCF, 0xFA, 0xED, 0xFE, 1, 2],
            };
            foreach (var (path, content) in files)
            {
                Plant(path, content);
            }

            var manifest = new ExecutableManifest
            {
                Name = ExecutableNames.MlxLmServer,
                Version = "0.32.2",
                AllFiles = allFiles,
                Files = [.. files.Select(f => new ExecutableManifestFile(f.Key, Convert.ToHexStringLower(SHA256.HashData(f.Value)), f.Value.Length))],
            };
            File.WriteAllText(Path.Combine(Root, ExecutableCatalog.ManifestFileName), JsonSerializer.Serialize(manifest, AgentJson.Options));
        }

        public string Root { get; }

        public string Python => Path.Combine(Root, "venv", "bin", "python");

        public void Plant(string relative, byte[] content)
        {
            string path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        public ExecutableCatalog Catalog()
        {
            var options = new ExecutableOptions { Name = ExecutableNames.MlxLmServer, Default = "0.32.2" };
            options.Versions.Add(new ExecutableVersionOptions
            {
                Version = "0.32.2",
                Path = Python,
                Manifest = Path.Combine(Root, ExecutableCatalog.ManifestFileName),
            });
            return new ExecutableCatalog([options], NullLogger<ExecutableCatalog>.Instance, ExecutablePlatform.MacOS);
        }
    }
}
