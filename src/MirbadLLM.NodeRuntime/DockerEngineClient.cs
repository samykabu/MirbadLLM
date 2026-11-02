using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MirbadLLM.NodeRuntime;

/// <summary>One managed container's view, as reported by the Docker Engine <c>/containers/json</c> list.</summary>
public sealed record ContainerSummary(
    string Id,
    string Name,
    string State,
    string Status,
    IReadOnlyDictionary<string, string> Labels,
    DateTimeOffset? CreatedAt = null);

/// <summary>The image and command a container was created with (from <c>GET /containers/{id}/json</c>).</summary>
/// <param name="Image"><c>Config.Image</c>: the reference the container was created from.</param>
/// <param name="Cmd"><c>Config.Cmd</c>.</param>
/// <param name="Env">
/// <c>Config.Env</c> (<c>NAME=value</c>; Docker merges the image's own variables in). May hold secret values: callers
/// report names only for secrets.
/// </param>
/// <param name="Binds"><c>HostConfig.Binds</c>.</param>
public sealed record ContainerLaunchConfig(string Image, IReadOnlyList<string> Cmd, IReadOnlyList<string> Env, IReadOnlyList<string> Binds)
{
    /// <summary>Shows the image, command and bind count only: <see cref="Env"/> may hold secret values.</summary>
    public override string ToString() => $"ContainerLaunchConfig {{ Image = {Image}, Cmd = [{string.Join(' ', Cmd)}], Env = {Env.Count} entries, Binds = {Binds.Count} }}";
}

/// <summary>How often Docker has restarted a container and how its last run ended.</summary>
public sealed record ContainerRuntimeState(int RestartCount, int ExitCode, bool OomKilled);

/// <summary>
/// Everything <see cref="DockerEngineClient.CreateJobContainerAsync"/> needs to create a short-lived helper
/// container (e.g. a Hugging Face download) that does not publish a port and does not request a GPU.
/// </summary>
/// <param name="User">
/// Docker <c>User</c> (<c>uid[:gid]</c> or a name in the image); null = the image's default user (root for the vLLM
/// image), and the field is left out of the create body.
/// </param>
public sealed record CreateJobContainerRequest(
    string Name,
    string Image,
    IReadOnlyList<string> Entrypoint,
    IReadOnlyList<string> Cmd,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<string> Binds,
    IReadOnlyList<string> Env,
    string Network,
    string NetworkAlias,
    string? User = null);

/// <summary>Everything <see cref="DockerEngineClient.CreateContainerAsync"/> needs to create one model container.</summary>
/// <param name="Name">Container name.</param>
/// <param name="Image">Image reference to create from (a tag, or <c>repository@digest</c>).</param>
/// <param name="Cmd">Command.</param>
/// <param name="Labels">Labels.</param>
/// <param name="Binds">Bind mounts.</param>
/// <param name="ContainerPort">Port inside the container.</param>
/// <param name="HostPort">Published host port.</param>
/// <param name="HostIp">Host address the port is published on.</param>
/// <param name="Network">Network to join.</param>
/// <param name="NetworkAlias">Alias on <paramref name="Network"/>.</param>
/// <param name="IpcMode">IPC mode.</param>
/// <param name="Env">
/// Environment as <c>NAME=value</c> (MN-107). Null or empty = no <c>Env</c> field in the create body, exactly the P1 body.
/// May hold secret values, so the record's <c>ToString</c> shows only how many there are.
/// </param>
/// <param name="GpuDeviceIds">
/// GPUs by UUID (MN-106): the NVIDIA device request names exactly these (<c>DeviceIDs</c>, <c>Count</c> 0). Null = every
/// GPU (<c>Count</c> -1), exactly the P1 body.
/// </param>
public sealed record CreateContainerRequest(
    string Name,
    string Image,
    IReadOnlyList<string> Cmd,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<string> Binds,
    int ContainerPort,
    int HostPort,
    string HostIp,
    string Network,
    string NetworkAlias,
    string IpcMode,
    IReadOnlyList<string>? Env = null,
    IReadOnlyList<string>? GpuDeviceIds = null)
{
    /// <summary>Everything but the environment values, which may be secrets.</summary>
    public override string ToString() =>
        $"CreateContainerRequest {{ Name = {Name}, Image = {Image}, Env = {Env?.Count ?? 0} entries }}";
}

/// <summary>
/// Thin HTTP client over the Docker Engine API (<c>/v1.47</c>), reached over a Unix socket, a Windows
/// named pipe or plain TCP depending on <see cref="Core.MirbadLLMOptions.DockerEndpoint"/>.
/// </summary>
public sealed class DockerEngineClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Builds a <see cref="SocketsHttpHandler"/> whose transport is chosen by the endpoint's scheme.</summary>
    public static SocketsHttpHandler CreateHandler(string dockerEndpoint) => new()
    {
        ConnectCallback = async (context, ct) =>
        {
            if (dockerEndpoint.StartsWith("unix://", StringComparison.Ordinal))
            {
                string path = dockerEndpoint["unix://".Length..];
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }

            if (dockerEndpoint.StartsWith("npipe://", StringComparison.Ordinal))
            {
                string rest = dockerEndpoint["npipe://".Length..];
                const string PipeMarker = "/pipe/";
                int markerIndex = rest.IndexOf(PipeMarker, StringComparison.Ordinal);
                string server = markerIndex > 0 ? rest[..markerIndex] : ".";
                string pipeName = markerIndex >= 0 ? rest[(markerIndex + PipeMarker.Length)..] : rest;
                var pipe = new NamedPipeClientStream(server, pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5_000, ct).ConfigureAwait(false);
                return pipe;
            }

            if (dockerEndpoint.StartsWith("tcp://", StringComparison.Ordinal))
            {
                var uri = new Uri("http://" + dockerEndpoint["tcp://".Length..]);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(uri.Host, uri.Port, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }

            throw new InvalidOperationException($"Unsupported Docker endpoint scheme: '{dockerEndpoint}'.");
        },
    };

    /// <summary>Convenience for standalone/manual use; DI registers the client with a factory-built <see cref="HttpClient"/> instead.</summary>
    public static HttpClient CreateHttpClient(string dockerEndpoint) => new(CreateHandler(dockerEndpoint))
    {
        BaseAddress = new Uri("http://docker/v1.47/"),
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>
    /// Containers matching every label filter. A filter on a <c>mirbadllm.*</c> label also finds containers that carry
    /// its pre-rename <c>spark-serve.*</c> twin (<see cref="LegacyLabels"/>), and labels come back under both names.
    /// </summary>
    public async Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(
        IEnumerable<string> labelFilters, bool all = true, CancellationToken ct = default)
    {
        string[] filters = [.. labelFilters];
        var found = await ListByLabelsAsync(filters, all, ct).ConfigureAwait(false);
        string[] legacy = [.. filters.Select(f => LegacyLabels.ToLegacyFilter(f) ?? f)];
        if (!legacy.SequenceEqual(filters, StringComparer.Ordinal))
        {
            var ids = found.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            found.AddRange((await ListByLabelsAsync(legacy, all, ct).ConfigureAwait(false)).Where(c => ids.Add(c.Id)));
        }

        return found;
    }

    private async Task<List<ContainerSummary>> ListByLabelsAsync(string[] labelFilters, bool all, CancellationToken ct)
    {
        var filters = new Dictionary<string, string[]> { ["label"] = labelFilters };
        string filtersJson = JsonSerializer.Serialize(filters);
        string url = $"containers/json?all={(all ? "true" : "false")}&filters={Uri.EscapeDataString(filtersJson)}";

        using var response = await httpClient.GetAsync(url, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        var items = await response.Content.ReadFromJsonAsync<List<ContainerSummaryDto>>(JsonOptions, ct).ConfigureAwait(false)
            ?? [];
        return items.Select(i => new ContainerSummary(
            i.Id ?? "",
            i.Names?.FirstOrDefault()?.TrimStart('/') ?? "",
            i.State ?? "",
            i.Status ?? "",
            LegacyLabels.Normalize(i.Labels),
            i.Created > 0 ? DateTimeOffset.FromUnixTimeSeconds(i.Created) : null)).ToList();
    }

    public async Task<string> CreateContainerAsync(CreateContainerRequest request, CancellationToken ct = default)
    {
        var body = new CreateContainerBody
        {
            Image = request.Image,
            Cmd = request.Cmd,
            Env = request.Env is { Count: > 0 } env ? env : null,
            Labels = request.Labels,
            ExposedPorts = new Dictionary<string, EmptyBody> { [$"{request.ContainerPort}/tcp"] = new() },
            HostConfig = new HostConfigBody
            {
                Binds = request.Binds,
                PortBindings = new Dictionary<string, List<PortBindingBody>>
                {
                    [$"{request.ContainerPort}/tcp"] =
                    [
                        new PortBindingBody { HostIp = request.HostIp, HostPort = request.HostPort.ToString(CultureInfo.InvariantCulture) },
                    ],
                },
                // "no": mirbadllm owns starts. With unless-stopped, Docker relaunched every model at once when the DGX
                // rebooted (2026-10-14), ahead of the controller's one-at-a-time boot auto-start, and the smallest model
                // found no memory left for its KV cache. A model that should come back after a reboot sets AutoStart.
                RestartPolicy = new RestartPolicyBody { Name = "no" },
                IpcMode = request.IpcMode,
                DeviceRequests =
                [
                    request.GpuDeviceIds is { Count: > 0 } devices
                        ? new DeviceRequestBody { Driver = "nvidia", Count = 0, DeviceIds = devices, Capabilities = [["gpu"]] }
                        : new DeviceRequestBody { Driver = "nvidia", Count = -1, Capabilities = [["gpu"]] },
                ],
            },
            NetworkingConfig = new NetworkingConfigBody
            {
                EndpointsConfig = new Dictionary<string, EndpointConfigBody>
                {
                    [request.Network] = new EndpointConfigBody { Aliases = [request.NetworkAlias] },
                },
            },
        };

        using var content = JsonContent.Create(body, options: JsonOptions);
        using var response = await httpClient.PostAsync($"containers/create?name={Uri.EscapeDataString(request.Name)}", content, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        var created = await response.Content.ReadFromJsonAsync<CreateContainerResponse>(JsonOptions, ct).ConfigureAwait(false);
        return created?.Id ?? throw new InvalidOperationException("Docker did not return a container Id.");
    }

    /// <summary>Creates a helper container (no published port, no GPU device request) such as a Hugging Face download job.</summary>
    public async Task<string> CreateJobContainerAsync(CreateJobContainerRequest request, CancellationToken ct = default)
    {
        var body = new CreateJobContainerBody
        {
            Image = request.Image,
            Entrypoint = request.Entrypoint,
            Cmd = request.Cmd,
            Env = request.Env,
            Labels = request.Labels,
            User = request.User,
            HostConfig = new JobHostConfigBody { Binds = request.Binds },
            NetworkingConfig = new NetworkingConfigBody
            {
                EndpointsConfig = new Dictionary<string, EndpointConfigBody>
                {
                    [request.Network] = new EndpointConfigBody { Aliases = [request.NetworkAlias] },
                },
            },
        };

        using var content = JsonContent.Create(body, options: JsonOptions);
        using var response = await httpClient.PostAsync($"containers/create?name={Uri.EscapeDataString(request.Name)}", content, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        var created = await response.Content.ReadFromJsonAsync<CreateContainerResponse>(JsonOptions, ct).ConfigureAwait(false);
        return created?.Id ?? throw new InvalidOperationException("Docker did not return a container Id.");
    }

    /// <summary>Repository:tag strings of every locally present image (skips untagged <c>&lt;none&gt;:&lt;none&gt;</c> entries).</summary>
    public async Task<IReadOnlyList<string>> ListImageRepoTagsAsync(CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync("images/json", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        var items = await response.Content.ReadFromJsonAsync<List<ImageSummaryDto>>(JsonOptions, ct).ConfigureAwait(false) ?? [];
        return items
            .SelectMany(i => i.RepoTags ?? [])
            .Where(tag => tag != "<none>:<none>")
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Restart count and last exit details from <c>GET /containers/{id}/json</c>.</summary>
    public async Task<ContainerRuntimeState> InspectStateAsync(string containerId, CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync($"containers/{containerId}/json", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var dto = await response.Content.ReadFromJsonAsync<ContainerInspectDto>(JsonOptions, ct).ConfigureAwait(false);
        return new ContainerRuntimeState(
            dto?.RestartCount ?? 0,
            dto?.State?.ExitCode ?? 0,
            dto?.State?.OOMKilled ?? false);
    }

    /// <summary>Image, command, environment and binds from <c>GET /containers/{id}/json</c>.</summary>
    public async Task<ContainerLaunchConfig> InspectLaunchConfigAsync(string containerId, CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync($"containers/{containerId}/json", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var dto = await response.Content.ReadFromJsonAsync<ContainerInspectDto>(JsonOptions, ct).ConfigureAwait(false);
        return new ContainerLaunchConfig(
            dto?.Config?.Image ?? "",
            dto?.Config?.Cmd ?? [],
            dto?.Config?.Env ?? [],
            dto?.HostConfig?.Binds ?? []);
    }

    public async Task StartContainerAsync(string containerId, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsync($"containers/{containerId}/start", content: null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct, HttpStatusCode.NotModified).ConfigureAwait(false);
    }

    public async Task StopContainerAsync(string containerId, int timeoutSeconds = 30, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsync($"containers/{containerId}/stop?t={timeoutSeconds}", content: null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct, HttpStatusCode.NotModified).ConfigureAwait(false);
    }

    public async Task RemoveContainerAsync(string containerId, CancellationToken ct = default)
    {
        using var response = await httpClient.DeleteAsync($"containers/{containerId}", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<bool> ImageExistsAsync(string image, CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync($"images/{image}/json", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Pulls <paramref name="image"/>: <c>repository[:tag]</c> (no tag = <c>latest</c>) or <c>repository@sha256:…</c>,
    /// which Docker pulls by digest (the digest goes in the <c>tag</c> parameter; MN-107).
    /// </summary>
    public async Task PullImageAsync(string image, CancellationToken ct = default)
    {
        string fromImage;
        string tag;
        int at = image.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            string withoutDigest = image[..at];
            int slash = withoutDigest.LastIndexOf('/');
            int tagColon = withoutDigest.LastIndexOf(':');
            fromImage = tagColon > slash ? withoutDigest[..tagColon] : withoutDigest;
            tag = image[(at + 1)..];
        }
        else
        {
            int lastSlash = image.LastIndexOf('/');
            int colon = image.LastIndexOf(':');
            fromImage = colon > lastSlash ? image[..colon] : image;
            tag = colon > lastSlash ? image[(colon + 1)..] : "latest";
        }

        string url = $"images/create?fromImage={Uri.EscapeDataString(fromImage)}&tag={Uri.EscapeDataString(tag)}";
        using var response = await httpClient.PostAsync(url, content: null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        // Drain the newline-delimited progress stream fully before returning; surface a reported error.
        string progress = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (progress.Contains("\"error\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Docker image pull for '{image}' reported an error: {progress}");
        }
    }

    public async Task EnsureNetworkAsync(string network, CancellationToken ct = default)
    {
        using var inspect = await httpClient.GetAsync($"networks/{Uri.EscapeDataString(network)}", ct).ConfigureAwait(false);
        if (inspect.StatusCode == HttpStatusCode.OK)
        {
            return;
        }

        using var content = JsonContent.Create(new NetworkCreateBody { Name = network }, options: JsonOptions);
        using var response = await httpClient.PostAsync("networks/create", content, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Reads and demultiplexes the Docker logs stream (8-byte frame headers when the container has no TTY).</summary>
    public async IAsyncEnumerable<string> GetLogsAsync(
        string containerId, int tail = 200, bool follow = false, [EnumeratorCancellation] CancellationToken ct = default)
    {
        string url = $"containers/{containerId}/logs?stdout=1&stderr=1&tail={tail}&follow={(follow ? "true" : "false")}";
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await foreach (var line in DemuxLogFramesAsync(stream, ct).ConfigureAwait(false))
        {
            yield return line;
        }
    }

    private static async IAsyncEnumerable<string> DemuxLogFramesAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        var header = new byte[8];
        var leftover = new StringBuilder();

        while (true)
        {
            if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
            {
                break;
            }

            int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4, 4));
            var payload = length > 0 ? new byte[length] : [];
            if (length > 0 && !await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
            {
                break;
            }

            leftover.Append(Encoding.UTF8.GetString(payload));
            string buffered = leftover.ToString();
            string[] lines = buffered.Split('\n');
            for (int i = 0; i < lines.Length - 1; i++)
            {
                yield return lines[i];
            }

            leftover.Clear();
            leftover.Append(lines[^1]);
        }

        if (leftover.Length > 0)
        {
            yield return leftover.ToString();
        }
    }

    /// <summary>Fills <paramref name="buffer"/> completely; returns false if the stream ended before any byte was read.</summary>
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return offset > 0 ? throw new EndOfStreamException("Docker log stream ended mid-frame.") : false;
            }

            offset += read;
        }

        return true;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct, params HttpStatusCode[] alsoOk)
    {
        if (response.IsSuccessStatusCode || alsoOk.Contains(response.StatusCode))
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new DockerApiException(response.StatusCode, body);
    }

    // --- Wire DTOs (Docker Engine API JSON shapes; kept internal and minimal) ---

    private sealed class ContainerInspectDto
    {
        [JsonPropertyName("RestartCount")]
        public int RestartCount { get; set; }

        [JsonPropertyName("State")]
        public ContainerInspectStateDto? State { get; set; }

        [JsonPropertyName("Config")]
        public ContainerInspectConfigDto? Config { get; set; }

        [JsonPropertyName("HostConfig")]
        public ContainerInspectHostConfigDto? HostConfig { get; set; }
    }

    private sealed class ContainerInspectConfigDto
    {
        [JsonPropertyName("Image")]
        public string? Image { get; set; }

        [JsonPropertyName("Cmd")]
        public List<string>? Cmd { get; set; }

        [JsonPropertyName("Env")]
        public List<string>? Env { get; set; }
    }

    private sealed class ContainerInspectHostConfigDto
    {
        [JsonPropertyName("Binds")]
        public List<string>? Binds { get; set; }
    }

    private sealed class ContainerInspectStateDto
    {
        [JsonPropertyName("ExitCode")]
        public int ExitCode { get; set; }

        [JsonPropertyName("OOMKilled")]
        public bool OOMKilled { get; set; }
    }

    private sealed class ContainerSummaryDto
    {
        [JsonPropertyName("Created")]
        public long Created { get; set; }

        [JsonPropertyName("Id")]
        public string? Id { get; set; }

        [JsonPropertyName("Names")]
        public List<string>? Names { get; set; }

        [JsonPropertyName("State")]
        public string? State { get; set; }

        [JsonPropertyName("Status")]
        public string? Status { get; set; }

        [JsonPropertyName("Labels")]
        public Dictionary<string, string>? Labels { get; set; }
    }

    private sealed class CreateContainerBody
    {
        [JsonPropertyName("Image")]
        public required string Image { get; set; }

        [JsonPropertyName("Cmd")]
        public required IReadOnlyList<string> Cmd { get; set; }

        /// <summary>Omitted when null, so a launch without environment sends the P1 body byte for byte.</summary>
        [JsonPropertyName("Env")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<string>? Env { get; set; }

        [JsonPropertyName("Labels")]
        public required IReadOnlyDictionary<string, string> Labels { get; set; }

        [JsonPropertyName("ExposedPorts")]
        public required Dictionary<string, EmptyBody> ExposedPorts { get; set; }

        [JsonPropertyName("HostConfig")]
        public required HostConfigBody HostConfig { get; set; }

        [JsonPropertyName("NetworkingConfig")]
        public required NetworkingConfigBody NetworkingConfig { get; set; }
    }

    private sealed class EmptyBody;

    private sealed class HostConfigBody
    {
        [JsonPropertyName("Binds")]
        public required IReadOnlyList<string> Binds { get; set; }

        [JsonPropertyName("PortBindings")]
        public required Dictionary<string, List<PortBindingBody>> PortBindings { get; set; }

        [JsonPropertyName("RestartPolicy")]
        public required RestartPolicyBody RestartPolicy { get; set; }

        [JsonPropertyName("IpcMode")]
        public required string IpcMode { get; set; }

        [JsonPropertyName("DeviceRequests")]
        public required IReadOnlyList<DeviceRequestBody> DeviceRequests { get; set; }
    }

    private sealed class PortBindingBody
    {
        [JsonPropertyName("HostIp")]
        public required string HostIp { get; set; }

        [JsonPropertyName("HostPort")]
        public required string HostPort { get; set; }
    }

    private sealed class RestartPolicyBody
    {
        [JsonPropertyName("Name")]
        public required string Name { get; set; }
    }

    private sealed class DeviceRequestBody
    {
        [JsonPropertyName("Driver")]
        public required string Driver { get; set; }

        [JsonPropertyName("Count")]
        public required int Count { get; set; }

        [JsonPropertyName("Capabilities")]
        public required IReadOnlyList<IReadOnlyList<string>> Capabilities { get; set; }

        /// <summary>Selected GPUs by UUID; omitted when null (every GPU), so the P1 body is unchanged.</summary>
        [JsonPropertyName("DeviceIDs")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<string>? DeviceIds { get; set; }
    }

    private sealed class NetworkingConfigBody
    {
        [JsonPropertyName("EndpointsConfig")]
        public required Dictionary<string, EndpointConfigBody> EndpointsConfig { get; set; }
    }

    private sealed class EndpointConfigBody
    {
        [JsonPropertyName("Aliases")]
        public required IReadOnlyList<string> Aliases { get; set; }
    }

    private sealed class CreateContainerResponse
    {
        [JsonPropertyName("Id")]
        public string? Id { get; set; }
    }

    private sealed class NetworkCreateBody
    {
        [JsonPropertyName("Name")]
        public required string Name { get; set; }
    }

    private sealed class CreateJobContainerBody
    {
        [JsonPropertyName("Image")]
        public required string Image { get; set; }

        [JsonPropertyName("Entrypoint")]
        public required IReadOnlyList<string> Entrypoint { get; set; }

        [JsonPropertyName("Cmd")]
        public required IReadOnlyList<string> Cmd { get; set; }

        [JsonPropertyName("Env")]
        public required IReadOnlyList<string> Env { get; set; }

        [JsonPropertyName("Labels")]
        public required IReadOnlyDictionary<string, string> Labels { get; set; }

        /// <summary>Omitted when null: the image's default user, as before MN-126.</summary>
        [JsonPropertyName("User")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? User { get; set; }

        [JsonPropertyName("HostConfig")]
        public required JobHostConfigBody HostConfig { get; set; }

        [JsonPropertyName("NetworkingConfig")]
        public required NetworkingConfigBody NetworkingConfig { get; set; }
    }

    private sealed class JobHostConfigBody
    {
        [JsonPropertyName("Binds")]
        public required IReadOnlyList<string> Binds { get; set; }
    }

    private sealed class ImageSummaryDto
    {
        [JsonPropertyName("RepoTags")]
        public List<string>? RepoTags { get; set; }
    }
}

/// <summary>A non-2xx response from the Docker Engine API.</summary>
public sealed class DockerApiException(HttpStatusCode statusCode, string responseBody)
    : Exception($"Docker API error {(int)statusCode} {statusCode}: {responseBody}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string ResponseBody { get; } = responseBody;
}
