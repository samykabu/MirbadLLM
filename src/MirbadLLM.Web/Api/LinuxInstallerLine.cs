using System.Text;
using System.Text.RegularExpressions;
using MirbadLLM.Core;

namespace MirbadLLM.Web.Api;

/// <summary>
/// The Linux <c>install-agent.sh</c> line of a join or rekey token (design §12.1, MN-103/MN-114), built from its parts so
/// both forms come from one place (W8 review MN-128): <see cref="WithToken"/>, the one-line form the master returns in
/// <see cref="JoinCommands.Linux"/> (token in the environment of <c>sudo</c>: briefly visible in <c>ps</c>, kept in shell
/// history), and <see cref="Prompt"/>, the same installer arguments without the token, so the installer asks for it on its
/// terminal. The token is never part of this record.
/// <para>With an <see cref="AgentDownload"/> (the master serves agent bundles, <c>GET /agent/bundles</c>) the line is one
/// paste for a clean PC with curl and sudo: in a subshell it picks the bundle by <c>uname -m</c>, downloads it into a temp
/// folder, checks it against the SHA-256 the master put in the command (<c>sha256sum -c</c>, so plain HTTP is safe), unpacks
/// it and runs the bundle's <c>install-agent.sh install</c> (or <c>rekey</c>); the folder is removed when the subshell ends.
/// Without one it is the pre-bundle line, run in the folder that holds <c>install-agent.sh</c>.</para>
/// </summary>
public sealed partial record LinuxInstallerLine
{
    /// <summary>The variable the installer (and <c>join</c>) reads the token from.</summary>
    public const string TokenVariable = "MIRBADLLM_JOIN_TOKEN";

    /// <summary>The tarball's name in the command's temp folder.</summary>
    public const string DownloadFile = "agent.tar.gz";

    private const string Installer = "./install-agent.sh";

    /// <summary>The bundle's script after unpacking (one folder <c>mirbadllm-agent-{version}-{rid}</c>).</summary>
    private const string BundleInstaller = "./mirbadllm-agent-*/install-agent.sh";

    /// <summary><c>uname -m</c> patterns per bundle architecture.</summary>
    private static readonly Dictionary<string, string> Machines = new(StringComparer.Ordinal)
    {
        ["linux-x64"] = "x86_64|amd64",
        ["linux-arm64"] = "aarch64|arm64",
    };

    private readonly string _verb;
    private readonly AgentDownload? _download;

    private LinuxInstallerLine(string verb, string arguments, string legacyArguments, AgentDownload? download)
    {
        _verb = verb;
        Arguments = arguments;
        LegacyArguments = legacyArguments;
        _download = download;
    }

    /// <summary>The installer arguments after the verb (no token).</summary>
    public string Arguments { get; }

    /// <summary>The arguments of the pre-bundle line (no verb for a join, <c>--rekey</c> for a rekey).</summary>
    private string LegacyArguments { get; }

    /// <summary>True when the line downloads and checks the agent bundle itself.</summary>
    public bool DownloadsAgent => _download is not null;

    /// <summary>A join: <c>install --node-id … --enroll-url … --ca-hash …</c>, downloading <paramref name="download"/> when given.</summary>
    public static LinuxInstallerLine Join(string nodeId, string enrollUrl, string caHash, AgentDownload? download = null)
    {
        string arguments = $"--node-id {nodeId} --enroll-url {enrollUrl} --ca-hash {caHash}";
        return new("install", arguments, arguments, Usable(download));
    }

    /// <summary>A rekey of an installed node: <c>rekey --node-id … --node-uid … --enroll-url … --ca-hash …</c>.</summary>
    public static LinuxInstallerLine Rekey(string nodeId, string nodeUid, string enrollUrl, string caHash, AgentDownload? download = null)
    {
        string arguments = $"--node-id {nodeId} --node-uid {nodeUid} --enroll-url {enrollUrl} --ca-hash {caHash}";
        return new("rekey", arguments, "--rekey " + arguments, Usable(download));
    }

    /// <summary>
    /// The line of a minted token, rebuilt from its response (and the node uid a rekey line carries), when it is exactly
    /// the line the master returned; null otherwise (another master version or shape: the page then offers only the
    /// master's own line instead of guessing a prompt form).
    /// </summary>
    public static LinuxInstallerLine? Of(JoinTokenCreateResponse response, string? nodeUid)
    {
        ArgumentNullException.ThrowIfNull(response);
        LinuxInstallerLine? line = response.Purpose == JoinTokenPurpose.Rekey
            ? nodeUid is { Length: > 0 } uid ? Rekey(response.NodeId, uid, response.EnrollUrl, response.CaHash, response.AgentDownload) : null
            : Join(response.NodeId, response.EnrollUrl, response.CaHash, response.AgentDownload);
        return line is not null && string.Equals(line.WithToken(response.Token), response.Commands.Linux, StringComparison.Ordinal)
            ? line
            : null;
    }

    /// <summary>The one-line form with <paramref name="token"/> (base64url: no quoting needed) in <c>sudo</c>'s environment.</summary>
    public string WithToken(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return _download is null
            ? $"sudo {TokenVariable}='{token}' {Installer} {LegacyArguments}"
            : Downloading($"sudo {TokenVariable}='{token}' {BundleInstaller} {_verb} {Arguments}");
    }

    /// <summary>
    /// The form without the token: the installer prompts for it on the terminal. The bundle form keeps
    /// <c>--preserve-env</c>, so a token exported beforehand is used instead of the prompt.
    /// </summary>
    public string Prompt() => _download is null
        ? $"sudo {Installer} {LegacyArguments}"
        : Downloading($"sudo --preserve-env={TokenVariable} {BundleInstaller} {_verb} {Arguments}");

    /// <summary>The download, check and unpack steps around <paramref name="install"/>, as one subshell line.</summary>
    private string Downloading(string install)
    {
        var text = new StringBuilder();
        text.Append("(set -e; d=\"$(mktemp -d)\"; trap 'rm -rf \"$d\"' EXIT; cd \"$d\"; case \"$(uname -m)\" in ");
        foreach (AgentBundleLink bundle in _download!.Bundles)
        {
            text.Append(Machines[bundle.Rid]).Append(") u=").Append(SingleQuoted(bundle.Url)).Append(" h=").Append(bundle.Sha256).Append(" ;; ");
        }

        text.Append("*) echo \"the master has no mirbadllm agent for $(uname -m)\" >&2; exit 1 ;; esac; ");
        text.Append("curl -fsSL -o ").Append(DownloadFile).Append(" \"$u\"; ");
        text.Append("echo \"$h  ").Append(DownloadFile).Append("\" | sha256sum -c -; ");
        text.Append("tar -xzf ").Append(DownloadFile).Append("; ");
        text.Append(install).Append(')');
        return text.ToString();
    }

    /// <summary>
    /// <paramref name="download"/>'s Linux entries when every one is one this builder can put in a command (a known
    /// architecture once, a hex SHA-256, an absolute http(s) URL), else null (the pre-bundle line: never a command with a
    /// guessed hash). Entries of other systems (<c>win-x64</c>, MN-218: <see cref="WindowsInstallerLine"/>) are not this
    /// line's; a Linux-only list is returned as it is, so the line is byte-identical to the one before the Windows bundle.
    /// </summary>
    private static AgentDownload? Usable(AgentDownload? download)
    {
        if (download?.Bundles is not { Count: > 0 } all)
        {
            return null;
        }

        IReadOnlyList<AgentBundleLink> bundles = all.Any(IsOtherSystem) ? [.. all.Where(b => !IsOtherSystem(b))] : all;
        if (bundles.Count == 0)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (AgentBundleLink bundle in bundles)
        {
            if (bundle is null || !Machines.ContainsKey(bundle.Rid) || !seen.Add(bundle.Rid) || !Sha256Regex().IsMatch(bundle.Sha256 ?? "")
                || !Uri.TryCreate(bundle.Url, UriKind.Absolute, out Uri? url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }
        }

        return ReferenceEquals(bundles, all) ? download : new AgentDownload { Bundles = bundles };
    }

    private static bool IsOtherSystem(AgentBundleLink? bundle) =>
        bundle?.Rid is { } rid && !rid.StartsWith("linux-", StringComparison.Ordinal);

    private static string SingleQuoted(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Regex();
}
