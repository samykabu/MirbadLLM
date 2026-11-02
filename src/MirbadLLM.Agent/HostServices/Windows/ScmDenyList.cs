using System.Text.RegularExpressions;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.HostServices.Windows;

/// <summary>
/// Windows services the agent never touches, whatever the master asks or the whitelist file says (design §9.1,
/// design-p3p4 §A.4.3): compiled in, not configurable. Denied services still show in discovery (read-only, with the
/// matching pattern), but every action and every whitelist add is refused with 403 <c>denied</c>, also for a name that is
/// on the whitelist. Patterns are globs (<c>*</c> = any run of characters), matched case-insensitively against the
/// service key name, as Windows compares service names.
/// <para><c>install-agent.ps1 -AllowService</c> carries the same list (<c>Get-ScmDenyList</c>; a test keeps the two
/// equal).</para>
/// </summary>
public sealed class ScmDenyList
{
    /// <summary>
    /// The compiled patterns: the design's list (Defender, the Event Log, RPC, the DNS client, file sharing, Remote
    /// Desktop, Windows Update, the NVIDIA display driver and its container service, the agent itself), plus the services
    /// whose stop would cut the agent off or take the firewall down (RPC endpoint mapper, DCOM launcher, the SAM, the
    /// firewall and its filtering engine).
    /// </summary>
    public static IReadOnlyList<string> BuiltInPatterns { get; } =
    [
        "WinDefend",
        "WdNisSvc",
        "EventLog",
        "RpcSs",
        "RpcEptMapper",
        "DcomLaunch",
        "SamSs",
        "Dnscache",
        "LanmanServer",
        "TermService",
        "wuauserv",
        "mpssvc",
        "BFE",
        "nvlddmkm",
        "NVDisplay.ContainerLocalSystem",
        WindowsServiceHosting.CurrentServiceName,
        LegacyNames.AgentName, // an install made before the rename keeps its service name
    ];

    private readonly List<(string Pattern, Regex Regex)> _patterns;

    /// <summary>The built-in patterns plus <paramref name="extra"/> (for example a renamed agent service).</summary>
    public ScmDenyList(IEnumerable<string>? extra = null)
    {
        _patterns = [.. BuiltInPatterns.Concat(extra ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => (p, ToRegex(p)))];
    }

    /// <summary>The built-in list only.</summary>
    public static ScmDenyList BuiltIn { get; } = new();

    /// <summary>Every pattern in force.</summary>
    public IReadOnlyList<string> Patterns => [.. _patterns.Select(p => p.Pattern)];

    /// <summary>The first pattern that denies <paramref name="serviceName"/>; null when none does.</summary>
    public string? Match(string? serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return null;
        }

        string name = serviceName.Trim();
        foreach (var (pattern, regex) in _patterns)
        {
            if (regex.IsMatch(name))
            {
                return pattern;
            }
        }

        return null;
    }

    private static Regex ToRegex(string pattern) =>
        new("^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
