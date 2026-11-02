using System.Globalization;
using System.Text;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Contracts;

/// <summary>Where a route's input comes from.</summary>
public enum AgentRouteInput
{
    /// <summary>Path parameters only; request type <see cref="NoBody"/>.</summary>
    None,

    /// <summary>Query string: the request type's properties as camelCase keys (lists comma-separated).</summary>
    Query,

    /// <summary>A JSON body (<see cref="AgentJson.Options"/>).</summary>
    Body,

    /// <summary>The raw client body streamed byte for byte (proxy).</summary>
    Raw,
}

/// <summary>What a route answers.</summary>
public enum AgentRouteOutput
{
    /// <summary>A JSON body of the response type.</summary>
    Json,

    /// <summary>Server-Sent Events (<see cref="AgentJson.EventStreamContentType"/>) of the response type.</summary>
    EventStream,

    /// <summary>The upstream response streamed byte for byte (proxy).</summary>
    Raw,
}

/// <summary>
/// Which of a node's two independently owned handlers carries the route (design §7.2, review N1): <see cref="Control"/>
/// (2 connections, never competes with inference) or <see cref="Data"/> (16 connections, multiple HTTP/2 connections).
/// </summary>
public enum AgentChannel
{
    /// <summary>Status, operations, events, telemetry, PKI and every other non-inference call.</summary>
    Control,

    /// <summary>Proxy (router, chat, benchmark load).</summary>
    Data,
}

/// <summary>Timeout and retry class of a call (design §4.5); see <see cref="AgentCallPolicies"/>.</summary>
public enum AgentCallClass
{
    /// <summary>Reads: connect 3 s, total 10 s, 2 retries (0.5 s, 2 s).</summary>
    Read,

    /// <summary>Mutations returning an operation: connect 3 s, 30 s to the answer; retried with the same id and body only on transport errors before a response.</summary>
    Mutation,

    /// <summary>Host-service actions: connect 3 s, total 60 s (hostd's action timeout), no retries.</summary>
    HostService,

    /// <summary><c>/gpu/idle</c>: connect 3 s, total = seconds + 10 s, no retries.</summary>
    GpuIdle,

    /// <summary>Logs and events: connect 3 s, no total; events reconnect in a loop.</summary>
    Stream,

    /// <summary>Telemetry: connect 3 s, total 5 s, no retries (next interval).</summary>
    Telemetry,

    /// <summary>Proxy: connect 3 s, no total, no retries.</summary>
    Proxy,

    /// <summary>Enrollment (agent → master): TCP connect 3 s (the TLS handshake counts against the total), total 30 s, no automatic retries.</summary>
    Enroll,
}

/// <summary>Timeout/retry policy of one <see cref="AgentCallClass"/> (design §4.5).</summary>
/// <param name="Connect">Connect timeout.</param>
/// <param name="Total">Total timeout; null = none (streams).</param>
/// <param name="RetryDelays">Delays before each retry; empty = no retries.</param>
/// <param name="RetryOnlyBeforeResponse">True: retry only transport errors before any response; an HTTP error response is never retried.</param>
public sealed record AgentCallPolicy(TimeSpan Connect, TimeSpan? Total, IReadOnlyList<TimeSpan> RetryDelays, bool RetryOnlyBeforeResponse);

/// <summary>The §4.5 table as data.</summary>
public static class AgentCallPolicies
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The policy of a call class. For <see cref="AgentCallClass.GpuIdle"/> add the sampled seconds to <see cref="AgentCallPolicy.Total"/>.</summary>
    public static AgentCallPolicy For(AgentCallClass callClass) => callClass switch
    {
        AgentCallClass.Read => new(ConnectTimeout, TimeSpan.FromSeconds(10), [TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(2)], true),
        AgentCallClass.Mutation => new(ConnectTimeout, TimeSpan.FromSeconds(30), [TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(2)], true),
        AgentCallClass.HostService => new(ConnectTimeout, TimeSpan.FromSeconds(60), [], true),
        AgentCallClass.GpuIdle => new(ConnectTimeout, TimeSpan.FromSeconds(10), [], true),
        AgentCallClass.Stream => new(ConnectTimeout, null, [], true),
        AgentCallClass.Telemetry => new(ConnectTimeout, TimeSpan.FromSeconds(5), [], true),
        AgentCallClass.Proxy => new(ConnectTimeout, null, [], true),
        AgentCallClass.Enroll => new(ConnectTimeout, TimeSpan.FromSeconds(30), [], true),
        _ => throw new ArgumentOutOfRangeException(nameof(callClass), callClass, "Unknown call class."),
    };
}

/// <summary>HTTP method names used by <see cref="AgentRoute.Method"/>.</summary>
public static class AgentHttpMethods
{
    /// <summary>GET.</summary>
    public const string Get = "GET";

    /// <summary>POST.</summary>
    public const string Post = "POST";

    /// <summary>PUT.</summary>
    public const string Put = "PUT";

    /// <summary>PATCH.</summary>
    public const string Patch = "PATCH";

    /// <summary>DELETE.</summary>
    public const string Delete = "DELETE";

    /// <summary>Any method (the proxy forwards the client's).</summary>
    public const string Any = "*";
}

/// <summary>
/// One route of the agent API (design §4.3): method, path template (relative to <see cref="Protocol.BasePath"/>, ASP.NET
/// route syntax), request and response types, success status, handler channel, call class and the error codes it may
/// answer. The agent maps every route from <see cref="AgentRoutes.All"/>; <c>FakeAgent</c> and the contract suites
/// enumerate it.
/// </summary>
public sealed record AgentRoute
{
    /// <summary>Unique route name (also the typed client method's stem).</summary>
    public required string Name { get; init; }

    /// <summary>HTTP method (<see cref="AgentHttpMethods"/>).</summary>
    public required string Method { get; init; }

    /// <summary>Path template relative to <see cref="Protocol.BasePath"/>, e.g. <c>/runtimes/{modelId}/launch</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Where the input comes from.</summary>
    public required AgentRouteInput Input { get; init; }

    /// <summary>Request type (<see cref="NoBody"/> / <see cref="RawBody"/> markers when there is no JSON input).</summary>
    public required Type RequestType { get; init; }

    /// <summary>What the route answers.</summary>
    public AgentRouteOutput Output { get; init; } = AgentRouteOutput.Json;

    /// <summary>Response type (the SSE event type for streams, <see cref="RawBody"/> for the proxy).</summary>
    public required Type ResponseType { get; init; }

    /// <summary>Success status (200, or 202 for asynchronous operations).</summary>
    public int SuccessStatus { get; init; } = 200;

    /// <summary>Handler channel.</summary>
    public AgentChannel Channel { get; init; } = AgentChannel.Control;

    /// <summary>Timeout/retry class.</summary>
    public required AgentCallClass CallClass { get; init; }

    /// <summary>The operation kind for mutating routes (<see cref="OperationKinds"/>); null for reads.</summary>
    public string? OperationKind { get; init; }

    /// <summary>Type of <see cref="OperationStatus.Result"/> once the operation succeeded; null when it has none.</summary>
    public Type? ResultType { get; init; }

    /// <summary>Error codes (besides transport failures) the route may answer, with their meaning in <see cref="ErrorCodes"/>.</summary>
    public IReadOnlyList<string> ErrorCodes { get; init; } = [];

    /// <summary>False only for <c>/info</c> (negotiation happens through it).</summary>
    public bool RequiresProtocolHeader { get; init; } = true;

    /// <summary>
    /// True for the calls still allowed while a node is Incompatible or Pending takeover: <c>info</c>, <c>capabilities</c>
    /// and <c>pki/*</c> (design §3.5, §3.6).
    /// </summary>
    public bool AllowedWhenBlocked { get; init; }

    /// <summary>Feature flag the route needs (<see cref="AgentFeatures"/>); null = always available.</summary>
    public string? Feature { get; init; }

    /// <summary>
    /// Other features that offer the same route (MN-202, D116): the route is available when the node reports
    /// <see cref="Feature"/> <b>or</b> any of these. Used where a P3/P4 node serves a P1 route through another subsystem:
    /// <c>POST /downloads</c> with the native downloader (<see cref="AgentFeatures.DownloadsHfNative"/>,
    /// <see cref="AgentFeatures.DownloadsOllama"/>) and the host-service mutations through the Windows SCM
    /// (<see cref="AgentFeatures.ServicesScm"/>) or launchd (<see cref="AgentFeatures.ServicesLaunchd"/>). Empty for every other route. Every feature check goes through
    /// <see cref="IsOfferedBy"/>.
    /// </summary>
    public IReadOnlyList<string> AlternativeFeatures { get; init; } = [];

    /// <summary>
    /// True when a node reporting <paramref name="features"/> offers this route: no <see cref="Feature"/>, or
    /// <see cref="Feature"/> or one of <see cref="AlternativeFeatures"/> reported (ordinal).
    /// </summary>
    public bool IsOfferedBy(IEnumerable<string> features)
    {
        ArgumentNullException.ThrowIfNull(features);
        if (Feature is null)
        {
            return true;
        }

        var reported = features as ICollection<string> ?? [.. features];
        return reported.Contains(Feature, StringComparer.Ordinal) ||
               AlternativeFeatures.Any(f => reported.Contains(f, StringComparer.Ordinal));
    }

    /// <summary>True for mutating routes: the request derives from <see cref="AgentMutation"/> and carries <c>MirbadLLM-Master</c> and <c>MirbadLLM-Request-Time</c>.</summary>
    public bool IsMutation => OperationKind is not null;

    /// <summary>Parameter names in <see cref="Path"/> in order (a catch-all <c>{**path}</c> yields <c>path</c>).</summary>
    public IReadOnlyList<string> PathParameters
    {
        get
        {
            var names = new List<string>();
            int i = 0;
            while ((i = Path.IndexOf('{', i)) >= 0)
            {
                int end = Path.IndexOf('}', i);
                names.Add(Path[(i + 1)..end].TrimStart('*'));
                i = end + 1;
            }

            return names;
        }
    }

    /// <summary>
    /// The relative path with parameters substituted in order, each escaped with <see cref="Uri.EscapeDataString"/>
    /// except a catch-all, whose <c>/</c> separators are kept. Example: <c>Expand("qwen")</c> on
    /// <c>/runtimes/{modelId}/launch</c> → <c>/runtimes/qwen/launch</c>.
    /// </summary>
    public string Expand(params string[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var names = PathParameters;
        if (values.Length != names.Count)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"Route {Name} takes {names.Count} path parameter(s), got {values.Length}."), nameof(values));
        }

        var result = new StringBuilder();
        int index = 0;
        int position = 0;
        while (index < values.Length)
        {
            int open = Path.IndexOf('{', position);
            int close = Path.IndexOf('}', open);
            result.Append(Path, position, open - position);
            bool catchAll = Path[open + 1] == '*';
            string value = values[index] ?? throw new ArgumentException("Path parameter values cannot be null.", nameof(values));
            result.Append(catchAll
                ? string.Join('/', value.Split('/').Select(Uri.EscapeDataString))
                : Uri.EscapeDataString(value));
            position = close + 1;
            index++;
        }

        result.Append(Path, position, Path.Length - position);
        return result.ToString();
    }
}

/// <summary>
/// The agent API catalogue (design §4.3; P1, plus P3's <see cref="GgufInspect"/>, MN-202): every route with its DTOs. Paths are relative to
/// <see cref="Protocol.BasePath"/> (<c>https://{node}:9443/agent/v1</c>). <see cref="Enroll"/> is the master's
/// enroll-listener route, the only agent→master call, kept out of <see cref="All"/>.
/// </summary>
public static class AgentRoutes
{
    private static readonly string[] ReadErrors = [ErrorCodes.ProtocolMismatch, ErrorCodes.InvalidRequest];

    private static readonly string[] MutationErrors =
    [
        ErrorCodes.ProtocolMismatch, ErrorCodes.InvalidRequest, ErrorCodes.ClockSkew, ErrorCodes.StaleMaster,
        ErrorCodes.CompetingMaster, ErrorCodes.OperationIdConflict, ErrorCodes.OperationExpired, ErrorCodes.StateUnavailable,
    ];

    private static readonly string[] HostdErrors =
    [
        ErrorCodes.HostdUnavailable, ErrorCodes.NotFound, ErrorCodes.Denied, ErrorCodes.ConfirmationRequired, ErrorCodes.Timeout,
        ErrorCodes.OutcomeUnknown,
    ];

    private static readonly string[] ContainerErrors =
    [
        ErrorCodes.DockerUnavailable, ErrorCodes.NotFound, ErrorCodes.Denied, ErrorCodes.Timeout, ErrorCodes.OutcomeUnknown,
    ];

    private static readonly string[] PkiErrors = [ErrorCodes.NotFound, ErrorCodes.InvalidState];

    /// <summary>
    /// The Windows SCM (design-p3p4 §A.4.3, MN-228) and the macOS hostd's launchd (docs/macos/implementation-plan.md M10)
    /// offer the systemd-gated host-service mutations too.
    /// </summary>
    private static readonly string[] ScmAlternative = [AgentFeatures.ServicesScm, AgentFeatures.ServicesLaunchd];

    // ---- Identity, health, telemetry ----

    /// <summary><c>GET /info</c>: identity, protocol range, accepted master (no protocol header needed).</summary>
    public static AgentRoute Info { get; } = new()
    {
        Name = "Info", Method = AgentHttpMethods.Get, Path = "/info", Input = AgentRouteInput.None, RequestType = typeof(NoBody),
        ResponseType = typeof(InfoResponse), CallClass = AgentCallClass.Read, RequiresProtocolHeader = false,
        AllowedWhenBlocked = true, ErrorCodes = [],
    };

    /// <summary><c>GET /capabilities</c>: the capability report (design §3.3).</summary>
    public static AgentRoute Capabilities { get; } = new()
    {
        Name = "Capabilities", Method = AgentHttpMethods.Get, Path = "/capabilities", Input = AgentRouteInput.None,
        RequestType = typeof(NoBody), ResponseType = typeof(NodeCapabilities), CallClass = AgentCallClass.Read,
        AllowedWhenBlocked = true, ErrorCodes = ReadErrors,
    };

    /// <summary><c>GET /status</c>: consistent snapshot with its event watermark.</summary>
    public static AgentRoute Status { get; } = new()
    {
        Name = "Status", Method = AgentHttpMethods.Get, Path = "/status", Input = AgentRouteInput.None,
        RequestType = typeof(NoBody), ResponseType = typeof(StatusSnapshot), CallClass = AgentCallClass.Read, ErrorCodes = ReadErrors,
    };

    /// <summary><c>GET /events?bootId&amp;afterSeq</c>: SSE of <see cref="AgentEvent"/> with replay, reset and overflow.</summary>
    public static AgentRoute Events { get; } = new()
    {
        Name = "Events", Method = AgentHttpMethods.Get, Path = "/events", Input = AgentRouteInput.Query,
        RequestType = typeof(EventsQuery), Output = AgentRouteOutput.EventStream, ResponseType = typeof(AgentEvent),
        CallClass = AgentCallClass.Stream, ErrorCodes = ReadErrors,
    };

    /// <summary><c>GET /telemetry?bootId&amp;afterSeq&amp;max</c>: samples from the 15-minute ring.</summary>
    public static AgentRoute Telemetry { get; } = new()
    {
        Name = "Telemetry", Method = AgentHttpMethods.Get, Path = "/telemetry", Input = AgentRouteInput.Query,
        RequestType = typeof(TelemetryQuery), ResponseType = typeof(TelemetryPage), CallClass = AgentCallClass.Telemetry,
        Feature = AgentFeatures.TelemetryV1, ErrorCodes = ReadErrors,
    };

    /// <summary><c>GET /gpu/idle?seconds&amp;gpus</c>: GPU utilization sampled for a few seconds.</summary>
    public static AgentRoute GpuIdle { get; } = new()
    {
        Name = "GpuIdle", Method = AgentHttpMethods.Get, Path = "/gpu/idle", Input = AgentRouteInput.Query,
        RequestType = typeof(GpuIdleQuery), ResponseType = typeof(GpuIdleResponse), CallClass = AgentCallClass.GpuIdle, ErrorCodes = ReadErrors,
    };

    // ---- Runtimes ----

    /// <summary><c>GET /runtimes?label</c>: every instance (or tuning candidates).</summary>
    public static AgentRoute RuntimesList { get; } = new()
    {
        Name = "RuntimesList", Method = AgentHttpMethods.Get, Path = "/runtimes", Input = AgentRouteInput.Query,
        RequestType = typeof(RuntimesQuery), ResponseType = typeof(IReadOnlyList<RuntimeStatus>), CallClass = AgentCallClass.Read,
        ErrorCodes = [.. ReadErrors, ErrorCodes.DockerUnavailable],
    };

    /// <summary><c>GET /runtimes/{modelId}</c>: one instance; 404 <c>not_found</c> = no instance = Stopped.</summary>
    public static AgentRoute RuntimeGet { get; } = new()
    {
        Name = "RuntimeGet", Method = AgentHttpMethods.Get, Path = "/runtimes/{modelId}", Input = AgentRouteInput.None,
        RequestType = typeof(NoBody), ResponseType = typeof(RuntimeStatus), CallClass = AgentCallClass.Read,
        ErrorCodes = [.. ReadErrors, ErrorCodes.NotFound, ErrorCodes.DockerUnavailable],
    };

    /// <summary><c>POST /runtimes/{modelId}/launch</c>: 202 operation after fencing, start rule, admission and reservation.</summary>
    public static AgentRoute RuntimeLaunch { get; } = new()
    {
        Name = "RuntimeLaunch", Method = AgentHttpMethods.Post, Path = "/runtimes/{modelId}/launch", Input = AgentRouteInput.Body,
        RequestType = typeof(LaunchRequest), ResponseType = typeof(OperationStatus), SuccessStatus = 202,
        CallClass = AgentCallClass.Mutation, OperationKind = OperationKinds.Launch, ResultType = typeof(RuntimeStatus),
        ErrorCodes =
        [
            .. MutationErrors, ErrorCodes.AdmissionRefused, ErrorCodes.StartConflict, ErrorCodes.SpecRejected,
            ErrorCodes.GpuMissing, ErrorCodes.DockerUnavailable, ErrorCodes.ExecutableMissing, ErrorCodes.ExecutableUnverified,
        ],
    };

    /// <summary><c>POST /runtimes/{modelId}/stop</c>: 202 operation; no instance → Succeeded.</summary>
    public static AgentRoute RuntimeStop { get; } = new()
    {
        Name = "RuntimeStop", Method = AgentHttpMethods.Post, Path = "/runtimes/{modelId}/stop", Input = AgentRouteInput.Body,
        RequestType = typeof(StopRequest), ResponseType = typeof(OperationStatus), SuccessStatus = 202,
        CallClass = AgentCallClass.Mutation, OperationKind = OperationKinds.Stop,
        ErrorCodes = [.. MutationErrors, ErrorCodes.DockerUnavailable],
    };

    /// <summary><c>GET /runtimes/{modelId}/launch-config</c>: what the instance was launched with.</summary>
    public static AgentRoute RuntimeLaunchConfig { get; } = new()
    {
        Name = "RuntimeLaunchConfig", Method = AgentHttpMethods.Get, Path = "/runtimes/{modelId}/launch-config",
        Input = AgentRouteInput.None, RequestType = typeof(NoBody), ResponseType = typeof(LaunchConfig), CallClass = AgentCallClass.Read,
        ErrorCodes = [.. ReadErrors, ErrorCodes.NotFound, ErrorCodes.DockerUnavailable],
    };

    /// <summary><c>GET /runtimes/{modelId}/logs?tail&amp;follow</c>: SSE, one <c>data:</c> line per log line.</summary>
    public static AgentRoute RuntimeLogs { get; } = new()
    {
        Name = "RuntimeLogs", Method = AgentHttpMethods.Get, Path = "/runtimes/{modelId}/logs", Input = AgentRouteInput.Query,
        RequestType = typeof(LogsQuery), Output = AgentRouteOutput.EventStream, ResponseType = typeof(LogLine),
        CallClass = AgentCallClass.Stream, ErrorCodes = [.. ReadErrors, ErrorCodes.NotFound, ErrorCodes.DockerUnavailable],
    };

    /// <summary>
    /// <c>* /proxy/{modelId}/{**path}</c>: the inference proxy on the data channel (design §7.2). 503
    /// <c>model_not_running</c> before headers when the instance is not running; 404 for paths outside the allow-list.
    /// </summary>
    public static AgentRoute Proxy { get; } = new()
    {
        Name = "Proxy", Method = AgentHttpMethods.Any, Path = "/proxy/{modelId}/{**path}", Input = AgentRouteInput.Raw,
        RequestType = typeof(RawBody), Output = AgentRouteOutput.Raw, ResponseType = typeof(RawBody),
        Channel = AgentChannel.Data, CallClass = AgentCallClass.Proxy,
        ErrorCodes = [ErrorCodes.ProtocolMismatch, ErrorCodes.ModelNotRunning, ErrorCodes.ServerBusy, ErrorCodes.NotFound],
    };

    // ---- Images, folders, downloads ----

    /// <summary><c>GET /images</c>.</summary>
    public static AgentRoute ImagesList { get; } = new()
    {
        Name = "ImagesList", Method = AgentHttpMethods.Get, Path = "/images", Input = AgentRouteInput.None,
        RequestType = typeof(NoBody), ResponseType = typeof(IReadOnlyList<ImageInfo>), CallClass = AgentCallClass.Read,
        ErrorCodes = [.. ReadErrors, ErrorCodes.DockerUnavailable],
    };

    /// <summary><c>POST /images/pull</c>: 202 operation with progress.</summary>
    public static AgentRoute ImagePull { get; } = new()
    {
        Name = "ImagePull", Method = AgentHttpMethods.Post, Path = "/images/pull", Input = AgentRouteInput.Body,
        RequestType = typeof(ImagePullRequest), ResponseType = typeof(OperationStatus), SuccessStatus = 202,
        CallClass = AgentCallClass.Mutation, OperationKind = OperationKinds.ImagePull, ResultType = typeof(ImageInfo),
        ErrorCodes = [.. MutationErrors, ErrorCodes.SpecRejected, ErrorCodes.DockerUnavailable],
    };

    /// <summary><c>GET /model-folders</c>.</summary>
    public static AgentRoute ModelFoldersList { get; } = new()
    {
        Name = "ModelFoldersList", Method = AgentHttpMethods.Get, Path = "/model-folders", Input = AgentRouteInput.None,
        RequestType = typeof(NoBody), ResponseType = typeof(IReadOnlyList<ModelFolderEntry>), CallClass = AgentCallClass.Read,
        ErrorCodes = ReadErrors,
    };

    /// <summary><c>GET /model-folders/{folder}</c>: today's <see cref="ModelFolderInspection"/>.</summary>
    public static AgentRoute ModelFolderInspect { get; } = new()
    {
        Name = "ModelFolderInspect", Method = AgentHttpMethods.Get, Path = "/model-folders/{folder}", Input = AgentRouteInput.None,
        RequestType = typeof(NoBody), ResponseType = typeof(ModelFolderInspection), CallClass = AgentCallClass.Read,
        ErrorCodes = [.. ReadErrors, ErrorCodes.NotFound],
    };

    /// <summary><c>POST /model-folders/{folder}/delete</c>: 200 operation; 409 <c>in_use</c>.</summary>
    public static AgentRoute ModelFolderDelete { get; } = new()
    {
        Name = "ModelFolderDelete", Method = AgentHttpMethods.Post, Path = "/model-folders/{folder}/delete", Input = AgentRouteInput.Body,
        RequestType = typeof(FolderDeleteRequest), ResponseType = typeof(OperationStatus), CallClass = AgentCallClass.Mutation,
        OperationKind = OperationKinds.FolderDelete, ErrorCodes = [.. MutationErrors, ErrorCodes.InUse, ErrorCodes.NotFound],
    };

    /// <summary>
    /// <c>GET /model-folders/{folder}/gguf?file=</c>: the header summary of a GGUF in the folder (design-p3p4 §A.1.10;
    /// P3, MN-202, D116; implemented by MN-209). Gated by <see cref="AgentFeatures.GgufInspect"/>, so a P1/P2 agent (and
    /// a P3 agent before MN-209) answers 501 <c>not_supported</c>.
    /// </summary>
    public static AgentRoute GgufInspect { get; } = new()
    {
        Name = "GgufInspect", Method = AgentHttpMethods.Get, Path = "/model-folders/{folder}/gguf", Input = AgentRouteInput.Query,
        RequestType = typeof(GgufInspectQuery), ResponseType = typeof(GgufSummary), CallClass = AgentCallClass.Read,
        Feature = AgentFeatures.GgufInspect, ErrorCodes = [.. ReadErrors, ErrorCodes.NotFound, ErrorCodes.GgufInvalid],
    };

    /// <summary><c>POST /downloads</c>: 202 with the download status (its <c>operationId</c> names the operation).</summary>
    public static AgentRoute DownloadStart { get; } = new()
    {
        Name = "DownloadStart", Method = AgentHttpMethods.Post, Path = "/downloads", Input = AgentRouteInput.Body,
        RequestType = typeof(DownloadStartRequest), ResponseType = typeof(NodeDownloadStatus), SuccessStatus = 202,
        CallClass = AgentCallClass.Mutation, OperationKind = OperationKinds.Download, ResultType = typeof(NodeDownloadStatus),
        Feature = AgentFeatures.DownloadsHfContainer, AlternativeFeatures = [AgentFeatures.DownloadsHfNative, AgentFeatures.DownloadsOllama],
        ErrorCodes =
        [
            .. MutationErrors, ErrorCodes.AlreadyRunning, ErrorCodes.FolderExists, ErrorCodes.NoFilesMatch,
            ErrorCodes.InsufficientDisk, ErrorCodes.DockerUnavailable, ErrorCodes.UnsupportedManifest,
        ],
    };

    /// <summary><c>GET /downloads</c>.</summary>
    public static AgentRoute DownloadsList { get; } = new()
    {
        Name = "DownloadsList", Method = AgentHttpMethods.Get, Path = "/downloads", Input = AgentRouteInput.None,
        RequestType = typeof(NoBody), ResponseType = typeof(IReadOnlyList<NodeDownloadStatus>), CallClass = AgentCallClass.Read,
        ErrorCodes = ReadErrors,
    };

    /// <summary><c>GET /downloads/{folder}</c>.</summary>
    public static AgentRoute DownloadGet { get; } = new()
    {
        Name = "DownloadGet", Method = AgentHttpMethods.Get, Path = "/downloads/{folder}", Input = AgentRouteInput.None,
        RequestType = typeof(NoBody), ResponseType = typeof(NodeDownloadStatus), CallClass = AgentCallClass.Read,
        ErrorCodes = [.. ReadErrors, ErrorCodes.NotFound],
    };

    /// <summary><c>POST /downloads/{folder}/cancel</c>: 200 operation; already finished → Succeeded with the final state.</summary>
    public static AgentRoute DownloadCancel { get; } = new()
    {
        Name = "DownloadCancel", Method = AgentHttpMethods.Post, Path = "/downloads/{folder}/cancel", Input = AgentRouteInput.Body,
        RequestType = typeof(DownloadCancelRequest), ResponseType = typeof(OperationStatus), CallClass = AgentCallClass.Mutation,
        OperationKind = OperationKinds.DownloadCancel, ResultType = typeof(NodeDownloadStatus),
        ErrorCodes = [.. MutationErrors, ErrorCodes.NotFound],
    };

    // ---- Operations ----

    /// <summary><c>GET /operations/{operationId}</c>.</summary>
    public static AgentRoute OperationGet { get; } = new()
    {
        Name = "OperationGet", Method = AgentHttpMethods.Get, Path = "/operations/{operationId}", Input = AgentRouteInput.None,
        RequestType = typeof(NoBody), ResponseType = typeof(OperationStatus), CallClass = AgentCallClass.Read,
        ErrorCodes = [.. ReadErrors, ErrorCodes.NotFound],
    };

    /// <summary><c>GET /operations?state=active</c>.</summary>
    public static AgentRoute OperationsList { get; } = new()
    {
        Name = "OperationsList", Method = AgentHttpMethods.Get, Path = "/operations", Input = AgentRouteInput.Query,
        RequestType = typeof(OperationsQuery), ResponseType = typeof(IReadOnlyList<OperationStatus>), CallClass = AgentCallClass.Read,
        ErrorCodes = ReadErrors,
    };

    /// <summary><c>POST /operations/{operationId}/cancel</c>: a new operation that cancels a launch (before its instance), pull or download.</summary>
    public static AgentRoute OperationCancel { get; } = new()
    {
        Name = "OperationCancel", Method = AgentHttpMethods.Post, Path = "/operations/{operationId}/cancel", Input = AgentRouteInput.Body,
        RequestType = typeof(OperationCancelRequest), ResponseType = typeof(OperationStatus), CallClass = AgentCallClass.Mutation,
        OperationKind = OperationKinds.OperationCancel, ResultType = typeof(OperationStatus),
        ErrorCodes = [.. MutationErrors, ErrorCodes.NotFound, ErrorCodes.InvalidState],
    };

    // ---- Agent update (docs/multinode/agent-updates.md) ----

    /// <summary>
    /// <c>POST /agent/update</c>: download the master's bundle, verify it and hand it to the node's update helper; 202, the
    /// operation completes when the agent runs the new build (or the helper reports the installer rolled back).
    /// </summary>
    public static AgentRoute AgentUpdate { get; } = new()
    {
        Name = "AgentUpdate", Method = AgentHttpMethods.Post, Path = "/agent/update", Input = AgentRouteInput.Body,
        RequestType = typeof(AgentUpdateRequest), ResponseType = typeof(OperationStatus), SuccessStatus = 202,
        CallClass = AgentCallClass.Mutation, OperationKind = OperationKinds.AgentUpdate, ResultType = typeof(AgentUpdateResult),
        Feature = AgentFeatures.AgentUpdate, ErrorCodes = [.. MutationErrors, ErrorCodes.NotSupported],
    };

    // ---- Host services (feature-gated; synchronous; bounded by hostd's 60 s) ----

    /// <summary><c>GET /host/services?include=all</c>: hostd units and containers.</summary>
    public static AgentRoute HostServicesList { get; } = new()
    {
        Name = "HostServicesList", Method = AgentHttpMethods.Get, Path = "/host/services", Input = AgentRouteInput.Query,
        RequestType = typeof(HostServicesQuery), ResponseType = typeof(HostServicesResponse), CallClass = AgentCallClass.HostService,
        ErrorCodes = ReadErrors,
    };

    /// <summary><c>POST /host/services/{name}/start</c> (desired state).</summary>
    public static AgentRoute HostServiceStart { get; } = HostUnitAction("HostServiceStart", "start", OperationKinds.ServiceStart);

    /// <summary><c>POST /host/services/{name}/stop</c> (desired state).</summary>
    public static AgentRoute HostServiceStop { get; } = HostUnitAction("HostServiceStop", "stop", OperationKinds.ServiceStop);

    /// <summary><c>POST /host/services/{name}/restart</c> (unverifiable → Unknown after a crash).</summary>
    public static AgentRoute HostServiceRestart { get; } = HostUnitAction("HostServiceRestart", "restart", OperationKinds.ServiceRestart);

    /// <summary><c>POST /host/services/{name}/autostart</c>.</summary>
    public static AgentRoute HostServiceAutostart { get; } = new()
    {
        Name = "HostServiceAutostart", Method = AgentHttpMethods.Post, Path = "/host/services/{name}/autostart", Input = AgentRouteInput.Body,
        RequestType = typeof(HostServiceAutostartRequest), ResponseType = typeof(OperationStatus), CallClass = AgentCallClass.HostService,
        OperationKind = OperationKinds.ServiceAutostart, ResultType = typeof(HostUnit), Feature = AgentFeatures.ServicesSystemd, AlternativeFeatures = ScmAlternative,
        ErrorCodes = [.. MutationErrors, .. HostdErrors],
    };

    /// <summary><c>POST /host/whitelist</c>.</summary>
    public static AgentRoute HostWhitelistAdd { get; } = new()
    {
        Name = "HostWhitelistAdd", Method = AgentHttpMethods.Post, Path = "/host/whitelist", Input = AgentRouteInput.Body,
        RequestType = typeof(HostWhitelistAddRequest), ResponseType = typeof(OperationStatus), CallClass = AgentCallClass.HostService,
        OperationKind = OperationKinds.WhitelistAdd, ResultType = typeof(HostUnit), Feature = AgentFeatures.ServicesSystemd, AlternativeFeatures = ScmAlternative,
        ErrorCodes = [.. MutationErrors, .. HostdErrors, ErrorCodes.Conflict],
    };

    /// <summary><c>PATCH /host/whitelist/{name}</c>.</summary>
    public static AgentRoute HostWhitelistUpdate { get; } = new()
    {
        Name = "HostWhitelistUpdate", Method = AgentHttpMethods.Patch, Path = "/host/whitelist/{name}", Input = AgentRouteInput.Body,
        RequestType = typeof(HostWhitelistUpdateRequest), ResponseType = typeof(OperationStatus), CallClass = AgentCallClass.HostService,
        OperationKind = OperationKinds.WhitelistUpdate, ResultType = typeof(HostUnit), Feature = AgentFeatures.ServicesSystemd, AlternativeFeatures = ScmAlternative,
        ErrorCodes = [.. MutationErrors, .. HostdErrors],
    };

    /// <summary><c>DELETE /host/whitelist/{name}</c>.</summary>
    public static AgentRoute HostWhitelistRemove { get; } = new()
    {
        Name = "HostWhitelistRemove", Method = AgentHttpMethods.Delete, Path = "/host/whitelist/{name}", Input = AgentRouteInput.Body,
        RequestType = typeof(HostWhitelistRemoveRequest), ResponseType = typeof(OperationStatus), CallClass = AgentCallClass.HostService,
        OperationKind = OperationKinds.WhitelistRemove, Feature = AgentFeatures.ServicesSystemd, AlternativeFeatures = ScmAlternative,
        ErrorCodes = [.. MutationErrors, .. HostdErrors],
    };

    /// <summary><c>GET /host/gpu-processes</c>: hostd's GPU processes with owners.</summary>
    public static AgentRoute HostGpuProcesses { get; } = new()
    {
        Name = "HostGpuProcesses", Method = AgentHttpMethods.Get, Path = "/host/gpu-processes", Input = AgentRouteInput.None,
        RequestType = typeof(NoBody), ResponseType = typeof(IReadOnlyList<GpuProcess>), CallClass = AgentCallClass.Read,
        Feature = AgentFeatures.GpuProcesses, ErrorCodes = [.. ReadErrors, ErrorCodes.HostdUnavailable, ErrorCodes.NotSupported],
    };

    /// <summary><c>GET /host/containers/{name}</c>: one non-model container (additive to §4.3, used after actions).</summary>
    public static AgentRoute HostContainerGet { get; } = new()
    {
        Name = "HostContainerGet", Method = AgentHttpMethods.Get, Path = "/host/containers/{name}", Input = AgentRouteInput.None,
        RequestType = typeof(NoBody), ResponseType = typeof(HostContainerInfo), CallClass = AgentCallClass.Read,
        Feature = AgentFeatures.ServicesDocker, ErrorCodes = [.. ReadErrors, .. ContainerErrors],
    };

    /// <summary><c>POST /host/containers/{name}/start</c> (desired state).</summary>
    public static AgentRoute HostContainerStart { get; } = ContainerAction("HostContainerStart", "start", OperationKinds.ContainerStart);

    /// <summary><c>POST /host/containers/{name}/stop</c> (desired state).</summary>
    public static AgentRoute HostContainerStop { get; } = ContainerAction("HostContainerStop", "stop", OperationKinds.ContainerStop);

    /// <summary><c>POST /host/containers/{name}/restart</c> (unverifiable → Unknown after a crash).</summary>
    public static AgentRoute HostContainerRestart { get; } = ContainerAction("HostContainerRestart", "restart", OperationKinds.ContainerRestart);

    /// <summary><c>POST /host/containers/{name}/autostart</c>: set the restart policy.</summary>
    public static AgentRoute HostContainerAutostart { get; } = new()
    {
        Name = "HostContainerAutostart", Method = AgentHttpMethods.Post, Path = "/host/containers/{name}/autostart", Input = AgentRouteInput.Body,
        RequestType = typeof(HostContainerAutostartRequest), ResponseType = typeof(OperationStatus), CallClass = AgentCallClass.HostService,
        OperationKind = OperationKinds.ContainerAutostart, ResultType = typeof(HostContainerInfo), Feature = AgentFeatures.ServicesDocker,
        ErrorCodes = [.. MutationErrors, .. ContainerErrors],
    };

    /// <summary><c>GET /host/backup-mount</c>.</summary>
    public static AgentRoute BackupMountGet { get; } = new()
    {
        Name = "BackupMountGet", Method = AgentHttpMethods.Get, Path = "/host/backup-mount", Input = AgentRouteInput.None,
        RequestType = typeof(NoBody), ResponseType = typeof(BackupMountInfo), CallClass = AgentCallClass.HostService,
        Feature = AgentFeatures.BackupMount, ErrorCodes = [.. ReadErrors, ErrorCodes.HostdUnavailable, ErrorCodes.NotSupported],
    };

    /// <summary><c>PUT /host/backup-mount</c> (password redacted from the stored operation; unverifiable → Unknown).</summary>
    public static AgentRoute BackupMountSet { get; } = new()
    {
        Name = "BackupMountSet", Method = AgentHttpMethods.Put, Path = "/host/backup-mount", Input = AgentRouteInput.Body,
        RequestType = typeof(BackupMountSetRequest), ResponseType = typeof(OperationStatus), CallClass = AgentCallClass.HostService,
        OperationKind = OperationKinds.BackupMountSet, ResultType = typeof(BackupMountInfo), Feature = AgentFeatures.BackupMount,
        ErrorCodes = [.. MutationErrors, ErrorCodes.HostdUnavailable, ErrorCodes.NotSupported, ErrorCodes.Timeout, ErrorCodes.OutcomeUnknown],
    };

    /// <summary><c>DELETE /host/backup-mount</c> (desired state).</summary>
    public static AgentRoute BackupMountRemove { get; } = new()
    {
        Name = "BackupMountRemove", Method = AgentHttpMethods.Delete, Path = "/host/backup-mount", Input = AgentRouteInput.Body,
        RequestType = typeof(BackupMountRemoveRequest), ResponseType = typeof(OperationStatus), CallClass = AgentCallClass.HostService,
        OperationKind = OperationKinds.BackupMountRemove, ResultType = typeof(BackupMountInfo), Feature = AgentFeatures.BackupMount,
        ErrorCodes = [.. MutationErrors, ErrorCodes.HostdUnavailable, ErrorCodes.NotSupported, ErrorCodes.Timeout, ErrorCodes.OutcomeUnknown],
    };

    /// <summary><c>POST /host/backup-mount/test</c>: read-only check, not an operation.</summary>
    public static AgentRoute BackupMountTest { get; } = new()
    {
        Name = "BackupMountTest", Method = AgentHttpMethods.Post, Path = "/host/backup-mount/test", Input = AgentRouteInput.Body,
        RequestType = typeof(BackupMountTestRequest), ResponseType = typeof(BackupMountTestResult), CallClass = AgentCallClass.HostService,
        Feature = AgentFeatures.BackupMount, ErrorCodes = [.. ReadErrors, ErrorCodes.HostdUnavailable, ErrorCodes.NotSupported, ErrorCodes.Timeout],
    };

    // ---- PKI (design §10.2; allowed while blocked) ----

    /// <summary><c>POST /pki/rotations</c>: pending key + CSR (same CSR on retry).</summary>
    public static AgentRoute PkiRotationStart { get; } = new()
    {
        Name = "PkiRotationStart", Method = AgentHttpMethods.Post, Path = "/pki/rotations", Input = AgentRouteInput.Body,
        RequestType = typeof(PkiRotationStartRequest), ResponseType = typeof(OperationStatus), CallClass = AgentCallClass.Mutation,
        OperationKind = OperationKinds.PkiRotationStart, ResultType = typeof(PkiRotationCsr), AllowedWhenBlocked = true,
        ErrorCodes = [.. MutationErrors, ErrorCodes.InvalidState],
    };

    /// <summary><c>PUT /pki/rotations/{rotationId}/cert</c>: install the new certificate as pending.</summary>
    public static AgentRoute PkiRotationCert { get; } = new()
    {
        Name = "PkiRotationCert", Method = AgentHttpMethods.Put, Path = "/pki/rotations/{rotationId}/cert", Input = AgentRouteInput.Body,
        RequestType = typeof(PkiRotationCertRequest), ResponseType = typeof(OperationStatus), CallClass = AgentCallClass.Mutation,
        OperationKind = OperationKinds.PkiRotationCert, ResultType = typeof(PkiRotationStatus), AllowedWhenBlocked = true,
        ErrorCodes = [.. MutationErrors, .. PkiErrors],
    };

    /// <summary><c>POST /pki/rotations/{rotationId}/commit</c>: irreversible, idempotent; over a fresh connection.</summary>
    public static AgentRoute PkiRotationCommit { get; } = new()
    {
        Name = "PkiRotationCommit", Method = AgentHttpMethods.Post, Path = "/pki/rotations/{rotationId}/commit", Input = AgentRouteInput.Body,
        RequestType = typeof(PkiRotationCommitRequest), ResponseType = typeof(OperationStatus), CallClass = AgentCallClass.Mutation,
        OperationKind = OperationKinds.PkiRotationCommit, ResultType = typeof(PkiRotationStatus), AllowedWhenBlocked = true,
        ErrorCodes = [.. MutationErrors, .. PkiErrors],
    };

    /// <summary><c>POST /pki/rotations/{rotationId}/abort</c>: before commit only; afterwards 409 <c>already_committed</c>.</summary>
    public static AgentRoute PkiRotationAbort { get; } = new()
    {
        Name = "PkiRotationAbort", Method = AgentHttpMethods.Post, Path = "/pki/rotations/{rotationId}/abort", Input = AgentRouteInput.Body,
        RequestType = typeof(PkiRotationAbortRequest), ResponseType = typeof(OperationStatus), CallClass = AgentCallClass.Mutation,
        OperationKind = OperationKinds.PkiRotationAbort, ResultType = typeof(PkiRotationStatus), AllowedWhenBlocked = true,
        ErrorCodes = [.. MutationErrors, .. PkiErrors, ErrorCodes.AlreadyCommitted],
    };

    /// <summary><c>GET /pki/rotations/{rotationId}</c>: the agent-side stage (resolves a lost commit response).</summary>
    public static AgentRoute PkiRotationGet { get; } = new()
    {
        Name = "PkiRotationGet", Method = AgentHttpMethods.Get, Path = "/pki/rotations/{rotationId}", Input = AgentRouteInput.None,
        RequestType = typeof(NoBody), ResponseType = typeof(PkiRotationStatus), CallClass = AgentCallClass.Read, AllowedWhenBlocked = true,
        ErrorCodes = [.. ReadErrors, ErrorCodes.NotFound],
    };

    /// <summary><c>GET /pki/trust</c>.</summary>
    public static AgentRoute PkiTrustGet { get; } = new()
    {
        Name = "PkiTrustGet", Method = AgentHttpMethods.Get, Path = "/pki/trust", Input = AgentRouteInput.None,
        RequestType = typeof(NoBody), ResponseType = typeof(PkiTrustResponse), CallClass = AgentCallClass.Read, AllowedWhenBlocked = true,
        ErrorCodes = ReadErrors,
    };

    /// <summary><c>PUT /pki/trust</c>: replace the anchors (keeps one that validates the caller).</summary>
    public static AgentRoute PkiTrustSet { get; } = new()
    {
        Name = "PkiTrustSet", Method = AgentHttpMethods.Put, Path = "/pki/trust", Input = AgentRouteInput.Body,
        RequestType = typeof(PkiTrustSetRequest), ResponseType = typeof(OperationStatus), CallClass = AgentCallClass.Mutation,
        OperationKind = OperationKinds.PkiTrustSet, ResultType = typeof(PkiTrustResponse), AllowedWhenBlocked = true,
        ErrorCodes = [.. MutationErrors, ErrorCodes.InvalidState],
    };

    // ---- Master enroll listener (agent → master) ----

    /// <summary>
    /// <c>POST https://{enroll listener}/enroll</c> (design §3.2). Not under <see cref="Protocol.BasePath"/> and not in
    /// <see cref="All"/>: it is served by the master on its enroll listener.
    /// </summary>
    public static AgentRoute Enroll { get; } = new()
    {
        Name = "Enroll", Method = AgentHttpMethods.Post, Path = "/enroll", Input = AgentRouteInput.Body,
        RequestType = typeof(EnrollRequest), ResponseType = typeof(EnrollResponse), CallClass = AgentCallClass.Enroll,
        RequiresProtocolHeader = false, ErrorCodes = ErrorCodes.EnrollCodes,
    };

    /// <summary>
    /// <c>POST https://{enroll listener}/enroll/rekey</c> (design §9.4 step 5; P2, MN-114, D73): re-pairs an enrolled node,
    /// keeping its uid. Served by the master on its enroll listener with <see cref="Enroll"/>'s rate limit; not in
    /// <see cref="All"/>.
    /// </summary>
    public static AgentRoute Rekey { get; } = new()
    {
        Name = "Rekey", Method = AgentHttpMethods.Post, Path = "/enroll/rekey", Input = AgentRouteInput.Body,
        RequestType = typeof(RekeyRequest), ResponseType = typeof(EnrollResponse), CallClass = AgentCallClass.Enroll,
        RequiresProtocolHeader = false, ErrorCodes = ErrorCodes.EnrollCodes,
    };

    /// <summary>The master's enroll-listener routes (agent → master), not under <see cref="Protocol.BasePath"/>.</summary>
    public static IReadOnlyList<AgentRoute> EnrollListener { get; } = [Enroll, Rekey];

    /// <summary>Every agent route, in catalogue order.</summary>
    public static IReadOnlyList<AgentRoute> All { get; } =
    [
        Info, Capabilities, Status, Events, Telemetry, GpuIdle,
        RuntimesList, RuntimeGet, RuntimeLaunch, RuntimeStop, RuntimeLaunchConfig, RuntimeLogs, Proxy,
        ImagesList, ImagePull, ModelFoldersList, ModelFolderInspect, ModelFolderDelete, GgufInspect,
        DownloadStart, DownloadsList, DownloadGet, DownloadCancel,
        OperationGet, OperationsList, OperationCancel,
        HostServicesList, HostServiceStart, HostServiceStop, HostServiceRestart, HostServiceAutostart,
        HostWhitelistAdd, HostWhitelistUpdate, HostWhitelistRemove, HostGpuProcesses, AgentUpdate,
        HostContainerGet, HostContainerStart, HostContainerStop, HostContainerRestart, HostContainerAutostart,
        BackupMountGet, BackupMountSet, BackupMountRemove, BackupMountTest,
        PkiRotationStart, PkiRotationCert, PkiRotationCommit, PkiRotationAbort, PkiRotationGet, PkiTrustGet, PkiTrustSet,
    ];

    /// <summary>
    /// Every JSON DTO type the catalogue uses (requests, responses, results, the enroll route, and every concrete
    /// <see cref="AgentEvent"/>), with list types unwrapped and the markers <see cref="NoBody"/>, <see cref="RawBody"/>
    /// and <see cref="LogLine"/> left out. Golden-fixture tests (MN-003) enumerate this.
    /// </summary>
    public static IReadOnlyList<Type> DtoTypes { get; } = CollectDtoTypes();

    private static AgentRoute HostUnitAction(string name, string action, string kind) => new()
    {
        Name = name, Method = AgentHttpMethods.Post, Path = "/host/services/{name}/" + action, Input = AgentRouteInput.Body,
        RequestType = typeof(HostServiceActionRequest), ResponseType = typeof(OperationStatus), CallClass = AgentCallClass.HostService,
        OperationKind = kind, ResultType = typeof(HostUnit), Feature = AgentFeatures.ServicesSystemd, AlternativeFeatures = ScmAlternative,
        ErrorCodes = [.. MutationErrors, .. HostdErrors],
    };

    private static AgentRoute ContainerAction(string name, string action, string kind) => new()
    {
        Name = name, Method = AgentHttpMethods.Post, Path = "/host/containers/{name}/" + action, Input = AgentRouteInput.Body,
        RequestType = typeof(HostContainerActionRequest), ResponseType = typeof(OperationStatus), CallClass = AgentCallClass.HostService,
        OperationKind = kind, ResultType = typeof(HostContainerInfo), Feature = AgentFeatures.ServicesDocker,
        ErrorCodes = [.. MutationErrors, .. ContainerErrors],
    };

    private static List<Type> CollectDtoTypes()
    {
        var markers = new HashSet<Type> { typeof(NoBody), typeof(RawBody), typeof(LogLine) };
        var seen = new HashSet<Type>();
        var result = new List<Type>();

        void Add(Type? type)
        {
            if (type is null)
            {
                return;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            {
                type = type.GetGenericArguments()[0];
            }

            if (!markers.Contains(type) && !type.IsAbstract && seen.Add(type))
            {
                result.Add(type);
            }
        }

        foreach (var route in All.Concat(EnrollListener))
        {
            Add(route.RequestType);
            Add(route.ResponseType);
            Add(route.ResultType);
        }

        foreach (var derived in new[]
                 {
                     typeof(HeartbeatEvent), typeof(RuntimeEvent), typeof(OperationEvent), typeof(DownloadEvent),
                     typeof(ReservationEvent), typeof(HealthEvent), typeof(ResetEvent), typeof(OverflowEvent),
                 })
        {
            Add(derived);
        }

        return result;
    }
}
