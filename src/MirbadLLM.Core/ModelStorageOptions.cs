namespace MirbadLLM.Core;

/// <summary>
/// Bound from the same <c>MirbadLLM</c> configuration section as <see cref="MirbadLLMOptions"/> (kept as a
/// separate options type so that frozen file does not need to change).
/// </summary>
public sealed class ModelStorageOptions
{
    /// <summary>
    /// Path where the controller process itself sees the models root — used to list checkpoint folders and
    /// read their <c>config.json</c>, and to measure Hugging Face download progress on disk. The integrator
    /// mounts <see cref="MirbadLLMOptions.ModelsHostPath"/> here (typically read-only). Default: <c>/models</c>.
    /// </summary>
    public string ModelsLocalPath { get; set; } = "/models";
}
