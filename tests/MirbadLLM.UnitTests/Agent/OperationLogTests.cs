using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Operations;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required here.

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// MN-013: the agent's durable operation log (design §4.4 items 1–8): durable acceptance, payload-digest idempotency
/// with recursive secret redaction, conflicts, envelope expiry and skew, stages and terminal states (incl.
/// <c>dispatched</c>/<c>Unknown</c>), replay after restart, torn lines, compaction with bounded retention, and
/// crash-safety of compaction.
/// </summary>
public sealed class OperationLogTests : IDisposable
{
    private static readonly MasterTriple Master = new("5b1f0c9e2d8a4f6b9e3c7a1d0f2e4b6c", 3, "e0d1c2b3a4958677685940312a1b2c3d");

    private readonly string _stateDir = Path.Combine(Path.GetTempPath(), "mirbadllm-oplog-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly List<OperationLog> _open = [];

    public void Dispose()
    {
        foreach (OperationLog log in _open)
        {
            log.Dispose();
        }

        if (Directory.Exists(_stateDir))
        {
            Directory.Delete(_stateDir, recursive: true);
        }
    }

    private string LogPath => Path.Combine(_stateDir, "operations", "log.jsonl");

    private string CompactedPath => Path.Combine(_stateDir, "operations", "operations.json");

    private OperationLog Open(int minRetained = OperationRetention.MinRetainedRecords, string bootId = "boot-1")
    {
        var log = new OperationLog(
            new OperationLogOptions { StateDirectory = _stateDir, BootId = bootId, NodeUid = "uid-1", MinRetainedRecords = minRetained },
            _time);
        _open.Add(log);
        return log;
    }

    /// <summary>Simulates an agent restart: the old instance goes away (its handle is closed), a new one loads the files.</summary>
    private OperationLog Reopen(OperationLog old, int minRetained = OperationRetention.MinRetainedRecords)
    {
        old.Dispose();
        _open.Remove(old);
        return Open(minRetained, bootId: "boot-2");
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private OperationSubmission Submission(
        string id,
        string kind = OperationKinds.Launch,
        string target = "qwen36-35b",
        string extra = "\"mode\":\"ensureStarted\"",
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? expiresAt = null)
    {
        DateTimeOffset issued = issuedAt ?? _time.GetUtcNow();
        DateTimeOffset expires = expiresAt ?? issued + OperationRetention.EnvelopeLifetime;
        string body = $"{{\"operationId\":\"{id}\",\"issuedAt\":\"{Iso(issued)}\",\"expiresAt\":\"{Iso(expires)}\"" +
                      (extra.Length > 0 ? "," + extra : "") + "}";
        return new OperationSubmission
        {
            OperationId = id,
            Kind = kind,
            Target = target,
            IssuedAt = issued,
            ExpiresAt = expires,
            Master = Master,
            Payload = Json(body),
        };
    }

    private static async Task<OperationStatus> AcceptNew(OperationLog log, OperationSubmission submission)
    {
        OperationAcceptance acceptance = await log.AcceptAsync(submission);
        Assert.Equal(OperationAcceptOutcome.Accepted, acceptance.Outcome);
        return Assert.IsType<OperationStatus>(acceptance.Status);
    }

    /// <summary>Reads a file the log may hold open for writing (it shares reads only with writers allowed).</summary>
    private static byte[] ReadSharedBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Records holding lists or <see cref="JsonElement"/>s are compared by their wire form.</summary>
    private static void AssertSameJson<T>(T expected, T? actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected, AgentJson.Options), JsonSerializer.Serialize(actual, AgentJson.Options));

    private static string ReadShared(string path) => Encoding.UTF8.GetString(ReadSharedBytes(path));

    private string[] LogLines() =>
        ReadShared(LogPath).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // ---- Durable acceptance (§4.4 item 1) ---------------------------------------------------------------------------

    [Fact]
    public async Task Accept_NewOperation_IsAcceptedAndOnDiskBeforeReturning()
    {
        var log = Open();
        DateTimeOffset now = _time.GetUtcNow();
        OperationSubmission submission = Submission("op-1");

        OperationStatus status = await AcceptNew(log, submission);

        Assert.Equal("op-1", status.Id);
        Assert.Equal(OperationKinds.Launch, status.Kind);
        Assert.Equal("qwen36-35b", status.Target);
        Assert.Equal(OperationState.Accepted, status.State);
        Assert.Equal(OperationStages.Accepted, status.Stage);
        Assert.Equal(now, status.StartedAt);
        Assert.Equal(submission.IssuedAt, status.IssuedAt);
        Assert.Equal(submission.ExpiresAt, status.ExpiresAt);
        Assert.Null(status.EndedAt);
        Assert.Equal(44, status.PayloadDigest.Length); // Base64 of 32 bytes

        // Read by another handle while the log is still open: the line is complete (newline-terminated) on disk.
        string text;
        using (var reader = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(reader))
        {
            text = await sr.ReadToEndAsync();
        }

        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        OperationLogEntry entry = JsonSerializer.Deserialize<OperationLogEntry>(text.TrimEnd('\n'), AgentJson.Options)!;
        Assert.Equal(OperationLogEntryType.Accepted, entry.Type);
        Assert.Equal("op-1", entry.OperationId);
        Assert.Equal("boot-1", entry.BootId);
        Assert.Equal("uid-1", entry.NodeUid);
        Assert.Equal(Master, entry.Master);
        Assert.Equal(status.PayloadDigest, entry.PayloadDigest);
        Assert.Equal(OperationState.Accepted, entry.State);
        Assert.Equal(submission.IssuedAt, entry.IssuedAt);
        Assert.Equal(submission.ExpiresAt, entry.ExpiresAt);
        Assert.Equal("ensureStarted", entry.Payload!.Value.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Accept_SurvivesRestart_AndIsRecoverableWithMasterAndPayload()
    {
        var log = Open();
        OperationStatus accepted = await AcceptNew(log, Submission("op-1"));

        var reopened = Reopen(log);

        Assert.Equal(accepted, reopened.Find("op-1"));
        RecoverableOperation recoverable = Assert.Single(reopened.ListRecoverable());
        Assert.Equal(accepted, recoverable.Status);
        Assert.Equal(Master, recoverable.Master);
        Assert.Equal("op-1", recoverable.Payload!.Value.GetProperty("operationId").GetString());
        Assert.Null(recoverable.Reservation);
        Assert.Equal(0, reopened.TornLinesIgnored);
    }

    [Fact]
    public async Task Accept_WhenCancelledBeforeWriting_WritesNothing()
    {
        var log = Open();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => log.AcceptAsync(Submission("op-1"), cts.Token));

        Assert.Null(log.Find("op-1"));
        Assert.Empty(LogLines());
    }

    [Fact]
    public async Task Accept_WhenStateDirectoryIsNotWritable_ThrowsStateUnavailable_ReportsHealth_AndRecovers()
    {
        // "operations" exists as a file, so the log directory cannot be created.
        Directory.CreateDirectory(_stateDir);
        string blocker = Path.Combine(_stateDir, "operations");
        await File.WriteAllTextAsync(blocker, "not a directory");

        var log = Open();
        Assert.NotNull(log.UnavailableReason);
        HealthReason reason = Assert.Single(await log.CheckAsync());
        Assert.Equal(HealthReasonCodes.StateUnavailable, reason.Code);

        var ex = await Assert.ThrowsAsync<OperationLogUnavailableException>(() => log.AcceptAsync(Submission("op-1")));
        Assert.Equal(ErrorCodes.StateUnavailable, OperationLogUnavailableException.ErrorCode);
        Assert.NotNull(ex.InnerException);
        Assert.Null(log.Find("op-1")); // nothing runs

        File.Delete(blocker);
        Assert.Empty(await log.CheckAsync()); // health probe reopens the log
        Assert.Null(log.UnavailableReason);
        await AcceptNew(log, Submission("op-1"));
        Assert.NotNull(Reopen(log).Find("op-1"));
    }

    [Fact]
    public async Task Health_IsEmptyWhenWritable()
    {
        var log = Open();
        Assert.Empty(await log.CheckAsync());
        Assert.Null(log.UnavailableReason);
    }

    // ---- Payload-bound deduplication (§4.4 item 2) -------------------------------------------------------------------

    [Fact]
    public async Task Accept_SameIdKindTargetAndDigest_ReturnsStoredStatus_WithoutWriting()
    {
        var log = Open();
        OperationSubmission submission = Submission("op-1");
        OperationStatus first = await AcceptNew(log, submission);
        await log.RecordStageAsync("op-1", new OperationStageUpdate { Stage = OperationStages.Admitted });
        int lines = LogLines().Length;

        OperationAcceptance again = await log.AcceptAsync(submission);

        Assert.Equal(OperationAcceptOutcome.Duplicate, again.Outcome);
        Assert.Equal(OperationStages.Admitted, again.Status!.Stage);
        Assert.Equal(first.PayloadDigest, again.Status.PayloadDigest);
        Assert.Equal(lines, LogLines().Length);
    }

    [Fact]
    public async Task Accept_DuplicateAfterRestart_IsStillDeduplicated()
    {
        var log = Open();
        OperationSubmission submission = Submission("op-1");
        await AcceptNew(log, submission);

        var reopened = Reopen(log);
        OperationAcceptance again = await reopened.AcceptAsync(submission);

        Assert.Equal(OperationAcceptOutcome.Duplicate, again.Outcome);
    }

    [Fact]
    public async Task Accept_SameIdWithDifferentDigest_IsConflict()
    {
        var log = Open();
        DateTimeOffset issued = _time.GetUtcNow();
        await AcceptNew(log, Submission("op-1", extra: "\"mode\":\"ensureStarted\"", issuedAt: issued));

        OperationAcceptance conflict = await log.AcceptAsync(Submission("op-1", extra: "\"mode\":\"replace\"", issuedAt: issued));

        Assert.Equal(OperationAcceptOutcome.Conflict, conflict.Outcome);
        Assert.Null(conflict.Status);
        Assert.Single(LogLines());
    }

    [Fact]
    public async Task Accept_SameIdWithDifferentKind_IsConflict()
    {
        var log = Open();
        DateTimeOffset issued = _time.GetUtcNow();
        await AcceptNew(log, Submission("op-1", issuedAt: issued));

        OperationAcceptance conflict = await log.AcceptAsync(Submission("op-1", kind: OperationKinds.Stop, issuedAt: issued));

        Assert.Equal(OperationAcceptOutcome.Conflict, conflict.Outcome);
    }

    [Fact]
    public async Task Accept_SameIdWithDifferentTarget_IsConflict()
    {
        var log = Open();
        DateTimeOffset issued = _time.GetUtcNow();
        await AcceptNew(log, Submission("op-1", issuedAt: issued));

        OperationAcceptance conflict = await log.AcceptAsync(Submission("op-1", target: "other-model", issuedAt: issued));

        Assert.Equal(OperationAcceptOutcome.Conflict, conflict.Outcome);
    }

    [Fact]
    public async Task Accept_EnvelopeFieldsArePartOfTheDigest()
    {
        var log = Open();
        DateTimeOffset issued = _time.GetUtcNow();
        await AcceptNew(log, Submission("op-1", issuedAt: issued));

        // A retry must resend issuedAt/expiresAt unchanged; a changed envelope is a different payload.
        OperationAcceptance conflict = await log.AcceptAsync(Submission("op-1", issuedAt: issued.AddSeconds(1)));

        Assert.Equal(OperationAcceptOutcome.Conflict, conflict.Outcome);
    }

    [Fact]
    public async Task Accept_ConcurrentSameId_AcceptsExactlyOnce()
    {
        var log = Open();
        OperationSubmission submission = Submission("op-1");

        OperationAcceptance[] results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => log.AcceptAsync(submission))));

        Assert.Single(results, r => r.Outcome == OperationAcceptOutcome.Accepted);
        Assert.Equal(15, results.Count(r => r.Outcome == OperationAcceptOutcome.Duplicate));
        Assert.Single(LogLines());
    }

    [Fact]
    public async Task Accept_ConcurrentDistinctIds_AreAllDurable()
    {
        var log = Open();

        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => AcceptNew(log, Submission($"op-{i}")))));

        var reopened = Reopen(log);
        Assert.Equal(32, reopened.ListActive().Count);
        Assert.Equal(0, reopened.TornLinesIgnored);
    }

    // ---- Secrets (§4.4 item 1, §10.4) -------------------------------------------------------------------------------

    [Fact]
    public async Task Secrets_AreRedactedRecursively_BeforeStoring()
    {
        var log = Open();
        const string Body = "\"repoId\":\"org/m\",\"token\":\"hf_SECRET_TOKEN\"," +
                            "\"settings\":{\"server\":\"nas\",\"Password\":\"SHARE_PASSWORD\",\"nested\":[{\"password\":\"DEEP_PASSWORD\"}]}," +
                            "\"spec\":{\"env\":{\"HF_TOKEN\":\"ENV_SECRET\",\"PLAIN\":\"visible\"},\"secretEnvNames\":[\"HF_TOKEN\"]}";

        await AcceptNew(log, Submission("op-1", kind: OperationKinds.Download, extra: Body));

        string onDisk = ReadShared(LogPath);
        Assert.DoesNotContain("hf_SECRET_TOKEN", onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain("SHARE_PASSWORD", onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain("DEEP_PASSWORD", onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain("ENV_SECRET", onDisk, StringComparison.Ordinal);
        Assert.Contains("visible", onDisk, StringComparison.Ordinal);

        JsonElement payload = Assert.Single(log.ListRecoverable()).Payload!.Value;
        Assert.Equal("***", payload.GetProperty("token").GetString());
        Assert.Equal("***", payload.GetProperty("settings").GetProperty("Password").GetString());
        Assert.Equal("***", payload.GetProperty("settings").GetProperty("nested")[0].GetProperty("password").GetString());
        Assert.Equal("***", payload.GetProperty("spec").GetProperty("env").GetProperty("HF_TOKEN").GetString());
        Assert.Equal("visible", payload.GetProperty("spec").GetProperty("env").GetProperty("PLAIN").GetString());
        Assert.Equal("nas", payload.GetProperty("settings").GetProperty("server").GetString());

        // Also after compaction.
        await log.CompactAsync();
        string compacted = await File.ReadAllTextAsync(CompactedPath);
        Assert.DoesNotContain("hf_SECRET_TOKEN", compacted, StringComparison.Ordinal);
        Assert.DoesNotContain("SHARE_PASSWORD", compacted, StringComparison.Ordinal);
        Assert.DoesNotContain("ENV_SECRET", compacted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secrets_AreReplacedBeforeHashing_SoARetryWithAnotherTokenIsADuplicate()
    {
        var log = Open();
        DateTimeOffset issued = _time.GetUtcNow();
        OperationStatus first = await AcceptNew(log, Submission("op-1", OperationKinds.Download, "f", "\"token\":\"hf_one\"", issued));

        OperationAcceptance retry = await log.AcceptAsync(Submission("op-1", OperationKinds.Download, "f", "\"token\":\"hf_two\"", issued));

        Assert.Equal(OperationAcceptOutcome.Duplicate, retry.Outcome);
        string expected = OperationPayload.ComputeDigest(OperationPayload.Redact(Submission("op-1", OperationKinds.Download, "f", "\"token\":\"***\"", issued).Payload));
        Assert.Equal(expected, first.PayloadDigest);
    }

    [Fact]
    public void Redact_KeepsNullSecretsNull_AndRedactsNonStringSecrets()
    {
        JsonElement redacted = OperationPayload.Redact(Json("{\"token\":null,\"password\":{\"a\":1},\"tokens\":\"not-a-secret-field\"}"));

        Assert.Equal(JsonValueKind.Null, redacted.GetProperty("token").ValueKind);
        Assert.Equal("***", redacted.GetProperty("password").GetString());
        Assert.Equal("not-a-secret-field", redacted.GetProperty("tokens").GetString());
    }

    [Fact]
    public void Redact_PreservesPropertyOrderAndNonSecretValues()
    {
        JsonElement redacted = OperationPayload.Redact(Json("{\"b\":1.50,\"a\":[true,false,null,\"x\"],\"token\":\"t\"}"));

        Assert.Equal("{\"b\":1.50,\"a\":[true,false,null,\"x\"],\"token\":\"***\"}", redacted.GetRawText());
    }

    [Fact]
    public void Digest_IsCanonical_IndependentOfPropertyOrderAndWhitespace()
    {
        string a = OperationPayload.ComputeDigest(Json("{\"b\":{\"y\":2,\"x\":1},\"a\":[3,1]}"));
        string b = OperationPayload.ComputeDigest(Json("{ \"a\" : [3, 1],\n \"b\" : {\"x\":1, \"y\":2} }"));
        string c = OperationPayload.ComputeDigest(Json("{\"a\":[1,3],\"b\":{\"x\":1,\"y\":2}}"));

        Assert.Equal(a, b);
        Assert.NotEqual(a, c); // array order matters
        Assert.Equal("{\"a\":[3,1],\"b\":{\"x\":1,\"y\":2}}", Encoding.UTF8.GetString(OperationPayload.Canonicalize(Json("{\"b\":{\"y\":2,\"x\":1},\"a\":[3,1]}"))));
    }

    [Fact]
    public void Redact_AbsentBody_IsNull()
    {
        JsonElement redacted = OperationPayload.Redact(default);

        Assert.Equal(JsonValueKind.Null, redacted.ValueKind);
        Assert.Equal(OperationPayload.ComputeDigest(Json("null")), OperationPayload.ComputeDigest(redacted));
    }

    [Fact]
    public void RequestDigest_OfTheMaster_EqualsTheAgentsDigestOfTheWireBytes_AndIgnoresSecretEnvValues()
    {
        LaunchRequest Launch(string secret, string plain)
        {
            var launch = MirbadLLM.UnitTests.Multinode.Contract.AgentContractSuite.LaunchFor(
                "m", env: new Dictionary<string, string> { ["HF_TOKEN"] = secret, ["PLAIN"] = plain }, secretEnvNames: ["HF_TOKEN"]);
            return launch with { OperationId = "op-fixed", IssuedAt = DateTimeOffset.UnixEpoch, ExpiresAt = DateTimeOffset.UnixEpoch.AddDays(1) };
        }

        LaunchRequest request = Launch("hf_secret", "1");
        string master = OperationPayload.ComputeRequestDigest(request, typeof(LaunchRequest));
        string wire = JsonSerializer.Serialize(request, AgentJson.Options);
        (JsonElement stored, string agent) = OperationPayload.RedactAndDigest(Json(wire));

        Assert.Equal(master, agent);
        Assert.DoesNotContain("hf_secret", stored.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(master, OperationPayload.ComputeRequestDigest(Launch("hf_other", "1"), typeof(LaunchRequest)));
        Assert.NotEqual(master, OperationPayload.ComputeRequestDigest(Launch("hf_secret", "2"), typeof(LaunchRequest)));
    }

    [Fact]
    public void Digest_NormalizesStringEscapes()
    {
        string plain = OperationPayload.ComputeDigest(Json("{\"a\":\"é/x\"}"));
        string escaped = OperationPayload.ComputeDigest(Json("{\"a\":\"\\u00e9\\/x\"}"));

        Assert.Equal(plain, escaped);
    }

    // ---- Envelope: expiry (410) and skew (§4.3, §4.4 item 6) --------------------------------------------------------

    [Fact]
    public async Task Accept_UnknownIdWithExpiredEnvelope_IsExpired_AndNothingIsWritten()
    {
        var log = Open();
        DateTimeOffset now = _time.GetUtcNow();

        OperationAcceptance result = await log.AcceptAsync(Submission("op-1", issuedAt: now.AddHours(-25), expiresAt: now.AddHours(-1)));

        Assert.Equal(OperationAcceptOutcome.Expired, result.Outcome);
        Assert.Null(result.Status);
        Assert.Null(log.Find("op-1"));
        Assert.Empty(LogLines());
        Assert.Equal(ErrorCodes.OperationExpired, "operation_expired");
    }

    [Fact]
    public async Task Accept_ExpiringExactlyNow_IsExpired()
    {
        var log = Open();
        DateTimeOffset now = _time.GetUtcNow();

        OperationAcceptance result = await log.AcceptAsync(Submission("op-1", issuedAt: now.AddHours(-24), expiresAt: now));

        Assert.Equal(OperationAcceptOutcome.Expired, result.Outcome);
    }

    [Fact]
    public async Task Accept_KnownIdAfterItsEnvelopeExpired_IsStillAnsweredFromTheLog()
    {
        var log = Open();
        OperationSubmission submission = Submission("op-1");
        await AcceptNew(log, submission);
        await log.CompleteAsync("op-1", OperationState.Succeeded);

        _time.Advance(TimeSpan.FromHours(25));

        OperationAcceptance again = await log.AcceptAsync(submission);
        Assert.Equal(OperationAcceptOutcome.Duplicate, again.Outcome);
        Assert.Equal(OperationState.Succeeded, again.Status!.State);
    }

    [Fact]
    public async Task Accept_IssuedMoreThanFiveMinutesInTheFuture_IsRefused()
    {
        var log = Open();
        DateTimeOffset now = _time.GetUtcNow();

        OperationAcceptance refused = await log.AcceptAsync(Submission("op-1", issuedAt: now + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1)));
        OperationAcceptance atLimit = await log.AcceptAsync(Submission("op-2", issuedAt: now + TimeSpan.FromMinutes(5)));

        Assert.Equal(OperationAcceptOutcome.IssuedInFuture, refused.Outcome);
        Assert.Null(refused.Status);
        Assert.Null(log.Find("op-1"));
        Assert.Equal(OperationAcceptOutcome.Accepted, atLimit.Outcome);
    }

    // ---- Stages (§4.4 items 4, 8) -----------------------------------------------------------------------------------

    [Fact]
    public async Task RecordStage_FirstStageMakesItRunning_AndIsDurable()
    {
        var log = Open();
        await AcceptNew(log, Submission("op-1"));
        var reservation = new ReservationInfo
        {
            OperationId = "op-1",
            ModelId = "qwen36-35b",
            BudgetGiB = 36,
            HostPort = 8101,
            CreatedAt = _time.GetUtcNow(),
        };

        OperationStatus admitted = await log.RecordStageAsync("op-1", new OperationStageUpdate { Stage = OperationStages.Admitted, Reservation = reservation });
        Assert.Equal(OperationState.Running, admitted.State);
        Assert.Equal(OperationStages.Admitted, admitted.Stage);

        var progress = new OperationProgress { Percent = 40.5, BytesDone = 10, BytesTotal = 20, Message = "pulling layer 5/12" };
        OperationStatus pulling = await log.RecordStageAsync("op-1", new OperationStageUpdate { Stage = OperationStages.Pulling, Progress = progress });
        Assert.Equal(progress, pulling.Progress);

        // Progress-only update keeps the stage.
        var later = progress with { Percent = 80 };
        OperationStatus progressed = await log.RecordStageAsync("op-1", new OperationStageUpdate { Progress = later });
        Assert.Equal(OperationStages.Pulling, progressed.Stage);
        Assert.Equal(later, progressed.Progress);

        // A new stage without progress clears the old stage's progress.
        OperationStatus created = await log.RecordStageAsync("op-1", new OperationStageUpdate { Stage = OperationStages.Created });
        Assert.Null(created.Progress);

        var reopened = Reopen(log);
        Assert.Equal(created, reopened.Find("op-1"));
        RecoverableOperation recoverable = Assert.Single(reopened.ListRecoverable());
        Assert.Equal(OperationStages.Created, recoverable.Status.Stage);
        AssertSameJson(reservation, recoverable.Reservation); // kept from the admitted stage (§4.8 rebuild)
    }

    [Fact]
    public async Task RecordStage_Dispatched_IsDurableForHostServiceRecovery()
    {
        var log = Open();
        await AcceptNew(log, Submission("op-1", kind: OperationKinds.ServiceRestart, target: "comfyui", extra: ""));

        await log.RecordStageAsync("op-1", new OperationStageUpdate { Stage = OperationStages.Dispatched });

        // Crash before the terminal record: the restarted agent sees it dispatched and decides Unknown (§4.4 item 8).
        var reopened = Reopen(log);
        RecoverableOperation recoverable = Assert.Single(reopened.ListRecoverable());
        Assert.Equal(OperationStages.Dispatched, recoverable.Status.Stage);
        Assert.Contains(recoverable.Status.Kind, OperationKinds.Unverifiable);

        var observed = new AgentError { Code = ErrorCodes.Timeout, Message = "outcome unknown", Details = Json("{\"observed\":\"active\"}") };
        OperationStatus unknown = await reopened.CompleteAsync("op-1", OperationState.Unknown, observed);

        Assert.Equal(OperationState.Unknown, unknown.State);
        Assert.Equal(OperationStages.Dispatched, unknown.Stage); // where it stopped
        Assert.Equal("active", unknown.Error!.Details!.Value.GetProperty("observed").GetString());
        Assert.Empty(reopened.ListActive());
        AssertSameJson(unknown, Reopen(reopened).Find("op-1"));
    }

    [Fact]
    public async Task RecordStage_ExplicitState_IsKept()
    {
        var log = Open();
        await AcceptNew(log, Submission("op-1"));

        OperationStatus status = await log.RecordStageAsync("op-1", new OperationStageUpdate { State = OperationState.Running });

        Assert.Equal(OperationState.Running, status.State);
        Assert.Equal(OperationStages.Accepted, status.Stage);
    }

    [Fact]
    public async Task RecordStage_WithTerminalState_Throws()
    {
        var log = Open();
        await AcceptNew(log, Submission("op-1"));

        await Assert.ThrowsAsync<ArgumentException>(() => log.RecordStageAsync("op-1", new OperationStageUpdate { State = OperationState.Succeeded }));
    }

    [Fact]
    public async Task RecordStage_And_Complete_ForUnknownId_Throw()
    {
        var log = Open();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => log.RecordStageAsync("nope", new OperationStageUpdate { Stage = OperationStages.Admitted }));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => log.CompleteAsync("nope", OperationState.Failed));
    }

    [Fact]
    public async Task RecordStage_AfterTerminal_ReturnsTerminalUnchanged_WithoutWriting()
    {
        var log = Open();
        await AcceptNew(log, Submission("op-1"));
        OperationStatus cancelled = await log.CompleteAsync("op-1", OperationState.Cancelled);
        int lines = LogLines().Length;

        OperationStatus late = await log.RecordStageAsync("op-1", new OperationStageUpdate { Stage = OperationStages.Pulling });

        Assert.Equal(cancelled, late);
        Assert.Equal(lines, LogLines().Length);
    }

    // ---- Terminal states --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Complete_Succeeded_WithResult_IsDurable()
    {
        var log = Open();
        await AcceptNew(log, Submission("op-1"));
        await log.RecordStageAsync("op-1", new OperationStageUpdate { Stage = OperationStages.Started });
        _time.Advance(TimeSpan.FromSeconds(3));
        DateTimeOffset end = _time.GetUtcNow();

        OperationStatus done = await log.CompleteAsync("op-1", OperationState.Succeeded, result: Json("{\"modelId\":\"qwen36-35b\",\"hostPort\":8101}"));

        Assert.Equal(OperationState.Succeeded, done.State);
        Assert.Equal(OperationStages.Completed, done.Stage);
        Assert.Equal(end, done.EndedAt);
        Assert.Equal(8101, done.Result!.Value.GetProperty("hostPort").GetInt32());
        Assert.Null(done.Error);
        Assert.Empty(log.ListActive());
        Assert.Empty(log.ListRecoverable());

        var reopened = Reopen(log);
        OperationStatus reloaded = reopened.Find("op-1")!;
        Assert.Equal(done.State, reloaded.State);
        Assert.Equal(done.EndedAt, reloaded.EndedAt);
        Assert.Equal(done.Result.Value.GetRawText(), reloaded.Result!.Value.GetRawText());
    }

    [Fact]
    public async Task Complete_Failed_KeepsLastStageAndError()
    {
        var log = Open();
        await AcceptNew(log, Submission("op-1"));
        await log.RecordStageAsync("op-1", new OperationStageUpdate { Stage = OperationStages.Pulling });

        var error = new AgentError { Code = ErrorCodes.Superseded, Message = "master superseded" };
        OperationStatus failed = await log.CompleteAsync("op-1", OperationState.Failed, error);

        Assert.Equal(OperationState.Failed, failed.State);
        Assert.Equal(OperationStages.Pulling, failed.Stage);
        Assert.Equal(error, failed.Error);
    }

    [Fact]
    public async Task Complete_Twice_ReturnsTheFirstTerminalStateUnchanged()
    {
        var log = Open();
        await AcceptNew(log, Submission("op-1"));
        OperationStatus first = await log.CompleteAsync("op-1", OperationState.Succeeded);
        int lines = LogLines().Length;

        OperationStatus second = await log.CompleteAsync("op-1", OperationState.Failed, new AgentError { Code = ErrorCodes.InternalError, Message = "x" });

        Assert.Equal(first, second);
        Assert.Equal(lines, LogLines().Length);
    }

    [Fact]
    public async Task Complete_WithNonTerminalState_Throws()
    {
        var log = Open();
        await AcceptNew(log, Submission("op-1"));

        await Assert.ThrowsAsync<ArgumentException>(() => log.CompleteAsync("op-1", OperationState.Running));
    }

    [Fact]
    public async Task Complete_ClearsTheReservation()
    {
        var log = Open();
        await AcceptNew(log, Submission("op-1"));
        await log.RecordStageAsync("op-1", new OperationStageUpdate
        {
            Stage = OperationStages.Admitted,
            Reservation = new ReservationInfo { OperationId = "op-1", ModelId = "m", BudgetGiB = 1, HostPort = 8101, CreatedAt = _time.GetUtcNow() },
        });
        await log.CompleteAsync("op-1", OperationState.Cancelled);
        await AcceptNew(log, Submission("op-2"));

        RecoverableOperation only = Assert.Single(Reopen(log).ListRecoverable());
        Assert.Equal("op-2", only.Status.Id);
    }

    // ---- Lists ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ListActive_IsOldestFirst_AndListRecent_NewestFirstAndBounded()
    {
        var log = Open();
        for (int i = 0; i < 5; i++)
        {
            await AcceptNew(log, Submission($"op-{i}"));
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        await log.CompleteAsync("op-3", OperationState.Succeeded);
        _time.Advance(TimeSpan.FromSeconds(1));
        await log.CompleteAsync("op-1", OperationState.Failed);
        _time.Advance(TimeSpan.FromSeconds(1));
        await log.CompleteAsync("op-4", OperationState.Cancelled);

        Assert.Equal(["op-0", "op-2"], log.ListActive().Select(s => s.Id));
        Assert.Equal(["op-4", "op-1", "op-3"], log.ListRecent(10).Select(s => s.Id));
        Assert.Equal(["op-4", "op-1"], log.ListRecent(2).Select(s => s.Id));
        Assert.Empty(log.ListRecent(0));
    }

    // ---- Torn lines and corruption ----------------------------------------------------------------------------------

    [Fact]
    public async Task TornLastLine_IsIgnoredAndReported_AndTheNextAppendIsClean()
    {
        var log = Open();
        await AcceptNew(log, Submission("op-1"));
        await log.RecordStageAsync("op-1", new OperationStageUpdate { Stage = OperationStages.Admitted });
        log.Dispose();
        _open.Remove(log);

        // Crash mid-write: half a stage line, no newline.
        await File.AppendAllTextAsync(LogPath, "{\"type\":\"stage\",\"operationId\":\"op-1\",\"at\":\"2026-10-0");

        var recovered = Open();
        Assert.Equal(1, recovered.TornLinesIgnored);
        Assert.Equal(OperationStages.Admitted, recovered.Find("op-1")!.Stage);

        // The torn tail is cut, so the next line does not merge with it.
        await recovered.RecordStageAsync("op-1", new OperationStageUpdate { Stage = OperationStages.Pulling });
        await AcceptNew(recovered, Submission("op-2"));

        var again = Reopen(recovered);
        Assert.Equal(0, again.TornLinesIgnored);
        Assert.Equal(OperationStages.Pulling, again.Find("op-1")!.Stage);
        Assert.NotNull(again.Find("op-2"));
        Assert.All(LogLines(), line => JsonSerializer.Deserialize<OperationLogEntry>(line, AgentJson.Options));
    }

    [Fact]
    public async Task TornAcceptedLine_MeansTheOperationWasNeverAccepted()
    {
        var log = Open();
        await AcceptNew(log, Submission("op-1"));
        log.Dispose();
        _open.Remove(log);
        string full = ReadShared(LogPath);

        // Only part of the accepted line made it (no ack was sent): the id is unknown, so a retry runs it.
        await File.WriteAllTextAsync(LogPath, full[..(full.Length / 2)]);

        var recovered = Open();
        Assert.Equal(1, recovered.TornLinesIgnored);
        Assert.Null(recovered.Find("op-1"));
        Assert.Equal(OperationAcceptOutcome.Accepted, (await recovered.AcceptAsync(Submission("op-1"))).Outcome);
    }

    [Fact]
    public async Task CorruptMiddleLine_IsIgnoredAndCounted_OtherLinesStillApply()
    {
        var log = Open();
        await AcceptNew(log, Submission("op-1"));
        log.Dispose();
        _open.Remove(log);
        await File.AppendAllTextAsync(LogPath, "garbage{\n\n");

        var second = Open();
        await second.CompleteAsync("op-1", OperationState.Succeeded);

        var third = Reopen(second);
        Assert.Equal(1, third.TornLinesIgnored);
        Assert.Equal(OperationState.Succeeded, third.Find("op-1")!.State);
    }

    [Fact]
    public async Task LinesForUnknownOperations_AreIgnored()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        var stray = new OperationLogEntry { Type = OperationLogEntryType.Stage, OperationId = "ghost", At = _time.GetUtcNow(), Stage = OperationStages.Pulling };
        await File.WriteAllTextAsync(LogPath, JsonSerializer.Serialize(stray, AgentJson.Options) + "\n");

        var log = Open();

        Assert.Null(log.Find("ghost"));
        Assert.Equal(1, log.OrphanLinesIgnored);
        Assert.Equal(0, log.TornLinesIgnored);
    }

    [Fact]
    public void Load_ContractFixture_FoldsToTheExpectedStatuses()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.Copy(FixturePath("operation-log.jsonl"), LogPath);

        var log = Open();

        OperationStatus launch = log.Find("7c4e9a1b2d3f4e5a8b6c7d8e9f0a1b2c")!;
        Assert.Equal(OperationState.Succeeded, launch.State);
        Assert.Equal(OperationStages.Completed, launch.Stage);
        Assert.Equal("qwen36-35b", launch.Target);
        Assert.Equal(8101, launch.Result!.Value.GetProperty("hostPort").GetInt32());
        Assert.Null(log.Find("0a1b2c3d4e5f60718293a4b5c6d7e8f9")); // completed line without an accepted line
        Assert.Equal(1, log.OrphanLinesIgnored);
        Assert.Equal(0, log.TornLinesIgnored);
    }

    // ---- Compaction and retention (§4.4 items 1, 6) ----------------------------------------------------------------

    [Fact]
    public async Task Compact_WritesSnapshot_TruncatesLog_LeavesNoTempFile_AndReloads()
    {
        var log = Open();
        await AcceptNew(log, Submission("op-1"));
        await log.RecordStageAsync("op-1", new OperationStageUpdate { Stage = OperationStages.Admitted });
        await AcceptNew(log, Submission("op-2"));
        await log.CompleteAsync("op-2", OperationState.Succeeded);
        OperationStatus op1 = log.Find("op-1")!;
        OperationStatus op2 = log.Find("op-2")!;

        await log.CompactAsync();

        Assert.True(File.Exists(CompactedPath));
        Assert.False(File.Exists(CompactedPath + ".tmp"));
        Assert.Equal(0, new FileInfo(LogPath).Length);

        // Appends after compaction land in the fresh log.
        await log.RecordStageAsync("op-1", new OperationStageUpdate { Stage = OperationStages.Pulling });
        await AcceptNew(log, Submission("op-3"));

        var reopened = Reopen(log);
        Assert.Equal(OperationStages.Pulling, reopened.Find("op-1")!.Stage);
        Assert.Equal(op1.PayloadDigest, reopened.Find("op-1")!.PayloadDigest);
        Assert.Equal(op2.State, reopened.Find("op-2")!.State);
        Assert.Equal(op2.EndedAt, reopened.Find("op-2")!.EndedAt);
        Assert.NotNull(reopened.Find("op-3"));
        Assert.Equal(["op-1", "op-3"], reopened.ListActive().Select(s => s.Id));
        Assert.Equal(Master, reopened.ListRecoverable()[0].Master);
    }

    [Fact]
    public async Task Compact_NeverPrunesActiveOperations_EvenLongExpired()
    {
        var log = Open(minRetained: 0);
        await AcceptNew(log, Submission("active-accepted"));
        await AcceptNew(log, Submission("active-running"));
        await log.RecordStageAsync("active-running", new OperationStageUpdate { Stage = OperationStages.Dispatched });
        await AcceptNew(log, Submission("done"));
        await log.CompleteAsync("done", OperationState.Succeeded);

        _time.Advance(TimeSpan.FromDays(30));
        await log.CompactAsync();

        Assert.NotNull(log.Find("active-accepted"));
        Assert.NotNull(log.Find("active-running"));
        Assert.Null(log.Find("done"));
        var reopened = Reopen(log, minRetained: 0);
        Assert.Equal(["active-accepted", "active-running"], reopened.ListActive().Select(s => s.Id));
    }

    [Fact]
    public async Task Compact_KeepsTerminalRecordsUntilExpiresAtPlusTenMinutes()
    {
        var log = Open(minRetained: 0);
        DateTimeOffset start = _time.GetUtcNow();
        await AcceptNew(log, Submission("op-1", expiresAt: start.AddHours(1)));
        await log.CompleteAsync("op-1", OperationState.Succeeded);

        _time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(9));
        await log.CompactAsync();
        Assert.NotNull(log.Find("op-1"));

        _time.Advance(TimeSpan.FromMinutes(1));
        await log.CompactAsync();
        Assert.Null(log.Find("op-1"));
    }

    [Fact]
    public async Task Compact_KeepsAtLeastTheMostRecentNRecords_EvenWhenExpired()
    {
        var log = Open(minRetained: 3);
        for (int i = 0; i < 6; i++)
        {
            await AcceptNew(log, Submission($"op-{i}"));
            await log.CompleteAsync($"op-{i}", OperationState.Succeeded);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        _time.Advance(TimeSpan.FromDays(2));
        await log.CompactAsync();

        Assert.Equal(["op-5", "op-4", "op-3"], log.ListRecent(100).Select(s => s.Id));
        var reopened = Reopen(log, minRetained: 3);
        Assert.Equal(["op-5", "op-4", "op-3"], reopened.ListRecent(100).Select(s => s.Id));
    }

    [Fact]
    public async Task Compact_PrunedIdWithExpiredEnvelope_IsAnsweredExpired_NeverReExecuted()
    {
        var log = Open(minRetained: 0);
        OperationSubmission submission = Submission("op-1");
        await AcceptNew(log, submission);
        await log.CompleteAsync("op-1", OperationState.Succeeded);

        _time.Advance(OperationRetention.EnvelopeLifetime + OperationRetention.RetentionMargin + TimeSpan.FromSeconds(1));
        await log.CompactAsync();
        Assert.Null(log.Find("op-1"));

        OperationAcceptance retry = await log.AcceptAsync(submission);
        Assert.Equal(OperationAcceptOutcome.Expired, retry.Outcome);
        Assert.Equal(OperationAcceptOutcome.Expired, (await Reopen(log, minRetained: 0).AcceptAsync(submission)).Outcome);
    }

    [Fact]
    public async Task Compact_WithDefaultRetention_KeepsTheLast1000ExpiredRecords()
    {
        // Seed 1,005 expired, terminal operations directly into the log (no per-line fsync needed for setup).
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        DateTimeOffset now = _time.GetUtcNow();
        var builder = new StringBuilder();
        for (int i = 0; i < 1_005; i++)
        {
            DateTimeOffset at = now.AddDays(-3).AddSeconds(i);
            string id = $"op-{i:D4}";
            builder.Append(JsonSerializer.Serialize(new OperationLogEntry
            {
                Type = OperationLogEntryType.Accepted,
                OperationId = id,
                At = at,
                Kind = OperationKinds.ImagePull,
                Target = "img",
                PayloadDigest = "d",
                Payload = Json("{}"),
                IssuedAt = at,
                ExpiresAt = at.AddHours(24),
                Master = Master,
                State = OperationState.Accepted,
                Stage = OperationStages.Accepted,
            }, AgentJson.Options)).Append('\n');
            builder.Append(JsonSerializer.Serialize(new OperationLogEntry
            {
                Type = OperationLogEntryType.Completed,
                OperationId = id,
                At = at.AddMilliseconds(500),
                State = OperationState.Succeeded,
                Stage = OperationStages.Completed,
            }, AgentJson.Options)).Append('\n');
        }

        await File.WriteAllTextAsync(LogPath, builder.ToString());
        var log = Open();
        Assert.Equal(1_005, log.ListRecent(int.MaxValue).Count);

        await log.CompactAsync();

        IReadOnlyList<OperationStatus> kept = log.ListRecent(int.MaxValue);
        Assert.Equal(OperationRetention.MinRetainedRecords, kept.Count);
        Assert.Equal("op-1004", kept[0].Id);
        Assert.Equal("op-0005", kept[^1].Id);
        Assert.Equal(OperationRetention.MinRetainedRecords, Reopen(log).ListRecent(int.MaxValue).Count);
    }

    [Fact]
    public async Task Compact_CrashBetweenSnapshotRenameAndLogTruncation_DoesNotReplayOrResurrect()
    {
        var log = Open(minRetained: 0);
        await AcceptNew(log, Submission("pruned"));
        await log.CompleteAsync("pruned", OperationState.Succeeded);
        _time.Advance(TimeSpan.FromDays(2));
        await AcceptNew(log, Submission("active"));
        await log.RecordStageAsync("active", new OperationStageUpdate { Stage = OperationStages.Admitted });
        await log.RecordStageAsync("active", new OperationStageUpdate { Stage = OperationStages.Pulling, Progress = new OperationProgress { Percent = 50 } });
        await AcceptNew(log, Submission("kept"));
        await log.CompleteAsync("kept", OperationState.Failed, new AgentError { Code = ErrorCodes.InternalError, Message = "boom" });
        byte[] logBeforeCompaction = ReadSharedBytes(LogPath);

        await log.CompactAsync();
        OperationStatus active = log.Find("active")!;
        log.Dispose();
        _open.Remove(log);

        // The truncation never reached the disk: the old log is still there next to the new snapshot.
        await File.WriteAllBytesAsync(LogPath, logBeforeCompaction);

        var recovered = Open(minRetained: 0);
        Assert.Null(recovered.Find("pruned")); // not resurrected from the covered prefix
        Assert.Equal(active, recovered.Find("active"));
        Assert.Equal(OperationState.Failed, recovered.Find("kept")!.State);
        Assert.Equal(0, recovered.TornLinesIgnored);

        // New appends after the covered prefix still apply on the next load.
        await recovered.RecordStageAsync("active", new OperationStageUpdate { Stage = OperationStages.Created });
        var again = Reopen(recovered, minRetained: 0);
        Assert.Equal(OperationStages.Created, again.Find("active")!.Stage);
        Assert.Null(again.Find("pruned"));
    }

    [Fact]
    public async Task Load_SnapshotFromNewerBuild_Throws()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CompactedPath)!);
        await File.WriteAllTextAsync(CompactedPath, "{\"schemaVersion\":99,\"operations\":[]}");

        Assert.Throws<InvalidDataException>(() => Open());
    }

    [Fact]
    public async Task Load_CorruptSnapshot_Throws_RatherThanForgettingOperations()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CompactedPath)!);
        await File.WriteAllTextAsync(CompactedPath, "{\"schemaVersion\":1,\"operations\":[");

        Assert.Throws<InvalidDataException>(() => Open());
    }

    [Fact]
    public async Task CompactionService_CompactsAtStart()
    {
        var log = Open(minRetained: 0);
        await AcceptNew(log, Submission("op-1"));
        await log.CompleteAsync("op-1", OperationState.Succeeded);
        _time.Advance(TimeSpan.FromDays(2));

        using var service = new OperationLogCompactionService(log, _time, TimeSpan.FromHours(24));
        await service.StartAsync(CancellationToken.None);
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (service.CompletedRuns == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, service.CompletedRuns);
        Assert.Null(log.Find("op-1"));
        Assert.True(File.Exists(CompactedPath));
    }

    [Fact]
    public void AddAgentOperationLog_RegistersLogHealthAndCompaction()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddAgentOperationLog(_ => new OperationLogOptions { StateDirectory = _stateDir, BootId = "boot-1" });

        using ServiceProvider provider = services.BuildServiceProvider();
        var log = provider.GetRequiredService<IOperationLog>();

        Assert.Same(log, provider.GetRequiredService<OperationLog>());
        Assert.Single(provider.GetServices<IHealthContributor>().OfType<OperationLogHealth>());
        Assert.DoesNotContain(provider.GetServices<IHealthContributor>(), h => ReferenceEquals(h, log));
        Assert.Contains(provider.GetServices<IHostedService>(), h => h is OperationLogCompactionService);
    }

    [Fact]
    public async Task AddAgentOperationLog_TheLogOutlivesItsWriters_WhenTheHealthContributorsAreResolvedLate()
    {
        // W8 review (MN-128): the container disposes a singleton at the position each of its registrations first produced
        // it, in reverse order. The health watcher resolves IHealthContributor at host start, after the storage services;
        // an alias returning the log itself made the container dispose the log before those services, whose disposal
        // still writes terminal records (ObjectDisposedException in RealAgentContractTests at agent shutdown).
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddAgentOperationLog(_ => new OperationLogOptions { StateDirectory = _stateDir, BootId = "boot-1" });
        services.AddSingleton<LogWriterOnDispose>();

        var provider = services.BuildServiceProvider();
        var writer = provider.GetRequiredService<LogWriterOnDispose>(); // a storage service: created with the log
        _ = provider.GetServices<IHealthContributor>().ToList();        // the health watcher, later
        await provider.DisposeAsync();

        Assert.Null(writer.Failure);
        Assert.True(writer.Wrote);
    }

    /// <summary>Writes to the log while it is disposed, as the download and image services finish a record at shutdown.</summary>
    private sealed class LogWriterOnDispose(IOperationLog log) : IAsyncDisposable
    {
        public Exception? Failure { get; private set; }

        public bool Wrote { get; private set; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await log.CompactAsync();
                Wrote = true;
            }
            catch (ObjectDisposedException ex)
            {
                Failure = ex;
            }
        }
    }

    [Fact]
    public void Options_DefaultRetention_IsTheContractMinimum()
    {
        var options = new OperationLogOptions { StateDirectory = _stateDir, BootId = "b" };

        Assert.Equal(1_000, options.MinRetainedRecords);
        Assert.EndsWith(Path.Combine("operations", "log.jsonl"), options.LogPath, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("operations", "operations.json"), options.CompactedPath, StringComparison.Ordinal);
    }

    private static string FixturePath(string name, [CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(sourceFile))!, "Contracts", "Fixtures", name);
}
