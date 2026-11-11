using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;

namespace MirbadLLM.Agent;

/// <summary>
/// The <c>nvidia-smi</c> of a node whose <c>Agent:Accelerator</c> is <c>cpu</c> or <c>vulkan</c>: never run, always
/// "not installed". Everything that reads NVIDIA GPUs (inventory, telemetry, GPU processes, the memory model) then
/// behaves as on a machine without the NVIDIA driver, also when an unused NVIDIA card sits in the machine.
/// </summary>
internal sealed class AbsentNvidiaSmi : INvidiaSmiRunner
{
    public static AbsentNvidiaSmi Instance { get; } = new();

    public Task<NvidiaSmiResult> RunAsync(string arguments, CancellationToken ct = default) => Task.FromResult(NvidiaSmiResult.NotFound);
}

/// <summary>
/// The capability report of a node whose <c>Agent:Accelerator</c> is <c>cpu</c> or <c>vulkan</c>: it carries the value
/// (<see cref="NodeCapabilities.Accelerator"/>) and lists only what runs without CUDA. That is llama.cpp, as a container
/// or a process; vLLM, SGLang and NIM containers need an NVIDIA GPU, so their runtimes and features are removed and the
/// master refuses to place such a model here instead of failing at Docker. Downloads go through the agent's own
/// downloader (<c>downloads.hf-native</c>), because the helper container runs from the vLLM image, which such a node has
/// no reason to hold. Registered after every contributor that adds runtimes; never registered on an NVIDIA or macOS node,
/// whose reports stay as they were.
/// </summary>
internal sealed class AcceleratorContributor(string accelerator) : ICapabilityContributor
{
    /// <summary>Features a node without CUDA does not report.</summary>
    public static IReadOnlyList<string> CudaOnlyFeatures { get; } =
    [
        AgentFeatures.RuntimeVllmContainer,
        AgentFeatures.RuntimeSglangContainer,
        AgentFeatures.RuntimeNimContainer,
        AgentFeatures.DownloadsHfContainer,
    ];

    public Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        return Task.FromResult(current with
        {
            Accelerator = accelerator,
            Features = [.. current.Features.Where(f => !CudaOnlyFeatures.Contains(f, StringComparer.Ordinal))],
            Runtimes = [.. current.Runtimes.Where(r => r.Kind == RuntimeKind.LlamaCpp)],
        });
    }
}
