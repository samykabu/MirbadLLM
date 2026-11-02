using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.UnitTests.Multinode.P1Exit;
using static MirbadLLM.UnitTests.Multinode.P2Exit.P2Env;

namespace MirbadLLM.UnitTests.Multinode.P2Exit;

/// <summary>
/// MN-123, the <c>/v1</c> precedence matrix end to end (design §6.5 item 2, §13 "router error precedence matrix", §14 P2
/// "precedence matrix for P2 order"; MN-126 D83/D84, MN-130 D101): the real master and real agents, an inference API key
/// from the key store, a real benchmark for the maintenance window (its slow first token keeps the job running), a node's
/// <c>/health</c> held for "not ready" (<see cref="MirbadLLM.UnitTests.Agent.ContractNode.StartsHeld"/>) and a stopped agent
/// for "node offline". Every request answers the first condition of its set in the mode's order:
/// <list type="bullet">
/// <item>P2 order (a node-scoped window): 401 → 413 → 400 <c>model_required</c> → 404 → 503 <c>maintenance</c> (only models
/// on the job's node) → 503 <c>node_offline</c> → 503 <c>model_not_ready</c> (401 before 413, D84). A two-node fleet with
/// <c>MirbadLLM:Router:Maintenance:NodeScoped</c> unset (automatic, D101) or <c>true</c>.</item>
/// <item>P1 order (a global window): 503 <c>maintenance</c> before everything, then the same order. A two-node fleet with the
/// switch <c>false</c> (D83) and the single DGX with it unset (automatic, one fleet member).</item>
/// </list>
/// <para>Not combinable end to end, and covered by the unit matrix (<c>Router/PrecedenceMatrixTests</c>): maintenance with an
/// offline or not-ready model on the job's node. The job's own load runs through that node (an offline node fails the job)
/// and the held <c>/health</c> is node-wide (it would turn the model under test not Ready). Both orders put maintenance
/// before those two, so no row of either order depends on the combination.</para>
/// </summary>
public sealed class PrecedenceMatrixExitTests
{
    /// <summary>The fleet and switch of one run.</summary>
    public enum Mode
    {
        /// <summary>DGX + PC, switch unset: automatic, node-scoped windows (two fleet members), P2 order.</summary>
        TwoNodesAutomatic,

        /// <summary>DGX + PC, switch true: node-scoped windows, P2 order.</summary>
        TwoNodesOn,

        /// <summary>DGX + PC, switch false: global windows, P1 order.</summary>
        TwoNodesOff,

        /// <summary>The single DGX, switch unset: automatic with one member, global windows, P1 order.</summary>
        SingleDgx,
    }

    private enum Condition
    {
        Maintenance,
        NoKey,
        TooLarge,
        NoModel,
        Unknown,
        NodeOffline,
        NotReady,
    }

    private enum Body
    {
        Valid,
        TooLarge,
        NoModel,
        Unknown,
    }

    private static readonly Condition[] P1Order =
        [Condition.Maintenance, Condition.NoKey, Condition.TooLarge, Condition.NoModel, Condition.Unknown, Condition.NodeOffline, Condition.NotReady];

    private static readonly Condition[] P2Order =
        [Condition.NoKey, Condition.TooLarge, Condition.NoModel, Condition.Unknown, Condition.Maintenance, Condition.NodeOffline, Condition.NotReady];

    private static readonly Dictionary<Condition, (HttpStatusCode Status, string Code)> Answers = new()
    {
        [Condition.Maintenance] = (HttpStatusCode.ServiceUnavailable, "maintenance"),
        [Condition.NoKey] = (HttpStatusCode.Unauthorized, "invalid_api_key"),
        [Condition.TooLarge] = (HttpStatusCode.RequestEntityTooLarge, "request_too_large"),
        [Condition.NoModel] = (HttpStatusCode.BadRequest, "model_required"),
        [Condition.Unknown] = (HttpStatusCode.NotFound, "model_not_found"),
        [Condition.NodeOffline] = (HttpStatusCode.ServiceUnavailable, "node_offline"),
        [Condition.NotReady] = (HttpStatusCode.ServiceUnavailable, "model_not_ready"),
    };

    [Theory]
    [InlineData(Mode.TwoNodesAutomatic)]
    [InlineData(Mode.TwoNodesOn)]
    [InlineData(Mode.TwoNodesOff)]
    [InlineData(Mode.SingleDgx)]
    public async Task EveryRequestAnswersTheFirstConditionOfItsModesOrder(Mode mode)
    {
        bool twoNodes = mode != Mode.SingleDgx;
        string jobNode = twoNodes ? "pc" : "dgx";
        var settings = new Dictionary<string, string?>();
        if (mode == Mode.TwoNodesOn)
        {
            settings[NodeScopedMaintenancePolicy.Key] = "true";
        }
        else if (mode == Mode.TwoNodesOff)
        {
            settings[NodeScopedMaintenancePolicy.Key] = "false";
        }

        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions
        {
            Settings = settings,
            Models = [Model("serving", "dgx")],
        });
        if (twoNodes)
        {
            await fleet.JoinAsync("pc", PcGpus);
        }

        var master = fleet.Master;
        // bench: the model under test; paused: another model on the job's node (not exempt); serving: on dgx.
        await master.CreateModelAsync(ModelBody("bench", jobNode, twoNodes ? [PcGpus[0]] : null));
        await master.CreateModelAsync(ModelBody("paused", jobNode, twoNodes ? [PcGpus[1]] : null));
        foreach (string id in new[] { "serving", "bench", "paused" })
        {
            await master.StartModelAsync(id);
        }

        string key = (await master.Get<IApiKeyStore>().CreateAsync("matrix", ApiKeyScope.Inference, Ct)).FullKey;
        var matrix = new Matrix(master, key, new Dictionary<string, string>
        {
            ["serving"] = "dgx",
            ["bench"] = jobNode,
            ["paused"] = jobNode,
        });

        // 1. Nothing paused, held or offline: every valid request is served.
        await matrix.RunAsync("idle", window: null, notReady: [], offline: []);
        await matrix.ModelsListAsync("idle", window: null, listed: ["serving", "bench", "paused"]);

        // 2. A real benchmark on bench: its window is node-scoped (P2) or global (P1) as the mode decides.
        var maintenance = master.Get<MaintenanceModeService>();
        var jobs = master.Get<ModelJobService>();
        fleet[jobNode].Node.Model.SetTimeToFirstTokenMs(600_000);
        try
        {
            await master.ExpectAsync("/api/models/bench/benchmark", null, HttpStatusCode.Accepted);
            var window = Assert.Single(maintenance.Active);
            Assert.Equal("bench", window.ModelId);
            Assert.Equal(mode is Mode.TwoNodesAutomatic or Mode.TwoNodesOn ? jobNode : null, window.NodeId);

            await matrix.RunAsync("window", window, notReady: [], offline: []);
            await matrix.ModelsListAsync("window", window, listed: window.IsGlobal ? [] : ["serving"]);

            if (twoNodes)
            {
                // 3. dgx's /health held: serving reads Starting while the PC's job keeps its window.
                await matrix.RunAsync("window+held", window, notReady: await HoldDgxAsync(fleet, matrix), offline: []);
            }

            // The window was open for every row above (a job that ended early would make the rows prove nothing).
            Assert.Equal(window.JobId, Assert.Single(maintenance.Active).JobId);
            Assert.NotNull(jobs.JobOnNode(jobNode));
        }
        finally
        {
            fleet[jobNode].Node.Model.SetTimeToFirstTokenMs(50);
            jobs.CancelOnNode(jobNode, JobKind.Benchmark);
            await jobs.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(60), Ct);
        }

        Assert.Empty(maintenance.Active);

        // 4. No window, serving not ready (/health held on dgx).
        var held = await HoldDgxAsync(fleet, matrix);
        await matrix.RunAsync("held", window: null, notReady: held, offline: []);

        // 5. dgx's agent stopped: dgx reads Offline (serving still not ready in the cache: offline comes first).
        await fleet.Dgx.StopAsync();
        await master.WaitForNodeAsync("dgx", n => n.State == NodeState.Offline, "dgx Offline");
        await matrix.RunAsync("offline", window: null, notReady: held, offline: ["dgx"]);

        Assert.True(matrix.Failures.Count == 0, $"{mode}, {matrix.Rows} rows:\n" + string.Join("\n", matrix.Failures));
        // 12 rows a phase (2 keys × 3 models + 2 keys × 3 body errors), 5 phases with two nodes, 4 on the single DGX; 4 lists.
        Assert.Equal(twoNodes ? 64 : 52, matrix.Rows);
    }

    /// <summary>Holds dgx's <c>/health</c> (503) and waits until each of its models reads Starting; returns them.</summary>
    private static async Task<IReadOnlyCollection<string>> HoldDgxAsync(P2Fleet fleet, Matrix matrix)
    {
        fleet.Dgx.Node.StartsHeld = true;
        var onDgx = matrix.ModelsOn("dgx");
        foreach (string id in onDgx)
        {
            await fleet.Master.WaitForModelAsync(id, ModelState.Starting);
        }

        return onDgx;
    }

    /// <summary>The request rows of one fleet: every key × body × target, judged against the order of the current window.</summary>
    private sealed class Matrix(P2Master master, string key, IReadOnlyDictionary<string, string> nodeOf)
    {
        public List<string> Failures { get; } = [];

        public int Rows { get; private set; }

        public IReadOnlyCollection<string> ModelsOn(string nodeId) => [.. nodeOf.Where(p => p.Value == nodeId).Select(p => p.Key)];

        /// <summary>
        /// Every row with the window open (<paramref name="window"/>) or not, the models not ready and the nodes offline.
        /// Body errors are sent naming "paused" where a model is named, so in P2 they are shown to precede its maintenance.
        /// </summary>
        public async Task RunAsync(string phase, MaintenanceState? window, IReadOnlyCollection<string> notReady, IReadOnlyCollection<string> offline)
        {
            var order = window is { IsGlobal: false } ? P2Order : P1Order;
            foreach (bool withKey in new[] { true, false })
            {
                foreach (Body body in Enum.GetValues<Body>())
                {
                    foreach (string target in body == Body.Valid ? nodeOf.Keys : ["paused"])
                    {
                        var set = new HashSet<Condition>();
                        if (!withKey)
                        {
                            set.Add(Condition.NoKey);
                        }

                        switch (body)
                        {
                            case Body.TooLarge:
                                set.Add(Condition.TooLarge);
                                break;
                            case Body.NoModel:
                                set.Add(Condition.NoModel);
                                break;
                            case Body.Unknown:
                                set.Add(Condition.Unknown);
                                break;
                        }

                        bool named = body is Body.Valid or Body.TooLarge;
                        if (window is { IsGlobal: true } || (window is { } w && named && w.NodeId == nodeOf[target]))
                        {
                            set.Add(Condition.Maintenance);
                        }

                        if (named && offline.Contains(nodeOf[target]))
                        {
                            set.Add(Condition.NodeOffline);
                        }

                        if (named && notReady.Contains(target))
                        {
                            set.Add(Condition.NotReady);
                        }

                        await RowAsync(phase, window, order, set, withKey, body, target);
                    }
                }
            }
        }

        /// <summary><c>GET /v1/models</c>: 503 before the key in a global window; else 401 without a key, the serving models with one.</summary>
        public async Task ModelsListAsync(string phase, MaintenanceState? window, IReadOnlyCollection<string> listed)
        {
            foreach (bool withKey in new[] { true, false })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
                if (withKey)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                }

                using var response = await master.Anonymous.SendAsync(request, Ct);
                string text = await response.Content.ReadAsStringAsync(Ct);
                Rows++;
                var (status, code) = window is { IsGlobal: true } ? Answers[Condition.Maintenance]
                    : withKey ? (HttpStatusCode.OK, "") : Answers[Condition.NoKey];
                if (response.StatusCode != status || ErrorCode(text) != code)
                {
                    Failures.Add($"{phase} GET /v1/models key={withKey}: expected {(int)status} {code}, got {(int)response.StatusCode} {text}");
                    continue;
                }

                if (status == HttpStatusCode.OK)
                {
                    using var json = JsonDocument.Parse(text);
                    var ids = json.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()).Order(StringComparer.Ordinal);
                    if (!ids.SequenceEqual(listed.Order(StringComparer.Ordinal)))
                    {
                        Failures.Add($"{phase} GET /v1/models: expected [{string.Join(",", listed)}], got {text}");
                    }
                }
            }
        }

        private async Task RowAsync(string phase, MaintenanceState? window, Condition[] order, HashSet<Condition> set, bool withKey, Body body, string target)
        {
            Rows++;
            var first = order.FirstOrDefault(set.Contains, (Condition)(-1));
            var (status, code) = Enum.IsDefined(first) ? Answers[first] : (HttpStatusCode.OK, "");
            string row = $"{phase} key={withKey} body={body} model={target} [{string.Join(",", set)}]";
            HttpResponseMessage response;
            try
            {
                using var request = Request(withKey, body, target);
                if (body == Body.TooLarge)
                {
                    // As a well-behaved client uploads: "Expect: 100-continue", so a refusal before the body is read (401,
                    // 413 from Content-Length, 503) is answered without the 64 MiB on the wire; on its own connection, as
                    // the server closes one whose body it did not read.
                    request.Headers.ExpectContinue = true;
                    using var upload = new HttpClient(new SocketsHttpHandler { UseProxy = false, Expect100ContinueTimeout = TimeSpan.FromSeconds(30) })
                    {
                        BaseAddress = master.HttpAddress,
                        Timeout = TimeSpan.FromSeconds(60),
                    };
                    response = await upload.SendAsync(request, Ct);
                }
                else
                {
                    response = await master.Anonymous.SendAsync(request, Ct);
                }
            }
            catch (HttpRequestException ex)
            {
                Failures.Add($"{row}: expected {(int)status} {code}, the request failed: {ex.Message}");
                return;
            }

            using (response)
            {
                string text = await response.Content.ReadAsStringAsync(Ct);
                string actual = status == HttpStatusCode.OK ? "" : ErrorCode(text);
                if (response.StatusCode != status || actual != code)
                {
                    Failures.Add($"{row}: expected {(int)status} {code}, got {(int)response.StatusCode} {text}");
                    return;
                }

                if (first == Condition.Maintenance)
                {
                    // The text names the node only for a node-scoped window (P1: exactly today's text); a Retry-After always.
                    string expected = window!.IsGlobal ? "running a benchmark on bench; API paused until ~" : $"running a benchmark on bench on node {window.NodeId}; API paused until ~";
                    if (!text.Contains(expected, StringComparison.Ordinal) || response.Headers.RetryAfter is null)
                    {
                        Failures.Add($"{row}: maintenance without \"{expected}\" or Retry-After: {text}");
                    }
                }

                if (first == Condition.NodeOffline && !text.Contains("is offline", StringComparison.Ordinal))
                {
                    Failures.Add($"{row}: node_offline without its text: {text}");
                }
            }
        }

        private HttpRequestMessage Request(bool withKey, Body body, string target)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
            if (withKey)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }

            request.Content = body switch
            {
                Body.TooLarge => TooLargeContent(target),
                Body.NoModel => new StringContent("""{"prompt":"x"}""", Encoding.UTF8, "application/json"),
                Body.Unknown => JsonContent.Create(new { model = "nobody", messages = new[] { new { role = "user", content = "hi" } } }),
                _ => JsonContent.Create(new { model = target, messages = new[] { new { role = "user", content = "hi" } }, max_tokens = 4 }),
            };
            return request;
        }

        /// <summary>A real body one byte over the 64 MiB limit, a valid JSON object naming <paramref name="target"/>.</summary>
        private static ByteArrayContent TooLargeContent(string target)
        {
            byte[] head = Encoding.UTF8.GetBytes($$"""{"model":"{{target}}","pad":" """);
            byte[] tail = "\"}"u8.ToArray();
            byte[] body = new byte[Protocol.MaxProxyRequestBodyBytes + 1];
            body.AsSpan().Fill((byte)'a');
            head.CopyTo(body, 0);
            tail.CopyTo(body, body.Length - tail.Length);
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return content;
        }

        private static string ErrorCode(string body)
        {
            try
            {
                using var json = JsonDocument.Parse(body);
                return json.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var code) ? code.GetString() ?? "" : "";
            }
            catch (JsonException)
            {
                return "";
            }
        }
    }
}
