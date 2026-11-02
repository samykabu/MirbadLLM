using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace MirbadLLM.UnitTests.Web;

/// <summary>
/// Agent bundle tarballs in the layout <c>install-agent.sh package</c> makes (one folder
/// <c>mirbadllm-agent-{version}-{rid}</c>), for the catalog, endpoint and command tests.
/// </summary>
internal static class AgentBundleFixtures
{
    /// <summary>Writes <c>{dir}/{rid}.tar.gz</c>; returns its lower-case hex SHA-256.</summary>
    public static string Write(string dir, string rid, string version = "abc123def456", string? installScript = null,
        bool withBuildInfo = true, string? folder = null, bool withBinary = true)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, rid + ".tar.gz");
        string top = folder ?? $"mirbadllm-agent-{version}-{rid}";
        using (FileStream file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, top + "/") { Mode = (UnixFileMode)0b111_101_101 });
            if (withBinary)
            {
                // Not a real agent: random bytes stand in for the self-contained binary (the hash is what matters).
                Add(tar, $"{top}/mirbadllm-agent", RandomNumberGenerator.GetBytes(4096), executable: true);
            }

            Add(tar, $"{top}/install-agent.sh", Encoding.UTF8.GetBytes(installScript ?? "#!/usr/bin/env bash\necho fake installer\n"), executable: true);
            Add(tar, $"{top}/mirbadllm-agent.service", Encoding.UTF8.GetBytes("[Unit]\n"), executable: false);
            Add(tar, $"{top}/agent.linux.example.json", Encoding.UTF8.GetBytes("{}\n"), executable: false);
            Add(tar, $"{top}/README.md", Encoding.UTF8.GetBytes("# agent\n"), executable: false);
            if (withBuildInfo)
            {
                Add(tar, $"{top}/BUILD-INFO", Encoding.UTF8.GetBytes($"revision={version}\nrid={rid}\nbuilt=2026-10-07T12:00:00Z\n"), executable: false);
            }
        }

        return Sha256(path);
    }

    public static string Sha256(string path)
    {
        using FileStream file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    private static void Add(TarWriter tar, string name, byte[] content, bool executable)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            Mode = executable ? (UnixFileMode)0b111_101_101 : (UnixFileMode)0b110_100_100,
            DataStream = new MemoryStream(content),
        };
        tar.WriteEntry(entry);
    }
}
