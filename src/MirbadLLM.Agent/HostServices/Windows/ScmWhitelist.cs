using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.HostServices.Windows;

/// <summary>One whitelisted Windows service.</summary>
/// <param name="Name">Whitelist name (the route's <c>{name}</c>): lower case, <c>[a-z0-9-]</c>.</param>
/// <param name="Service">The service key name.</param>
/// <param name="DisplayName">Display name on the Services page.</param>
/// <param name="Protected">Protected: stop, restart and autostart off need the typed name.</param>
/// <param name="Risk">What goes wrong when it stops.</param>
public sealed record ScmWhitelistEntry(string Name, string Service, string DisplayName, bool Protected, string? Risk);

/// <summary>
/// The SCM host services' whitelist (design §9.1, design-p3p4 §A.4.3): <c>{state}\scm-whitelist.json</c>, the only
/// services the agent starts, stops, restarts or changes the start type of. Empty by default. The operator adds a service
/// with <c>install-agent.ps1 -AllowService &lt;name&gt;</c> (which also grants the service account the rights on that
/// service with <c>sc sdset</c>) or from the Services page (the whitelist add route); the file lives in the state
/// directory, which only SYSTEM, Administrators and the service account can read or write.
/// <para>The file is read on every call, so an entry the installer adds while the agent runs counts at once. It fails
/// closed: an unreadable or malformed file is a <see cref="HostServiceException"/> (nothing is listed or acted on), never
/// an empty list. Writes go to a temporary file that replaces the old one. Denied services are refused on add here
/// (below the client's own check); an entry for one that is already in the file is never acted on
/// (<see cref="ScmHostClient"/> checks the deny list first).</para>
/// </summary>
public sealed partial class ScmWhitelist
{
    /// <summary>The file name in the agent's state directory (<c>install-agent.ps1</c> writes the same file).</summary>
    public const string FileName = "scm-whitelist.json";

    private const int MaxNameLength = 64;

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
    private readonly ScmDenyList _deny;
    private readonly ILogger _logger;

    /// <summary>A whitelist kept at <paramref name="path"/>.</summary>
    public ScmWhitelist(string path, ScmDenyList deny, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(deny);
        ArgumentNullException.ThrowIfNull(logger);
        FilePath = path;
        _deny = deny;
        _logger = logger;
    }

    /// <summary>The whitelist file.</summary>
    public string FilePath { get; }

    /// <summary>
    /// The whitelist name for a service key: lower case, anything outside <c>[a-z0-9-]</c> turned into <c>-</c>, runs of
    /// <c>-</c> collapsed, trimmed to 64 characters (<c>ComfyUI_Server</c> → <c>comfyui-server</c>). The same rule as
    /// hostd's, without the <c>.service</c> suffix; <c>Get-ScmWhitelistName</c> in the installer is its copy.
    /// </summary>
    public static string DeriveName(string service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var chars = service.Trim().ToLowerInvariant().Select(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? c : '-').ToArray();
        string name = DashRun().Replace(new string(chars), "-").Trim('-');
        if (name.Length > MaxNameLength)
        {
            name = name[..MaxNameLength].TrimEnd('-');
        }

        return name.Length == 0 ? "service" : name;
    }

    /// <summary>True for a valid Windows service key name (1–256 characters, no slashes, no control characters).</summary>
    public static bool IsValidService(string? service) =>
        !string.IsNullOrWhiteSpace(service) && service.Length <= 256 && service == service.Trim()
        && !service.Any(c => c is '/' or '\\' || char.IsControl(c));

    /// <summary>Every entry, as the file holds them now. Throws <see cref="HostServiceException"/> when the file is unreadable.</summary>
    public IReadOnlyList<ScmWhitelistEntry> Read()
    {
        lock (_gate)
        {
            return ReadLocked();
        }
    }

    /// <summary>The entry named <paramref name="name"/> (exact, case-sensitive, as hostd); null when there is none.</summary>
    public ScmWhitelistEntry? Find(string name) => Read().FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));

    /// <summary>The entry of service <paramref name="service"/> (case-insensitive); null when it is not whitelisted.</summary>
    public ScmWhitelistEntry? FindService(string service) =>
        Read().FirstOrDefault(e => string.Equals(e.Service, service, StringComparison.OrdinalIgnoreCase));

    /// <summary>The name <paramref name="service"/> has (when whitelisted) or would get.</summary>
    public string ProposeName(string service) => FindService(service)?.Name ?? DeriveName(service);

    /// <summary>
    /// Adds <paramref name="service"/>. Throws <see cref="HostServiceDeniedException"/> for a denied service,
    /// <see cref="HostServiceConflictException"/> when it is already whitelisted or its name is taken.
    /// </summary>
    public ScmWhitelistEntry Add(string service, string? displayName, bool isProtected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        if (_deny.Match(service) is { } pattern)
        {
            throw new HostServiceDeniedException($"{service} is a system service (matches '{pattern}') and cannot be managed.");
        }

        lock (_gate)
        {
            var entries = ReadLocked();
            if (entries.Any(e => string.Equals(e.Service, service, StringComparison.OrdinalIgnoreCase)))
            {
                throw new HostServiceConflictException($"{service} is already managed.");
            }

            string name = DeriveName(service);
            if (entries.Any(e => string.Equals(e.Name, name, StringComparison.Ordinal)))
            {
                throw new HostServiceConflictException($"The name '{name}' is already used by another managed service.");
            }

            var entry = new ScmWhitelistEntry(name, service, string.IsNullOrWhiteSpace(displayName) ? service : displayName.Trim(), isProtected, null);
            WriteLocked([.. entries, entry]);
            string after = Describe(entry);
            ScmHostServicesLog.WhitelistChanged(_logger, "add", name, "null", after);
            return entry;
        }
    }

    /// <summary>
    /// Renames or (un)protects an entry. Turning protection off needs <paramref name="confirm"/> equal to the name
    /// (<see cref="HostServiceConfirmationRequiredException"/>); an unknown name is <see cref="HostServiceNotFoundException"/>.
    /// </summary>
    public ScmWhitelistEntry Update(string name, string? displayName, bool? isProtected, string? confirm)
    {
        lock (_gate)
        {
            var entries = ReadLocked();
            var entry = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal))
                ?? throw new HostServiceNotFoundException($"'{name}' is not in the whitelist.");
            if (entry.Protected && isProtected == false && !string.Equals(confirm, name, StringComparison.Ordinal))
            {
                throw new HostServiceConfirmationRequiredException($"'{name}' is protected: confirm with its name to unprotect it.");
            }

            var updated = entry with
            {
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? entry.DisplayName : displayName.Trim(),
                Protected = isProtected ?? entry.Protected,
            };
            if (updated == entry)
            {
                return entry;
            }

            WriteLocked([.. entries.Select(e => e == entry ? updated : e)]);
            string beforeText = Describe(entry);
            string afterText = Describe(updated);
            ScmHostServicesLog.WhitelistChanged(_logger, "update", name, beforeText, afterText);
            return updated;
        }
    }

    /// <summary>Removes an entry (the service keeps running, it just is not managed any more).</summary>
    public ScmWhitelistEntry Remove(string name)
    {
        lock (_gate)
        {
            var entries = ReadLocked();
            var entry = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal))
                ?? throw new HostServiceNotFoundException($"'{name}' is not in the whitelist.");
            WriteLocked([.. entries.Where(e => e != entry)]);
            string removed = Describe(entry);
            ScmHostServicesLog.WhitelistChanged(_logger, "remove", name, removed, "null");
            return entry;
        }
    }

    private List<ScmWhitelistEntry> ReadLocked()
    {
        string text;
        try
        {
            if (!File.Exists(FilePath))
            {
                return [];
            }

            text = File.ReadAllText(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HostServiceException($"The service whitelist {FilePath} cannot be read: {ex.Message}", ex);
        }

        FileDto file;
        try
        {
            file = JsonSerializer.Deserialize<FileDto>(text, JsonOptions) ?? throw new JsonException("the file is empty");
        }
        catch (JsonException ex)
        {
            throw new HostServiceException($"The service whitelist {FilePath} is malformed ({ex.Message}); fix or delete it.", ex);
        }

        var entries = new List<ScmWhitelistEntry>();
        foreach (var dto in file.Services ?? [])
        {
            string name = dto?.Name ?? "";
            if (!NamePattern().IsMatch(name))
            {
                throw new HostServiceException($"The service whitelist {FilePath} has an invalid name '{name}'.");
            }

            if (!IsValidService(dto!.Service))
            {
                throw new HostServiceException($"The service whitelist {FilePath}: '{name}' has an invalid service '{dto.Service}'.");
            }

            if (entries.Any(e => e.Name == name || string.Equals(e.Service, dto.Service, StringComparison.OrdinalIgnoreCase)))
            {
                throw new HostServiceException($"The service whitelist {FilePath} lists '{name}' ({dto.Service}) twice.");
            }

            entries.Add(new ScmWhitelistEntry(
                name,
                dto.Service!,
                string.IsNullOrWhiteSpace(dto.DisplayName) ? dto.Service! : dto.DisplayName,
                dto.Protected,
                string.IsNullOrWhiteSpace(dto.Risk) ? null : dto.Risk));
        }

        return entries;
    }

    private void WriteLocked(IReadOnlyList<ScmWhitelistEntry> entries)
    {
        var body = new FileDto
        {
            Version = 1,
            Services = [.. entries.Select(e => new EntryDto
            {
                Name = e.Name, Service = e.Service, DisplayName = e.DisplayName, Protected = e.Protected, Risk = e.Risk,
            })],
        };

        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string temp = FilePath + ".tmp";
            using (var stream = new FileStream(temp, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None }))
            {
                JsonSerializer.Serialize(stream, body, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HostServiceException($"The service whitelist {FilePath} cannot be written: {ex.Message}", ex);
        }
    }

    private static string Describe(ScmWhitelistEntry e) =>
        JsonSerializer.Serialize(new EntryDto { Name = e.Name, Service = e.Service, DisplayName = e.DisplayName, Protected = e.Protected, Risk = e.Risk },
            LogJsonOptions);

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex("-{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex DashRun();

    private sealed class FileDto
    {
        public int Version { get; set; } = 1;

        public List<EntryDto?>? Services { get; set; }
    }

    private sealed class EntryDto
    {
        public string? Name { get; set; }

        public string? Service { get; set; }

        public string? DisplayName { get; set; }

        public bool Protected { get; set; }

        public string? Risk { get; set; }
    }
}
