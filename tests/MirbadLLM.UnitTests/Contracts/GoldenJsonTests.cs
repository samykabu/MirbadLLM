using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MirbadLLM.Agent.Contracts;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Contracts;

/// <summary>
/// Golden JSON for every P1 protocol DTO (MN-003, rewritten by MN-090). Each <c>Fixtures/{TypeName}.json</c> is
/// <b>deserialized into its DTO</b> with the wire options (<see cref="AgentJson.Options"/>: web defaults, camelCase string
/// enums) plus <see cref="JsonUnmappedMemberHandling.Disallow"/>, re-serialized, and compared with
/// a structural comparison in <b>both</b> directions. So a renamed or removed property (the fixture key no
/// longer maps), an added property (the output gains a key the fixture lacks), a changed enum spelling, default or
/// required member all fail here. Event fixtures go through the <see cref="AgentEvent"/> base type, as on the wire, so the
/// <c>type</c> discriminator is part of the contract. The test only reads the fixtures; it never writes them. When a DTO
/// changes on purpose (additive, through the wave review), update its fixture by hand in the same commit.
/// </summary>
public sealed class GoldenJsonTests
{
    /// <summary>Fixtures from MN-001 with their own tests (file formats, not route DTOs).</summary>
    private static readonly HashSet<string> NonDtoFixtures = new(StringComparer.Ordinal)
    {
        "master.v1.json", "models.v2.json", "mutation-envelope.json", "nodes.v1.json", "operation-log.jsonl",
    };

    private static readonly JsonSerializerOptions Strict = CreateStrict();

    /// <summary>Route DTOs first; nested record types (e.g. <c>GpuSample</c>) may have their own fixture too.</summary>
    private static readonly Dictionary<string, Type> TypesByName = BuildTypeMap();

    /// <summary>Every route DTO and event type, plus every other fixture file that names a contract type.</summary>
    public static TheoryData<string> FixtureNames() =>
    [
        .. AgentRoutes.DtoTypes.Select(t => t.Name)
            .Union(Directory.GetFiles(FixturesPath(), "*.json").Select(Path.GetFileName).Where(n => !NonDtoFixtures.Contains(n!))
                .Select(n => Path.GetFileNameWithoutExtension(n!)))
            .Order(StringComparer.Ordinal),
    ];

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Fixture_RoundTripsExactly_ThroughItsDto(string typeName)
    {
        Assert.True(TypesByName.TryGetValue(typeName, out Type? type), $"{typeName}.json names no type in Agent.Contracts or Core.");
        string path = Path.Combine(FixturesPath(), typeName + ".json");
        Assert.True(File.Exists(path), $"Missing golden fixture for {typeName}: {path}");
        JsonNode fixture = JsonNode.Parse(File.ReadAllText(path))!;

        Type wireType = typeof(AgentEvent).IsAssignableFrom(type) ? typeof(AgentEvent) : type;
        object? value;
        try
        {
            value = fixture.Deserialize(wireType, Strict);
        }
        catch (JsonException ex)
        {
            Assert.Fail($"{typeName}.json does not match the DTO: {ex.Message}");
            return;
        }

        Assert.NotNull(value);
        Assert.IsType(type, value);
        JsonNode output = JsonSerializer.SerializeToNode(value, wireType, AgentJson.Options)!;

        string? difference = Difference(fixture, output, "$");
        Assert.True(
            difference is null,
            $"{typeName}.json does not round-trip: {difference}\nfixture: {fixture.ToJsonString()}\noutput:  {output.ToJsonString()}");
    }

    /// <summary>
    /// Exact structural comparison in both directions (same keys, same array lengths, same kinds), with two value
    /// equivalences that are not contract changes: numbers by value (<c>121.0</c> = <c>121</c>) and ISO-8601 timestamps
    /// by instant and offset (<c>Z</c> = <c>+00:00</c>). Returns the first difference, or null.
    /// </summary>
    private static string? Difference(JsonNode? expected, JsonNode? actual, string path)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null ? null : $"{path}: {Show(expected)} vs {Show(actual)}";
        }

        if (expected.GetValueKind() != actual.GetValueKind())
        {
            return $"{path}: {expected.GetValueKind()} vs {actual.GetValueKind()}";
        }

        switch (expected)
        {
            case JsonObject e:
                var a = actual.AsObject();
                foreach (var key in e.Select(p => p.Key).Union(a.Select(p => p.Key)))
                {
                    if (!e.ContainsKey(key))
                    {
                        return $"{path}.{key}: missing from the fixture (the DTO writes it)";
                    }

                    if (!a.ContainsKey(key))
                    {
                        return $"{path}.{key}: not written by the DTO";
                    }

                    if (Difference(e[key], a[key], $"{path}.{key}") is { } inner)
                    {
                        return inner;
                    }
                }

                return null;
            case JsonArray e:
                var arr = actual.AsArray();
                if (e.Count != arr.Count)
                {
                    return $"{path}: {e.Count} items vs {arr.Count}";
                }

                for (int i = 0; i < e.Count; i++)
                {
                    if (Difference(e[i], arr[i], $"{path}[{i}]") is { } inner)
                    {
                        return inner;
                    }
                }

                return null;
            default:
                return ScalarEquals(expected.AsValue(), actual.AsValue()) ? null : $"{path}: {Show(expected)} vs {Show(actual)}";
        }
    }

    private static bool ScalarEquals(JsonValue expected, JsonValue actual)
    {
        if (expected.GetValueKind() == JsonValueKind.Number)
        {
            return expected.GetValue<decimal>() == actual.GetValue<decimal>();
        }

        if (expected.GetValueKind() == JsonValueKind.String)
        {
            string e = expected.GetValue<string>();
            string a = actual.GetValue<string>();
            if (string.Equals(e, a, StringComparison.Ordinal))
            {
                return true;
            }

            return DateTimeOffset.TryParse(e, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var de)
                && DateTimeOffset.TryParse(a, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var da)
                && de == da && de.Offset == da.Offset;
        }

        return JsonNode.DeepEquals(expected, actual);
    }

    private static string Show(JsonNode? node) => node?.ToJsonString() ?? "null";

    [Fact]
    public void EveryRouteDto_HasAFixture()
    {
        var missing = AgentRoutes.DtoTypes.Where(t => !File.Exists(Path.Combine(FixturesPath(), t.Name + ".json"))).Select(t => t.Name).ToList();

        Assert.True(missing.Count == 0, "Route DTOs without a golden fixture: " + string.Join(", ", missing));
    }

    private static Dictionary<string, Type> BuildTypeMap()
    {
        var map = AgentRoutes.DtoTypes.ToDictionary(t => t.Name, StringComparer.Ordinal);
        foreach (var type in typeof(AgentRoutes).Assembly.GetExportedTypes().Concat(typeof(MirbadLLM.Core.ModelDefinition).Assembly.GetExportedTypes()))
        {
            if (type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } && !map.ContainsKey(type.Name))
            {
                map[type.Name] = type;
            }
        }

        return map;
    }

    [Fact]
    public void EveryEventType_IsCovered()
    {
        foreach (var attribute in typeof(AgentEvent).GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false).Cast<JsonDerivedTypeAttribute>())
        {
            Assert.Contains(attribute.DerivedType, AgentRoutes.DtoTypes);
        }
    }

    [Fact]
    public void TheStrictOptions_RefuseAnUnknownProperty()
    {
        // Guards the guard: without Disallow a renamed property would be dropped silently and only the reverse
        // comparison would notice.
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OperationCancelRequest>(
            """{"operationId":"op-1","issuedAt":"2026-10-03T09:00:00Z","expiresAt":"2026-10-04T09:00:00Z","nope":1}""", Strict));
    }

    private static JsonSerializerOptions CreateStrict() =>
        new(AgentJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    private static string FixturesPath([CallerFilePath] string sourceFile = "")
    {
        string path = Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures");
        Assert.True(Directory.Exists(path), $"Fixtures directory not found: {path}");
        return path;
    }
}
