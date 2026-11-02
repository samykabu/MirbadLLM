using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MirbadLLM.NodeRuntime;

/// <summary>What the Services page needs from <c>GET /containers/{name}/json</c>.</summary>
public sealed record ContainerDetails(
    string Id,
    string Name,
    string Status,
    int ExitCode,
    bool OomKilled,
    string? Health,
    DateTimeOffset? StartedAt,
    string RestartPolicy,
    IReadOnlyDictionary<string, string> Labels,
    string Image = "");

/// <summary>One row of <c>GET /containers/json?all=true</c>.</summary>
public sealed record ServiceContainerSummary(
    string Id,
    string Name,
    string State,
    string Status,
    string Image,
    IReadOnlyDictionary<string, string> Labels);

/// <summary>
/// Docker Engine calls for the Services page (inspect, stats, start/stop/restart, restart-policy update) on
/// the same socket as <see cref="DockerEngineClient"/>, kept separate so model orchestration stays untouched.
/// </summary>
public sealed class DockerServiceClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Null when no container has that name.</summary>
    public async Task<ContainerDetails?> InspectAsync(string name, CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync($"containers/{Uri.EscapeDataString(name)}/json", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var dto = await response.Content.ReadFromJsonAsync<InspectDto>(JsonOptions, ct).ConfigureAwait(false);
        if (dto is null)
        {
            return null;
        }

        DateTimeOffset? startedAt = DateTimeOffset.TryParse(dto.State?.StartedAt, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var started) && started.Year > 1
            ? started
            : null;

        return new ContainerDetails(
            dto.Id ?? "",
            (dto.Name ?? name).TrimStart('/'),
            dto.State?.Status ?? "",
            dto.State?.ExitCode ?? 0,
            dto.State?.OOMKilled ?? false,
            dto.State?.Health?.Status,
            startedAt,
            dto.HostConfig?.RestartPolicy?.Name ?? "",
            LegacyLabels.Normalize(dto.Config?.Labels),
            dto.Config?.Image ?? "");
    }

    /// <summary>Every container, running or not (<c>GET /containers/json?all=true</c>).</summary>
    public async Task<IReadOnlyList<ServiceContainerSummary>> ListAsync(CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync("containers/json?all=true", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var rows = await response.Content.ReadFromJsonAsync<List<SummaryDto>>(JsonOptions, ct).ConfigureAwait(false) ?? [];
        return [.. rows.Select(r => new ServiceContainerSummary(
            r.Id ?? "",
            (r.Names?.FirstOrDefault() ?? r.Id ?? "").TrimStart('/'),
            r.State ?? "",
            r.Status ?? "",
            r.Image ?? "",
            LegacyLabels.Normalize(r.Labels)))];
    }

    /// <summary>
    /// Memory in use as <c>docker stats</c> reports it (usage minus reclaimable page cache: <c>inactive_file</c>
    /// on cgroup v2, <c>total_inactive_file</c> on v1). Null when Docker reports nothing (stopped container).
    /// </summary>
    public async Task<long?> GetMemoryUsageAsync(string id, CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync($"containers/{Uri.EscapeDataString(id)}/stats?stream=false&one-shot=true", ct)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var dto = await response.Content.ReadFromJsonAsync<StatsDto>(JsonOptions, ct).ConfigureAwait(false);
        return MemoryFromStats(dto?.MemoryStats);
    }

    internal static long? MemoryFromStats(MemoryStatsDto? stats)
    {
        if (stats?.Usage is not long usage || usage <= 0)
        {
            return null;
        }

        long cache = 0;
        if (stats.Stats is { } s)
        {
            cache = s.TryGetValue("inactive_file", out long v2) ? v2
                : s.TryGetValue("total_inactive_file", out long v1) ? v1
                : 0;
        }

        return cache < usage ? usage - cache : usage;
    }

    public Task StartAsync(string id, CancellationToken ct = default) =>
        PostAsync($"containers/{Uri.EscapeDataString(id)}/start", content: null, ct, HttpStatusCode.NotModified);

    public Task StopAsync(string id, int timeoutSeconds = 30, CancellationToken ct = default) =>
        PostAsync($"containers/{Uri.EscapeDataString(id)}/stop?t={timeoutSeconds}", content: null, ct, HttpStatusCode.NotModified);

    public Task RestartAsync(string id, int timeoutSeconds = 30, CancellationToken ct = default) =>
        PostAsync($"containers/{Uri.EscapeDataString(id)}/restart?t={timeoutSeconds}", content: null, ct);

    /// <summary><c>POST /containers/{id}/update</c> with only the restart policy (<c>no</c>, <c>always</c>, <c>unless-stopped</c>, <c>on-failure</c>).</summary>
    public async Task UpdateRestartPolicyAsync(string id, string policy, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(new UpdateBody { RestartPolicy = new RestartPolicyDto { Name = policy } }, options: JsonOptions);
        await PostAsync($"containers/{Uri.EscapeDataString(id)}/update", content, ct).ConfigureAwait(false);
    }

    private async Task PostAsync(string url, HttpContent? content, CancellationToken ct, params HttpStatusCode[] alsoOk)
    {
        using var response = await httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct, alsoOk).ConfigureAwait(false);
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

    // --- Wire DTOs ---

    private sealed class InspectDto
    {
        [JsonPropertyName("Id")]
        public string? Id { get; set; }

        [JsonPropertyName("Name")]
        public string? Name { get; set; }

        [JsonPropertyName("State")]
        public StateDto? State { get; set; }

        [JsonPropertyName("HostConfig")]
        public HostConfigDto? HostConfig { get; set; }

        [JsonPropertyName("Config")]
        public ConfigDto? Config { get; set; }
    }

    private sealed class SummaryDto
    {
        [JsonPropertyName("Id")]
        public string? Id { get; set; }

        [JsonPropertyName("Names")]
        public List<string>? Names { get; set; }

        [JsonPropertyName("Image")]
        public string? Image { get; set; }

        [JsonPropertyName("State")]
        public string? State { get; set; }

        [JsonPropertyName("Status")]
        public string? Status { get; set; }

        [JsonPropertyName("Labels")]
        public Dictionary<string, string>? Labels { get; set; }
    }

    private sealed class StateDto
    {
        [JsonPropertyName("Status")]
        public string? Status { get; set; }

        [JsonPropertyName("ExitCode")]
        public int ExitCode { get; set; }

        [JsonPropertyName("OOMKilled")]
        public bool OOMKilled { get; set; }

        [JsonPropertyName("StartedAt")]
        public string? StartedAt { get; set; }

        [JsonPropertyName("Health")]
        public HealthDto? Health { get; set; }
    }

    private sealed class HealthDto
    {
        [JsonPropertyName("Status")]
        public string? Status { get; set; }
    }

    private sealed class HostConfigDto
    {
        [JsonPropertyName("RestartPolicy")]
        public RestartPolicyDto? RestartPolicy { get; set; }
    }

    private sealed class ConfigDto
    {
        [JsonPropertyName("Image")]
        public string? Image { get; set; }

        [JsonPropertyName("Labels")]
        public Dictionary<string, string>? Labels { get; set; }
    }

    private sealed class RestartPolicyDto
    {
        [JsonPropertyName("Name")]
        public string? Name { get; set; }
    }

    private sealed class UpdateBody
    {
        [JsonPropertyName("RestartPolicy")]
        public required RestartPolicyDto RestartPolicy { get; set; }
    }

    private sealed class StatsDto
    {
        [JsonPropertyName("memory_stats")]
        public MemoryStatsDto? MemoryStats { get; set; }
    }

    internal sealed class MemoryStatsDto
    {
        [JsonPropertyName("usage")]
        public long? Usage { get; set; }

        [JsonPropertyName("stats")]
        public Dictionary<string, long>? Stats { get; set; }
    }
}
