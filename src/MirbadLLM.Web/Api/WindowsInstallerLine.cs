using System.Text;
using System.Text.RegularExpressions;
using MirbadLLM.Core;

namespace MirbadLLM.Web.Api;

/// <summary>
/// The Windows <c>install-agent.ps1</c> line of a join or rekey token (design-p3p4 §A.4.5, §A.4.6; MN-218), the Windows
/// counterpart of <see cref="LinuxInstallerLine"/>, built from its parts so the master's line and the page's prompt
/// variant come from one place (D98). One paste into an <b>elevated Windows PowerShell 5.1</b> window (plain 5.1 syntax,
/// also valid in PowerShell 7): in a child scope it downloads the <c>win-x64</c> bundle into a new folder under
/// <c>$env:TEMP</c>, checks its SHA-256 against the value the master put in the line (so plain HTTP is safe), unpacks it
/// and runs the bundle's <c>install-agent.ps1 install</c> (or <c>rekey</c>); the folder is removed in a <c>finally</c>.
/// <list type="bullet">
/// <item><see cref="WithToken"/> (the line in <see cref="JoinCommands.Windows"/>) sets <c>$env:MIRBADLLM_JOIN_TOKEN</c>
/// first and removes it <b>inside</b> the <c>finally</c>: a statement after a throwing script block on the same line never
/// runs, so a trailing <c>Remove-Item</c> left the token in the window's environment whenever the download, the hash check
/// or the install failed (spike S4 / F5, D122). The variable keeps the word <c>TOKEN</c>: PSReadLine's default history
/// filter keys on it, so the line is not written to the history file.</item>
/// <item><see cref="Prompt"/> is the same line without the <c>$env:</c> statement and with <c>-PromptToken</c>: the
/// installer reads the token with <c>Read-Host -AsSecureString</c>; the line carries no secret.</item>
/// </list>
/// There is never a <c>-JoinToken</c> argument: process command lines are readable by every user (<c>Win32_Process</c>).
/// The installer runs as <c>powershell.exe -NoProfile -ExecutionPolicy Bypass -File …</c>: a clean Windows 11 PC has the
/// <c>Restricted</c> execution policy, which refuses <c>&amp; install-agent.ps1</c>; the child process changes nothing in
/// the operator's window, inherits the token variable, and is always Windows PowerShell 5.1 (the installer's target, F6).
/// Its exit code is checked, so a failed install ends the line with an error. <c>$ProgressPreference</c> is silenced
/// because 5.1's progress bar slows a large download a lot. Without a usable <c>win-x64</c> bundle there is no line
/// (<see cref="JoinCommands.Windows"/> stays null, as before MN-218).
/// </summary>
public sealed partial record WindowsInstallerLine
{
    /// <summary>The variable the installer (and <c>join</c>) reads the token from; part of the contract (§A.4.5).</summary>
    public const string TokenVariable = LinuxInstallerLine.TokenVariable;

    /// <summary>The zip's name in the line's temp folder.</summary>
    public const string DownloadFile = "agent.zip";

    /// <summary>The installer switch that makes it prompt for the token (<c>Read-Host -AsSecureString</c>).</summary>
    public const string PromptTokenSwitch = "-PromptToken";

    /// <summary>The join line installs the pinned llama.cpp too (design-p3p4 §A.1.2, the lock file in the bundle).</summary>
    public const string LlamaCppSwitch = "-LlamaCpp";

    private readonly AgentBundleLink _bundle;

    private WindowsInstallerLine(string verb, string arguments, AgentBundleLink bundle)
    {
        Verb = verb;
        Arguments = arguments;
        _bundle = bundle;
    }

    /// <summary>The installer verb: <c>install</c> or <c>rekey</c>.</summary>
    public string Verb { get; }

    /// <summary>The installer arguments after the verb (no token, no <c>-PromptToken</c>).</summary>
    public string Arguments { get; }

    /// <summary>The bundle the line downloads.</summary>
    public string BundleUrl => _bundle.Url;

    /// <summary>The SHA-256 the line checks the download against.</summary>
    public string BundleSha256 => _bundle.Sha256;

    /// <summary>
    /// A join: <c>install -NodeId … -EnrollUrl … -CaHash … -LlamaCpp</c>, downloading the <c>win-x64</c> bundle of
    /// <paramref name="download"/>; null when it has no usable one.
    /// </summary>
    public static WindowsInstallerLine? Join(string nodeId, string enrollUrl, string caHash, AgentDownload? download) =>
        Usable(download) is { } bundle
            ? new("install", $"-NodeId {Quoted(nodeId)} -EnrollUrl {Quoted(enrollUrl)} -CaHash {Quoted(caHash)} {LlamaCppSwitch}", bundle)
            : null;

    /// <summary>
    /// A rekey of an installed node (§A.4.6): <c>rekey -NodeId … -NodeUid … -EnrollUrl … -CaHash …</c>. The uid is on the
    /// line, as on Linux, because an agent that lost its state directory cannot read it (MN-114). Null without a usable
    /// <c>win-x64</c> bundle.
    /// </summary>
    public static WindowsInstallerLine? Rekey(string nodeId, string nodeUid, string enrollUrl, string caHash, AgentDownload? download) =>
        Usable(download) is { } bundle
            ? new("rekey", $"-NodeId {Quoted(nodeId)} -NodeUid {Quoted(nodeUid)} -EnrollUrl {Quoted(enrollUrl)} -CaHash {Quoted(caHash)}", bundle)
            : null;

    /// <summary>
    /// The Windows line of a minted token, rebuilt from its response (and the node uid a rekey line carries), when it is
    /// exactly the line the master returned in <see cref="JoinCommands.Windows"/>; null otherwise (no Windows line, or
    /// another master version or shape: the page then offers only the master's own line instead of guessing a prompt form).
    /// </summary>
    public static WindowsInstallerLine? Of(JoinTokenCreateResponse response, string? nodeUid)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Commands?.Windows is not { } windows)
        {
            return null;
        }

        WindowsInstallerLine? line = response.Purpose == JoinTokenPurpose.Rekey
            ? nodeUid is { Length: > 0 } uid ? Rekey(response.NodeId, uid, response.EnrollUrl, response.CaHash, response.AgentDownload) : null
            : Join(response.NodeId, response.EnrollUrl, response.CaHash, response.AgentDownload);
        return line is not null && string.Equals(line.WithToken(response.Token), windows, StringComparison.Ordinal) ? line : null;
    }

    /// <summary>The line with <paramref name="token"/> in <c>$env:MIRBADLLM_JOIN_TOKEN</c>, cleared inside the <c>finally</c>.</summary>
    public string WithToken(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return $"$env:{TokenVariable}={Quoted(token)}; {Block(Arguments)}";
    }

    /// <summary>The same line without the token statement and with <c>-PromptToken</c>: no secret at all.</summary>
    public string Prompt() => Block($"{Arguments} {PromptTokenSwitch}");

    /// <summary>The download, check, unpack and install steps as one script block, with the cleanup in its <c>finally</c>.</summary>
    private string Block(string installArguments)
    {
        string zip = $"\"$d\\{DownloadFile}\"";
        var text = new StringBuilder();
        text.Append("& { $ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; ");
        text.Append("$d = Join-Path $env:TEMP ([guid]::NewGuid()); New-Item -ItemType Directory $d | Out-Null; ");
        text.Append("try { ");
        text.Append("Invoke-WebRequest -UseBasicParsing ").Append(Quoted(_bundle.Url)).Append(" -OutFile ").Append(zip).Append("; ");
        text.Append("if ((Get-FileHash ").Append(zip).Append(" -Algorithm SHA256).Hash -ne '").Append(_bundle.Sha256).Append("') { throw 'agent bundle hash mismatch' }; ");
        text.Append("Expand-Archive ").Append(zip).Append(" $d; ");
        text.Append("powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Get-ChildItem \"$d\\mirbadllm-agent-*\\install-agent.ps1\").FullName ")
            .Append(Verb).Append(' ').Append(installArguments).Append("; ");
        text.Append("if ($LASTEXITCODE -ne 0) { throw \"install-agent.ps1 ").Append(Verb).Append(" failed (exit $LASTEXITCODE)\" } ");
        text.Append("} finally { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue; ");
        text.Append("Remove-Item Env:").Append(TokenVariable).Append(" -ErrorAction SilentlyContinue } }");
        return text.ToString();
    }

    /// <summary>
    /// The one <c>win-x64</c> entry of <paramref name="download"/> when this builder can put it in a line (a hex SHA-256, an
    /// absolute http(s) URL); null when there is none, more than one, or it is malformed (never a line with a guessed hash).
    /// The Linux entries are <see cref="LinuxInstallerLine"/>'s.
    /// </summary>
    private static AgentBundleLink? Usable(AgentDownload? download)
    {
        AgentBundleLink[] windows = [.. (download?.Bundles ?? []).Where(b => b?.Rid == AgentBundleCatalog.WindowsRid)];
        if (windows.Length != 1)
        {
            return null;
        }

        AgentBundleLink bundle = windows[0];
        return Sha256Regex().IsMatch(bundle.Sha256 ?? "")
            && Uri.TryCreate(bundle.Url, UriKind.Absolute, out Uri? url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
            && !bundle.Url.Any(char.IsControl)
            ? bundle
            : null;
    }

    /// <summary>
    /// <paramref name="value"/> as a PowerShell single-quoted string: every character PowerShell takes as a single quote
    /// (', U+2018, U+2019, U+201A, U+201B) is doubled. Control characters are refused: the line is one paste.
    /// </summary>
    public static string Quoted(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("A value in the Windows installer line must not contain control characters.", nameof(value));
        }

        var text = new StringBuilder(value.Length + 2).Append('\'');
        foreach (char c in value)
        {
            text.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛')
            {
                text.Append(c);
            }
        }

        return text.Append('\'').ToString();
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Regex();
}
