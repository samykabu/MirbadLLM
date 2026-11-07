using System.Text.RegularExpressions;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Contracts;

/// <summary>
/// Protocol constants of the master↔agent API (design §3.6, §4.1–4.8, §7.2): versions, headers, identities, ports,
/// limits and timings. Changing a value here is a protocol change.
/// </summary>
public static partial class Protocol
{
    /// <summary>Oldest protocol version this build speaks (design §3.6).</summary>
    public const int MinVersion = 1;

    /// <summary>Newest protocol version this build speaks (design §3.6).</summary>
    public const int CurrentVersion = 1;

    /// <summary>This build's range, <c>{min: 1, current: 1}</c>.</summary>
    public static ProtocolRange Range { get; } = new(MinVersion, CurrentVersion);

    /// <summary>Base path of every agent route (design §4.3).</summary>
    public const string BasePath = "/agent/v1";

    /// <summary>
    /// Selected protocol version on every request after negotiation (design §3.6). Not needed on <c>GET /info</c>.
    /// A version the agent no longer supports gets 400 <c>protocol_mismatch</c>, and the master renegotiates.
    /// </summary>
    public const string ProtocolHeader = "MirbadLLM-Protocol";

    /// <summary>The master triple on every mutating request: <c>{instanceId}/{epoch}/{bootId}</c> (design §4.2, <see cref="MasterTriple.ToHeaderValue"/>).</summary>
    public const string MasterHeader = "MirbadLLM-Master";

    /// <summary>
    /// Response header on every error the agent itself writes on the proxy route (and on its request-pipeline refusals),
    /// value = the agent error code. The proxy strips it from model responses, so on <c>/proxy/…</c> its presence alone
    /// tells an agent error (translate it, design §7.4) from the model's own 4xx/5xx (pass through unchanged). Added by the
    /// W2 review (MN-092, D40); the master's local <c>server_busy</c> answer carries it too.
    /// </summary>
    public const string AgentErrorHeader = "MirbadLLM-Agent-Error";

    /// <summary>The master's clock on every mutating request, ISO 8601 (round-trip <c>O</c> format); skew &gt; 5 min is refused (design §4.2).</summary>
    public const string RequestTimeHeader = "MirbadLLM-Request-Time";

    /// <summary>Prefix of the agent-protocol headers above.</summary>
    public const string HeaderPrefix = "MirbadLLM-";

    /// <summary>
    /// The pre-rename twin of an agent-protocol header (<c>MirbadLLM-Master</c> → <c>SparkServe-Master</c>); null for any
    /// other header. Both sides send both names and read either, so a new master and an old agent (or the reverse)
    /// understand each other during a rolling upgrade.
    /// </summary>
    public static string? LegacyHeaderOf(string name) =>
        name.StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase) ? LegacyNames.HeaderPrefix + name[HeaderPrefix.Length..] : null;

    /// <summary>The current name of a pre-rename agent-protocol header (<c>SparkServe-Master</c> → <c>MirbadLLM-Master</c>); null for any other.</summary>
    public static string? CurrentHeaderOf(string name) =>
        name.StartsWith(LegacyNames.HeaderPrefix, StringComparison.OrdinalIgnoreCase) ? HeaderPrefix + name[LegacyNames.HeaderPrefix.Length..] : null;

    /// <summary>SPIFFE id in the SAN URI of every master certificate (client and enroll listener, design §3.2, §10.2).</summary>
    public const string MasterSpiffeId = "spiffe://mirbadllm/master";

    /// <summary>Prefix of agent SPIFFE ids: <c>spiffe://mirbadllm/node/{nodeId}</c> (design §3.1).</summary>
    public const string NodeSpiffeIdPrefix = "spiffe://mirbadllm/node/";

    /// <summary>The master SPIFFE id under the pre-rename trust domain (<see cref="LegacyNames.SpiffeTrustDomain"/>).</summary>
    public const string LegacyMasterSpiffeId = "spiffe://" + LegacyNames.SpiffeTrustDomain + "/master";

    /// <summary>Prefix of agent SPIFFE ids under the pre-rename trust domain.</summary>
    public const string LegacyNodeSpiffeIdPrefix = "spiffe://" + LegacyNames.SpiffeTrustDomain + "/node/";

    /// <summary>
    /// The master SPIFFE id the CA puts in new certificates: still the pre-rename one, because every pre-rename agent
    /// requires exactly it (see <see cref="LegacyNames.SpiffeTrustDomain"/>). Validation accepts both domains.
    /// </summary>
    public const string IssuedMasterSpiffeId = LegacyMasterSpiffeId;

    /// <summary>Default agent mTLS port (design §4.1).</summary>
    public const int DefaultAgentPort = 9443;

    /// <summary>Default master enroll-listener port (design §3.2).</summary>
    public const int DefaultEnrollPort = 8443;

    /// <summary>Requests whose <c>MirbadLLM-Request-Time</c> or envelope <c>issuedAt</c> differs from the agent clock by more are refused (design §4.2, §4.3).</summary>
    public static readonly TimeSpan MaxRequestSkew = TimeSpan.FromMinutes(5);

    /// <summary>Clock skew above which a node is Degraded (design §3.5, §4.6).</summary>
    public static readonly TimeSpan DegradedClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>Agent heartbeat event interval on <c>/events</c> (design §3.4).</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    /// <summary>No contact for this long makes a node Offline (3 missed heartbeats, design §3.5).</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(15);

    /// <summary>First event-stream reconnect delay (design §3.4).</summary>
    public static readonly TimeSpan ReconnectInitialDelay = TimeSpan.FromSeconds(1);

    /// <summary>Maximum event-stream reconnect delay (design §3.4).</summary>
    public static readonly TimeSpan ReconnectMaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>Events kept in the agent's replay ring (design §4.7), together with <see cref="EventRingMaxAge"/>.</summary>
    public const int EventRingCapacity = 10_000;

    /// <summary>Oldest event kept in the replay ring (design §4.7).</summary>
    public static readonly TimeSpan EventRingMaxAge = TimeSpan.FromMinutes(10);

    /// <summary>Bounded queue per event subscriber; overflow sends <c>overflow</c> and closes the stream (design §4.7).</summary>
    public const int EventSubscriberQueueCapacity = 1_000;

    /// <summary>Telemetry ring length on the agent (design §8.1).</summary>
    public static readonly TimeSpan TelemetryRingWindow = TimeSpan.FromMinutes(15);

    /// <summary>Default sampling interval on the agent and pull interval on the master (design §8.1).</summary>
    public static readonly TimeSpan TelemetryInterval = TimeSpan.FromSeconds(5);

    /// <summary>Request body limit per proxied request (today's 64 MiB, design §7.2).</summary>
    public const long MaxProxyRequestBodyBytes = 64L * 1024 * 1024;

    /// <summary>Default in-flight proxied requests per node, on master and agent; excess → 503 <c>server_busy</c> (design §7.2).</summary>
    public const int DefaultMaxInFlightPerNode = 512;

    /// <summary>Default global in-flight upload budget on the master (<c>MirbadLLM:Router:MaxBufferedBodyBytes</c>, design §7.2).</summary>
    public const long DefaultMaxBufferedBodyBytes = 512L * 1024 * 1024;

    /// <summary>How long a request waits for upload budget before 503 <c>server_busy</c> (design §7.2).</summary>
    public static readonly TimeSpan UploadBudgetWait = TimeSpan.FromSeconds(10);

    /// <summary>Proxy response copy buffer (design §7.2).</summary>
    public const int ProxyCopyBufferBytes = 16 * 1024;

    /// <summary>
    /// Proxy paths allowed by default under <c>/proxy/{modelId}/</c> (design §7.2): <c>v1/*</c> plus these exact paths.
    /// Anything else is 404, so vLLM's development endpoints stay out of reach.
    /// </summary>
    public static IReadOnlyList<string> DefaultProxyAllowedPaths { get; } =
        ["v1/*", "health", "metrics", "tokenize", "detokenize", "version", "pooling", "score", "rerank", "classify"];

    /// <summary>Field names whose values are secrets: replaced by <see cref="RedactedValue"/> before hashing or storing a payload (design §4.4 item 1, §10.4).</summary>
    public static IReadOnlyList<string> SecretFieldNames { get; } = ["token", "password"];

    /// <summary>What a secret value is replaced with in stored payloads (design §10.4).</summary>
    public const string RedactedValue = "***";

    /// <summary>Node id rule (design §3.1): <c>^[a-z0-9][a-z0-9-]{0,39}$</c>.</summary>
    public const string NodeIdPattern = "^[a-z0-9][a-z0-9-]{0,39}$";

    /// <summary>The SAN URI of an agent certificate: <c>spiffe://mirbadllm/node/{nodeId}</c>.</summary>
    public static string NodeSpiffeId(string nodeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        return NodeSpiffeIdPrefix + nodeId;
    }

    /// <summary>The SAN URI the CA puts in a new agent certificate (pre-rename trust domain, like <see cref="IssuedMasterSpiffeId"/>).</summary>
    public static string IssuedNodeSpiffeId(string nodeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        return LegacyNodeSpiffeIdPrefix + nodeId;
    }

    /// <summary>True when <paramref name="actual"/> is <paramref name="expected"/> under either trust domain.</summary>
    public static bool SpiffeIdMatches(string? actual, string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        const string current = "spiffe://mirbadllm/";
        return string.Equals(actual, expected, StringComparison.Ordinal)
            || (expected.StartsWith(current, StringComparison.Ordinal)
                && string.Equals(actual, "spiffe://" + LegacyNames.SpiffeTrustDomain + "/" + expected[current.Length..], StringComparison.Ordinal));
    }

    /// <summary>Extracts the node id from an agent SPIFFE id (either trust domain); false when it is not one or the id breaks <see cref="NodeIdPattern"/>.</summary>
    public static bool TryParseNodeSpiffeId(string? spiffeId, out string nodeId)
    {
        nodeId = "";
        string? prefix = spiffeId is null ? null
            : spiffeId.StartsWith(NodeSpiffeIdPrefix, StringComparison.Ordinal) ? NodeSpiffeIdPrefix
            : spiffeId.StartsWith(LegacyNodeSpiffeIdPrefix, StringComparison.Ordinal) ? LegacyNodeSpiffeIdPrefix
            : null;
        if (prefix is null)
        {
            return false;
        }

        string candidate = spiffeId![prefix.Length..];
        if (!IsValidNodeId(candidate))
        {
            return false;
        }

        nodeId = candidate;
        return true;
    }

    /// <summary>True when <paramref name="nodeId"/> matches <see cref="NodeIdPattern"/>.</summary>
    public static bool IsValidNodeId(string? nodeId) => nodeId is not null && NodeIdRegex().IsMatch(nodeId);

    /// <summary>
    /// Protocol negotiation (design §3.6): <c>selected = min(master.current, agent.current)</c>, valid only when
    /// <c>selected ≥ max(master.min, agent.min)</c>. Null = no overlap: the node is Incompatible.
    /// </summary>
    public static int? Negotiate(ProtocolRange master, ProtocolRange agent)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(agent);
        int selected = Math.Min(master.Current, agent.Current);
        return selected >= Math.Max(master.Min, agent.Min) ? selected : null;
    }

    [GeneratedRegex(NodeIdPattern, RegexOptions.CultureInvariant)]
    private static partial Regex NodeIdRegex();
}

/// <summary>
/// Container labels (design §2.4, §4.4 item 3, §5.1, §10.3). The first five predate P1 (the single-host <c>DockerModelRuntime</c>);
/// the agent adds node, operation and the execution metadata. <see cref="Reserved"/> labels are set by the agent itself
/// and a launch spec that sets them is refused with 422 <c>spec_rejected</c>.
/// </summary>
public static class ContainerLabels
{
    /// <summary>Model id; identifies a mirbadllm model container (agent-set).</summary>
    public const string Model = "mirbadllm.model";

    /// <summary>Served name.</summary>
    public const string ServedName = "mirbadllm.served-name";

    /// <summary>Published loopback port.</summary>
    public const string Port = "mirbadllm.port";

    /// <summary>Legacy vLLM config hash (unchanged from today, design §5.1).</summary>
    public const string ConfigHash = "mirbadllm.config-hash";

    /// <summary>Memory budget in GiB.</summary>
    public const string BudgetGiB = "mirbadllm.budget-gib";

    /// <summary><c>1</c> on tuning candidates.</summary>
    public const string Tuning = "mirbadllm.tuning";

    /// <summary>Download helper containers.</summary>
    public const string Download = "mirbadllm.download";

    /// <summary>Node id (agent-set, new in P1; containers without it are adopted by the DGX agent, design §5.4).</summary>
    public const string Node = "mirbadllm.node";

    /// <summary>Operation id that created the resource (agent-set, design §4.4 item 3).</summary>
    public const string Operation = "mirbadllm.operation";

    /// <summary>Execution fingerprint, 16 hex of SHA-256 (design §5.1).</summary>
    public const string ExecutionFingerprint = "mirbadllm.exec-fingerprint";

    /// <summary>Runtime kind recorded by the container driver for truthful read-back.</summary>
    public const string Runtime = "mirbadllm.runtime";

    /// <summary>Runtime-approved readiness path recorded by the container driver.</summary>
    public const string HealthPath = "mirbadllm.health-path";

    /// <summary>Ordered GPU UUIDs, comma-separated (design §5.1 read-back).</summary>
    public const string GpuUuids = "mirbadllm.gpu-uuids";

    /// <summary>Per-GPU total GiB used as the utilization denominator (design §5.1 read-back).</summary>
    public const string GpuTotalGiB = "mirbadllm.gpu-total-gib";

    /// <summary>Labels only the agent sets; a spec carrying any of them is refused (design §10.3).</summary>
    public static IReadOnlyList<string> Reserved { get; } = [Model, Node, Operation];
}

/// <summary>
/// Feature flags in <see cref="NodeCapabilities.Features"/> (design §3.3, §3.6): they gate individual capabilities inside
/// a protocol version; the master checks one before offering an action.
/// </summary>
public static class AgentFeatures
{
    /// <summary>vLLM in containers.</summary>
    public const string RuntimeVllmContainer = "runtime.vllm.container";

    /// <summary>llama.cpp in containers (P3).</summary>
    public const string RuntimeLlamaCppContainer = "runtime.llamacpp.container";

    /// <summary>llama.cpp as a native process (P3/P4).</summary>
    public const string RuntimeLlamaCppProcess = "runtime.llamacpp.process";

    /// <summary>
    /// MLX (<c>mlx_lm.server</c>) as a native process on Apple Silicon (docs/macos/implementation-plan.md, Phase 3). Reported
    /// with a <c>{kind: mlx, delivery: process}</c> runtime when at least one <c>mlx-lm-server</c> version is installed.
    /// </summary>
    public const string RuntimeMlxProcess = "runtime.mlx.process";

    /// <summary>SGLang in a Linux container.</summary>
    public const string RuntimeSglangContainer = "runtime.sglang.container";

    /// <summary>NVIDIA NIM in a container on a node explicitly configured for it.</summary>
    public const string RuntimeNimContainer = "runtime.nim.container";

    /// <summary>Hugging Face downloads through the container helper.</summary>
    public const string DownloadsHfContainer = "downloads.hf-container";

    /// <summary>Native .NET Hugging Face downloader (P3/P4).</summary>
    public const string DownloadsHfNative = "downloads.hf-native";

    /// <summary>systemd units through hostd.</summary>
    public const string ServicesSystemd = "services.systemd";

    /// <summary>Non-model Docker containers.</summary>
    public const string ServicesDocker = "services.docker";

    /// <summary>Windows Service Control Manager (P4).</summary>
    public const string ServicesScm = "services.scm";

    /// <summary>
    /// launchd jobs through the macOS hostd (docs/macos/implementation-plan.md M10, Phase 4): the same
    /// <c>/host/services*</c> and <c>/host/whitelist*</c> routes as <see cref="ServicesSystemd"/>, with launchd labels as
    /// unit names (<see cref="HostServiceManagers.Launchd"/>).
    /// </summary>
    public const string ServicesLaunchd = "services.launchd";

    /// <summary>Backup-share mount through hostd (topology a).</summary>
    public const string BackupMount = "backup-mount";

    /// <summary>GPU processes with owners.</summary>
    public const string GpuProcesses = "gpu-processes";

    /// <summary>Telemetry ring, version 1.</summary>
    public const string TelemetryV1 = "telemetry.v1";

    // ---- P2 (MN-101, design D73). Protocol stays version 1: a P2 master sends a P2-only field or value to an agent only
    // ---- when the agent reports the feature, so a P1 agent (which reports none of these) gets byte-identical requests.

    /// <summary>
    /// The agent launches with <c>gpus.mode = uuids</c> (<c>DeviceIDs</c> / <c>CUDA_VISIBLE_DEVICES</c> by UUID), refuses a
    /// placed UUID that is not on the node with 422 <c>gpu_missing</c> before reserving, and writes the
    /// <see cref="ContainerLabels.GpuUuids"/> / <see cref="ContainerLabels.GpuTotalGiB"/> read-back labels (design §5.1,
    /// MN-106; lifts the P1 refusal of D47). Without it the master sends only <c>gpus.mode = all</c>.
    /// </summary>
    public const string GpuSelectionUuids = "gpus.uuids";

    /// <summary>
    /// The agent accepts <c>spec.env</c> and <c>spec.secretEnvNames</c>, keeps secret values out of
    /// <c>launch-config</c>, logs and the operation log (design §4.3, §10.4, MN-107; lifts the P1 refusal of D47). Without
    /// it the master sends an empty <c>env</c>.
    /// </summary>
    public const string LaunchEnv = "launch.env";

    /// <summary>
    /// Discrete admission (design §4.8, §6.1, MN-105): one start reservation per GPU, the host-memory part serialized
    /// node-wide, per-GPU reserves from <see cref="AdmissionRequest.GpuReservesGiB"/>, refusals naming the GPU. Reported
    /// only by agents whose memory model is <c>discrete</c>; the master sends <see cref="AdmissionRequest.GpuReservesGiB"/>
    /// only to them.
    /// </summary>
    public const string AdmissionDiscrete = "admission.discrete";

    /// <summary>
    /// Launches must name an approved repository digest (<c>spec.imageDigest</c>); the agent creates the container from
    /// <c>repo@digest</c> (design §10.3, MN-107). Informational for the master: it always sends the digest when it knows
    /// one; a node without the feature keeps P1's tag fallback (<c>AllowTagPulls</c>).
    /// </summary>
    public const string ImagesDigestPinned = "images.digest-pinned";

    // ---- P3/P4 (MN-202, design D116). Protocol stays version 1. No P1 or P2 agent reports any of these or the three
    // ---- reused P1 constants above (runtime.llamacpp.process, downloads.hf-native, services.scm), so a P3 master sends a
    // ---- P3 field, value or route only to an agent that reports its flag, and the DGX (P2) gets byte-identical requests.

    /// <summary>
    /// Downloads with <c>source: "ollama"</c> (design-p3p4 §A.2.2, MN-221): the agent pulls a registry manifest's single
    /// model layer, verifies its digest and writes the template/params sidecars. Deferred with owner decision O7 (D113):
    /// no P3/P4 exit criterion needs it, the contract stays frozen.
    /// </summary>
    public const string DownloadsOllama = "downloads.ollama";

    /// <summary><c>GET /model-folders/{folder}/gguf</c> answers a <see cref="GgufSummary"/> (design-p3p4 §A.1.10, MN-209).</summary>
    public const string GgufInspect = "gguf.inspect";

    /// <summary>
    /// Admission honours <see cref="AdmissionRequest.GpuBudgetsGiB"/> per GPU (design-p3p4 §A.1.5, MN-224). The master sends
    /// the map only to agents reporting it; without it the agent splits the budget evenly (P2).
    /// </summary>
    public const string AdmissionGpuBudgets = "admission.gpu-budgets";

    /// <summary>The features a P1 agent may report; everything else in this class is P2 or later.</summary>
    public static IReadOnlyList<string> P1 { get; } =
    [
        RuntimeVllmContainer, RuntimeLlamaCppContainer, RuntimeLlamaCppProcess, DownloadsHfContainer, DownloadsHfNative,
        ServicesSystemd, ServicesDocker, ServicesScm, BackupMount, GpuProcesses, TelemetryV1,
    ];

    /// <summary>Features added by P2 (design D73); a master checks each before using the field or value it gates.</summary>
    public static IReadOnlyList<string> P2 { get; } = [GpuSelectionUuids, LaunchEnv, AdmissionDiscrete, ImagesDigestPinned];

    /// <summary>Features added by P3/P4 (design D116). <see cref="P3Reported"/> lists every flag a P3/P4 agent can newly report.</summary>
    public static IReadOnlyList<string> P3 { get; } = [DownloadsOllama, GgufInspect, AdmissionGpuBudgets];

    /// <summary>Container runtimes added by the runtime expansion.</summary>
    public static IReadOnlyList<string> RuntimeExpansion { get; } = [RuntimeSglangContainer, RuntimeNimContainer];

    /// <summary>
    /// Every flag a P3/P4 agent may report that no P1/P2 agent reports: <see cref="P3"/> plus the three P1 constants first
    /// used now (<see cref="RuntimeLlamaCppProcess"/>, <see cref="DownloadsHfNative"/>, <see cref="ServicesScm"/>). The DGX
    /// agent reports none of them (asserted by MN-232).
    /// </summary>
    public static IReadOnlyList<string> P3Reported { get; } = [RuntimeLlamaCppProcess, DownloadsHfNative, ServicesScm, .. P3];

    /// <summary>Flags only a macOS agent reports (docs/macos/implementation-plan.md); the DGX and Windows agents report none.</summary>
    public static IReadOnlyList<string> MacOS { get; } = [RuntimeMlxProcess, ServicesLaunchd];

    /// <summary>
    /// <c>POST /agent/update</c> works here (docs/multinode/agent-updates.md U6): the node's root update helper is installed.
    /// Never reported by the DGX agent, a container that CI redeploys.
    /// </summary>
    public const string AgentUpdate = "agent.update";

    /// <summary>Flags of the pushed agent update (agent-updates.md); reported by Linux, Windows and macOS agents with the helper.</summary>
    public static IReadOnlyList<string> Updates { get; } = [AgentUpdate];
}

/// <summary>Logical volumes and fixed mount targets a launch spec may use (design §10.3).</summary>
public static class LogicalVolumes
{
    /// <summary>The checkpoints root (read-only for runtimes; read-write only for the download helper).</summary>
    public const string Models = "models";

    /// <summary>The compile/autotune cache root.</summary>
    public const string Cache = "cache";

    /// <summary>Fixed container target of <see cref="Models"/>.</summary>
    public const string ModelsTarget = "/models";

    /// <summary>Fixed container target of <see cref="Cache"/>.</summary>
    public const string CacheTarget = "/root/.cache";

    /// <summary>Argv token a process spec uses for the models root; the agent substitutes and validates it (design §4.3).</summary>
    public const string ModelsToken = "{models}";

    /// <summary>Argv token a process spec uses for the cache root.</summary>
    public const string CacheToken = "{cache}";
}
