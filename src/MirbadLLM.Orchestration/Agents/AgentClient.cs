using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Agents;

/// <summary>
/// The typed control client of one node for one handler generation (design §4.3, §4.5, §7.2), implementing every
/// <see cref="IAgentClient"/> method from <see cref="AgentRoutes"/>.
/// <para>Per request: HTTP/2 exact (set by <see cref="GenerationHandler"/>), <c>MirbadLLM-Protocol</c> (except
/// <c>/info</c>), and on mutations <c>MirbadLLM-Master</c> (the current triple) and <c>MirbadLLM-Request-Time</c>.</para>
/// <para>Retries (§4.5): only transport failures before any response, with the delays of the route's call class, and
/// with the <b>same</b> serialized body bytes (same id and envelope); an HTTP error response is never retried. Each
/// attempt is bounded by the class's total timeout.</para>
/// </summary>
internal sealed class AgentClient : IAgentClient
{
    private const int MaxErrorBodyBytes = 64 * 1024;

    private readonly AgentHandlerGeneration _generation;
    private readonly AgentClientContext _context;

    public AgentClient(AgentHandlerGeneration generation, AgentClientContext context, InFlightLimiter dataLimiter)
    {
        _generation = generation;
        _context = context;
        DataClient = new AgentDataClient(generation, context, dataLimiter);
    }

    public string NodeId => _generation.Endpoint.NodeId;

    public IAgentDataClient Data => DataClient;

    internal AgentDataClient DataClient { get; }

    internal AgentHandlerGeneration Generation => _generation;

    // ---- Identity, health, telemetry ----

    public Task<InfoResponse> GetInfoAsync(CancellationToken ct = default) =>
        CallAsync<InfoResponse>(AgentRoutes.Info, AgentRoutes.Info.Path, null, null, ct);

    public Task<NodeCapabilities> GetCapabilitiesAsync(CancellationToken ct = default) =>
        CallAsync<NodeCapabilities>(AgentRoutes.Capabilities, AgentRoutes.Capabilities.Path, null, null, ct);

    public Task<StatusSnapshot> GetStatusAsync(CancellationToken ct = default) =>
        CallAsync<StatusSnapshot>(AgentRoutes.Status, AgentRoutes.Status.Path, null, null, ct);

    public IAsyncEnumerable<AgentEvent> SubscribeEventsAsync(EventsQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ReadEventsAsync(query, ct);
    }

    public Task<TelemetryPage> GetTelemetryAsync(TelemetryQuery query, CancellationToken ct = default) =>
        CallAsync<TelemetryPage>(AgentRoutes.Telemetry, AgentRoutes.Telemetry.Path, query, null, ct);

    public Task<GpuIdleResponse> SampleGpuIdleAsync(GpuIdleQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        TimeSpan total = TimeSpan.FromSeconds(Math.Max(0, query.Seconds)) + AgentCallPolicies.For(AgentCallClass.GpuIdle).Total!.Value;
        return CallAsync<GpuIdleResponse>(AgentRoutes.GpuIdle, AgentRoutes.GpuIdle.Path, query, null, ct, total);
    }

    // ---- Runtimes ----

    public Task<IReadOnlyList<RuntimeStatus>> ListRuntimesAsync(RuntimesQuery query, CancellationToken ct = default) =>
        CallAsync<IReadOnlyList<RuntimeStatus>>(AgentRoutes.RuntimesList, AgentRoutes.RuntimesList.Path, query, null, ct);

    public Task<RuntimeStatus?> GetRuntimeAsync(string modelId, CancellationToken ct = default) =>
        CallOrNullAsync<RuntimeStatus>(AgentRoutes.RuntimeGet, AgentRoutes.RuntimeGet.Expand(modelId), IsNotFound, ct);

    public Task<OperationStatus> LaunchAsync(string modelId, LaunchRequest request, CancellationToken ct = default) =>
        CallAsync<OperationStatus>(AgentRoutes.RuntimeLaunch, AgentRoutes.RuntimeLaunch.Expand(modelId), null, Required(request), ct);

    public Task<OperationStatus> StopAsync(string modelId, StopRequest request, CancellationToken ct = default) =>
        CallAsync<OperationStatus>(AgentRoutes.RuntimeStop, AgentRoutes.RuntimeStop.Expand(modelId), null, Required(request), ct);

    public Task<LaunchConfig?> GetLaunchConfigAsync(string modelId, CancellationToken ct = default) =>
        CallOrNullAsync<LaunchConfig>(AgentRoutes.RuntimeLaunchConfig, AgentRoutes.RuntimeLaunchConfig.Expand(modelId), IsNotFound, ct);

    public IAsyncEnumerable<string> StreamLogsAsync(string modelId, LogsQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ReadLogsAsync(modelId, query, ct);
    }

    // ---- Images, folders, downloads ----

    public Task<IReadOnlyList<ImageInfo>> ListImagesAsync(CancellationToken ct = default) =>
        CallAsync<IReadOnlyList<ImageInfo>>(AgentRoutes.ImagesList, AgentRoutes.ImagesList.Path, null, null, ct);

    public Task<OperationStatus> PullImageAsync(ImagePullRequest request, CancellationToken ct = default) =>
        CallAsync<OperationStatus>(AgentRoutes.ImagePull, AgentRoutes.ImagePull.Path, null, Required(request), ct);

    public Task<IReadOnlyList<ModelFolderEntry>> ListModelFoldersAsync(CancellationToken ct = default) =>
        CallAsync<IReadOnlyList<ModelFolderEntry>>(AgentRoutes.ModelFoldersList, AgentRoutes.ModelFoldersList.Path, null, null, ct);

    public Task<ModelFolderInspection?> InspectModelFolderAsync(string folder, CancellationToken ct = default) =>
        CallOrNullAsync<ModelFolderInspection>(AgentRoutes.ModelFolderInspect, AgentRoutes.ModelFolderInspect.Expand(folder), IsNotFound, ct);

    public Task<OperationStatus> DeleteModelFolderAsync(string folder, FolderDeleteRequest request, CancellationToken ct = default) =>
        CallAsync<OperationStatus>(AgentRoutes.ModelFolderDelete, AgentRoutes.ModelFolderDelete.Expand(folder), null, Required(request), ct);

    // P3 (MN-202, D116): implemented here, not left to the default interface member, so W13's editor and admin-API tasks
    // (MN-226, MN-229) read GGUF summaries without depending on each other. Callers check AgentFeatures.GgufInspect first.
    public Task<GgufSummary> InspectGgufAsync(string folder, string? file, CancellationToken ct = default) =>
        CallAsync<GgufSummary>(AgentRoutes.GgufInspect, AgentRoutes.GgufInspect.Expand(folder), new GgufInspectQuery { File = file }, null, ct);

    public Task<NodeDownloadStatus> StartDownloadAsync(DownloadStartRequest request, CancellationToken ct = default) =>
        CallAsync<NodeDownloadStatus>(AgentRoutes.DownloadStart, AgentRoutes.DownloadStart.Path, null, Required(request), ct);

    public Task<IReadOnlyList<NodeDownloadStatus>> ListDownloadsAsync(CancellationToken ct = default) =>
        CallAsync<IReadOnlyList<NodeDownloadStatus>>(AgentRoutes.DownloadsList, AgentRoutes.DownloadsList.Path, null, null, ct);

    public Task<NodeDownloadStatus?> GetDownloadAsync(string folder, CancellationToken ct = default) =>
        CallOrNullAsync<NodeDownloadStatus>(AgentRoutes.DownloadGet, AgentRoutes.DownloadGet.Expand(folder), IsNotFound, ct);

    public Task<OperationStatus> CancelDownloadAsync(string folder, DownloadCancelRequest request, CancellationToken ct = default) =>
        CallAsync<OperationStatus>(AgentRoutes.DownloadCancel, AgentRoutes.DownloadCancel.Expand(folder), null, Required(request), ct);

    // ---- Operations ----

    public Task<OperationStatus?> GetOperationAsync(string operationId, CancellationToken ct = default) =>
        CallOrNullAsync<OperationStatus>(AgentRoutes.OperationGet, AgentRoutes.OperationGet.Expand(operationId), IsNotFound, ct);

    public Task<IReadOnlyList<OperationStatus>> ListOperationsAsync(OperationsQuery query, CancellationToken ct = default) =>
        CallAsync<IReadOnlyList<OperationStatus>>(AgentRoutes.OperationsList, AgentRoutes.OperationsList.Path, query, null, ct);

    public Task<OperationStatus> CancelOperationAsync(string operationId, OperationCancelRequest request, CancellationToken ct = default) =>
        CallAsync<OperationStatus>(AgentRoutes.OperationCancel, AgentRoutes.OperationCancel.Expand(operationId), null, Required(request), ct);

    // ---- Host services ----

    public Task<HostServicesResponse> ListHostServicesAsync(HostServicesQuery query, CancellationToken ct = default) =>
        CallAsync<HostServicesResponse>(AgentRoutes.HostServicesList, AgentRoutes.HostServicesList.Path, query, null, ct);

    public Task<OperationStatus> RunHostServiceAsync(string name, HostServiceAction action, HostServiceActionRequest request, CancellationToken ct = default)
    {
        AgentRoute route = action switch
        {
            HostServiceAction.Start => AgentRoutes.HostServiceStart,
            HostServiceAction.Stop => AgentRoutes.HostServiceStop,
            HostServiceAction.Restart => AgentRoutes.HostServiceRestart,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown host service action."),
        };
        return CallAsync<OperationStatus>(route, route.Expand(name), null, Required(request), ct);
    }

    public Task<OperationStatus> UpdateAgentAsync(AgentUpdateRequest request, CancellationToken ct = default) =>
        CallAsync<OperationStatus>(AgentRoutes.AgentUpdate, AgentRoutes.AgentUpdate.Path, null, Required(request), ct);

    public Task<OperationStatus> SetHostServiceAutostartAsync(string name, HostServiceAutostartRequest request, CancellationToken ct = default) =>
        CallAsync<OperationStatus>(AgentRoutes.HostServiceAutostart, AgentRoutes.HostServiceAutostart.Expand(name), null, Required(request), ct);

    public Task<OperationStatus> AddToWhitelistAsync(HostWhitelistAddRequest request, CancellationToken ct = default) =>
        CallAsync<OperationStatus>(AgentRoutes.HostWhitelistAdd, AgentRoutes.HostWhitelistAdd.Path, null, Required(request), ct);

    public Task<OperationStatus> UpdateWhitelistAsync(string name, HostWhitelistUpdateRequest request, CancellationToken ct = default) =>
        CallAsync<OperationStatus>(AgentRoutes.HostWhitelistUpdate, AgentRoutes.HostWhitelistUpdate.Expand(name), null, Required(request), ct);

    public Task<OperationStatus> RemoveFromWhitelistAsync(string name, HostWhitelistRemoveRequest request, CancellationToken ct = default) =>
        CallAsync<OperationStatus>(AgentRoutes.HostWhitelistRemove, AgentRoutes.HostWhitelistRemove.Expand(name), null, Required(request), ct);

    public Task<IReadOnlyList<GpuProcess>?> GetGpuProcessesAsync(CancellationToken ct = default) =>
        CallOrNullAsync<IReadOnlyList<GpuProcess>>(
            AgentRoutes.HostGpuProcesses,
            AgentRoutes.HostGpuProcesses.Path,
            e => IsNotFound(e) || e.Code is ErrorCodes.NotSupported or ErrorCodes.HostdUnavailable,
            ct);

    public Task<HostContainerInfo?> GetContainerAsync(string name, CancellationToken ct = default) =>
        CallOrNullAsync<HostContainerInfo>(AgentRoutes.HostContainerGet, AgentRoutes.HostContainerGet.Expand(name), IsNotFound, ct);

    public Task<OperationStatus> RunContainerAsync(string name, HostServiceAction action, HostContainerActionRequest request, CancellationToken ct = default)
    {
        AgentRoute route = action switch
        {
            HostServiceAction.Start => AgentRoutes.HostContainerStart,
            HostServiceAction.Stop => AgentRoutes.HostContainerStop,
            HostServiceAction.Restart => AgentRoutes.HostContainerRestart,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown container action."),
        };
        return CallAsync<OperationStatus>(route, route.Expand(name), null, Required(request), ct);
    }

    public Task<OperationStatus> SetContainerAutostartAsync(string name, HostContainerAutostartRequest request, CancellationToken ct = default) =>
        CallAsync<OperationStatus>(AgentRoutes.HostContainerAutostart, AgentRoutes.HostContainerAutostart.Expand(name), null, Required(request), ct);

    public Task<BackupMountInfo> GetBackupMountAsync(CancellationToken ct = default) =>
        CallAsync<BackupMountInfo>(AgentRoutes.BackupMountGet, AgentRoutes.BackupMountGet.Path, null, null, ct);

    public Task<OperationStatus> SetBackupMountAsync(BackupMountSetRequest request, CancellationToken ct = default) =>
        CallAsync<OperationStatus>(AgentRoutes.BackupMountSet, AgentRoutes.BackupMountSet.Path, null, Required(request), ct);

    public Task<OperationStatus> RemoveBackupMountAsync(BackupMountRemoveRequest request, CancellationToken ct = default) =>
        CallAsync<OperationStatus>(AgentRoutes.BackupMountRemove, AgentRoutes.BackupMountRemove.Path, null, Required(request), ct);

    public Task<BackupMountTestResult> TestBackupMountAsync(BackupMountTestRequest request, CancellationToken ct = default) =>
        CallAsync<BackupMountTestResult>(AgentRoutes.BackupMountTest, AgentRoutes.BackupMountTest.Path, null, Required(request), ct);

    // ---- PKI ----

    public Task<OperationStatus> StartRotationAsync(PkiRotationStartRequest request, CancellationToken ct = default) =>
        CallAsync<OperationStatus>(AgentRoutes.PkiRotationStart, AgentRoutes.PkiRotationStart.Path, null, Required(request), ct);

    public Task<OperationStatus> InstallRotationCertificateAsync(PkiRotationCertRequest request, CancellationToken ct = default) =>
        CallAsync<OperationStatus>(AgentRoutes.PkiRotationCert, AgentRoutes.PkiRotationCert.Expand(Required(request).RotationId), null, request, ct);

    public Task<OperationStatus> CommitRotationAsync(PkiRotationCommitRequest request, CancellationToken ct = default) =>
        CallAsync<OperationStatus>(AgentRoutes.PkiRotationCommit, AgentRoutes.PkiRotationCommit.Expand(Required(request).RotationId), null, request, ct);

    public Task<OperationStatus> AbortRotationAsync(PkiRotationAbortRequest request, CancellationToken ct = default) =>
        CallAsync<OperationStatus>(AgentRoutes.PkiRotationAbort, AgentRoutes.PkiRotationAbort.Expand(Required(request).RotationId), null, request, ct);

    public Task<PkiRotationStatus?> GetRotationAsync(string rotationId, CancellationToken ct = default) =>
        CallOrNullAsync<PkiRotationStatus>(AgentRoutes.PkiRotationGet, AgentRoutes.PkiRotationGet.Expand(rotationId), IsNotFound, ct);

    public Task<PkiTrustResponse> GetTrustAsync(CancellationToken ct = default) =>
        CallAsync<PkiTrustResponse>(AgentRoutes.PkiTrustGet, AgentRoutes.PkiTrustGet.Path, null, null, ct);

    public Task<OperationStatus> SetTrustAsync(PkiTrustSetRequest request, CancellationToken ct = default) =>
        CallAsync<OperationStatus>(AgentRoutes.PkiTrustSet, AgentRoutes.PkiTrustSet.Path, null, Required(request), ct);

    // ---- Core ----

    /// <summary>The serialized body of a request (once per call; every retry sends these exact bytes).</summary>
    internal static byte[] SerializeBody(AgentRoute route, object body) =>
        JsonSerializer.SerializeToUtf8Bytes(body, route.RequestType, AgentJson.Options);

    private static T Required<T>(T request)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        return request;
    }

    private static bool IsNotFound(AgentException exception) =>
        exception.StatusCode == (int)HttpStatusCode.NotFound && exception.Code == ErrorCodes.NotFound;

    private async Task<T> CallAsync<T>(AgentRoute route, string path, object? query, object? body, CancellationToken ct, TimeSpan? total = null)
    {
        AgentReply reply = await ExecuteAsync(route, path, query, body, total, ct).ConfigureAwait(false);
        if (!reply.IsSuccess)
        {
            throw CreateAgentException(reply.Status, reply.Body);
        }

        return Deserialize<T>(reply.Body, reply.Status);
    }

    private async Task<T?> CallOrNullAsync<T>(AgentRoute route, string path, Func<AgentException, bool> absent, CancellationToken ct)
        where T : class
    {
        AgentReply reply = await ExecuteAsync(route, path, null, null, null, ct).ConfigureAwait(false);
        if (!reply.IsSuccess)
        {
            AgentException error = CreateAgentException(reply.Status, reply.Body);
            if (absent(error))
            {
                return null;
            }

            throw error;
        }

        return Deserialize<T>(reply.Body, reply.Status);
    }

    /// <summary>
    /// Sends a request with the §4.5 policy of its route: per-attempt total timeout, retries only for transport failures
    /// before any response, the same body bytes on every attempt. Returns the first HTTP response (any status).
    /// </summary>
    internal async Task<AgentReply> ExecuteAsync(AgentRoute route, string path, object? query, object? body, TimeSpan? totalOverride, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(route);
        Uri uri = _generation.Endpoint.Resolve(path + AgentQueryString.Build(query, route.RequestType));
        byte[]? payload = body is null ? null : SerializeBody(route, body);
        AgentCallPolicy policy = AgentCallPolicies.For(route.CallClass);
        TimeSpan? total = totalOverride ?? policy.Total;

        for (int attempt = 0; ; attempt++)
        {
            AgentAttempt result = await SendOnceAsync(route, uri, payload, total, ct).ConfigureAwait(false);
            if (result.Failure is null)
            {
                return result.Reply!;
            }

            if (!result.Retryable || attempt >= policy.RetryDelays.Count)
            {
                throw result.Failure;
            }

            await Task.Delay(policy.RetryDelays[attempt], _context.Time, ct).ConfigureAwait(false);
        }
    }

    private async Task<AgentAttempt> SendOnceAsync(AgentRoute route, Uri uri, byte[]? payload, TimeSpan? total, CancellationToken ct)
    {
        using CancellationTokenSource? timeout = total is { } limit ? new CancellationTokenSource(limit, _context.Time) : null;
        using CancellationTokenSource? linked = timeout is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        CancellationToken token = linked?.Token ?? ct;
        using HttpRequestMessage request = CreateRequest(route, uri, payload);
        ValidationTracker tracker = _generation.ControlTracker;
        long rejections = tracker.Rejected;

        HttpResponseMessage response;
        try
        {
            response = await _generation.Control.SendAsync(request, token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // TLS/ALPN failures (certificate refused by the master, HTTP/2 not negotiated) are configuration, not
            // transient: not retried. Everything else before a response is (same body bytes, §4.5).
            return AgentAttempt.Failed(
                AgentErrorMapper.Classify(NodeId, ex, tracker, rejections),
                retryable: ex.HttpRequestError != HttpRequestError.SecureConnectionError);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return AgentAttempt.Failed(TimedOut(route, total, ex), retryable: true);
        }
        catch (ObjectDisposedException ex)
        {
            return AgentAttempt.Failed(Retired(ex), retryable: false);
        }

        using (response)
        {
            try
            {
                byte[] bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                return AgentAttempt.Succeeded(new AgentReply((int)response.StatusCode, bytes));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                // A response had started: never retried (the agent may have acted).
                return AgentAttempt.Failed(new AgentUnreachableException(NodeId, $"The response of the agent of node {NodeId} was cut: {ex.Message}", ex), retryable: false);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                return AgentAttempt.Failed(TimedOut(route, total, ex), retryable: false);
            }
        }
    }

    private HttpRequestMessage CreateRequest(AgentRoute route, Uri uri, byte[]? payload)
    {
        var request = new HttpRequestMessage(new HttpMethod(route.Method), uri);
        if (route.RequiresProtocolHeader)
        {
            request.Headers.TryAddWithoutValidation(Protocol.ProtocolHeader, _context.ProtocolVersion(NodeId).ToString(CultureInfo.InvariantCulture));
        }

        if (route.IsMutation)
        {
            request.Headers.TryAddWithoutValidation(Protocol.MasterHeader, _context.Identity.Current.ToHeaderValue());
            request.Headers.TryAddWithoutValidation(Protocol.RequestTimeHeader, _context.Time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        }

        if (payload is not null)
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue(AgentJson.JsonContentType);
            request.Content = content;
        }

        return request;
    }

    private async Task<HttpResponseMessage> OpenStreamAsync(AgentRoute route, string path, object? query, CancellationToken ct)
    {
        Uri uri = _generation.Endpoint.Resolve(path + AgentQueryString.Build(query, route.RequestType));
        using HttpRequestMessage request = CreateRequest(route, uri, null);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(AgentJson.EventStreamContentType));
        ValidationTracker tracker = _generation.ControlTracker;
        long rejections = tracker.Rejected;
        HttpResponseMessage response;
        try
        {
            response = await _generation.Control.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw AgentErrorMapper.Classify(NodeId, ex, tracker, rejections);
        }
        catch (ObjectDisposedException ex)
        {
            throw Retired(ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            byte[] body;
            try
            {
                body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                body = [];
            }

            throw CreateAgentException((int)response.StatusCode, body);
        }
    }

    private async IAsyncEnumerable<AgentEvent> ReadEventsAsync(EventsQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        using HttpResponseMessage response = await OpenStreamAsync(AgentRoutes.Events, AgentRoutes.Events.Path, query, ct).ConfigureAwait(false);
        Stream stream = await ReadStreamAsync(response, ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new SseReader(stream);
            while (true)
            {
                SseMessage? message = await ReadMessageAsync(reader, ct).ConfigureAwait(false);
                if (message is null)
                {
                    yield break;
                }

                if (ParseEvent(message) is { } agentEvent)
                {
                    yield return agentEvent;
                }
            }
        }
    }

    private async IAsyncEnumerable<string> ReadLogsAsync(string modelId, LogsQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        using HttpResponseMessage response = await OpenStreamAsync(AgentRoutes.RuntimeLogs, AgentRoutes.RuntimeLogs.Expand(modelId), query, ct).ConfigureAwait(false);
        Stream stream = await ReadStreamAsync(response, ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new SseReader(stream);
            while (true)
            {
                SseMessage? message = await ReadMessageAsync(reader, ct).ConfigureAwait(false);
                if (message is null)
                {
                    yield break;
                }

                foreach (string line in message.DataLines)
                {
                    yield return line;
                }
            }
        }
    }

    private async Task<Stream> ReadStreamAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new AgentUnreachableException(NodeId, $"The stream from the agent of node {NodeId} failed: {ex.Message}", ex);
        }
    }

    private async Task<SseMessage?> ReadMessageAsync(SseReader reader, CancellationToken ct)
    {
        try
        {
            return await reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new AgentUnreachableException(NodeId, $"The stream from the agent of node {NodeId} failed: {ex.Message}", ex);
        }
    }

    /// <summary>An event, or null for an unknown event type (additive types are ignored, design §3.6) or an empty message.</summary>
    private static AgentEvent? ParseEvent(SseMessage message)
    {
        string data = message.Data;
        if (string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AgentEvent>(data, AgentJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private T Deserialize<T>(byte[] body, int status)
    {
        try
        {
            T? value = JsonSerializer.Deserialize<T>(body, AgentJson.Options);
            if (value is not null)
            {
                return value;
            }
        }
        catch (JsonException ex)
        {
            throw new AgentException(status, new AgentError
            {
                Code = ErrorCodes.InternalError,
                Message = $"The agent of node {NodeId} answered HTTP {status} with an unreadable body: {ex.Message}",
            }, NodeId, ex);
        }

        throw new AgentException(status, new AgentError
        {
            Code = ErrorCodes.InternalError,
            Message = $"The agent of node {NodeId} answered HTTP {status} with an empty body.",
        }, NodeId);
    }

    /// <summary>The <see cref="AgentException"/> of a non-2xx answer (agent error shape, or a generic one when the body is not it).</summary>
    internal AgentException CreateAgentException(int status, byte[] body)
    {
        AgentError? error = null;
        if (body.Length is > 0 and <= MaxErrorBodyBytes)
        {
            try
            {
                error = JsonSerializer.Deserialize<AgentErrorResponse>(body, AgentJson.Options)?.Error;
            }
            catch (JsonException)
            {
                error = null;
            }
        }

        if (error is null || string.IsNullOrEmpty(error.Code))
        {
            error = new AgentError
            {
                Code = ErrorCodes.InternalError,
                Message = string.Create(CultureInfo.InvariantCulture, $"The agent of node {NodeId} answered HTTP {status} without an agent error body."),
            };
        }

        return new AgentException(status, error, NodeId);
    }

    private AgentUnreachableException TimedOut(AgentRoute route, TimeSpan? total, Exception inner) =>
        new(NodeId, string.Create(CultureInfo.InvariantCulture, $"The agent of node {NodeId} did not answer {route.Name} within {total?.TotalSeconds ?? 0:0.#} s."), inner);

    private AgentUnreachableException Retired(Exception inner) =>
        new(NodeId, $"The agent client of node {NodeId} belongs to retired handlers; get a new client from the factory.", inner);
}

/// <summary>An HTTP answer of the agent: status and body bytes.</summary>
internal sealed record AgentReply(int Status, byte[] Body)
{
    public bool IsSuccess => Status is >= 200 and <= 299;
}

/// <summary>Outcome of one attempt.</summary>
internal readonly record struct AgentAttempt(AgentReply? Reply, AgentUnreachableException? Failure, bool Retryable)
{
    public static AgentAttempt Succeeded(AgentReply reply) => new(reply, null, false);

    public static AgentAttempt Failed(AgentUnreachableException failure, bool retryable) => new(null, failure, retryable);
}
