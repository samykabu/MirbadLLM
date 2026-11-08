using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>
/// The ownership check of the model registry (design §6.7, MN-108), an <see cref="IModelDefinitionValidator"/> run under
/// the registry lock: while an ownership record holds a model, only its holder (<see cref="ModelWriteScope.AsOwner"/>)
/// may delete the model or change its placement (node, GPUs, placing or unplacing); anyone else gets
/// <see cref="ModelBusyException"/> (409 <c>model_busy</c> naming the holder). Other edits of a held model (settings,
/// tags) are allowed. Register it before <see cref="PlacementValidator"/>.
/// </summary>
public sealed class OwnershipValidator(IOwnershipStore ownership) : IModelDefinitionValidator
{
    /// <inheritdoc/>
    public async Task<ModelDefinition?> ValidateAsync(ModelDefinitionChange change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Stored is null)
        {
            return change.Proposed; // a create: nothing can hold a model that does not exist yet
        }

        bool placementChanges = change.IsDelete || !SamePlacementLocation(change.Stored.Placement, change.Proposed!.Placement);
        if (!placementChanges)
        {
            return change.Proposed;
        }

        var holder = await ownership.GetForModelAsync(change.ModelId, ct).ConfigureAwait(false);
        if (holder is not null && !string.Equals(holder.Id, ModelWriteScope.CurrentOwnerRecordId, StringComparison.Ordinal))
        {
            throw new ModelBusyException(change.ModelId, holder);
        }

        return change.Proposed;
    }

    /// <summary>Same node and ordered GPU UUIDs, or both unplaced.</summary>
    internal static bool SamePlacementLocation(ModelPlacement? a, ModelPlacement? b) =>
        a is null ? b is null : a.SameLocation(b);
}

/// <summary>
/// Placement validation and the placement generation (design §5.1 validation, §6.7, D75; MN-108), an
/// <see cref="IModelDefinitionValidator"/> run under the registry lock with the stored definition.
/// <para><b>Generation</b> (server-owned, <see cref="ModelPlacement.Generation"/>):</para>
/// <list type="bullet">
/// <item>A save onto a placed model must carry the stored generation. An older one (a move, unplace or GPU change landed
/// between the caller's read and its write, or a client that sends a placement without a generation once one exists)
/// is refused with <see cref="PlacementConflictException"/> (409 <c>placement_changed</c>). A newer one was never issued
/// by this master and is refused the same way. The holder of the model's ownership record is not checked: it is the
/// writer that changes the placement.</item>
/// <item>The stored generation is kept when node and GPUs stay the same, and incremented when they change. A save that
/// leaves out the placement keeps the stored placement and generation, so today's single-DGX saves (generation 0,
/// never changed) behave exactly as before.</item>
/// <item>A create starts at 0. Placing an unplaced model is a move (design §5.4): only the holder of an ownership
/// record on the model may do it (else 409 <c>placement_changed</c>: it was unplaced meanwhile), and the new generation
/// is the one it carries + 1 (a move passes its record's <see cref="OwnershipRecord.PlacementGeneration"/>, so the
/// generation keeps rising across an unplace).</item>
/// <item>Within <see cref="ModelWriteScope.ExpectStoredPlacement"/> the stored placement must still be the one read
/// (node, GPUs and generation, or still unplaced), else 409: this covers a model placed or unplaced between a
/// read-merge-write's read and its write, which the generation alone cannot see.</item>
/// </list>
/// <para><b>Validation</b> (<see cref="PlacementValidationException"/>, 400 <c>placement_invalid</c>, every problem with
/// its own message) runs when a model is created, placed, gets another node or other GPUs, or changes runtime kind or
/// delivery; a save that changes none of these is not re-validated against capabilities that may have drifted (a
/// removed GPU is reported at launch as <c>gpu_missing</c>, §5.1). It uses the node's <b>last</b> capability report
/// (<see cref="NodeRecord.LastCapabilities"/>), so a definition can be saved while its node is Offline; without any
/// report the hardware checks are skipped. Checks: the node exists, is approved and not revoked, and is not draining
/// for a new placement on it; GPU UUIDs are unique and on the node; tensor-parallel vLLM GPUs have the same model and
/// <c>memory.total</c> (llama.cpp splits layers over unequal GPUs, design-p3p4 §A.1.5); unified nodes take no GPUs and discrete nodes need at least one; GPU selection needs the agent's
/// <see cref="AgentFeatures.GpuSelectionUuids"/> feature; the runtime kind and delivery are in the node's runtimes;
/// <c>llamaCpp</c> is present exactly when the kind is llama.cpp. On a discrete node indices without UUIDs are mapped
/// to UUIDs, and the indices are refreshed from the UUIDs. Before the bootstrap node record exists (first boot, empty
/// <c>nodes.json</c>) a placement on the bootstrap node is accepted, as <see cref="Nodes.DefaultPlacement"/> places it.</para>
/// <para><b>llama.cpp</b> (MN-224): on every save of a llama.cpp model (its settings do not drift with the node's report)
/// <c>llamacpp_settings_invalid</c> (<see cref="LlamaCppArgsBuilder.Problems"/>, split mode none on several GPUs) and, when
/// placed, <c>gpu_budgets_mismatch</c> (<see cref="LlamaCppArgsBuilder.GpuBudgetProblems"/>); with the placement checks,
/// or when the pin changes, <c>executable_version_unavailable</c> for a pinned version the node's last report does not
/// list as installed and verified.</para>
/// </summary>
public sealed partial class PlacementValidator : IModelDefinitionValidator
{
    private readonly INodeRegistry _nodes;
    private readonly IOwnershipStore? _ownership;
    private readonly string _bootstrapNodeId;

    /// <summary>Creates the validator.</summary>
    /// <param name="nodes">The node registry (last capabilities, draining, revoked).</param>
    /// <param name="ownership">The ownership store (who may place an unplaced model); null = nobody may.</param>
    /// <param name="migration">Bootstrap node id; null = <see cref="RegistryMigrationOptions.DefaultBootstrapNodeId"/>.</param>
    public PlacementValidator(INodeRegistry nodes, IOwnershipStore? ownership = null, IOptions<RegistryMigrationOptions>? migration = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        _nodes = nodes;
        _ownership = ownership;
        _bootstrapNodeId = migration?.Value.EffectiveBootstrapNodeId ?? RegistryMigrationOptions.DefaultBootstrapNodeId;
    }

    /// <inheritdoc/>
    public async Task<ModelDefinition?> ValidateAsync(ModelDefinitionChange change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.IsDelete)
        {
            return null;
        }

        var stored = change.Stored;
        var proposed = change.Proposed!;
        var storedPlacement = stored?.Placement;
        var placement = proposed.Placement;

        if (ModelWriteScope.CurrentExpectation is { } expectation)
        {
            if (stored is null)
            {
                // The write updates a model it read; saving now would re-create a model deleted meanwhile.
                throw new PlacementConflictException($"Model '{proposed.Id}' was deleted meanwhile; reload the model list.");
            }

            if (!expectation.Matches(storedPlacement))
            {
                throw new PlacementConflictException(
                    $"The placement of model '{proposed.Id}' changed meanwhile (it is now {Describe(storedPlacement)}); reload it and try again.");
            }
        }

        bool isHolder = stored is not null && await IsHolderAsync(proposed.Id, ct).ConfigureAwait(false);
        RefuseStale(stored, storedPlacement, placement, isHolder, proposed.Id);

        bool newOnNode = placement is not null && !string.Equals(storedPlacement?.NodeId, placement.NodeId, StringComparison.Ordinal);
        bool mustValidate = stored is null
            || !OwnershipValidator.SamePlacementLocation(storedPlacement, placement)
            || stored.Runtime != proposed.Runtime
            || Delivery(stored) != Delivery(proposed)
            || (stored.LlamaCpp is null) != (proposed.LlamaCpp is null)
            || (stored.Mlx is null) != (proposed.Mlx is null)
            || (stored.Container is null) != (proposed.Container is null);

        // A llama.cpp model's own settings (MN-224) are checked on every save: they do not depend on the node's report, and
        // the save is the only place ExtraArgs, budgets and the split mode are refused before a launch.
        bool llamaCpp = proposed.Runtime == RuntimeKind.LlamaCpp && proposed.LlamaCpp is not null;
        bool mlx = proposed.Runtime == RuntimeKind.Mlx && proposed.Mlx is not null;
        bool sglangSpeculative = proposed.Runtime == RuntimeKind.Sglang && proposed.Speculative is not null;
        // Like llama.cpp's and MLX's: otherwise a cleared image or a reserved flag saves fine and only fails at the next start.
        bool sharedContainer = proposed.Runtime is RuntimeKind.Sglang or RuntimeKind.Nim;
        if (mustValidate || llamaCpp || mlx || sharedContainer)
        {
            var problems = new List<PlacementProblem>();
            if (sharedContainer && !mustValidate)
            {
                CheckContainerSettings(proposed, problems);
            }

            if (mustValidate)
            {
                CheckLlamaCppSettings(proposed, problems);
                CheckMlxSettings(proposed, problems);
                CheckContainerSettings(proposed, problems);
                if (placement is not null)
                {
                    placement = await ValidatePlacementAsync(proposed, placement, newOnNode, problems, ct).ConfigureAwait(false);
                }
            }
            else if (placement is not null &&
                !string.Equals(LaunchSpecBuilder.PinnedExecutableVersion(stored!), LaunchSpecBuilder.PinnedExecutableVersion(proposed), StringComparison.Ordinal) &&
                await _nodes.GetAsync(placement.NodeId, ct).ConfigureAwait(false) is { LastCapabilities: { } capabilities } node)
            {
                // A new pin on a model that stays where it is: checked against the node's last report like a placement.
                CheckExecutableVersion(node, capabilities, proposed, problems);
            }

            if (llamaCpp)
            {
                CheckLlamaCppLaunch(proposed with { Placement = placement }, problems);
            }

            if (mlx)
            {
                // MLX's own settings, like llama.cpp's, are checked on every save (they need no node report).
                problems.AddRange(MlxArgsBuilder.Problems(proposed).Where(p => p.Code != PlacementProblemCodes.MlxSettingsMismatch));
            }

            if (sglangSpeculative && SglangSpeculativeArgs.Problem(proposed) is { } speculativeProblem)
            {
                problems.Add(new PlacementProblem(PlacementProblemCodes.ContainerSettingsInvalid, speculativeProblem));
            }

            if (problems.Count > 0)
            {
                throw new PlacementValidationException(problems);
            }
        }

        if (placement is null)
        {
            return proposed;
        }

        return proposed with { Placement = placement with { Generation = NextGeneration(stored, storedPlacement, placement) } };
    }

    private async Task<bool> IsHolderAsync(string modelId, CancellationToken ct)
    {
        if (_ownership is null || ModelWriteScope.CurrentOwnerRecordId is not { } recordId)
        {
            return false;
        }

        var holder = await _ownership.GetForModelAsync(modelId, ct).ConfigureAwait(false);
        return holder is not null && string.Equals(holder.Id, recordId, StringComparison.Ordinal);
    }

    /// <summary>Refuses a save whose placement is not based on the stored one (D75). Runs before validation: a stale save is reloaded, not fixed.</summary>
    private static void RefuseStale(ModelDefinition? stored, ModelPlacement? storedPlacement, ModelPlacement? placement, bool isHolder, string modelId)
    {
        if (placement is null || stored is null || isHolder)
        {
            return; // unplacing (ownership decides), a create, or the holder's own write
        }

        if (storedPlacement is null)
        {
            throw new PlacementConflictException(
                $"Model '{modelId}' is unplaced (it was unplaced meanwhile, or never placed); placing it is a move " +
                $"(POST /api/models/{modelId}/move). Reload it and try again.");
        }

        if (placement.Generation != storedPlacement.Generation)
        {
            if (placement.Generation == 0)
            {
                // Most often a client that does not know the field (a pre-P2 script echoing the placement it read without
                // it, or a hand-written body): say how to save instead of suggesting a concurrent change.
                throw new PlacementConflictException(
                    $"The placement of model '{modelId}' is at generation {storedPlacement.Generation} and this save carries none (0): " +
                    $"send placement.generation as read from GET /api/models/{modelId}, or leave placement out to keep it. " +
                    "If you did read it at generation 0, it changed meanwhile; reload it and try again.");
            }

            throw new PlacementConflictException(placement.Generation < storedPlacement.Generation
                ? $"The placement of model '{modelId}' changed meanwhile (generation {storedPlacement.Generation}, this save carries " +
                  $"{placement.Generation}); reload it and try again."
                : $"The placement generation {placement.Generation} of model '{modelId}' was not issued by this master (stored: " +
                  $"{storedPlacement.Generation}); reload it and try again.");
        }
    }

    /// <summary>The generation to store with the (validated) <paramref name="placement"/>.</summary>
    private static long NextGeneration(ModelDefinition? stored, ModelPlacement? storedPlacement, ModelPlacement placement)
    {
        if (stored is null)
        {
            return 0; // a create: never changed
        }

        if (storedPlacement is null)
        {
            return Math.Max(placement.Generation, 0) + 1; // the holder places an unplaced model (a move or restore)
        }

        return storedPlacement.SameLocation(placement) ? storedPlacement.Generation : storedPlacement.Generation + 1;
    }

    private async Task<ModelPlacement> ValidatePlacementAsync(
        ModelDefinition model, ModelPlacement placement, bool newOnNode, List<PlacementProblem> problems, CancellationToken ct)
    {
        var uuids = placement.GpuUuids ?? [];
        var indices = placement.GpuIndices ?? [];
        foreach (string duplicate in uuids.GroupBy(u => u, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key))
        {
            problems.Add(new PlacementProblem(
                PlacementProblemCodes.GpuDuplicate, $"GPU {duplicate} is listed more than once; pick each GPU once.", duplicate));
        }

        var node = await _nodes.GetAsync(placement.NodeId, ct).ConfigureAwait(false);
        if (node is null)
        {
            if (string.Equals(placement.NodeId, _bootstrapNodeId, StringComparison.Ordinal) &&
                !(await _nodes.ListAsync(ct).ConfigureAwait(false)).Any(n => !n.Revoked))
            {
                return placement; // first boot: the bootstrap node record is not written yet (DefaultPlacement)
            }

            problems.Add(new PlacementProblem(PlacementProblemCodes.NodeUnknown, $"Node '{placement.NodeId}' does not exist."));
            return placement;
        }

        if (node.Revoked)
        {
            problems.Add(new PlacementProblem(
                PlacementProblemCodes.NodeRevoked, $"Node '{node.Id}' is revoked; models cannot be placed there."));
            return placement;
        }

        if (!node.Approved)
        {
            problems.Add(new PlacementProblem(
                PlacementProblemCodes.NodeUnknown, $"Node '{node.Id}' is waiting for approval; approve it before placing models there."));
            return placement;
        }

        if (node.Draining && newOnNode)
        {
            problems.Add(new PlacementProblem(
                PlacementProblemCodes.NodeDraining, $"Node '{node.Id}' is draining; it takes no new placements until it is undrained."));
        }

        if (node.LastCapabilities is not { } capabilities)
        {
            return placement; // never reported: hardware checks need a report (validation uses the last one, §5.1)
        }

        placement = capabilities.Memory.Model == MemoryModel.Unified
            ? CheckUnified(node, placement, uuids, indices, problems)
            : CheckDiscrete(node, capabilities, model, placement, uuids, indices, problems);

        if (CheckRuntime(node, capabilities, model, problems))
        {
            CheckExecutableVersion(node, capabilities, model, problems);
        }

        return placement;
    }

    private static ModelPlacement CheckUnified(
        NodeRecord node, ModelPlacement placement, IReadOnlyList<string> uuids, IReadOnlyList<int> indices, List<PlacementProblem> problems)
    {
        if (uuids.Count > 0 || indices.Count > 0)
        {
            problems.Add(new PlacementProblem(
                PlacementProblemCodes.GpusOnUnifiedNode,
                $"Node '{node.Id}' has unified memory: a model there uses the whole node, so GPUs cannot be selected; clear the GPU selection."));
        }

        return placement;
    }

    private static ModelPlacement CheckDiscrete(
        NodeRecord node,
        NodeCapabilities capabilities,
        ModelDefinition model,
        ModelPlacement placement,
        IReadOnlyList<string> uuids,
        IReadOnlyList<int> indices,
        List<PlacementProblem> problems)
    {
        var gpus = capabilities.Gpus;
        if (uuids.Count == 0 && indices.Count > 0)
        {
            // Selected by index (editor, API): the identity saved is the UUID of the card at that index now (§5.1).
            var mapped = new List<string>();
            foreach (int index in indices)
            {
                if (gpus.FirstOrDefault(g => g.Index == index) is { } gpu)
                {
                    mapped.Add(gpu.Uuid);
                }
                else
                {
                    problems.Add(new PlacementProblem(
                        PlacementProblemCodes.GpuUnknown, $"Node '{node.Id}' has no GPU {index.ToString(CultureInfo.InvariantCulture)}."));
                }
            }

            uuids = mapped;
            if (problems.Count == 0)
            {
                foreach (string duplicate in mapped.GroupBy(u => u, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key))
                {
                    problems.Add(new PlacementProblem(
                        PlacementProblemCodes.GpuDuplicate, $"GPU {duplicate} is listed more than once; pick each GPU once.", duplicate));
                }
            }
        }

        if (uuids.Count == 0)
        {
            problems.Add(new PlacementProblem(
                PlacementProblemCodes.GpusRequired, $"Node '{node.Id}' has discrete GPUs; select at least one GPU for the model."));
            return placement;
        }

        var selected = new List<GpuCapability>();
        foreach (string uuid in uuids)
        {
            if (gpus.FirstOrDefault(g => string.Equals(g.Uuid, uuid, StringComparison.Ordinal)) is { } gpu)
            {
                selected.Add(gpu);
            }
            else
            {
                problems.Add(new PlacementProblem(
                    PlacementProblemCodes.GpuUnknown,
                    $"GPU {uuid} is not on node '{node.Id}' (as of its last report); select one of its GPUs.",
                    uuid));
            }
        }

        // The equal-GPU rule is vLLM's tensor parallelism (D25); llama.cpp splits layers over unequal GPUs by its tensor
        // split and per-GPU budgets (design-p3p4 §A.1.5, MN-224).
        var distinct = selected.DistinctBy(g => g.Uuid, StringComparer.Ordinal).ToList();
        if (distinct.Count > 1 && model.Runtime != RuntimeKind.LlamaCpp)
        {
            var first = distinct[0];
            foreach (var other in distinct.Skip(1))
            {
                if (!string.Equals(other.Name, first.Name, StringComparison.Ordinal) || other.MemoryTotalGiB != first.MemoryTotalGiB)
                {
                    problems.Add(new PlacementProblem(
                        PlacementProblemCodes.GpuCapacityMismatch,
                        $"Tensor parallelism needs GPUs of the same model and memory: GPU {Index(first)} is {Card(first)}, GPU " +
                        $"{Index(other)} is {Card(other)}. Select GPUs of one kind, or a single GPU.",
                        other.Uuid));
                }
            }
        }

        if (!capabilities.Features.Contains(AgentFeatures.GpuSelectionUuids, StringComparer.Ordinal))
        {
            problems.Add(new PlacementProblem(
                PlacementProblemCodes.RuntimeUnavailable,
                $"The agent on node '{node.Id}' cannot run a model on selected GPUs (it does not report the " +
                $"'{AgentFeatures.GpuSelectionUuids}' feature); upgrade the agent."));
        }

        // The saved identity is the UUID; indices are a display aid, refreshed from the last report (§5.1).
        return selected.Count == uuids.Count
            ? placement with { GpuUuids = [.. uuids], GpuIndices = [.. selected.Select(g => g.Index)] }
            : placement with { GpuUuids = [.. uuids] };
    }

    /// <summary>Adds <c>runtime_unavailable</c> when the node does not report the model's runtime and delivery; true when it does.</summary>
    private static bool CheckRuntime(NodeRecord node, NodeCapabilities capabilities, ModelDefinition model, List<PlacementProblem> problems)
    {
        var delivery = Delivery(model);
        if (!capabilities.Runtimes.Any(r => r.Kind == model.Runtime && r.Delivery == delivery))
        {
            problems.Add(new PlacementProblem(
                PlacementProblemCodes.RuntimeUnavailable,
                $"Node '{node.Id}' cannot run {RuntimeName(model.Runtime)} as a {DeliveryName(delivery)} (its last report lists " +
                $"{(capabilities.Runtimes.Count == 0 ? "no runtimes" : string.Join(", ", capabilities.Runtimes.Select(r => RuntimeName(r.Kind) + " " + DeliveryName(r.Delivery))))})."));
            return false;
        }

        return true;
    }

    /// <summary>
    /// <c>executable_version_unavailable</c> (design-p3p4 §A.1.2, MN-224): a llama.cpp model that pins
    /// <see cref="LlamaCppSettings.ExecutableVersion"/> needs that version installed and verified on the node, as its last
    /// report lists it (<see cref="RuntimeCapability.Versions"/> of the runtime and delivery). Without a pin the launch uses
    /// the node's default version.
    /// </summary>
    private static void CheckExecutableVersion(NodeRecord node, NodeCapabilities capabilities, ModelDefinition model, List<PlacementProblem> problems)
    {
        if (model.Runtime is not (RuntimeKind.LlamaCpp or RuntimeKind.Mlx) ||
            LaunchSpecBuilder.PinnedExecutableVersion(model) is not { Length: > 0 } pinned)
        {
            return;
        }

        var delivery = Delivery(model);
        var versions = capabilities.Runtimes
            .Where(r => r.Kind == model.Runtime && r.Delivery == delivery)
            .SelectMany(r => r.Versions ?? [])
            .ToList();
        if (versions.Exists(v => v.Verified && string.Equals(v.Version, pinned, StringComparison.Ordinal)))
        {
            return;
        }

        string reported = versions.Count == 0
            ? "none"
            : string.Join(", ", versions.Select(v => v.Version + (v.Verified ? string.Empty : " (not verified)")));
        problems.Add(new PlacementProblem(
            PlacementProblemCodes.ExecutableVersionUnavailable,
            $"Model '{model.Id}' pins {(model.Runtime == RuntimeKind.Mlx ? ExecutableNames.MlxLmServer : ExecutableNames.LlamaServer)} {pinned}, which node '{node.Id}' does not report as installed and verified " +
            $"(it reports {reported}); install that version on the node, or pin one it has (or none, for its default)."));
    }

    /// <summary>
    /// The launch checks of a llama.cpp model (design-p3p4 §A.1.5, §A.1.6; MN-224), from its settings and placement alone:
    /// <c>llamacpp_settings_invalid</c> (<see cref="LlamaCppArgsBuilder.Problems"/>: paths, sizes, pooling, chat template,
    /// refused ExtraArgs incl. the typed flags; and split mode none on several GPUs) and, for a placed model,
    /// <c>gpu_budgets_mismatch</c> (<see cref="LlamaCppArgsBuilder.GpuBudgetProblems"/> against the placed GPUs).
    /// </summary>
    private static void CheckLlamaCppLaunch(ModelDefinition model, List<PlacementProblem> problems)
    {
        problems.AddRange(LlamaCppArgsBuilder.Problems(model).Where(p => p.Code != PlacementProblemCodes.LlamaCppSettingsMismatch));
        int gpuCount = model.Placement?.GpuUuids?.Count ?? 0;
        if (gpuCount > 1 && model.LlamaCpp!.EffectiveSplitMode(gpuCount) == LlamaCppSplitMode.None)
        {
            problems.Add(new PlacementProblem(
                PlacementProblemCodes.LlamaCppSettingsInvalid,
                $"Model '{model.Id}' is placed on {gpuCount.ToString(CultureInfo.InvariantCulture)} GPUs with split mode 'none', which " +
                "would use only the first; choose split mode 'layer' or place it on one GPU."));
        }

        if (model.Placement is not null)
        {
            problems.AddRange(LlamaCppArgsBuilder.GpuBudgetProblems(model, gpuCount));
        }
    }

    private static void CheckLlamaCppSettings(ModelDefinition model, List<PlacementProblem> problems)
    {
        if (model.Runtime == RuntimeKind.LlamaCpp && model.LlamaCpp is null)
        {
            problems.Add(new PlacementProblem(
                PlacementProblemCodes.LlamaCppSettingsMismatch, "A llama.cpp model needs its llama.cpp settings (model file, context size)."));
        }
        else if (model.Runtime != RuntimeKind.LlamaCpp && model.LlamaCpp is not null)
        {
            problems.Add(new PlacementProblem(
                PlacementProblemCodes.LlamaCppSettingsMismatch,
                $"llama.cpp settings are given but the runtime is {RuntimeName(model.Runtime)}; remove them or switch the runtime to llama.cpp."));
        }
    }

    /// <summary>
    /// <c>mlx_settings_mismatch</c> (docs/macos/implementation-plan.md, Phase 3): an MLX model needs its MLX settings, and
    /// MLX settings on another runtime are refused, as for llama.cpp.
    /// </summary>
    private static void CheckMlxSettings(ModelDefinition model, List<PlacementProblem> problems)
    {
        if (model.Runtime == RuntimeKind.Mlx && model.Mlx is null)
        {
            problems.Add(new PlacementProblem(
                PlacementProblemCodes.MlxSettingsMismatch, "An MLX model needs its MLX settings (the checkpoint folder)."));
        }
        else if (model.Runtime != RuntimeKind.Mlx && model.Mlx is not null)
        {
            problems.Add(new PlacementProblem(
                PlacementProblemCodes.MlxSettingsMismatch,
                $"MLX settings are given but the runtime is {RuntimeName(model.Runtime)}; remove them or switch the runtime to MLX."));
        }
    }

    private static void CheckContainerSettings(ModelDefinition model, List<PlacementProblem> problems)
    {
        bool sharedContainer = model.Runtime is RuntimeKind.Sglang or RuntimeKind.Nim;
        if (sharedContainer && model.Container is null)
        {
            problems.Add(new PlacementProblem(
                PlacementProblemCodes.ContainerSettingsMismatch, $"A {RuntimeName(model.Runtime)} model needs container settings."));
            return;
        }

        if (!sharedContainer && model.Container is not null)
        {
            problems.Add(new PlacementProblem(
                PlacementProblemCodes.ContainerSettingsMismatch,
                $"Container runtime settings are given but the runtime is {RuntimeName(model.Runtime)}; remove them."));
            return;
        }

        if (model.Container is not { } settings)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.Image) ||
            model.Runtime == RuntimeKind.Nim && string.IsNullOrWhiteSpace(settings.ImageDigest) ||
            !LlamaCppArgsBuilder.IsRelativePath(model.ModelPath))
        {
            problems.Add(new PlacementProblem(
                PlacementProblemCodes.ContainerSettingsInvalid,
                $"{RuntimeName(model.Runtime)} needs a relative model path, an explicit image{(model.Runtime == RuntimeKind.Nim ? " and approved digest" : string.Empty)}."));
        }

        if (settings.ImageDigest is { } digest && !ContainerDigestPattern().IsMatch(digest))
        {
            problems.Add(new PlacementProblem(
                PlacementProblemCodes.ContainerSettingsInvalid, $"Image digest '{digest}' must be sha256: followed by 64 lowercase hex characters."));
        }

        if (LaunchSpecBuilder.ContainerExtraArgProblem(model.Runtime, settings.ExtraArgs) is { } argProblem)
        {
            problems.Add(new PlacementProblem(PlacementProblemCodes.ContainerSettingsInvalid, argProblem));
        }
    }

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerDigestPattern();

    /// <summary>How the model is delivered: llama.cpp's setting, MLX always as a native process, all others in containers.</summary>
    private static LaunchDelivery Delivery(ModelDefinition model) => model.Runtime switch
    {
        RuntimeKind.LlamaCpp => model.LlamaCpp?.Delivery ?? LaunchDelivery.Container,
        RuntimeKind.Mlx => LaunchDelivery.Process,
        _ => LaunchDelivery.Container,
    };

    private static string RuntimeName(RuntimeKind kind) => kind switch
    {
        RuntimeKind.Vllm => "vLLM",
        RuntimeKind.LlamaCpp => "llama.cpp",
        RuntimeKind.Mlx => "MLX",
        RuntimeKind.Sglang => "SGLang",
        RuntimeKind.Nim => "NIM",
        _ => kind.ToString(),
    };

    private static string DeliveryName(LaunchDelivery delivery) => delivery == LaunchDelivery.Process ? "native process" : "container";

    private static string Index(GpuCapability gpu) => gpu.Index.ToString(CultureInfo.InvariantCulture);

    private static string Card(GpuCapability gpu) =>
        gpu.MemoryTotalGiB is { } total
            ? $"{gpu.Name} {total.ToString("0.#", CultureInfo.InvariantCulture)} GiB"
            : gpu.Name;

    private static string Describe(ModelPlacement? placement) =>
        placement is null
            ? "unplaced"
            : $"on node '{placement.NodeId}', generation {placement.Generation.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>DI helpers for placement validation and ownership (for the P2 composition, MN-130).</summary>
public static class PlacementServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="OwnershipStore"/> as <see cref="IOwnershipStore"/> and, in this order, <see cref="OwnershipValidator"/>
    /// and <see cref="PlacementValidator"/> as <see cref="IModelDefinitionValidator"/>s. The registry must then be built
    /// with <c>sp.GetServices&lt;IModelDefinitionValidator&gt;()</c> (<c>JsonModelRegistry</c>'s validator constructor, D76).
    /// Expects <see cref="INodeRegistry"/> and <c>IOptions&lt;MirbadLLMOptions&gt;</c>; uses a <see cref="TimeProvider"/>
    /// and <c>IOptions&lt;RegistryMigrationOptions&gt;</c> when registered.
    /// </summary>
    public static IServiceCollection AddPlacementValidation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp => new OwnershipStore(
            sp.GetRequiredService<IOptions<MirbadLLMOptions>>(), sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IOwnershipStore>(sp => sp.GetRequiredService<OwnershipStore>());
        services.AddSingleton<IModelDefinitionValidator>(sp => new OwnershipValidator(sp.GetRequiredService<IOwnershipStore>()));
        services.AddSingleton<IModelDefinitionValidator>(sp => new PlacementValidator(
            sp.GetRequiredService<INodeRegistry>(), sp.GetRequiredService<IOwnershipStore>(), sp.GetService<IOptions<RegistryMigrationOptions>>()));
        return services;
    }
}
