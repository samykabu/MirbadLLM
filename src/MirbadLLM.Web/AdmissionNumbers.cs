using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;

namespace MirbadLLM.Web;

/// <summary>
/// The numbers a "Start anyway?" / "Restart anyway?" dialog states (W8 review MN-128, D96): the agent's own admission
/// refusal when it carries one (<see cref="AgentErrorMapper.AdmissionDetails"/>: need, free after the reserve, available,
/// reserve, released, held by other starts, and on a discrete node the refusing GPU or the host part), else the page's
/// own estimate.
/// </summary>
/// <param name="NeedGiB">The budget the start needs (per GPU on a discrete node).</param>
/// <param name="FreeAfterReserveGiB">Free after the reserve and other starts (what the decision compared against).</param>
/// <param name="AvailableGiB">Available memory the probe read.</param>
/// <param name="ReserveGiB">The reserve applied (node, host or GPU).</param>
/// <param name="ReleasedGiB">Memory a replaced instance releases (a restart).</param>
/// <param name="HeldByOthersGiB">Memory other starts hold.</param>
/// <param name="Where">"GPU 1 (NVIDIA GeForce RTX 5060 Ti) on pc-office", "host RAM on pc-office", "dgx"; null when unknown.</param>
/// <param name="FromAgent">True when the numbers are the agent's own refusal.</param>
public sealed record AdmissionNumbers(
    double NeedGiB, double FreeAfterReserveGiB, double AvailableGiB, double ReserveGiB, double ReleasedGiB, double HeldByOthersGiB,
    string? Where, bool FromAgent)
{
    /// <summary>
    /// The agent's refusal numbers behind <paramref name="refusal"/>, naming the GPU by its current index from
    /// <paramref name="gpus"/>; null when the refusal carries none.
    /// </summary>
    public static AdmissionNumbers? FromRefusal(Exception refusal, string? nodeId, IReadOnlyList<GpuCapability>? gpus = null)
    {
        if (AgentErrorMapper.AdmissionDetails(refusal) is not { } details)
        {
            return null;
        }

        return new AdmissionNumbers(
            details.NeedGiB, Math.Max(0, details.FreeGiB), details.AvailableGiB, details.ReserveGiB, details.ReleasedGiB,
            details.ReservedByOthersGiB, Describe(details, nodeId, gpus), FromAgent: true);
    }

    private static string? Describe(AdmissionRefusedDetails details, string? nodeId, IReadOnlyList<GpuCapability>? gpus)
    {
        string on = string.IsNullOrEmpty(nodeId) ? "" : " on " + nodeId;
        if (details.Gpu is { Length: > 0 } uuid)
        {
            var gpu = gpus?.FirstOrDefault(g => string.Equals(g.Uuid, uuid, StringComparison.Ordinal));
            return gpu is null
                ? $"GPU {uuid}{on}"
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"GPU {gpu.Index} ({gpu.Name}){on}");
        }

        if (details.Host == true)
        {
            return "host RAM" + on;
        }

        return string.IsNullOrEmpty(nodeId) ? null : nodeId;
    }
}
