using System.Text.Json;
using System.Text.RegularExpressions;

namespace MirbadLLM.HostAgent;

/// <summary>
/// Which service manager this hostd drives: systemd on Linux (<see cref="UnitManager"/>) or launchd on macOS
/// (<see cref="LaunchdManager"/>, docs/macos/implementation-plan.md M10). It decides what a valid unit name is and which
/// deny list is compiled in; the whitelist, the confirmations and the API are the same.
/// </summary>
internal enum ServiceFlavor
{
    Systemd,
    Launchd,
}

/// <summary>Where a unit lives: the system manager (PID 1) or one user's manager.</summary>
internal enum UnitScope
{
    System,
    User,
}

/// <summary>
/// One whitelisted unit (seeded from <c>/etc/mirbadllm/hostd.json</c>, then managed in
/// <c>/var/lib/mirbadllm-hostd/whitelist.json</c>). Only whitelisted units are ever started, stopped,
/// restarted or enabled/disabled.
/// </summary>
internal sealed record AllowedUnit(
    string Name,
    string Unit,
    UnitScope Scope,
    string? User,
    string DisplayName,
    string Description,
    bool Protected,
    string? Risk);

/// <summary>
/// The agent's root-owned configuration. The controller cannot change it. Its <see cref="Services"/> seed the
/// admin-managed whitelist (<see cref="WhitelistStore"/>) on first run, and entries added here later are merged
/// into the whitelist once; <see cref="DiscoverUsers"/> names the users whose user units are discovered.
/// </summary>
internal sealed partial class HostdConfig
{
    public const string DefaultPath = "/etc/mirbadllm/hostd.json";
    public const string DefaultSocket = "/run/mirbadllm-hostd/hostd.sock";

    /// <summary>macOS: hostd's own root-owned directory (root:wheel 0755), apart from the agent's, which <c>_mirbadllm</c> writes.</summary>
    public const string MacDirectory = "/Library/Application Support/mirbadllm-hostd";

    public const string MacDefaultPath = MacDirectory + "/hostd.json";

    /// <summary>macOS: <c>/var/run</c> is emptied at boot, so hostd creates the directory itself (root:<see cref="SocketGroup"/> 0750).</summary>
    public const string MacDefaultSocket = "/var/run/mirbadllm-hostd/hostd.sock";

    /// <summary>The flavor of this machine: launchd on macOS, systemd everywhere else.</summary>
    public static ServiceFlavor HostFlavor => OperatingSystem.IsMacOS() ? ServiceFlavor.Launchd : ServiceFlavor.Systemd;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public HostdConfig(
        string socket,
        IReadOnlyList<AllowedUnit> services,
        IReadOnlyList<string>? discoverUsers = null,
        ServiceFlavor flavor = ServiceFlavor.Systemd,
        string? socketGroup = null)
    {
        Flavor = flavor;
        SocketGroup = socketGroup;
        Socket = socket;
        Services = services;
        DiscoverUsers = discoverUsers ?? [.. services.Where(s => s.Scope == UnitScope.User && s.User is not null).Select(s => s.User!).Distinct(StringComparer.Ordinal)];
    }

    public string Socket { get; }

    public ServiceFlavor Flavor { get; }

    /// <summary>
    /// macOS only: the group that may use the socket (the agent's <c>_mirbadllm</c>). On Linux the unit's
    /// <c>Group=docker</c> and <c>RuntimeDirectory=</c> own the socket, so this stays null there.
    /// </summary>
    public string? SocketGroup { get; }

    /// <summary>The seed entries from hostd.json (not the live whitelist; see <see cref="WhitelistStore"/>).</summary>
    public IReadOnlyList<AllowedUnit> Services { get; }

    /// <summary>Users whose <c>--user</c> units are discovered and may be whitelisted. Default: the users of the seed's user-scope entries.</summary>
    public IReadOnlyList<string> DiscoverUsers { get; }

    /// <summary>Exact, case-sensitive lookup; null for anything not on the allowlist.</summary>
    public AllowedUnit? Find(string name) =>
        Services.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Parses and validates the file. Fails closed: any malformed entry throws, so a typo can never widen
    /// what the agent will touch.
    /// </summary>
    public static HostdConfig Parse(string json, ServiceFlavor flavor = ServiceFlavor.Systemd)
    {
        var file = JsonSerializer.Deserialize<FileDto>(json, JsonOptions)
            ?? throw new FormatException("hostd.json is empty.");

        string socket = string.IsNullOrWhiteSpace(file.Socket)
            ? (flavor == ServiceFlavor.Launchd ? MacDefaultSocket : DefaultSocket)
            : file.Socket;
        if (!socket.StartsWith('/'))
        {
            throw new FormatException("hostd.json: 'socket' must be an absolute path.");
        }

        string? socketGroup = null;
        if (flavor == ServiceFlavor.Launchd)
        {
            socketGroup = string.IsNullOrWhiteSpace(file.SocketGroup) ? "_mirbadllm" : file.SocketGroup;
            if (!UserPattern().IsMatch(socketGroup))
            {
                throw new FormatException($"hostd.json: invalid 'socketGroup' '{socketGroup}'.");
            }
        }

        var services = new List<AllowedUnit>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in file.Services ?? [])
        {
            string name = entry.Name ?? "";
            if (!NamePattern().IsMatch(name))
            {
                throw new FormatException($"hostd.json: invalid service name '{name}' (lowercase letters, digits and '-').");
            }

            if (!names.Add(name))
            {
                throw new FormatException($"hostd.json: duplicate service name '{name}'.");
            }

            string unit = entry.Unit ?? "";
            if (!IsValidUnit(unit, flavor))
            {
                throw new FormatException(flavor == ServiceFlavor.Launchd
                    ? $"hostd.json: '{name}' has an invalid unit '{unit}' (must be a launchd label such as com.example.app)."
                    : $"hostd.json: '{name}' has an invalid unit '{unit}' (must be a *.service name).");
            }

            UnitScope scope = (entry.Scope ?? "system") switch
            {
                "system" => UnitScope.System,
                "user" => UnitScope.User,
                _ => throw new FormatException($"hostd.json: '{name}' scope must be 'system' or 'user'."),
            };

            string? user = entry.User;
            if (scope == UnitScope.User && (user is null || !UserPattern().IsMatch(user)))
            {
                throw new FormatException($"hostd.json: user-scope service '{name}' needs a valid 'user'.");
            }

            if (scope == UnitScope.System)
            {
                user = null;
            }

            services.Add(new AllowedUnit(
                name,
                unit,
                scope,
                user,
                string.IsNullOrWhiteSpace(entry.DisplayName) ? name : entry.DisplayName,
                entry.Description ?? "",
                entry.Protected,
                string.IsNullOrWhiteSpace(entry.Risk) ? null : entry.Risk));
        }

        List<string>? discoverUsers = null;
        if (file.DiscoverUsers is not null)
        {
            discoverUsers = [];
            foreach (string? u in file.DiscoverUsers)
            {
                if (u is null || !UserPattern().IsMatch(u))
                {
                    throw new FormatException($"hostd.json: invalid user '{u}' in 'discoverUsers'.");
                }

                discoverUsers.Add(u);
            }
        }

        return new HostdConfig(socket, services, discoverUsers, flavor, socketGroup);
    }

    /// <summary>A <c>*.service</c> name systemd would accept (no template without instance, no option-looking name).</summary>
    public static bool IsValidUnit(string? unit) =>
        unit is not null && UnitPattern().IsMatch(unit) && !unit.EndsWith("@.service", StringComparison.Ordinal);

    /// <summary>
    /// A unit name for <paramref name="flavor"/>: a <c>*.service</c> name (<see cref="IsValidUnit(string?)"/>) or a launchd
    /// label: letters, digits, '.', '_' and '-', starting with a letter or digit, so it is never read as an option and
    /// never names a path (no '/').
    /// </summary>
    public static bool IsValidUnit(string? unit, ServiceFlavor flavor) => flavor == ServiceFlavor.Launchd
        ? unit is not null && LabelPattern().IsMatch(unit)
        : IsValidUnit(unit);

    /// <summary><see cref="IsValidUnit(string?, ServiceFlavor)"/> for this configuration's flavor.</summary>
    public bool IsValidUnitName(string? unit) => IsValidUnit(unit, Flavor);

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    public static bool IsValidUser(string? user) => user is not null && UserPattern().IsMatch(user);

    /// <summary>
    /// The whitelist name for a unit: the unit without <c>.service</c>, lowercased, anything outside
    /// <c>[a-z0-9-]</c> turned into '-', trimmed to 64 characters (e.g. <c>smartmontools.service</c> → <c>smartmontools</c>).
    /// </summary>
    public static string DeriveName(string unit)
    {
        string stem = unit.EndsWith(".service", StringComparison.Ordinal) ? unit[..^".service".Length] : unit;
        var chars = stem.ToLowerInvariant().Select(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? c : '-').ToArray();
        string name = new string(chars).Trim('-');
        while (name.Contains("--", StringComparison.Ordinal))
        {
            name = name.Replace("--", "-", StringComparison.Ordinal);
        }

        if (name.Length > 64)
        {
            name = name[..64].TrimEnd('-');
        }

        return name.Length == 0 ? "unit" : name;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    // Backslash appears in escaped instance names (systemd-fsck@dev-disk-by\x2duuid-….service); arguments are
    // passed as an argv vector after "--", never through a shell.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9@._:\\-]{0,240}\.service$", RegexOptions.CultureInvariant)]
    private static partial Regex UnitPattern();

    [GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex UserPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelPattern();

    private sealed class FileDto
    {
        public string? Socket { get; set; }

        public string? SocketGroup { get; set; }

        public List<EntryDto>? Services { get; set; }

        public List<string?>? DiscoverUsers { get; set; }
    }

    private sealed class EntryDto
    {
        public string? Name { get; set; }

        public string? Unit { get; set; }

        public string? Scope { get; set; }

        public string? User { get; set; }

        public string? DisplayName { get; set; }

        public string? Description { get; set; }

        public bool Protected { get; set; }

        public string? Risk { get; set; }
    }
}
