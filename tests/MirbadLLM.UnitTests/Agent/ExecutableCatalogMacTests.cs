using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Runtimes.Process;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// macOS Phase 2 (docs/macos/implementation-plan.md): <see cref="ExecutableCatalog"/> with the macOS version-folder rules
/// over a folder shaped like <c>install-agent.sh</c> leaves it (<c>runtimes/llama.cpp/{tag}/</c>: the Mach-O
/// <c>llama-server</c>, its <c>@rpath</c> dylibs, <c>LICENSE</c> and <c>manifest.json</c>). The rules are passed in, so
/// these run on any OS: an unlisted <c>.dylib</c>, <c>.so</c> or Mach-O file of any name makes the version unverified,
/// paths compare case-insensitively (APFS), and the Windows/Linux rules still ignore those files.
/// </summary>
public sealed class ExecutableCatalogMacTests : IDisposable
{
    private const string Tag = "b11476";
    private readonly List<MacVersionFolder> _folders = [];

    public void Dispose()
    {
        foreach (var folder in _folders)
        {
            folder.Dispose();
        }
    }

    [Fact]
    public async Task MacFolder_Verifies_AndResolvesTheMachOServer_WithoutCuda()
    {
        var folder = NewFolder();
        var catalog = folder.Catalog(ExecutablePlatform.MacOS);

        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new ResolvedExecutable(ExecutableNames.LlamaServer, Tag, folder.ExecutablePath, folder.Folder, null),
            catalog.Resolve(ExecutableNames.LlamaServer, version: null));
        Assert.Equal(new ExecutableVersionState(ExecutableNames.LlamaServer, Tag, null, Verified: true, IsDefault: true, Problem: null),
            Assert.Single(catalog.List()));
    }

    [Theory]
    [InlineData("libggml-evil.dylib", new byte[] { 1, 2, 3 })] // by extension, whatever the content
    [InlineData("sub/libfoo.so", new byte[] { 1, 2, 3 })]
    [InlineData("Plugin.DYLIB", new byte[] { 1 })] // any letter case
    [InlineData("llama-helper", new byte[] { 0xCF, 0xFA, 0xED, 0xFE, 0x0C, 0x00, 0x00, 0x01 })] // arm64 Mach-O, no extension
    [InlineData("helper.bin", new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x00, 0x00, 0x02 })] // universal binary
    [InlineData("ggml-metal.metal", new byte[] { 0xFE, 0xED, 0xFA, 0xCF, 0x00 })] // big-endian 64-bit magic
    [InlineData("tool.exe", new byte[] { 0x4D, 0x5A })] // the Windows rule still applies on macOS
    public async Task UnlistedLibraryOrMachO_MakesTheMacVersionUnverified(string file, byte[] content)
    {
        var folder = NewFolder();
        folder.Plant(file, content);
        var catalog = folder.Catalog(ExecutablePlatform.MacOS);

        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        var state = Assert.Single(catalog.List());
        Assert.False(state.Verified);
        Assert.Equal($"{file}: an executable file the manifest does not list", state.Problem);
        var ex = Assert.Throws<AgentException>(() => catalog.Resolve(ExecutableNames.LlamaServer, Tag));
        Assert.Equal(422, ex.StatusCode);
        Assert.Equal(ErrorCodes.ExecutableUnverified, ex.Code);
        Assert.Contains(file, ex.Message, StringComparison.Ordinal);
        Assert.Contains("install-agent.sh --update-llama", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WindowsAndLinuxRules_StillFlagOnlyExeAndDll(bool windows)
    {
        var platform = windows ? ExecutablePlatform.Windows : ExecutablePlatform.Other;
        var folder = NewFolder();
        folder.Plant("libggml-evil.dylib", [1, 2, 3]);
        folder.Plant("libfoo.so", [1, 2, 3]);
        folder.Plant("llama-helper", [0xCF, 0xFA, 0xED, 0xFE, 0x0C, 0x00, 0x00, 0x01]);
        var catalog = folder.Catalog(platform);

        await catalog.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(catalog.List()).Verified);

        folder.Plant("version.dll", [1, 2, 3]);
        var ex = Assert.Throws<AgentException>(() => catalog.Resolve(ExecutableNames.LlamaServer, Tag));
        Assert.Contains("version.dll", ex.Message, StringComparison.Ordinal);
        Assert.Contains("install-agent.ps1 -LlamaCpp", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonMachOFilesWithoutALibraryExtension_AreFine_OnMac()
    {
        var folder = NewFolder();
        folder.Plant("README.txt", "notes"u8.ToArray());
        folder.Plant("tiny", [0xCF, 0xFA]); // shorter than a magic number
        folder.Plant("empty", []);
        folder.Plant("script.sh", "#!/bin/sh\necho hi\n"u8.ToArray());
        var catalog = folder.Catalog(ExecutablePlatform.MacOS);

        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(catalog.List()).Verified);
    }

    [Fact]
    public async Task MachOPlantedAfterVerification_IsCaughtByTheNextResolve_AndRemovingItRestoresTheVersion()
    {
        var folder = NewFolder();
        var catalog = folder.Catalog(ExecutablePlatform.MacOS);
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);
        catalog.Resolve(ExecutableNames.LlamaServer, Tag);

        // No extension: only the content says it is code (the change check reads the header, not just the names).
        folder.Plant("libggml-blas.0.dylib.bak", [0xCF, 0xFA, 0xED, 0xFE, 0x0C, 0x00, 0x00, 0x01]);
        var ex = Assert.Throws<AgentException>(() => catalog.Resolve(ExecutableNames.LlamaServer, Tag));
        Assert.Contains("libggml-blas.0.dylib.bak", ex.Message, StringComparison.Ordinal);

        File.Delete(Path.Combine(folder.Folder, "libggml-blas.0.dylib.bak"));
        Assert.Equal(folder.ExecutablePath, catalog.Resolve(ExecutableNames.LlamaServer, Tag).Path);
    }

    [Fact]
    public async Task TamperedDylib_IsAHashMismatch_OnMac()
    {
        var folder = NewFolder();
        folder.Tamper("libggml-metal.0.dylib");
        var catalog = folder.Catalog(ExecutablePlatform.MacOS);

        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("libggml-metal.0.dylib: hash mismatch", Assert.Single(catalog.List()).Problem);
    }

    [Fact]
    public async Task PathsCompareCaseInsensitively_OnMac_LikeAPFS()
    {
        // The manifest names the dylib in lower case; the folder holds it as LibGGML-Metal.0.dylib.
        var folder = NewFolder();
        folder.Rename("libggml-metal.0.dylib", "LibGGML-Metal.0.dylib");
        bool caseInsensitiveDisk = File.Exists(Path.Combine(folder.Folder, "libggml-metal.0.dylib"));

        var mac = folder.Catalog(ExecutablePlatform.MacOS);
        await mac.RefreshAsync(TestContext.Current.CancellationToken);
        var state = Assert.Single(mac.List());

        // Never "an executable file the manifest does not list": the listed name matches regardless of case. On a
        // case-insensitive disk (APFS, NTFS) the file is found and hashed; on a case-sensitive one (a Linux CI) it is missing.
        if (caseInsensitiveDisk)
        {
            Assert.True(state.Verified, state.Problem);
        }
        else
        {
            Assert.Equal("libggml-metal.0.dylib: missing", state.Problem);
        }

        // The Linux rules compare ordinally (and ignore .dylib): only the hash step can find the file.
        var linux = folder.Catalog(ExecutablePlatform.Other);
        await linux.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(caseInsensitiveDisk ? null : "libggml-metal.0.dylib: missing", Assert.Single(linux.List()).Problem);
    }

    [Fact]
    public async Task DuplicateListingThatDiffersOnlyInCase_IsRefused_OnMac()
    {
        var folder = NewFolder();
        folder.WriteManifest(m => m with { Files = [.. m.Files, m.Files.First(f => f.Path == "LICENSE") with { Path = "license" }] });
        var catalog = folder.Catalog(ExecutablePlatform.MacOS);

        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal($"{ExecutableCatalog.ManifestFileName} lists 'license' twice", Assert.Single(catalog.List()).Problem);
    }

    [Theory]
    [InlineData(new byte[] { 0xCF, 0xFA, 0xED, 0xFE }, true)]
    [InlineData(new byte[] { 0xFE, 0xED, 0xFA, 0xCF }, true)]
    [InlineData(new byte[] { 0xCE, 0xFA, 0xED, 0xFE }, true)]
    [InlineData(new byte[] { 0xFE, 0xED, 0xFA, 0xCE }, true)]
    [InlineData(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }, true)]
    [InlineData(new byte[] { 0xBE, 0xBA, 0xFE, 0xCA }, true)]
    [InlineData(new byte[] { 0xCA, 0xFE, 0xBA, 0xBF }, true)]
    [InlineData(new byte[] { 0x7F, 0x45, 0x4C, 0x46 }, false)] // ELF
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00 }, false)] // PE ("MZ")
    [InlineData(new byte[] { 0xCF, 0xFA, 0xED }, false)] // truncated
    [InlineData(new byte[] { }, false)]
    public void StartsWithMachOMagic_ReadsTheFirstFourBytes(byte[] header, bool expected)
    {
        var folder = NewFolder();
        string path = Path.Combine(folder.Folder, "probe");
        File.WriteAllBytes(path, header.Length < 4 ? header : [.. header, 0x00, 0x11]); // a short file ends there

        Assert.Equal(expected, ExecutableCatalog.StartsWithMachOMagic(path));
        Assert.False(ExecutableCatalog.StartsWithMachOMagic(Path.Combine(folder.Folder, "gone")));
    }

    [Fact]
    public void CurrentPlatform_FollowsTheOs()
    {
        var expected = OperatingSystem.IsWindows() ? ExecutablePlatform.Windows
            : OperatingSystem.IsMacOS() ? ExecutablePlatform.MacOS
            : ExecutablePlatform.Other;
        Assert.Equal(expected, ExecutableCatalog.CurrentPlatform);
    }

    private MacVersionFolder NewFolder()
    {
        var folder = new MacVersionFolder(Tag);
        _folders.Add(folder);
        return folder;
    }

    /// <summary>
    /// A macOS llama.cpp version folder as <c>install-agent.sh</c> leaves it: a Mach-O <c>llama-server</c>, its dylibs under
    /// the names their install names use (<c>libggml-metal.0.dylib</c>), <c>LICENSE</c>, and <c>manifest.json</c>.
    /// </summary>
    private sealed class MacVersionFolder : IDisposable
    {
        private static readonly string[] Files = ["llama-server", "libllama-server-impl.dylib", "libllama.0.dylib", "libggml-metal.0.dylib", "LICENSE"];
        private readonly Dictionary<string, byte[]> _original = new(StringComparer.Ordinal);
        private readonly string _root;

        public MacVersionFolder(string version)
        {
            Version = version;
            _root = Path.Combine(Path.GetTempPath(), "macos-p2-" + Guid.NewGuid().ToString("N")[..12]);
            Folder = Path.Combine(_root, "runtimes", "llama.cpp", version);
            Directory.CreateDirectory(Folder);
            foreach (string file in Files)
            {
                byte[] content = file switch
                {
                    "LICENSE" => "MIT License"u8.ToArray(),
                    _ => [0xCF, 0xFA, 0xED, 0xFE, 0x0C, 0x00, 0x00, 0x01, .. RandomNumberGenerator.GetBytes(32 * 1024 + file.Length)],
                };
                _original[file] = content;
                File.WriteAllBytes(Path.Combine(Folder, file), content);
            }

            WriteManifest();
        }

        public string Version { get; }

        public string Folder { get; }

        public string ManifestPath => Path.Combine(Folder, ExecutableCatalog.ManifestFileName);

        public string ExecutablePath => Path.Combine(Folder, "llama-server");

        public ExecutableCatalog Catalog(ExecutablePlatform platform)
        {
            var options = new ExecutableOptions { Name = ExecutableNames.LlamaServer, Default = Version };
            options.Versions.Add(new ExecutableVersionOptions { Version = Version, Path = ExecutablePath, Manifest = ManifestPath });
            return new ExecutableCatalog([options], NullLogger<ExecutableCatalog>.Instance, platform);
        }

        public void WriteManifest(Func<ExecutableManifest, ExecutableManifest>? edit = null)
        {
            var manifest = new ExecutableManifest
            {
                Name = ExecutableNames.LlamaServer,
                Version = Version,
                Sources = [$"llama-{Version}-bin-macos-arm64.tar.gz"],
                Files = [.. _original.Select(kv => new ExecutableManifestFile(kv.Key, Convert.ToHexStringLower(SHA256.HashData(kv.Value)), kv.Value.Length))],
            };
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(edit?.Invoke(manifest) ?? manifest, AgentJson.Options));
        }

        public void Plant(string relative, byte[] content)
        {
            string path = Path.Combine(Folder, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        /// <summary>Flips one byte (size unchanged).</summary>
        public void Tamper(string file)
        {
            string path = Path.Combine(Folder, file);
            byte[] content = File.ReadAllBytes(path);
            content[content.Length / 2] ^= 0xFF;
            File.WriteAllBytes(path, content);
        }

        /// <summary>Renames a file to a name that differs only in letter case (through a temporary name, for NTFS/APFS).</summary>
        public void Rename(string from, string to)
        {
            string temporary = Path.Combine(Folder, from + ".renaming");
            File.Move(Path.Combine(Folder, from), temporary);
            File.Move(temporary, Path.Combine(Folder, to));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
