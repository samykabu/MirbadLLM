using System.Text.Json;
using System.Text.Json.Serialization;

namespace MirbadLLM.Agent.Fencing;

/// <summary>
/// <c>{stateDirectory}/fencing.json</c> (design §4.2 layer 2): the highest master triple the agent has accepted. Written
/// atomically (temp file flushed with <c>Flush(true)</c>, then renamed over the target) before a higher epoch is
/// reported as accepted.
/// </summary>
public sealed record FencingStateDocument
{
    /// <summary>Schema version of this file format.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>File name in the state directory.</summary>
    public const string FileName = "fencing.json";

    /// <summary>Schema version.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The accepted master's instance id (<c>masterInstanceId</c> in its <c>master.json</c>).</summary>
    public required string MasterInstanceId { get; init; }

    /// <summary>The accepted epoch.</summary>
    public required long Epoch { get; init; }

    /// <summary>The accepted master boot id.</summary>
    public required string BootId { get; init; }

    /// <summary>When the agent accepted this triple.</summary>
    public DateTimeOffset AcceptedAt { get; init; }
}

/// <summary>JSON options of <see cref="FencingStateDocument"/>.</summary>
internal static class FencingStateJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
