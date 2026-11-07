using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Web.Api;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MirbadLLM.Web.Mcp;

[McpServerToolType]
public sealed partial class MirbadMcpTools(
    IModelRegistry models,
    IModelRuntime runtime,
    INodeRegistry nodes,
    INodeStatusCache nodeStatus,
    IHuggingFaceDownloader downloader,
    ModelStartService starts,
    ModelJobService jobs,
    BenchmarkStore benchmarkStore,
    IServiceProvider services,
    IHttpContextAccessor http,
    IAuditLog audit,
    TimeProvider time)
{
    private const int DefaultPageSize = 50;
    private const int MaximumPageSize = 100;

    [McpServerTool(Name = "mirbad_get_fleet_health", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Summarize controller, node, model, download, and job health.")]
    public async Task<object> GetFleetHealth(CancellationToken ct)
    {
        var definitions = await models.ListAsync(ct).ConfigureAwait(false);
        var statuses = await runtime.ListStatusAsync(ct).ConfigureAwait(false);
        var downloads = await downloader.ListAsync(ct).ConfigureAwait(false);
        return new
        {
            status = nodeStatus.ListNodes().All(n => n.IsServing) ? "healthy" : "degraded",
            nodes = nodeStatus.ListNodes(),
            models = new { total = definitions.Count, ready = statuses.Count(s => s.State == ModelState.Ready) },
            downloads = new { total = downloads.Count, active = downloads.Count(d => d.State is DownloadState.Downloading or DownloadState.Queued) },
            jobs = new { running = jobs.Running.Count, limit = jobs.MaxConcurrent },
        };
    }

    [McpServerTool(Name = "mirbad_list_nodes", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List registered fleet nodes and their current live status.")]
    public async Task<object> ListNodes(int offset = 0, int limit = DefaultPageSize, CancellationToken ct = default)
    {
        (offset, limit) = PageArgs(offset, limit);
        var registered = (await nodes.ListAsync(ct).ConfigureAwait(false)).Where(n => !n.Revoked).ToList();
        var items = registered.Skip(offset).Take(limit).Select(n => new { record = n, live = nodeStatus.GetNode(n.Id) }).ToList();
        return Page(items, offset, limit, registered.Count);
    }

    [McpServerTool(Name = "mirbad_list_models", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List registered models with current runtime state.")]
    public async Task<object> ListModels(int offset = 0, int limit = DefaultPageSize, CancellationToken ct = default)
    {
        (offset, limit) = PageArgs(offset, limit);
        var definitions = await models.ListAsync(ct).ConfigureAwait(false);
        var statuses = (await runtime.ListStatusAsync(ct).ConfigureAwait(false)).ToDictionary(s => s.Id, StringComparer.Ordinal);
        var items = definitions.Skip(offset).Take(limit).Select(m => new { model = m, status = statuses.GetValueOrDefault(m.Id) }).ToList();
        return Page(items, offset, limit, definitions.Count);
    }

    [McpServerTool(Name = "mirbad_get_model", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get one registered model and its current runtime state.")]
    public async Task<object> GetModel([Description("Stable model id.")] string modelId, CancellationToken ct = default)
    {
        ModelDefinition model = await RequireModel(modelId, ct).ConfigureAwait(false);
        return new { model, status = await runtime.GetStatusAsync(modelId, ct).ConfigureAwait(false) };
    }

    [McpServerTool(Name = "mirbad_list_downloads", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List Hugging Face downloads on a selected node.")]
    public async Task<object> ListDownloads(string? nodeId = null, int offset = 0, int limit = DefaultPageSize, CancellationToken ct = default)
    {
        (offset, limit) = PageArgs(offset, limit);
        var selected = await Downloader(nodeId, ct).ConfigureAwait(false);
        var all = await selected.Value.ListAsync(ct).ConfigureAwait(false);
        return new { nodeId = selected.NodeId, page = Page(all.Skip(offset).Take(limit).ToList(), offset, limit, all.Count) };
    }

    [McpServerTool(Name = "mirbad_get_download", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get one download by its safe models-root folder name.")]
    public async Task<object> GetDownload(string folder, string? nodeId = null, CancellationToken ct = default)
    {
        ValidateFolder(folder);
        var selected = await Downloader(nodeId, ct).ConfigureAwait(false);
        var status = await selected.Value.GetAsync(folder, ct).ConfigureAwait(false)
            ?? throw new McpException($"Download '{folder}' was not found on node '{selected.NodeId}'.");
        return new { nodeId = selected.NodeId, download = status };
    }

    [McpServerTool(Name = "mirbad_list_jobs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List active benchmark and tuning jobs across nodes.")]
    public async Task<object> ListJobs(int offset = 0, int limit = DefaultPageSize, CancellationToken ct = default)
    {
        (offset, limit) = PageArgs(offset, limit);
        var all = await jobs.ListJobSummariesAsync(ct).ConfigureAwait(false);
        return Page(all.Skip(offset).Take(limit).ToList(), offset, limit, all.Count);
    }

    [McpServerTool(Name = "mirbad_get_job", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get an active job by job id.")]
    public object GetJob(string jobId)
    {
        var job = jobs.Running.FirstOrDefault(j => string.Equals(j.JobId, jobId, StringComparison.Ordinal))
            ?? throw new McpException($"Active job '{jobId}' was not found; use mirbad_list_jobs to refresh.");
        return new { job };
    }

    [McpServerTool(Name = "mirbad_list_benchmarks", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List persisted benchmark runs for a model, newest first.")]
    public async Task<object> ListBenchmarks(string modelId, int offset = 0, int limit = DefaultPageSize, CancellationToken ct = default)
    {
        (offset, limit) = PageArgs(offset, limit);
        await RequireModel(modelId, ct).ConfigureAwait(false);
        var all = (await benchmarkStore.ListBenchmarksAsync(modelId, ct).ConfigureAwait(false)).Reverse().ToList();
        return Page(all.Skip(offset).Take(limit).ToList(), offset, limit, all.Count);
    }

    [McpServerTool(Name = "mirbad_list_tuning_runs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List persisted tuning runs for a model, newest first.")]
    public async Task<object> ListTuningRuns(string modelId, int offset = 0, int limit = DefaultPageSize, CancellationToken ct = default)
    {
        (offset, limit) = PageArgs(offset, limit);
        await RequireModel(modelId, ct).ConfigureAwait(false);
        var all = (await benchmarkStore.ListTuningAsync(modelId, ct).ConfigureAwait(false)).Reverse().ToList();
        return Page(all.Skip(offset).Take(limit).ToList(), offset, limit, all.Count);
    }

    [McpServerTool(Name = "mirbad_deploy_model", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Register a validated model and immediately start its Hugging Face download; returns without waiting for completion.")]
    public async Task<object> DeployModel(
        string modelId,
        string servedName,
        string repoId,
        string folder,
        string nodeId,
        string? revision = null,
        IReadOnlyList<string>? allowPatterns = null,
        CancellationToken ct = default)
    {
        RequireOperator();
        ValidateModelId(modelId);
        ValidateFolder(folder);
        if (string.IsNullOrWhiteSpace(servedName) || servedName.Length > 200)
            throw new McpException("servedName is required and must be at most 200 characters.");
        if (!RepoIdRegex().IsMatch(repoId))
            throw new McpException("repoId must be a Hugging Face owner/repository identifier.");
        if (allowPatterns is { Count: > 32 } || allowPatterns?.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 200 || p.Contains("..", StringComparison.Ordinal)) == true)
            throw new McpException("allowPatterns may contain at most 32 non-empty repository-relative glob patterns without '..'.");
        if (await models.GetAsync(modelId, ct).ConfigureAwait(false) is not null)
            throw new McpException($"Model '{modelId}' already exists; choose another id.");

        var selected = await Downloader(nodeId, ct).ConfigureAwait(false);
        var model = new ModelDefinition
        {
            Id = modelId,
            ServedName = servedName,
            ModelPath = folder,
            Sources = [new ModelSource(repoId, folder, revision, allowPatterns)],
            Placement = new ModelPlacement(selected.NodeId),
        };

        await models.UpsertAsync(model, ct).ConfigureAwait(false);
        try
        {
            DownloadStatus state = await selected.Value.StartAsync(new DownloadRequest(repoId, revision, Folder: folder, AllowPatterns: allowPatterns), ct).ConfigureAwait(false);
            await Audit(AuditActions.ModelCreate, modelId, selected.NodeId, AuditResults.Accepted, "MCP deploy and download started").ConfigureAwait(false);
            return new { model, folder, nodeId = selected.NodeId, download = state, nextAction = "Poll mirbad_get_download; when complete, call mirbad_start_model." };
        }
        catch
        {
            await models.DeleteAsync(modelId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    [McpServerTool(Name = "mirbad_start_model", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Start a registered model using existing placement, ownership, and admission checks.")]
    public async Task<object> StartModel(string modelId, bool force = false, CancellationToken ct = default)
    {
        RequireOperator();
        var status = await starts.StartAsync(modelId, force, ct).ConfigureAwait(false);
        await Audit(AuditActions.ModelStart, modelId, null, AuditResults.Accepted).ConfigureAwait(false);
        return new { modelId, status, nextAction = "Poll mirbad_get_model until the state is Ready." };
    }

    [McpServerTool(Name = "mirbad_stop_model", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Stop a model after enforcing ownership checks; also cancels a queued manual start.")]
    public async Task<object> StopModel(string modelId, CancellationToken ct = default)
    {
        RequireOperator();
        await RequireModel(modelId, ct).ConfigureAwait(false);
        await HeldModelGuard.EnsureNotHeldAsync(services.GetService<IOwnershipStore>(), modelId, ct).ConfigureAwait(false);
        starts.Cancel(modelId);
        await runtime.StopAsync(modelId, ct).ConfigureAwait(false);
        await Audit(AuditActions.ModelStop, modelId, null, AuditResults.Succeeded).ConfigureAwait(false);
        return new { modelId, state = "stopped" };
    }

    [McpServerTool(Name = "mirbad_delete_model", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Delete a model registration; files in the models root are not deleted.")]
    public async Task<object> DeleteModel(string modelId, CancellationToken ct = default)
    {
        RequireOperator();
        bool deleted = await models.DeleteAsync(modelId, ct).ConfigureAwait(false);
        if (!deleted) throw new McpException($"Model '{modelId}' was not found.");
        await Audit(AuditActions.ModelDelete, modelId, null, AuditResults.Succeeded).ConfigureAwait(false);
        return new { modelId, deleted = true, modelFilesDeleted = false };
    }

    [McpServerTool(Name = "mirbad_start_benchmark", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Start a benchmark job and return immediately; the model must be Ready.")]
    public async Task<object> StartBenchmark(string modelId, TuningGoal? preset = null, CancellationToken ct = default)
    {
        RequireOperator();
        var job = await jobs.StartBenchmarkAsync(modelId, preset, ct).ConfigureAwait(false);
        await Audit(AuditActions.Job, modelId, job.NodeId, AuditResults.Accepted, "MCP benchmark started").ConfigureAwait(false);
        return new { job, nextAction = "Poll mirbad_get_job or mirbad_list_jobs." };
    }

    [McpServerTool(Name = "mirbad_start_tuning", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Start a tuning job and return immediately; the winner is never applied automatically.")]
    public async Task<object> StartTuning(string modelId, TuningGoal goal = TuningGoal.Balanced, IReadOnlyList<TuningDimension>? dimensions = null, CancellationToken ct = default)
    {
        RequireOperator();
        var job = await jobs.StartTuningAsync(modelId, goal, dimensions, ct).ConfigureAwait(false);
        await Audit(AuditActions.Job, modelId, job.NodeId, AuditResults.Accepted, "MCP tuning started").ConfigureAwait(false);
        return new { job, nextAction = "Poll mirbad_get_job; inspect mirbad_list_tuning_runs after completion." };
    }

    [McpServerTool(Name = "mirbad_cancel_job", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Request cancellation of an active benchmark or tuning job by job id.")]
    public async Task<object> CancelJob(string jobId)
    {
        RequireOperator();
        var current = jobs.Running.FirstOrDefault(j => string.Equals(j.JobId, jobId, StringComparison.Ordinal))
            ?? throw new McpException($"Active job '{jobId}' was not found.");
        if (!jobs.CancelJob(jobId)) throw new McpException($"Job '{jobId}' completed before cancellation was requested.");
        await Audit(AuditActions.Job, current.ModelId, current.NodeId, AuditResults.Accepted, "MCP cancellation requested").ConfigureAwait(false);
        return new { jobId, cancellationRequested = true };
    }

    [McpServerTool(Name = "mirbad_apply_tuning_candidate", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Apply an accepted tuning candidate to the saved model definition without starting it.")]
    public async Task<object> ApplyTuningCandidate(string modelId, string runId, [Range(0, int.MaxValue)] int candidateIndex, CancellationToken ct = default)
    {
        RequireOperator();
        if (candidateIndex < 0) throw new McpException("candidateIndex must be non-negative.");
        var model = await jobs.ApplyTuningCandidateAsync(modelId, runId, candidateIndex, ct).ConfigureAwait(false);
        await Audit(AuditActions.ModelUpdate, modelId, model.Placement?.NodeId, AuditResults.Succeeded, $"MCP applied tuning candidate {runId}#{candidateIndex}").ConfigureAwait(false);
        return new { model, runId, candidateIndex, nextAction = "Restart the model to use the saved settings." };
    }

    private void RequireOperator()
    {
        string? scope = http.HttpContext?.User.FindFirst(McpApiKeyAuthenticationHandler.ScopeClaim)?.Value;
        if (!string.Equals(scope, ApiKeyScope.McpOperator.ToString(), StringComparison.Ordinal))
            throw new McpException("MCP Operator scope is required for this mutation; no state was changed (403 equivalent).");
    }

    private async Task<ModelDefinition> RequireModel(string modelId, CancellationToken ct)
    {
        ValidateModelId(modelId);
        return await models.GetAsync(modelId, ct).ConfigureAwait(false)
            ?? throw new McpException($"Model '{modelId}' was not found; use mirbad_list_models to refresh.");
    }

    private async Task<(IHuggingFaceDownloader Value, string NodeId)> Downloader(string? nodeId, CancellationToken ct)
    {
        ApiNodeScope scope = await ApiNodeScope.ResolveAsync(services, nodeId, ct).ConfigureAwait(false);
        if (scope.Refusal is not null || scope.NodeId is null)
            throw new McpException("The node could not be selected. Supply an approved, non-revoked node id; a fleet with multiple nodes requires nodeId.");
        return (DownloadApiEndpoints.DownloaderFor(services, downloader, scope), scope.NodeId);
    }

    private async Task Audit(string action, string? modelId, string? nodeId, string result, string? detail = null)
    {
        string actor = http.HttpContext?.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "mcp";
        await audit.WriteAsync(new AuditRecord
        {
            Time = time.GetUtcNow(), Actor = "mcp:" + actor, Action = action, ModelId = modelId, NodeId = nodeId,
            Result = result, Detail = detail,
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private static (int Offset, int Limit) PageArgs(int offset, int limit)
    {
        if (offset < 0) throw new McpException("offset must be non-negative.");
        if (limit is < 1 or > MaximumPageSize) throw new McpException($"limit must be between 1 and {MaximumPageSize}.");
        return (offset, limit);
    }

    private static object Page<T>(IReadOnlyList<T> items, int offset, int limit, int total) =>
        new { items, offset, limit, total, hasMore = offset + items.Count < total };

    private static void ValidateModelId(string value)
    {
        if (!ModelIdRegex().IsMatch(value ?? ""))
            throw new McpException("modelId must be a lowercase slug of 1-40 letters, digits, or hyphens.");
    }

    private static void ValidateFolder(string value)
    {
        if (!FolderRegex().IsMatch(value ?? "") || (value?.Contains("..", StringComparison.Ordinal) ?? false))
            throw new McpException("folder must be a single safe models-root folder name (letters, digits, '.', '_' or '-', at most 100 characters).");
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,38}[a-z0-9])?$")]
    private static partial Regex ModelIdRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")]
    private static partial Regex FolderRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,95}/[A-Za-z0-9][A-Za-z0-9._-]{0,95}$")]
    private static partial Regex RepoIdRegex();
}
