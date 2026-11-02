using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Agent.Pki;
using MirbadLLM.Core;
using MirbadLLM.Pki;

namespace MirbadLLM.Agent.Api;

/// <summary>
/// The agent's <c>/pki/*</c> routes (design §4.3 "PKI", §10.2; all allowed while blocked): rotation start, certificate
/// install, commit, abort and status, and the master trust anchors. Mutations answer 200 with the completed
/// <see cref="OperationStatus"/> (result typed by the route) or the agent error; the work is
/// <see cref="AgentPkiService"/>'s. Mapped through <see cref="AgentEndpointExtensions.MapAgentRoute"/>, so the pipeline
/// applies the protocol, request-time and fencing checks first.
/// </summary>
public static class PkiEndpoints
{
    private const string RotationIdParameter = "rotationId";

    /// <summary>Maps every PKI route of <see cref="AgentRoutes"/>.</summary>
    public static IEndpointRouteBuilder MapPkiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapAgentRoute(AgentRoutes.PkiRotationStart, (HttpContext http) =>
            MutateAsync<PkiRotationStartRequest>(http, AgentRoutes.PkiRotationStart, (service, request, master, body) =>
                service.StartAsync(request, master, body, http.RequestAborted)));
        endpoints.MapAgentRoute(AgentRoutes.PkiRotationCert, (HttpContext http) =>
            MutateAsync<PkiRotationCertRequest>(http, AgentRoutes.PkiRotationCert, (service, request, master, body) =>
                service.InstallCertificateAsync(RotationId(http), request, master, body, http.RequestAborted)));
        endpoints.MapAgentRoute(AgentRoutes.PkiRotationCommit, (HttpContext http) =>
            MutateAsync<PkiRotationCommitRequest>(http, AgentRoutes.PkiRotationCommit, (service, request, master, body) =>
                service.CommitAsync(RotationId(http), request, master, body, ConnectionSpki(http), http.RequestAborted)));
        endpoints.MapAgentRoute(AgentRoutes.PkiRotationAbort, (HttpContext http) =>
            MutateAsync<PkiRotationAbortRequest>(http, AgentRoutes.PkiRotationAbort, (service, request, master, body) =>
                service.AbortAsync(RotationId(http), request, master, body, http.RequestAborted)));
        endpoints.MapAgentRoute(AgentRoutes.PkiTrustSet, (HttpContext http) =>
            MutateAsync<PkiTrustSetRequest>(http, AgentRoutes.PkiTrustSet, (service, request, master, body) =>
                service.SetTrustAsync(request, master, body, http.Connection.ClientCertificate, http.RequestAborted)));

        endpoints.MapAgentRoute(AgentRoutes.PkiRotationGet, (HttpContext http) =>
            Results.Json(Service(http).GetRotation(RotationId(http)), AgentJson.Options, AgentJson.JsonContentType));
        endpoints.MapAgentRoute(AgentRoutes.PkiTrustGet, (HttpContext http) =>
            Results.Json(Service(http).GetTrust(), AgentJson.Options, AgentJson.JsonContentType));
        return endpoints;
    }

    /// <summary>
    /// SPKI pin of the server certificate this request's TLS connection negotiated, or null when the connection is not
    /// TLS (the rotation commit then refuses: it must prove the new certificate, §10.2 step 3).
    /// </summary>
    internal static string? ConnectionSpki(HttpContext http)
    {
        X509Certificate? local = http.Features.Get<ISslStreamFeature>()?.SslStream.LocalCertificate;
        if (local is null)
        {
            return null;
        }

        if (local is X509Certificate2 typed)
        {
            return Spki.Sha256(typed);
        }

        using X509Certificate2 converted = X509CertificateLoader.LoadCertificate(local.GetRawCertData());
        return Spki.Sha256(converted);
    }

    private static async Task<IResult> MutateAsync<T>(
        HttpContext http,
        AgentRoute route,
        Func<AgentPkiService, T, MasterTriple, JsonElement, Task<OperationStatus>> handler)
        where T : AgentMutation
    {
        JsonElement body;
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted).ConfigureAwait(false);
            body = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return AgentErrorResults.Error(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, $"The body is not valid JSON: {ex.Message}");
        }

        T? request;
        try
        {
            request = body.Deserialize<T>(AgentJson.Options);
        }
        catch (JsonException ex)
        {
            return AgentErrorResults.Error(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, $"The body does not match {typeof(T).Name}: {ex.Message}");
        }

        if (request is null)
        {
            return AgentErrorResults.Error(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, $"A {typeof(T).Name} body is required.");
        }

        OperationStatus status = await handler(Service(http), request, http.GetAcceptedMaster(), body).ConfigureAwait(false);
        return Results.Json(status, AgentJson.Options, AgentJson.JsonContentType, route.SuccessStatus);
    }

    private static AgentPkiService Service(HttpContext http) => http.RequestServices.GetRequiredService<AgentPkiService>();

    private static string RotationId(HttpContext http) => http.Request.RouteValues[RotationIdParameter] as string ?? "";
}
