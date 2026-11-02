using System.Globalization;
using Microsoft.Extensions.Configuration;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.Orchestration.Nodes;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>
/// When P2 opens node-scoped maintenance windows (design §6.5 item 2, D83; decided by MN-130, D101). One key,
/// <c>MirbadLLM:Router:Maintenance:NodeScoped</c>:
/// <list type="bullet">
/// <item><c>true</c> / <c>false</c>: as set, always (the operator's explicit choice).</item>
/// <item>unset (the default): <b>automatic</b>. A job opens a node-scoped window only while two or more approved,
/// non-revoked nodes (<see cref="NodeFleet"/>) are registered when it starts; with one node (the live single DGX) every window stays global, so <c>/v1</c> answers in
/// the P1 order with P1's 503 texts exactly. The router's switch is on in this mode: with only global windows open it
/// answers exactly as with the switch off (a global window keeps the P1 order whatever the switch says), and once a job
/// on a two-node fleet opens a node-scoped window, the P2 order applies to it.</item>
/// </list>
/// </summary>
public sealed class NodeScopedMaintenancePolicy
{
    /// <summary>The configuration key (shared with the router, <c>RouterMaintenanceOptions.NodeScopedKey</c>).</summary>
    public const string Key = ModelJobService.NodeScopedMaintenanceKey;

    /// <summary>Fewest fleet members (approved, non-revoked nodes, <see cref="NodeFleet"/>) for which the automatic mode opens node-scoped windows.</summary>
    public const int AutomaticMinimumNodes = NodeFleet.MultiNodeMinimum;

    private readonly INodeRegistry _nodes;

    /// <param name="configured">The explicit setting; null = automatic.</param>
    /// <param name="nodes">The node registry the automatic mode counts.</param>
    public NodeScopedMaintenancePolicy(bool? configured, INodeRegistry nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        Configured = configured;
        _nodes = nodes;
    }

    /// <summary>The explicit setting, or null in the automatic mode.</summary>
    public bool? Configured { get; }

    /// <summary>The router's switch: the explicit setting, else on (see the type's remarks).</summary>
    public bool RouterSwitch => Configured ?? true;

    /// <summary>
    /// The explicit setting in <paramref name="configuration"/>, or null when unset or empty. A value that is not a boolean
    /// is refused at startup (<see cref="InvalidOperationException"/>) rather than guessed.
    /// </summary>
    public static bool? ReadConfigured(IConfiguration? configuration)
    {
        string? value = configuration?[Key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return bool.TryParse(value.Trim(), out bool parsed)
            ? parsed
            : throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"{Key} must be true, false or unset (automatic: node-scoped only with {AutomaticMinimumNodes} or more nodes); got '{value}'."));
    }

    /// <summary>Whether a job starting now opens a node-scoped window.</summary>
    public async ValueTask<bool> IsOnAsync(CancellationToken ct = default)
    {
        if (Configured is { } configured)
        {
            return configured;
        }

        // Approved members only (W9 review MN-132, D102): a node waiting for approval does not change /v1 on the DGX.
        return await NodeFleet.IsMultiNodeAsync(_nodes, ct).ConfigureAwait(false);
    }
}
