using System.Text.Json;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Web.Api;

namespace MirbadLLM.UnitTests.Multinode;

#pragma warning disable CA1707 // MethodUnderTest_Scenario_ExpectedBehavior test naming style, used throughout this project.

/// <summary>
/// <see cref="ModelDefinition.ImageDigest"/> (W7 review MN-126, closing the MN-107 gap): a model's own image can be
/// digest-pinned; omitted when null so P1 definitions, fixtures and the DGX's launches are unchanged.
/// </summary>
public sealed class ModelImageDigestTests
{
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string NodeDigest = "sha256:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private static readonly ModelDefinition Model = new()
    {
        Id = "own-image",
        ServedName = "own-image",
        ModelPath = "OwnImage",
        Placement = new ModelPlacement("pc"),
    };

    private static readonly NodeRecord Node = new()
    {
        Id = "pc",
        Uid = "uid-pc",
        Address = "https://pc:9443",
        DefaultVllmImage = "vllm/vllm-openai:v1",
        DefaultVllmImageDigest = NodeDigest,
    };

    [Fact]
    public void AnOwnImage_SendsItsOwnDigest_OrNone()
    {
        var pinned = Model with { Image = "vllm/vllm-openai:v2", ImageDigest = Digest };
        Assert.Equal(Digest, LaunchSpecBuilder.ResolveImageDigest(pinned, Node, LaunchSpecBuilder.ResolveImage(pinned, Node.DefaultVllmImage!)));

        // An own image without a digest never borrows the node default's digest (it would be refused as a mismatch).
        var unpinned = Model with { Image = "vllm/vllm-openai:v1" };
        Assert.Null(LaunchSpecBuilder.ResolveImageDigest(unpinned, Node, Node.DefaultVllmImage!));
    }

    [Fact]
    public void WithoutAnOwnImage_TheNodeDefaultDigestApplies_AndAStrayDigestIsIgnored()
    {
        var stray = Model with { ImageDigest = Digest };
        Assert.Equal(NodeDigest, LaunchSpecBuilder.ResolveImageDigest(stray, Node, Node.DefaultVllmImage!));
        Assert.Null(LaunchSpecBuilder.ResolveImageDigest(stray, Node with { DefaultVllmImage = null, DefaultVllmImageDigest = null }, "vllm/global:latest"));
    }

    [Fact]
    public void TheField_IsOmittedWhenNull_AndRoundTrips()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.DoesNotContain("imageDigest", JsonSerializer.Serialize(Model, options), StringComparison.Ordinal);
        string json = JsonSerializer.Serialize(Model with { Image = "x/y:z", ImageDigest = Digest }, options);
        Assert.Equal(Digest, JsonSerializer.Deserialize<ModelDefinition>(json, options)!.ImageDigest);
    }

    [Theory]
    [InlineData("sha256:ABC")]
    [InlineData("sha256:0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("md5:0123456789abcdef0123456789abcdef")]
    [InlineData("")]
    public void TheRegistry_RefusesAMalformedDigest(string digest)
    {
        Assert.Throws<ArgumentException>(() => JsonModelRegistry.Validate(Model with { Image = "x/y:z", ImageDigest = digest }));
    }

    [Fact]
    public void TheRegistry_AcceptsAWellFormedDigest() =>
        JsonModelRegistry.Validate(Model with { Image = "x/y:z", ImageDigest = Digest });

    [Fact]
    public void ThePutMerge_KeepsAnAbsentDigest_OnlyWhileTheImageStaysTheSame()
    {
        var stored = Model with { Image = "x/y:1", ImageDigest = Digest };
        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(Digest, AdminApiEndpoints.MergeAbsentFields(Model with { Image = "x/y:1" }, stored, none).ImageDigest);
        Assert.Null(AdminApiEndpoints.MergeAbsentFields(Model with { Image = "x/y:2" }, stored, none).ImageDigest);
        Assert.Null(AdminApiEndpoints.MergeAbsentFields(
            Model with { Image = "x/y:1" }, stored, new HashSet<string>(["ImageDigest"], StringComparer.OrdinalIgnoreCase)).ImageDigest);
    }
}
