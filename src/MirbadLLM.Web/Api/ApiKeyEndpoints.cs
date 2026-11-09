using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MirbadLLM.Core;

namespace MirbadLLM.Web.Api;

/// <summary>Request body for <c>POST /api/keys</c>: <paramref name="Scopes"/>, or the single <paramref name="Scope"/> when it is left out.</summary>
public sealed record CreateApiKeyRequest(string Name, ApiKeyScope Scope, IReadOnlyList<ApiKeyScope>? Scopes = null);

/// <summary>Request body for <c>PUT /api/keys/{id}/scopes</c>: the full set the key should hold.</summary>
public sealed record UpdateApiKeyScopesRequest(IReadOnlyList<ApiKeyScope>? Scopes);

/// <summary>Response for <c>POST /api/keys</c>: the persisted record plus the plaintext key, shown once.</summary>
public sealed record CreateApiKeyResponse(ApiKeyRecord Record, string FullKey);

/// <summary>The admin API for managing API keys (<c>/api/keys</c>). Protected the same way as the rest of
/// <c>/api/*</c> — by the app's cookie-authorization fallback policy, wired up in <c>Program.cs</c> — so this
/// file adds no authorization of its own.</summary>
public static class ApiKeyEndpoints
{
    public static IEndpointRouteBuilder MapApiKeyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/keys");

        api.MapGet("/", async (IApiKeyStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(ct)));

        api.MapPost("/", async (CreateApiKeyRequest request, IApiKeyStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.Problem(detail: "Name must not be empty.", statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var created = await store.CreateAsync(request.Name, request.Scopes ?? [request.Scope], ct);
                return Results.Created($"/api/keys/{created.Record.Id}", new CreateApiKeyResponse(created.Record, created.FullKey));
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        api.MapPut("/{id}/scopes", async (string id, UpdateApiKeyScopesRequest request, IApiKeyStore store, CancellationToken ct) =>
        {
            try
            {
                return await store.UpdateScopesAsync(id, request.Scopes ?? [], ct) is { } updated ? Results.Ok(updated) : Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        api.MapPost("/{id}/revoke", async (string id, IApiKeyStore store, CancellationToken ct) =>
            await store.RevokeAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        // Permanent removal, not revocation: refuses an active key with 409 so a key is always revoked
        // (see above) before it can be deleted — see IApiKeyStore.DeleteAsync.
        api.MapDelete("/{id}", async (string id, IApiKeyStore store, CancellationToken ct) =>
            await store.DeleteAsync(id, ct) switch
            {
                ApiKeyDeleteResult.Deleted => Results.NoContent(),
                ApiKeyDeleteResult.StillActive => Results.Problem(
                    detail: "This key is still active. Revoke it before deleting it.",
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.NotFound(),
            });

        return endpoints;
    }
}
