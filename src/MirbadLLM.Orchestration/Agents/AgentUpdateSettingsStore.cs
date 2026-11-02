using System.Text.Json;

namespace MirbadLLM.Orchestration.Agents;

/// <summary>
/// The fleet's agent-update setting (docs/multinode/agent-updates.md U8): <c>{DataPath}/agent-updates.json</c>. Off by
/// default; on, the master updates every outdated node it can reach on its own (<c>AgentAutoUpdater</c>). In Orchestration
/// so backups carry it and restores set it again.
/// </summary>
public sealed class AgentUpdateSettingsStore(string? path)
{
    public const string FileName = "agent-updates.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Lock _gate = new();

    /// <summary>False when this master has no data folder, so <see cref="SetAutomatic"/> cannot keep the setting.</summary>
    public bool CanPersist => path is not null;

    public bool Automatic
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    return path is not null && File.Exists(path)
                        && JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json)?.Automatic == true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    return false;
                }
            }
        }
    }

    /// <exception cref="InvalidOperationException">No data folder (<see cref="CanPersist"/> is false).</exception>
    public void SetAutomatic(bool automatic)
    {
        if (path is null)
        {
            throw new InvalidOperationException("This master has no data folder to keep the setting in.");
        }

        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string part = path + ".tmp";
            File.WriteAllText(part, JsonSerializer.Serialize(new Settings(automatic), Json));
            File.Move(part, path, overwrite: true);
        }
    }

    private sealed record Settings(bool Automatic);
}
