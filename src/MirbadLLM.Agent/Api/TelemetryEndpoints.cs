using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Telemetry;

namespace MirbadLLM.Agent.Api;

/// <summary>
/// <c>GET /telemetry</c> and <c>GET /gpu/idle</c> (design §4.3, §8.1). Map on the <see cref="Protocol.BasePath"/>
/// group; mTLS, the protocol header and feature checks belong to the host (MN-014). Query keys follow
/// <see cref="AgentRouteInput.Query"/> (camelCase, lists comma-separated); a malformed query (unparsable or repeated
/// key, out-of-range value) answers 400 <c>invalid_request</c> in the agent error shape.
/// </summary>
public static class TelemetryEndpoints
{
    /// <summary>Maps <see cref="AgentRoutes.Telemetry"/> and <see cref="AgentRoutes.GpuIdle"/>.</summary>
    public static IEndpointRouteBuilder MapTelemetryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(AgentRoutes.Telemetry.Path, GetTelemetry);
        endpoints.MapGet(AgentRoutes.GpuIdle.Path, GetGpuIdleAsync);
        return endpoints;
    }

    private static IResult GetTelemetry(HttpRequest request, [FromServices] ITelemetryRing ring, [FromServices] IOptions<TelemetryOptions> options)
    {
        var settings = options.Value;
        if (!TryQuery(request, "bootId", out string? bootId) ||
            !TryQuery(request, "afterSeq", out string? seqText) ||
            !TryQuery(request, "max", out string? maxText))
        {
            return Invalid("A query key is given more than once.");
        }

        long? afterSeq = null;
        if (seqText is not null)
        {
            if (!long.TryParse(seqText, NumberStyles.None, CultureInfo.InvariantCulture, out long seq))
            {
                return Invalid("afterSeq must be a non-negative integer.");
            }

            afterSeq = seq;
        }

        if (afterSeq is not null && bootId is null)
        {
            return Invalid("afterSeq needs the bootId it belongs to.");
        }

        int max = settings.DefaultPageSize;
        if (maxText is not null && (!int.TryParse(maxText, NumberStyles.None, CultureInfo.InvariantCulture, out max) || max < 1))
        {
            return Invalid("max must be a positive integer.");
        }

        var page = ring.Read(bootId, afterSeq, Math.Min(max, settings.MaxPageSize));
        return Results.Json(page, AgentJson.Options);
    }

    private static async Task<IResult> GetGpuIdleAsync(HttpRequest request, [FromServices] GpuIdleSampler sampler, [FromServices] IOptions<TelemetryOptions> options, CancellationToken ct)
    {
        int maxSeconds = options.Value.GpuIdleMaxSeconds;
        if (!TryQuery(request, "seconds", out string? secondsText) || !TryQuery(request, "gpus", out string? gpusText))
        {
            return Invalid("A query key is given more than once.");
        }

        int seconds = new GpuIdleQuery().Seconds;
        if (secondsText is not null &&
            (!int.TryParse(secondsText, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds < 1 || seconds > maxSeconds))
        {
            return Invalid(string.Create(CultureInfo.InvariantCulture, $"seconds must be an integer from 1 to {maxSeconds}."));
        }

        List<int>? gpus = null;
        if (gpusText is not null)
        {
            gpus = [];
            foreach (string part in gpusText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                {
                    return Invalid("gpus must be comma-separated GPU indices.");
                }

                gpus.Add(index);
            }
        }

        var response = await sampler.SampleAsync(seconds, gpus, ct).ConfigureAwait(false);
        return Results.Json(response, AgentJson.Options);
    }

    /// <summary>
    /// Reads a query key: <paramref name="value"/> is its trimmed value, or null when absent or empty. False when the key
    /// is repeated.
    /// </summary>
    private static bool TryQuery(HttpRequest request, string key, out string? value)
    {
        var values = request.Query[key];
        if (values.Count > 1)
        {
            value = null;
            return false;
        }

        string? text = values.Count == 1 ? values[0]?.Trim() : null;
        value = string.IsNullOrEmpty(text) ? null : text;
        return true;
    }

    private static IResult Invalid(string message) =>
        Results.Json(
            new AgentErrorResponse(new AgentError { Code = ErrorCodes.InvalidRequest, Message = message }),
            AgentJson.Options,
            statusCode: StatusCodes.Status400BadRequest);
}
