using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;

namespace MirbadLLM.UnitTests.Multinode.Chaos;

/// <summary>
/// The end-state invariants every chaos sequence must reach once its faults are healed (MN-044):
/// <list type="number">
/// <item>no model has two containers in the Docker engine, and the engine's model containers are exactly the agent's
/// instances;</item>
/// <item>no operation on the agent is still active and no start reservation is left (idle node);</item>
/// <item>every master operation reference is closed with the agent's terminal state, or is open only because the
/// agent never received that operation (it closes as <c>NotAccepted</c> after the grace period); every operation the agent
/// answered with a success status (<see cref="AgentRequestRecorder"/>) is still known to it, across crashes, and its
/// master reference is closed;</item>
/// <item>the master's status cache equals the agent's <c>/status</c> (boot, instances and their states, open operations)
/// and the node is Online;</item>
/// <item>the router serves <c>/v1/chat/completions</c> for every Ready model;</item>
/// <item>neither the Hugging Face token nor the backup share password appears in any log entry of either side or in any
/// file under the master's data folder or the agent's state directory (operation logs, references, registry).</item>
/// </list>
/// </summary>
internal static class ChaosInvariants
{
    /// <summary>
    /// Marks the violation of the known finding <c>ChaosFindingTests.ANodeThatRecoversWhileConnectedBecomesOnline</c>: the
    /// master keeps health reasons the agent no longer reports (health travels only in snapshots).
    /// </summary>
    public const string StaleHealth = "stale health (MN-044 finding)";

    /// <summary>All violations right now (empty = the system has settled correctly).</summary>
    public static async Task<List<string>> CheckAsync(ChaosCluster cluster, bool includeRouter, CancellationToken ct)
    {
        var violations = new List<string>();
        if (!cluster.MasterRunning || !cluster.AgentRunning)
        {
            violations.Add("master and agent must both run when invariants are checked");
            return violations;
        }

        // 1. Containers.
        var containers = await cluster.ModelContainersAsync(ct);
        foreach (var group in containers.GroupBy(c => c.Model).Where(g => g.Count() > 1))
        {
            violations.Add($"duplicate model containers for '{group.Key}': {string.Join(", ", group.Select(c => $"{c.Name} ({c.State})"))}");
        }

        StatusSnapshot status;
        IReadOnlyList<OperationStatus> active;
        try
        {
            var agent = cluster.AgentClient();
            status = await agent.GetStatusAsync(ct);
            active = await agent.ListOperationsAsync(new OperationsQuery { State = OperationsQuery.ActiveState }, ct);
        }
        catch (Exception ex) when (ex is AgentException or AgentUnreachableException or HttpRequestException)
        {
            violations.Add($"the agent's /status could not be read: {ex.Message}");
            return violations;
        }

        var instanceModels = status.Runtimes.Select(r => r.ModelId).Order(StringComparer.Ordinal).ToList();
        var containerModels = containers.Select(c => c.Model).Distinct().Order(StringComparer.Ordinal).ToList();
        if (!instanceModels.SequenceEqual(containerModels))
        {
            violations.Add($"Docker model containers [{string.Join(", ", containerModels)}] != agent instances [{string.Join(", ", instanceModels)}]");
        }

        // 2. Idle node.
        foreach (var operation in active.Concat(status.Operations.Where(o => !o.State.IsTerminal())).DistinctBy(o => o.Id))
        {
            violations.Add($"operation {operation.Kind} {operation.Target} {operation.Id} is still {operation.State} (stage {operation.Stage})");
        }

        if (active.Count == 0 && status.Reservations.Count > 0)
        {
            violations.Add($"leaked reservations with no active operation: {string.Join(", ", status.Reservations.Select(r => $"{r.ModelId}/{r.OperationId}"))}");
        }

        // 3. Master operation references.
        var references = await cluster.MasterServices.GetRequiredService<IAgentOperationReferenceStore>().ListAsync(ct);
        var agentClient = cluster.AgentClient();
        foreach (var reference in references)
        {
            OperationStatus? onAgent;
            try
            {
                onAgent = await agentClient.GetOperationAsync(reference.OperationId, ct);
            }
            catch (Exception ex) when (ex is AgentException or AgentUnreachableException)
            {
                violations.Add($"reference {reference.Action} {reference.Target} {reference.OperationId}: the agent could not be asked ({ex.Message})");
                continue;
            }

            if (reference.Status == AgentOperationReferenceStatus.Open)
            {
                if (onAgent is not null)
                {
                    violations.Add($"reference {reference.Action} {reference.Target} {reference.OperationId} is still open although the agent has it {onAgent.State}");
                }

                continue;
            }

            if (onAgent is not null && !Consistent(reference.Outcome, onAgent.State))
            {
                violations.Add($"reference {reference.Action} {reference.Target} {reference.OperationId} closed {reference.Outcome} but the agent says {onAgent.State}");
            }
        }

        // Every operation the agent answered with a success status is durable and closed on the master.
        var referenceIds = references.ToDictionary(r => r.OperationId, StringComparer.Ordinal);
        foreach ((string operationId, string answer) in cluster.AgentRequests.Accepted)
        {
            OperationStatus? onAgent;
            try
            {
                onAgent = await agentClient.GetOperationAsync(operationId, ct);
            }
            catch (Exception ex) when (ex is AgentException or AgentUnreachableException)
            {
                violations.Add($"accepted operation {operationId} ({answer}): the agent could not be asked ({ex.Message})");
                continue;
            }

            if (onAgent is null)
            {
                violations.Add($"the agent answered {answer} for operation {operationId} but no longer knows it");
            }

            if (referenceIds.TryGetValue(operationId, out var reference) && reference.Status == AgentOperationReferenceStatus.Open)
            {
                violations.Add($"the master's reference for accepted operation {operationId} ({answer}) is still open");
            }
        }

        // 4. Cache == agent.
        var cached = cluster.CachedNode();
        if (cached is null)
        {
            violations.Add("the master's cache does not know dgx");
        }
        else
        {
            if (status.Health.Reasons.Count > 0)
            {
                violations.Add($"the agent still reports Degraded with every fault healed: {string.Join("; ", status.Health.Reasons.Select(r => r.Code))}");
            }
            else if (cached.State == NodeState.Degraded)
            {
                violations.Add($"{StaleHealth}: dgx is Degraded in the master's cache ({string.Join("; ", cached.Reasons)}) although the agent reports no health reason");
            }
            else if (cached.State != NodeState.Online)
            {
                violations.Add($"dgx is {cached.State} in the master's cache ({string.Join("; ", cached.Reasons)})");
            }

            if (cached.BootId != status.BootId)
            {
                violations.Add($"cache boot {cached.BootId} != agent boot {status.BootId}");
            }

            string agentView = Describe(status.Runtimes.Select(r => (r.ModelId, r.State, r.InstanceId)));
            string cacheView = Describe(cached.Runtimes.Select(r => (r.ModelId, r.State, r.InstanceId)));
            if (agentView != cacheView)
            {
                violations.Add($"cache instances [{cacheView}] != agent /status [{agentView}]");
            }

            if (cached.OpenOperations.Count > 0 && active.Count == 0)
            {
                violations.Add($"cache lists open operations the agent finished: {string.Join(", ", cached.OpenOperations.Select(o => $"{o.Kind} {o.Target} {o.OperationId} {o.State}"))}");
            }
        }

        // 5. Router.
        if (includeRouter && violations.Count == 0)
        {
            foreach (var ready in status.Runtimes.Where(r => r.State == ModelState.Ready))
            {
                string served = ready.ServedName ?? ready.ModelId;
                try
                {
                    using var chat = await cluster.Http.PostAsJsonAsync("/v1/chat/completions",
                        new { model = served, messages = new[] { new { role = "user", content = "chaos" } }, max_tokens = 2 }, ct);
                    string body = await chat.Content.ReadAsStringAsync(ct);
                    if (chat.StatusCode != HttpStatusCode.OK || !body.Contains("\"choices\"", StringComparison.Ordinal))
                    {
                        violations.Add($"/v1/chat/completions for Ready '{served}': {(int)chat.StatusCode} {Trim(body)}");
                    }
                }
                catch (HttpRequestException ex)
                {
                    violations.Add($"/v1/chat/completions for Ready '{served}' failed: {ex.Message}");
                }
            }
        }

        violations.AddRange(cluster.ShutdownFailures);

        // 6. Secrets.
        violations.AddRange(FindSecrets(cluster));
        return violations;
    }

    /// <summary>Every place the cluster's secrets were written: log entries of both sides, files of both sides.</summary>
    public static List<string> FindSecrets(ChaosCluster cluster)
    {
        var found = new List<string>();
        foreach (string secret in new[] { cluster.HfToken, cluster.SharePassword })
        {
            string label = secret == cluster.HfToken ? "HF token" : "share password";
            foreach (string entry in cluster.MasterLog.Containing(secret).Take(3))
            {
                found.Add($"{label} in a master log entry: {Trim(entry)}");
            }

            foreach (string entry in cluster.AgentLog.Containing(secret).Take(3))
            {
                found.Add($"{label} in an agent log entry: {Trim(entry)}");
            }

            foreach (string file in FilesContaining(secret, cluster.DataDirectory, cluster.Node.StateDirectory, cluster.BootstrapDirectory))
            {
                found.Add($"{label} written to {file}");
            }
        }

        return found;
    }

    public static IEnumerable<string> FilesContaining(string secret, params string[] roots)
    {
        byte[] needle = Encoding.UTF8.GetBytes(secret);
        foreach (string root in roots.Where(Directory.Exists))
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                byte[] content;
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    content = memory.ToArray();
                }
                catch (IOException)
                {
                    continue; // removed while scanning (a temp file being renamed)
                }

                if (content.AsSpan().IndexOf(needle) >= 0)
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>
    /// A closed reference against the agent's record of the same id: a terminal outcome must be the agent's terminal state;
    /// a refusal may be recorded by the agent as failed (D41); "never arrived"/"expired" must not exist on the agent.
    /// </summary>
    private static bool Consistent(AgentOperationOutcome? outcome, OperationState state) => outcome switch
    {
        AgentOperationOutcome.Succeeded or AgentOperationOutcome.Failed or AgentOperationOutcome.Cancelled or AgentOperationOutcome.Unknown =>
            state.IsTerminal() && AgentOperationRunner.ToOutcome(state) == outcome,
        AgentOperationOutcome.Refused => state == OperationState.Failed,
        AgentOperationOutcome.NotAccepted or AgentOperationOutcome.Expired => false,
        _ => true,
    };

    private static string Describe(IEnumerable<(string Model, ModelState State, string? Instance)> runtimes) =>
        string.Join(", ", runtimes.OrderBy(r => r.Model, StringComparer.Ordinal).Select(r => $"{r.Model}:{r.State}:{r.Instance}"));

    private static string Trim(string text) => text.Length <= 300 ? text : text[..300] + "…";

    /// <summary>Serialized for failure messages.</summary>
    public static string Json<T>(T value) => JsonSerializer.Serialize(value, AgentJson.Options);
}
