using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Audit;
using MirbadLLM.UnitTests.Auth;
using ManualClock = MirbadLLM.UnitTests.Pki.ManualTimeProvider;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-116: the master's audit log (design §10.5): the append-only, bounded JSONL store and its reader, the secret
/// redaction, the request filter for the pre-P2 <c>/api</c> routes (checked against the real app's route table), the
/// operation-reference observer for <c>system:*</c> actors and the query endpoint.
/// </summary>
public sealed class AuditTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // =====================================================================================================
    // Store and reader
    // =====================================================================================================

    [Fact]
    public async Task Lines_AreAppendedOnePerRecord_AndReadNewestFirstWithFilters()
    {
        using var dir = new TempDir();
        var clock = new ManualClock(Start);
        using var log = new JsonlAuditLog(dir.Path, clock);

        for (int i = 0; i < 6; i++)
        {
            await log.WriteAsync(Line(clock.GetUtcNow(), node: i % 2 == 0 ? "dgx" : "pc", model: i < 3 ? "m1" : "m2", detail: "n" + i), Ct);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        string file = Path.Combine(dir.Path, "2026-10.jsonl");
        string[] raw = File.ReadAllLines(file);
        Assert.Equal(6, raw.Length);
        Assert.All(raw, l => Assert.StartsWith("{\"time\":", l, StringComparison.Ordinal)); // camelCase, one line, not indented

        IReadOnlyList<AuditRecord> all = await log.ReadAsync(new AuditQuery(), Ct);
        Assert.Equal(["n5", "n4", "n3", "n2", "n1", "n0"], all.Select(r => r.Detail));
        Assert.Equal(["n4", "n2", "n0"], (await log.ReadAsync(new AuditQuery { NodeId = "dgx" }, Ct)).Select(r => r.Detail));
        Assert.Equal(["n2", "n0"], (await log.ReadAsync(new AuditQuery { NodeId = "dgx", ModelId = "m1" }, Ct)).Select(r => r.Detail));
        Assert.Equal(["n5", "n4"], (await log.ReadAsync(new AuditQuery { Since = Start.AddMinutes(4) }, Ct)).Select(r => r.Detail));
        Assert.Equal(["n5", "n4"], (await log.ReadAsync(new AuditQuery { Max = 2 }, Ct)).Select(r => r.Detail));

        // The fixture's shape round-trips through a line (MasterStateJson, camelCase).
        AuditRecord parsed = JsonSerializer.Deserialize<AuditRecord>(raw[0], MasterStateJson.Options)!;
        Assert.Equal(all[^1], parsed);
    }

    [Fact]
    public async Task TheLog_IsAppendOnly_AndATornLastLineNeverMergesWithTheNext()
    {
        using var dir = new TempDir();
        var clock = new ManualClock(Start);
        using var log = new JsonlAuditLog(dir.Path, clock);
        await log.WriteAsync(Line(Start, detail: "first"), Ct);
        string file = Path.Combine(dir.Path, "2026-10.jsonl");
        byte[] before = File.ReadAllBytes(file);

        // A crash mid-write leaves half a line without a newline.
        File.AppendAllText(file, "{\"time\":\"2026-10-06T09:00:00+00:00\",\"actor\":\"adm");
        await log.WriteAsync(Line(Start, detail: "second"), Ct);
        await log.WriteAsync(Line(Start, detail: "third"), Ct);

        byte[] after = File.ReadAllBytes(file);
        Assert.Equal(before, after[..before.Length]); // earlier bytes untouched
        Assert.Equal(["third", "second", "first"], (await log.ReadAsync(new AuditQuery(), Ct)).Select(r => r.Detail));
        Assert.Equal(4, File.ReadAllLines(file).Length);
    }

    [Fact]
    public async Task AFullFile_RollsToNumberedSegments_AndTheTotalSizeIsBounded()
    {
        using var dir = new TempDir();
        var clock = new ManualClock(Start);
        var options = new AuditLogOptions { MaxFileBytes = 2048, MaxTotalBytes = 8192 };
        using var log = new JsonlAuditLog(dir.Path, clock, auditOptions: options);

        for (int i = 0; i < 200; i++)
        {
            await log.WriteAsync(Line(clock.GetUtcNow(), detail: $"line {i:D3} " + new string('x', 60)), Ct);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        string[] files = [.. Directory.GetFiles(dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];
        Assert.Contains("2026-10.jsonl", files);
        Assert.Contains(files, f => f!.StartsWith("2026-10.", StringComparison.Ordinal) && f != "2026-10.jsonl");
        Assert.All(Directory.GetFiles(dir.Path), f => Assert.True(new FileInfo(f).Length <= options.MaxFileBytes, f));
        Assert.True(Directory.GetFiles(dir.Path).Sum(f => new FileInfo(f).Length) <= options.MaxTotalBytes);

        // The newest lines survive in order; the oldest segments were deleted.
        IReadOnlyList<AuditRecord> read = await log.ReadAsync(new AuditQuery { Max = 1000 }, Ct);
        Assert.StartsWith("line 199", read[0].Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(read, r => r.Detail!.StartsWith("line 000", StringComparison.Ordinal));
        Assert.Equal(read.OrderByDescending(r => r.Time).Select(r => r.Detail), read.Select(r => r.Detail));
        for (int i = 1; i < read.Count; i++)
        {
            Assert.True(string.CompareOrdinal(read[i - 1].Detail, read[i].Detail) > 0); // no gap inside what is kept
        }
    }

    [Fact]
    public async Task FilesOlderThanTwelveMonths_AreDeleted()
    {
        using var dir = new TempDir();
        var clock = new ManualClock(new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero));
        using var log = new JsonlAuditLog(dir.Path, clock);
        for (int month = 0; month < 15; month++)
        {
            await log.WriteAsync(Line(clock.GetUtcNow(), detail: "month " + month), Ct);
            clock.Now = clock.Now.AddMonths(1);
        }

        string[] names = [.. Directory.GetFiles(dir.Path).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];
        Assert.Equal(12, names.Length);
        Assert.Equal("2025-04.jsonl", names[0]);
        Assert.Equal("2026-03.jsonl", names[^1]);
        Assert.Equal(12, (await log.ReadAsync(new AuditQuery { Max = 100 }, Ct)).Count);
        Assert.Equal(["month 14"], (await log.ReadAsync(new AuditQuery { Since = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero) }, Ct)).Select(r => r.Detail));
    }

    [Fact]
    public async Task AWriteThatCannotReachTheDisk_IsDroppedWithoutThrowing()
    {
        using var dir = new TempDir();
        string blocked = Path.Combine(dir.Path, "audit");
        File.WriteAllText(blocked, "not a directory");
        using var log = new JsonlAuditLog(blocked, TimeProvider.System);

        await log.WriteAsync(Line(Start), Ct);

        Assert.Equal(1, log.DroppedLines);
        Assert.Empty(await log.ReadAsync(new AuditQuery(), Ct));
    }

    [Fact]
    public void FileNames_ActiveAndSegments_AreRecognised()
    {
        Assert.True(JsonlAuditLog.TryParseName("2026-10.jsonl", out string month, out int? segment));
        Assert.Equal("2026-10", month);
        Assert.Null(segment);
        Assert.True(JsonlAuditLog.TryParseName("2026-10.3.jsonl", out _, out segment));
        Assert.Equal(3, segment);
        Assert.False(JsonlAuditLog.TryParseName("2026-13.jsonl", out _, out _));
        Assert.False(JsonlAuditLog.TryParseName("2026-10.0.jsonl", out _, out _));
        Assert.False(JsonlAuditLog.TryParseName("notes.jsonl", out _, out _));
        Assert.False(JsonlAuditLog.TryParseName("2026-10.json", out _, out _));
    }

    // =====================================================================================================
    // Redaction
    // =====================================================================================================

    [Fact]
    public async Task SecretsNeverReachAnAuditLine()
    {
        using var dir = new TempDir();
        using var log = new JsonlAuditLog(dir.Path, TimeProvider.System);
        using var key = System.Security.Cryptography.ECDsa.Create();
        string pem = key.ExportPkcs8PrivateKeyPem();
        string pin = "XP57F1GNCuXwnRAJ2V/vAio4JzkDigX2TYyjTJD+Tek=";
        string joinToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        await log.WriteAsync(Line(DateTimeOffset.UtcNow, detail: "key " + pem), Ct);
        await log.WriteAsync(Line(DateTimeOffset.UtcNow, detail: $"api key ssk_abcdefghijklmnopqrstuvwxyz0123456789 and hf_ABCDEFGHIJKLMNOPQRSTUVWX token {joinToken} pin {pin}") with { Target = "line1\nline2" }, Ct);

        string text = string.Join("\n", Directory.GetFiles(dir.Path).Select(File.ReadAllText));
        Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ssk_abcdefghijklmnop", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hf_ABCDEFGHIJ", text, StringComparison.Ordinal);
        Assert.DoesNotContain(joinToken, text, StringComparison.Ordinal);
        Assert.Contains(pin.Replace("+", "\\u002B", StringComparison.Ordinal), text, StringComparison.Ordinal); // a pin stays readable
        IReadOnlyList<AuditRecord> lines = await log.ReadAsync(new AuditQuery(), Ct);
        Assert.Equal(AuditRedaction.Redacted, lines[1].Detail);
        Assert.Equal("line1 line2", lines[0].Target);
        Assert.Equal(2, File.ReadAllLines(Directory.GetFiles(dir.Path).Single()).Length);
        Assert.True(AuditRedaction.LooksSecret(joinToken));
        Assert.False(AuditRedaction.LooksSecret(pin));
        Assert.False(AuditRedaction.LooksSecret("0f9a1b2c3d4e5f60718293a4b5c6d7e8")); // a node uid / operation id
    }

    [Fact]
    public void Redaction_CoversAssignmentsAndATokenAfterAnEqualsSign_AndKeepsOrdinaryDetails()
    {
        string joinToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        AuditRecord record = Line(Start, detail: $"url ?token={joinToken}&x=1; password=hunter2, Passphrase: \"two words\" api_key='abc' secret=s3") with { Target = $"t={joinToken}" };

        AuditRecord clean = AuditRedaction.Sanitize(record);

        Assert.DoesNotContain(joinToken, clean.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain(joinToken, clean.Target!, StringComparison.Ordinal);
        foreach (string secret in new[] { "hunter2", "two words", "abc", "s3" })
        {
            Assert.DoesNotContain(secret, clean.Detail!, StringComparison.Ordinal);
        }

        Assert.Contains("password=***", clean.Detail!, StringComparison.Ordinal);
        Assert.True(AuditRedaction.LooksSecret("token=" + joinToken));
        Assert.True(AuditRedaction.LooksSecret("password: x"));

        // What the writers actually put in details stays readable.
        foreach (string ordinary in new[]
                 {
                     "rekey token, expires 2026-10-06T09:00:00.0000000+00:00",
                     "enroll: token Expired",
                     "rekey: Bootstrap token (valid only on /enroll; not consumed)",
                     "stage Started: started: new CA XP57F1GNCuXwnRAJ2V/vAio4JzkDigX2TYyjTJD+Tek=, 2 node(s)",
                     "the secrets section is missing",
                 })
        {
            Assert.Equal(ordinary, AuditRedaction.Sanitize(Line(Start, detail: ordinary)).Detail);
        }
    }

    [Fact]
    public async Task ConcurrentWriters_EachGetOneWholeLine_AndAZeroFilledTornTailIsSkipped()
    {
        using var dir = new TempDir();
        var clock = new ManualClock(Start);
        using var log = new JsonlAuditLog(dir.Path, clock);
        await log.WriteAsync(Line(Start, detail: "first"), Ct);
        string file = Path.Combine(dir.Path, "2026-10.jsonl");

        // ext4 after a power loss can leave the last block zero-filled instead of torn mid-text.
        File.AppendAllText(file, new string('\0', 64));
        await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() => log.WriteAsync(Line(Start, detail: "w" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)), Ct))));

        IReadOnlyList<AuditRecord> read = await log.ReadAsync(new AuditQuery { Max = 1000 }, Ct);
        Assert.Equal(65, read.Count);
        Assert.Equal(Enumerable.Range(0, 64).Select(i => "w" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).Append("first").Order(StringComparer.Ordinal),
            read.Select(r => r.Detail!).Order(StringComparer.Ordinal));
        string[] lines = File.ReadAllLines(file);
        Assert.Equal(66, lines.Length); // 65 records + the closed zero-filled tail
        Assert.All(lines.Where(l => !l.StartsWith('\0')), l => Assert.StartsWith("{\"time\":", l, StringComparison.Ordinal));
    }

    // =====================================================================================================
    // Request filter
    // =====================================================================================================

    [Fact]
    public async Task TheFilter_WritesOneLinePerAuditedCall_FromRouteValuesAndResultOnly()
    {
        await using var app = await FilterApp.StartAsync();
        HttpClient client = app.Client;

        // Admin start that submits an agent operation: the line carries its id; the observer writes no second line.
        using (HttpRequestMessage start = Admin(HttpMethod.Post, "/api/models/qwen/start"))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(start, Ct)).StatusCode);
        }

        // Refusal with a ProblemDetails code; a failing handler; a body holding a password; a route's {action}.
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(Admin(HttpMethod.Put, "/api/models/qwen", "{}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await client.SendAsync(Admin(HttpMethod.Delete, "/api/models/qwen"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Admin(HttpMethod.Post, "/api/services/whitelist",
            "{\"unit\":\"ollama.service\",\"password\":\"hunter2-secret\"}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Admin(HttpMethod.Post, "/api/services/ollama.service/restart"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(Admin(HttpMethod.Post, "/api/models",
            "{\"id\":\"new-model\",\"servedName\":\"new\",\"modelPath\":\"/models/new\"}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/downloads", null, Ct)).StatusCode); // no session: api:{ip}

        // Not audited here: a GET, and a P2 route mapped in the same audited group (its task writes its own line).
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Admin(HttpMethod.Get, "/api/models/qwen"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Admin(HttpMethod.Post, "/api/models/qwen/move"), Ct)).StatusCode);

        List<AuditRecord> lines = [.. (await app.Log.ReadAsync(new AuditQuery { Max = 100 }, Ct)).Reverse()];
        Assert.Equal(7, lines.Count);
        string admin = AuditActors.Admin(AuditActorResolver.SessionHash("cookie-value-123"));

        Assert.Equal((admin, AuditActions.ModelStart, "qwen", "op-qwen-1", AuditResults.Succeeded), (lines[0].Actor, lines[0].Action, lines[0].ModelId, lines[0].OperationId, lines[0].Result));
        Assert.Equal((AuditActions.ModelUpdate, AuditResults.Refused, ErrorCodes.PlacementChanged, "HTTP 409"), (lines[1].Action, lines[1].Result, lines[1].ErrorCode, lines[1].Detail));
        Assert.Equal((AuditActions.ModelDelete, AuditResults.Failed), (lines[2].Action, lines[2].Result));
        Assert.Contains("unhandled InvalidOperationException", lines[2].Detail, StringComparison.Ordinal);
        Assert.Equal((AuditActions.HostService, "whitelist add", null), (lines[3].Action, lines[3].Detail, lines[3].Target));
        Assert.Equal((AuditActions.HostService, "ollama.service", "restart"), (lines[4].Action, lines[4].Target, lines[4].Detail));
        Assert.Equal((AuditActions.ModelCreate, "new-model"), (lines[5].Action, lines[5].ModelId));
        Assert.Equal((AuditActions.Download, AuditResults.Accepted), (lines[6].Action, lines[6].Result));
        Assert.StartsWith("api:", lines[6].Actor, StringComparison.Ordinal);

        string text = string.Join("\n", Directory.GetFiles(app.Log.Directory).Select(File.ReadAllText));
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("cookie-value-123", text, StringComparison.Ordinal);

        // The operation submitted inside the request is closed later: no observer line (the request line holds it).
        await app.Observer.CloseAsync("op-qwen-1", AgentOperationOutcome.Succeeded, null, Ct);
        Assert.Equal(7, (await app.Log.ReadAsync(new AuditQuery { Max = 100 }, Ct)).Count);
    }

    [Fact]
    public async Task EveryPreP2MutatingApiRouteOfTheRealApp_IsAudited_AndEveryAuditedRouteExists()
    {
        using var factory = new AuthWebFactory();
        _ = factory.Server;
        EndpointDataSource endpoints = factory.Services.GetRequiredService<EndpointDataSource>();
        string[] mutating = ["POST", "PUT", "PATCH", "DELETE"];
        List<(string Method, string Pattern)> routes = [.. endpoints.Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api", StringComparison.Ordinal) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => (m, e.RoutePattern.RawText!)))
            .Where(r => mutating.Contains(r.Item1))];

        // Not the filter's business: read-only previews that use POST for their body (no audit line at all), and P2 routes
        // that write their own audit line in the handler (P2 rule; each one's own tests check its line). MN-130 adds the
        // remaining P2 routes here when it maps them.
        (string, string)[] notFiltered =
        [
            ("POST", "/api/nodes/{id}/fit"),   // MN-108 fit preview, read-only
            ("DELETE", "/api/nodes/{id}/job"), // MN-111, audited in JobApiEndpoints
            // MN-102 node admin API (NodeAdminService writes node.* lines), mapped by the W7 review (MN-126).
            ("POST", "/api/nodes/join-tokens"),
            ("DELETE", "/api/nodes/{id}/join-tokens"),
            ("POST", "/api/nodes/{id}/approve"),
            ("POST", "/api/nodes/{id}/reject"),
            ("POST", "/api/nodes/{id}/drain"),
            ("POST", "/api/nodes/{id}/undrain"),
            ("PATCH", "/api/nodes/{id}"),
            ("DELETE", "/api/nodes/{id}"),
            ("POST", "/api/nodes/{id}/agent-update"), // NodeAdminService writes node.agent-update (docs/multinode/agent-updates.md)
            ("POST", "/api/agent-updates/"),          // update all: one node.agent-update line per node
            ("PUT", "/api/agent-updates/settings"),   // agent-updates.settings
            // MN-115 takeover (FencingApiEndpoints and TakeoverService write fencing.* lines).
            ("POST", "/api/fencing/takeover"),
            ("POST", "/api/fencing/takeover/continue"),
            // MN-109 move and unplace (MoveService writes model.move, model.move.force-commit and model.unplace lines).
            ("POST", "/api/models/{id}/move"),
            ("POST", "/api/models/{id}/move/force-commit"),
            ("POST", "/api/models/{id}/move/cancel"),
            ("POST", "/api/models/{id}/unplace"),
        ];
        routes.RemoveAll(r => notFiltered.Contains(r));

        Assert.NotEmpty(routes);
        foreach ((string method, string pattern) in routes)
        {
            Assert.True(LegacyApiAuditFilter.Find(method, pattern) is not null, $"{method} {pattern} is a mutating /api route without an audit line (pre-P2: add it to LegacyApiAuditFilter.Routes; P2: write the line in its handler).");
        }

        foreach (LegacyAuditRoute audited in LegacyApiAuditFilter.Routes)
        {
            Assert.Contains((audited.Method, audited.Pattern), routes);
        }
    }

    // =====================================================================================================
    // Operation-reference observer
    // =====================================================================================================

    [Fact]
    public async Task TheObserver_WritesOneLinePerClosedSystemOperation_WithTheOperationId()
    {
        using var dir = new TempDir();
        var clock = new ManualClock(Start);
        using var log = new JsonlAuditLog(dir.Path, clock);
        using var inner = new JsonAgentOperationReferenceStore(Microsoft.Extensions.Options.Options.Create(new MirbadLLMOptions { DataPath = dir.Path }), clock);
        var observer = new AuditingOperationReferenceStore(inner, log, clock);

        // An owner names the actor; a system scope names it otherwise; neither = "system".
        await observer.AddAsync(Reference("op-job", OperationKinds.Launch, "m1", AuditOwners.Job("bench-1")), Ct);
        using (AuditScope.Begin(AuditActors.SystemAutoStart))
        {
            await observer.AddAsync(Reference("op-auto", OperationKinds.Launch, "m2", owner: null), Ct);
        }

        await observer.AddAsync(Reference("op-bg", OperationKinds.FolderDelete, "folder-x", owner: null), Ct);
        Assert.Empty(await log.ReadAsync(new AuditQuery(), Ct)); // nothing before the outcome is known

        await observer.CloseAsync("op-job", AgentOperationOutcome.Succeeded, null, Ct);
        await observer.CloseAsync("op-auto", AgentOperationOutcome.Failed, ErrorCodes.DockerUnavailable, Ct);
        await observer.CloseAsync("op-bg", AgentOperationOutcome.Unknown, "hostd timed out", Ct);
        await observer.CloseAsync("op-bg", AgentOperationOutcome.Unknown, null, Ct); // already closed: no second line
        Assert.Null(await observer.CloseAsync("op-none", AgentOperationOutcome.Succeeded, null, Ct));

        List<AuditRecord> lines = [.. (await log.ReadAsync(new AuditQuery(), Ct)).Reverse()];
        Assert.Equal(3, lines.Count);
        Assert.Equal((AuditActors.SystemJob, AuditActions.ModelStart, "m1", "op-job", "dgx", AuditResults.Succeeded),
            (lines[0].Actor, lines[0].Action, lines[0].ModelId, lines[0].OperationId, lines[0].NodeId, lines[0].Result));
        Assert.Equal((AuditActors.SystemAutoStart, AuditResults.Failed, ErrorCodes.DockerUnavailable), (lines[1].Actor, lines[1].Result, lines[1].ErrorCode));
        Assert.Equal((AuditingOperationReferenceStore.UnattributedSystemActor, AuditActions.FolderDelete, "folder-x", AuditResults.Unknown, null, "hostd timed out"),
            (lines[2].Actor, lines[2].Action, lines[2].Target, lines[2].Result, lines[2].ErrorCode, lines[2].Detail));

        // After a master restart (a new observer over the same store) an open reference still gets its line, by owner.
        await observer.AddAsync(Reference("op-move", OperationKinds.Stop, "m3", AuditOwners.Move("mv-1")), Ct);
        var restarted = new AuditingOperationReferenceStore(inner, log, clock);
        await restarted.CloseAsync("op-move", AgentOperationOutcome.Succeeded, null, Ct);
        Assert.Equal((AuditActors.SystemMove, AuditActions.ModelStop), ((await log.ReadAsync(new AuditQuery(), Ct))[0].Actor, (await log.ReadAsync(new AuditQuery(), Ct))[0].Action));
    }

    [Fact]
    public async Task TheQueryEndpoint_AnswersNewestFirstWithFilters()
    {
        await using var app = await FilterApp.StartAsync();
        await app.Log.WriteAsync(Line(Start, node: "dgx", detail: "a"), Ct);
        await app.Log.WriteAsync(Line(Start.AddMinutes(1), node: "pc", detail: "b"), Ct);
        await app.Log.WriteAsync(Line(Start.AddMinutes(2), node: "dgx", detail: "c"), Ct);

        List<AuditRecord>? dgx = await app.Client.GetFromJsonAsync<List<AuditRecord>>("/api/audit?nodeId=dgx", Ct);
        Assert.Equal(["c", "a"], dgx!.Select(r => r.Detail));
        List<AuditRecord>? one = await app.Client.GetFromJsonAsync<List<AuditRecord>>("/api/audit?max=1", Ct);
        Assert.Equal(["c"], one!.Select(r => r.Detail));
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync("/api/audit?max=0", Ct)).StatusCode);
    }

    [Fact]
    public void Registration_ReplacesTheNullLog_AndDecoratesTheReferenceStore()
    {
        using var dir = new TempDir();
        var services = new ServiceCollection();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new MirbadLLMOptions { DataPath = dir.Path }));
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IAuditLog>(NullAuditLog.Instance);
        services.AddSingleton(sp => new JsonAgentOperationReferenceStore(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MirbadLLMOptions>>(), TimeProvider.System));
        services.AddSingleton<IAgentOperationReferenceStore>(sp => sp.GetRequiredService<JsonAgentOperationReferenceStore>());

        services.AddMirbadLLMAudit();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<JsonlAuditLog>(provider.GetRequiredService<IAuditLog>());
        Assert.IsType<AuditingOperationReferenceStore>(provider.GetRequiredService<IAgentOperationReferenceStore>());
        Assert.Equal(Path.Combine(dir.Path, AuditLogFiles.DirectoryName), provider.GetRequiredService<JsonlAuditLog>().Directory);
    }

    private static AuditRecord Line(DateTimeOffset time, string? node = null, string? model = null, string? detail = null) => new()
    {
        Time = time,
        Actor = AuditActors.Admin("a1b2c3d4e5f6"),
        Action = AuditActions.ModelStart,
        NodeId = node,
        ModelId = model,
        Result = AuditResults.Succeeded,
        Detail = detail,
    };

    private static AgentOperationReference Reference(string id, string kind, string target, string? owner) => new()
    {
        OperationId = id,
        NodeId = "dgx",
        NodeUid = "uid-dgx",
        Action = kind,
        Target = target,
        PayloadDigest = "digest",
        IssuedAt = Start,
        ExpiresAt = Start.AddHours(24),
        Owner = owner,
    };

    private static HttpRequestMessage Admin(HttpMethod method, string path, string? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", ".AspNetCore.Cookies=cookie-value-123");
        request.Headers.Add("X-Test-Admin", "1");
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mirbadllm-audit-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    /// <summary>A TestServer app with the filter on a group holding stub handlers at the real pre-P2 patterns.</summary>
    private sealed class FilterApp : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly TempDir _dir;

        private FilterApp(WebApplication app, TempDir dir, JsonlAuditLog log, AuditingOperationReferenceStore observer)
        {
            _app = app;
            _dir = dir;
            Log = log;
            Observer = observer;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public JsonlAuditLog Log { get; }

        public AuditingOperationReferenceStore Observer { get; }

        public static async Task<FilterApp> StartAsync()
        {
            var dir = new TempDir();
            var log = new JsonlAuditLog(System.IO.Path.Combine(dir.Path, "audit"), TimeProvider.System);
            var inner = new JsonAgentOperationReferenceStore(Microsoft.Extensions.Options.Options.Create(new MirbadLLMOptions { DataPath = dir.Path }), TimeProvider.System);
            var observer = new AuditingOperationReferenceStore(inner, log, TimeProvider.System);

            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IAuditLog>(log);
            builder.Services.AddSingleton<IAgentOperationReferenceStore>(observer);
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
            WebApplication app = builder.Build();
            app.Use(async (HttpContext http, RequestDelegate next) =>
            {
                if (http.Request.Headers.ContainsKey("X-Test-Admin"))
                {
                    http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "Cookies"));
                }

                try
                {
                    await next(http);
                }
                catch (InvalidOperationException)
                {
                    http.Response.StatusCode = StatusCodes.Status500InternalServerError;
                }
            });

            RouteGroupBuilder audited = app.MapGroup("").AddEndpointFilter(new LegacyApiAuditFilter());
            RouteGroupBuilder api = audited.MapGroup("/api");
            api.MapPost("/models/{id}/start", async (string id, IAgentOperationReferenceStore references) =>
            {
                await references.AddAsync(Reference($"op-{id}-1", OperationKinds.Launch, id, owner: null));
                return Results.Ok();
            });
            api.MapPut("/models/{id}", (string id) => Results.Problem(statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.PlacementChanged }));
            api.MapDelete("/models/{id}", IResult (string id) => throw new InvalidOperationException("boom " + id));
            api.MapPost("/services/whitelist", (JsonElement body) => Results.Ok(new { added = true }));
            api.MapPost("/services/{name}/{action}", (string name, string action) => Results.Ok());
            api.MapPost("/models", (ModelDefinition model) => Results.Created($"/api/models/{model.Id}", model));
            api.MapPost("/downloads", () => Results.Accepted());
            api.MapGet("/models/{id}", (string id) => Results.Ok());
            api.MapPost("/models/{id}/move", (string id) => Results.Ok());
            app.MapAuditApi();

            await app.StartAsync();
            return new FilterApp(app, dir, log, observer);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            Log.Dispose();
            _dir.Dispose();
        }
    }
}
