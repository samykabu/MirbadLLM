using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Web.Components.Pages;

namespace MirbadLLM.Web.Components.Ui;

/// <summary>The Overview's attention list, shared by both shell readouts.</summary>
public sealed record AttentionItems(IReadOnlyList<AttentionItems.ModelItem> Models, IReadOnlyList<Nodes.NodeRow> Nodes)
{
    public int Count => Models.Count + Nodes.Count;

    public sealed record ModelItem(ModelDefinition Definition, ModelStatus Status, ModelProgressIssue? Progress);

    public static AttentionItems Build(LiveSnapshot snapshot, IEnumerable<Nodes.NodeRow> nodes, IMetricsHistory? history)
    {
        var models = new List<ModelItem>();
        foreach (var definition in snapshot.Models ?? [])
        {
            if (snapshot.Statuses?.GetValueOrDefault(definition.Id) is not { } status)
            {
                continue;
            }

            var progress = status.State == ModelState.Ready && history is not null
                ? ModelProgress.Assess(history.GetModel(definition.Id, ModelProgress.Window), snapshot.At)
                : null;
            if (status.State is ModelState.Failed or ModelState.Starting or ModelState.Stopping || progress is not null)
            {
                models.Add(new(definition, status, progress));
            }
        }

        return new(models, nodes.Where(n => n.State is not (NodeState.Revoked or NodeState.PendingApproval)
            && (n.State != NodeState.Online || n.Draining)).ToList());
    }
}
