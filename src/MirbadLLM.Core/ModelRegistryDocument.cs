using System.Text.Json.Serialization;

namespace MirbadLLM.Core;

/// <summary>
/// The version-2 envelope of <c>{DataPath}/models.json</c> (design §5.4): <c>{"schemaVersion": 2, "models": [...]}</c>.
/// A bare JSON array is version 1 and is migrated exactly once by <c>JsonModelRegistry</c> (MN-006): every definition
/// gets the bootstrap node as its placement and <see cref="RuntimeKind.Vllm"/>, the configured keep-serving list is
/// applied, the original is kept as <c>models.v1.json</c>, and the file is rewritten as version 2. In a version-2 file
/// a missing or <c>null</c> placement means unplaced and is preserved.
/// <para>The envelope's own keys are camelCase whatever options the registry uses (they are pinned below); the model
/// objects inside keep the registry's naming.</para>
/// </summary>
public sealed record ModelRegistryDocument
{
    /// <summary>Version of the envelope format written from P1 on.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>The implicit version of a bare-array file.</summary>
    public const int LegacySchemaVersion = 1;

    /// <summary>File name under the data path.</summary>
    public const string FileName = "models.json";

    /// <summary>Name of the one-time copy of a version-1 file kept by the migration.</summary>
    public const string LegacyCopyFileName = "models.v1.json";

    /// <summary>Schema version; <see cref="CurrentSchemaVersion"/> for files this code writes.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The model definitions.</summary>
    [JsonPropertyName("models")]
    public IReadOnlyList<ModelDefinition> Models { get; init; } = [];
}
