using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Orchestration.Recovery;

namespace MirbadLLM.Web.Api;

/// <summary>
/// Hugging Face downloads (<c>/api/downloads</c>): start, list/poll progress, cancel.
/// <para>Per node (design §5.6, D106 follow-up, MN-229): every route except the repo inspection (it reads Hugging Face, not
/// a node) takes an optional <c>?node=</c>. Without it the only fleet member acts, exactly as in P2 (the bootstrap node's
/// registered downloader); with two or more members it is 400 <c>node_required</c> (<see cref="ApiNodeScope"/>). An
/// unknown or revoked node is 404 <c>not_found</c>.</para>
/// <para>An Ollama source (<see cref="DownloadRequest.Source"/> = <see cref="ModelSourceKind.Ollama"/>) is refused with
/// 501 <c>not_supported</c> before anything is sent: no node of this master reports <see cref="AgentFeatures.DownloadsOllama"/>
/// (the puller, MN-221, is deferred with owner decisions O4/O7, D110/D113), and the master does not forward Ollama
/// requests yet, so it is refused even for a node that reports the flag. Without the refusal the Hugging Face path would
/// read the <c>{name}:{tag}</c> key as a Hugging Face repo id.</para>
/// </summary>
public static class DownloadApiEndpoints
{
    public static RouteGroupBuilder MapDownloadApi(this RouteGroupBuilder group)
    {
        group.MapPost("/downloads", async (DownloadRequest request, string? node, IHuggingFaceDownloader downloader, IServiceProvider services, CancellationToken ct) =>
        {
            var scope = await ApiNodeScope.ResolveAsync(services, node, ct);
            if (scope.Refusal is { } refused)
            {
                return refused;
            }

            if (request.Source == ModelSourceKind.Ollama)
            {
                return await OllamaRefusalAsync(services, scope.NodeId, ct);
            }

            try
            {
                var status = await DownloaderFor(services, downloader, scope).StartAsync(request, ct);
                return Results.Created(scope.IsDefault ? $"/api/downloads/{status.Folder}" : $"/api/downloads/{status.Folder}?node={scope.NodeId}", status);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (NodeUnavailableException ex) when (ex.IsTransient)
            {
                // Multi-node wiring: the node cannot be reached or Docker is down on it (MN-047, consistent with start/stop).
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
            catch (HuggingFaceApiException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        });

        group.MapGet("/downloads", async (string? node, IHuggingFaceDownloader downloader, IServiceProvider services, CancellationToken ct) =>
        {
            var scope = await ApiNodeScope.ResolveAsync(services, node, ct);
            return scope.Refusal ?? await ReadAsync(() => DownloaderFor(services, downloader, scope).ListAsync(ct), Results.Ok);
        });

        // Repo inspection (format + GGUF variants), read-only: no download is started. The token, when given,
        // travels only as the X-HF-Token request header — never a query string — and is never logged. It reads Hugging
        // Face, not a node, so it takes no ?node= (MN-229).
        group.MapGet("/downloads/inspect", async (
            string? repoId, string? revision, HttpRequest request, IHuggingFaceRepoInspector inspector, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(repoId))
            {
                return Results.Problem(detail: "repoId is required.", statusCode: StatusCodes.Status400BadRequest);
            }

            string? token = request.Headers.TryGetValue("X-HF-Token", out var values) && values.Count > 0 ? values[0] : null;
            try
            {
                var inspection = await inspector.InspectAsync(repoId, revision, string.IsNullOrEmpty(token) ? null : token, ct);
                return Results.Ok(inspection);
            }
            catch (HuggingFaceApiException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        });

        group.MapGet("/downloads/{folder}", async (string folder, string? node, IHuggingFaceDownloader downloader, IServiceProvider services, CancellationToken ct) =>
        {
            var scope = await ApiNodeScope.ResolveAsync(services, node, ct);
            if (scope.Refusal is { } refused)
            {
                return refused;
            }

            return await ReadAsync(
                () => DownloaderFor(services, downloader, scope).GetAsync(folder, ct),
                status => status is null ? Results.NotFound() : Results.Ok(status));
        });

        group.MapDelete("/downloads/{folder}", async (string folder, string? node, IHuggingFaceDownloader downloader, IServiceProvider services, CancellationToken ct) =>
        {
            var scope = await ApiNodeScope.ResolveAsync(services, node, ct);
            if (scope.Refusal is { } refused)
            {
                return refused;
            }

            return await ReadAsync(
                () => DownloaderFor(services, downloader, scope).CancelAsync(folder, ct),
                cancelled => cancelled ? Results.NoContent() : Results.NotFound());
        });

        return group;
    }

    /// <summary>
    /// Runs a per-node download read and answers 503 when the node cannot be reached, exactly as the model-folders
    /// routes do. Without this an unreachable node (<see cref="NodeUnavailableException"/>) reached the error handler
    /// and the caller got 500 (MN-230, D130).
    /// </summary>
    private static async Task<IResult> ReadAsync<T>(Func<Task<T>> read, Func<T, IResult> answer)
    {
        try
        {
            return answer(await read().ConfigureAwait(false));
        }
        catch (NodeUnavailableException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// The downloader of <paramref name="scope"/>'s node: the registered one (the bootstrap node's, P2's path) for the
    /// default node and the bootstrap node, else the node's own through its agent (the master's per-node downloaders,
    /// <see cref="IRestoreNodeResources"/>, or one built like the model editor's).
    /// </summary>
    internal static IHuggingFaceDownloader DownloaderFor(IServiceProvider services, IHuggingFaceDownloader registered, ApiNodeScope scope)
    {
        string node = scope.NodeId!;
        if (registered is RemoteHuggingFaceDownloader own ? own.NodeId == node : node == ApiNodeScope.BootstrapNodeId(services))
        {
            return registered;
        }

        if (services.GetService<IRestoreNodeResources>() is { } resources)
        {
            return resources.DownloaderFor(node);
        }

        return services.GetService<IAgentClientFactory>() is { } clients && services.GetService<AgentOperationRunner>() is { } runner
            ? new RemoteHuggingFaceDownloader(node, clients, runner)
            : registered;
    }

    private static async Task<IResult> OllamaRefusalAsync(IServiceProvider services, string? nodeId, CancellationToken ct)
    {
        bool reports = nodeId is not null && await ApiNodeScope.ReportsAsync(services, nodeId, AgentFeatures.DownloadsOllama, ct);
        string detail = reports
            ? $"Node '{nodeId}' reports '{AgentFeatures.DownloadsOllama}', but this master does not start Ollama-registry downloads (deferred, MN-221)."
            : $"Node '{nodeId}' does not support Ollama-registry downloads (feature '{AgentFeatures.DownloadsOllama}').";
        return Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status501NotImplemented,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotSupported });
    }
}

/// <summary>
/// The node an admin call acts on (design §5.6 "Optional <c>?node=</c>; default = the only node, else 400
/// <c>node_required</c>"; D106 follow-up, MN-229). Used by <c>/api/downloads*</c>, <c>/api/model-folders*</c> and
/// <c>/api/catalog</c>.
/// <list type="bullet">
/// <item>Without <c>?node=</c>: the only fleet member (approved, non-revoked; <see cref="DefaultPlacement.ResolveAsync"/>,
/// the same rule as a model created without a placement), or the bootstrap node when no registry is composed or it holds
/// no node yet: <see cref="IsDefault"/>, which is P2's behaviour. With two or more members: 400 <c>node_required</c>.</item>
/// <item>With <c>?node=</c>: that node when it is a valid id of a known, approved, non-revoked node (400
/// <c>invalid_request</c> for a malformed id, 404 <c>not_found</c> for an unknown or revoked one, 409
/// <c>invalid_state</c> for one that is not approved yet, MN-230). A node that is not serving is refused by the read
/// itself (503).</item>
/// </list>
/// </summary>
public sealed record ApiNodeScope
{
    /// <summary>The node acted on; null only with a <see cref="Refusal"/>.</summary>
    public string? NodeId { get; init; }

    /// <summary>True when the node was not named (the only member or the bootstrap node).</summary>
    public bool IsDefault { get; init; }

    /// <summary>The answer when no node can be chosen.</summary>
    public IResult? Refusal { get; init; }

    /// <summary>Resolves <paramref name="node"/> (the <c>?node=</c> value, null when absent).</summary>
    public static async Task<ApiNodeScope> ResolveAsync(IServiceProvider services, string? node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        string bootstrap = BootstrapNodeId(services);
        var registry = services.GetService<INodeRegistry>();
        if (string.IsNullOrWhiteSpace(node))
        {
            return await DefaultPlacement.ResolveAsync(registry, bootstrap, ct) is { } placement
                ? new ApiNodeScope { NodeId = placement.NodeId, IsDefault = true }
                : Refuse(StatusCodes.Status400BadRequest, ErrorCodes.NodeRequired,
                    "More than one node exists; name the node with ?node=<id>.");
        }

        if (!Protocol.IsValidNodeId(node))
        {
            return Refuse(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, "?node= is not a valid node id.");
        }

        if (registry is null)
        {
            return string.Equals(node, bootstrap, StringComparison.Ordinal)
                ? new ApiNodeScope { NodeId = node }
                : Refuse(StatusCodes.Status404NotFound, ErrorCodes.NotFound, $"Node '{node}' does not exist.");
        }

        var record = await registry.GetAsync(node, ct).ConfigureAwait(false);
        if (record is null || record.Revoked)
        {
            return Refuse(StatusCodes.Status404NotFound, ErrorCodes.NotFound, $"Node '{node}' does not exist.");
        }

        // Approval is the master's trust gate: before it, nothing is sent to the node — a download would hand it the
        // Hugging Face token and write to its disk (MN-230, D130). Same refusal as the node commands (MN-029).
        return record.Approved
            ? new ApiNodeScope { NodeId = node }
            : Refuse(StatusCodes.Status409Conflict, ErrorCodes.InvalidState, $"Node '{node}' is not approved yet; approve it first.");
    }

    /// <summary>
    /// True when <paramref name="nodeId"/> reports <paramref name="feature"/>: its live capabilities (status cache), else
    /// the last ones the registry stored.
    /// </summary>
    public static async Task<bool> ReportsAsync(IServiceProvider services, string nodeId, string feature, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        IReadOnlyList<string>? features = services.GetService<INodeStatusCache>()?.GetNode(nodeId)?.Capabilities?.Features;
        if (features is null && services.GetService<INodeRegistry>() is { } registry)
        {
            features = (await registry.GetAsync(nodeId, ct))?.LastCapabilities?.Features;
        }

        return features?.Contains(feature, StringComparer.Ordinal) == true;
    }

    /// <summary>The bootstrap node id (<c>dgx</c> by default).</summary>
    public static string BootstrapNodeId(IServiceProvider services) =>
        services.GetService<IOptions<RegistryMigrationOptions>>()?.Value.EffectiveBootstrapNodeId ?? RegistryMigrationOptions.DefaultBootstrapNodeId;

    private static ApiNodeScope Refuse(int status, string code, string detail) => new()
    {
        Refusal = Results.Problem(detail: detail, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code }),
    };
}
