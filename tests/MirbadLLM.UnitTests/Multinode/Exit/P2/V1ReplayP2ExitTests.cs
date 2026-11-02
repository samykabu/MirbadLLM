using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.UnitTests.Multinode.Contract;
using MirbadLLM.UnitTests.Multinode.P1Exit;
using static MirbadLLM.UnitTests.Multinode.P2Exit.P2Env;

namespace MirbadLLM.UnitTests.Multinode.P2Exit;

/// <summary>
/// MN-123, design §14 P1 exit "recorded /v1 request set returns the same status codes, error bodies and Retry-After as
/// before", re-run against the P2 master: MN-009's golden recordings (<c>Multinode/V1Compat/Recordings</c>, the set
/// <c>V1ReplayExitTests</c> replays against the P1 master with FakeAgent) replayed through the real P2 composition (own CA,
/// admin password, the node-scoped switch unset = automatic) with the real DGX agent enrolled from its bootstrap bundle:
/// models registered and started through the admin API, requests through the router and the agent's proxy to the node's
/// model server. Status, <c>Content-Type</c>, <c>Retry-After</c> and the normalized body must equal the recording.
/// <para>Set-up differences (the answers are the same): the recording's maintenance window was entered directly; here a
/// real benchmark opens it (its estimate is zero, so its end is already reached and Retry-After is the 30 s floor, as
/// recorded). The recording's not-ready model was Starting next to a Ready one; the real agent's held <c>/health</c> is
/// node-wide, so here it is a model whose image pull is held (not Ready either; <c>/v1/models</c> lists Ready models only).
/// <c>upstream_connect_failure</c> is P1's documented difference (<c>V1ReplayExitTests</c>) and is not replayed here.</para>
/// </summary>
public sealed class V1ReplayP2ExitTests
{
    /// <summary>The recordings replayed (every one except the documented difference).</summary>
    public static TheoryData<string> Recordings =>
    [
        "get_models", "streaming_chat", "embeddings", "malformed_body", "unknown_model", "body_too_large", "missing_api_key",
        "invalid_api_key", "model_not_ready", "maintenance_mode",
    ];

    private const string StreamingBody = """
        data: {"choices":[{"delta":{"content":"Hello"},"finish_reason":null}]}

        data: {"choices":[{"delta":{"content":" "},"finish_reason":null}]}

        data: {"choices":[{"delta":{"content":"world"},"finish_reason":null}]}

        data: {"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":2}}

        data: [DONE]

        """;

    private const string EmbeddingsBody = """
        {
          "object": "list",
          "data": [
            {
              "object": "embedding",
              "index": 0,
              "embedding": [0.1, 0.2, 0.3, 0.4]
            }
          ],
          "model": "embed-model",
          "usage": {"prompt_tokens": 5, "total_tokens": 5}
        }
        """;

    /// <summary>The recordings' models (model id ≠ served name), on dgx; qwen is the maintenance recording's model under test.</summary>
    private static readonly ModelDefinition[] Models =
    [
        ExitEnv.Model("ready-1", "ready-model"),
        ExitEnv.Model("notready-1", "notready-model", image: AgentContractSeed.PullImage),
        ExitEnv.Model("chat-1", "chat-model"),
        ExitEnv.Model("embed-1", "embed-model"),
        ExitEnv.Model("qwen"),
    ];

    [Theory]
    [MemberData(nameof(Recordings))]
    public async Task TheRecordingReplaysIdenticallyAgainstTheP2MasterAndTheRealDgxAgent(string scenario)
    {
        var settings = new Dictionary<string, string?>();
        if (scenario is "missing_api_key" or "invalid_api_key")
        {
            settings["MirbadLLM:ApiKey"] = "required-secret";
        }

        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions
        {
            Settings = settings,
            Models = Models,
            // The maintenance recording's window ended already when it was replayed (Retry-After floor 30 s).
            Services = services => services.AddSingleton(new ModelJobOptions { DefaultBenchmarkDuration = TimeSpan.Zero }),
        });
        var golden = await Recording.LoadAsync(scenario);

        using var response = await ReplayAsync(scenario, fleet);
        var actual = await Recording.FromAsync(response);

        Assert.Equal(golden.Status, actual.Status);
        Assert.Equal(golden.ContentType, actual.ContentType);
        Assert.Equal(golden.RetryAfter, actual.RetryAfter);
        Assert.Equal(Normalize(golden.Body), Normalize(actual.Body));
    }

    /// <summary>Sets the DGX up as the recording's scenario needs and sends its request through the master.</summary>
    private static async Task<HttpResponseMessage> ReplayAsync(string scenario, P2Fleet fleet)
    {
        var master = fleet.Master;
        var http = master.Anonymous;
        var server = fleet.Dgx.Node.Model;
        switch (scenario)
        {
            case "get_models":
                await master.StartModelAsync("ready-1");
                // notready-1 stays not Ready: its image pull is held (its start answers once the pull is released).
                fleet.Dgx.Docker.SetPullsHeld(true);
                var start = master.PostAsync("/api/models/notready-1/start");
                await ExitEnv.UntilAsync(async () => (await master.Client("dgx").ListOperationsAsync(new OperationsQuery { State = OperationsQuery.ActiveState }, Ct))
                    .Any(o => o.Kind == OperationKinds.Launch && o.Target == "notready-1" && o.Stage == OperationStages.Pulling), "notready-1's launch to pull");
                Assert.False(start.IsCompleted, "notready-1's start ended while its pull was held");
                var list = await http.GetAsync("/v1/models", Ct);
                await list.Content.LoadIntoBufferAsync(Ct);
                Assert.NotEqual(ModelState.Ready, master.Cache.GetRuntime("notready-1")?.State);
                fleet.Dgx.Docker.SetPullsHeld(false);
                var (started, startBody) = await start;
                Assert.True(started == HttpStatusCode.OK, $"{(int)started} {startBody}");
                return list;

            case "streaming_chat":
                server.SetCannedResponse("/v1/chat/completions", "text/event-stream; charset=utf-8", StreamingBody);
                await master.StartModelAsync("chat-1");
                return await http.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model", stream = true }, Ct);

            case "embeddings":
                server.SetCannedResponse("/v1/embeddings", "application/json; charset=utf-8", EmbeddingsBody);
                await master.StartModelAsync("embed-1");
                return await http.PostAsJsonAsync("/v1/embeddings", new { model = "embed-model", input = "test" }, Ct);

            case "malformed_body":
                await master.StartModelAsync("chat-1");
                return await http.PostAsync("/v1/chat/completions", new StringContent("not json", Encoding.UTF8, "application/json"), Ct);

            case "unknown_model":
                return await http.PostAsJsonAsync("/v1/chat/completions", new { model = "nonexistent-model" }, Ct);

            case "body_too_large":
                await master.StartModelAsync("chat-1");
                byte[] large = new byte[(64 * 1024 * 1024) + 1];
                large.AsSpan().Fill((byte)'a');
                using (var content = new ByteArrayContent(large))
                using (var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = content })
                using (var upload = new HttpClient(new SocketsHttpHandler { UseProxy = false, Expect100ContinueTimeout = TimeSpan.FromSeconds(30) })
                {
                    BaseAddress = master.HttpAddress,
                    Timeout = TimeSpan.FromSeconds(60),
                })
                {
                    // "Expect: 100-continue": the refusal from Content-Length comes before the 64 MiB are on the wire (the
                    // server closes a connection whose body it did not read).
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    request.Headers.ExpectContinue = true;
                    return await upload.SendAsync(request, Ct);
                }

            case "missing_api_key":
                await master.StartModelAsync("chat-1");
                return await http.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, Ct);

            case "invalid_api_key":
                await master.StartModelAsync("chat-1");
                using (var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = JsonContent.Create(new { model = "chat-model" }) })
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-secret");
                    return await http.SendAsync(request, Ct);
                }

            case "model_not_ready":
                fleet.Dgx.Node.StartsHeld = true; // /health 503: the instance stays Starting
                await master.StartModelAsync("chat-1", waitReady: false);
                await master.WaitForModelAsync("chat-1", ModelState.Starting);
                return await http.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, Ct);

            case "maintenance_mode":
                await master.StartModelAsync("chat-1");
                await master.StartModelAsync("qwen");
                return await DuringABenchmarkOnQwenAsync(fleet, () => http.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, Ct));

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "No replay set-up.");
        }
    }

    /// <summary>
    /// Sends <paramref name="send"/> while a real benchmark on qwen runs (its first token is held back, so the job runs until
    /// it is cancelled): the single DGX opens a global window (automatic mode, one fleet member), as P1 did.
    /// </summary>
    private static async Task<HttpResponseMessage> DuringABenchmarkOnQwenAsync(P2Fleet fleet, Func<Task<HttpResponseMessage>> send)
    {
        var master = fleet.Master;
        var jobs = master.Get<ModelJobService>();
        var maintenance = master.Get<MaintenanceModeService>();
        fleet.Dgx.Node.Model.SetTimeToFirstTokenMs(600_000);
        try
        {
            await master.ExpectAsync("/api/models/qwen/benchmark", null, HttpStatusCode.Accepted);
            var window = Assert.Single(maintenance.Active);
            Assert.True(window.IsGlobal, $"the single DGX's window names node {window.NodeId}");
            var response = await send();
            await response.Content.LoadIntoBufferAsync(Ct);
            Assert.Equal(window.JobId, Assert.Single(maintenance.Active).JobId); // still the same job's window
            return response;
        }
        finally
        {
            fleet.Dgx.Node.Model.SetTimeToFirstTokenMs(50);
            jobs.CancelOnNode("dgx", JobKind.Benchmark);
            await jobs.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(60), Ct);
        }
    }

    /// <summary>MN-009's normalization: line endings (checkout-dependent) and the wall-clock time of the maintenance text.</summary>
    private static string Normalize(string body) =>
        Regex.Replace(body.ReplaceLineEndings("\n"), @"\d{1,2}:\d{2}( UTC)", "HH:MM$1");

    /// <summary>One recording (MN-009's file shape: scenario, method, path, status, contentType, retryAfter, body).</summary>
    private sealed record Recording(int Status, string? ContentType, string? RetryAfter, string Body)
    {
        public static async Task<Recording> LoadAsync(string scenario)
        {
            string path = Path.Combine(RecordingsDirectory(), scenario + ".json");
            Assert.True(File.Exists(path), $"Golden recording missing: {path}");
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, Ct));
            var root = doc.RootElement;
            return new Recording(
                root.GetProperty("status").GetInt32(),
                root.GetProperty("contentType").GetString(),
                root.GetProperty("retryAfter").GetString(),
                root.GetProperty("body").GetString() ?? "");
        }

        public static async Task<Recording> FromAsync(HttpResponseMessage response) => new(
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.ToString(),
            response.Headers.RetryAfter?.ToString(),
            await response.Content.ReadAsStringAsync(Ct));

        private static string RecordingsDirectory()
        {
            foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "MirbadLLM.slnx")))
                    {
                        return Path.Combine(dir.FullName, "tests", "MirbadLLM.UnitTests", "Multinode", "V1Compat", "Recordings");
                    }
                }
            }

            throw new DirectoryNotFoundException("MirbadLLM.slnx not found above the test directory.");
        }
    }
}
