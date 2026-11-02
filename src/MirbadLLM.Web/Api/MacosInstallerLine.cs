using System.Text;
using System.Text.RegularExpressions;
using MirbadLLM.Core;

namespace MirbadLLM.Web.Api;

/// <summary>
/// The macOS <c>install-agent.sh</c> line of a join or rekey token (docs/macos/implementation-plan.md, Phase 1): one paste
/// for an Apple Silicon Mac with curl and sudo. In a subshell it refuses anything but <c>arm64</c>, downloads the
/// <c>osx-arm64</c> bundle into a temp folder, checks it against the SHA-256 the master put in the command
/// (<c>shasum -a 256 -c</c>, macOS has no <c>sha256sum</c>; plain HTTP is therefore safe), unpacks it and runs the
/// bundle's <c>install-agent.sh install</c> (or <c>rekey</c>); the folder is removed when the subshell ends. Unlike
/// <see cref="LinuxInstallerLine"/> there is no pre-bundle form: without the master's <c>osx-arm64</c> bundle there is no
/// macOS command (<see cref="JoinCommands.Macos"/> stays null). The token is never part of this record.
/// </summary>
public sealed partial record MacosInstallerLine
{
    /// <summary>The bundle's runtime identifier.</summary>
    public const string Rid = "osx-arm64";

    private const string BundleInstaller = "./mirbadllm-agent-*/install-agent.sh";

    private readonly string _verb;
    private readonly AgentBundleLink _bundle;

    private MacosInstallerLine(string verb, string arguments, AgentBundleLink bundle)
    {
        _verb = verb;
        Arguments = arguments;
        _bundle = bundle;
    }

    /// <summary>The installer arguments after the verb (no token).</summary>
    public string Arguments { get; }

    /// <summary>A join: <c>install --node-id … --enroll-url … --ca-hash …</c>; null without a usable <c>osx-arm64</c> bundle.</summary>
    public static MacosInstallerLine? Join(string nodeId, string enrollUrl, string caHash, AgentDownload? download) =>
        Usable(download) is { } bundle ? new("install", $"--node-id {nodeId} --enroll-url {enrollUrl} --ca-hash {caHash}", bundle) : null;

    /// <summary>A rekey of an installed node: <c>rekey --node-id … --node-uid … --enroll-url … --ca-hash …</c>.</summary>
    public static MacosInstallerLine? Rekey(string nodeId, string nodeUid, string enrollUrl, string caHash, AgentDownload? download) =>
        Usable(download) is { } bundle
            ? new("rekey", $"--node-id {nodeId} --node-uid {nodeUid} --enroll-url {enrollUrl} --ca-hash {caHash}", bundle)
            : null;

    /// <summary>
    /// The line of a minted token, rebuilt from its response, when it is exactly the macOS line the master returned; null
    /// otherwise (the page then shows only the master's own line).
    /// </summary>
    public static MacosInstallerLine? Of(JoinTokenCreateResponse response, string? nodeUid)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Commands.Macos is not { } macos)
        {
            return null;
        }

        MacosInstallerLine? line = response.Purpose == JoinTokenPurpose.Rekey
            ? nodeUid is { Length: > 0 } uid ? Rekey(response.NodeId, uid, response.EnrollUrl, response.CaHash, response.AgentDownload) : null
            : Join(response.NodeId, response.EnrollUrl, response.CaHash, response.AgentDownload);
        return line is not null && string.Equals(line.WithToken(response.Token), macos, StringComparison.Ordinal) ? line : null;
    }

    /// <summary>The one-line form with <paramref name="token"/> (base64url: no quoting needed) in <c>sudo</c>'s environment.</summary>
    public string WithToken(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return Downloading($"sudo {LinuxInstallerLine.TokenVariable}='{token}' {BundleInstaller} {_verb} {Arguments}");
    }

    /// <summary>The form without the token: the installer asks for it on the terminal (or uses an exported one).</summary>
    public string Prompt() => Downloading($"sudo --preserve-env={LinuxInstallerLine.TokenVariable} {BundleInstaller} {_verb} {Arguments}");

    private string Downloading(string install)
    {
        var text = new StringBuilder();
        text.Append("(set -e; d=\"$(mktemp -d)\"; trap 'rm -rf \"$d\"' EXIT; cd \"$d\"; ");
        text.Append("[ \"$(uname -m)\" = arm64 ] || { echo \"the mirbadllm agent needs an Apple Silicon Mac (arm64)\" >&2; exit 1; }; ");
        text.Append("curl -fsSL -o ").Append(LinuxInstallerLine.DownloadFile).Append(' ').Append(SingleQuoted(_bundle.Url)).Append("; ");
        text.Append("echo \"").Append(_bundle.Sha256).Append("  ").Append(LinuxInstallerLine.DownloadFile).Append("\" | shasum -a 256 -c -; ");
        text.Append("tar -xzf ").Append(LinuxInstallerLine.DownloadFile).Append("; ");
        text.Append(install).Append(')');
        return text.ToString();
    }

    /// <summary>The download's <c>osx-arm64</c> entry when it is one this builder can put in a command, else null.</summary>
    private static AgentBundleLink? Usable(AgentDownload? download)
    {
        AgentBundleLink[] mac = [.. (download?.Bundles ?? []).Where(b => b?.Rid == Rid)];
        if (mac.Length != 1)
        {
            return null;
        }

        AgentBundleLink bundle = mac[0];
        return Sha256Regex().IsMatch(bundle.Sha256 ?? "")
            && Uri.TryCreate(bundle.Url, UriKind.Absolute, out Uri? url)
            && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
                ? bundle
                : null;
    }

    private static string SingleQuoted(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Regex();
}
