using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Web.Api;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MirbadLLM.Web.Mcp;

// OutputSchemaType: a method returning object otherwise publishes a boolean subschema, and Claude Code then rejects all of tools/list.
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

    private const string UnprovenSpeculativeNote =
        "Speculative decoding is saved but not proven. It depends on the runtime's image and the drafter, so plan to check it: " +
        "after mirbad_start_model, poll mirbad_get_model for Ready, then run mirbad_start_benchmark. If it fails, delete the model and deploy again without it; the downloaded files are kept.";

    private const string UnprovenImageNote =
        "The image is pulled when the model starts if the node lacks it. A digestPinned node refuses images it has not approved, and only a start shows whether the image can serve this model.";

    /// <summary>Sent to every client when it connects, so a calling model can plan around these limits before it deploys.</summary>
    public const string PlanningGuide =
        "Planning a model deployment. " +
        "(1) Call mirbad_list_node_runtimes first: a node only runs the runtimes it reports, and a node with discrete GPUs needs gpuIndices. " +
        "(2) Settings depend on the runtime, and mirbad_deploy_model refuses one the runtime would ignore: kvCacheDtype works on vllm, llamacpp and mlx; enablePrefixCaching on vllm; " +
        "speculative decoding on vllm, sglang and mlx, never on llamacpp or nim; sglang needs speculativeMethod, an algorithm such as DFLASH, EAGLE3, STANDALONE or NGRAM. " +
        "(3) A deploy only proves the settings are valid. " + UnprovenSpeculativeNote + " " +
        "(4) " + UnprovenImageNote + " " +
        "(5) Tuning works only on vllm and llamacpp. On mlx, sglang and nim it is refused because it could not change anything; mirbad_start_benchmark still works there.";

    [McpServerTool(Name = "mirbad_get_fleet_health", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Summarize controller, node, model, download, and job health.")]
    public async Task<object> GetFleetHealth(CancellationToken ct)
    {
        var definitions = await models.ListAsync(ct).ConfigureAwait(false);
        var statuses = await runtime.ListStatusAsync(ct).ConfigureAwait(false);
        var live = nodeStatus.ListNodes();
        var reads = await Task.WhenAll(live.Select(n => ReadDownloads(n, ct))).ConfigureAwait(false);
        var downloads = reads.SelectMany(r => r.Downloads ?? []).ToList();
        var unavailable = reads.Where(r => r.Downloads is null).Select(r => r.NodeId).ToList();
        return new
        {
            status = live.Count > 0 && unavailable.Count == 0 ? "healthy" : "degraded",
            nodes = live,
            models = new { total = definitions.Count, ready = statuses.Count(s => s.State == ModelState.Ready) },
            downloads = new
            {
                total = downloads.Count,
                active = downloads.Count(d => d.State is DownloadState.Downloading or DownloadState.Queued),
                unavailableNodes = unavailable,
            },
            jobs = new { running = jobs.Running.Count, limit = jobs.MaxConcurrent },
        };
    }

    // A health check has to answer while a node is down, so a node it can't read is reported instead of failing the call.
    private async Task<(string NodeId, IReadOnlyList<DownloadStatus>? Downloads)> ReadDownloads(NodeLiveStatus node, CancellationToken ct)
    {
        if (!node.IsServing) return (node.NodeId, null);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var selected = DownloadApiEndpoints.DownloaderFor(services, downloader, new ApiNodeScope { NodeId = node.NodeId });
            return (node.NodeId, await selected.ListAsync(timeout.Token).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested)
        {
            return (node.NodeId, null);
        }
    }

    [McpServerTool(Name = "mirbad_list_nodes", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("List registered fleet nodes and their current live status.")]
    public async Task<object> ListNodes(int offset = 0, int limit = DefaultPageSize, CancellationToken ct = default)
    {
        (offset, limit) = PageArgs(offset, limit);
        var registered = (await nodes.ListAsync(ct).ConfigureAwait(false)).Where(n => !n.Revoked).ToList();
        var items = registered.Skip(offset).Take(limit).Select(n => new { record = n, live = nodeStatus.GetNode(n.Id) }).ToList();
        return Page(items, offset, limit, registered.Count);
    }

    [McpServerTool(Name = "mirbad_list_node_runtimes", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("List the runtimes, container images and GPUs each node last reported, to choose mirbad_deploy_model arguments.")]
    public async Task<object> ListNodeRuntimes([Description("One node id; omit for every node.")] string? nodeId = null, int offset = 0, int limit = DefaultPageSize, CancellationToken ct = default)
    {
        (offset, limit) = PageArgs(offset, limit);
        var registered = (await nodes.ListAsync(ct).ConfigureAwait(false))
            .Where(n => !n.Revoked && (nodeId is null || n.Id == nodeId))
            .ToList();
        if (nodeId is not null && registered.Count == 0)
            throw new McpException($"Node '{nodeId}' was not found; use mirbad_list_nodes to refresh.");

        var items = registered.Skip(offset).Take(limit).Select(n =>
        {
            var live = nodeStatus.GetNode(n.Id);
            var capabilities = live?.Capabilities ?? n.LastCapabilities;
            return new
            {
                nodeId = n.Id,
                state = live?.State,
                approved = n.Approved,
                draining = n.Draining,
                memoryModel = capabilities?.Memory.Model,
                digestPinned = capabilities?.Features.Contains(AgentFeatures.ImagesDigestPinned, StringComparer.Ordinal) ?? false,
                runtimes = capabilities?.Runtimes ?? [],
                images = capabilities?.Images ?? [],
                gpus = capabilities?.Gpus ?? [],
            };
        }).ToList();
        return Page(items, offset, limit, registered.Count);
    }

    [McpServerTool(Name = "mirbad_list_models", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("List registered models with current runtime state.")]
    public async Task<object> ListModels(int offset = 0, int limit = DefaultPageSize, CancellationToken ct = default)
    {
        (offset, limit) = PageArgs(offset, limit);
        var definitions = await models.ListAsync(ct).ConfigureAwait(false);
        var statuses = (await runtime.ListStatusAsync(ct).ConfigureAwait(false)).ToDictionary(s => s.Id, StringComparer.Ordinal);
        var items = definitions.Skip(offset).Take(limit).Select(m => new { model = m, status = statuses.GetValueOrDefault(m.Id) }).ToList();
        return Page(items, offset, limit, definitions.Count);
    }

    [McpServerTool(Name = "mirbad_get_model", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Get one registered model and its current runtime state.")]
    public async Task<object> GetModel([Description("Stable model id.")] string modelId, CancellationToken ct = default)
    {
        ModelDefinition model = await RequireModel(modelId, ct).ConfigureAwait(false);
        return new { model, status = await runtime.GetStatusAsync(modelId, ct).ConfigureAwait(false) };
    }

    [McpServerTool(Name = "mirbad_list_downloads", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("List Hugging Face downloads on a selected node.")]
    public async Task<object> ListDownloads(string? nodeId = null, int offset = 0, int limit = DefaultPageSize, CancellationToken ct = default)
    {
        (offset, limit) = PageArgs(offset, limit);
        var selected = await Downloader(nodeId, ct).ConfigureAwait(false);
        try
        {
            var all = await selected.Value.ListAsync(ct).ConfigureAwait(false);
            return new { nodeId = selected.NodeId, available = true, page = Page(all.Skip(offset).Take(limit).ToList(), offset, limit, all.Count) };
        }
        catch (NodeUnavailableException ex)
        {
            return new { nodeId = selected.NodeId, available = false, reason = ex.Message, page = Page<DownloadStatus>([], offset, limit, 0) };
        }
    }

    [McpServerTool(Name = "mirbad_get_download", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Get one download by its safe models-root folder name.")]
    public async Task<object> GetDownload(string folder, string? nodeId = null, CancellationToken ct = default)
    {
        ValidateFolder(folder);
        var selected = await Downloader(nodeId, ct).ConfigureAwait(false);
        try
        {
            var status = await selected.Value.GetAsync(folder, ct).ConfigureAwait(false)
                ?? throw new McpException($"Download '{folder}' was not found on node '{selected.NodeId}'.");
            return new { nodeId = selected.NodeId, download = status };
        }
        catch (NodeUnavailableException ex)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    [McpServerTool(Name = "mirbad_list_jobs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("List active benchmark and tuning jobs across nodes.")]
    public async Task<object> ListJobs(int offset = 0, int limit = DefaultPageSize, CancellationToken ct = default)
    {
        (offset, limit) = PageArgs(offset, limit);
        var all = await jobs.ListJobSummariesAsync(ct).ConfigureAwait(false);
        return Page(all.Skip(offset).Take(limit).ToList(), offset, limit, all.Count);
    }

    [McpServerTool(Name = "mirbad_get_job", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Get an active job by job id.")]
    public object GetJob(string jobId)
    {
        var job = jobs.Running.FirstOrDefault(j => string.Equals(j.JobId, jobId, StringComparison.Ordinal))
            ?? throw new McpException($"Active job '{jobId}' was not found; use mirbad_list_jobs to refresh.");
        return new { job };
    }

    [McpServerTool(Name = "mirbad_list_benchmarks", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("List persisted benchmark runs for a model, newest first.")]
    public async Task<object> ListBenchmarks(string modelId, int offset = 0, int limit = DefaultPageSize, CancellationToken ct = default)
    {
        (offset, limit) = PageArgs(offset, limit);
        await RequireModel(modelId, ct).ConfigureAwait(false);
        var all = (await benchmarkStore.ListBenchmarksAsync(modelId, ct).ConfigureAwait(false)).Reverse().ToList();
        return Page(all.Skip(offset).Take(limit).ToList(), offset, limit, all.Count);
    }

    [McpServerTool(Name = "mirbad_list_tuning_runs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("List persisted tuning runs for a model, newest first.")]
    public async Task<object> ListTuningRuns(string modelId, int offset = 0, int limit = DefaultPageSize, CancellationToken ct = default)
    {
        (offset, limit) = PageArgs(offset, limit);
        await RequireModel(modelId, ct).ConfigureAwait(false);
        var all = (await benchmarkStore.ListTuningAsync(modelId, ct).ConfigureAwait(false)).Reverse().ToList();
        return Page(all.Skip(offset).Take(limit).ToList(), offset, limit, all.Count);
    }

    [McpServerTool(Name = "mirbad_deploy_model", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Register a validated model on a runtime the node supports (see mirbad_list_node_runtimes) and immediately start its Hugging Face downloads; returns without waiting for completion. Validity is not proof: an image, drafter or speculative method can still fail when the model starts.")]
    public async Task<object> DeployModel(
        string modelId,
        string servedName,
        string repoId,
        string folder,
        string nodeId,
        string? revision = null,
        IReadOnlyList<string>? allowPatterns = null,
        [Description("Runtime that serves the model.")] RuntimeKind runtime = RuntimeKind.Vllm,
        [Description("llama.cpp: the GGUF file inside folder. It is the only file downloaded unless allowPatterns is set.")] string? modelFile = null,
        [Description("Container image, pulled when the model starts if the node lacks it. Required for sglang and nim; otherwise the node's default image. A digestPinned node refuses images it has not approved.")] string? image = null,
        [Description("sha256 digest of image. Required for nim and on digestPinned nodes; taken from the node's report when the image is already there.")] string? imageDigest = null,
        [Description("GPU indices from mirbad_list_node_runtimes. Required on a node with discrete GPUs.")] IReadOnlyList<int>? gpuIndices = null,
        [Description("Memory budget in GiB that start admission checks. Defaults to 36.")] double? memoryBudgetGiB = null,
        [Description("Extra command-line arguments for the server, one array item per flag and per value, such as [\"--tp\", \"2\"]. Flags MirbadLLM owns, such as the host and port, are refused, and mlx accepts only a fixed list that the refusal names.")] IReadOnlyList<string>? extraArgs = null,
        [Description("KV cache type. vllm: auto or fp8. llamacpp: a type such as q8_0, for keys and values. mlx: bits, 4 or 8.")] string? kvCacheDtype = null,
        [Description("vllm: reuse the KV cache across requests that share a prefix. On by default.")] bool? enablePrefixCaching = null,
        [Description("vllm: speculative method such as mtp, ngram or dflash; omit with draftFolder to let vLLM infer it from the drafter. sglang: required algorithm such as DFLASH, EAGLE, EAGLE3, STANDALONE or NGRAM. Support depends on the image and the checkpoint, and is only proven when the model starts.")] string? speculativeMethod = null,
        [Description("vllm, sglang and mlx: how much to draft per step. Required by vllm and sglang when speculative decoding is on. On sglang this is draft tokens for DFLASH and NGRAM, and steps for EAGLE, EAGLE3 and STANDALONE.")] int? speculativeTokens = null,
        [Description("vllm, sglang and mlx: folder of the draft model, such as a DFlash or DSpark drafter. The image must support that drafter.")] string? draftFolder = null,
        [Description("Hugging Face repository to download into draftFolder. Omit when the folder is already on the node.")] string? draftRepoId = null,
        CancellationToken ct = default)
    {
        RequireOperator();
        ValidateModelId(modelId);
        ValidateFolder(folder);
        if (draftFolder is not null) ValidateFolder(draftFolder);
        if (string.IsNullOrWhiteSpace(servedName) || servedName.Length > 200)
            throw new McpException("servedName is required and must be at most 200 characters.");
        if (!RepoIdRegex().IsMatch(repoId))
            throw new McpException("repoId must be a Hugging Face owner/repository identifier.");
        if (allowPatterns is { Count: > 32 } || allowPatterns?.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 200 || p.Contains("..", StringComparison.Ordinal)) == true)
            throw new McpException("allowPatterns may contain at most 32 non-empty repository-relative glob patterns without '..'.");
        if (draftRepoId is not null && (draftFolder is null || !RepoIdRegex().IsMatch(draftRepoId)))
            throw new McpException("draftRepoId must be a Hugging Face owner/repository identifier and needs draftFolder.");
        if (kvCacheDtype is not null && !SettingRegex().IsMatch(kvCacheDtype))
            throw new McpException("kvCacheDtype must be a single word of letters, digits, '_' or '-'.");
        if (speculativeMethod is not null && !SettingRegex().IsMatch(speculativeMethod))
            throw new McpException("speculativeMethod must be a single word of letters, digits, '_' or '-'.");
        if (image is not null && !ImageRegex().IsMatch(image))
            throw new McpException("image must be a container image reference such as registry/name:tag.");
        if (imageDigest is not null && (image is null || !ImageDigestRegex().IsMatch(imageDigest)))
            throw new McpException("imageDigest must be sha256: followed by 64 lowercase hex characters, and needs image.");
        if (extraArgs is { Count: > 64 } || extraArgs?.Any(a => string.IsNullOrWhiteSpace(a) || a.Length > 1000 || a.Any(char.IsControl)) == true)
            throw new McpException("extraArgs may contain at most 64 non-empty items of at most 1000 characters, without control characters.");
        RefuseIgnoredSettings(runtime, modelFile, image, kvCacheDtype, enablePrefixCaching, speculativeMethod, speculativeTokens, draftFolder);
        if (await models.GetAsync(modelId, ct).ConfigureAwait(false) is not null)
            throw new McpException($"Model '{modelId}' already exists; choose another id.");

        var selected = await Downloader(nodeId, ct).ConfigureAwait(false);
        var capabilities = nodeStatus.GetNode(selected.NodeId)?.Capabilities
            ?? (await nodes.GetAsync(selected.NodeId, ct).ConfigureAwait(false))?.LastCapabilities;
        string? digest = imageDigest ?? capabilities?.Images.FirstOrDefault(i => i.Ref == image)?.Digest;

        IReadOnlyList<string>? patterns = allowPatterns is not { Count: > 0 } && runtime == RuntimeKind.LlamaCpp && modelFile is not null ? [modelFile] : allowPatterns;
        var model = new ModelDefinition
        {
            Id = modelId,
            ServedName = servedName,
            ModelPath = folder,
            Sources = draftRepoId is null
                ? [new ModelSource(repoId, folder, revision, patterns)]
                : [new ModelSource(repoId, folder, revision, patterns), new ModelSource(draftRepoId, draftFolder!)],
            Placement = new ModelPlacement(selected.NodeId, GpuIndices: gpuIndices),
        };
        if (memoryBudgetGiB is { } budget) model = model with { MemoryBudgetGiB = budget };
        SpeculativeDecoding? speculative = runtime is not (RuntimeKind.Vllm or RuntimeKind.Sglang) || speculativeMethod is null && speculativeTokens is null && draftFolder is null
            ? null
            : new SpeculativeDecoding(speculativeMethod ?? "", speculativeTokens ?? 0)
            {
                DraftModel = draftFolder is null ? null : $"{LogicalVolumes.ModelsTarget}/{draftFolder}",
            };
        model = runtime switch
        {
            RuntimeKind.LlamaCpp => model with
            {
                Runtime = runtime,
                LlamaCpp = LlamaCppFor(selected.NodeId, capabilities, folder, modelFile, image, digest, kvCacheDtype) with { ExtraArgs = extraArgs ?? [] },
            },
            RuntimeKind.Mlx => model with
            {
                Runtime = runtime,
                ToolCallParser = null,
                ReasoningParser = null,
                Mlx = new MlxSettings { ModelFolder = folder, KvBits = MlxKvBits(kvCacheDtype), DraftModelFolder = draftFolder, NumDraftTokens = speculativeTokens, ExtraArgs = extraArgs ?? [] },
            },
            RuntimeKind.Sglang or RuntimeKind.Nim => model with
            {
                Runtime = runtime,
                ToolCallParser = null,
                ReasoningParser = null,
                Container = new ContainerRuntimeSettings { Image = image ?? "", ImageDigest = digest, ExtraArgs = extraArgs ?? [] },
                Speculative = speculative,
            },
            _ => model with
            {
                ExtraArgs = extraArgs ?? [],
                KvCacheDtype = kvCacheDtype ?? model.KvCacheDtype,
                EnablePrefixCaching = enablePrefixCaching ?? model.EnablePrefixCaching,
                Image = image,
                ImageDigest = digest,
                Speculative = speculative,
            },
        };

        try
        {
            await models.UpsertAsync(model, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message, ex);
        }

        try
        {
            // The small drafter goes first, so a refused draft leaves no large download running for a deleted model.
            DownloadStatus? draft = draftRepoId is null
                ? null
                : await selected.Value.StartAsync(new DownloadRequest(draftRepoId, Folder: draftFolder), ct).ConfigureAwait(false);
            DownloadStatus state = await selected.Value.StartAsync(new DownloadRequest(repoId, revision, Folder: folder, AllowPatterns: patterns), ct).ConfigureAwait(false);
            await Audit(AuditActions.ModelCreate, modelId, selected.NodeId, AuditResults.Accepted, "MCP deploy and download started").ConfigureAwait(false);
            string[] notes =
            [
                .. model.Speculative is not null || model.Mlx?.DraftModelFolder is not null ? [UnprovenSpeculativeNote] : Array.Empty<string>(),
                .. image is not null ? [UnprovenImageNote] : Array.Empty<string>(),
            ];
            return new { model, folder, nodeId = selected.NodeId, download = state, draftDownload = draft, planningNotes = notes, nextAction = "Poll mirbad_get_download for each folder; when complete, call mirbad_start_model." };
        }
        catch (Exception ex)
        {
            await models.DeleteAsync(modelId, CancellationToken.None).ConfigureAwait(false);
            if (ex is InvalidOperationException or ArgumentException or MirbadLLM.NodeRuntime.HuggingFaceApiException) throw new McpException(ex.Message, ex);
            throw;
        }
    }

    // A runtime would ignore these without a word, and the caller would believe the setting took effect.
    private static void RefuseIgnoredSettings(RuntimeKind runtime, string? modelFile, string? image, string? kv, bool? prefix, string? method, int? tokens, string? draft)
    {
        var ignored = new (bool Given, bool Applies, string Name)[]
        {
            (modelFile is not null, runtime == RuntimeKind.LlamaCpp, "modelFile"),
            (image is not null, runtime != RuntimeKind.Mlx, "image"),
            (kv is not null, runtime is RuntimeKind.Vllm or RuntimeKind.LlamaCpp or RuntimeKind.Mlx, "kvCacheDtype"),
            (prefix is not null, runtime == RuntimeKind.Vllm, "enablePrefixCaching"),
            (method is not null, runtime is RuntimeKind.Vllm or RuntimeKind.Sglang, "speculativeMethod"),
            (tokens is not null, runtime is RuntimeKind.Vllm or RuntimeKind.Sglang or RuntimeKind.Mlx, "speculativeTokens"),
            (draft is not null, runtime is RuntimeKind.Vllm or RuntimeKind.Sglang or RuntimeKind.Mlx, "draftFolder"),
        }.Where(s => s.Given && !s.Applies).Select(s => s.Name).ToList();
        if (ignored.Count > 0)
            throw new McpException($"The {runtime} runtime does not use {string.Join(", ", ignored)}; no state was changed.");
    }

    private static LlamaCppSettings LlamaCppFor(string nodeId, NodeCapabilities? capabilities, string folder, string? modelFile, string? image, string? digest, string? kv)
    {
        if (string.IsNullOrWhiteSpace(modelFile))
            throw new McpException("The LlamaCpp runtime needs modelFile, the GGUF file inside folder.");
        LaunchDelivery delivery = LlamaCppEditing.Runtime(capabilities)?.Delivery ?? LaunchDelivery.Container;
        if (image is not null && delivery == LaunchDelivery.Process)
            throw new McpException($"Node '{nodeId}' runs llama.cpp as a native process, so it does not use image.");
        return new LlamaCppSettings
        {
            ModelFile = $"{folder}/{modelFile}",
            Delivery = delivery,
            Image = image,
            ImageDigest = digest,
            CacheTypeK = kv,
            CacheTypeV = kv,
        };
    }

    private static int? MlxKvBits(string? kv) => kv switch
    {
        null => null,
        "4" => 4,
        "8" => 8,
        _ => throw new McpException("The Mlx runtime takes kvCacheDtype 4 or 8."),
    };

    [McpServerTool(Name = "mirbad_start_model", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Start a registered model using existing placement, ownership, and admission checks.")]
    public async Task<object> StartModel(string modelId, bool force = false, CancellationToken ct = default)
    {
        RequireOperator();
        var status = await starts.StartAsync(modelId, force, ct).ConfigureAwait(false);
        await Audit(AuditActions.ModelStart, modelId, null, AuditResults.Accepted).ConfigureAwait(false);
        return new { modelId, status, nextAction = "Poll mirbad_get_model until the state is Ready." };
    }

    [McpServerTool(Name = "mirbad_stop_model", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
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

    [McpServerTool(Name = "mirbad_delete_model", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Delete a model registration; files in the models root are not deleted.")]
    public async Task<object> DeleteModel(string modelId, CancellationToken ct = default)
    {
        RequireOperator();
        bool deleted = await models.DeleteAsync(modelId, ct).ConfigureAwait(false);
        if (!deleted) throw new McpException($"Model '{modelId}' was not found.");
        await Audit(AuditActions.ModelDelete, modelId, null, AuditResults.Succeeded).ConfigureAwait(false);
        return new { modelId, deleted = true, modelFilesDeleted = false };
    }

    [McpServerTool(Name = "mirbad_start_benchmark", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Start a benchmark job and return immediately; the model must be Ready.")]
    public async Task<object> StartBenchmark(string modelId, TuningGoal? preset = null, CancellationToken ct = default)
    {
        RequireOperator();
        var job = await jobs.StartBenchmarkAsync(modelId, preset, ct).ConfigureAwait(false);
        await Audit(AuditActions.Job, modelId, job.NodeId, AuditResults.Accepted, "MCP benchmark started").ConfigureAwait(false);
        return new { job, nextAction = "Poll mirbad_get_job or mirbad_list_jobs." };
    }

    [McpServerTool(Name = "mirbad_start_tuning", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Start a tuning job on a vllm or llamacpp model and return immediately; the winner is never applied automatically. Other runtimes are refused.")]
    public async Task<object> StartTuning(string modelId, TuningGoal goal = TuningGoal.Balanced, IReadOnlyList<TuningDimension>? dimensions = null, CancellationToken ct = default)
    {
        RequireOperator();
        JobSnapshot job;
        try
        {
            job = await jobs.StartTuningAsync(modelId, goal, dimensions, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message, ex);
        }

        await Audit(AuditActions.Job, modelId, job.NodeId, AuditResults.Accepted, "MCP tuning started").ConfigureAwait(false);
        return new { job, nextAction = "Poll mirbad_get_job; inspect mirbad_list_tuning_runs after completion." };
    }

    [McpServerTool(Name = "mirbad_cancel_job", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
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

    [McpServerTool(Name = "mirbad_apply_tuning_candidate", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Apply an accepted tuning candidate to the saved model definition without starting it.")]
    public async Task<object> ApplyTuningCandidate(string modelId, string runId, [Range(0, int.MaxValue)] int candidateIndex, CancellationToken ct = default)
    {
        RequireOperator();
        if (candidateIndex < 0) throw new McpException("candidateIndex must be non-negative.");
        ModelDefinition model;
        try
        {
            model = await jobs.ApplyTuningCandidateAsync(modelId, runId, candidateIndex, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message, ex);
        }

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

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$")]
    private static partial Regex SettingRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/@-]{0,254}$")]
    private static partial Regex ImageRegex();

    [GeneratedRegex("^sha256:[0-9a-f]{64}$")]
    private static partial Regex ImageDigestRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,95}/[A-Za-z0-9][A-Za-z0-9._-]{0,95}$")]
    private static partial Regex RepoIdRegex();
}
