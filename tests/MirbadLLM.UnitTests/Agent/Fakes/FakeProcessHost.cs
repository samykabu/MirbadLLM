using System.Text;
using MirbadLLM.FakeLlamaServer;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.UnitTests.Agent.Fakes;

/// <summary>
/// An in-memory "operating system" for <see cref="FakeProcessHost"/> (MN-211): the process table every host instance of a
/// test sees, so an agent restart (a new host and a new driver over the same table) can adopt what the old one started,
/// and a reboot (<see cref="Reboot"/>) ends everything. Nothing here touches a real process: PIDs are fake numbers and a
/// "process" is either an in-process <see cref="FakeLlamaServer.FakeLlamaServer"/> or an inert entry (a foreign program,
/// an orphan).
/// </summary>
internal sealed class FakeProcessTable
{
    /// <summary>The agent's account (the owner the candidate listing filters on).</summary>
    public const string AgentAccount = @"NT SERVICE\mirbadllm-agent";

    private readonly Lock _gate = new();
    private readonly Dictionary<int, FakeOsProcess> _running = [];
    private int _nextPid = 70_000;

    /// <summary>Processes running now.</summary>
    public IReadOnlyList<FakeOsProcess> Running
    {
        get
        {
            lock (_gate)
            {
                return [.. _running.Values];
            }
        }
    }

    /// <summary>Every process ever killed (by a host's kill or a job termination), in order.</summary>
    public List<FakeOsProcess> Killed { get; } = [];

    /// <summary>Adds a running process. <paramref name="arguments"/> null = its command line cannot be read.</summary>
    public FakeOsProcess Spawn(
        string imagePath, IReadOnlyList<string>? arguments, string owner = AgentAccount, DateTimeOffset? startTime = null,
        int? pid = null, FakeLlamaServer.FakeLlamaServer? server = null)
    {
        lock (_gate)
        {
            int id = pid ?? _nextPid++;
            if (_running.ContainsKey(id))
            {
                throw new InvalidOperationException($"PID {id} is running.");
            }

            var process = new FakeOsProcess(this, id, startTime ?? DateTimeOffset.UtcNow, imagePath, owner, arguments, server);
            _running[id] = process;
            return process;
        }
    }

    /// <summary>The running process with <paramref name="pid"/>, or null.</summary>
    public FakeOsProcess? Find(int pid)
    {
        lock (_gate)
        {
            return _running.GetValueOrDefault(pid);
        }
    }

    /// <summary>Kills <paramref name="process"/> (exit code 1, as <c>TerminateJobObject(…, 1)</c>).</summary>
    public async Task KillAsync(FakeOsProcess process)
    {
        lock (_gate)
        {
            Killed.Add(process);
        }

        await process.ExitAsync(1);
    }

    /// <summary>A reboot: every process ends; PIDs may be handed out again afterwards.</summary>
    public async Task RebootAsync()
    {
        foreach (var process in Running)
        {
            await process.ExitAsync(null);
        }
    }

    internal void Remove(FakeOsProcess process)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(process.Pid, out var current) && ReferenceEquals(current, process))
            {
                _running.Remove(process.Pid);
            }
        }
    }
}

/// <summary>One process of the <see cref="FakeProcessTable"/>.</summary>
internal sealed class FakeOsProcess
{
    private readonly FakeProcessTable _table;
    private readonly TaskCompletionSource<int?> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeOsProcess(
        FakeProcessTable table, int pid, DateTimeOffset startTime, string imagePath, string owner,
        IReadOnlyList<string>? arguments, FakeLlamaServer.FakeLlamaServer? server)
    {
        _table = table;
        Pid = pid;
        StartTime = startTime;
        ImagePath = imagePath;
        Owner = owner;
        Arguments = arguments;
        Server = server;
        if (server is not null)
        {
            _ = server.Exited.ContinueWith(t => Exited(t.Result), TaskScheduler.Default);
        }
    }

    public int Pid { get; }

    public DateTimeOffset StartTime { get; }

    public string ImagePath { get; }

    public string Owner { get; }

    public IReadOnlyList<string>? Arguments { get; }

    public FakeLlamaServer.FakeLlamaServer? Server { get; }

    public ProcessIdentity Identity => new(Pid, StartTime, ImagePath);

    /// <summary>Jobs the process was assigned to, in order (the start's job, then each adoption's new job).</summary>
    public List<string> Jobs { get; } = [];

    public bool HasExited => _exit.Task.IsCompleted;

    public Task<int?> Exit => _exit.Task;

    /// <summary>Called when the file the process logs to should be closed.</summary>
    public Action? OnExit { get; set; }

    public async Task ExitAsync(int? code)
    {
        if (Server is { } server)
        {
            await server.ExitAsync(code ?? 1);
            await server.Exited;
        }

        Exited(code);
    }

    private void Exited(int? code)
    {
        if (_exit.TrySetResult(code))
        {
            _table.Remove(this);
            OnExit?.Invoke();
        }
    }
}

/// <summary>
/// In-memory <see cref="IProcessHost"/> (MN-211) over a <see cref="FakeProcessTable"/>: a start runs the fake llama-server
/// in-process on the requested port with exactly the given arguments and environment block and logs to the given file;
/// <see cref="OpenAsync"/> checks PID, start time and image path and "creates a new job"; candidates are filtered by image
/// folder and owner; <see cref="ReadArgumentsAsync"/> answers the recorded command line. <see cref="SimulateAgentDeath"/>
/// models the agent's job handles closing without killing the children (no kill-on-close, spike S4 / F2).
/// </summary>
internal sealed class FakeProcessHost(FakeProcessTable table, string account = FakeProcessTable.AgentAccount) : IProcessHost
{
    private readonly List<Handle> _handles = [];

    /// <summary>Every start request, in order.</summary>
    public List<ProcessHostStart> Starts { get; } = [];

    /// <summary>Every identity <see cref="KillAsync"/> was asked to kill.</summary>
    public List<ProcessIdentity> KillRequests { get; } = [];

    /// <summary>Thrown by the next <see cref="StartAsync"/> (a failed create), then cleared.</summary>
    public Exception? FailNextStart { get; set; }

    /// <summary>
    /// Thrown by the next <see cref="OpenAsync"/> of a live, matching process (the identity checks passed, then the new job
    /// could not be made or assigned: the Windows host throws <c>Win32Exception</c>), then cleared (MN-219).
    /// </summary>
    public Exception? FailNextOpen { get; set; }

    /// <summary>Thrown by the next <see cref="KillAsync"/> (the Windows host throws Win32Exception or TimeoutException), then cleared.</summary>
    public Exception? FailNextKill { get; set; }

    /// <summary>Thrown by the next <see cref="IHostedProcess.KillTreeAsync"/> of any handle (a failed or cancelled kill), then cleared.</summary>
    public Exception? FailNextKillTree { get; set; }

    /// <summary>Runs at the start of every <see cref="ListCandidatesAsync"/> (something happens while the sweep runs).</summary>
    public Func<Task>? BeforeListCandidates { get; set; }

    /// <summary>Thrown by every <see cref="ListCandidatesAsync"/> while set: the sweep cannot look in that folder.</summary>
    public Exception? FailListCandidates { get; set; }

    /// <summary>Changes the environment the fake server sees (e.g. another key: a foreign server on the port).</summary>
    public Func<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>>? TamperEnvironment { get; set; }

    /// <summary>Changes the arguments the fake server sees (e.g. another alias).</summary>
    public Func<IReadOnlyList<string>, IReadOnlyList<string>>? TamperArguments { get; set; }

    /// <summary>Handles this host still holds (released by dispose or <see cref="SimulateAgentDeath"/>).</summary>
    public int OpenHandles
    {
        get
        {
            lock (_handles)
            {
                return _handles.Count(h => !h.Released);
            }
        }
    }

    public async Task<IHostedProcess> StartAsync(ProcessHostStart start, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        Starts.Add(start);
        if (FailNextStart is { } failure)
        {
            FailNextStart = null;
            throw failure;
        }

        var log = new StreamWriter(
            new FileStream(start.LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
            new UTF8Encoding(false)) { AutoFlush = true };
        var arguments = TamperArguments?.Invoke(start.Arguments) ?? start.Arguments;
        var environment = TamperEnvironment?.Invoke(start.Environment) ?? start.Environment;
        var server = await FakeLlamaServer.FakeLlamaServer.StartAsync(arguments, environment, log, ct);
        var process = table.Spawn(start.ExecutablePath, start.Arguments, account, server: server);
        process.OnExit = log.Dispose;
        if (server.Exited.IsCompleted)
        {
            log.Dispose();
        }

        process.Jobs.Add(start.JobName);
        return Track(new Handle(this, table, process, start.JobName));
    }

    public Task<IHostedProcess?> OpenAsync(ProcessIdentity expected, string jobName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var process = table.Find(expected.Pid);
        if (process is null || process.StartTime != expected.StartTime ||
            !string.Equals(process.ImagePath, expected.ImagePath, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<IHostedProcess?>(null);
        }

        if (FailNextOpen is { } failure)
        {
            FailNextOpen = null;
            throw failure;
        }

        process.Jobs.Add(jobName); // a new job (the old one cannot be re-opened by name)
        return Task.FromResult<IHostedProcess?>(Track(new Handle(this, table, process, jobName)));
    }

    public async Task<IReadOnlyList<ProcessIdentity>> ListCandidatesAsync(string imageFolder, CancellationToken ct = default)
    {
        if (BeforeListCandidates is { } hook)
        {
            await hook();
        }

        if (FailListCandidates is { } failure)
        {
            throw failure;
        }

        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(imageFolder)) + Path.DirectorySeparatorChar;
        return [.. table.Running
            .Where(p => string.Equals(p.Owner, account, StringComparison.Ordinal) &&
                        Path.GetFullPath(p.ImagePath).StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Identity)];
    }

    public Task<IReadOnlyList<string>?> ReadArgumentsAsync(ProcessIdentity identity, CancellationToken ct = default) =>
        Task.FromResult(Matching(identity)?.Arguments);

    public async Task<bool> KillAsync(ProcessIdentity identity, CancellationToken ct = default)
    {
        KillRequests.Add(identity);
        if (FailNextKill is { } failure)
        {
            FailNextKill = null;
            throw failure;
        }

        if (Matching(identity) is not { } process)
        {
            return false;
        }

        await table.KillAsync(process);
        return true;
    }

    /// <summary>The agent dies: its handles (and so its jobs' only handles) close; the children keep running.</summary>
    public void SimulateAgentDeath()
    {
        lock (_handles)
        {
            foreach (var handle in _handles)
            {
                handle.Released = true;
            }
        }
    }

    private FakeOsProcess? Matching(ProcessIdentity identity) =>
        table.Find(identity.Pid) is { } process && process.StartTime == identity.StartTime &&
        string.Equals(process.ImagePath, identity.ImagePath, StringComparison.OrdinalIgnoreCase)
            ? process
            : null;

    private Handle Track(Handle handle)
    {
        lock (_handles)
        {
            _handles.Add(handle);
        }

        return handle;
    }

    private sealed class Handle(FakeProcessHost host, FakeProcessTable table, FakeOsProcess process, string jobName) : IHostedProcess
    {
        public bool Released { get; set; }

        public ProcessIdentity Identity => process.Identity;

        public string JobName => jobName;

        public bool HasExited => process.HasExited;

        public int? ExitCode => process.HasExited ? process.Exit.Result : null;

        public Task<int?> WaitForExitAsync(CancellationToken ct = default) => process.Exit.WaitAsync(ct);

        public Task KillTreeAsync(CancellationToken ct = default)
        {
            if (host.FailNextKillTree is { } failure)
            {
                host.FailNextKillTree = null;
                return Task.FromException(failure);
            }

            return process.HasExited ? Task.CompletedTask : table.KillAsync(process);
        }

        public ValueTask DisposeAsync()
        {
            Released = true; // releases the handle only; never kills
            return ValueTask.CompletedTask;
        }
    }
}
