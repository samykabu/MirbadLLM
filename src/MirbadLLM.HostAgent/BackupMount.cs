using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MirbadLLM.HostAgent;

/// <summary>The kinds of network share the backup directory can be mounted from.</summary>
internal enum BackupShareType
{
    Smb,
    Nfs,
}

/// <summary>
/// <c>PUT /backup-mount</c> and <c>POST /backup-mount/test</c>. A class, not a record, so the password can never
/// end up in a log line through a generated <c>ToString()</c>.
/// </summary>
internal sealed class BackupMountRequest
{
    public string? Type { get; set; }

    public string? Server { get; set; }

    /// <summary>SMB: the share name. NFS: the export path.</summary>
    public string? Share { get; set; }

    public string? Subfolder { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    public string? Domain { get; set; }

    public string? SmbVersion { get; set; }

    public string? NfsVersion { get; set; }
}

/// <summary>A validated destination: every field is safe to put in a unit file, a mount option or a log line. No secret.</summary>
internal sealed record BackupMountSpec(
    BackupShareType Type,
    string Server,
    string Share,
    string? Subfolder,
    string? Username,
    string? Domain,
    string Version)
{
    public string TypeName => Type == BackupShareType.Smb ? "smb" : "nfs";
}

/// <summary>
/// The fixed locations of the backup mount. The target is hard-coded: callers choose the share, never where it is
/// mounted. The unit name is the systemd-escaped target (<c>systemd-escape -p --suffix=mount /srv/mirbadllm/backups</c>);
/// a mount unit with any other name is refused by systemd. A host agent installed before the rename keeps its mount
/// (<see cref="ForStateDirectory"/>).
/// </summary>
internal sealed record BackupMountPaths(
    string StateFile,
    string Target = BackupMountPaths.DefaultTarget,
    string UnitDirectory = "/etc/systemd/system",
    string CredentialsDirectory = "/etc/mirbadllm-hostd",
    string ProbeRoot = "/tmp")
{
    public const string DefaultTarget = "/srv/mirbadllm/backups";

    public const string StateFileName = "backup-mount.json";

    // A host agent installed before the rename (systemd StateDirectory=spark-serve-hostd) keeps its pre-rename mount
    // target and credentials folder: its unit only lets it write there, and the master's /backups bind points there.
    private const string LegacyStateDirectoryName = "spark-serve-hostd";
    private const string LegacyTarget = "/srv/spark-serve/backups";
    private const string LegacyCredentialsDirectory = "/etc/spark-serve-hostd";

    /// <summary>The mount unit's name, from <see cref="Target"/> (<c>srv-mirbadllm-backups.mount</c>).</summary>
    public string UnitName => EscapePath(Target) + ".mount";

    /// <summary>The automount of the README's earlier hand-made setup; the agent never writes one, only detects it.</summary>
    public string AutomountName => EscapePath(Target) + ".automount";

    /// <summary>The paths for a host agent whose state lives in <paramref name="stateDirectory"/>.</summary>
    public static BackupMountPaths ForStateDirectory(string stateDirectory) =>
        string.Equals(Path.GetFileName(stateDirectory.TrimEnd('/')), LegacyStateDirectoryName, StringComparison.Ordinal)
            ? new BackupMountPaths(Path.Combine(stateDirectory, StateFileName), LegacyTarget, CredentialsDirectory: LegacyCredentialsDirectory)
            : new BackupMountPaths(Path.Combine(stateDirectory, StateFileName));

    /// <summary><c>systemd-escape -p</c> for the plain absolute paths used here: '/' becomes '-', '-' becomes <c>\x2d</c>.</summary>
    private static string EscapePath(string path) => path.Trim('/').Replace("-", @"\x2d", StringComparison.Ordinal).Replace('/', '-');

    public string UnitPath => UnitDirectory.TrimEnd('/') + "/" + UnitName;

    public string AutomountPath => UnitDirectory.TrimEnd('/') + "/" + AutomountName;

    public string CredentialsPath => CredentialsDirectory.TrimEnd('/') + "/backup-share.cred";
}

/// <summary>
/// Strict validation of a backup destination. Everything except the password is checked against a small allowed
/// character set, so nothing can add a mount option (commas, <c>=</c>), a unit-file line (newlines), a systemd
/// specifier (<c>%</c>) or a path escape (<c>..</c>). The password may be any printable text: it is written only
/// to the root-only credentials file, never to a command line, a unit file or a log.
/// </summary>
internal static partial class BackupMountValidator
{
    public const int MaxPasswordLength = 256;

    public static readonly IReadOnlyList<string> SmbVersions = ["3.1.1", "3.0", "2.1"];

    public static readonly IReadOnlyList<string> NfsVersions = ["4.1", "3"];

    public const string DefaultSmbVersion = "3.0";

    public const string DefaultNfsVersion = "4.1";

    /// <summary>Returns the validated spec and password, or the first problem as a sentence for the UI.</summary>
    public static bool TryValidate(BackupMountRequest? request, out BackupMountSpec? spec, out string? password, out string? error)
    {
        spec = null;
        password = null;
        error = Check(request, ref spec, ref password);
        return error is null;
    }

    private static string? Check(BackupMountRequest? request, ref BackupMountSpec? spec, ref string? password)
    {
        if (request is null)
        {
            return "The body must be {\"type\": \"smb\"|\"nfs\", \"server\": \"…\", \"share\": \"…\"}.";
        }

        BackupShareType type;
        switch (request.Type?.Trim())
        {
            case "smb":
                type = BackupShareType.Smb;
                break;
            case "nfs":
                type = BackupShareType.Nfs;
                break;
            default:
                return "The type must be \"smb\" or \"nfs\".";
        }

        string server = (request.Server ?? "").Trim();
        if (ServerProblem(server, type) is { } serverProblem)
        {
            return serverProblem;
        }

        string share = (request.Share ?? "").Trim();
        if (type == BackupShareType.Smb)
        {
            if (share.Length == 0)
            {
                return "Enter the share name, for example backups.";
            }

            if (share.Length > 80 || !SmbShare().IsMatch(share) || share is "." or "..")
            {
                return "The share name may use letters, digits, spaces and . _ - $ only (no slashes, commas, quotes or =).";
            }
        }
        else
        {
            if (share.Length > 1)
            {
                share = share.TrimEnd('/');
            }

            if (share.Length == 0 || share[0] != '/')
            {
                return "Enter the export path as an absolute path, for example /volume1/backups.";
            }

            if (share.Length > 255 || !NfsExport().IsMatch(share) || HasBadSegment(share.TrimStart('/'), allowEmpty: share == "/"))
            {
                return "The export path may use letters, digits and . _ - / only, without .. or empty parts.";
            }
        }

        string? subfolder = string.IsNullOrWhiteSpace(request.Subfolder) ? null : request.Subfolder.Trim().TrimEnd('/');
        if (subfolder is not null)
        {
            if (subfolder.StartsWith('/') || subfolder.Length > 200 || HasBadSegment(subfolder, allowEmpty: false)
                || !(type == BackupShareType.Smb ? SmbSubfolder() : NfsSubfolder()).IsMatch(subfolder))
            {
                return type == BackupShareType.Smb
                    ? "The subfolder is a relative path such as mirbadllm or dgx/mirbadllm: letters, digits, spaces and . _ - $ only, no .. ."
                    : "The subfolder is a relative path such as mirbadllm or dgx/mirbadllm: letters, digits and . _ - only, no .. .";
            }
        }

        string? username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim();
        string? domain = string.IsNullOrWhiteSpace(request.Domain) ? null : request.Domain.Trim();
        string? secret = string.IsNullOrEmpty(request.Password) ? null : request.Password;
        string version;

        if (type == BackupShareType.Smb)
        {
            if (username is null)
            {
                return "Enter the NAS user name the backups are written as.";
            }

            if (!UserName().IsMatch(username))
            {
                return "The user name may use letters, digits and . _ - @ only.";
            }

            if (domain is not null && !DomainName().IsMatch(domain))
            {
                return "The domain may use letters, digits and . - only.";
            }

            if (secret is null)
            {
                return "Enter the NAS user's password.";
            }

            if (secret.Length > MaxPasswordLength || secret.Any(char.IsControl))
            {
                return $"The password must be at most {MaxPasswordLength} printable characters (no line breaks or tabs).";
            }

            version = string.IsNullOrWhiteSpace(request.SmbVersion) ? DefaultSmbVersion : request.SmbVersion.Trim();
            if (!SmbVersions.Contains(version, StringComparer.Ordinal))
            {
                return $"The SMB version must be one of {string.Join(", ", SmbVersions)}.";
            }
        }
        else
        {
            if (username is not null || domain is not null || secret is not null)
            {
                return "NFS exports have no user name, password or domain: the NAS allows this DGX by its address.";
            }

            version = string.IsNullOrWhiteSpace(request.NfsVersion) ? DefaultNfsVersion : request.NfsVersion.Trim();
            if (!NfsVersions.Contains(version, StringComparer.Ordinal))
            {
                return $"The NFS version must be one of {string.Join(", ", NfsVersions)}.";
            }
        }

        spec = new BackupMountSpec(type, server, share, subfolder, username, domain, version);
        password = secret;
        return null;
    }

    private static string? ServerProblem(string server, BackupShareType type)
    {
        if (server.Length == 0)
        {
            return "Enter the NAS address, for example 192.0.2.200.";
        }

        if (server.Length > 253)
        {
            return "The server name is too long.";
        }

        if (IsIPv6Literal(server))
        {
            return type == BackupShareType.Smb
                ? "Use the NAS's IPv4 address or host name for SMB; an IPv6 address is only supported for NFS."
                : null;
        }

        if (Ipv4().IsMatch(server))
        {
            return IPAddress.TryParse(server, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
                ? null
                : "That is not a valid IPv4 address.";
        }

        return HostName().IsMatch(server)
            ? null
            : "The server must be an IP address or a host name (letters, digits, . and - only).";
    }

    public static bool IsIPv6Literal(string server) =>
        Ipv6Chars().IsMatch(server) && server.Contains(':', StringComparison.Ordinal)
        && IPAddress.TryParse(server, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;

    private static bool HasBadSegment(string path, bool allowEmpty)
    {
        if (path.Length == 0)
        {
            return !allowEmpty;
        }

        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.Trim().Length != segment.Length)
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"^[0-9A-Fa-f:.]+$")]
    private static partial Regex Ipv6Chars();

    [GeneratedRegex(@"^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex HostName();

    [GeneratedRegex(@"^[A-Za-z0-9._$](?:[A-Za-z0-9._ $-]*[A-Za-z0-9._$-])?$")]
    private static partial Regex SmbShare();

    [GeneratedRegex(@"^/[A-Za-z0-9._/-]*$")]
    private static partial Regex NfsExport();

    [GeneratedRegex(@"^[A-Za-z0-9._ $/-]+$")]
    private static partial Regex SmbSubfolder();

    [GeneratedRegex(@"^[A-Za-z0-9._/-]+$")]
    private static partial Regex NfsSubfolder();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9._@-]{0,63}$")]
    private static partial Regex UserName();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.-]{0,63}$")]
    private static partial Regex DomainName();
}

/// <summary>Pure rendering of the mount source, options, the systemd unit and the credentials file.</summary>
internal static class BackupMountRendering
{
    /// <summary>First line of every unit the agent writes; a unit without it was made by hand and is never touched.</summary>
    public const string ManagedMarker = "# Managed by mirbadllm-hostd (Backup & restore page). Changes made here are overwritten.";

    /// <summary>The controller's uid/gid inside its container (the "app" user); backups are written as this user.</summary>
    public const int AppUid = 1654;

    public const int AppGid = 1654;

    /// <summary><c>//server/share[/subfolder]</c> or <c>server:/export[/subfolder]</c>.</summary>
    public static string Source(BackupMountSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Type == BackupShareType.Smb)
        {
            return $"//{spec.Server}/{spec.Share}{(spec.Subfolder is null ? "" : "/" + spec.Subfolder)}";
        }

        string host = BackupMountValidator.IsIPv6Literal(spec.Server) ? $"[{spec.Server}]" : spec.Server;
        string path = spec.Share == "/" ? "" : spec.Share;
        string full = spec.Subfolder is null ? (path.Length == 0 ? "/" : path) : $"{path}/{spec.Subfolder}";
        return $"{host}:{full}";
    }

    /// <summary>
    /// The <c>-o</c> options. <paramref name="forUnit"/> adds <c>_netdev,nofail</c> (boot must not wait for or fail on
    /// the NAS); the probe instead fails fast (<c>retry=0</c> for NFS). NFS is mounted <c>soft</c> so a NAS that goes
    /// away makes a backup fail with an I/O error instead of hanging the controller.
    /// </summary>
    public static string Options(BackupMountSpec spec, string credentialsPath, bool forUnit)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var options = new List<string>();
        if (spec.Type == BackupShareType.Smb)
        {
            options.Add($"credentials={credentialsPath}");
            options.Add(string.Create(CultureInfo.InvariantCulture, $"uid={AppUid}"));
            options.Add(string.Create(CultureInfo.InvariantCulture, $"gid={AppGid}"));
            options.Add("file_mode=0640");
            options.Add("dir_mode=0750");
            options.Add($"vers={spec.Version}");
        }
        else
        {
            options.Add($"vers={spec.Version}");
            options.Add("soft");
            options.Add("timeo=100");
            options.Add("retrans=3");
            if (spec.Version == "3")
            {
                // Backups never need NFS locks, and v3 locking needs rpc.statd running on the DGX.
                options.Add("nolock");
            }

            if (!forUnit)
            {
                options.Add("retry=0");
            }
        }

        if (forUnit)
        {
            options.Add("_netdev");
            options.Add("nofail");
        }

        return string.Join(',', options);
    }

    /// <summary>A short, secret-free description for the UI, e.g. "SMB 3.0 as backup" or "NFS 4.1".</summary>
    public static string Summary(BackupMountSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.Type == BackupShareType.Smb
            ? $"SMB {spec.Version} as {(spec.Domain is null ? "" : spec.Domain + "\\")}{spec.Username}"
            : $"NFS {spec.Version}";
    }

    /// <summary>The <c>.mount</c> unit for the fixed target. Every value comes from a validated <see cref="BackupMountSpec"/>.</summary>
    public static string Unit(BackupMountSpec spec, BackupMountPaths paths)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(paths);
        var unit = new StringBuilder();
        unit.Append(ManagedMarker).Append('\n');
        unit.Append("# Undo by hand: systemctl disable --now '").Append(paths.UnitName).Append("' and delete this file")
            .Append(spec.Type == BackupShareType.Smb ? " and " + paths.CredentialsPath : "").Append('\n');
        unit.Append("[Unit]\n");
        unit.Append("Description=mirbadllm backups on ").Append(Source(spec)).Append('\n');
        unit.Append("Wants=network-online.target\n");
        unit.Append("After=network-online.target\n");
        unit.Append('\n');
        unit.Append("[Mount]\n");
        unit.Append("What=").Append(Source(spec)).Append('\n');
        unit.Append("Where=").Append(paths.Target).Append('\n');
        unit.Append("Type=").Append(spec.Type == BackupShareType.Smb ? "cifs" : "nfs").Append('\n');
        unit.Append("Options=").Append(Options(spec, paths.CredentialsPath, forUnit: true)).Append('\n');
        unit.Append("TimeoutSec=30\n");
        unit.Append('\n');
        unit.Append("[Install]\n");
        unit.Append("WantedBy=multi-user.target\n");
        return unit.ToString();
    }

    /// <summary>The <c>credentials=</c> file for mount.cifs (root:root 0600).</summary>
    public static string Credentials(string username, string password, string? domain)
    {
        var text = new StringBuilder();
        text.Append("username=").Append(username).Append('\n');
        text.Append("password=").Append(password).Append('\n');
        if (domain is not null)
        {
            text.Append("domain=").Append(domain).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Turns mount's stderr into a sentence the owner can act on. Never includes the password (it is not in the output).</summary>
    public static string ExplainMountFailure(BackupMountSpec spec, ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(result);
        string source = Source(spec);
        if (result.TimedOut)
        {
            return $"Mounting {source} timed out: the NAS did not answer. Check the address and that {(spec.Type == BackupShareType.Smb ? "SMB" : "NFS")} is enabled on it.";
        }

        string raw = (result.StdErr.Trim().Length > 0 ? result.StdErr : result.StdOut).Trim();
        string first = raw.Split('\n', 2)[0].Trim();
        string lower = raw.ToLowerInvariant();

        string? hint =
            lower.Contains("mount error(13)", StringComparison.Ordinal) || (spec.Type == BackupShareType.Smb && lower.Contains("permission denied", StringComparison.Ordinal))
                ? "check the username and password"
            : lower.Contains("access denied by server", StringComparison.Ordinal)
                ? "the NAS refused this DGX: allow its address in the export's NFS permissions"
            : lower.Contains("mount error(2)", StringComparison.Ordinal) || lower.Contains("no such file or directory", StringComparison.Ordinal)
                ? spec.Subfolder is null ? "the share or export does not exist on the NAS" : "the share, export or subfolder does not exist on the NAS (create the subfolder first)"
            : lower.Contains("mount error(95)", StringComparison.Ordinal) || lower.Contains("operation not supported", StringComparison.Ordinal)
                ? $"the NAS does not accept {(spec.Type == BackupShareType.Smb ? "SMB" : "NFS")} {spec.Version}; try another protocol version"
            : lower.Contains("mount error(112)", StringComparison.Ordinal) || lower.Contains("host is down", StringComparison.Ordinal)
                ? "the NAS did not answer; check the address and that SMB is enabled"
            : lower.Contains("connection refused", StringComparison.Ordinal)
                ? $"the NAS refused the connection; check that {(spec.Type == BackupShareType.Smb ? "SMB" : "NFS")} is enabled on it"
            : lower.Contains("timed out", StringComparison.Ordinal) || lower.Contains("mount error(110)", StringComparison.Ordinal) || lower.Contains("mount error(115)", StringComparison.Ordinal)
                ? "the NAS did not answer in time; check the address and the network"
            : lower.Contains("could not resolve", StringComparison.Ordinal) || lower.Contains("failed to resolve", StringComparison.Ordinal) || lower.Contains("name or service not known", StringComparison.Ordinal)
                ? "the server name could not be resolved; use the NAS's IP address"
            : lower.Contains("unknown filesystem type", StringComparison.Ordinal)
                ? $"the {(spec.Type == BackupShareType.Smb ? "cifs" : "nfs")} kernel module is not loaded; run sudo modprobe {(spec.Type == BackupShareType.Smb ? "cifs" : "nfs")} on the DGX once"
            : lower.Contains("helper program", StringComparison.Ordinal) || lower.Contains("wrong fs type", StringComparison.Ordinal)
                ? spec.Type == BackupShareType.Smb
                    ? "cifs-utils may be missing on the DGX (sudo apt install cifs-utils)"
                    : "nfs-common may be missing on the DGX (sudo apt install nfs-common)"
            : null;

        string detail = first.Length == 0 ? result.Describe() : first;
        return hint is null ? $"Could not mount {source}: {detail}" : $"Could not mount {source}: {detail} — {hint}.";
    }
}

/// <summary>One line of <c>/proc/self/mountinfo</c> reduced to what the status needs.</summary>
internal sealed record MountInfoEntry(string MountPoint, string FileSystemType, string Source);

/// <summary>Parses <c>/proc/&lt;pid&gt;/mountinfo</c> (see proc(5)).</summary>
internal static class MountInfoParser
{
    /// <summary>The topmost (last listed) mount whose mount point is exactly <paramref name="target"/>, or null.</summary>
    public static MountInfoEntry? FindTop(string? mountInfo, string target)
    {
        MountInfoEntry? found = null;
        foreach (var entry in Parse(mountInfo))
        {
            if (string.Equals(entry.MountPoint, target, StringComparison.Ordinal))
            {
                found = entry;
            }
        }

        return found;
    }

    public static IEnumerable<MountInfoEntry> Parse(string? mountInfo)
    {
        if (string.IsNullOrEmpty(mountInfo))
        {
            yield break;
        }

        foreach (string rawLine in mountInfo.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            string[] fields = line.Split(' ');
            int separator = Array.IndexOf(fields, "-");
            if (fields.Length < 7 || separator < 6 || separator + 2 >= fields.Length)
            {
                continue;
            }

            yield return new MountInfoEntry(Unescape(fields[4]), fields[separator + 1], Unescape(fields[separator + 2]));
        }
    }

    /// <summary>The kernel writes space, tab, newline and backslash as <c>\040</c>, <c>\011</c>, <c>\012</c>, <c>\134</c>.</summary>
    public static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var text = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && IsOctal(value, i + 1) && IsOctal(value, i + 2) && IsOctal(value, i + 3))
            {
                text.Append((char)Convert.ToInt32(value.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                text.Append(value[i]);
            }
        }

        return text.ToString();
    }

    private static bool IsOctal(string value, int index) => index < value.Length && value[index] is >= '0' and <= '7';
}
