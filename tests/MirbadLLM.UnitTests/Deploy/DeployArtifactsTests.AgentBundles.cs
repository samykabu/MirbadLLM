using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.RegularExpressions;
using MirbadLLM.Web.Api;

namespace MirbadLLM.UnitTests.Deploy;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// The master image bakes the Linux agent bundles the master serves (<c>GET /agent/bundles</c>): an <c>agent-bundles</c>
/// stage on the build platform publishes both architectures with <c>install-agent.sh</c>'s own publish settings and lets
/// the script's <c>package</c> verb make each tarball, and the final stage puts them where <see cref="AgentBundleCatalog"/>
/// reads them. Opt-in (<c>MIRBADLLM_DOCKER_BUILD_TESTS=1</c> with Docker): build the stage and inspect the tarballs.
/// </summary>
public sealed partial class DeployArtifactsTests
{
    /// <summary>The entries of a bundle under its folder, as <c>install-agent.sh package</c> writes them.</summary>
    private static readonly string[] BundleLayout =
        ["", "/BUILD-INFO", "/README.md", "/agent.linux.example.json", "/install-agent.sh", "/mirbadllm-agent", "/mirbadllm-agent.service"];

    [Fact]
    public void MasterImage_HasAnAgentBundlesStage_OnTheBuildPlatform_FromTheOfficialSdk()
    {
        string stage = Stage(File.ReadAllText(RepoFile("deploy", "Dockerfile")), "agent-bundles");

        Assert.StartsWith("FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:", stage, StringComparison.Ordinal);
        // Every architecture the catalog serves is restored; the Linux ones are published one after the other (one publish's
        // memory at a time), the Windows one in its own step (MN-218).
        string[][] loops = [.. Regex.Matches(stage, @"for rid in ([^;]+);").Select(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))];
        Assert.Equal(2, loops.Length);
        Assert.Equal(AgentBundleCatalog.Rids, loops[0]);
        Assert.Equal(AgentBundleCatalog.LinuxRids, loops[1]);
        Assert.Contains("dotnet restore src/MirbadLLM.Agent/MirbadLLM.Agent.csproj -r \"$rid\" -p:SelfContained=true -p:PublishSingleFile=true", stage, StringComparison.Ordinal);
        Assert.DoesNotContain("&\n", stage.ReplaceLineEndings("\n"), StringComparison.Ordinal); // nothing in the background

        // The agent's whole project closure, csproj first for the restore layer, then the sources.
        foreach (string project in ProjectClosure("src/MirbadLLM.Agent/MirbadLLM.Agent.csproj"))
        {
            string dir = Path.GetDirectoryName(project)!.Replace('\\', '/');
            Assert.Contains($"COPY {project} {dir}/", stage, StringComparison.Ordinal);
            Assert.Contains($"COPY {dir}/ {dir}/", stage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AgentBundlesStage_PublishesLikeInstallAgentSh_AndPackagesWithIt()
    {
        string stage = Stage(File.ReadAllText(RepoFile("deploy", "Dockerfile")), "agent-bundles").ReplaceLineEndings("\n");
        string script = File.ReadAllText(RepoFile("deploy", "agent", "linux", "install-agent.sh")).ReplaceLineEndings("\n");

        // The publish flags of publish_from_repo (self-contained, single file, no symbols; trimmed only if that trims).
        string scriptPublish = Flatten(Regex.Match(script, @"dotnet publish src/MirbadLLM\.Agent/MirbadLLM\.Agent\.csproj -c Release -r ""\$RID""(?<flags>[^\n]*\\\n[^\n]*)").Groups["flags"].Value);
        string stagePublish = Flatten(Regex.Match(stage, @"dotnet publish src/MirbadLLM\.Agent/MirbadLLM\.Agent\.csproj -c Release -r ""\$rid""(?<flags>[^\n]*\\\n[^\n]*)").Groups["flags"].Value);
        Assert.NotEmpty(scriptPublish);
        Assert.Equal(PublishFlags(scriptPublish), PublishFlags(stagePublish));
        Assert.Contains("--self-contained true", stagePublish, StringComparison.Ordinal);

        // BUILD-INFO in the script's format, and the tarball made by the script's own package verb, named for the catalog.
        Assert.Contains(@"printf 'revision=%s\nrid=%s\nbuilt=%s\n'", stage, StringComparison.Ordinal);
        Assert.Contains(@"printf ""revision=%s\nrid=%s\nbuilt=%s\n""", script, StringComparison.Ordinal);
        Assert.Contains(@"bash deploy/agent/linux/install-agent.sh package --published-dir ""/publish/$rid"" --rid ""$rid"" --output ""/bundles/$rid.tar.gz""", stage, StringComparison.Ordinal);
        // What package puts in the bundle is in the stage (the unit, the example next to deploy/agent, the README).
        Assert.Contains("COPY deploy/agent/agent.linux.example.json deploy/agent/", stage, StringComparison.Ordinal);
        Assert.Contains("COPY deploy/agent/linux/install-agent.sh deploy/agent/linux/mirbadllm-agent.service deploy/agent/linux/README.md deploy/agent/linux/", stage, StringComparison.Ordinal);
        Assert.Contains("ARG SOURCE_REVISION=", stage, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Windows step (design-p3p4 §A.4.5, MN-218): the Linux publish settings cross-compiled for win-x64, only the exe from
    /// the publish folder, the installer, the example, the committed llama.cpp lock file (MN-207), BUILD-INFO in the Linux
    /// format and the README when there is one, in one folder <c>mirbadllm-agent-{revision}-win-x64</c>, zipped to the
    /// name the catalog reads.
    /// </summary>
    [Fact]
    public void AgentBundlesStage_BuildsTheWindowsZip_WithTheLinuxPublishSettings_AndTheBundleLayout()
    {
        string stage = Stage(File.ReadAllText(RepoFile("deploy", "Dockerfile")), "agent-bundles").ReplaceLineEndings("\n");
        string script = File.ReadAllText(RepoFile("deploy", "agent", "linux", "install-agent.sh")).ReplaceLineEndings("\n");

        string scriptPublish = Flatten(Regex.Match(script, @"dotnet publish src/MirbadLLM\.Agent/MirbadLLM\.Agent\.csproj -c Release -r ""\$RID""(?<flags>[^\n]*\\\n[^\n]*)").Groups["flags"].Value);
        Match windowsPublish = Regex.Match(stage, @"dotnet publish src/MirbadLLM\.Agent/MirbadLLM\.Agent\.csproj -c Release -r win-x64(?<flags>[^\n]*\\\n[^\n]*\\\n[^\n]*)");
        Assert.True(windowsPublish.Success, "no win-x64 publish in the agent-bundles stage");
        string flags = Flatten(windowsPublish.Groups["flags"].Value);
        Assert.Equal(PublishFlags(scriptPublish), PublishFlags(flags));
        Assert.Contains("--self-contained true", flags, StringComparison.Ordinal);
        Assert.Contains("-p:PublishSingleFile=true", flags, StringComparison.Ordinal);
        Assert.Contains(@"-p:SourceRevisionId=""${SOURCE_REVISION}"" -o /publish/win-x64", flags, StringComparison.Ordinal);

        // The step's files are in the stage and in the build context.
        Assert.Contains("COPY deploy/agent/agent.windows.example.json deploy/agent/", stage, StringComparison.Ordinal);
        Assert.Contains("COPY deploy/agent/windows/ deploy/agent/windows/", stage, StringComparison.Ordinal);
        string windowsStep = stage[stage.IndexOf("COPY deploy/agent/windows/ ", StringComparison.Ordinal)..];
        Assert.Contains(@"folder=""/stage/mirbadllm-agent-${SOURCE_REVISION}-win-x64""", windowsStep, StringComparison.Ordinal);
        string copy = Flatten(Regex.Match(windowsStep, @"cp /publish/win-x64/[^;]*;").Value);
        Assert.Equal(
            "cp /publish/win-x64/mirbadllm-agent.exe deploy/agent/windows/install-agent.ps1 deploy/agent/windows/llama-cpp.lock.json deploy/agent/agent.windows.example.json \"$folder/\";",
            copy);
        // Each required file except BUILD-INFO comes from that copy, by name; BUILD-INFO is written next to them.
        foreach (string required in AgentBundleCatalog.WindowsRequiredFiles.Where(f => f != "BUILD-INFO"))
        {
            Assert.Contains("/" + required, copy, StringComparison.Ordinal);
        }

        Assert.Contains(@"printf 'revision=%s\nrid=%s\nbuilt=%s\n' ""${SOURCE_REVISION}"" win-x64 ""$(date -u +%Y-%m-%dT%H:%M:%SZ)"" >""$folder/BUILD-INFO""", windowsStep, StringComparison.Ordinal);
        Assert.Contains(@"if [ -f deploy/agent/windows/README.md ]; then cp deploy/agent/windows/README.md ""$folder/""; fi", windowsStep, StringComparison.Ordinal);
        Assert.Contains($@"[System.IO.Compression.ZipFile]::CreateFromDirectory(""/stage"", ""/bundles/{AgentBundleCatalog.FileName(AgentBundleCatalog.WindowsRid)}"")", windowsStep, StringComparison.Ordinal);
        // The revision is checked in this step too (it names the folder), and the staging folders do not stay in the layer.
        Assert.Contains(@"case ""${SOURCE_REVISION}"" in *[!A-Za-z0-9._-]* | """") echo", windowsStep, StringComparison.Ordinal);
        Assert.Contains("rm -rf /publish/win-x64 /stage;", windowsStep, StringComparison.Ordinal);
    }

    [Fact]
    public void MasterImage_CopiesTheBundles_WhereTheCatalogReadsThem()
    {
        string dockerfile = File.ReadAllText(RepoFile("deploy", "Dockerfile")).ReplaceLineEndings("\n");
        string final = dockerfile[dockerfile.LastIndexOf("\nFROM ", StringComparison.Ordinal)..];

        Assert.Contains("WORKDIR /app", final, StringComparison.Ordinal);
        Assert.Contains($"COPY --from=agent-bundles /bundles /app/{AgentBundleCatalog.DefaultDirectoryName}", final, StringComparison.Ordinal);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"MirbadLLM.Web.dll\"]", final, StringComparison.Ordinal); // AppContext.BaseDirectory = /app
    }

    [Fact]
    public void MasterBuildContext_IncludesTheAgentAndItsPackagingFiles()
    {
        string[] ignore = [.. File.ReadAllLines(RepoFile("deploy", "Dockerfile.dockerignore")).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))];

        Assert.DoesNotContain("src/MirbadLLM.Agent/**", ignore);
        Assert.Contains("!src/**", ignore);
        foreach (string file in new[]
        {
            "deploy/agent/agent.linux.example.json", "deploy/agent/linux/install-agent.sh", "deploy/agent/linux/mirbadllm-agent.service", "deploy/agent/linux/README.md",
            "deploy/agent/agent.windows.example.json", "deploy/agent/windows/install-agent.ps1", "deploy/agent/windows/llama-cpp.lock.json",
        })
        {
            Assert.True(File.Exists(RepoFile([.. file.Split('/')])), file);
            int include = Array.IndexOf(ignore, "!" + file);
            Assert.True(include > Array.IndexOf(ignore, "**"), $"{file} is not re-included after '**'");
        }

        // The Windows README is MN-216's; the context takes it as soon as it exists (the stage copies it when present).
        Assert.True(Array.IndexOf(ignore, "!deploy/agent/windows/README.md") > Array.IndexOf(ignore, "**"));
        // Nothing else of deploy/agent/windows (MN-216's Pester tests) goes into the image.
        Assert.DoesNotContain(ignore, l => l.StartsWith("!deploy/agent/windows/", StringComparison.Ordinal) && l.Contains('*', StringComparison.Ordinal));
    }

    [Fact]
    public void Deploy_NamesTheBundlesAfterTheCommit()
    {
        string compose = ComposeText();
        Assert.Contains("      args:\n        SOURCE_REVISION: ${MIRBADLLM_REVISION:-unknown}\n", compose, StringComparison.Ordinal);
        string deploy = File.ReadAllText(RepoFile("deploy", "ci-deploy-multinode.sh"));
        Assert.Contains("MIRBADLLM_REVISION=\"${sha:0:12}\"", deploy, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the <c>agent-bundles</c> stage for linux/amd64 and linux/arm64 (the stage runs on the build platform, so both
    /// are the same work) and checks each tarball: one folder <c>mirbadllm-agent-{revision}-{rid}</c> with an ELF binary of
    /// that architecture, the script, the unit, the example, the README and BUILD-INFO. Minutes and network: opt-in.
    /// </summary>
    [Fact]
    public async Task AgentBundlesStage_Builds_BothTarballsHaveTheBundleLayout()
    {
        if (Environment.GetEnvironmentVariable("MIRBADLLM_DOCKER_BUILD_TESTS") != "1")
        {
            Assert.Skip("Builds the agent-bundles stage with Docker: set MIRBADLLM_DOCKER_BUILD_TESTS=1.");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        string tag = "mirbadllm-agent-bundles-test:" + Guid.NewGuid().ToString("N")[..8];
        string outDir = Path.Combine(Path.GetTempPath(), "mirbadllm-bundle-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (string platform in new[] { "linux/amd64", "linux/arm64" })
            {
                await DockerAsync(ct, "buildx", "build", "--platform", platform, "-f", RepoFile("deploy", "Dockerfile"), "--target", "agent-bundles",
                    "--build-arg", "SOURCE_REVISION=testrev", "-t", tag, "--load", RepoRoot());
            }

            string container = (await DockerAsync(ct, "create", tag)).Trim();
            Directory.CreateDirectory(outDir);
            await DockerAsync(ct, "cp", $"{container}:/bundles/.", outDir);
            await DockerAsync(ct, "rm", container);

            foreach ((string rid, ushort machine) in new[] { ("linux-x64", (ushort)62), ("linux-arm64", (ushort)183) })
            {
                string folder = $"mirbadllm-agent-testrev-{rid}";
                var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                await using (FileStream file = File.OpenRead(Path.Combine(outDir, rid + ".tar.gz")))
                await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                await using (var tar = new TarReader(gzip))
                {
                    while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
                    {
                        byte[] head = new byte[64];
                        int read = entry.DataStream is { } data ? await data.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct) : 0;
                        entries[entry.Name.TrimEnd('/')] = head[..read];
                    }
                }

                Assert.Equal(
                    BundleLayout,
                    entries.Keys.Select(k => k[folder.Length..]).Order(StringComparer.Ordinal));
                byte[] elf = entries[folder + "/mirbadllm-agent"];
                Assert.Equal("\u007fELF"u8.ToArray(), elf[..4]);
                Assert.Equal(machine, BitConverter.ToUInt16(elf, 18));
                Assert.StartsWith($"revision=testrev\nrid={rid}\n", System.Text.Encoding.UTF8.GetString(entries[folder + "/BUILD-INFO"]), StringComparison.Ordinal);
                Assert.NotNull(new AgentBundleCatalog(outDir, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentBundleCatalog>.Instance).Find(rid));
            }

            // The Windows zip (MN-218): one folder with an x64 PE exe and the files of §A.4.5.
            const string winFolder = "mirbadllm-agent-testrev-win-x64";
            using (ZipArchive zip = ZipFile.OpenRead(Path.Combine(outDir, AgentBundleCatalog.FileName(AgentBundleCatalog.WindowsRid))))
            {
                string[] expected = [.. AgentBundleCatalog.WindowsRequiredFiles
                    .Concat(File.Exists(RepoFile("deploy", "agent", "windows", "README.md")) ? ["README.md"] : Array.Empty<string>())
                    .Select(f => $"{winFolder}/{f}").Order(StringComparer.Ordinal)];
                Assert.Equal(expected, zip.Entries.Where(e => e.Name.Length > 0).Select(e => e.FullName).Order(StringComparer.Ordinal));
                byte[] exe = new byte[4096];
                await using (Stream stream = zip.GetEntry($"{winFolder}/mirbadllm-agent.exe")!.Open())
                {
                    await stream.ReadAtLeastAsync(exe, exe.Length, throwOnEndOfStream: false, ct);
                }

                Assert.Equal("MZ"u8.ToArray(), exe[..2]);
                int pe = BitConverter.ToInt32(exe, 0x3C);
                Assert.Equal("PE\0\0"u8.ToArray(), exe[pe..(pe + 4)]);
                Assert.Equal((ushort)0x8664, BitConverter.ToUInt16(exe, pe + 4)); // AMD64
                using var buildInfo = new StreamReader(zip.GetEntry($"{winFolder}/BUILD-INFO")!.Open());
                Assert.StartsWith("revision=testrev\nrid=win-x64\n", await buildInfo.ReadToEndAsync(ct), StringComparison.Ordinal);
                using var lockFile = new StreamReader(zip.GetEntry($"{winFolder}/llama-cpp.lock.json")!.Open());
                Assert.Equal(File.ReadAllText(RepoFile("deploy", "agent", "windows", "llama-cpp.lock.json")).ReplaceLineEndings("\n"), (await lockFile.ReadToEndAsync(ct)).ReplaceLineEndings("\n"));
            }

            Assert.Equal(winFolder, new AgentBundleCatalog(outDir, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentBundleCatalog>.Instance).Find(AgentBundleCatalog.WindowsRid)!.Folder);
        }
        finally
        {
            await DockerAsync(CancellationToken.None, "rmi", "-f", tag);
            if (Directory.Exists(outDir))
            {
                Directory.Delete(outDir, recursive: true);
            }
        }
    }

    private static async Task<string> DockerAsync(CancellationToken ct, params string[] args)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        Assert.True(process.ExitCode == 0, $"docker {string.Join(' ', args)}: {await error}");
        return await output;
    }

    /// <summary>The text of the stage <c>AS {name}</c>: its FROM line to the next FROM.</summary>
    private static string Stage(string dockerfile, string name)
    {
        string text = dockerfile.ReplaceLineEndings("\n");
        Match from = Regex.Match(text, $@"^FROM [^\n]* AS {Regex.Escape(name)}\n", RegexOptions.Multiline);
        Assert.True(from.Success, $"no stage '{name}'");
        int next = text.IndexOf("\nFROM ", from.Index + from.Length, StringComparison.Ordinal);
        return text[from.Index..(next < 0 ? text.Length : next)];
    }

    private static string Flatten(string text) => Regex.Replace(text.Replace("\\\n", " ", StringComparison.Ordinal), @"\s+", " ").Trim();

    /// <summary>The publish options that shape the binary (not the output folder, verbosity or the revision's quoting).</summary>
    private static string[] PublishFlags(string flags) =>
        [.. Regex.Matches(flags, @"--self-contained \S+|-p:(?!SourceRevisionId)[A-Za-z]+=\S+").Select(m => m.Value).Order(StringComparer.Ordinal)];
}
