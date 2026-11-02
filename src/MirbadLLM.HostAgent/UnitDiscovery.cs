using System.Text.Json;
using System.Text.Json.Serialization;

namespace MirbadLLM.HostAgent;

/// <summary>One discovered unit as sent to the controller (camelCase JSON).</summary>
internal sealed record DiscoveredUnitDto(
    string Name,
    string Unit,
    string Scope,
    string? User,
    string Description,
    string ActiveState,
    string SubState,
    string UnitFileState,
    long? ActiveSinceUnix,
    long? MemoryBytes,
    long? GpuMemoryBytes,
    bool Managed,
    bool Denied,
    string? DeniedBy);

/// <summary>
/// Turns <c>systemctl list-units --all -o json</c> + <c>list-unit-files -o json</c> + a batched <c>show</c> into
/// the discovery list. Kept free of I/O so it is tested against real DGX output.
/// </summary>
internal static class UnitDiscovery
{
    /// <summary>Unit-file states that are never offered: aliases duplicate the real unit; masked/transient can't be managed.</summary>
    private static readonly HashSet<string> SkippedFileStates = new(StringComparer.Ordinal) { "alias", "masked", "transient", "bad" };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The service units worth a <c>show</c>: loaded units from <paramref name="listUnitsJson"/> plus every unit
    /// file from <paramref name="listUnitFilesJson"/>, minus templates without an instance (<c>foo@.service</c>),
    /// non-services, not-found/masked units and names that fail validation. Sorted, distinct.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string listUnitsJson, string listUnitFilesJson)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var u in Parse<ListUnitDto>(listUnitsJson))
        {
            if (u.Load == "loaded" && Acceptable(u.Unit))
            {
                names.Add(u.Unit!);
            }
        }

        foreach (var f in Parse<UnitFileDto>(listUnitFilesJson))
        {
            string? name = f.UnitFile is null ? null : Path.GetFileName(f.UnitFile);
            if (f.State is not null && !SkippedFileStates.Contains(f.State) && Acceptable(name))
            {
                names.Add(name!);
            }
        }

        return [.. names];
    }

    /// <summary>
    /// Builds the discovered entries from a batched show. Units that are not loaded or have no unit file
    /// (transient, not-found) are dropped: there is nothing to enable and nothing stable to whitelist.
    /// </summary>
    public static List<DiscoveredUnitDto> Build(
        UnitScope scope,
        string? user,
        IReadOnlyDictionary<string, Dictionary<string, string>> shown,
        IReadOnlyDictionary<int, long>? gpu,
        ICgroupReader cgroups,
        WhitelistStore whitelist,
        DenyList deny)
    {
        var result = new List<DiscoveredUnitDto>();
        string scopeName = scope == UnitScope.User ? "user" : "system";
        foreach (var (id, values) in shown)
        {
            string Get(string key) => values.TryGetValue(key, out string? v) ? v.Trim() : "";

            if (Get("LoadState") != "loaded" || Get("FragmentPath").Length == 0 || !Acceptable(id)
                || Get("UnitFileState") is "masked" or "transient")
            {
                continue;
            }

            var props = UnitStatusParser.Parse(values);
            bool active = props.ActiveState is "active" or "reloading" or "activating" or "deactivating";
            var managed = whitelist.FindUnit(id, scope, user);
            string? deniedBy = deny.Match(id);
            result.Add(new DiscoveredUnitDto(
                managed?.Name ?? whitelist.ProposeName(id, scope, user),
                id,
                scopeName,
                scope == UnitScope.User ? user : null,
                Get("Description"),
                props.ActiveState,
                props.SubState,
                props.UnitFileState,
                active ? props.ActiveSince?.ToUnixTimeSeconds() : null,
                active ? props.MemoryBytes : null,
                gpu is null || !active ? null : GpuCgroupMapper.SumForCgroup(props.ControlGroup, gpu, cgroups),
                managed is not null,
                deniedBy is not null,
                deniedBy));
        }

        return result;
    }

    private static bool Acceptable(string? unit) =>
        unit is not null && unit.EndsWith(".service", StringComparison.Ordinal) && !unit.Contains("@.", StringComparison.Ordinal)
        && HostdConfig.IsValidUnit(unit);

    private static List<T> Parse<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? [];
    }

    private sealed class ListUnitDto
    {
        public string? Unit { get; set; }

        public string? Load { get; set; }
    }

    private sealed class UnitFileDto
    {
        [JsonPropertyName("unit_file")]
        public string? UnitFile { get; set; }

        public string? State { get; set; }
    }
}
