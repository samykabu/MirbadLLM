using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace MirbadLLM.HostAgent;

/// <summary>
/// Everything hostd does to launchd on macOS (docs/macos/implementation-plan.md M10): the <see cref="IUnitManager"/> of a
/// Mac. A unit is a launchd job: its label is the unit name, a system-scope job is a LaunchDaemon in the
/// <c>system</c> domain (plist in <see cref="LaunchdPaths.SystemDaemons"/>), a user-scope job a LaunchAgent in the user's
/// <c>gui/&lt;uid&gt;</c> domain (or <c>user/&lt;uid&gt;</c> with nobody logged in; plist in the user's or the
/// system-wide LaunchAgents folder).
/// <para>The mapping onto the systemd-shaped status: <c>start</c> loads the job when it is not loaded
/// (<c>launchctl bootstrap</c>) and kicks it (<c>kickstart</c>); <c>stop</c> unloads it (<c>bootout</c>) so a
/// <c>KeepAlive</c> job cannot come straight back; <c>restart</c> is <c>kickstart -k</c>; autostart is launchd's
/// enabled/disabled override (<c>launchctl enable|disable</c>), which decides whether the job loads at boot or login.
/// GPU memory per job is null: macOS has no per-process GPU accounting hostd can read.</para>
/// <para>Hardening: labels come from the whitelist (validated, after any option-looking text could matter) and every
/// argument is its own argv element. A plist is used only when it is a regular file (never a link) directly inside one
/// of the fixed folders, its <c>Label</c> equals the unit, and — for the system domain, where launchd runs it as root —
/// it is owned by root and not writable by group or others.</para>
/// </summary>
internal sealed class LaunchdManager(
    HostdConfig config,
    WhitelistStore whitelist,
    DenyList deny,
    IProcessRunner runner,
    LaunchdPaths paths,
    ILogger<UnitManager> logger) : IUnitManager
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(60);

    public HostdConfig Config => config;

    public WhitelistStore Whitelist => whitelist;

    public DenyList Deny => deny;

    public async Task<IReadOnlyList<UnitStatusDto>> ListAsync(CancellationToken ct)
    {
        var units = whitelist.Units;
        var result = new List<UnitStatusDto>(units.Count);
        foreach (var unit in units)
        {
            result.Add(await GetAsync(unit, ct).ConfigureAwait(false));
        }

        return result;
    }

    public async Task<DiscoveryDto> DiscoverAsync(CancellationToken ct)
    {
        var managed = await ListAsync(ct).ConfigureAwait(false);
        var discovered = new List<DiscoveredUnitDto>();
        var errors = new List<string>();

        var scopes = new List<(UnitScope Scope, string? User)> { (UnitScope.System, null) };
        scopes.AddRange(config.DiscoverUsers.Select(u => (UnitScope.User, (string?)u)));
        foreach (var (scope, user) in scopes)
        {
            string label = scope == UnitScope.User ? $"user {user}" : "system";
            try
            {
                discovered.AddRange(await DiscoverScopeAsync(scope, user, ct).ConfigureAwait(false));
            }
            catch (DiscoveryException ex)
            {
                Log.DiscoveryFailed(logger, label, ex.Message);
                errors.Add($"Could not list {label} launchd jobs: {ex.Message}");
            }
        }

        return new DiscoveryDto(managed, discovered, errors, deny.Patterns);
    }

    public async Task<DiscoveredUnitDto?> InspectAsync(string unit, UnitScope scope, string? user, CancellationToken ct)
    {
        string? domain;
        try
        {
            domain = await DomainAsync(scope, user, ct).ConfigureAwait(false);
        }
        catch (DiscoveryException)
        {
            return null;
        }

        var job = (await ReadPlistsAsync(scope, user, ct).ConfigureAwait(false))
            .FirstOrDefault(j => string.Equals(j.Label, unit, StringComparison.Ordinal));
        if (job is null || domain is null)
        {
            return null;
        }

        var disabled = await DisabledAsync(domain, ct).ConfigureAwait(false);
        return await DescribeAsync(job, scope, user, domain, disabled, ct).ConfigureAwait(false);
    }

    public async Task<UnitStatusDto> GetAsync(AllowedUnit unit, CancellationToken ct)
    {
        string scope = unit.Scope == UnitScope.User ? "user" : "system";
        UnitStatusDto Failed(string reason)
        {
            Log.StatusFailed(logger, unit.Name, unit.Unit, reason);
            return new UnitStatusDto(unit.Name, unit.Unit, scope, unit.User, unit.DisplayName, unit.Description, unit.Protected, unit.Risk,
                "unknown", "", "", null, null, null, null, $"Could not read status ({reason}).");
        }

        string? domain;
        try
        {
            domain = await DomainAsync(unit.Scope, unit.User, ct).ConfigureAwait(false);
        }
        catch (DiscoveryException ex)
        {
            return Failed(ex.Message);
        }

        if (domain is null)
        {
            return Failed($"user {unit.User} does not exist");
        }

        var print = await RunAsync(LaunchctlCommands.Print(domain, unit.Unit), QueryTimeout, ct).ConfigureAwait(false);
        JobState state;
        if (print.Succeeded)
        {
            state = LaunchctlParser.ParsePrint(print.StdOut);
        }
        else if (LaunchctlParser.IsNotLoaded(print))
        {
            state = JobState.NotLoaded;
        }
        else
        {
            return Failed(print.Describe());
        }

        var disabled = await DisabledAsync(domain, ct).ConfigureAwait(false);
        bool? plistDisabled = null;
        if (!state.Loaded)
        {
            // Not loaded: only the plist says whether it would load at boot (its Disabled key), and whether it exists at all.
            var job = await FindPlistAsync(unit, ct).ConfigureAwait(false);
            if (job is null)
            {
                return Failed("no plist with this label in the launchd folders");
            }

            plistDisabled = job.Disabled;
        }

        var usage = state.Pid is { } pid ? await UsageAsync(pid, ct).ConfigureAwait(false) : null;
        var (active, sub) = LaunchctlParser.States(state);
        return new UnitStatusDto(
            unit.Name,
            unit.Unit,
            scope,
            unit.User,
            unit.DisplayName,
            unit.Description,
            unit.Protected,
            unit.Risk,
            active,
            sub,
            LaunchctlParser.FileState(disabled, unit.Unit, plistDisabled),
            state.Pid,
            usage?.Started.ToUnixTimeSeconds(),
            usage?.RssBytes,
            null,
            null);
    }

    public async Task<ActionResult> RunAsync(AllowedUnit unit, UnitAction action, string? confirm, CancellationToken ct)
    {
        string verb = SystemctlCommands.Verb(action);
        if (Refuse(unit, verb, action != UnitAction.Start, confirm, $"'{unit.Name}' is protected: confirm with its name to {verb} it.") is { } refused)
        {
            return refused;
        }

        string? domain;
        try
        {
            domain = await DomainAsync(unit.Scope, unit.User, ct).ConfigureAwait(false);
        }
        catch (DiscoveryException ex)
        {
            return Fail(unit, verb, ex.Message);
        }

        if (domain is null)
        {
            return Fail(unit, verb, $"user {unit.User} does not exist");
        }

        var print = await RunAsync(LaunchctlCommands.Print(domain, unit.Unit), QueryTimeout, ct).ConfigureAwait(false);
        if (!print.Succeeded && !LaunchctlParser.IsNotLoaded(print))
        {
            return Fail(unit, verb, print.Describe());
        }

        bool loaded = print.Succeeded;
        switch (action)
        {
            case UnitAction.Stop:
                if (loaded)
                {
                    var bootout = await RunAsync(LaunchctlCommands.Bootout(domain, unit.Unit), ActionTimeout, ct).ConfigureAwait(false);
                    if (!bootout.Succeeded)
                    {
                        return Fail(unit, verb, $"launchctl bootout failed ({bootout.Describe()})");
                    }
                }

                break;

            case UnitAction.Restart when loaded:
                var kick = await RunAsync(LaunchctlCommands.Kickstart(domain, unit.Unit, kill: true), ActionTimeout, ct).ConfigureAwait(false);
                if (!kick.Succeeded)
                {
                    return Fail(unit, verb, $"launchctl kickstart -k failed ({kick.Describe()})");
                }

                break;

            default:
                if (await StartAsync(unit, domain, loaded, ct).ConfigureAwait(false) is { } problem)
                {
                    return Fail(unit, verb, problem);
                }

                break;
        }

        Log.ActionDone(logger, unit.Name, unit.Unit, verb);
        return new ActionResult(ActionOutcome.Done, null, await GetAsync(unit, ct).ConfigureAwait(false));
    }

    public async Task<ActionResult> SetAutostartAsync(AllowedUnit unit, bool enabled, string? confirm, CancellationToken ct)
    {
        string verb = enabled ? "enable" : "disable";
        if (Refuse(unit, verb, !enabled, confirm, $"'{unit.Name}' is protected: confirm with its name to disable it at boot.") is { } refused)
        {
            return refused;
        }

        string? domain;
        try
        {
            domain = await DomainAsync(unit.Scope, unit.User, ct).ConfigureAwait(false);
        }
        catch (DiscoveryException ex)
        {
            return Fail(unit, verb, ex.Message);
        }

        if (domain is null)
        {
            return Fail(unit, verb, $"user {unit.User} does not exist");
        }

        var run = await RunAsync(LaunchctlCommands.Autostart(domain, unit.Unit, enabled), ActionTimeout, ct).ConfigureAwait(false);
        if (!run.Succeeded)
        {
            return Fail(unit, verb, $"launchctl {verb} failed ({run.Describe()})");
        }

        Log.ActionDone(logger, unit.Name, unit.Unit, verb);
        return new ActionResult(ActionOutcome.Done, null, await GetAsync(unit, ct).ConfigureAwait(false));
    }

    /// <summary>Loads the job when it is not loaded, then kicks it. Null when it runs; otherwise why not.</summary>
    private async Task<string?> StartAsync(AllowedUnit unit, string domain, bool loaded, CancellationToken ct)
    {
        if (!loaded)
        {
            var job = await FindPlistAsync(unit, ct).ConfigureAwait(false);
            if (job is null)
            {
                return "no plist with this label in the launchd folders";
            }

            var bootstrap = await RunAsync(LaunchctlCommands.Bootstrap(domain, job.Path), ActionTimeout, ct).ConfigureAwait(false);
            if (!bootstrap.Succeeded)
            {
                // launchd will not load a job whose override says disabled; say so instead of its "Input/output error".
                var disabled = await DisabledAsync(domain, ct).ConfigureAwait(false);
                return LaunchctlParser.FileState(disabled, unit.Unit, job.Disabled) == "disabled"
                    ? "autostart is off, and launchd does not load a disabled job: turn autostart on first"
                    : $"launchctl bootstrap failed ({bootstrap.Describe()})";
            }
        }

        var kick = await RunAsync(LaunchctlCommands.Kickstart(domain, unit.Unit, kill: false), ActionTimeout, ct).ConfigureAwait(false);
        return kick.Succeeded ? null : $"launchctl kickstart failed ({kick.Describe()})";
    }

    private ActionResult? Refuse(AllowedUnit unit, string verb, bool needsConfirm, string? confirm, string confirmMessage)
    {
        if ((deny.Match(unit.Unit) ?? deny.Match(unit.Name)) is { } pattern)
        {
            Log.DeniedRequest(logger, unit.Name, verb, pattern);
            return new ActionResult(ActionOutcome.Denied, $"{unit.Unit} is a system service (matches '{pattern}') and cannot be managed.", null);
        }

        if (unit.Protected && needsConfirm && !string.Equals(confirm, unit.Name, StringComparison.Ordinal))
        {
            Log.ActionRefused(logger, unit.Name, unit.Unit, verb);
            return new ActionResult(ActionOutcome.ConfirmationRequired, confirmMessage, null);
        }

        return null;
    }

    private ActionResult Fail(AllowedUnit unit, string verb, string reason)
    {
        Log.ActionFailed(logger, unit.Name, unit.Unit, verb, reason);
        return new ActionResult(ActionOutcome.Failed, $"{verb} {unit.Unit} failed: {reason}.", null);
    }

    private async Task<List<DiscoveredUnitDto>> DiscoverScopeAsync(UnitScope scope, string? user, CancellationToken ct)
    {
        string domain = await DomainAsync(scope, user, ct).ConfigureAwait(false)
            ?? throw new DiscoveryException($"user {user} does not exist");
        var disabled = await DisabledAsync(domain, ct).ConfigureAwait(false);
        var result = new List<DiscoveredUnitDto>();
        foreach (var job in await ReadPlistsAsync(scope, user, ct).ConfigureAwait(false))
        {
            result.Add(await DescribeAsync(job, scope, user, domain, disabled, ct).ConfigureAwait(false));
        }

        return result;
    }

    private async Task<DiscoveredUnitDto> DescribeAsync(
        PlistJob job, UnitScope scope, string? user, string domain, IReadOnlyDictionary<string, bool> disabled, CancellationToken ct)
    {
        var print = await RunAsync(LaunchctlCommands.Print(domain, job.Label), QueryTimeout, ct).ConfigureAwait(false);
        var state = print.Succeeded ? LaunchctlParser.ParsePrint(print.StdOut) : JobState.NotLoaded;
        var usage = state.Pid is { } pid ? await UsageAsync(pid, ct).ConfigureAwait(false) : null;
        var (active, sub) = LaunchctlParser.States(state);
        var managed = whitelist.FindUnit(job.Label, scope, user);
        string? deniedBy = deny.Match(job.Label);
        return new DiscoveredUnitDto(
            managed?.Name ?? whitelist.ProposeName(job.Label, scope, user),
            job.Label,
            scope == UnitScope.User ? "user" : "system",
            scope == UnitScope.User ? user : null,
            job.Program is { Length: > 0 } program ? program : job.Path,
            active,
            sub,
            LaunchctlParser.FileState(disabled, job.Label, job.Disabled),
            usage?.Started.ToUnixTimeSeconds(),
            usage?.RssBytes,
            null,
            managed is not null,
            deniedBy is not null,
            deniedBy);
    }

    /// <summary>
    /// The launchd domain of a scope: <c>system</c>, or the user's <c>gui/&lt;uid&gt;</c> when they are logged in at the
    /// console, else <c>user/&lt;uid&gt;</c>. Null when the user does not exist (or is not one discovery may touch).
    /// </summary>
    private async Task<string?> DomainAsync(UnitScope scope, string? user, CancellationToken ct)
    {
        if (scope == UnitScope.System)
        {
            return LaunchctlCommands.SystemDomain;
        }

        if (!HostdConfig.IsValidUser(user) || !config.DiscoverUsers.Contains(user!, StringComparer.Ordinal))
        {
            return null;
        }

        var id = await RunAsync(LaunchctlCommands.IdPath, ["-u", user!], QueryTimeout, ct).ConfigureAwait(false);
        if (!id.Succeeded || !int.TryParse(id.StdOut.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int uid))
        {
            return null;
        }

        var gui = await RunAsync(LaunchctlCommands.PrintDomain(LaunchctlCommands.GuiDomain(uid)), QueryTimeout, ct).ConfigureAwait(false);
        return gui.Succeeded ? LaunchctlCommands.GuiDomain(uid) : LaunchctlCommands.UserDomain(uid);
    }

    /// <summary>The domain's enabled/disabled overrides; empty when they cannot be read (each job's plist then decides).</summary>
    private async Task<IReadOnlyDictionary<string, bool>> DisabledAsync(string domain, CancellationToken ct)
    {
        var run = await RunAsync(LaunchctlCommands.PrintDisabled(domain), QueryTimeout, ct).ConfigureAwait(false);
        return run.Succeeded ? LaunchctlParser.ParseDisabled(run.StdOut) : new Dictionary<string, bool>(StringComparer.Ordinal);
    }

    /// <summary>
    /// The plist of a whitelisted job: <c>&lt;label&gt;.plist</c> in the scope's folders first (the convention almost
    /// every job follows), else every plist of the scope. The same checks apply either way; null when none matches.
    /// </summary>
    private async Task<PlistJob?> FindPlistAsync(AllowedUnit unit, CancellationToken ct)
    {
        foreach (var (folder, owner) in paths.Folders(unit.Scope, unit.User))
        {
            string file = folder + "/" + unit.Unit + ".plist";
            if (!paths.ListPlists(folder).Contains(file, StringComparer.Ordinal) || await paths.RefuseAsync(file, owner, ct).ConfigureAwait(false) is not null)
            {
                continue;
            }

            var xml = await RunAsync(LaunchctlCommands.PlutilPath, LaunchctlCommands.PlistToXml(file), QueryTimeout, ct).ConfigureAwait(false);
            if (xml.Succeeded && LaunchctlParser.ParsePlist(xml.StdOut, file) is { } job && string.Equals(job.Label, unit.Unit, StringComparison.Ordinal))
            {
                return job;
            }
        }

        return (await ReadPlistsAsync(unit.Scope, unit.User, ct).ConfigureAwait(false))
            .FirstOrDefault(j => string.Equals(j.Label, unit.Unit, StringComparison.Ordinal));
    }

    /// <summary>Every acceptable plist of a scope, first folder first; a label seen twice keeps its first plist.</summary>
    private async Task<List<PlistJob>> ReadPlistsAsync(UnitScope scope, string? user, CancellationToken ct)
    {
        var jobs = new List<PlistJob>();
        foreach (var (folder, owner) in paths.Folders(scope, user))
        {
            foreach (string file in paths.ListPlists(folder))
            {
                if (await paths.RefuseAsync(file, owner, ct).ConfigureAwait(false) is not null)
                {
                    continue;
                }

                var xml = await RunAsync(LaunchctlCommands.PlutilPath, LaunchctlCommands.PlistToXml(file), QueryTimeout, ct).ConfigureAwait(false);
                if (!xml.Succeeded || LaunchctlParser.ParsePlist(xml.StdOut, file) is not { } job
                    || !HostdConfig.IsValidUnit(job.Label, ServiceFlavor.Launchd)
                    || jobs.Any(j => string.Equals(j.Label, job.Label, StringComparison.Ordinal)))
                {
                    continue;
                }

                jobs.Add(job);
            }
        }

        return jobs;
    }

    private async Task<ProcessUsage?> UsageAsync(int pid, CancellationToken ct)
    {
        var run = await RunAsync(LaunchctlCommands.PsPath, LaunchctlCommands.Ps(pid), QueryTimeout, ct).ConfigureAwait(false);
        return run.Succeeded ? LaunchctlParser.ParsePs(run.StdOut) : null;
    }

    private Task<ProcessResult> RunAsync(IReadOnlyList<string> launchctlArgs, TimeSpan timeout, CancellationToken ct) =>
        RunAsync(LaunchctlCommands.LaunchctlPath, launchctlArgs, timeout, ct);

    private async Task<ProcessResult> RunAsync(string program, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            return await runner.RunAsync(program, args, timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new ProcessResult(-1, "", ex.Message);
        }
    }
}

/// <summary>One job's state from <c>launchctl print</c>.</summary>
internal sealed record JobState(bool Loaded, string State, int? Pid)
{
    public static JobState NotLoaded { get; } = new(false, "not loaded", null);
}

/// <summary>A launchd plist: its label, what it runs and its <c>Disabled</c> key.</summary>
internal sealed record PlistJob(string Label, string Path, string? Program, bool Disabled);

/// <summary>A running job's resident memory and start time (<c>ps -o rss=,lstart=</c>).</summary>
internal sealed record ProcessUsage(long RssBytes, DateTimeOffset Started);

/// <summary>
/// launchctl, plutil, id and ps argument vectors. The label always comes from the whitelist or from a validated plist,
/// and every argument is a separate element (no shell).
/// </summary>
internal static class LaunchctlCommands
{
    public const string LaunchctlPath = "/bin/launchctl";
    public const string PlutilPath = "/usr/bin/plutil";
    public const string IdPath = "/usr/bin/id";
    public const string PsPath = "/bin/ps";
    public const string SystemDomain = "system";

    public static string GuiDomain(int uid) => string.Create(CultureInfo.InvariantCulture, $"gui/{uid}");

    public static string UserDomain(int uid) => string.Create(CultureInfo.InvariantCulture, $"user/{uid}");

    public static IReadOnlyList<string> Print(string domain, string label) => ["print", $"{domain}/{label}"];

    public static IReadOnlyList<string> PrintDomain(string domain) => ["print", domain];

    public static IReadOnlyList<string> PrintDisabled(string domain) => ["print-disabled", domain];

    public static IReadOnlyList<string> Bootstrap(string domain, string plist) => ["bootstrap", domain, plist];

    public static IReadOnlyList<string> Bootout(string domain, string label) => ["bootout", $"{domain}/{label}"];

    public static IReadOnlyList<string> Kickstart(string domain, string label, bool kill) =>
        kill ? ["kickstart", "-k", $"{domain}/{label}"] : ["kickstart", $"{domain}/{label}"];

    public static IReadOnlyList<string> Autostart(string domain, string label, bool enabled) =>
        [enabled ? "enable" : "disable", $"{domain}/{label}"];

    /// <summary>XML whatever the file's format (launchd also reads binary plists).</summary>
    public static IReadOnlyList<string> PlistToXml(string plist) => ["-convert", "xml1", "-o", "-", plist];

    public static IReadOnlyList<string> Ps(int pid) =>
        ["-o", "rss=,lstart=", "-p", pid.ToString(CultureInfo.InvariantCulture)];
}

/// <summary>Parsers for launchctl, plutil and ps output, free of I/O so they are tested against output from the Mac.</summary>
internal static class LaunchctlParser
{
    private const int NotFoundExitCode = 113;

    /// <summary>
    /// The top-level <c>state</c> and <c>pid</c> of a <c>launchctl print domain/label</c> dump. Only lines indented by
    /// exactly one tab are the job's own; nested blocks (arguments, environment, endpoints) are deeper.
    /// </summary>
    public static JobState ParsePrint(string output)
    {
        string state = "";
        int? pid = null;
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length < 2 || line[0] != '\t' || line[1] == '\t')
            {
                continue;
            }

            int eq = line.IndexOf(" = ", StringComparison.Ordinal);
            if (eq < 0)
            {
                continue;
            }

            string key = line[1..eq].Trim();
            string value = line[(eq + 3)..].Trim();
            if (key == "state" && state.Length == 0)
            {
                state = value;
            }
            else if (key == "pid" && pid is null && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p > 0)
            {
                pid = p;
            }
        }

        return new JobState(true, state.Length == 0 ? "unknown" : state, pid);
    }

    /// <summary>launchctl's "Could not find service" (exit 113): the job is not loaded in that domain.</summary>
    public static bool IsNotLoaded(ProcessResult result) =>
        !result.TimedOut && (result.ExitCode == NotFoundExitCode
            || result.StdErr.Contains("Could not find service", StringComparison.Ordinal)
            || result.StdOut.Contains("Could not find service", StringComparison.Ordinal));

    /// <summary>The systemd-shaped active and sub state of a job.</summary>
    public static (string Active, string Sub) States(JobState state) => state switch
    {
        { Loaded: false } => ("inactive", "not loaded"),
        { State: "running" } => ("active", "running"),
        { State: "not running" } => ("inactive", "not running"),
        _ => ("activating", state.State),
    };

    /// <summary>
    /// <c>launchctl print-disabled</c> lines <c>"label" => enabled|disabled</c> (older macOS: <c>=> false|true</c>, true
    /// meaning disabled) into label → disabled.
    /// </summary>
    public static Dictionary<string, bool> ParseDisabled(string output)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            int arrow = line.IndexOf("=>", StringComparison.Ordinal);
            if (!line.StartsWith('"') || arrow < 0)
            {
                continue;
            }

            int close = line.IndexOf('"', 1);
            if (close <= 1 || close > arrow)
            {
                continue;
            }

            string value = line[(arrow + 2)..].Trim();
            bool? disabled = value switch
            {
                "disabled" or "true" => true,
                "enabled" or "false" => false,
                _ => null,
            };
            if (disabled is { } d)
            {
                result.TryAdd(line[1..close], d);
            }
        }

        return result;
    }

    /// <summary>
    /// Whether the job loads at boot or login, as <c>enabled</c>/<c>disabled</c>: launchd's override when there is one,
    /// else the plist's <c>Disabled</c> key, else enabled.
    /// </summary>
    public static string FileState(IReadOnlyDictionary<string, bool> overrides, string label, bool? plistDisabled) =>
        (overrides.TryGetValue(label, out bool disabled) ? disabled : plistDisabled ?? false) ? "disabled" : "enabled";

    /// <summary>
    /// The <c>Label</c>, <c>Program</c> (or the first <c>ProgramArguments</c> element) and <c>Disabled</c> of a plist in
    /// XML form. Null when it is not a plist with a top-level dictionary and a label. DTDs are ignored, never fetched.
    /// </summary>
    public static PlistJob? ParsePlist(string xml, string path)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
            });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        if (document.Root?.Name.LocalName != "plist" || document.Root.Elements("dict").FirstOrDefault() is not { } dict)
        {
            return null;
        }

        string? label = null;
        string? program = null;
        string? firstArgument = null;
        bool disabled = false;
        var children = dict.Elements().ToList();
        for (int i = 0; i + 1 < children.Count; i++)
        {
            if (children[i].Name.LocalName != "key")
            {
                continue;
            }

            var value = children[i + 1];
            switch (children[i].Value)
            {
                case "Label" when value.Name.LocalName == "string":
                    label = value.Value;
                    break;
                case "Program" when value.Name.LocalName == "string":
                    program = value.Value;
                    break;
                case "ProgramArguments" when value.Name.LocalName == "array":
                    firstArgument = value.Elements("string").FirstOrDefault()?.Value;
                    break;
                case "Disabled":
                    disabled = value.Name.LocalName == "true";
                    break;
            }
        }

        return string.IsNullOrEmpty(label) ? null : new PlistJob(label, path, program ?? firstArgument, disabled);
    }

    /// <summary><c>ps -o rss=,lstart=</c>: resident KiB and the local start time (<c>Sat Oct 17 07:04:11 2026</c>).</summary>
    public static ProcessUsage? ParsePs(string output, TimeZoneInfo? zone = null)
    {
        string[] parts = output.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long kib))
        {
            return null;
        }

        string started = string.Join(' ', parts[1..6]);
        if (!DateTime.TryParseExact(started, "ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        var offset = (zone ?? TimeZoneInfo.Local).GetUtcOffset(local);
        return new ProcessUsage(kib * 1024, new DateTimeOffset(local, offset));
    }
}

/// <summary>
/// Where launchd plists may come from, and the checks a plist must pass before hostd reads or loads it. Overridable
/// for tests; the defaults are macOS's fixed folders.
/// </summary>
internal class LaunchdPaths
{
    public const string SystemDaemons = "/Library/LaunchDaemons";
    public const string SystemAgents = "/Library/LaunchAgents";

    /// <summary>The folders of a scope with the uid that must own their plists (null: the user or root).</summary>
    public virtual IEnumerable<(string Folder, string? Owner)> Folders(UnitScope scope, string? user)
    {
        if (scope == UnitScope.System)
        {
            yield return (SystemDaemons, "root");
            yield break;
        }

        if (HostdConfig.IsValidUser(user))
        {
            // macOS paths, joined with '/' whatever the platform the tests run on.
            yield return (UserHome(user!) + "/Library/LaunchAgents", user);
        }

        yield return (SystemAgents, "root");
    }

    /// <summary>A user's home folder: <c>/Users/&lt;name&gt;</c> (hostd discovers only the users hostd.json names).</summary>
    public virtual string UserHome(string user) => "/Users/" + user;

    /// <summary>The <c>*.plist</c> files directly in <paramref name="folder"/>, sorted; empty when it does not exist.</summary>
    public virtual IReadOnlyList<string> ListPlists(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? [.. Directory.EnumerateFiles(folder, "*.plist", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Why a plist may not be used, or null: it must be a regular file, not a link, and not writable by group or others;
    /// a root folder's plist must be owned by root, a user folder's by that user or root.
    /// </summary>
    public virtual async Task<string?> RefuseAsync(string file, string? owner, CancellationToken ct)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(file);
            if (!info.Exists || info.LinkTarget is not null)
            {
                return "not a regular file";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }

        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        if ((info.UnixFileMode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            return "writable by group or others";
        }

        string? fileOwner = await OwnerOfAsync(file, ct).ConfigureAwait(false);
        return fileOwner is null || (fileOwner != "root" && fileOwner != owner) ? "not owned by the folder's owner" : null;
    }

    /// <summary>The file's owner name (<c>stat -f %Su</c>), or null when it cannot be read.</summary>
    protected virtual async Task<string?> OwnerOfAsync(string file, CancellationToken ct)
    {
        try
        {
            var run = await new SystemProcessRunner()
                .RunAsync("/usr/bin/stat", ["-f", "%Su", file], TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return run.Succeeded ? run.StdOut.Trim() : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
