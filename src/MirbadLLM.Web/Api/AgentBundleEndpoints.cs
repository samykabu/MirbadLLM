using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Web.Api;

/// <summary>
/// An agent bundle the master serves, baked into the master image (deploy/Dockerfile, stage <c>agent-bundles</c>), with one
/// folder <c>mirbadllm-agent-{version}-{rid}</c>. Linux: the tarball <c>install-agent.sh package</c> makes (the
/// self-contained binary, <c>install-agent.sh</c>, the unit, the example configuration, the README and <c>BUILD-INFO</c>).
/// Windows (<c>win-x64</c>, design-p3p4 §A.4.5, MN-218): a zip with the self-contained single-file
/// <c>mirbadllm-agent.exe</c>, <c>install-agent.ps1</c>, <c>agent.windows.example.json</c>, <c>llama-cpp.lock.json</c>,
/// <c>BUILD-INFO</c> and, when the image has one, the README.
/// </summary>
/// <param name="Rid"><c>linux-x64</c>, <c>linux-arm64</c> or <c>win-x64</c>.</param>
/// <param name="FilePath">The tarball (the zip for <c>win-x64</c>) on disk.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the file, computed once when the catalog is built.</param>
/// <param name="Size">Bytes.</param>
/// <param name="Folder">The bundle's single top-level folder.</param>
/// <param name="Version">BUILD-INFO's <c>revision</c>, else the version part of <paramref name="Folder"/>.</param>
/// <param name="Built">BUILD-INFO's <c>built</c> time, when present.</param>
public sealed record AgentBundle(string Rid, string FilePath, string Sha256, long Size, string Folder, string Version, string? Built);

/// <summary>
/// The agent bundles in <see cref="NodeApiOptions.AgentBundleDirectory"/> (<c>{rid}.tar.gz</c> and <c>win-x64.zip</c>, see
/// <see cref="FileName"/>), read and hashed once when the catalog is built (at startup). A tarball that is missing or does not have the bundle layout is left out (and logged),
/// so a dev run without the image simply has no bundles: <c>GET /agent/bundles</c> answers 404 and the join command expects
/// <c>install-agent.sh</c> in the current folder, as before. Nothing here is secret.
/// </summary>
public sealed partial class AgentBundleCatalog
{
    /// <summary>The folder next to the master's binaries the image puts the bundles in (<c>/app/agent-bundles</c>).</summary>
    public const string DefaultDirectoryName = "agent-bundles";

    /// <summary>The route prefix (main port only: the enroll listener serves nothing but <c>/enroll</c>).</summary>
    public const string RoutePrefix = "/agent/bundles";

    /// <summary>The Windows bundle's runtime identifier (a zip, design-p3p4 §A.4.5).</summary>
    public const string WindowsRid = "win-x64";

    /// <summary>
    /// The architectures served, in the order <see cref="AgentDownload.Bundles"/> lists them: the Linux tarballs, the
    /// Windows zip (MN-218), then the macOS tarball (docs/macos/implementation-plan.md, Phase 1).
    /// </summary>
    public static readonly IReadOnlyList<string> Rids = ["linux-x64", "linux-arm64", WindowsRid, MacosRid];

    /// <summary>The macOS bundle's runtime identifier (Apple Silicon only; a tarball like Linux).</summary>
    public const string MacosRid = "osx-arm64";

    /// <summary>The Linux architectures: the <c>.tar.gz</c> bundles <c>install-agent.sh package</c> makes.</summary>
    public static readonly IReadOnlyList<string> LinuxRids = ["linux-x64", "linux-arm64"];

    /// <summary>The files every Windows bundle holds under its folder (design-p3p4 §A.4.5; the README is optional).</summary>
    public static readonly IReadOnlyList<string> WindowsRequiredFiles =
        ["mirbadllm-agent.exe", "install-agent.ps1", "agent.windows.example.json", "llama-cpp.lock.json", "BUILD-INFO"];

    private static readonly string[] LinuxRequiredFiles = ["mirbadllm-agent", "install-agent.sh"];

    private const string FolderPrefix = "mirbadllm-agent-";
    private const long MaxBuildInfoBytes = 4096;

    private readonly Dictionary<string, AgentBundle> _byRid;

    /// <summary>Reads the bundles of <paramref name="directory"/> (null or empty = <see cref="DefaultDirectory"/>).</summary>
    public AgentBundleCatalog(string? directory, ILogger<AgentBundleCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        Directory = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : Path.GetFullPath(directory);
        var bundles = new List<AgentBundle>();
        foreach (string rid in Rids)
        {
            string path = Path.Combine(Directory, FileName(rid));
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                AgentBundle bundle = Read(rid, path);
                bundles.Add(bundle);
                string fileName = FileName(rid);
                LogBundle(logger, rid, bundle.Version, bundle.Sha256, bundle.Size, fileName);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
            {
                LogUnusable(logger, ex, path);
            }
        }

        if (bundles.Count == 0)
        {
            LogNone(logger, Directory);
        }

        Bundles = bundles;
        _byRid = bundles.ToDictionary(b => b.Rid, StringComparer.Ordinal);
    }

    /// <summary>Creates the catalog from <see cref="NodeApiOptions.AgentBundleDirectory"/>.</summary>
    public AgentBundleCatalog(IOptions<NodeApiOptions> options, ILogger<AgentBundleCatalog> logger)
        : this((options ?? throw new ArgumentNullException(nameof(options))).Value.AgentBundleDirectory, logger)
    {
    }

    /// <summary><c>{AppContext.BaseDirectory}/agent-bundles</c>.</summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, DefaultDirectoryName);

    /// <summary>The directory read.</summary>
    public string Directory { get; }

    /// <summary>The usable bundles, in <see cref="Rids"/> order.</summary>
    public IReadOnlyList<AgentBundle> Bundles { get; }

    /// <summary>The bundle of <paramref name="rid"/>, or null.</summary>
    public AgentBundle? Find(string rid) => _byRid.GetValueOrDefault(rid);

    /// <summary>The bundle served as <paramref name="fileName"/> (<c>{rid}.tar.gz</c>, <c>win-x64.zip</c>), or null.</summary>
    public AgentBundle? FindFile(string fileName) =>
        Rids.FirstOrDefault(rid => string.Equals(FileName(rid), fileName, StringComparison.Ordinal)) is { } rid ? Find(rid) : null;

    /// <summary>A bundle's file name in the folder and in its URL: <c>win-x64.zip</c>, else <c>{rid}.tar.gz</c>.</summary>
    public static string FileName(string rid)
    {
        ArgumentException.ThrowIfNullOrEmpty(rid);
        return IsZip(rid) ? rid + ".zip" : rid + ".tar.gz";
    }

    /// <summary>The media type a bundle of <paramref name="rid"/> is served with.</summary>
    public static string ContentType(string rid) => IsZip(rid) ? "application/zip" : "application/gzip";

    private static bool IsZip(string rid) => rid.StartsWith("win-", StringComparison.Ordinal);

    /// <summary>The download URL of <paramref name="rid"/> under <paramref name="baseUrl"/> (the master's main address).</summary>
    public static Uri BundleUrl(Uri baseUrl, string rid)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        var root = baseUrl.AbsolutePath.EndsWith('/') ? baseUrl : new Uri(baseUrl.GetLeftPart(UriPartial.Path) + "/");
        return new Uri(root, $"{RoutePrefix.TrimStart('/')}/{FileName(rid)}");
    }

    /// <summary>What a join or rekey command downloads from <paramref name="baseUrl"/>; null when there are no bundles.</summary>
    public AgentDownload? DownloadFrom(Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return Bundles.Count == 0
            ? null
            : new AgentDownload { Bundles = [.. Bundles.Select(b => new AgentBundleLink(b.Rid, BundleUrl(baseUrl, b.Rid).AbsoluteUri, b.Sha256, b.Version))] };
    }

    private static AgentBundle Read(string rid, string path)
    {
        string sha256;
        long size;
        using (FileStream file = File.OpenRead(path))
        {
            sha256 = Convert.ToHexStringLower(SHA256.HashData(file));
            size = file.Length;
        }

        var folders = new HashSet<string>(StringComparer.Ordinal);
        var files = new HashSet<string>(StringComparer.Ordinal);
        string? buildInfo = IsZip(rid) ? ReadZip(path, folders, files) : ReadTar(path, folders, files);

        if (folders.Count != 1)
        {
            throw new InvalidDataException($"expected one top-level folder, found {folders.Count}");
        }

        string folder = folders.Single();
        Match m = FolderRegex().Match(folder);
        if (!m.Success || m.Groups["rid"].Value != rid)
        {
            throw new InvalidDataException($"top-level folder '{folder}' is not {FolderPrefix}<version>-{rid}");
        }

        foreach (string required in IsZip(rid) ? WindowsRequiredFiles : LinuxRequiredFiles)
        {
            if (!files.Contains(required))
            {
                throw new InvalidDataException($"{folder}/{required} is missing");
            }
        }

        string version = BuildInfoValue(buildInfo, "revision") is { } revision && VersionRegex().IsMatch(revision)
            ? revision
            : m.Groups["version"].Value;
        return new AgentBundle(rid, path, sha256, size, folder, version, BuildInfoValue(buildInfo, "built"));
    }

    /// <summary>Collects the tarball's top-level folders and the paths below them; returns BUILD-INFO's text (when small).</summary>
    private static string? ReadTar(string path, HashSet<string> folders, HashSet<string> files)
    {
        string? buildInfo = null;
        using FileStream file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            string name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
            if (AddEntry(name, folders, files) == "BUILD-INFO" && entry.DataStream is { } data && entry.Length <= MaxBuildInfoBytes)
            {
                using var reader = new StreamReader(data, Encoding.UTF8);
                buildInfo = reader.ReadToEnd();
            }
        }

        return buildInfo;
    }

    /// <summary>
    /// As <see cref="ReadTar"/> for the Windows zip. Entry names are read with either separator (a zip written on Windows may
    /// use '\'). A rooted name, a drive or a '..' segment makes the bundle unusable: the master never serves a zip whose
    /// <c>Expand-Archive</c> would write outside the command's temp folder.
    /// </summary>
    private static string? ReadZip(string path, HashSet<string> folders, HashSet<string> files)
    {
        string? buildInfo = null;
        using ZipArchive zip = ZipFile.OpenRead(path);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':', StringComparison.Ordinal) || name.Split('/').Contains(".."))
            {
                throw new InvalidDataException($"zip entry '{entry.FullName}' is outside the bundle folder");
            }

            if (AddEntry(name, folders, files) == "BUILD-INFO" && entry.Length <= MaxBuildInfoBytes)
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                buildInfo = reader.ReadToEnd();
            }
        }

        return buildInfo;
    }

    /// <summary>Records the top-level folder of <paramref name="name"/> and its path below it; returns that path (null for none).</summary>
    private static string? AddEntry(string name, HashSet<string> folders, HashSet<string> files)
    {
        name = name.TrimEnd('/');
        // The bundle's pre-rename alias folder (a link to the bundle folder, or the Windows installer stub), which the
        // update helper installed on a pre-rename node looks for: not the bundle's own folder.
        if (name.Length == 0 || name.StartsWith(LegacyNames.BundleFolderPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        int slash = name.IndexOf('/', StringComparison.Ordinal);
        folders.Add(slash < 0 ? name : name[..slash]);
        if (slash < 0)
        {
            return null;
        }

        string relative = name[(slash + 1)..];
        files.Add(relative);
        return relative;
    }

    private static string? BuildInfoValue(string? buildInfo, string key)
    {
        if (buildInfo is null)
        {
            return null;
        }

        foreach (string line in buildInfo.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith(key + "=", StringComparison.Ordinal))
            {
                string value = trimmed[(key.Length + 1)..].Trim();
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    [GeneratedRegex("^mirbadllm-agent-(?<version>[A-Za-z0-9._-]+)-(?<rid>linux-x64|linux-arm64|win-x64|osx-arm64)$")]
    private static partial Regex FolderRegex();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex VersionRegex();

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent bundle {Rid} {Version}: sha256 {Sha256}, {Size} bytes (GET /agent/bundles/{FileName}).")]
    private static partial void LogBundle(ILogger logger, string rid, string version, string sha256, long size, string fileName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent bundle {Path} is not usable and is not served.")]
    private static partial void LogUnusable(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "No agent bundles in {Directory}: GET /agent/bundles answers 404 and join commands expect install-agent.sh in the current folder.")]
    private static partial void LogNone(ILogger logger, string directory);
}

/// <summary>
/// The anonymous, read-only agent bundle downloads on the main port (no token, no secrets: the join token is what admits a
/// node, and the command checks each tarball's SHA-256 before running anything from it):
/// <list type="table">
/// <item><term><c>GET /agent/bundles</c></term><description>Index: rid, url, sha256, size, version per bundle (404 when none).</description></item>
/// <item><term><c>GET /agent/bundles/{rid}.tar.gz</c>, <c>GET /agent/bundles/win-x64.zip</c></term><description>The bundle (404 for an unknown name or a missing bundle).</description></item>
/// </list>
/// Outside <c>/api</c> (no admin cookie, no API key) and outside the <c>/v1</c> router. Not served in recovery mode
/// (<see cref="MasterHosting.UseRecoveryHostGate"/>: 503 <c>recovery_mode</c>): no node joins a master that is restoring.
/// </summary>
public static class AgentBundleEndpoints
{
    /// <summary>Registers <see cref="AgentBundleCatalog"/> (built once, on first resolution: <see cref="MapAgentBundles"/> resolves it at startup).</summary>
    public static IServiceCollection AddMirbadLLMAgentBundles(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp => new AgentBundleCatalog(
            sp.GetRequiredService<IOptions<NodeApiOptions>>(), sp.GetRequiredService<ILogger<AgentBundleCatalog>>()));
        return services;
    }

    /// <summary>Maps the routes listed on the class, anonymous; builds the catalog now so the hashes are computed at startup.</summary>
    public static IEndpointRouteBuilder MapAgentBundles(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        _ = endpoints.ServiceProvider.GetRequiredService<AgentBundleCatalog>();
        var group = endpoints.MapGroup(AgentBundleCatalog.RoutePrefix).AllowAnonymous();

        group.MapGet("", (HttpContext http, AgentBundleCatalog catalog) =>
        {
            if (catalog.Bundles.Count == 0)
            {
                return NoBundles();
            }

            Uri baseUrl = RequestBase(http);
            http.Response.Headers.CacheControl = "no-cache";
            return Results.Ok(new
            {
                bundles = catalog.Bundles.Select(b => new
                {
                    rid = b.Rid,
                    url = AgentBundleCatalog.BundleUrl(baseUrl, b.Rid).AbsoluteUri,
                    sha256 = b.Sha256,
                    size = b.Size,
                    version = b.Version,
                    built = b.Built,
                    folder = b.Folder,
                }),
            });
        });

        group.MapGet("/{file}", (string file, HttpContext http, AgentBundleCatalog catalog) =>
        {
            if (catalog.FindFile(file) is not { } bundle)
            {
                return catalog.Bundles.Count == 0
                    ? NoBundles()
                    : Results.Problem(
                        detail: $"No agent bundle '{file}'. This master serves: {string.Join(", ", catalog.Bundles.Select(b => AgentBundleCatalog.FileName(b.Rid)))}.",
                        statusCode: StatusCodes.Status404NotFound,
                        extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotFound });
            }

            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Content-SHA256"] = bundle.Sha256;
            return Results.File(bundle.FilePath, AgentBundleCatalog.ContentType(bundle.Rid), $"mirbadllm-agent-{AgentBundleCatalog.FileName(bundle.Rid)}",
                entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{bundle.Sha256}\""), enableRangeProcessing: true);
        });

        return endpoints;
    }

    /// <summary>The master's address as the caller reached it: <c>{scheme}://{host[:port]}{path base}/</c>.</summary>
    internal static Uri RequestBase(HttpContext http) =>
        new($"{http.Request.Scheme}://{(http.Request.Host.HasValue ? http.Request.Host.Value : "localhost")}{http.Request.PathBase}/");

    // A body is always written, so the status-code pages middleware leaves the 404 alone (no re-executed HTML page).
    private static IResult NoBundles() => Results.Problem(
        title: "No agent bundles",
        detail: "This master has no agent bundles: they are built into the master image (deploy/Dockerfile), and a dev run has none. " +
            "Run install-agent.sh from a mirbadllm checkout, or from a bundle made with 'install-agent.sh package'.",
        statusCode: StatusCodes.Status404NotFound,
        extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotFound });
}
