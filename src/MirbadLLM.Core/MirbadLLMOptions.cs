namespace MirbadLLM.Core;

/// <summary>Bound from the <c>MirbadLLM</c> configuration section.</summary>
public sealed class MirbadLLMOptions
{
    public const string SectionName = "MirbadLLM";

    /// <summary>Default vLLM image for models without their own.</summary>
    public string VllmImage { get; set; } = "local/qwen38-vllm-dflash2:66e5414c6d75";

    /// <summary>
    /// The MTP architectures the vLLM image registers (e.g. <c>Qwen3_5MoeMTP</c>), for the tuning MTP gate. Empty =
    /// unknown: MTP is then only tried when the saved configuration already runs it.
    /// </summary>
    public IList<string> MtpArchitectures { get; } = [];

    /// <summary>Docker endpoint: <c>unix:///var/run/docker.sock</c>, <c>npipe://./pipe/docker_engine</c> or <c>tcp://host:2375</c>.</summary>
    public string DockerEndpoint { get; set; } = "unix:///var/run/docker.sock";

    /// <summary>Host path of the checkpoints root, bind-mounted at <c>/models</c> in model containers.</summary>
    public string ModelsHostPath { get; set; } = "/srv/mirbadllm/models";

    /// <summary>Host path bind-mounted at <c>/root/.cache</c> (compile and autotune caches; makes restarts faster).</summary>
    public string CacheHostPath { get; set; } = "/srv/mirbadllm/cache";

    /// <summary>Directory for controller state (<c>models.json</c>).</summary>
    public string DataPath { get; set; } = "/data";

    /// <summary>Docker network that model containers join; the controller reaches them at <c>http://ss-{id}:8000</c>.</summary>
    public string DockerNetwork { get; set; } = "mirbadllm";

    /// <summary>When false (local dev), the controller uses the published loopback port instead of the container network.</summary>
    public bool UseContainerNetwork { get; set; } = true;

    /// <summary>Host address used to reach published ports when <see cref="UseContainerNetwork"/> is false.</summary>
    public string UpstreamHost { get; set; } = "127.0.0.1";

    /// <summary>First host port handed out to model containers (published on 127.0.0.1 only).</summary>
    public int PortRangeStart { get; set; } = 8101;

    /// <summary>Memory kept free for other workloads (ComfyUI, Ollama, OS) when admitting a model.</summary>
    public double MemoryReserveGiB { get; set; } = 8;

    /// <summary>Optional bearer key required on <c>/v1/*</c>. Empty = no auth.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Start a stopped model when a request names it (the request waits until ready or times out).</summary>
    public bool AutoStartOnRequest { get; set; }

    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan HealthPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Restart a running model whose container died on its own (Ready/Starting -> Failed, or exited without a
    /// Stop), through the node's one-at-a-time start lane with a short backoff. Off = leave it Failed.</summary>
    public bool CrashRestart { get; set; } = true;

    /// <summary>Consecutive crashes after which automatic restarts pause until a human starts the model again.</summary>
    public int CrashRestartLimit { get; set; } = 3;
}
