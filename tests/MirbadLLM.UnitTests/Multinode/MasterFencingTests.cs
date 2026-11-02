using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Fencing;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required here.

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>MN-007: master fencing (design §4.2 layers 1–3, master side).</summary>
public sealed class MasterFencingTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "mirbadllm-fencing-" + Guid.NewGuid().ToString("N"));

    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private string StatePath => Path.Combine(_dataDir, MasterStateDocument.FileName);

    private string FlagPath => Path.Combine(_dataDir, MasterStateDocument.TakeoverFlagFileName);

    private MasterIdentity Open(bool takeoverOption = false) =>
        MasterIdentity.Open(_dataDir, new FencingOptions { Takeover = takeoverOption, LockWait = TimeSpan.Zero }, _time);

    private MasterStateDocument ReadState() =>
        JsonSerializer.Deserialize<MasterStateDocument>(File.ReadAllBytes(StatePath), MasterStateJson.Options)!;

    // ---- layer 1: lock --------------------------------------------------------------------------------------------

    [Fact]
    public void SecondInstance_OnSameDirectory_FailsWithClearException()
    {
        using MasterIdentity first = Open();

        var ex = Assert.Throws<MasterLockException>(() => Open());

        Assert.Equal(Path.Combine(_dataDir, MasterStateDocument.LockFileName), ex.LockPath);
        Assert.Contains("Another mirbadllm master is already using this data directory", ex.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<IOException>(ex.InnerException);
    }

    [Fact]
    public async Task Open_WaitsForAPredecessorStillLettingGo_ThenRefusesALockThatStaysHeld()
    {
        // A master restarted right behind its predecessor (a crash-restart, or a test host still disposing) must not fail
        // on a lock released a moment later; a lock held past the wait is still refused.
        MasterIdentity first = Open();
        var release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400));
            first.Dispose();
        });
        using (MasterIdentity second = MasterIdentity.Open(_dataDir, new FencingOptions { LockWait = TimeSpan.FromSeconds(10) }))
        {
            await release;
            Assert.Equal(2, second.Epoch);
            var ex = Assert.Throws<MasterLockException>(() => MasterIdentity.Open(_dataDir, new FencingOptions { LockWait = TimeSpan.FromMilliseconds(300) }));
            Assert.IsAssignableFrom<IOException>(ex.InnerException);
        }
    }

    [Fact]
    public void LockProbe_SeesTheHeldLock_AndNotAReleasedOne()
    {
        // MN-090: the probe Open() uses to refuse a volume where locking is a no-op must tell "held" from "free" on the
        // platform under test (Windows sharing violation; Linux flock on a second open file description).
        string lockPath = Path.Combine(_dataDir, MasterStateDocument.LockFileName);
        MasterIdentity identity = Open();

        Assert.True(MasterIdentity.LockIsEffective(lockPath));

        identity.Dispose();
        Assert.False(MasterIdentity.LockIsEffective(lockPath));
    }

    [Fact]
    public void FailedSecondInstance_DoesNotTouchMasterJson()
    {
        using MasterIdentity first = Open();
        string before = File.ReadAllText(StatePath);

        Assert.Throws<MasterLockException>(() => Open());

        Assert.Equal(before, File.ReadAllText(StatePath));
        Assert.Equal(1, first.Epoch);
    }

    [Fact]
    public void Dispose_ReleasesLock_AndIsIdempotent()
    {
        MasterIdentity first = Open();
        first.Dispose();
        first.Dispose();

        using MasterIdentity second = Open();
        Assert.Equal(2, second.Epoch);
    }

    [Fact]
    public async Task AfterDispose_MutationsThrow()
    {
        MasterIdentity identity = Open();
        await identity.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => identity.RaiseEpochToAsync(10));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            identity.CompleteTakeoverAsync(new TakeoverRecord { TakeoverId = "t", Epoch = 10 }));
    }

    // ---- layer 2: identity + epoch --------------------------------------------------------------------------------

    [Fact]
    public void FirstBoot_CreatesInstanceId_AndPersistsEpochOne_BeforeReturning()
    {
        using MasterIdentity identity = Open();

        MasterStateDocument state = ReadState();
        Assert.Equal(MasterStateDocument.CurrentSchemaVersion, state.SchemaVersion);
        Assert.Equal(identity.InstanceId, state.MasterInstanceId);
        Assert.Equal(1, state.Epoch);
        Assert.Equal(1, identity.Epoch);
        Assert.Equal(_time.GetUtcNow(), state.CreatedAt);
        Assert.Empty(state.Takeovers);
        Assert.DoesNotContain('/', identity.InstanceId);
        Assert.DoesNotContain('/', identity.BootId);
        Assert.False(File.Exists(StatePath + ".tmp"));
    }

    [Fact]
    public void Epoch_IncrementsAcrossRestarts_InstanceIdKept_BootIdNew()
    {
        string instanceId;
        var bootIds = new HashSet<string>(StringComparer.Ordinal);
        using (MasterIdentity a = Open())
        {
            instanceId = a.InstanceId;
            bootIds.Add(a.BootId);
            Assert.Equal(1, a.Epoch);
        }

        DateTimeOffset createdAt = ReadState().CreatedAt;
        for (long expected = 2; expected <= 4; expected++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            using MasterIdentity b = Open();
            Assert.Equal(expected, b.Epoch);
            Assert.Equal(expected, ReadState().Epoch);
            Assert.Equal(instanceId, b.InstanceId);
            Assert.True(bootIds.Add(b.BootId), "boot id must be new every boot");
            Assert.Equal(createdAt, ReadState().CreatedAt);
            Assert.Equal(_time.GetUtcNow(), ReadState().UpdatedAt);
        }
    }

    [Fact]
    public void RestoredOlderMasterJson_ContinuesFromRestoredEpoch()
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(StatePath, """{"schemaVersion":1,"masterInstanceId":"restored01","epoch":41}""");

        using MasterIdentity identity = Open();

        Assert.Equal("restored01", identity.InstanceId);
        Assert.Equal(42, identity.Epoch);
        Assert.Empty(identity.Takeovers);
    }

    [Fact]
    public void Current_FormatsTripleHeaderPerContracts()
    {
        using MasterIdentity identity = Open();

        MasterTriple current = identity.Current;
        Assert.Equal(new MasterTriple(identity.InstanceId, identity.Epoch, identity.BootId), current);
        Assert.Equal($"{identity.InstanceId}/1/{identity.BootId}", current.ToHeaderValue());
        Assert.True(MasterTriple.TryParse(current.ToHeaderValue(), out MasterTriple parsed));
        Assert.True(parsed.SameBoot(current));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"schemaVersion":1,"masterInstanceId":"a/b","epoch":3}""")]
    [InlineData("""{"schemaVersion":1,"masterInstanceId":"","epoch":3}""")]
    [InlineData("""{"schemaVersion":1,"masterInstanceId":"abc","epoch":-1}""")]
    [InlineData("""{"schemaVersion":99,"masterInstanceId":"abc","epoch":3}""")]
    [InlineData("""{"schemaVersion":1,"masterInstanceId":"abc","epoch":9223372036854775807}""")]
    public void InvalidMasterJson_RefusesToStart_KeepsFile_ReleasesLock(string content)
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(StatePath, content);

        Assert.Throws<InvalidDataException>(() => Open());

        Assert.Equal(content, File.ReadAllText(StatePath));
        File.Delete(StatePath);
        using MasterIdentity identity = Open();
        Assert.Equal(1, identity.Epoch);
    }

    [Fact]
    public async Task RaiseEpochTo_PersistsBeforeReturning()
    {
        using MasterIdentity identity = Open();

        MasterTriple raised = await identity.RaiseEpochToAsync(17);

        Assert.Equal(17, raised.Epoch);
        Assert.Equal(17, ReadState().Epoch);
        Assert.Equal(17, identity.Epoch);
        Assert.Equal(raised, identity.Current);
        Assert.Equal(identity.BootId, raised.BootId);
        Assert.Equal(identity.InstanceId, raised.InstanceId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task RaiseEpochTo_LowerOrEqual_LeavesEpochUnchanged(long epoch)
    {
        using MasterIdentity identity = Open();
        await identity.RaiseEpochToAsync(5);
        MasterTriple before = identity.Current;

        MasterTriple result = await identity.RaiseEpochToAsync(epoch);

        Assert.Equal(before, result);
        Assert.Equal(5, ReadState().Epoch);
    }

    [Fact]
    public async Task RaiseEpochTo_Concurrent_PersistsHighest()
    {
        using MasterIdentity identity = Open();

        await Task.WhenAll(Enumerable.Range(2, 40).Select(n => Task.Run(() => identity.RaiseEpochToAsync(n))));

        Assert.Equal(41, identity.Epoch);
        Assert.Equal(41, ReadState().Epoch);
    }

    [Fact]
    public async Task RaisedEpoch_SurvivesRestart_NextBootIsAboveIt()
    {
        using (MasterIdentity identity = Open())
        {
            await identity.RaiseEpochToAsync(30);
        }

        using MasterIdentity next = Open();
        Assert.Equal(31, next.Epoch);
    }

    // ---- layer 3: takeover flag + record --------------------------------------------------------------------------

    [Fact]
    public void Takeover_NotRequested_ByDefault()
    {
        using MasterIdentity identity = Open();
        Assert.False(identity.TakeoverRequested);
    }

    [Fact]
    public void Takeover_FlagFile_DetectedAtBootAndWhileRunning()
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(FlagPath, "");
        using (MasterIdentity identity = Open())
        {
            Assert.True(identity.TakeoverRequested);
            Assert.Equal(FlagPath, identity.TakeoverFlagPath);
        }

        File.Delete(FlagPath);
        using MasterIdentity running = Open();
        Assert.False(running.TakeoverRequested);
        File.WriteAllText(FlagPath, "");
        Assert.True(running.TakeoverRequested);
    }

    [Fact]
    public void Takeover_Option_Detected()
    {
        using MasterIdentity identity = Open(takeoverOption: true);
        Assert.True(identity.TakeoverRequested);
    }

    [Fact]
    public async Task CompleteTakeover_PersistsEpochAndRecordTogether_ThenDeletesFlag()
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(FlagPath, "");
        using MasterIdentity identity = Open();
        TakeoverRecord record = TakeoverRules.CreateRecord(
            identity.Epoch,
            [new TakeoverNodeEpoch("dgx", "uid-dgx", 9), new TakeoverNodeEpoch("box", "uid-box", null)],
            ["win"],
            "admin-hash",
            _time.GetUtcNow());

        MasterTriple result = await identity.CompleteTakeoverAsync(record);

        Assert.Equal(10, result.Epoch);
        Assert.Equal(result, identity.Current);
        Assert.False(File.Exists(FlagPath));
        Assert.False(identity.TakeoverRequested);
        MasterStateDocument state = ReadState();
        Assert.Equal(10, state.Epoch);
        TakeoverRecord stored = Assert.Single(state.Takeovers);
        Assert.Equal(record.TakeoverId, stored.TakeoverId);
        Assert.Equal(10, stored.Epoch);
        Assert.Equal(1, stored.PreviousEpoch);
        Assert.Equal("admin-hash", stored.Operator);
        Assert.Equal(["win"], stored.Unreachable);
        Assert.Equal(2, stored.Answered.Count);
        Assert.Single(identity.Takeovers);
    }

    [Fact]
    public async Task CompleteTakeover_ByOption_ClearsRequestForThisProcess()
    {
        using MasterIdentity identity = Open(takeoverOption: true);
        TakeoverRecord record = TakeoverRules.CreateRecord(identity.Epoch, [], [], "config", _time.GetUtcNow());

        await identity.CompleteTakeoverAsync(record);

        Assert.False(identity.TakeoverRequested);
        Assert.Equal(2, identity.Epoch);
    }

    [Fact]
    public async Task CompleteTakeover_SetsTimeWhenMissing_AndRecordsSurviveRestart()
    {
        string takeoverId;
        using (MasterIdentity identity = Open())
        {
            var record = new TakeoverRecord { TakeoverId = "tk1", Epoch = 7, PreviousEpoch = 1 };
            takeoverId = record.TakeoverId;
            await identity.CompleteTakeoverAsync(record);
            Assert.Equal(_time.GetUtcNow(), Assert.Single(identity.Takeovers).At);
        }

        using MasterIdentity next = Open();
        Assert.Equal(8, next.Epoch);
        Assert.Equal(takeoverId, Assert.Single(next.Takeovers).TakeoverId);
    }

    [Fact]
    public async Task CompleteTakeover_NewRecordNotAboveCurrentEpoch_IsRefused()
    {
        using MasterIdentity identity = Open();
        await identity.RaiseEpochToAsync(12);
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(FlagPath, "");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            identity.CompleteTakeoverAsync(new TakeoverRecord { TakeoverId = "stale", Epoch = 12 }));

        Assert.True(File.Exists(FlagPath));
        Assert.Empty(ReadState().Takeovers);
        Assert.Equal(12, identity.Epoch);
    }

    [Theory]
    [InlineData("", 3)]
    [InlineData("id", 0)]
    public async Task CompleteTakeover_InvalidRecord_Throws(string id, long epoch)
    {
        using MasterIdentity identity = Open();
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            identity.CompleteTakeoverAsync(new TakeoverRecord { TakeoverId = id, Epoch = epoch }));
    }

    [Fact]
    public async Task Continuation_ReplacesRecord_RaisesToAgentEpochPlusOne_LeavesNewFlagAlone()
    {
        using MasterIdentity identity = Open();
        TakeoverRecord record = TakeoverRules.CreateRecord(
            identity.Epoch, [new TakeoverNodeEpoch("dgx", "uid-dgx", 3)], ["box"], "admin", _time.GetUtcNow());
        await identity.CompleteTakeoverAsync(record);
        Assert.Equal(4, identity.Epoch);

        // "box" returns holding epoch 20 (an older backup was restored there): blocked until continued.
        var boxAccepted = new MasterTriple("other", 20, "oldboot");
        ReturningAgentAssessment assessment = TakeoverRules.AssessAgent(identity.Current, boxAccepted);
        Assert.Equal(ReturningAgentStatus.NeedsContinuation, assessment.Status);

        File.WriteAllText(FlagPath, ""); // an unrelated new request must survive a continuation
        _time.Advance(TimeSpan.FromMinutes(5));
        TakeoverRecord continued = TakeoverRules.AddContinuation(
            identity.Takeovers[^1], "box", assessment.AgentEpoch!.Value, identity.Epoch, "admin", _time.GetUtcNow());
        MasterTriple result = await identity.CompleteTakeoverAsync(continued);

        Assert.Equal(21, result.Epoch);
        Assert.Equal(21, ReadState().Epoch);
        TakeoverRecord stored = Assert.Single(ReadState().Takeovers);
        Assert.Equal(record.TakeoverId, stored.TakeoverId);
        TakeoverContinuation c = Assert.Single(stored.Continuations);
        Assert.Equal(new TakeoverContinuation("box", 20, 21, _time.GetUtcNow(), "admin"), c);
        Assert.True(File.Exists(FlagPath));
        Assert.Equal(ReturningAgentStatus.AcceptsOnNextMutation, TakeoverRules.AssessAgent(identity.Current, boxAccepted).Status);
    }

    // ---- takeover rules -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(5, new long[] { }, 6)]
    [InlineData(5, new long[] { 3, 4 }, 6)]
    [InlineData(5, new long[] { 5 }, 6)]
    [InlineData(5, new long[] { 2, 9, 7 }, 10)]
    public void ComputeEpoch_IsMaxOfRestoredAndReachablePlusOne(long current, long[] agents, long expected)
    {
        IEnumerable<TakeoverNodeEpoch> answered = agents
            .Select((e, i) => new TakeoverNodeEpoch($"n{i}", $"u{i}", e))
            .Append(new TakeoverNodeEpoch("fresh", "uf", null));

        Assert.Equal(expected, TakeoverRules.ComputeEpoch(current, answered));
    }

    [Fact]
    public void CreateRecord_FillsFields_AndNewIds()
    {
        DateTimeOffset at = _time.GetUtcNow();
        TakeoverRecord a = TakeoverRules.CreateRecord(3, [new TakeoverNodeEpoch("dgx", "u", 8)], ["x", "x", "y"], "op", at);
        TakeoverRecord b = TakeoverRules.CreateRecord(3, [], [], null, at);

        Assert.Equal(9, a.Epoch);
        Assert.Equal(3, a.PreviousEpoch);
        Assert.Equal(at, a.At);
        Assert.Equal("op", a.Operator);
        Assert.Equal(["x", "y"], a.Unreachable);
        Assert.Empty(a.Continuations);
        Assert.NotEqual(a.TakeoverId, b.TakeoverId);
        Assert.Equal(4, b.Epoch);
    }

    [Fact]
    public void AssessAgent_FollowsAgentDecisionTable()
    {
        var master = new MasterTriple("me", 10, "boot");

        Assert.Equal(new ReturningAgentAssessment(ReturningAgentStatus.AcceptsOnNextMutation, null), TakeoverRules.AssessAgent(master, null));
        Assert.Equal(ReturningAgentStatus.AcceptsOnNextMutation, TakeoverRules.AssessAgent(master, new MasterTriple("old", 9, "b")).Status);
        Assert.Equal(ReturningAgentStatus.Current, TakeoverRules.AssessAgent(master, new MasterTriple("me", 10, "boot")).Status);
        // Equal epoch, other boot or other instance: competing_master, the first master at that epoch wins.
        Assert.Equal(ReturningAgentStatus.NeedsContinuation, TakeoverRules.AssessAgent(master, new MasterTriple("me", 10, "other")).Status);
        Assert.Equal(ReturningAgentStatus.NeedsContinuation, TakeoverRules.AssessAgent(master, new MasterTriple("copy", 10, "boot")).Status);
        // Higher epoch: stale_master.
        ReturningAgentAssessment higher = TakeoverRules.AssessAgent(master, new MasterTriple("x", 11, "b"));
        Assert.Equal(new ReturningAgentAssessment(ReturningAgentStatus.NeedsContinuation, 11), higher);
    }

    [Fact]
    public void AddContinuation_WhenAgentBelowMaster_IsRefused()
    {
        var record = new TakeoverRecord { TakeoverId = "t", Epoch = 5 };

        Assert.Throws<InvalidOperationException>(() =>
            TakeoverRules.AddContinuation(record, "box", 4, 5, "op", _time.GetUtcNow()));
        TakeoverRecord equal = TakeoverRules.AddContinuation(record, "box", 5, 5, "op", _time.GetUtcNow());
        Assert.Equal(6, Assert.Single(equal.Continuations).NewEpoch);
    }

    // ---- DI ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void AddMasterFencing_SingleInstance_BindsOption_DisposeReleasesLock()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MirbadLLM:Fencing:Takeover"] = "true" })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.Configure<MirbadLLMOptions>(o => o.DataPath = _dataDir);
        services.AddMasterFencing();

        using (ServiceProvider provider = services.BuildServiceProvider())
        {
            IMasterIdentity identity = provider.GetRequiredService<IMasterIdentity>();
            Assert.Same(provider.GetRequiredService<MasterIdentity>(), identity);
            Assert.True(identity.TakeoverRequested);
            Assert.Equal(1, identity.Epoch);
            Assert.Throws<MasterLockException>(() => Open());
        }

        using MasterIdentity next = Open();
        Assert.Equal(2, next.Epoch);
    }

    [Fact]
    public void AddMasterFencing_IsLazy_TheLockIsTakenOnFirstResolve()
    {
        // MN-040 relies on this: building the provider takes nothing; the composition must resolve IMasterIdentity right
        // after Build() (before anything touches DataPath and before app.Run) so a second master stops at startup.
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.Configure<MirbadLLMOptions>(o => o.DataPath = _dataDir);
        services.AddMasterFencing();

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.False(File.Exists(StatePath));
        using (MasterIdentity before = Open())
        {
            Assert.Equal(1, before.Epoch); // nothing held it: a plain open succeeds
        }

        _ = provider.GetRequiredService<IMasterIdentity>();
        Assert.Throws<MasterLockException>(() => Open());
    }
}
