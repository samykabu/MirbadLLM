using Microsoft.Extensions.Logging;
using MirbadLLM.HostAgent;

namespace MirbadLLM.UnitTests.HostAgent;

/// <summary>Records every program invocation and answers from a script instead of running anything.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = [];

    /// <summary>Default: systemctl show → an active unit; nvidia-smi → no compute apps; everything else succeeds.</summary>
    public Func<string, IReadOnlyList<string>, ProcessResult> Respond { get; set; } = (file, args) =>
        file == SystemctlCommands.NvidiaSmiPath
            ? new ProcessResult(0, "", "")
            : args.Contains("show")
                ? new ProcessResult(0, "ActiveState=active\nSubState=running\nUnitFileState=enabled\nMainPID=42\nActiveEnterTimestamp=@1790000000\nMemoryCurrent=1048576\nControlGroup=/system.slice/x.service\n", "")
                : new ProcessResult(0, "", "");

    public IEnumerable<IReadOnlyList<string>> SystemctlCalls =>
        Calls.Where(c => c.FileName == SystemctlCommands.SystemctlPath).Select(c => c.Arguments);

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Add((fileName, arguments.ToList()));
        return Task.FromResult(Respond(fileName, arguments));
    }
}

internal sealed class FakeCgroupReader(Dictionary<int, string> paths) : ICgroupReader
{
    public string? Read(int pid) => paths.GetValueOrDefault(pid);
}

/// <summary>Captures formatted log lines so tests can assert what reaches the journal.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}

internal static class HostAgentFixtures
{
    public const string ConfigJson = """
        {
          "socket": "/run/mirbadllm-hostd/hostd.sock",
          "services": [
            { "name": "comfyui", "unit": "comfyui.service", "scope": "system", "displayName": "ComfyUI", "description": "Images." },
            { "name": "lmstudio-server", "unit": "lmstudio-server.service", "scope": "user", "user": "dgxuser", "displayName": "LM Studio server" },
            { "name": "rustdesk", "unit": "rustdesk.service", "scope": "system", "displayName": "RustDesk", "protected": true, "risk": "Remote desktop." }
          ]
        }
        """;

    /// <summary>Real <c>systemctl show --timestamp=unix</c> output for comfyui on the DGX.</summary>
    public const string ComfyShow = """
        MainPID=276633
        ControlGroup=/system.slice/comfyui.service
        MemoryCurrent=1649680384
        Id=comfyui.service
        ActiveState=active
        SubState=running
        UnitFileState=enabled
        ActiveEnterTimestamp=@1790687285
        """;

    /// <summary>Real <c>nvidia-smi --query-compute-apps=pid,used_memory --format=csv,noheader,nounits</c> output.</summary>
    public const string ComputeApps = """
        213208, 20098
        276633, 40144
        277328, 7019
        280674, 2668
        """;

    public static HostdConfig Config() => HostdConfig.Parse(ConfigJson);

    /// <summary>A fresh whitelist file path in the temp folder (not created).</summary>
    public static string TempWhitelistPath() =>
        Path.Combine(Path.GetTempPath(), "mirbadllm-hostd-tests", Guid.NewGuid().ToString("N"), WhitelistStore.FileName);

    public static WhitelistStore Store(HostdConfig? config = null, DenyList? deny = null, string? path = null, ILogger? logger = null) =>
        WhitelistStore.Open(path ?? TempWhitelistPath(), config ?? Config(), deny ?? DenyList.BuiltIn, logger ?? new CapturingLogger<WhitelistStore>());

    /// <summary>A file captured read-only from the DGX (<c>HostAgent/Fixtures</c>).</summary>
    public static string Fixture(string name) =>
        File.ReadAllText(FindRepoFile(Path.Combine("tests", "MirbadLLM.UnitTests", "HostAgent", "Fixtures", name)));

    public static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(relative);
    }

    /// <summary>
    /// Answers systemctl like the DGX did: list-units / list-unit-files from the captured JSON, show (batched) from
    /// the captured <c>show-all-*.txt</c> blocks for exactly the units asked for; nvidia-smi from <see cref="ComputeApps"/>.
    /// </summary>
    public static Func<string, IReadOnlyList<string>, ProcessResult> DgxResponder()
    {
        var showSystem = UnitStatusParser.ParseBlocks(Fixture("show-all-system.txt"));
        var showUser = UnitStatusParser.ParseBlocks(Fixture("show-all-user.txt"));
        string unitsSystem = Fixture("list-units-system.json");
        string unitsUser = Fixture("list-units-user.json");
        string filesSystem = Fixture("list-unit-files-system.json");
        string filesUser = Fixture("list-unit-files-user.json");

        return (file, args) =>
        {
            if (file == SystemctlCommands.NvidiaSmiPath)
            {
                return new ProcessResult(0, ComputeApps, "");
            }

            bool user = args.Count > 0 && args[0] == "--user";
            if (args.Contains("list-units"))
            {
                return new ProcessResult(0, user ? unitsUser : unitsSystem, "");
            }

            if (args.Contains("list-unit-files"))
            {
                return new ProcessResult(0, user ? filesUser : filesSystem, "");
            }

            if (args.Contains("show"))
            {
                var blocks = user ? showUser : showSystem;
                int dash = args.ToList().IndexOf("--");
                var output = new System.Text.StringBuilder();
                foreach (string unit in args.Skip(dash + 1))
                {
                    if (blocks.TryGetValue(unit, out var values))
                    {
                        output.Append(string.Join('\n', values.Select(kv => $"{kv.Key}={kv.Value}"))).Append("\n\n");
                    }
                    else
                    {
                        output.Append($"Id={unit}\nLoadState=not-found\nActiveState=inactive\nSubState=dead\nFragmentPath=\n\n");
                    }
                }

                return new ProcessResult(0, output.ToString(), "");
            }

            return new ProcessResult(0, "", "");
        };
    }
}
