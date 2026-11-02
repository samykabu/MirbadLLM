using System.Text.Json;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Fencing;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Pki;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required here.

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// MN-014: the agent's fencing state (design §4.2 layer 2): the lower/equal/higher decision table, atomic persistence
/// across restarts, serialized checks and fail-closed loading.
/// </summary>
public sealed class FencingStateTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mn014-fencing-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(PkiTestSupport.Epoch);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    [Theory]
    [InlineData(null, null, 0L, "a", 0L, "x", FencingDecision.Accepted)] // nothing accepted yet
    [InlineData("a", "x", 5L, "a", 4L, "x", FencingDecision.StaleMaster)] // lower epoch, same instance
    [InlineData("a", "x", 5L, "b", 4L, "y", FencingDecision.StaleMaster)] // lower epoch, other instance
    [InlineData("a", "x", 5L, "a", 5L, "x", FencingDecision.Accepted)] // equal, same boot
    [InlineData("a", "x", 5L, "b", 5L, "x", FencingDecision.CompetingMaster)] // equal, other instance
    [InlineData("a", "x", 5L, "a", 5L, "y", FencingDecision.CompetingMaster)] // equal, other boot
    [InlineData("a", "x", 5L, "b", 6L, "y", FencingDecision.Accepted)] // higher epoch, anyone
    public void Decide_FollowsTheDecisionTable(
        string? acceptedInstance, string? acceptedBoot, long acceptedEpoch,
        string presentedInstance, long presentedEpoch, string presentedBoot, FencingDecision expected)
    {
        MasterTriple? accepted = acceptedInstance is null ? null : new MasterTriple(acceptedInstance, acceptedEpoch, acceptedBoot!);

        Assert.Equal(expected, FencingState.Decide(accepted, new MasterTriple(presentedInstance, presentedEpoch, presentedBoot)));
    }

    [Fact]
    public async Task FirstMaster_IsPersistedThenAccepted()
    {
        using FencingState state = new(_dir, _time);
        Assert.Null(state.Accepted);

        FencingCheck check = await state.CheckAsync(new MasterTriple("inst-a", 3, "boot-1"));

        Assert.Equal(FencingDecision.Accepted, check.Decision);
        Assert.Equal(new MasterTriple("inst-a", 3, "boot-1"), check.Accepted);
        Assert.Equal(new MasterTriple("inst-a", 3, "boot-1"), state.Accepted);
        FencingStateDocument document = ReadFile();
        Assert.Equal("inst-a", document.MasterInstanceId);
        Assert.Equal(3, document.Epoch);
        Assert.Equal("boot-1", document.BootId);
        Assert.Equal(PkiTestSupport.Epoch, document.AcceptedAt);
    }

    [Fact]
    public async Task LowerEpoch_IsStaleMaster_AndNothingChanges()
    {
        using FencingState state = new(_dir, _time);
        await state.CheckAsync(new MasterTriple("inst-a", 7, "boot-1"));
        string before = File.ReadAllText(state.FilePath);

        FencingCheck check = await state.CheckAsync(new MasterTriple("inst-a", 6, "boot-2"));

        Assert.Equal(FencingDecision.StaleMaster, check.Decision);
        Assert.Equal(new MasterTriple("inst-a", 7, "boot-1"), check.Accepted);
        Assert.Equal(new MasterTriple("inst-a", 7, "boot-1"), state.Accepted);
        Assert.Equal(before, File.ReadAllText(state.FilePath));
    }

    [Theory]
    [InlineData("inst-b", "boot-1")]
    [InlineData("inst-a", "boot-2")]
    [InlineData("inst-b", "boot-2")]
    public async Task EqualEpoch_WithAnotherInstanceOrBoot_IsCompetingMaster(string instanceId, string bootId)
    {
        using FencingState state = new(_dir, _time);
        await state.CheckAsync(new MasterTriple("inst-a", 4, "boot-1"));

        FencingCheck check = await state.CheckAsync(new MasterTriple(instanceId, 4, bootId));

        Assert.Equal(FencingDecision.CompetingMaster, check.Decision);
        Assert.Equal(new MasterTriple("inst-a", 4, "boot-1"), state.Accepted);
    }

    [Fact]
    public async Task EqualTriple_IsAccepted_WithoutRewritingTheFile()
    {
        using FencingState state = new(_dir, _time);
        await state.CheckAsync(new MasterTriple("inst-a", 4, "boot-1"));
        string before = File.ReadAllText(state.FilePath);
        _time.Advance(TimeSpan.FromHours(1));

        FencingCheck check = await state.CheckAsync(new MasterTriple("inst-a", 4, "boot-1"));

        Assert.Equal(FencingDecision.Accepted, check.Decision);
        Assert.Equal(before, File.ReadAllText(state.FilePath));
    }

    [Fact]
    public async Task HigherEpoch_IsPersisted_AndSurvivesARestart()
    {
        using (FencingState state = new(_dir, _time))
        {
            await state.CheckAsync(new MasterTriple("inst-a", 4, "boot-1"));
            FencingCheck check = await state.CheckAsync(new MasterTriple("inst-b", 9, "boot-7"));
            Assert.Equal(FencingDecision.Accepted, check.Decision);
        }

        using FencingState restarted = new(_dir, _time);

        Assert.Equal(new MasterTriple("inst-b", 9, "boot-7"), restarted.Accepted);
        Assert.Equal(FencingDecision.StaleMaster, (await restarted.CheckAsync(new MasterTriple("inst-a", 8, "boot-9"))).Decision);
        Assert.Equal(FencingDecision.CompetingMaster, (await restarted.CheckAsync(new MasterTriple("inst-a", 9, "boot-9"))).Decision);
        Assert.False(File.Exists(restarted.FilePath + ".tmp"));
    }

    [Fact]
    public async Task IsCurrent_IsTrueOnlyForTheAcceptedBoot()
    {
        using FencingState state = new(_dir, _time);
        MasterTriple first = new("inst-a", 1, "boot-1");
        Assert.False(state.IsCurrent(first));

        await state.CheckAsync(first);
        Assert.True(state.IsCurrent(first));
        Assert.True(state.IsCurrent(new MasterTriple("inst-a", 1, "boot-1")));
        Assert.False(state.IsCurrent(new MasterTriple("inst-a", 1, "boot-2")));

        // A newer master supersedes operations started by the first (design §4.2: re-check before destructive stages).
        await state.CheckAsync(new MasterTriple("inst-a", 2, "boot-2"));
        Assert.False(state.IsCurrent(first));
        Assert.True(state.IsCurrent(new MasterTriple("inst-a", 2, "boot-2")));
    }

    [Fact]
    public async Task ConcurrentChecks_AreSerialized_AndTheHighestEpochWins()
    {
        using FencingState state = new(_dir, _time);
        long[] epochs = [.. Enumerable.Range(1, 64).Select(i => (long)i).OrderBy(_ => Random.Shared.Next())];

        FencingCheck[] results = await Task.WhenAll(epochs.Select(e =>
            Task.Run(() => state.CheckAsync(new MasterTriple("inst-a", e, "boot-" + e)))));

        Assert.Equal(new MasterTriple("inst-a", 64, "boot-64"), state.Accepted);
        Assert.Equal(64, ReadFile().Epoch);
        Assert.Contains(results, r => r.Decision == FencingDecision.Accepted && r.Accepted!.Epoch == 64);
        Assert.All(results, r => Assert.NotEqual(FencingDecision.CompetingMaster, r.Decision));
    }

    [Fact]
    public async Task ConcurrentMastersAtOneEpoch_ExactlyOneWins()
    {
        using FencingState state = new(_dir, _time);

        FencingCheck[] results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => state.CheckAsync(new MasterTriple("inst-" + i, 5, "boot-" + i)))));

        FencingCheck winner = Assert.Single(results, r => r.Decision == FencingDecision.Accepted);
        Assert.Equal(19, results.Count(r => r.Decision == FencingDecision.CompetingMaster));
        Assert.Equal(winner.Accepted, state.Accepted);
        Assert.Equal(winner.Accepted!.InstanceId, ReadFile().MasterInstanceId);
    }

    [Fact]
    public async Task PersistFailure_IsStateUnavailable_AndKeepsThePreviousTriple()
    {
        using FencingState state = new(_dir, _time);
        await state.CheckAsync(new MasterTriple("inst-a", 1, "boot-1"));
        Directory.CreateDirectory(state.FilePath + ".tmp"); // the temp file cannot be created

        AgentException ex = await Assert.ThrowsAsync<AgentException>(() => state.CheckAsync(new MasterTriple("inst-a", 2, "boot-2")));

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal(ErrorCodes.StateUnavailable, ex.Code);
        Assert.Equal(new MasterTriple("inst-a", 1, "boot-1"), state.Accepted);
        Assert.False(state.IsCurrent(new MasterTriple("inst-a", 2, "boot-2")));
        Assert.Equal(1, ReadFile().Epoch);

        Directory.Delete(state.FilePath + ".tmp");
        Assert.Equal(FencingDecision.Accepted, (await state.CheckAsync(new MasterTriple("inst-a", 2, "boot-2"))).Decision);
        Assert.Equal(2, ReadFile().Epoch);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":1,\"masterInstanceId\":\"\",\"epoch\":1,\"bootId\":\"b\"}")]
    [InlineData("{\"schemaVersion\":1,\"masterInstanceId\":\"a\",\"epoch\":-1,\"bootId\":\"b\"}")]
    [InlineData("{\"schemaVersion\":99,\"masterInstanceId\":\"a\",\"epoch\":1,\"bootId\":\"b\"}")]
    public void CorruptStateFile_StopsTheAgent_InsteadOfForgettingTheEpoch(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, FencingStateDocument.FileName), content);

        Assert.Throws<InvalidDataException>(() => new FencingState(_dir, _time));
    }

    [Theory]
    [InlineData("", 1, "b")]
    [InlineData("a", 1, "")]
    [InlineData("a b", 1, "b")]
    [InlineData("a", -1, "b")]
    public async Task MalformedTriple_IsRefused(string instanceId, long epoch, string bootId)
    {
        using FencingState state = new(_dir, _time);

        await Assert.ThrowsAsync<ArgumentException>(() => state.CheckAsync(new MasterTriple(instanceId, epoch, bootId)));
        Assert.Null(state.Accepted);
    }

    private FencingStateDocument ReadFile() =>
        JsonSerializer.Deserialize<FencingStateDocument>(
            File.ReadAllText(Path.Combine(_dir, FencingStateDocument.FileName)),
            WebJson)!;
}
