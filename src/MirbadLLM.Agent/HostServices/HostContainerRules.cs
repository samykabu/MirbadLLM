using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.HostServices;

/// <summary>
/// The agent's container deny rules (design §10.3, §4.3 host containers): the agent never lists or controls its own
/// container, the master's (<c>mirbadllm</c>), model containers (<c>ss-*</c> with mirbadllm labels) or download
/// helpers. The master's container whitelist comes on top of this and is enforced by the master.
/// <para>Model containers are recognised by their labels, as <c>HostServiceManager.IsExcluded</c> does today: every
/// container the controller or the agent creates carries <c>mirbadllm.model</c> (models) or <c>mirbadllm.download</c>
/// (helpers), and agent-created ones also <c>mirbadllm.node</c>/<c>mirbadllm.operation</c>. An <c>ss-</c> name with any
/// <c>mirbadllm.*</c> label is refused too. A hand-made <c>ss-</c> container without mirbadllm labels (the legacy
/// <c>ss-vllm</c> in today's default container whitelist) stays manageable, so the Services page keeps working as today.</para>
/// </summary>
public static class HostContainerRules
{
    /// <summary>Name prefix of mirbadllm model containers.</summary>
    public const string ModelContainerPrefix = "ss-";

    private const string LabelPrefix = "mirbadllm.";

    private static readonly string[] DenyLabels =
        [ContainerLabels.Model, ContainerLabels.Download, ContainerLabels.Node, ContainerLabels.Operation];

    /// <summary>True when the agent must refuse to list or act on the container.</summary>
    public static bool IsDenied(string name, IReadOnlyDictionary<string, string>? labels, AgentHostServicesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string bare = (name ?? "").TrimStart('/');
        if (bare.Length == 0
            || string.Equals(bare, options.MasterContainerName, StringComparison.Ordinal)
            || string.Equals(bare, options.AgentContainerName, StringComparison.Ordinal)
            // The pre-rename containers, still present (stopped) during the DGX's first deploy after the rename.
            || bare is LegacyNames.MasterContainerName or LegacyNames.AgentContainerName)
        {
            return true;
        }

        if (labels is null || labels.Count == 0)
        {
            return false;
        }

        if (DenyLabels.Any(labels.ContainsKey))
        {
            return true;
        }

        return bare.StartsWith(ModelContainerPrefix, StringComparison.Ordinal)
            && labels.Keys.Any(k => k.StartsWith(LabelPrefix, StringComparison.Ordinal));
    }
}
