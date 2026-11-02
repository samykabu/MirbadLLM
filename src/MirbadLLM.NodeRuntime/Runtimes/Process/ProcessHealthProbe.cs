using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MirbadLLM.NodeRuntime.Runtimes.Process;

/// <summary>What one readiness probe of a llama-server instance found.</summary>
public enum ProcessProbeResult
{
    /// <summary>Nothing answers on the port (yet), or the probe timed out.</summary>
    Unreachable,

    /// <summary><c>/health</c> answers 503 (llama-server is loading the model).</summary>
    Loading,

    /// <summary>
    /// <c>/health</c> is 200 but <c>/v1/models</c> refuses the per-instance key (401/403): another process holds the port.
    /// </summary>
    WrongKey,

    /// <summary><c>/health</c> is 200 and <c>/v1/models</c> answers, but does not list the expected alias.</summary>
    AliasMissing,

    /// <summary><c>/health</c> is 200 and <c>/v1/models</c>, asked with the per-instance key, lists the alias.</summary>
    Ready,
}

/// <summary>
/// The Ready check of a supervised llama-server (design-p3p4 §A.1.3 "Health and readiness"; MN-207, D121 / spike S1 C5):
/// <c>GET /health</c> without a key (the only open endpoint) must be 200, then <c>GET /v1/models</c> <b>with</b>
/// <c>Authorization: Bearer {per-instance key}</c> must list the alias (<c>data[].id</c> or <c>data[].aliases</c>). The
/// second check catches a foreign process that grabbed the port: it would not know the key.
/// </summary>
public sealed class ProcessHealthProbe
{
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    /// <summary>Creates the probe.</summary>
    /// <param name="http">Client for loopback requests (no base address, no default auth).</param>
    /// <param name="timeout">Timeout of each request.</param>
    public ProcessHealthProbe(HttpClient http, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _timeout = timeout;
    }

    /// <summary>Probes the instance at <paramref name="baseAddress"/>.</summary>
    public async Task<ProcessProbeResult> ProbeAsync(Uri baseAddress, string alias, string apiKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrEmpty(alias);
        ArgumentException.ThrowIfNullOrEmpty(apiKey);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            using (var health = await _http.GetAsync(new Uri(baseAddress, "health"), cts.Token).ConfigureAwait(false))
            {
                if (health.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    return ProcessProbeResult.Loading;
                }

                if (!health.IsSuccessStatusCode)
                {
                    return ProcessProbeResult.Unreachable;
                }
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseAddress, "v1/models"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var models = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (models.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return ProcessProbeResult.WrongKey;
            }

            if (models.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return ProcessProbeResult.Loading;
            }

            if (!models.IsSuccessStatusCode)
            {
                return ProcessProbeResult.Unreachable;
            }

            await using var body = await models.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, default, cts.Token).ConfigureAwait(false);
            return ListsAlias(document.RootElement, alias) ? ProcessProbeResult.Ready : ProcessProbeResult.AliasMissing;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                throw;
            }

            return ProcessProbeResult.Unreachable;
        }
    }

    /// <summary>The base address of an instance on <paramref name="host"/>:<paramref name="port"/>.</summary>
    public static Uri BaseAddress(string host, int port) =>
        new($"http://{host}:{port.ToString(CultureInfo.InvariantCulture)}/");

    /// <summary>True when the OpenAI model list names <paramref name="alias"/> exactly.</summary>
    internal static bool ListsAlias(JsonElement root, string alias)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var model in data.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (model.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                string.Equals(id.GetString(), alias, StringComparison.Ordinal))
            {
                return true;
            }

            if (model.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array &&
                aliases.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.String && string.Equals(a.GetString(), alias, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}
