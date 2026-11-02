using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;

namespace MirbadLLM.Orchestration.Audit;

/// <summary>
/// The audit query API (design §10.5, §12.1 "Recent commands"): <c>GET /api/audit?nodeId=&amp;modelId=&amp;since=&amp;max=</c>,
/// newest first, admin-only like the rest of <c>/api</c> (the fallback authorization policy). Mapped by the composition
/// (MN-130).
/// </summary>
public static class AuditApiEndpoints
{
    /// <summary>Maps <c>GET /api/audit</c>.</summary>
    public static IEndpointRouteBuilder MapAuditApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/api/audit", async (string? nodeId, string? modelId, DateTimeOffset? since, int? max, IAuditLog log, CancellationToken ct) =>
        {
            if (max is < 1)
            {
                return Results.Problem(detail: "max must be at least 1.", statusCode: StatusCodes.Status400BadRequest);
            }

            var query = new AuditQuery { NodeId = nodeId, ModelId = modelId, Since = since, Max = max ?? new AuditQuery().Max };
            return Results.Ok(await log.ReadAsync(query, ct).ConfigureAwait(false));
        });
        return endpoints;
    }
}

/// <summary>Registration helpers for the audit log (called by the composition, MN-130).</summary>
public static class AuditServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="JsonlAuditLog"/> as the <see cref="IAuditLog"/> (replacing <see cref="NullAuditLog"/> or any
    /// earlier registration) and, when a <see cref="JsonAgentOperationReferenceStore"/> is registered, decorates the
    /// <see cref="IAgentOperationReferenceStore"/> with <see cref="AuditingOperationReferenceStore"/>. Call after the
    /// orchestration services.
    /// </summary>
    public static IServiceCollection AddMirbadLLMAudit(this IServiceCollection services, AuditLogOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new JsonlAuditLog(
            sp.GetRequiredService<IOptions<MirbadLLMOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<JsonlAuditLog>>(),
            options));
        services.RemoveAll<IAuditLog>();
        services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<JsonlAuditLog>());
        if (services.Any(d => d.ServiceType == typeof(JsonAgentOperationReferenceStore)))
        {
            services.RemoveAll<IAgentOperationReferenceStore>();
            services.AddSingleton<IAgentOperationReferenceStore>(sp => new AuditingOperationReferenceStore(
                sp.GetRequiredService<JsonAgentOperationReferenceStore>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>()));
        }

        return services;
    }
}
