using System.Globalization;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Admission;

/// <summary>
/// Refusal messages (design §4.8): the pre-P1 <c>DockerModelRuntime</c>'s texts, unchanged, with the node appended as
/// "… on node {id}." (the text's final period moves after the node; a GPU or host part follows in parentheses). On a
/// unified node with one start at a time no other reservation can hold memory, so the memory texts are exactly today's;
/// "and N GiB held by other starts" appears only when other reservations hold memory (several starts allowed).
/// </summary>
public static class AdmissionTexts
{
    /// <summary>
    /// Today's <c>ModelStartConflictException</c> text (<c>EnsureNoOtherModelStartingAsync</c>), unchanged for a
    /// container node. On a node that runs native processes (<paramref name="processDelivery"/> true,
    /// <see cref="AdmissionOptions.ProcessDelivery"/>) the clause naming the runtime is llama.cpp's: the Windows PC has
    /// no vLLM, and telling its operator that "vLLM measures memory while it loads" sends them looking for something
    /// that is not there (MN-239 H2, the same class as the hostd/systemd wordings of MN-239 item 1). The rest of the
    /// sentence — and the whole DGX text — is byte-identical to P1's.
    /// </summary>
    public static string StartConflict(string startingModelId, string modelId, bool processDelivery = false) =>
        $"Model '{startingModelId}' is still starting. Start '{modelId}' once it is Ready: " +
        (processDelivery
            ? "llama.cpp takes its weights and KV cache while it loads, "
            : "vLLM measures memory while it loads, ") +
        "so two models starting together both fail.";

    /// <summary>
    /// Today's <c>ModelMemoryAdmissionException</c> text: the <c>StartAsync</c> form without released memory, the
    /// <c>LaunchAsync</c>/<c>RestartAsync</c> form with it.
    /// </summary>
    public static string MemoryRefused(string modelId, double needGiB, double freeGiB, double availableGiB, double reserveGiB, double releasedGiB, double reservedByOthersGiB)
    {
        var c = CultureInfo.InvariantCulture;
        string reserve = reservedByOthersGiB > 0
            ? string.Create(c, $"minus a {reserveGiB:F1} GiB reserve and {reservedByOthersGiB:F1} GiB held by other starts")
            : string.Create(c, $"minus a {reserveGiB:F1} GiB reserve");
        return releasedGiB > 0
            ? string.Create(c, $"Model '{modelId}' needs {needGiB:F1} GiB but only {freeGiB:F1} GiB is free ({availableGiB:F1} GiB available plus {releasedGiB:F1} GiB released by its current container, {reserve}).")
            : string.Create(c, $"Model '{modelId}' needs {needGiB:F1} GiB but only {freeGiB:F1} GiB is free ({availableGiB:F1} GiB available {reserve}).");
    }

    /// <summary>The part naming the host-memory part of discrete admission: "… on node pc (host memory)."</summary>
    public const string HostPart = "host memory";

    /// <summary>
    /// Discrete admission (MN-105) for a placed GPU whose free memory the probe did not report (failed or partial
    /// <c>nvidia-smi</c> read). Followed by the GPU part like every per-GPU refusal.
    /// </summary>
    public static string GpuMemoryUnknown(string modelId, double needGiB) =>
        string.Create(CultureInfo.InvariantCulture, $"Model '{modelId}' needs {needGiB:F1} GiB but the GPU's free memory could not be read.");

    /// <summary>
    /// The part naming GPUs on a discrete node (MN-105): "GPU 1" or "GPUs 0, 1", by the current <c>nvidia-smi</c> index
    /// from <paramref name="indices"/>; a UUID the map does not know is shown as is, the pseudo-GPU <c>host</c> as
    /// "host" (a CPU-only start).
    /// </summary>
    public static string GpuPart(IReadOnlyList<string> uuids, IReadOnlyDictionary<string, int> indices)
    {
        ArgumentNullException.ThrowIfNull(uuids);
        ArgumentNullException.ThrowIfNull(indices);
        if (uuids.Count == 1 && uuids[0] == ReservationInfo.HostPseudoGpu)
        {
            return ReservationInfo.HostPseudoGpu;
        }

        string[] names = [.. uuids.Select(u => indices.TryGetValue(u, out int index) ? index.ToString(CultureInfo.InvariantCulture) : u)];
        return names.Length == 1 ? "GPU " + names[0] : "GPUs " + string.Join(", ", names);
    }

    /// <summary>Appends "on node {id}" (and an optional "(GPU …)" / "(host memory)" part) to a text that ends with a period.</summary>
    public static string OnNode(string text, string nodeId, string? part = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        string body = text.EndsWith('.') ? text[..^1] : text;
        return part is null ? $"{body} on node {nodeId}." : $"{body} on node {nodeId} ({part}).";
    }
}
