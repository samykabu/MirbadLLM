using System.Security.Cryptography;
using System.Text.Json;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Deploy;

/// <summary>
/// macOS Phase 3 (docs/macos/implementation-plan.md, M6): the MLX pin the installer provisions from. mlx-lm.lock.json pins
/// uv by tag, SHA-256 and size and names the requirements file by its SHA-256; that file pins mlx-lm at the lock's version
/// and every package with hashes (uv pip sync --require-hashes); the installer and the agent bundle carry both files.
/// </summary>
public sealed partial class DeployArtifactsTests
{
    [Fact]
    public void MlxLock_PinsUvByTag_AndTheRequirementsFileByItsHash()
    {
        using var lockJson = JsonDocument.Parse(File.ReadAllText(RepoFile("deploy", "agent", "macos", "mlx-lm.lock.json")));
        var root = lockJson.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        string version = root.GetProperty("version").GetString()!;
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
        Assert.Matches(@"^3\.\d+\.\d+$", root.GetProperty("python").GetString()!);

        var uv = root.GetProperty("uv");
        string uvVersion = uv.GetProperty("version").GetString()!;
        Assert.Equal("uv-aarch64-apple-darwin.tar.gz", uv.GetProperty("name").GetString());
        Assert.Equal($"https://github.com/astral-sh/uv/releases/download/{uvVersion}/uv-aarch64-apple-darwin.tar.gz", uv.GetProperty("url").GetString());
        Assert.Matches("^[0-9a-f]{64}$", uv.GetProperty("sha256").GetString()!);
        Assert.True(uv.GetProperty("bytes").GetInt64() > 0);

        string requirements = root.GetProperty("requirements").GetString()!;
        Assert.Equal("mlx-lm.lock.txt", requirements);
        byte[] text = File.ReadAllBytes(RepoFile("deploy", "agent", "macos", requirements));
        Assert.DoesNotContain((byte)'\r', text); // .gitattributes keeps it LF, so the hash is the same on every checkout
        Assert.Equal(root.GetProperty("requirementsSha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(text)));

        // Every requirement is pinned with == and followed by at least one --hash line; mlx-lm itself at the lock's version.
        string[] lines = File.ReadAllLines(RepoFile("deploy", "agent", "macos", requirements));
        var packages = lines.Where(l => l.Length > 0 && char.IsAsciiLetterOrDigit(l[0])).ToList();
        Assert.Contains($"mlx-lm=={version} \\", packages);
        Assert.All(packages, p => Assert.Matches(@"^[A-Za-z0-9._-]+==[A-Za-z0-9._+-]+ \\$", p));
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length > 0 && char.IsAsciiLetterOrDigit(lines[i][0]))
            {
                Assert.StartsWith("    --hash=sha256:", lines[i + 1], StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void MacInstaller_ProvisionsMlx_FromThePinnedFiles_AndPackagesThem()
    {
        string script = File.ReadAllText(RepoFile("deploy", "agent", "macos", "install-agent.sh"));

        Assert.Contains("MLX_LOCK_NAME=mlx-lm.lock.json", script, StringComparison.Ordinal);
        Assert.Contains("MLX_EXECUTABLE=mlx-lm-server", script, StringComparison.Ordinal);
        Assert.Contains("--require-hashes --compile-bytecode", script, StringComparison.Ordinal);
        Assert.Contains("\"allFiles\": true", script, StringComparison.Ordinal);
        Assert.Contains("${MLX_TARGET}/venv/bin/python", script, StringComparison.Ordinal); // the registered executable
        foreach (string verb in new[] { "update-mlx) do_update_mlx ;;", "stage-mlx) do_stage_mlx ;;", "--with-mlx) with_mlx=1;" })
        {
            Assert.Contains(verb, script, StringComparison.Ordinal);
        }

        // The bundle carries both MLX files next to the script, as it carries the llama.cpp lock.
        Assert.Contains("for mlx_file in \"${MLX_LOCK_NAME}\" mlx-lm.lock.txt; do", script, StringComparison.Ordinal);
    }
}
