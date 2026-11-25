using System.Net;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Downloads.Native;
using MirbadLLM.Pki;
using MirbadLLM.Orchestration;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707 // underscore test names read better for these format/grouping scenarios.

namespace MirbadLLM.UnitTests.Downloads;

/// <summary>
/// <see cref="HuggingFaceRepoInspector.BuildInspection"/> (pure grouping/format-detection, no network) against
/// a fixture built from the real <c>orcarouter/Qwen3.8-Flash-Next-Uncensored-GGUF</c> file listing (a subset:
/// five quant variants with their real split-part sizes, its MTP-draft and mmproj auxiliary files, and its
/// two non-GGUF files) — the repo that reproduced the original "Completed at 0 bytes" bug.
/// </summary>
public sealed class HuggingFaceRepoInspectorTests
{
    private const string Prefix = "Qwen3.8-Flash-Next-Uncensored-";

    /// <summary>The real file list (byte-exact), trimmed to the variants named in the bug report.</summary>
    private static readonly IReadOnlyList<RepoFile> GgufRepoFixture =
    [
        new($"{Prefix}Q2_K-00001-of-00002.gguf", 44_893_254_944),
        new($"{Prefix}Q2_K-00002-of-00002.gguf", 35_554_194_880),
        new($"{Prefix}Q4_K_M-00001-of-00003.gguf", 44_598_509_600),
        new($"{Prefix}Q4_K_M-00002-of-00003.gguf", 44_693_535_520),
        new($"{Prefix}Q4_K_M-00003-of-00003.gguf", 29_858_677_824),
        new($"{Prefix}Q8_0-00001-of-00005.gguf", 5_566_294_560),
        new($"{Prefix}Q8_0-00002-of-00005.gguf", 54_400_261_312),
        new($"{Prefix}Q8_0-00003-of-00005.gguf", 46_969_998_688),
        new($"{Prefix}Q8_0-00004-of-00005.gguf", 46_887_247_200),
        new($"{Prefix}Q8_0-00005-of-00005.gguf", 34_382_716_608),
        new($"{Prefix}Q8_0-MTP-00001-of-00005.gguf", 5_566_294_592),
        new($"{Prefix}Q8_0-MTP-00002-of-00005.gguf", 54_400_261_312),
        new($"{Prefix}Q8_0-MTP-00003-of-00005.gguf", 46_903_029_984),
        new($"{Prefix}Q8_0-MTP-00004-of-00005.gguf", 46_901_260_896),
        new($"{Prefix}Q8_0-MTP-00005-of-00005.gguf", 37_209_759_328),
        new($"{Prefix}MTP-draft.gguf", 4_135_893_632),
        new($"mmproj-{Prefix}F16.gguf", 907_543_296),
        new($"{Prefix}IQ2_XXS-00001-of-00002.gguf", 44_615_966_944),
        new($"{Prefix}IQ2_XXS-00002-of-00002.gguf", 30_232_371_488),
        new("README.md", 12_947),
        new(".gitattributes", 6_181),
    ];

    [Fact]
    public async Task Inspect_UsesConfiguredOriginForPublicListing_AndOnlyOfficialForToken()
    {
        const string repo = "org/repo";
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        using var ca = CertificateAuthority.Create();
        using var credential = ca.IssueEnrollServerCertificate(["127.0.0.1"]);
        using var leaf = credential.CreateTlsCertificate();
        await using var official = await NativeDownloaderTests.FakeHub.StartAsync(leaf);
        await using var mirror = await NativeDownloaderTests.FakeHub.StartAsync(leaf);
        var file = NativeDownloaderTests.FakeFile.Small("sub/config.json", "123456");
        official.AddRepo(repo).Commit(sha, file);
        mirror.AddRepo(repo).Commit(sha, file);
        using var officialHttp = HubMirrorTests.OpenFake(official.BaseUri, ca);
        var clients = new HubHttpClients(officialHttp, () => [ca.Certificate.ExportCertificatePem()]);
        var inspector = new HuggingFaceRepoInspector(officialHttp, clients, HubOrigin.Parse(mirror.BaseUri.AbsoluteUri, null));

        var result = await inspector.InspectAsync(repo, null, null, TestContext.Current.CancellationToken);
        Assert.Equal(file.Content.Length, Assert.Single(result.AllFiles).Bytes);
        var mirrorRequest = Assert.Single(mirror.Requests);
        Assert.Equal($"/api/models/{repo}/revision/{sha}", mirrorRequest.Path);
        Assert.Null(mirrorRequest.Authorization);

        await inspector.InspectAsync(repo, "main", "test-token", TestContext.Current.CancellationToken);
        Assert.Single(mirror.Requests);
        Assert.Contains(official.Requests, request => request.Path == $"/api/models/{repo}/revision/main" &&
            request.Authorization == "Bearer test-token");
    }

    [Fact]
    public async Task Inspect_OfficialAliasFollowsHubRedirectAndPreservesSameOriginCredentials()
    {
        using var handler = new FakeHttpMessageHandler
        {
            Handler = request => request.RequestUri!.AbsolutePath == "/api/models/alias"
                ? new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    Headers = { Location = new Uri("/api/models/org/current?blobs=true", UriKind.Relative) },
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"siblings":[{"rfilename":"config.json","size":19}]}"""),
                },
        };
        using var http = new HttpClient(handler) { BaseAddress = HubOrigin.OfficialEndpoint };
        var inspector = new HuggingFaceRepoInspector(http, new HubHttpClients(http, () => []));

        var listing = await inspector.InspectAsync("alias", null, "test-token", TestContext.Current.CancellationToken);
        Assert.Equal(19, Assert.Single(listing.AllFiles).Bytes);
        Assert.Equal(["https://huggingface.co/api/models/alias?blobs=true", "https://huggingface.co/api/models/org/current?blobs=true"],
            handler.Requests.Select(request => request.RequestUri!.AbsoluteUri));
        Assert.All(handler.Requests, request => Assert.Equal("test-token", request.Headers.Authorization?.Parameter));
    }

    [Fact]
    public void BuildInspection_GgufOnlyRepo_DetectsGgufFormat()
    {
        var inspection = HuggingFaceRepoInspector.BuildInspection("orcarouter/Qwen3.8-Flash-Next-Uncensored-GGUF", null, GgufRepoFixture);

        Assert.Equal(RepoFormat.Gguf, inspection.Format);
        Assert.Equal(GgufRepoFixture.Count, inspection.AllFiles.Count);
    }

    [Fact]
    public void BuildInspection_GroupsSplitPartsIntoVariants_StrippedOfTheRepoPrefix()
    {
        var inspection = HuggingFaceRepoInspector.BuildInspection("orcarouter/Qwen3.8-Flash-Next-Uncensored-GGUF", null, GgufRepoFixture);

        var byName = inspection.Variants.ToDictionary(v => v.Name, StringComparer.Ordinal);
        Assert.Equal(["IQ2_XXS", "Q2_K", "Q4_K_M", "Q8_0", "Q8_0-MTP"], byName.Keys.OrderBy(n => n, StringComparer.Ordinal));

        AssertVariant(byName["Q2_K"], 2, 44_893_254_944 + 35_554_194_880);
        AssertVariant(byName["Q4_K_M"], 3, 44_598_509_600 + 44_693_535_520 + 29_858_677_824);
        AssertVariant(byName["Q8_0"], 5, 5_566_294_560 + 54_400_261_312 + 46_969_998_688 + 46_887_247_200 + 34_382_716_608);
        AssertVariant(byName["Q8_0-MTP"], 5, 5_566_294_592 + 54_400_261_312 + 46_903_029_984 + 46_901_260_896 + 37_209_759_328);
        AssertVariant(byName["IQ2_XXS"], 2, 44_615_966_944 + 30_232_371_488);
    }

    private static void AssertVariant(GgufVariant variant, int expectedFileCount, long expectedTotalBytes)
    {
        Assert.Equal(expectedFileCount, variant.FileCount);
        Assert.Equal(expectedFileCount, variant.Files.Count);
        Assert.Equal(expectedTotalBytes, variant.TotalBytes);
    }

    [Fact]
    public void BuildInspection_PutsTheDraftModelAndVisionProjector_InAuxiliaryFiles_NotAsVariants()
    {
        var inspection = HuggingFaceRepoInspector.BuildInspection("orcarouter/Qwen3.8-Flash-Next-Uncensored-GGUF", null, GgufRepoFixture);

        Assert.Equal(2, inspection.AuxiliaryFiles.Count);
        Assert.Contains(inspection.AuxiliaryFiles, f => f.Name == $"{Prefix}MTP-draft.gguf" && f.Bytes == 4_135_893_632);
        Assert.Contains(inspection.AuxiliaryFiles, f => f.Name == $"mmproj-{Prefix}F16.gguf" && f.Bytes == 907_543_296);

        // "Q8_0-MTP" is a full quant split into 5 parts, not the single-file MTP-draft head: "MTP" alone must
        // not be enough to classify a file as auxiliary, or the 5-part variant above would disappear.
        Assert.DoesNotContain(inspection.AuxiliaryFiles, f => f.Name.Contains("Q8_0-MTP", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildInspection_NonGgufFiles_GoInOtherFiles()
    {
        var inspection = HuggingFaceRepoInspector.BuildInspection("orcarouter/Qwen3.8-Flash-Next-Uncensored-GGUF", null, GgufRepoFixture);

        Assert.Equal(2, inspection.OtherFiles.Count);
        Assert.Contains(inspection.OtherFiles, f => f.Name == "README.md");
        Assert.Contains(inspection.OtherFiles, f => f.Name == ".gitattributes");
    }

    [Fact]
    public void BuildInspection_SafetensorsOnlyRepo_DetectsSafetensorsFormat_NoVariants()
    {
        IReadOnlyList<RepoFile> files =
        [
            new RepoFile("config.json", 100),
            new RepoFile("model.safetensors", 900),
            new RepoFile("tokenizer.json", 50),
        ];

        var inspection = HuggingFaceRepoInspector.BuildInspection("ornith-ai/Ornith-1.5-9B", null, files);

        Assert.Equal(RepoFormat.Safetensors, inspection.Format);
        Assert.Empty(inspection.Variants);
        Assert.Empty(inspection.AuxiliaryFiles);
        Assert.Equal(3, inspection.OtherFiles.Count);
    }

    [Fact]
    public void BuildInspection_BothSafetensorsAndGguf_IsMixed()
    {
        IReadOnlyList<RepoFile> files =
        [
            new RepoFile("model.safetensors", 900),
            new RepoFile("model-Q4_K_M.gguf", 500),
        ];

        var inspection = HuggingFaceRepoInspector.BuildInspection("some-org/mixed-repo", null, files);

        Assert.Equal(RepoFormat.Mixed, inspection.Format);
        Assert.Single(inspection.Variants);
    }

    [Fact]
    public void BuildInspection_NeitherSafetensorsNorGguf_IsOther()
    {
        IReadOnlyList<RepoFile> files = [new RepoFile("model.onnx", 900), new RepoFile("README.md", 10)];

        var inspection = HuggingFaceRepoInspector.BuildInspection("some-org/onnx-repo", null, files);

        Assert.Equal(RepoFormat.Other, inspection.Format);
        Assert.Empty(inspection.Variants);
    }

    [Fact]
    public void BuildInspection_SingleFileGgufRepo_NoSplitParts_IsItsOwnVariant()
    {
        IReadOnlyList<RepoFile> files = [new RepoFile("tinymodel-Q4_0.gguf", 500)];

        var inspection = HuggingFaceRepoInspector.BuildInspection("some-org/tiny-gguf", null, files);

        var variant = Assert.Single(inspection.Variants);
        Assert.Equal("tinymodel-Q4_0", variant.Name);
        Assert.Equal(1, variant.FileCount);
        Assert.Equal(500, variant.TotalBytes);
    }
}
