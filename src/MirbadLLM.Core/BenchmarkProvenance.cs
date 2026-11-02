using System.Text.Json.Serialization;

namespace MirbadLLM.Core;

/// <summary>A GPU a benchmark ran on (design §6.5 item 5).</summary>
/// <param name="Uuid">GPU UUID (the identity).</param>
/// <param name="Name">Product name, when known.</param>
/// <param name="Index">Index at the time of the run (display only).</param>
public sealed record ProvenanceGpu(
    string Uuid,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Index = null);

/// <summary>
/// Where and how a benchmark or tuning result was measured (design §6.5 item 5; P2, frozen by MN-101, D73; recorded by
/// MN-112 on each stored run). Results stay after a move; comparisons, ETAs and "best" lists use only runs that are
/// <see cref="IsComparableWith"/> each other (others are shown, labelled "different hardware"). Runs stored before P2
/// have none (null = the bootstrap node, direct path). Null members are omitted from JSON (W8 review MN-128, as the store
/// and the fixture already wrote them).
/// </summary>
public sealed record BenchmarkProvenance
{
    /// <summary>Node id.</summary>
    public required string NodeId { get; init; }

    /// <summary>Node uid (a re-installed machine with the same id is a different node).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NodeUid { get; init; }

    /// <summary>GPUs, in placement order; empty on unified nodes.</summary>
    public IReadOnlyList<ProvenanceGpu> Gpus { get; init; } = [];

    /// <summary>Runtime kind.</summary>
    public RuntimeKind Runtime { get; init; } = RuntimeKind.Vllm;

    /// <summary>Container or process.</summary>
    public LaunchDelivery Delivery { get; init; } = LaunchDelivery.Container;

    /// <summary>Image digest the instance ran (container), when known.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageDigest { get; init; }

    /// <summary>Legacy config hash of the instance.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConfigHash { get; init; }

    /// <summary>Execution fingerprint of the instance.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutionFingerprint { get; init; }

    /// <summary>
    /// Version of the logical executable the instance ran (process delivery, e.g. llama.cpp <c>b11476</c>; MN-202, D116).
    /// Not part of <see cref="IsComparableWith"/> (as the image digest is not): results across versions stay comparable and
    /// the UI labels a version change (design-p3p4 §A.6). Omitted when null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutableVersion { get; init; }

    /// <summary>Data path: <c>agent-mtls-loopback</c>, <c>agent-mtls-lan</c>; null = direct (historical runs, D55/D59).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Proxy { get; init; }

    /// <summary>Whether other load on the node (or the GPUs) disturbed the run (design §6.5 item 4).</summary>
    public BenchmarkNoise Noise { get; init; } = BenchmarkNoise.Unknown;

    /// <summary>
    /// True when the two runs measured the same hardware and runtime: same node uid, same ordered GPU UUIDs, same runtime
    /// kind and delivery. (Settings and image may differ: that is what tuning compares.)
    /// </summary>
    public bool IsComparableWith(BenchmarkProvenance? other) =>
        other is not null &&
        string.Equals(NodeUid ?? NodeId, other.NodeUid ?? other.NodeId, StringComparison.Ordinal) &&
        Runtime == other.Runtime &&
        Delivery == other.Delivery &&
        Gpus.Select(g => g.Uuid).SequenceEqual(other.Gpus.Select(g => g.Uuid), StringComparer.Ordinal);
}

/// <summary>
/// Noise verdict of a run (design §6.5 item 4: counters diffed over the run; incomplete telemetry is "unknown", not "quiet").
/// Serialized as <c>unknown</c> / <c>quiet</c> / <c>noisy</c> by every serializer (W8 review MN-128: the admin API wrote
/// PascalCase while the store and the fixture write camelCase).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<BenchmarkNoise>))]
public enum BenchmarkNoise
{
    /// <summary>Telemetry for the run was incomplete: noise unknown.</summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown,

    /// <summary>No other model on the node (or sharing a GPU) served requests during the run.</summary>
    [JsonStringEnumMemberName("quiet")]
    Quiet,

    /// <summary>Other models served requests during the run.</summary>
    [JsonStringEnumMemberName("noisy")]
    Noisy,
}
