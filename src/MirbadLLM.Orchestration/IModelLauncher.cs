using MirbadLLM.Core;

namespace MirbadLLM.Orchestration;

/// <summary>How <see cref="IModelLauncher.LaunchAsync"/> recreates a container.</summary>
/// <param name="Force">Skip the memory admission check.</param>
/// <param name="Tuning">Label the container <c>mirbadllm.tuning=1</c>: it runs a candidate definition that is not the saved one.</param>
public sealed record LaunchOptions(bool Force = false, bool Tuning = false);

/// <summary>
/// Lower-level container control used by restarts, benchmarks and tuning: launch a model with a given
/// definition (which need not be the saved one) and read back what the running container was launched with.
/// </summary>
public interface IModelLauncher
{
    /// <summary>
    /// Stops and removes the model's current container (if any) and creates a new one from
    /// <paramref name="definition"/>. The registry is not touched. Admission counts the current container's
    /// budget as released.
    /// </summary>
    /// <exception cref="ModelMemoryAdmissionException">The budget does not fit and <see cref="LaunchOptions.Force"/> is false.</exception>
    Task<ModelStatus> LaunchAsync(ModelDefinition definition, LaunchOptions options, CancellationToken ct = default);

    /// <summary>What the current container was launched with, read back from its command line. Null when there is no container.</summary>
    Task<ModelDefinition?> GetLaunchedDefinitionAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// The canonical config hash of the current container (see <see cref="ModelConfigHash"/>; recomputed from its
    /// command line, so a container launched before the canonical hash compares equal to the definition vLLM
    /// effectively ran). Null when there is no container or it predates the hash label.
    /// </summary>
    Task<string?> GetLaunchedConfigHashAsync(string id, CancellationToken ct = default);

    /// <summary>The config hash a container launched from <paramref name="definition"/> would carry on this host.</summary>
    Task<string> ComputeConfigHashAsync(ModelDefinition definition, CancellationToken ct = default);

    /// <summary>Ids of models whose current container carries the tuning label (left behind by an interrupted tuning run).</summary>
    Task<IReadOnlyList<string>> ListTuningContainersAsync(CancellationToken ct = default);
}
