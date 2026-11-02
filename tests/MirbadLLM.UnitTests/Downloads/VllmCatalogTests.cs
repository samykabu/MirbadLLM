using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.UnitTests.Multinode;

namespace MirbadLLM.UnitTests.Downloads;

/// <summary>
/// The model editor's vLLM catalog (<see cref="RemoteVllmCatalogService"/>): the fixed vLLM option lists (sourced from
/// docs/phase0-results.md's verified <c>local/qwen38-vllm-dflash2:66e5414c6d75</c> build) plus the node's image list,
/// which keeps the default and the model's current image even when the node does not report them. The image lists
/// expected here are the pre-P1 local catalog's (<c>VllmCatalogService</c> over the Docker socket, deleted by MN-047,
/// D60), recorded from it for the same images before its removal.
/// </summary>
public sealed class VllmCatalogTests
{
    private const string DefaultImage = "local/qwen38-vllm-dflash2:66e5414c6d75";

    /// <summary>The images the recordings were made with (Docker's <c>RepoTags</c>, an untagged one included).</summary>
    private static readonly string[] HostImages = [DefaultImage, "nvcr.io/nvidia/vllm:26.01", "<none>:<none>", "ollama/ollama:latest"];

    private static NodeCapabilities Capabilities(IEnumerable<string> images) => new()
    {
        Protocol = Protocol.Range,
        AgentVersion = "test",
        Os = new NodeOsInfo("linux", "test"),
        Arch = "arm64",
        Memory = new NodeMemoryInfo(MemoryModel.Unified, 128),
        Images = [.. images.Select(i => new ImageInfo { Ref = i })],
    };

    [Fact]
    public async Task ExposesTheFixedOptionLists()
    {
        var service = new RemoteVllmCatalogService(new FakeNodeRegistry(), Options.Create(new MirbadLLMOptions()));

        var catalog = await service.GetAsync("dgx", currentImage: null, TestContext.Current.CancellationToken);

        Assert.Contains("qwen3_coder", catalog.ToolCallParsers);
        Assert.Contains("qwen3", catalog.ReasoningParsers);
        Assert.Equal(VllmOptionsCatalog.MaxModelLenPresets, catalog.MaxModelLenPresets);
        Assert.Equal(VllmOptionsCatalog.MaxNumSeqsPresets, catalog.MaxNumSeqsPresets);
        Assert.Contains(catalog.Quantizations, o => o.Value == "fp8");
        Assert.Contains(catalog.CudaGraphModes, o => o.Value == "PIECEWISE");
        Assert.Contains(catalog.SpeculativeMethods, o => o.Value == "mtp");
    }

    [Fact]
    public void ImagesFilterToVllmAndSkipUntaggedAsRecorded()
    {
        Assert.Equal(
            [DefaultImage, "nvcr.io/nvidia/vllm:26.01"],
            RemoteVllmCatalogService.SelectImages(Capabilities(HostImages), DefaultImage, currentImage: null));
    }

    [Fact]
    public void ImagesAlwaysIncludeTheDefaultImageFirstAsRecorded()
    {
        Assert.Equal(
            ["local/some-other-default:tag", DefaultImage, "nvcr.io/nvidia/vllm:26.01"],
            RemoteVllmCatalogService.SelectImages(Capabilities(HostImages), "local/some-other-default:tag", currentImage: null));
    }

    [Fact]
    public void ImagesKeepTheCurrentImageFirstEvenWhenTheNodeDoesNotReportItAsRecorded()
    {
        Assert.Equal(
            ["someone/custom-vllm-build:v3", DefaultImage, "nvcr.io/nvidia/vllm:26.01"],
            RemoteVllmCatalogService.SelectImages(Capabilities(HostImages), DefaultImage, "someone/custom-vllm-build:v3"));
    }

    [Fact]
    public void ImagesWithoutAReportAreTheDefaultImageAsRecordedWithDockerDown()
    {
        Assert.Equal([DefaultImage], RemoteVllmCatalogService.SelectImages(null, DefaultImage, currentImage: null));
        Assert.Equal([DefaultImage], RemoteVllmCatalogService.SelectImages(Capabilities([]), DefaultImage, currentImage: null));
    }
}
