using System.Globalization;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Nodes;

namespace MirbadLLM.Web;

/// <summary>
/// One node as the model editor's Placement section and move dialog see it (design §12.2): the stored record (last
/// capabilities, draining, reserves) and the master's live view (state), when the status cache is composed.
/// </summary>
/// <param name="Record">The <c>nodes.json</c> record.</param>
/// <param name="Live">The live view; null when the master has none (not composed, or not watched yet).</param>
public sealed record PlacementNode(NodeRecord Record, NodeLiveStatus? Live)
{
    /// <summary>Node id.</summary>
    public string Id => Record.Id;

    /// <summary>"Office PC (pc-office)", or the id when the display name is the id or missing.</summary>
    public string Label =>
        string.IsNullOrWhiteSpace(Record.DisplayName) || string.Equals(Record.DisplayName, Record.Id, StringComparison.Ordinal)
            ? Record.Id
            : $"{Record.DisplayName} ({Record.Id})";

    /// <summary>The live state; null when unknown (no live view).</summary>
    public NodeState? State => Live?.State;

    /// <summary>True when the router may send traffic and the master may send commands; unknown counts as serving.</summary>
    public bool Serving => Live?.IsServing ?? true;

    /// <summary>Draining: no new placements or moves to it (design §3.5).</summary>
    public bool Draining => Record.Draining || Live?.Draining == true;

    /// <summary>The node's memory model from its last capability report (unified when it has none).</summary>
    public MemoryModel Memory => (Record.LastCapabilities ?? Live?.Capabilities)?.Memory.Model ?? MemoryModel.Unified;

    /// <summary>True on a discrete-GPU node: the placement picks GPUs.</summary>
    public bool Discrete => Memory == MemoryModel.Discrete;

    /// <summary>True when the node's last report says it has no usable GPU and runs llama.cpp on the CPU.</summary>
    public bool CpuOnly => NodeAccelerators.IsCpu(Record.LastCapabilities ?? Live?.Capabilities);

    /// <summary>True when the node's last report says its GPUs run through llama.cpp's Vulkan build (experimental).</summary>
    public bool Vulkan => NodeAccelerators.IsVulkan(Record.LastCapabilities ?? Live?.Capabilities);

    /// <summary>The node's GPUs in index order (last capability report).</summary>
    public IReadOnlyList<GpuCapability> Gpus =>
        [.. ((Record.LastCapabilities ?? Live?.Capabilities)?.Gpus ?? []).OrderBy(g => g.Index)];

    /// <summary>The runtime kinds the node reports (vLLM when it reports none).</summary>
    public IReadOnlyList<RuntimeKind> RuntimeKinds =>
        ((Record.LastCapabilities ?? Live?.Capabilities)?.Runtimes ?? []).Select(r => r.Kind).Distinct()
            .DefaultIfEmpty(RuntimeKind.Vllm).ToList();
}

/// <summary>A message for the editor's callouts: one sentence plus, when there are several causes, one line each.</summary>
/// <param name="Text">The sentence.</param>
/// <param name="Details">One line per problem (placement validation); empty otherwise.</param>
public sealed record EditorMessage(string Text, IReadOnlyList<string> Details)
{
    /// <summary>A message without details.</summary>
    public static EditorMessage Of(string text) => new(text, []);

    /// <inheritdoc/>
    public override string ToString() => Details.Count == 0 ? Text : Text + " " + string.Join(" ", Details);
}

/// <summary>
/// The model editor's placement rules and words (design §5.1, §6.3, §12.2; MN-118), kept out of the page so they are
/// testable on their own: which nodes are offered, GPU selection (stored as UUIDs, shown with the current indices,
/// single-GPU or equal-capacity tensor parallel only), the placement a save sends back (with the stored generation, D75),
/// and the final wording of every refusal the editor shows (W7 review MN-126).
/// </summary>
public static partial class PlacementEditing
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>
    /// True when the master has two or more approved, non-revoked nodes (<see cref="NodeFleet"/>; a node waiting for
    /// approval does not count), the only case the editor shows a Placement section; with
    /// the single DGX it looks and behaves as before P2 (the node is shown read-only).
    /// </summary>
    public static bool IsMultiNode(IReadOnlyList<NodeRecord> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        return NodeFleet.IsMultiNode(nodes); // approved, non-revoked members (W9 review MN-132, D102)
    }

    /// <summary>
    /// The nodes the editor lists (design §12.2): approved, not revoked, Online, Degraded or Offline (or not watched yet);
    /// waiting-for-approval, incompatible and pending-takeover nodes are left out, except <paramref name="currentNodeId"/>
    /// (the model's own node), which is always listed. Sorted by id.
    /// </summary>
    public static IReadOnlyList<PlacementNode> Offered(IReadOnlyList<NodeRecord> nodes, Func<string, NodeLiveStatus?> live, string? currentNodeId)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(live);
        return
        [
            .. nodes
                .Where(n => !n.Revoked)
                .Select(n => new PlacementNode(n, live(n.Id)))
                .Where(n => string.Equals(n.Id, currentNodeId, StringComparison.Ordinal) ||
                    (n.Record.Approved && n.State is null or NodeState.Online or NodeState.Degraded or NodeState.Offline))
                .OrderBy(n => n.Id, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Why <paramref name="node"/> cannot take a new placement (a new model or a move); null when it can. With
    /// <paramref name="runtime"/> (a move keeps the model's runtime), a node that does not report that runtime is refused
    /// too: the master would answer <c>runtime_unavailable</c> anyway, and the dialog used to let it be picked (2026-10-15).
    /// </summary>
    public static string? PlacementRefusal(PlacementNode node, bool needsServing, RuntimeKind? runtime = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (runtime is { } kind && !node.RuntimeKinds.Contains(kind))
        {
            string runs = string.Join(" and ", node.RuntimeKinds.Select(LlamaCppEditing.RuntimeName));
            return $"{node.Id} runs {runs} only, so it cannot run this {LlamaCppEditing.RuntimeName(kind)} model. " +
                $"A move keeps the runtime: to serve it on {node.Id}, add a {runs} model for it there.";
        }

        if (node.Draining)
        {
            return $"{node.Id} is draining: it takes no new models until it is undrained.";
        }

        if (needsServing && !node.Serving)
        {
            return $"{node.Id} is {StateText(node.State, false)}: the checkpoint is downloaded there first, so it must be online.";
        }

        return null;
    }

    /// <summary>The few words a disabled option carries, since a disabled option cannot be picked to read the hint.</summary>
    public static string ShortRefusal(PlacementNode node, bool needsServing, RuntimeKind? runtime = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (runtime is { } kind && !node.RuntimeKinds.Contains(kind))
        {
            return $"no {LlamaCppEditing.RuntimeName(kind)} runtime";
        }

        return node.Draining ? "draining" : needsServing && !node.Serving ? "not online" : "not available";
    }

    /// <summary>The state in the editor's words ("online", "degraded", "offline · draining", "state unknown").</summary>
    public static string StateText(NodeState? state, bool draining)
    {
        string text = state switch
        {
            NodeState.Online => "online",
            NodeState.Degraded => "degraded",
            NodeState.Offline => "offline",
            NodeState.Incompatible => "incompatible",
            NodeState.PendingApproval => "waiting for approval",
            NodeState.PendingTakeover => "pending takeover",
            NodeState.Revoked => "revoked",
            _ => "state unknown",
        };
        return draining ? text + " · draining" : text;
    }

    /// <summary>
    /// The fit preview's line with what it measures named (MN-239 item 2, D130): the model's memory budget — the number
    /// admission uses — against the node's free memory. On a llama.cpp model the GGUF picker shows a second verdict on
    /// the same page from the file's estimate (<see cref="LlamaCppEditing.NeedText"/>), and the two can disagree (a
    /// 5 GiB budget per GPU against a 9.3 GiB estimate), so neither verdict is shown unlabelled. Empty for no preview.
    /// </summary>
    public static string FitLine(NodeFitResult? fit) => fit is null ? "" : "Memory budget: " + fit.Message;

    /// <summary>The fit verdict for a node option: "fits", "does not fit now", "fit unknown".</summary>
    public static string FitWord(NodeFitResult? fit) => fit?.Fits switch
    {
        true => "fits",
        false => "does not fit now",
        _ => "fit unknown",
    };

    /// <summary>"GPU 0 · NVIDIA GeForce RTX 5060 Ti · 16 GiB" (current index, name, VRAM).</summary>
    public static string GpuLabel(GpuCapability gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        string total = gpu.MemoryTotalGiB is { } t ? " · " + UiFormat.GiB(t) : "";
        return string.Create(Culture, $"GPU {gpu.Index} · {gpu.Name}{total}");
    }

    /// <summary>
    /// True when <paramref name="candidate"/> may join <paramref name="selected"/>: tensor parallel needs GPUs of the same
    /// model and the same <c>memory.total</c> (design §5.1, D25); a single GPU has no restriction.
    /// </summary>
    public static bool Compatible(GpuCapability candidate, IEnumerable<GpuCapability> selected)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(selected);
        return selected.All(s =>
            string.Equals(s.Uuid, candidate.Uuid, StringComparison.Ordinal) ||
            (string.Equals(s.Name, candidate.Name, StringComparison.Ordinal) && Nullable.Equals(s.MemoryTotalGiB, candidate.MemoryTotalGiB)));
    }

    /// <summary>
    /// The selection after <paramref name="uuid"/> is ticked or unticked: in current index order, or in the stored order
    /// when the same GPUs as <paramref name="stored"/> are selected (so re-ticking the stored set is no change). A GPU
    /// that does not fit the equal-capacity rule is not added.
    /// </summary>
    public static IReadOnlyList<string> Toggle(
        IReadOnlyList<string> selected, string uuid, bool on, IReadOnlyList<GpuCapability> gpus, IReadOnlyList<string>? stored)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(gpus);
        var set = new HashSet<string>(selected, StringComparer.Ordinal);
        if (on)
        {
            var candidate = gpus.FirstOrDefault(g => string.Equals(g.Uuid, uuid, StringComparison.Ordinal));
            if (candidate is null || !Compatible(candidate, gpus.Where(g => set.Contains(g.Uuid))))
            {
                return selected;
            }

            set.Add(uuid);
        }
        else
        {
            set.Remove(uuid);
        }

        if (stored is { Count: > 0 } && set.SetEquals(stored))
        {
            return [.. stored];
        }

        var index = gpus.ToDictionary(g => g.Uuid, g => g.Index, StringComparer.Ordinal);
        return [.. set.OrderBy(u => index.TryGetValue(u, out int i) ? i : int.MaxValue).ThenBy(u => u, StringComparer.Ordinal)];
    }

    /// <summary>The selected GPUs that are no longer on the node (card removed or replaced, design §5.1).</summary>
    public static IReadOnlyList<string> Missing(IReadOnlyList<string> selected, IReadOnlyList<GpuCapability> gpus)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(gpus);
        return [.. selected.Where(u => !gpus.Any(g => string.Equals(g.Uuid, u, StringComparison.Ordinal)))];
    }

    /// <summary>
    /// The placement a save sends (design §5.1, D75): the node, the selected GPU UUIDs in order (null when none) with
    /// their current indices for display, and <paramref name="generation"/>, the stored generation the edit is based on.
    /// </summary>
    public static ModelPlacement Build(string nodeId, IReadOnlyList<string> gpuUuids, IReadOnlyList<GpuCapability> gpus, long generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(gpuUuids);
        ArgumentNullException.ThrowIfNull(gpus);
        if (gpuUuids.Count == 0)
        {
            return new ModelPlacement(nodeId, Generation: generation);
        }

        var indices = gpuUuids.Select(u => gpus.FirstOrDefault(g => string.Equals(g.Uuid, u, StringComparison.Ordinal))?.Index).ToList();
        return new ModelPlacement(
            nodeId,
            [.. gpuUuids],
            indices.TrueForAll(i => i is not null) ? [.. indices.Select(i => i!.Value)] : null,
            generation);
    }

    /// <summary>True when the editor's selection differs from the stored GPUs (an edit that needs a restart, §6.3).</summary>
    public static bool GpusChanged(ModelPlacement? stored, IReadOnlyList<string> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return !(stored?.GpuUuids ?? []).SequenceEqual(selected, StringComparer.Ordinal);
    }

    /// <summary>"GPU 0 and GPU 1" from the node's current indices ("GPU-…" for a card that is gone).</summary>
    public static string GpuList(IReadOnlyList<string> uuids, IReadOnlyList<GpuCapability> gpus)
    {
        ArgumentNullException.ThrowIfNull(uuids);
        ArgumentNullException.ThrowIfNull(gpus);
        var names = uuids
            .Select(u => gpus.FirstOrDefault(g => string.Equals(g.Uuid, u, StringComparison.Ordinal)) is { } gpu
                ? string.Create(Culture, $"GPU {gpu.Index}")
                : u + " (missing)")
            .ToList();
        return names.Count switch
        {
            0 => "no GPU",
            1 => names[0],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        };
    }

    /// <summary>
    /// Why <paramref name="digest"/> is not an image digest the registry stores (<c>sha256:</c> + 64 lowercase hex, D85);
    /// null when it is one or blank.
    /// </summary>
    public static string? DigestProblem(string? digest) =>
        string.IsNullOrWhiteSpace(digest) || DigestPattern().IsMatch(digest.Trim())
            ? null
            : "Use sha256: followed by 64 lowercase hex characters, as the registry stores it.";

    [System.Text.RegularExpressions.GeneratedRegex("^sha256:[0-9a-f]{64}$")]
    private static partial System.Text.RegularExpressions.Regex DigestPattern();

    /// <summary>"on node pc-office, GPU 0 and GPU 1", "on node dgx", "unplaced".</summary>
    public static string Describe(ModelPlacement? placement, IReadOnlyList<GpuCapability>? gpus = null)
    {
        if (placement is null)
        {
            return "unplaced";
        }

        return placement.GpuUuids is { Count: > 0 } uuids
            ? $"on node {placement.NodeId}, {GpuList(uuids, gpus ?? [])}"
            : $"on node {placement.NodeId}";
    }
}

/// <summary>
/// The final wording of the refusals the model editor and its dialogs show (W7 review MN-126, MN-118): what was not done,
/// why, and what to do next, in the words of the page. Every message names the model.
/// </summary>
public static class EditorRefusals
{
    /// <summary>A save the registry refused (D75, D76, D87).</summary>
    /// <param name="ex">The refusal.</param>
    /// <param name="modelId">The model.</param>
    /// <param name="current">The model as stored now (re-read after a <c>placement_changed</c>); null when deleted or unknown.</param>
    /// <param name="currentGpus">The GPUs of the node it is placed on now, for the indices.</param>
    /// <param name="placementSection">True when the page shows the Placement section (several nodes); the single DGX shows
    /// the node read-only instead.</param>
    public static EditorMessage Save(
        Exception ex, string modelId, ModelDefinition? current = null, IReadOnlyList<GpuCapability>? currentGpus = null, bool placementSection = true)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex switch
        {
            PlacementConflictException when current is null => EditorMessage.Of(
                $"Not saved: {modelId} was deleted while this page was open. Go back to Models to add it again."),
            PlacementConflictException => EditorMessage.Of(
                $"Not saved: the placement of {modelId} changed while this page was open (a move, an unplace or another " +
                $"editor); it is now {PlacementEditing.Describe(current!.Placement, currentGpus)}. " +
                (placementSection ? "The Placement section shows" : "The page shows") +
                " it now and your other edits are still in the form: check them and save again."),
            PlacementValidationException invalid => new EditorMessage(
                $"Not saved: the placement of {modelId} was refused.",
                invalid.Problems.Count > 0 ? [.. invalid.Problems.Select(p => p.Message)] : [invalid.Message]),
            ModelBusyException busy => EditorMessage.Of(
                $"Not saved: {Holder(busy)} holds {modelId}, so its placement cannot change until that ends. " +
                "Undo the placement change to save your other edits now, or save again when it has finished."),
            _ => EditorMessage.Of(ex.Message),
        };
    }

    /// <summary>A delete the registry refused (a job, move, restore or unplace holds the model, D87).</summary>
    public static EditorMessage Delete(Exception ex, string modelId)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex is ModelBusyException busy
            ? EditorMessage.Of(
                $"Not deleted: {Holder(busy)} holds {modelId}. Delete it once that has finished" +
                (busy.Holder?.Kind == OwnershipKind.Move ? ", or cancel the move first." : "."))
            : EditorMessage.Of($"Not deleted: {ex.Message}");
    }

    /// <summary>A move the master refused (design §5.6; MN-109 codes).</summary>
    public static EditorMessage Move(Exception ex, string modelId)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex switch
        {
            MoveRefusedException { Code: ErrorCodes.ModelNotMovable } refused => EditorMessage.Of(
                $"{modelId} can't be moved: {refused.Message}"),
            MoveRefusedException { Code: ErrorCodes.RateLimited } => EditorMessage.Of(
                "Too many wrong passwords from this session: wait a minute, then try again."),
            MoveRefusedException { Code: ErrorCodes.ReauthenticationFailed } refused => EditorMessage.Of(refused.Message),
            MoveRefusedException refused => EditorMessage.Of($"Not moved: {refused.Message}"),
            PlacementValidationException invalid => new EditorMessage(
                $"Not moved: the target placement of {modelId} was refused.",
                invalid.Problems.Count > 0 ? [.. invalid.Problems.Select(p => p.Message)] : [invalid.Message]),
            ModelBusyException busy => EditorMessage.Of(
                $"Not moved: {Holder(busy)} holds {modelId}. Start the move once that has finished."),
            KeyNotFoundException => EditorMessage.Of($"{modelId} no longer exists."),
            _ => EditorMessage.Of($"Not moved: {ex.Message}"),
        };
    }

    /// <summary>A force-commit or cancel of a move the master refused.</summary>
    public static EditorMessage MoveAction(Exception ex, string action)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex switch
        {
            MoveRefusedException { Code: ErrorCodes.RateLimited } => EditorMessage.Of(
                "Too many wrong passwords from this session: wait a minute, then try again."),
            MoveRefusedException { Code: ErrorCodes.ReauthenticationFailed or ErrorCodes.ConfirmationRequired } refused => EditorMessage.Of(refused.Message),
            _ => EditorMessage.Of($"Not {action}: {ex.Message}"),
        };
    }

    /// <summary>An unplace the master refused (or that timed out).</summary>
    public static EditorMessage Unplace(Exception ex, string modelId)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex switch
        {
            MoveRefusedException { Code: ErrorCodes.RateLimited } => EditorMessage.Of(
                "Too many wrong passwords from this session: wait a minute, then try again."),
            MoveRefusedException { Code: ErrorCodes.ReauthenticationFailed } refused => EditorMessage.Of(refused.Message),
            MoveRefusedException refused => EditorMessage.Of($"Not unplaced: {refused.Message}"),
            ModelBusyException busy => EditorMessage.Of($"Not unplaced: {Holder(busy)} holds {modelId}. Try again once that has finished."),
            TimeoutException => EditorMessage.Of(
                $"Not unplaced: {modelId} did not stop in time, so it stays placed. Check its state on the Dashboard and try again."),
            _ => EditorMessage.Of($"Not unplaced: {ex.Message}"),
        };
    }

    /// <summary>
    /// A manual start, stop or restart refused because another record holds the model (409 <c>model_busy</c>, design §6.7,
    /// D101): which work holds it and what to do: wait for a benchmark or tuning run or stop it from the maintenance banner,
    /// wait for a move or cancel it from the model's editor, wait for a restore (Backup page) or an unplace.
    /// </summary>
    /// <param name="busy">The refusal.</param>
    /// <param name="name">The model's served name.</param>
    /// <param name="action">"start", "stop" or "restart".</param>
    public static string Runtime(ModelBusyException busy, string name, string action)
    {
        ArgumentNullException.ThrowIfNull(busy);
        // Only a move's or an unplace's stage says where it is; a job's is its kind, a restore's its internal step.
        string holder = busy.Holder?.Kind is OwnershipKind.Move or OwnershipKind.Edit ? Holder(busy) : ModelBusyException.DescribeHolder(busy.Holder);
        string next = busy.Holder?.Kind switch
        {
            OwnershipKind.Job => "Wait for it to finish, or stop it from the banner at the top of the page.",
            OwnershipKind.Move => "Wait for the move to finish, or cancel it in the model's editor.",
            OwnershipKind.Restore => "Wait for the restore to finish; the Backup page shows its progress.",
            _ => "Try again once it has finished.",
        };
        return $"Couldn't {action} {name}: {holder} is running on it. {next}";
    }

    /// <summary>"a move to node pc-office (stage downloading)".</summary>
    public static string Holder(ModelBusyException busy)
    {
        ArgumentNullException.ThrowIfNull(busy);
        string holder = ModelBusyException.DescribeHolder(busy.Holder);
        return busy.Holder?.Stage is { Length: > 0 } stage ? $"{holder} (stage {stage})" : holder;
    }
}

/// <summary>One step of a move as the move dialog lists it.</summary>
/// <param name="Stage">The <see cref="MoveStages"/> value.</param>
/// <param name="Label">The step in words ("Download on pc-office").</param>
/// <param name="State">Done, current or to do.</param>
public sealed record MoveStep(string Stage, string Label, MoveStepState State);

/// <summary>Where a move step stands.</summary>
public enum MoveStepState
{
    /// <summary>Not reached yet.</summary>
    Todo,

    /// <summary>The move is at this step.</summary>
    Current,

    /// <summary>Passed.</summary>
    Done,

    /// <summary>The move failed at (or was cancelled before) this step.</summary>
    Failed,
}

/// <summary>The move dialog's progress words (design §6.7).</summary>
public static class MoveProgress
{
    private static readonly string[] Order =
        [MoveStages.Downloading, MoveStages.StoppingSource, MoveStages.SourceStopped, MoveStages.PlacementCommitted, MoveStages.StartingTarget, MoveStages.Done];

    /// <summary>
    /// The steps of <paramref name="record"/>: the stop steps only when the model had a source node, the start step only
    /// when it was running. A failed move marks the step it was at last (<paramref name="failedAt"/>, else the first).
    /// </summary>
    public static IReadOnlyList<MoveStep> Steps(OwnershipRecord record, string? failedAt = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        var move = record.Move ?? throw new ArgumentException("Not a move record.", nameof(record));
        bool hasSource = move.SourceNodeId.Length > 0;
        var stages = Order.Where(s =>
            (hasSource || s is not (MoveStages.StoppingSource or MoveStages.SourceStopped)) &&
            (move.WasRunning || s != MoveStages.StartingTarget)).ToList();

        string at = record.Stage == MoveStages.Failed ? failedAt ?? MoveStages.Downloading : record.Stage;
        int current = stages.IndexOf(at);
        if (current < 0)
        {
            current = at == MoveStages.StartingTarget ? stages.IndexOf(MoveStages.PlacementCommitted) + 1 : 0;
        }

        return
        [
            .. stages.Select((s, i) => new MoveStep(s, Label(s, move), record.Stage switch
            {
                MoveStages.Done => MoveStepState.Done,
                MoveStages.Failed when i == current => MoveStepState.Failed,
                _ when i < current => MoveStepState.Done,
                _ when i == current => MoveStepState.Current,
                _ => MoveStepState.Todo,
            })),
        ];
    }

    /// <summary>A stage in words.</summary>
    public static string Label(string stage, MoveDetails move)
    {
        ArgumentNullException.ThrowIfNull(move);
        return stage switch
        {
            MoveStages.Downloading => $"Download the checkpoint on {move.TargetNodeId}",
            MoveStages.StoppingSource => $"Stop it on {move.SourceNodeId}",
            MoveStages.SourceStopped => $"Confirm it stopped on {move.SourceNodeId}",
            MoveStages.PlacementCommitted => $"Place it on {move.TargetNodeId}",
            MoveStages.StartingTarget => $"Start it on {move.TargetNodeId}",
            MoveStages.Done => "Done",
            _ => "Failed",
        };
    }

    /// <summary>True when the move finished at <c>done</c> but the start on the target was refused (D94).</summary>
    public static bool DoneWithError(OwnershipRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.Stage == MoveStages.Done && !string.IsNullOrWhiteSpace(record.Error);
    }

    /// <summary>Percent of a download (0 when its size is unknown).</summary>
    public static double Percent(MoveDownloadView download)
    {
        ArgumentNullException.ThrowIfNull(download);
        return download.TotalBytes > 0 ? Math.Clamp(100.0 * download.BytesDownloaded / download.TotalBytes, 0, 100) : 0;
    }
}
