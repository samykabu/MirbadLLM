using System.Text.Json;
using System.Text.RegularExpressions;

namespace MirbadLLM.HostAgent;

/// <summary>
/// Units the agent never touches, whatever the controller asks: sshd, systemd itself, D-Bus, Docker,
/// networking, the NVIDIA driver daemons, login and security plumbing, and the agent itself. Denied units
/// still show up in discovery (read-only, with the matching pattern) but every action and every
/// whitelist-add for them is refused with 403.
/// <para>
/// The built-in patterns are compiled in and cannot be removed. A root-owned
/// <c>/etc/mirbadllm/hostd-deny.json</c> (<c>{"patterns": ["foo*"]}</c>) can only add more.
/// </para>
/// </summary>
internal sealed partial class DenyList
{
    public const string DefaultExtraPath = "/etc/mirbadllm/hostd-deny.json";

    public const string MacExtraPath = HostdConfig.MacDirectory + "/hostd-deny.json";

    /// <summary>Glob patterns (<c>*</c> = any run of characters), matched case-insensitively against the unit name.</summary>
    public static IReadOnlyList<string> BuiltInPatterns { get; } =
    [
        "ssh*",
        "sshd*",
        "systemd-*",
        "dbus*",
        "docker*",
        "containerd*",
        "NetworkManager*",
        "networkd*",
        "systemd-networkd*",
        "systemd-resolved*",
        "wpa_supplicant*",
        "polkit*",
        "getty*",
        "serial-getty*",
        "nvidia-persistenced*",
        "nvidia-fabricmanager*",
        "nvidia-*driver*",
        "udev*",
        "cron*",
        "rsyslog*",
        "snapd*",
        "apparmor*",
        "ufw*",
        "chrony*",
        "auditd*",
        "user@*",
        "mirbadllm-hostd*",
        "spark-serve-hostd*", // a host agent installed before the rename keeps its unit name
        "gdm*",
        "display-manager*",
        "accounts-daemon*",
        "unattended-upgrades*",
        "multipathd*",
        "lvm2*",
    ];

    /// <summary>
    /// The launchd deny list (macOS, docs/macos/implementation-plan.md M10), matched against the label: Apple's own jobs,
    /// mirbadllm's agent and hostd, the GitHub Actions runners that share the Mac (other organisations' builds), Docker,
    /// SSH, cron and time. RustDesk is not denied: as on the DGX it is a protected entry of the seed (typed confirmation).
    /// </summary>
    public static IReadOnlyList<string> LaunchdPatterns { get; } =
    [
        "com.apple.*",
        "com.mirbadllm.*",
        "com.spark-serve.*", // an agent installed before the rename keeps its launchd labels
        "actions.runner.*",
        "com.docker.*",
        "com.openssh.*",
        "org.openssh.*",
        "com.vix.cron",
        "org.ntp.*",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly List<(string Pattern, Regex Regex)> _patterns;
    private readonly ServiceFlavor _flavor;

    public DenyList(IEnumerable<string> extraPatterns, ServiceFlavor flavor = ServiceFlavor.Systemd)
    {
        _flavor = flavor;
        _patterns = [.. (flavor == ServiceFlavor.Launchd ? LaunchdPatterns : BuiltInPatterns).Concat(extraPatterns)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => (p, ToRegex(p)))];
    }

    /// <summary>Built-in patterns only.</summary>
    public static DenyList BuiltIn { get; } = new([]);

    public IReadOnlyList<string> Patterns => [.. _patterns.Select(p => p.Pattern)];

    /// <summary>
    /// The first pattern that denies <paramref name="nameOrUnit"/>, or null. A bare name (<c>sshd</c>) is also
    /// tested as <c>sshd.service</c>, so a request can't slip past a pattern by dropping the suffix.
    /// </summary>
    public string? Match(string nameOrUnit)
    {
        if (string.IsNullOrEmpty(nameOrUnit))
        {
            return null;
        }

        // A launchd label has no suffix to drop.
        string withSuffix = _flavor == ServiceFlavor.Launchd || nameOrUnit.EndsWith(".service", StringComparison.OrdinalIgnoreCase)
            ? nameOrUnit
            : nameOrUnit + ".service";
        foreach (var (pattern, regex) in _patterns)
        {
            if (regex.IsMatch(nameOrUnit) || regex.IsMatch(withSuffix))
            {
                return pattern;
            }
        }

        return null;
    }

    public bool IsDenied(string nameOrUnit) => Match(nameOrUnit) is not null;

    /// <summary>
    /// Parses the optional extension file. Fails closed: a malformed file throws (the agent then refuses to
    /// start) rather than silently running with fewer denials than the operator wrote down.
    /// </summary>
    public static IReadOnlyList<string> ParseExtra(string json)
    {
        var file = JsonSerializer.Deserialize<ExtraDto>(json, JsonOptions)
            ?? throw new FormatException("hostd-deny.json is empty.");
        var result = new List<string>();
        foreach (string? pattern in file.Patterns ?? [])
        {
            if (pattern is null || !PatternShape().IsMatch(pattern))
            {
                throw new FormatException($"hostd-deny.json: invalid pattern '{pattern}' (letters, digits, '@._:-' and '*').");
            }

            result.Add(pattern);
        }

        return result;
    }

    private static Regex ToRegex(string pattern) =>
        new("^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    [GeneratedRegex(@"^[A-Za-z0-9@._:\-*]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex PatternShape();

    private sealed class ExtraDto
    {
        public List<string?>? Patterns { get; set; }
    }
}
