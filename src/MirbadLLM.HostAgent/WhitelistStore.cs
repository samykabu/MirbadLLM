using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace MirbadLLM.HostAgent;

/// <summary>Why a whitelist change was refused.</summary>
internal enum WhitelistError
{
    None,
    Invalid,
    Denied,
    Exists,
    NotFound,
    ConfirmationRequired,
}

internal sealed record WhitelistResult(WhitelistError Error, string? Message, AllowedUnit? Unit)
{
    public bool Ok => Error == WhitelistError.None;

    public static WhitelistResult Fail(WhitelistError error, string message) => new(error, message, null);
}

/// <summary>
/// The admin-managed whitelist: the only units the agent will start, stop, restart or enable/disable.
/// Persisted at <c>/var/lib/mirbadllm-hostd/whitelist.json</c> (root, 0600, in the unit's StateDirectory),
/// seeded on first run from <c>/etc/mirbadllm/hostd.json</c>. Entries added to hostd.json later are merged in
/// once (their names are remembered in <c>seeded</c>, so removing one from the UI sticks). Denied units can never
/// be in it: they are refused on add and dropped (with a warning) on load. Every change is logged with before/after.
/// </summary>
internal sealed class WhitelistStore
{
    public const string DefaultDirectory = "/var/lib/mirbadllm-hostd";

    /// <summary>macOS: the state directory inside hostd's root-owned directory (root 0700).</summary>
    public const string MacDirectory = HostdConfig.MacDirectory + "/state";
    public const string FileName = "whitelist.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions LogJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly DenyList _deny;
    private readonly ServiceFlavor _flavor;
    private readonly ILogger _logger;
    private List<AllowedUnit> _units;
    private HashSet<string> _seeded;

    private WhitelistStore(string path, DenyList deny, ServiceFlavor flavor, ILogger logger, List<AllowedUnit> units, HashSet<string> seeded)
    {
        _path = path;
        _deny = deny;
        _flavor = flavor;
        _logger = logger;
        _units = units;
        _seeded = seeded;
    }

    public string FilePath => _path;

    public IReadOnlyList<AllowedUnit> Units
    {
        get
        {
            lock (_gate)
            {
                return [.. _units];
            }
        }
    }

    /// <summary>Exact, case-sensitive lookup by whitelist name.</summary>
    public AllowedUnit? Find(string name)
    {
        lock (_gate)
        {
            return _units.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.Ordinal));
        }
    }

    public AllowedUnit? FindUnit(string unit, UnitScope scope, string? user)
    {
        lock (_gate)
        {
            return _units.FirstOrDefault(u => string.Equals(u.Unit, unit, StringComparison.Ordinal) && u.Scope == scope
                && (scope == UnitScope.System || string.Equals(u.User, user, StringComparison.Ordinal)));
        }
    }

    /// <summary>
    /// The name a unit gets when whitelisted: <see cref="HostdConfig.DeriveName"/>, with <c>-user</c> appended for a
    /// user unit whose name is already taken by a different unit.
    /// </summary>
    public string ProposeName(string unit, UnitScope scope, string? user)
    {
        lock (_gate)
        {
            return ProposeNameLocked(unit, scope, user);
        }
    }

    /// <summary>
    /// Loads (or seeds) the whitelist. Fails closed: an unreadable or malformed file throws, so the agent refuses
    /// to start instead of running with a whitelist nobody wrote.
    /// </summary>
    public static WhitelistStore Open(string path, HostdConfig seed, DenyList deny, ILogger logger)
    {
        List<AllowedUnit> units;
        HashSet<string> seeded;
        bool changed = false;
        if (File.Exists(path))
        {
            var file = JsonSerializer.Deserialize<FileDto>(File.ReadAllText(path), JsonOptions)
                ?? throw new FormatException($"{path} is empty.");
            units = [];
            foreach (var entry in file.Services ?? [])
            {
                var unit = Validate(entry, path, seed.Flavor);
                if (units.Any(u => u.Name == unit.Name))
                {
                    throw new FormatException($"{path}: duplicate name '{unit.Name}'.");
                }

                units.Add(unit);
            }

            seeded = new HashSet<string>(file.Seeded ?? [], StringComparer.Ordinal);
        }
        else
        {
            units = [];
            seeded = new HashSet<string>(StringComparer.Ordinal);
            changed = true;
        }

        // Merge seed entries not seen before (first run: all of them).
        int merged = 0;
        foreach (var entry in seed.Services)
        {
            if (!seeded.Add(entry.Name))
            {
                continue;
            }

            changed = true;
            if (units.Any(u => u.Name == entry.Name || (u.Unit == entry.Unit && u.Scope == entry.Scope && u.User == entry.User)))
            {
                continue;
            }

            units.Add(entry);
            merged++;
        }

        // Denied units can never be whitelisted, whatever the file says.
        foreach (var unit in units.ToList())
        {
            if (deny.Match(unit.Unit) is { } pattern)
            {
                units.Remove(unit);
                changed = true;
                Log.WhitelistDroppedDenied(logger, unit.Name, unit.Unit, pattern);
            }
        }

        var store = new WhitelistStore(path, deny, seed.Flavor, logger, units, seeded);
        if (changed)
        {
            store.Save();
        }

        Log.WhitelistLoaded(logger, path, units.Count, merged);
        return store;
    }

    /// <summary>Adds a unit. The caller has already checked the unit exists; the deny list is re-checked here.</summary>
    public WhitelistResult Add(string unit, UnitScope scope, string? user, string? displayName, string description, bool isProtected)
    {
        if (!HostdConfig.IsValidUnit(unit, _flavor))
        {
            return WhitelistResult.Fail(WhitelistError.Invalid, $"'{unit}' is not a valid service unit name.");
        }

        if (_deny.Match(unit) is { } pattern)
        {
            Log.WhitelistRefusedDenied(_logger, unit, pattern);
            return WhitelistResult.Fail(WhitelistError.Denied, $"{unit} is a system service (matches '{pattern}') and cannot be managed.");
        }

        lock (_gate)
        {
            if (_units.Any(u => u.Unit == unit && u.Scope == scope && (scope == UnitScope.System || u.User == user)))
            {
                return WhitelistResult.Fail(WhitelistError.Exists, $"{unit} is already managed.");
            }

            string name = ProposeNameLocked(unit, scope, user);
            if (_units.Any(u => u.Name == name))
            {
                return WhitelistResult.Fail(WhitelistError.Exists, $"The name '{name}' is already used by another managed service.");
            }

            var entry = new AllowedUnit(
                name,
                unit,
                scope,
                scope == UnitScope.User ? user : null,
                string.IsNullOrWhiteSpace(displayName) ? name : displayName.Trim(),
                description,
                isProtected,
                null);
            var before = _units;
            _units = [.. _units, entry];
            TrySave(before);
            string after = Describe(entry);
            Log.WhitelistChanged(_logger, "add", name, "null", after);
            return new WhitelistResult(WhitelistError.None, null, entry);
        }
    }

    public WhitelistResult Remove(string name)
    {
        lock (_gate)
        {
            var entry = _units.FirstOrDefault(u => u.Name == name);
            if (entry is null)
            {
                return WhitelistResult.Fail(WhitelistError.NotFound, $"'{name}' is not in the whitelist.");
            }

            var before = _units;
            _units = [.. _units.Where(u => u.Name != name)];
            TrySave(before);
            string removed = Describe(entry);
            Log.WhitelistChanged(_logger, "remove", name, removed, "null");
            return new WhitelistResult(WhitelistError.None, null, entry);
        }
    }

    /// <summary>Renames or (un)protects an entry. Turning protection off needs <paramref name="confirm"/> equal to the name.</summary>
    public WhitelistResult Update(string name, string? displayName, bool? isProtected, string? confirm)
    {
        lock (_gate)
        {
            var entry = _units.FirstOrDefault(u => u.Name == name);
            if (entry is null)
            {
                return WhitelistResult.Fail(WhitelistError.NotFound, $"'{name}' is not in the whitelist.");
            }

            if (entry.Protected && isProtected == false && !string.Equals(confirm, name, StringComparison.Ordinal))
            {
                return WhitelistResult.Fail(WhitelistError.ConfirmationRequired, $"'{name}' is protected: confirm with its name to unprotect it.");
            }

            var updated = entry with
            {
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? entry.DisplayName : displayName.Trim(),
                Protected = isProtected ?? entry.Protected,
            };
            if (updated == entry)
            {
                return new WhitelistResult(WhitelistError.None, null, entry);
            }

            var before = _units;
            _units = [.. _units.Select(u => u.Name == name ? updated : u)];
            TrySave(before);
            string beforeText = Describe(entry);
            string afterText = Describe(updated);
            Log.WhitelistChanged(_logger, "update", name, beforeText, afterText);
            return new WhitelistResult(WhitelistError.None, null, updated);
        }
    }

    private string ProposeNameLocked(string unit, UnitScope scope, string? user)
    {
        var existing = _units.FirstOrDefault(u => u.Unit == unit && u.Scope == scope && (scope == UnitScope.System || u.User == user));
        if (existing is not null)
        {
            return existing.Name;
        }

        string name = HostdConfig.DeriveName(unit);
        if (scope == UnitScope.User && _units.Any(u => u.Name == name))
        {
            name = HostdConfig.DeriveName(name + "-user");
        }

        return name;
    }

    private static AllowedUnit Validate(EntryDto entry, string path, ServiceFlavor flavor)
    {
        string name = entry.Name ?? "";
        if (!HostdConfig.IsValidName(name))
        {
            throw new FormatException($"{path}: invalid name '{name}'.");
        }

        if (!HostdConfig.IsValidUnit(entry.Unit, flavor))
        {
            throw new FormatException($"{path}: '{name}' has an invalid unit '{entry.Unit}'.");
        }

        UnitScope scope = entry.Scope switch
        {
            "system" or null => UnitScope.System,
            "user" => UnitScope.User,
            _ => throw new FormatException($"{path}: '{name}' scope must be 'system' or 'user'."),
        };
        if (scope == UnitScope.User && !HostdConfig.IsValidUser(entry.User))
        {
            throw new FormatException($"{path}: user-scope '{name}' needs a valid 'user'.");
        }

        return new AllowedUnit(
            name,
            entry.Unit!,
            scope,
            scope == UnitScope.User ? entry.User : null,
            string.IsNullOrWhiteSpace(entry.DisplayName) ? name : entry.DisplayName,
            entry.Description ?? "",
            entry.Protected,
            string.IsNullOrWhiteSpace(entry.Risk) ? null : entry.Risk);
    }

    /// <summary>Persists, rolling the in-memory list back if the write fails (so memory never diverges from disk).</summary>
    private void TrySave(List<AllowedUnit> before)
    {
        try
        {
            Save();
        }
        catch
        {
            _units = before;
            throw;
        }
    }

    private void Save()
    {
        var body = new FileDto
        {
            Version = 1,
            Seeded = [.. _seeded.Order(StringComparer.Ordinal)],
            Services = [.. _units.Select(ToDto)],
        };
        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string temp = _path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, options))
        {
            JsonSerializer.Serialize(stream, body, JsonOptions);
            stream.Flush(flushToDisk: true);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, _path, overwrite: true);
    }

    private static EntryDto ToDto(AllowedUnit u) => new()
    {
        Name = u.Name,
        Unit = u.Unit,
        Scope = u.Scope == UnitScope.User ? "user" : "system",
        User = u.User,
        DisplayName = u.DisplayName,
        Description = u.Description,
        Protected = u.Protected,
        Risk = u.Risk,
    };

    private static string Describe(AllowedUnit u) => JsonSerializer.Serialize(ToDto(u), LogJsonOptions);

    private sealed class FileDto
    {
        public int Version { get; set; } = 1;

        public List<string>? Seeded { get; set; }

        public List<EntryDto>? Services { get; set; }
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
