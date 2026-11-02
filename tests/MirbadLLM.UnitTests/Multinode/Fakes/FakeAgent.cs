using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Multinode.Contract;

namespace MirbadLLM.UnitTests.Multinode.Fakes;

/// <summary>
/// An in-process agent that speaks the P1 protocol of design §4 over real mTLS + HTTP/2 (Kestrel on loopback), with
/// simulated instances, unified memory, a durable operation log and fencing triple in a temp state directory, the event
/// bus with replay/reset/overflow, telemetry, host services, PKI rotation, and an inference proxy to a
/// <see cref="FakeModelServer"/>. It implements every route of <see cref="AgentRoutes.All"/>.
/// <para>Faithfulness rule (MN-022 brief): whatever <c>AgentContractSuite</c> asserts holds for the real agent too. The
/// knobs (<see cref="SetPartitioned"/>, <see cref="DropResponses"/>, <see cref="CrashAfterStage"/>,
/// <see cref="RestartAsync"/>, …) inject the failures MN-043/MN-044 need; they never change protocol semantics.</para>
/// <para>Checks run in the order of design §4.9: protocol header (400 <c>protocol_mismatch</c>), feature (501), master
/// and request-time headers (400), body (400 <c>invalid_request</c>), fencing (409), envelope and deduplication
/// (400 <c>clock_skew</c> / stored status / 409 <c>operation_id_conflict</c> / 410), route validation, then durable
/// acceptance and execution. Deterministic refusals (404, 409, 422) happen before acceptance, so nothing is recorded.</para>
/// </summary>
internal sealed partial class FakeAgent : IAgentContractHarness
{
    private readonly object _gate = new();
    private readonly FakeAgentOptions _options;
    private readonly TimeProvider _time;
    private readonly string _stateDir;
    private readonly bool _ownsStateDir;
    private readonly CertificateAuthority _ca;
    private readonly bool _ownsCa;
    private readonly IssuedCredential _masterCredential;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<IDisposable> _retired = [];
    private readonly Dictionary<string, Func<FakeCall, Task<FakeReply>>> _handlers;
    private readonly FakeGate _starts = new();
    private readonly FakeGate _pulls = new();
    private readonly FakeGate _downloadsGate = new();
    private readonly List<string> _requests = [];
    private WebApplication? _app;
    private Boot _boot;
    private MasterTriple? _acceptedMaster;
    private bool _partitioned;
    private bool _crashed;
    private bool _failStarts;
    private Func<AgentRoute, bool>? _dropResponse;
    private (string Kind, string Stage)? _crashAfter;
    private Task? _telemetryLoop;

    private FakeAgent(FakeAgentOptions options)
    {
        _options = options;
        _time = options.Time;
        NodeId = options.NodeId;
        NodeUid = options.NodeUid ?? "uid-" + Guid.NewGuid().ToString("N")[..12];
        _ownsStateDir = options.StateDirectory is null;
        _stateDir = options.StateDirectory ?? Path.Combine(Path.GetTempPath(), "fake-agent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_stateDir, "operations"));
        _ownsCa = options.Ca is null;
        _ca = options.Ca ?? CertificateAuthority.Create();
        _masterCredential = _ca.IssueMasterClientCertificate();
        MasterCertificate = _masterCredential.CreateTlsCertificate();
        _boot = new Boot(NewBootId(), _time.GetUtcNow());
        _handlers = CreateHandlers();
        InitializePki();
        LoadFencing();
        if (options.SeedContractFixture)
        {
            SeedContractFixture();
        }
    }

    /// <summary>Node id.</summary>
    public string NodeId { get; }

    /// <summary>Node uid.</summary>
    public string NodeUid { get; }

    /// <summary>Current boot id (changes on <see cref="RestartAsync"/>).</summary>
    public string BootId
    {
        get
        {
            lock (_gate)
            {
                return _boot.Id;
            }
        }
    }

    /// <summary><c>https://127.0.0.1:{port}/</c>.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>The master CA (the agent's trust anchor).</summary>
    public CertificateAuthority Ca => _ca;

    /// <summary>A master client certificate (TLS-ready) issued by <see cref="Ca"/>.</summary>
    public X509Certificate2 MasterCertificate { get; }

    /// <summary>The state directory (operation log <c>operations/log.jsonl</c>, <c>fencing.json</c>).</summary>
    public string StateDirectory => _stateDir;

    /// <summary>
    /// The name of every agent route this fake was asked for, in order (MN-233), including the ones it then refused.
    /// Proves what the master did — and did not — send to a node.
    /// </summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Takes one telemetry sample now (the loop's work), so a test does not wait for the interval (MN-233).</summary>
    public void TakeTelemetrySample()
    {
        lock (_gate)
        {
            TakeSampleLocked();
        }
    }

    /// <inheritdoc/>
    public AgentContractTarget Target => new(BaseAddress, NodeId, _ca, MasterCertificate, CurrentSpki);

    /// <summary>Starts a fake agent on a random loopback port.</summary>
    public static async Task<FakeAgent> StartAsync(FakeAgentOptions? options = null, CancellationToken ct = default)
    {
        var agent = new FakeAgent(options ?? new FakeAgentOptions());
        try
        {
            await agent.StartHostAsync(ct);
            return agent;
        }
        catch
        {
            await agent.DisposeAsync();
            throw;
        }
    }

    // ---- Harness knobs (IAgentContractHarness) ----

    /// <inheritdoc/>
    public Task SetStartsHeldAsync(bool held)
    {
        _starts.Set(held);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SetStartsFailingAsync(bool failing)
    {
        lock (_gate)
        {
            _failStarts = failing;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SetPullsHeldAsync(bool held)
    {
        _pulls.Set(held);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SetDockerAvailableAsync(bool available)
    {
        SetDockerAvailable(available);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SetDownloadsHeldAsync(bool held)
    {
        _downloadsGate.Set(held);
        return Task.CompletedTask;
    }

    // ---- Failure knobs (MN-043/MN-044) ----

    /// <summary>
    /// A network partition: every request (and open stream) is cut at the transport while it lasts; the agent keeps
    /// running its operations (design §4.6).
    /// </summary>
    public void SetPartitioned(bool partitioned)
    {
        lock (_gate)
        {
            _partitioned = partitioned;
            if (partitioned)
            {
                AbortSubscribersLocked();
            }
        }
    }

    /// <summary>
    /// Lost acknowledgement: matching mutations are executed and recorded normally, then the connection is aborted
    /// instead of answering (design §13 item 6 "lost acknowledgement"). Null clears it.
    /// </summary>
    public void DropResponses(Func<AgentRoute, bool>? predicate)
    {
        lock (_gate)
        {
            _dropResponse = predicate;
        }
    }

    /// <summary>
    /// Simulated agent crash right after an operation of <paramref name="kind"/> durably recorded <paramref name="stage"/>:
    /// all in-flight work stops, every request is cut until <see cref="RestartAsync"/> (design §4.4 item 5, §4.6).
    /// </summary>
    public void CrashAfterStage(string kind, string stage)
    {
        lock (_gate)
        {
            _crashAfter = (kind, stage);
        }
    }

    /// <summary>Crashes the agent now (same effect as a <see cref="CrashAfterStage"/> trigger).</summary>
    public void Crash()
    {
        lock (_gate)
        {
            CrashLocked();
        }
    }

    /// <summary>True after a crash until <see cref="RestartAsync"/>.</summary>
    public bool Crashed
    {
        get
        {
            lock (_gate)
            {
                return _crashed;
            }
        }
    }

    /// <summary>Sets the memory available with no instance running.</summary>
    public void SetAvailableMemory(double availableGiB)
    {
        lock (_gate)
        {
            _baseAvailableGiB = availableGiB;
        }
    }

    // ---- Lifetime ----

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        lock (_gate)
        {
            _boot.Cts.Cancel();
            AbortSubscribersLocked();
        }

        _starts.Set(false);
        _pulls.Set(false);
        _downloadsGate.Set(false);
        if (_app is not null)
        {
            try
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _app.StopAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
                // Streams that ignore shutdown are cut.
            }

            await _app.DisposeAsync();
        }

        if (_telemetryLoop is not null)
        {
            try
            {
                await _telemetryLoop;
            }
            catch (OperationCanceledException)
            {
                // Expected on dispose.
            }
        }

        if (_modelServer is not null)
        {
            await ((IAsyncDisposable)_modelServer).DisposeAsync();
        }

        _upstream.Dispose();
        lock (_gate)
        {
            CloseLogLocked();
            DisposePkiLocked();
            foreach (var retired in _retired)
            {
                retired.Dispose();
            }

            _boot.Cts.Dispose();
        }

        // MasterCertificate is left to the finalizer: test clients present it, and a connection attempt a disposed client
        // abandoned can still be in its handshake; disposing it under SChannel is a use-after-free (MN-047).
        _masterCredential.Dispose();
        if (_ownsCa)
        {
            _ca.Dispose();
        }

        _lifetime.Dispose();
        if (_ownsStateDir)
        {
            try
            {
                Directory.Delete(_stateDir, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    private async Task StartHostAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = MirbadLLM.Agent.Contracts.Protocol.MaxProxyRequestBodyBytes + (1024 * 1024);
            kestrel.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificateSelector = (_, _) => SelectServerCertificate(),
                    ClientCertificateMode = ClientCertificateMode.RequireCertificate,
                    CheckCertificateRevocation = false,
                    ClientCertificateValidation = (certificate, chain, errors) => _masterValidator.ValidateClientCertificate(certificate, chain, errors),
                    SslProtocols = PkiTls.Protocols,
                });
            });
        });

        var app = builder.Build();
        foreach (var route in AgentRoutes.All)
        {
            string pattern = MirbadLLM.Agent.Contracts.Protocol.BasePath + route.Path;
            if (route.Method == AgentHttpMethods.Any)
            {
                app.Map(pattern, context => HandleAsync(context, route));
            }
            else
            {
                app.MapMethods(pattern, [route.Method], context => HandleAsync(context, route));
            }
        }

        await app.StartAsync(ct);
        _app = app;
        string address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
        BaseAddress = new Uri($"https://127.0.0.1:{new Uri(address).Port}/");
        lock (_gate)
        {
            TakeSampleLocked();
        }

        _telemetryLoop = Task.Run(() => TelemetryLoopAsync(_lifetime.Token), CancellationToken.None);
    }

    // ---- Request pipeline ----

    private bool Unreachable
    {
        get
        {
            lock (_gate)
            {
                return _partitioned || _crashed;
            }
        }
    }

    private async Task HandleAsync(HttpContext http, AgentRoute route)
    {
        lock (_gate)
        {
            // Every request that reached this agent, in order (MN-233: "nothing was sent to the node that is not approved").
            _requests.Add(route.Name);
        }

        if (Unreachable)
        {
            http.Abort();
            return;
        }

        FakeReply reply;
        FakeCall? call = null;
        try
        {
            call = await PrepareAsync(http, route);
            reply = await _handlers[route.Name](call);
        }
        catch (AgentException ex)
        {
            RecordRefusal(call, ex);
            reply = FakeReply.Error(ex.StatusCode, ex.Error);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        if (reply.Streamed)
        {
            return;
        }

        if (route.IsMutation && reply.Status < 300 && ShouldDrop(route))
        {
            http.Abort();
            return;
        }

        if (Unreachable)
        {
            http.Abort();
            return;
        }

        http.Response.StatusCode = reply.Status;
        http.Response.ContentType = AgentJson.JsonContentType;
        if (reply.Body is AgentErrorResponse agentError)
        {
            http.Response.Headers[MirbadLLM.Agent.Contracts.Protocol.AgentErrorHeader] = agentError.Error.Code;
        }
        await JsonSerializer.SerializeAsync(http.Response.Body, reply.Body, reply.BodyType, AgentJson.Options, http.RequestAborted);
    }

    private bool ShouldDrop(AgentRoute route)
    {
        lock (_gate)
        {
            return _dropResponse?.Invoke(route) ?? false;
        }
    }

    private async Task<FakeCall> PrepareAsync(HttpContext http, AgentRoute route)
    {
        var call = new FakeCall(http, route);
        foreach (string name in route.PathParameters)
        {
            call.Path[name] = http.Request.RouteValues[name]?.ToString() ?? "";
        }

        if (route.RequiresProtocolHeader)
        {
            string header = http.Request.Headers[MirbadLLM.Agent.Contracts.Protocol.ProtocolHeader].ToString();
            if (!int.TryParse(header, NumberStyles.None, CultureInfo.InvariantCulture, out int version) ||
                version < _options.Protocol.Min || version > _options.Protocol.Current)
            {
                throw Fail(ErrorCodes.ProtocolMismatch,
                    $"Protocol version '{header}' is not supported; this agent speaks {_options.Protocol.Min}–{_options.Protocol.Current}.");
            }
        }

        if (route.Feature is { } feature && !route.IsOfferedBy(_options.Features))
        {
            throw Fail(ErrorCodes.NotSupported, $"This node does not offer {feature}.");
        }

        if (route.IsMutation)
        {
            if (!MasterTriple.TryParse(http.Request.Headers[MirbadLLM.Agent.Contracts.Protocol.MasterHeader].ToString(), out var master))
            {
                throw Fail(ErrorCodes.InvalidRequest, $"A mutation needs a valid {MirbadLLM.Agent.Contracts.Protocol.MasterHeader} header.");
            }

            call.Master = master;
            string time = http.Request.Headers[MirbadLLM.Agent.Contracts.Protocol.RequestTimeHeader].ToString();
            if (!DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var requestTime))
            {
                throw Fail(ErrorCodes.InvalidRequest, $"A mutation needs a {MirbadLLM.Agent.Contracts.Protocol.RequestTimeHeader} header.");
            }

            if ((requestTime - Now).Duration() > MirbadLLM.Agent.Contracts.Protocol.MaxRequestSkew)
            {
                throw Fail(ErrorCodes.ClockSkew, "The request time differs from the agent clock by more than 5 minutes.");
            }
        }

        if (route.Input == AgentRouteInput.Body)
        {
            string text;
            using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8))
            {
                text = await reader.ReadToEndAsync(http.RequestAborted);
            }

            try
            {
                using var doc = JsonDocument.Parse(text);
                call.Raw = doc.RootElement.Clone();
                call.Body = call.Raw.Deserialize(route.RequestType, AgentJson.Options);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
            {
                throw Fail(ErrorCodes.InvalidRequest, "The body is not a valid " + route.RequestType.Name + ": " + ex.Message);
            }

            if (call.Body is null)
            {
                throw Fail(ErrorCodes.InvalidRequest, "A body is required.");
            }
        }
        else if (route.Input == AgentRouteInput.Query)
        {
            call.Body = ParseQuery(route.RequestType, http.Request.Query);
        }

        if (route.IsMutation)
        {
            if (call.Body is not AgentMutation mutation || string.IsNullOrWhiteSpace(mutation.OperationId))
            {
                throw Fail(ErrorCodes.InvalidRequest, "The body needs an operationId.");
            }

            call.Kind = route.OperationKind!;
            call.Target = TargetOf(call);
            if (string.IsNullOrWhiteSpace(call.Target))
            {
                throw Fail(ErrorCodes.InvalidRequest, "The operation has no target.");
            }

            (call.Redacted, call.Digest) = Canonicalize(call.Raw);
            lock (_gate)
            {
                FenceLocked(call.Master!);
            }
        }

        call.Prepared = true;
        return call;
    }

    /// <summary>
    /// Storage and host-service handlers on the real agent accept durably first and record a refusal their executor finds
    /// (folder exists, in use, denied, hostd's own refusals, ...) as Failed before the first stage; a same-id retry replays
    /// it (W2 review MN-092, D41). Launch, stop, cancel and PKI refusals record nothing. Host-service body validation
    /// (<c>invalid_request</c>) runs before acceptance on the real agent too.
    /// </summary>
    private void RecordRefusal(FakeCall? call, AgentException ex)
    {
        if (call is not { Prepared: true, Route.IsMutation: true } || !RecordsRefusals(call.Kind) ||
            ex.Code is ErrorCodes.OperationIdConflict or ErrorCodes.OperationExpired or ErrorCodes.ClockSkew or ErrorCodes.StateUnavailable ||
            (ex.Code == ErrorCodes.InvalidRequest && !IsStorageKind(call.Kind)))
        {
            return;
        }

        lock (_gate)
        {
            if (_ops.ContainsKey(call.Mutation.OperationId))
            {
                return;
            }

            var op = AcceptLocked(call);
            op.Refusal = true;
            CompleteLocked(op, OperationState.Failed, ex.Error);
        }
    }

    private static bool IsStorageKind(string kind) =>
        kind is OperationKinds.ImagePull or OperationKinds.Download or OperationKinds.DownloadCancel or OperationKinds.FolderDelete;

    private static bool IsHostKind(string kind) => OperationKinds.DesiredState.Contains(kind) || OperationKinds.Unverifiable.Contains(kind);

    private static bool RecordsRefusals(string kind) => IsStorageKind(kind) || IsHostKind(kind);

    private static string TargetOf(FakeCall call) => call.Route.OperationKind switch
    {
        OperationKinds.Launch or OperationKinds.Stop => call.Path["modelId"],
        OperationKinds.ImagePull => ((ImagePullRequest)call.Body!).Image,
        OperationKinds.FolderDelete or OperationKinds.DownloadCancel => call.Path["folder"],
        OperationKinds.Download => ((DownloadStartRequest)call.Body!).Folder,
        OperationKinds.OperationCancel => call.Path["operationId"],
        OperationKinds.WhitelistAdd => ((HostWhitelistAddRequest)call.Body!).Unit,
        OperationKinds.BackupMountSet or OperationKinds.BackupMountRemove => "backup-mount",
        OperationKinds.PkiRotationStart => ((PkiRotationStartRequest)call.Body!).RotationId,
        OperationKinds.PkiRotationCert or OperationKinds.PkiRotationCommit or OperationKinds.PkiRotationAbort => call.Path["rotationId"],
        OperationKinds.PkiTrustSet => "trust",
        _ => call.Path.TryGetValue("name", out string? name) ? name : "",
    };

    /// <summary>
    /// The redacted payload and its digest (design §4.4 item 1): the shared canonical form the real agent and the master
    /// use (<see cref="OperationPayload"/>, W2 review D38).
    /// </summary>
    private static (JsonElement Redacted, string Digest) Canonicalize(JsonElement raw) => OperationPayload.RedactAndDigest(raw);

    private static object ParseQuery(Type type, IQueryCollection query)
    {
        string? Get(string key) => query.TryGetValue(key, out var values) ? values.ToString() : null;
        long? Long(string key) => Get(key) is { Length: > 0 } s
            ? long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v) ? v : throw Fail(ErrorCodes.InvalidRequest, $"'{key}' is not a number.")
            : null;
        int? Int(string key) => Long(key) is { } v ? (v is >= int.MinValue and <= int.MaxValue ? (int)v : throw Fail(ErrorCodes.InvalidRequest, $"'{key}' is out of range.")) : null;
        bool? Bool(string key) => Get(key) is { Length: > 0 } s
            ? bool.TryParse(s, out bool v) ? v : throw Fail(ErrorCodes.InvalidRequest, $"'{key}' is not true or false.")
            : null;

        return type switch
        {
            _ when type == typeof(EventsQuery) => new EventsQuery { BootId = Get("bootId"), AfterSeq = Long("afterSeq") },
            _ when type == typeof(TelemetryQuery) => new TelemetryQuery { BootId = Get("bootId"), AfterSeq = Long("afterSeq"), Max = Int("max") },
            _ when type == typeof(GpuIdleQuery) => new GpuIdleQuery
            {
                Seconds = Int("seconds") ?? 5,
                Gpus = Get("gpus") is { Length: > 0 } gpus
                    ? [.. gpus.Split(',').Select(g => int.TryParse(g, NumberStyles.None, CultureInfo.InvariantCulture, out int i) ? i : throw Fail(ErrorCodes.InvalidRequest, "'gpus' must list indices."))]
                    : null,
            },
            _ when type == typeof(RuntimesQuery) => new RuntimesQuery { Label = Get("label") },
            _ when type == typeof(LogsQuery) => new LogsQuery { Tail = Int("tail") ?? 200, Follow = Bool("follow") ?? true },
            _ when type == typeof(OperationsQuery) => Get("state") is { } state && state != OperationsQuery.ActiveState
                ? throw Fail(ErrorCodes.InvalidRequest, $"Unknown state filter '{state}'.")
                : new OperationsQuery { State = Get("state") },
            _ when type == typeof(HostServicesQuery) => new HostServicesQuery { Include = Get("include") },
            _ when type == typeof(GgufInspectQuery) => new GgufInspectQuery { File = Get("file") },
            _ => throw new InvalidOperationException("No query binding for " + type.Name),
        };
    }

    private Dictionary<string, Func<FakeCall, Task<FakeReply>>> CreateHandlers()
    {
        var handlers = new Dictionary<string, Func<FakeCall, Task<FakeReply>>>(StringComparer.Ordinal)
        {
            [AgentRoutes.Info.Name] = c => Sync(Info()),
            [AgentRoutes.Capabilities.Name] = c => Sync(Capabilities()),
            [AgentRoutes.Status.Name] = c => Sync(Status()),
            [AgentRoutes.Events.Name] = EventsAsync,
            [AgentRoutes.Telemetry.Name] = c => Sync(Telemetry((TelemetryQuery)c.Body!)),
            [AgentRoutes.GpuIdle.Name] = GpuIdleAsync,
            [AgentRoutes.RuntimesList.Name] = c => Sync(RuntimesList((RuntimesQuery)c.Body!)),
            [AgentRoutes.RuntimeGet.Name] = c => Sync(RuntimeGet(c.Path["modelId"])),
            [AgentRoutes.RuntimeLaunch.Name] = c => Sync(Launch(c)),
            [AgentRoutes.RuntimeStop.Name] = c => Sync(StopRuntime(c)),
            [AgentRoutes.RuntimeLaunchConfig.Name] = c => Sync(LaunchConfigOf(c.Path["modelId"])),
            [AgentRoutes.RuntimeLogs.Name] = LogsAsync,
            [AgentRoutes.Proxy.Name] = ProxyAsync,
            [AgentRoutes.ImagesList.Name] = c => Sync(ImagesList()),
            [AgentRoutes.ImagePull.Name] = c => Sync(ImagePull(c)),
            [AgentRoutes.ModelFoldersList.Name] = c => Sync(FoldersList()),
            [AgentRoutes.ModelFolderInspect.Name] = c => Sync(FolderInspect(c.Path["folder"])),
            [AgentRoutes.ModelFolderDelete.Name] = c => Sync(FolderDelete(c)),
            [AgentRoutes.GgufInspect.Name] = c => Sync(GgufInspect(c.Path["folder"], (GgufInspectQuery)c.Body!)),
            [AgentRoutes.DownloadStart.Name] = c => Sync(DownloadStart(c)),
            [AgentRoutes.DownloadsList.Name] = c => Sync(DownloadsList()),
            [AgentRoutes.DownloadGet.Name] = c => Sync(DownloadGet(c.Path["folder"])),
            [AgentRoutes.DownloadCancel.Name] = c => Sync(DownloadCancel(c)),
            [AgentRoutes.OperationGet.Name] = c => Sync(OperationGet(c.Path["operationId"])),
            [AgentRoutes.OperationsList.Name] = c => Sync(OperationsList((OperationsQuery)c.Body!)),
            [AgentRoutes.OperationCancel.Name] = c => Sync(OperationCancel(c)),
            [AgentRoutes.HostServicesList.Name] = c => Sync(HostServicesList((HostServicesQuery)c.Body!)),
            [AgentRoutes.HostServiceStart.Name] = c => Sync(UnitAction(c, HostServiceAction.Start)),
            [AgentRoutes.HostServiceStop.Name] = c => Sync(UnitAction(c, HostServiceAction.Stop)),
            [AgentRoutes.HostServiceRestart.Name] = c => Sync(UnitAction(c, HostServiceAction.Restart)),
            [AgentRoutes.HostServiceAutostart.Name] = c => Sync(UnitAutostart(c)),
            [AgentRoutes.HostWhitelistAdd.Name] = c => Sync(WhitelistAdd(c)),
            [AgentRoutes.HostWhitelistUpdate.Name] = c => Sync(WhitelistUpdate(c)),
            [AgentRoutes.HostWhitelistRemove.Name] = c => Sync(WhitelistRemove(c)),
            [AgentRoutes.HostGpuProcesses.Name] = c => Sync(GpuProcesses()),
            [AgentRoutes.HostContainerGet.Name] = c => Sync(ContainerGet(c.Path["name"])),
            [AgentRoutes.HostContainerStart.Name] = c => Sync(ContainerAction(c, HostServiceAction.Start)),
            [AgentRoutes.HostContainerStop.Name] = c => Sync(ContainerAction(c, HostServiceAction.Stop)),
            [AgentRoutes.HostContainerRestart.Name] = c => Sync(ContainerAction(c, HostServiceAction.Restart)),
            [AgentRoutes.HostContainerAutostart.Name] = c => Sync(ContainerAutostart(c)),
            [AgentRoutes.BackupMountGet.Name] = c => Sync(BackupMountGet()),
            [AgentRoutes.BackupMountSet.Name] = c => Sync(BackupMountSet(c)),
            [AgentRoutes.BackupMountRemove.Name] = c => Sync(BackupMountRemove(c)),
            [AgentRoutes.BackupMountTest.Name] = c => Sync(BackupMountTest((BackupMountTestRequest)c.Body!)),
            [AgentRoutes.PkiRotationStart.Name] = c => Sync(RotationStart(c)),
            [AgentRoutes.PkiRotationCert.Name] = c => Sync(RotationCert(c)),
            [AgentRoutes.PkiRotationCommit.Name] = c => Sync(RotationCommit(c)),
            [AgentRoutes.PkiRotationAbort.Name] = c => Sync(RotationAbort(c)),
            [AgentRoutes.PkiRotationGet.Name] = c => Sync(RotationGet(c.Path["rotationId"])),
            [AgentRoutes.PkiTrustGet.Name] = c => Sync(TrustGet()),
            [AgentRoutes.PkiTrustSet.Name] = c => Sync(TrustSet(c)),
            // The fake reports no agent.update (no update helper), so the feature check answers before this.
            [AgentRoutes.AgentUpdate.Name] = _ => throw Fail(ErrorCodes.NotSupported, "The fake agent has no update helper."),
        };

        var missing = AgentRoutes.All.Where(r => !handlers.ContainsKey(r.Name)).Select(r => r.Name).ToList();
        return missing.Count == 0 ? handlers : throw new InvalidOperationException("FakeAgent misses routes: " + string.Join(", ", missing));
    }

    private static Task<FakeReply> Sync(FakeReply reply) => Task.FromResult(reply);

    // ---- Shared helpers ----

    private DateTimeOffset Now => _time.GetUtcNow();

    private static string NewBootId() => "boot-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>An <see cref="AgentException"/> with the HTTP status the design gives <paramref name="code"/>.</summary>
    internal static AgentException Fail(string code, string message, object? details = null) =>
        new(StatusOf(code), new AgentError { Code = code, Message = message, Details = details is null ? null : AgentJson.ToElement(details) });

    /// <summary>HTTP status of each agent error code (design §4.3, §4.4, §7.2).</summary>
    internal static int StatusOf(string code) => ErrorMappings.AgentStatus(code);

    private static AgentError Error(string code, string message, object? details = null) =>
        new() { Code = code, Message = message, Details = details is null ? null : AgentJson.ToElement(details) };

    private void CrashLocked()
    {
        _crashed = true;
        _boot.Cts.Cancel();
        AbortSubscribersLocked();
    }

    private sealed class FakeCall(HttpContext http, AgentRoute route)
    {
        public HttpContext Http { get; } = http;

        public AgentRoute Route { get; } = route;

        public Dictionary<string, string> Path { get; } = new(StringComparer.Ordinal);

        public object? Body { get; set; }

        public JsonElement Raw { get; set; }

        public MasterTriple? Master { get; set; }

        public string Kind { get; set; } = "";

        public string Target { get; set; } = "";

        public JsonElement Redacted { get; set; }

        public string Digest { get; set; } = "";

        /// <summary>True once the envelope, master header and fencing passed (the handler runs next).</summary>
        public bool Prepared { get; set; }

        public AgentMutation Mutation => (AgentMutation)Body!;

        public T Request<T>()
            where T : class => (T)Body!;
    }

    private readonly record struct FakeReply(int Status, object? Body, Type BodyType, bool Streamed = false)
    {
        public static FakeReply Json<T>(T body, int status = 200) => new(status, body, typeof(T));

        public static FakeReply Error(int status, AgentError error) => new(status, new AgentErrorResponse(error), typeof(AgentErrorResponse));

        public static FakeReply StreamDone { get; } = new(200, null, typeof(object), Streamed: true);
    }

    private sealed class Boot(string id, DateTimeOffset startedAt)
    {
        public string Id { get; } = id;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public CancellationTokenSource Cts { get; } = new();
    }

    /// <summary>A hold knob: <see cref="WaitAsync"/> completes immediately unless held.</summary>
    private sealed class FakeGate
    {
        private readonly object _lock = new();
        private TaskCompletionSource _open = NewOpen();

        public void Set(bool held)
        {
            lock (_lock)
            {
                if (held && _open.Task.IsCompleted)
                {
                    _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                else if (!held)
                {
                    _open.TrySetResult();
                }
            }
        }

        public Task WaitAsync(CancellationToken ct)
        {
            lock (_lock)
            {
                return _open.Task.WaitAsync(ct);
            }
        }

        private static TaskCompletionSource NewOpen()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs.SetResult();
            return tcs;
        }
    }
}
