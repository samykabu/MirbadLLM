using MirbadLLM.Core;
using MirbadLLM.Web.Api;

namespace MirbadLLM.Web;

/// <summary>
/// The manual update of one agent (docs/multinode/agent-updates.md U9): its bundle on this master, the hash to check and
/// the commands for its OS, which run the new bundle's own installer <c>upgrade</c> (identity and settings kept, rolled
/// back when the new agent does not come up).
/// </summary>
/// <param name="Rid">The bundle's runtime id.</param>
/// <param name="Url">Where this master serves it.</param>
/// <param name="Sha256">Its SHA-256.</param>
/// <param name="Version">Its revision.</param>
/// <param name="Shell">What to run the commands in.</param>
/// <param name="Commands">One command per line.</param>
public sealed record AgentUpdatePlan(string Rid, string Url, string Sha256, string Version, string Shell, string Commands)
{
    /// <summary>The bundle for a node's OS and architecture: <c>linux-x64</c>, <c>linux-arm64</c>, <c>win-x64</c>, <c>osx-arm64</c>; null otherwise.</summary>
    public static string? RidFor(NodeCapabilities? caps)
    {
        string os = caps?.Os.Family?.Trim().ToLowerInvariant() ?? "";
        string arch = caps?.Arch?.Trim().ToLowerInvariant() ?? "";
        bool arm = arch is "arm64" or "aarch64";
        bool x64 = arch is "amd64" or "x64" or "x86_64";
        return os switch
        {
            "linux" when arm => "linux-arm64",
            "linux" when x64 => "linux-x64",
            "windows" when x64 => "win-x64",
            "macos" when arm => "osx-arm64",
            _ => null,
        };
    }

    /// <summary>The plan for a node from <paramref name="catalog"/> at <paramref name="baseUrl"/>; null when there is no bundle for it.</summary>
    public static AgentUpdatePlan? For(NodeCapabilities? caps, AgentBundleCatalog catalog, Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(baseUrl);
        return RidFor(caps) is { } rid && catalog.Find(rid) is { } bundle
            ? Build(bundle.Rid, AgentBundleCatalog.BundleUrl(baseUrl, rid).AbsoluteUri, bundle.Sha256, bundle.Version, bundle.Folder)
            : null;
    }

    /// <summary>The commands for one bundle (pure, for the tests).</summary>
    public static AgentUpdatePlan Build(string rid, string url, string sha256, string version, string folder)
    {
        string file = AgentBundleCatalog.FileName(rid);
        return rid switch
        {
            "win-x64" => new(rid, url, sha256, version, "Windows PowerShell as administrator",
                $"Invoke-WebRequest -Uri '{url}' -OutFile {file}\n" +
                $"Expand-Archive -Path .\\{file} -DestinationPath . -Force\n" +
                $".\\{folder}\\install-agent.ps1 upgrade -Bundle .\\{file} -BundleSha256 {sha256}"),
            "osx-arm64" => new(rid, url, sha256, version, "Terminal on the Mac",
                $"curl -fLO {url}\n" +
                $"echo '{sha256}  {file}' | shasum -a 256 -c\n" +
                $"tar -xzf {file}\n" +
                $"sudo ./{folder}/install-agent.sh upgrade"),
            _ => new(rid, url, sha256, version, "A shell on the node",
                $"curl -fLO {url}\n" +
                $"echo '{sha256}  {file}' | sha256sum -c\n" +
                $"tar -xzf {file}\n" +
                $"sudo ./{folder}/install-agent.sh upgrade"),
        };
    }
}
