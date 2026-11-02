using MirbadLLM.Core;
using MirbadLLM.Web;

namespace MirbadLLM.UnitTests.Web;

/// <summary>Release notes between two builds (docs/multinode/agent-updates.md U1/U2) and the manual update steps (U9).</summary>
public sealed class ReleaseNotesTests
{
    private static ReleaseEntry Pr(int number, string commit, int day, string? added = null, string? fixedText = null) => new()
    {
        Number = number,
        Title = $"PR {number}",
        Commit = commit,
        MergedAt = new DateTimeOffset(2026, 10, day, 12, 0, 0, TimeSpan.Zero),
        Added = added is null ? [] : [added],
        Fixed = fixedText is null ? [] : [fixedText],
    };

    private static readonly ReleaseNotes Notes = new(
    [
        Pr(153, "8bb626717368aaaa", 15, added: "MLX"),
        Pr(155, "826bc75fc222bbbb", 17, added: "Apple GPU"),
        Pr(154, "5e86a49365aecccc", 16, fixedText: "capabilities refresh"),
        Pr(156, "f7dce9640000dddd", 17, fixedText: "install-hostd"),
    ]);

    [Fact]
    public void TheRangeIsEveryEntryAfterTheOlderBuildUpToTheNewerNewestFirst()
    {
        var range = Notes.Between("8bb626717368", "826bc75fc222");

        Assert.Equal([155, 154], range.Entries.Select(e => e.Number));
        Assert.False(range.Truncated);
        Assert.True(range.Available);
    }

    [Fact]
    public void WithoutAnEndTheRangeRunsToTheNewestAndTheSameBuildIsEmpty()
    {
        Assert.Equal([156, 155, 154], Notes.Between("8bb6267").Entries.Select(e => e.Number));
        Assert.Empty(Notes.Between("826bc75fc222", "826bc75fc222").Entries);
    }

    [Fact]
    public void AnUnknownOrMissingStartListsTheLatestAndSaysItIsTruncated()
    {
        var unknown = Notes.Between("2d71c7d97d84", "826bc75fc222");
        Assert.True(unknown.Truncated);
        Assert.Equal([155, 154, 153], unknown.Entries.Select(e => e.Number));

        Assert.True(Notes.Between(null).Truncated);
    }

    [Fact]
    public void ADirtyBuildIsFoundByItsCommit() =>
        Assert.Equal(155, Notes.Find("826bc75fc222-dirty")!.Number);

    [Fact]
    public void AMissingOrBrokenFileMeansNoNotesNotAnError()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mirbadllm-notes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(ReleaseNotes.Load(Path.Combine(dir, "none.json")).Available);
            File.WriteAllText(Path.Combine(dir, "broken.json"), "{ not json");
            Assert.False(ReleaseNotes.Load(Path.Combine(dir, "broken.json")).Available);
            File.WriteAllText(Path.Combine(dir, "ok.json"), """
                {"error": null, "entries": [
                  {"number": 155, "title": "macOS Phase 4", "mergedAt": "2026-10-17T12:40:00Z", "commit": "826bc75fc222bbbb",
                   "url": "https://github.com/x/y/pull/155", "added": ["Apple GPU telemetry"], "fixed": [], "changed": []}]}
                """);
            var notes = ReleaseNotes.Load(Path.Combine(dir, "ok.json"));
            Assert.True(notes.Available);
            Assert.Equal("Apple GPU telemetry", Assert.Single(notes.Entries).Added[0]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TheCommittedPlaceholderLoadsAsNoNotes() =>
        Assert.False(ReleaseNotes.Load(MirbadLLM.UnitTests.HostAgent.HostAgentFixtures.FindRepoFile(Path.Combine("deploy", "release-notes.json"))).Available);

    // ---- Fleet setting ----

    [Fact]
    public void AutomaticAgentUpdatesAreOffUntilSetAndTheSettingIsKept()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mirbadllm-updates-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MirbadLLM.Orchestration.Agents.AgentUpdateSettingsStore(Path.Combine(dir, MirbadLLM.Orchestration.Agents.AgentUpdateSettingsStore.FileName));
            Assert.False(store.Automatic);
            store.SetAutomatic(true);
            Assert.True(new MirbadLLM.Orchestration.Agents.AgentUpdateSettingsStore(Path.Combine(dir, MirbadLLM.Orchestration.Agents.AgentUpdateSettingsStore.FileName)).Automatic);
            store.SetAutomatic(false);
            Assert.False(store.Automatic);
            Assert.False(new MirbadLLM.Orchestration.Agents.AgentUpdateSettingsStore(null).Automatic);
            Assert.Throws<InvalidOperationException>(() => new MirbadLLM.Orchestration.Agents.AgentUpdateSettingsStore(null).SetAutomatic(true));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ---- Manual update steps ----

    [Theory]
    [InlineData("linux", "amd64", "linux-x64")]
    [InlineData("linux", "arm64", "linux-arm64")]
    [InlineData("windows", "amd64", "win-x64")]
    [InlineData("macos", "arm64", "osx-arm64")]
    [InlineData("macos", "amd64", null)]
    [InlineData("freebsd", "amd64", null)]
    public void EachNodeGetsTheBundleOfItsOsAndArchitecture(string os, string arch, string? rid) =>
        Assert.Equal(rid, AgentUpdatePlan.RidFor(new NodeCapabilities
        {
            Protocol = MirbadLLM.Agent.Contracts.Protocol.Range,
            AgentVersion = "1.0.0",
            Os = new NodeOsInfo(os, os),
            Arch = arch,
            Memory = new NodeMemoryInfo(MemoryModel.Unified, 8),
        }));

    [Fact]
    public void TheStepsCheckTheHashAndRunTheNewBundlesOwnUpgrade()
    {
        const string Sha = "ab12";
        var mac = AgentUpdatePlan.Build("osx-arm64", "http://m:8080/agent/bundles/osx-arm64.tar.gz", Sha, "826bc75fc222", "mirbadllm-agent-826bc75fc222-osx-arm64");
        Assert.Equal(
            "curl -fLO http://m:8080/agent/bundles/osx-arm64.tar.gz\necho 'ab12  osx-arm64.tar.gz' | shasum -a 256 -c\ntar -xzf osx-arm64.tar.gz\nsudo ./mirbadllm-agent-826bc75fc222-osx-arm64/install-agent.sh upgrade",
            mac.Commands);

        var linux = AgentUpdatePlan.Build("linux-x64", "http://m:8080/agent/bundles/linux-x64.tar.gz", Sha, "v", "f");
        Assert.Contains("| sha256sum -c", linux.Commands, StringComparison.Ordinal);

        var windows = AgentUpdatePlan.Build("win-x64", "http://m:8080/agent/bundles/win-x64.zip", Sha, "v", "mirbadllm-agent-v-win-x64");
        Assert.EndsWith(@".\mirbadllm-agent-v-win-x64\install-agent.ps1 upgrade -Bundle .\win-x64.zip -BundleSha256 ab12", windows.Commands, StringComparison.Ordinal);
        Assert.Contains("administrator", windows.Shell, StringComparison.Ordinal);
    }
}
