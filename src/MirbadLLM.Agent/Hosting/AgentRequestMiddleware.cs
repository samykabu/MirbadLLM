using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Hosting;

/// <summary>
/// The agent's request checks (design §3.6, §4.2, §4.3), run after routing and before every handler:
/// <list type="number">
/// <item>a client certificate must be on the connection (defence in depth; the TLS listener already required and
/// validated it), else 403 <c>forbidden</c>;</item>
/// <item>no endpoint → 404 <c>not_found</c> in the agent error shape;</item>
/// <item>every route except <c>/info</c> needs <c>MirbadLLM-Protocol</c> naming a version in
/// [<see cref="Protocol.MinVersion"/>, <see cref="Protocol.CurrentVersion"/>], else 400 <c>protocol_mismatch</c> (the
/// master renegotiates);</item>
/// <item>a route whose <see cref="AgentRoute.Feature"/> is not in the node's reported features (design §3.6,
/// <see cref="AgentCapabilityReporter.GetFeaturesAsync"/>) → 501 <c>not_supported</c>, before any mutation check;</item>
/// <item>mutating routes (<see cref="AgentRoute.IsMutation"/>; an unknown route with a non-safe method is treated as
/// one, fail closed) need <c>MirbadLLM-Request-Time</c> within <see cref="Protocol.MaxRequestSkew"/> of the agent clock
/// (else 400 <c>clock_skew</c>) and a parseable <c>MirbadLLM-Master</c> triple (else 400 <c>invalid_request</c>), which
/// <see cref="IFencingState.CheckAsync"/> must accept (else 409 <c>stale_master</c> / <c>competing_master</c>; 503
/// <c>state_unavailable</c> when it cannot be persisted);</item>
/// <item><see cref="AgentException"/> from a handler becomes its status and error; any other unhandled exception
/// 500 <c>internal_error</c> (details only in the agent log).</item>
/// </list>
/// The outcome is published as <see cref="AgentRequestFeature"/>.
/// </summary>
internal sealed class AgentRequestMiddleware(
    RequestDelegate next,
    IFencingState fencing,
    TimeProvider time,
    AgentHostOptions options,
    AgentRouteCatalog catalog,
    AgentCapabilityReporter capabilities,
    ILogger<AgentRequestMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        AcceptLegacyHeaders(context);
        try
        {
            AgentError? refusal = await CheckAsync(context).ConfigureAwait(false);
            if (refusal is not null)
            {
                await RefuseAsync(context, StatusOf(refusal.Code), refusal).ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        }
        catch (AgentException ex) when (!context.Response.HasStarted)
        {
            await RefuseAsync(context, ex.StatusCode, ex.Error).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
        {
            await RefuseAsync(context, ex.StatusCode, AgentErrorResults.Create(ErrorCodes.InvalidRequest, ex.Message)).ConfigureAwait(false);
        }
        catch (Exception ex) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            AgentHostLog.UnhandledError(logger, ex, context.Request.Method, context.Request.Path);
            await AgentErrorResults.WriteAsync(context, StatusCodes.Status500InternalServerError,
                AgentErrorResults.Create(ErrorCodes.InternalError, "The agent failed to process the request; see the agent log.")).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A pre-rename master sends <c>SparkServe-*</c> headers and reads <c>SparkServe-*</c> answers: map its request headers
    /// to the current names (a current header wins) and send every protocol response header under both names.
    /// </summary>
    internal static void AcceptLegacyHeaders(HttpContext context)
    {
        foreach (var (name, value) in context.Request.Headers.ToList())
        {
            if (Protocol.CurrentHeaderOf(name) is { } current && !context.Request.Headers.ContainsKey(current))
            {
                context.Request.Headers[current] = value;
            }
        }

        var response = context.Response;
        response.OnStarting(() =>
        {
            foreach (var (name, value) in response.Headers.ToList())
            {
                if (Protocol.LegacyHeaderOf(name) is { } legacy)
                {
                    response.Headers[legacy] = value;
                }
            }

            return Task.CompletedTask;
        });
    }

    private async Task<AgentError?> CheckAsync(HttpContext context)
    {
        if (options.RequireClientCertificateOnRequests && context.Connection.ClientCertificate is null)
        {
            return AgentErrorResults.Create(ErrorCodes.Forbidden, "A master client certificate is required.");
        }

        Endpoint? endpoint = context.GetEndpoint();
        if (endpoint is null)
        {
            return AgentErrorResults.Create(ErrorCodes.NotFound, "No such agent route.");
        }

        string method = context.Request.Method;
        AgentRoute? route = catalog.Find(endpoint, method);

        int? protocolVersion = null;
        if (route?.RequiresProtocolHeader ?? true)
        {
            string? header = context.Request.Headers[Protocol.ProtocolHeader];
            if (!int.TryParse(header, NumberStyles.None, CultureInfo.InvariantCulture, out int version) ||
                version < Protocol.MinVersion || version > Protocol.CurrentVersion)
            {
                return AgentErrorResults.Create(ErrorCodes.ProtocolMismatch, string.Create(CultureInfo.InvariantCulture,
                    $"This agent speaks protocol versions {Protocol.MinVersion}-{Protocol.CurrentVersion}; {Protocol.ProtocolHeader}: '{header}' is not one of them. Renegotiate through /info."),
                    new { supported = Protocol.Range });
            }

            protocolVersion = version;
        }

        if (route?.Feature is { } feature &&
            !route.IsOfferedBy(await capabilities.GetFeaturesAsync(context.RequestAborted).ConfigureAwait(false)))
        {
            return AgentErrorResults.Create(ErrorCodes.NotSupported, $"This node's agent does not offer '{feature}' ({route.Name}).");
        }

        bool mutation = route?.IsMutation ?? !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));
        if (!mutation)
        {
            context.Features.Set(new AgentRequestFeature { Route = route, ProtocolVersion = protocolVersion });
            return null;
        }

        string? timeHeader = context.Request.Headers[Protocol.RequestTimeHeader];
        if (!DateTimeOffset.TryParse(timeHeader, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset requestTime))
        {
            return AgentErrorResults.Create(ErrorCodes.InvalidRequest, $"A mutating request needs a {Protocol.RequestTimeHeader} header (ISO 8601).");
        }

        DateTimeOffset now = time.GetUtcNow();
        if ((now - requestTime).Duration() > Protocol.MaxRequestSkew)
        {
            return AgentErrorResults.Create(ErrorCodes.ClockSkew, string.Create(CultureInfo.InvariantCulture,
                $"{Protocol.RequestTimeHeader} {requestTime:O} differs from the agent clock {now:O} by more than {Protocol.MaxRequestSkew.TotalMinutes:0} minutes."),
                new { agentTime = now, requestTime });
        }

        if (!MasterTriple.TryParse(context.Request.Headers[Protocol.MasterHeader], out MasterTriple presented) ||
            presented.InstanceId.Any(char.IsWhiteSpace) || presented.BootId.Any(char.IsWhiteSpace))
        {
            return AgentErrorResults.Create(ErrorCodes.InvalidRequest, $"A mutating request needs a {Protocol.MasterHeader} header '{{instanceId}}/{{epoch}}/{{bootId}}'.");
        }

        FencingCheck check = await fencing.CheckAsync(presented, context.RequestAborted).ConfigureAwait(false);
        switch (check.Decision)
        {
            case FencingDecision.StaleMaster:
                return AgentErrorResults.Create(ErrorCodes.StaleMaster, string.Create(CultureInfo.InvariantCulture,
                    $"Master epoch {presented.Epoch} is lower than the accepted epoch {check.Accepted?.Epoch}; a takeover is needed."),
                    new { acceptedMaster = check.Accepted });
            case FencingDecision.CompetingMaster:
                return AgentErrorResults.Create(ErrorCodes.CompetingMaster, string.Create(CultureInfo.InvariantCulture,
                    $"Another master was accepted first at epoch {presented.Epoch}."),
                    new { acceptedMaster = check.Accepted });
        }

        context.Features.Set(new AgentRequestFeature
        {
            Route = route,
            ProtocolVersion = protocolVersion,
            Master = check.Accepted ?? presented,
            RequestTime = requestTime,
        });
        return null;
    }

    private async Task RefuseAsync(HttpContext context, int statusCode, AgentError error)
    {
        AgentHostLog.RequestRefused(logger, context.Request.Method, context.Request.Path, error.Code, error.Message);
        await AgentErrorResults.WriteAsync(context, statusCode, error).ConfigureAwait(false);
    }

    private static int StatusOf(string code) => ErrorMappings.AgentStatus(code);
}
