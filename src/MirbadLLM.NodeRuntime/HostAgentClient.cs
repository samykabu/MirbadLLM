using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using MirbadLLM.Core;

namespace MirbadLLM.NodeRuntime;

/// <summary>One unit as reported by mirbadllm-hostd (<c>GET /services</c>).</summary>
public sealed record HostAgentUnit(
    string Name,
    string Unit,
    string Scope,
    string? User,
    string DisplayName,
    string Description,
    bool Protected,
    string? Risk,
    string ActiveState,
    string SubState,
    string UnitFileState,
    int? MainPid,
    long? ActiveSinceUnix,
    long? MemoryBytes,
    long? GpuMemoryBytes,
    string? Error);

/// <summary>One unit from <c>GET /services?include=all</c> (<c>discovered</c>).</summary>
public sealed record HostAgentDiscoveredUnit(
    string Name,
    string Unit,
    string Scope,
    string? User,
    string Description,
    string ActiveState,
    string SubState,
    string UnitFileState,
    long? ActiveSinceUnix,
    long? MemoryBytes,
    long? GpuMemoryBytes,
    bool Managed,
    bool Denied,
    string? DeniedBy);

/// <summary><c>GET /services?include=all</c>.</summary>
public sealed record HostAgentDiscovery(
    IReadOnlyList<HostAgentUnit> Services,
    IReadOnlyList<HostAgentDiscoveredUnit> Discovered,
    IReadOnlyList<string> DiscoveryErrors,
    IReadOnlyList<string> DenyPatterns);

/// <summary>The host agent's socket is missing or does not answer.</summary>
public sealed class HostAgentUnavailableException : HostServiceException
{
    public HostAgentUnavailableException()
    {
    }

    public HostAgentUnavailableException(string message)
        : base(message)
    {
    }

    public HostAgentUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Client for mirbadllm-hostd over its Unix socket (<c>/run/mirbadllm-hostd/hostd.sock</c>, bind-mounted
/// into the controller container). The agent owns the allowlist; this client only forwards names.
/// </summary>
public sealed class HostAgentClient(HttpClient httpClient, Func<bool>? isInstalled = null) : IHostAgentBackupMountClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>A handler whose every connection goes to <paramref name="socketPath"/>.</summary>
    public static SocketsHttpHandler CreateHandler(string socketPath) => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public async Task<IReadOnlyList<HostAgentUnit>> ListAsync(CancellationToken ct = default)
    {
        EnsureInstalled();
        try
        {
            using var response = await httpClient.GetAsync("services", ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            var body = await response.Content.ReadFromJsonAsync<ServicesBody>(JsonOptions, ct).ConfigureAwait(false);
            return body?.Services ?? [];
        }
        catch (Exception ex) when (IsTransportFailure(ex, ct))
        {
            throw Unavailable(ex);
        }
    }

    /// <summary><c>GET /services?include=all</c>: managed units plus every discovered unit (managed/denied flags).</summary>
    public async Task<HostAgentDiscovery> DiscoverAsync(CancellationToken ct = default)
    {
        EnsureInstalled();
        try
        {
            using var response = await httpClient.GetAsync("services?include=all", ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            var body = await response.Content.ReadFromJsonAsync<DiscoveryBody>(JsonOptions, ct).ConfigureAwait(false);
            return new HostAgentDiscovery(body?.Services ?? [], body?.Discovered ?? [], body?.DiscoveryErrors ?? [], body?.DenyPatterns ?? []);
        }
        catch (Exception ex) when (IsTransportFailure(ex, ct))
        {
            throw Unavailable(ex);
        }
    }

    /// <summary><c>POST /whitelist</c>. The agent re-checks the deny list (403) and the typed name.</summary>
    public async Task<HostAgentUnit> AddToWhitelistAsync(string unit, string scope, string? displayName, bool isProtected, string? confirm, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(new { unit, scope, displayName, @protected = isProtected, confirm }, options: JsonOptions);
        return await SendAsync(HttpMethod.Post, "whitelist", content, ct).ConfigureAwait(false);
    }

    /// <summary><c>PATCH /whitelist/{name}</c>.</summary>
    public async Task<HostAgentUnit> UpdateWhitelistAsync(string name, string? displayName, bool? isProtected, string? confirm, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(new { displayName, @protected = isProtected, confirm }, options: JsonOptions);
        return await SendAsync(HttpMethod.Patch, $"whitelist/{Uri.EscapeDataString(name)}", content, ct).ConfigureAwait(false);
    }

    /// <summary><c>DELETE /whitelist/{name}</c>.</summary>
    public async Task RemoveFromWhitelistAsync(string name, CancellationToken ct = default)
    {
        EnsureInstalled();
        try
        {
            using var response = await httpClient.DeleteAsync($"whitelist/{Uri.EscapeDataString(name)}", ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransportFailure(ex, ct))
        {
            throw Unavailable(ex);
        }
    }

    /// <summary><c>POST /services/{name}/{start|stop|restart}?confirm=</c>.</summary>
    public async Task<HostAgentUnit> RunAsync(string name, HostServiceAction action, string? confirm, CancellationToken ct = default)
    {
        string verb = action switch
        {
            HostServiceAction.Start => "start",
            HostServiceAction.Stop => "stop",
            HostServiceAction.Restart => "restart",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        return await PostAsync($"services/{Uri.EscapeDataString(name)}/{verb}{ConfirmQuery(confirm)}", content: null, ct).ConfigureAwait(false);
    }

    /// <summary><c>POST /services/{name}/autostart?confirm=</c> with <c>{"enabled": …}</c>.</summary>
    public async Task<HostAgentUnit> SetAutostartAsync(string name, bool enabled, string? confirm, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(new { enabled }, options: JsonOptions);
        return await PostAsync($"services/{Uri.EscapeDataString(name)}/autostart{ConfirmQuery(confirm)}", content, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /gpu-processes</c>: GPU compute processes with their owning container or unit. Null — never an exception —
    /// when the agent isn't installed, doesn't answer, predates the endpoint (404) or can't run nvidia-smi (503), so
    /// monitoring falls back to budgets. The agent is installed by hand and can lag the controller.
    /// </summary>
    public async Task<IReadOnlyList<GpuProcess>?> GetGpuProcessesAsync(CancellationToken ct = default)
    {
        if (isInstalled is not null && !isInstalled())
        {
            return null;
        }

        try
        {
            using var response = await httpClient.GetAsync("gpu-processes", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<List<GpuProcess>>(JsonOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransportFailure(ex, ct) || ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary><c>GET /backup-mount</c>.</summary>
    public async Task<HostAgentBackupMount> GetBackupMountAsync(CancellationToken ct = default) =>
        await SendForAsync<HostAgentBackupMount>(HttpMethod.Get, "backup-mount", null, ct).ConfigureAwait(false);

    /// <summary><c>POST /backup-mount/test</c>: the agent mounts the share privately, writes a test file and unmounts.</summary>
    public async Task<HostAgentBackupMountTest> TestBackupMountAsync(HostAgentBackupMountRequest request, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(request, options: JsonOptions);
        return await SendForAsync<HostAgentBackupMountTest>(HttpMethod.Post, "backup-mount/test", content, ct).ConfigureAwait(false);
    }

    /// <summary><c>PUT /backup-mount</c>: test, then mount the share at the backup directory on the host.</summary>
    public async Task<HostAgentBackupMount> SetBackupMountAsync(HostAgentBackupMountRequest request, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(request, options: JsonOptions);
        return await SendForAsync<HostAgentBackupMount>(HttpMethod.Put, "backup-mount", content, ct).ConfigureAwait(false);
    }

    /// <summary><c>DELETE /backup-mount</c>: unmount and go back to the local disk.</summary>
    public async Task<HostAgentBackupMount> RemoveBackupMountAsync(CancellationToken ct = default) =>
        await SendForAsync<HostAgentBackupMount>(HttpMethod.Delete, "backup-mount", null, ct).ConfigureAwait(false);

    private async Task<T> SendForAsync<T>(HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        EnsureInstalled();
        try
        {
            using var request = new HttpRequestMessage(method, url) { Content = content };
            using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false)
                ?? throw new HostServiceException("The host agent returned an empty response.");
        }
        catch (Exception ex) when (IsTransportFailure(ex, ct))
        {
            throw Unavailable(ex);
        }
    }

    private Task<HostAgentUnit> PostAsync(string url, HttpContent? content, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, url, content, ct);

    private async Task<HostAgentUnit> SendAsync(HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        EnsureInstalled();
        try
        {
            using var request = new HttpRequestMessage(method, url) { Content = content };
            using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<HostAgentUnit>(JsonOptions, ct).ConfigureAwait(false)
                ?? throw new HostServiceException("The host agent returned an empty response.");
        }
        catch (Exception ex) when (IsTransportFailure(ex, ct))
        {
            throw Unavailable(ex);
        }
    }

    private void EnsureInstalled()
    {
        if (isInstalled is not null && !isInstalled())
        {
            throw new HostAgentUnavailableException("The host agent is not installed (no socket).");
        }
    }

    private static string ConfirmQuery(string? confirm) =>
        string.IsNullOrEmpty(confirm) ? "" : $"?confirm={Uri.EscapeDataString(confirm)}";

    private static bool IsTransportFailure(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or SocketException or IOException
        || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    private static HostAgentUnavailableException Unavailable(Exception ex) =>
        new($"The host agent did not answer: {ex.Message}", ex);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var (message, code) = await ReadErrorAsync(response, ct).ConfigureAwait(false);
        throw (response.StatusCode, code) switch
        {
            (HttpStatusCode.Forbidden, "denied") => new HostServiceDeniedException(message),
            (HttpStatusCode.Forbidden, _) => new HostServiceConfirmationRequiredException(message),
            (HttpStatusCode.NotFound, _) => new HostServiceNotFoundException(message),
            (HttpStatusCode.Conflict, _) => new HostServiceConflictException(message),
            _ => new HostServiceException(message),
        };
    }

    private static async Task<(string Message, string? Code)> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                string? code = doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                return (error.GetString() ?? text, code);
            }
        }
        catch (JsonException)
        {
            // Not JSON; use the raw text.
        }

        return (text.Length == 0 ? $"Host agent error {(int)response.StatusCode}." : text, null);
    }

    private sealed class ServicesBody
    {
        public List<HostAgentUnit>? Services { get; set; }
    }

    private sealed class DiscoveryBody
    {
        public List<HostAgentUnit>? Services { get; set; }

        public List<HostAgentDiscoveredUnit>? Discovered { get; set; }

        public List<string>? DiscoveryErrors { get; set; }

        public List<string>? DenyPatterns { get; set; }
    }
}
