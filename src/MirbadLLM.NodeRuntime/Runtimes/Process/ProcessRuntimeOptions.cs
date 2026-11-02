namespace MirbadLLM.NodeRuntime.Runtimes.Process;

/// <summary>
/// Node-local settings of <see cref="ProcessRuntimeDriver"/> (design-p3p4 §A.1.3). The supervision values mirror
/// <c>Agent:Process</c> (<c>AgentProcessOptions</c>, MN-202, D116), which the agent maps onto this type when it registers
/// the driver (MN-232): NodeRuntime cannot reference the agent host. Defaults equal the agent's defaults.
/// </summary>
public sealed class ProcessRuntimeOptions
{
    /// <summary>This node's id (the <c>mirbadllm.node</c> label of every instance).</summary>
    public string NodeId { get; set; } = "";

    /// <summary>The <c>models</c> root (substituted for <c>{models}</c>; every model path is confined below it).</summary>
    public string ModelsRoot { get; set; } = "";

    /// <summary>The <c>cache</c> root (substituted for <c>{cache}</c>; holds the child's <c>tmp</c> and CUDA cache).</summary>
    public string CacheRoot { get; set; } = "";

    /// <summary>The agent's state directory: <c>processes.json</c> and <c>logs/ss-{id}.log</c> live there.</summary>
    public string StateDirectory { get; set; } = "";

    /// <summary>Host the driver probes and the proxy reaches (llama-server binds <c>--host 127.0.0.1</c>).</summary>
    public string UpstreamHost { get; set; } = "127.0.0.1";

    /// <summary>How long a process may stay Starting before it is Failed (<c>Agent:Process:StartTimeout</c>, default 10 min).</summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Restarts after Ready allowed within <see cref="RestartWindow"/> (default 2).</summary>
    public int RestartBudget { get; set; } = 2;

    /// <summary>Window of <see cref="RestartBudget"/> (default 10 min).</summary>
    public TimeSpan RestartWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Size at which a log file is rotated before the next start (default 50 MiB).</summary>
    public long LogMaxBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>Rotated log files kept (<c>.1</c>, <c>.2</c>; default 2).</summary>
    public int LogFilesKept { get; set; } = 2;

    /// <summary>How often <c>/health</c> is polled while Starting (default 2 s).</summary>
    public TimeSpan HealthPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Timeout of one probe request (default 3 s, the container driver's).</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Log lines read for a failure report (design-p3p4 §A.1.3: the last 400).</summary>
    public int FailureLogLines { get; set; } = 400;

    /// <summary>
    /// The child's <c>SystemRoot</c> (Windows needs it for the CRT and CUDA). Null = the agent's own value when it has one
    /// (no entry on Linux).
    /// </summary>
    public string? SystemRoot { get; set; }

    /// <summary><c>{state}/processes.json</c>.</summary>
    public string RecordsPath => Path.Combine(StateDirectory, Abstractions.ProcessRecordsDocument.FileName);

    /// <summary><c>{state}/logs</c>.</summary>
    public string LogsDirectory => Path.Combine(StateDirectory, "logs");

    /// <summary>Throws <see cref="InvalidOperationException"/> naming the first unusable setting.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(NodeId))
        {
            throw new InvalidOperationException("ProcessRuntimeOptions.NodeId is not configured.");
        }

        foreach (var (name, value) in new[] { ("ModelsRoot", ModelsRoot), ("CacheRoot", CacheRoot), ("StateDirectory", StateDirectory) })
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            {
                throw new InvalidOperationException($"ProcessRuntimeOptions.{name} must be an absolute path.");
            }
        }

        if (StartTimeout <= TimeSpan.Zero || HealthPollInterval <= TimeSpan.Zero || ProbeTimeout <= TimeSpan.Zero ||
            RestartWindow <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("ProcessRuntimeOptions time spans must be positive.");
        }

        if (RestartBudget < 0 || LogFilesKept < 0 || LogMaxBytes < 1 || FailureLogLines < 1)
        {
            throw new InvalidOperationException("ProcessRuntimeOptions counts must not be negative.");
        }
    }
}
