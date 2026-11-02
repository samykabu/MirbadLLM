namespace MirbadLLM.Web.Components.Ui;

/// <summary>
/// One GPU of a discrete node in <see cref="MemoryHeadroom"/>'s per-GPU variant (design §8.3 "one bar per GPU for
/// discrete"). Memory values are null when the node did not report them; the bar then stays empty and the legend shows
/// "—".
/// </summary>
/// <param name="Index"><c>nvidia-smi</c> index (display).</param>
/// <param name="Uuid">GPU UUID (identity, used for test ids and keys).</param>
/// <param name="Name">Product name, e.g. "NVIDIA GeForce RTX 4060 Ti"; null when unknown.</param>
/// <param name="TotalGiB">VRAM total.</param>
/// <param name="AvailableGiB">VRAM free.</param>
/// <param name="ModelsGiB">VRAM held by mirbadllm models on this GPU (measured, or the budget share).</param>
/// <param name="ReserveGiB">Reserve kept free on this GPU.</param>
public sealed record GpuHeadroom(
    int Index,
    string Uuid,
    string? Name,
    double? TotalGiB,
    double? AvailableGiB,
    double ModelsGiB,
    double ReserveGiB);
