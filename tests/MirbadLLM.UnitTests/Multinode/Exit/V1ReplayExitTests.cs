using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.UnitTests.Multinode.Fakes;
using static MirbadLLM.UnitTests.Multinode.P1Exit.ExitEnv;

namespace MirbadLLM.UnitTests.Multinode.P1Exit;

/// <summary>
/// Design §14 P1 exit "recorded /v1 request set returns the same status codes, error bodies and Retry-After as before" and
/// plan §3.8 item 4: MN-009's golden recordings (<c>Multinode/V1Compat/Recordings</c>, made against today's router) are
/// replayed against the real P1 master composition with FakeAgent as <c>dgx</c>: models are registered and started through
/// the admin API, requests go through the master's router and the agent's proxy over mTLS to the node's model server
/// (answering with the same upstream bodies the recordings used). Each replay compares status, <c>Content-Type</c>,
/// <c>Retry-After</c> and the body (normalized as MN-009 does: line endings, the wall-clock "HH:MM" of maintenance).
/// <para><b>Documented differences</b> (the only ones accepted):</para>
/// <list type="bullet">
/// <item><c>upstream_connect_failure</c>: today the router's upstream was the model, and a refused connection was 502
/// <c>upstream_unavailable</c>. In P1 the router's upstream is the agent: a connection to it that fails now is 503
/// <c>node_unreachable</c> with <c>Retry-After: 10</c> (design §7.4 table, D54: the message carries no transport text). The
/// replay asserts that row; 502 <c>upstream_unavailable</c> remains for an agent that answered an unexpected error.</item>
/// </list>
/// </summary>
public sealed class V1ReplayExitTests
{
    /// <summary>Every recording except the documented difference, replayed byte-for-byte (after normalization).</summary>
    public static TheoryData<string> Identical =>
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

    /// <summary>The recordings' models as P1 definitions (model id ≠ served name, as the recordings' resolver had it).</summary>
    private static readonly ModelDefinition[] Models =
    [
        Model("ready-1", "ready-model"), Model("notready-1", "notready-model"), Model("chat-1", "chat-model"), Model("embed-1", "embed-model"),
    ];

    [Theory]
    [MemberData(nameof(Identical))]
    public async Task TheRecordingReplaysIdenticallyAgainstTheP1Master(string scenario)
    {
        var settings = scenario is "missing_api_key" or "invalid_api_key"
            ? new Dictionary<string, string?> { ["MirbadLLM:ApiKey"] = "required-secret" }
            : null;
        await using var node = await FakeNode.StartAsync(models: Models);
        await using var master = await node.Master(settings: settings).StartAsync();
        var golden = await Recording.LoadAsync(scenario);

        using var response = await ReplayAsync(scenario, node, master);
        var actual = await Recording.FromAsync(response);

        Assert.Equal(golden.Status, actual.Status);
        Assert.Equal(golden.ContentType, actual.ContentType);
        Assert.Equal(golden.RetryAfter, actual.RetryAfter);
        Assert.Equal(Normalize(golden.Body), Normalize(actual.Body));
    }

    [Fact]
    public async Task UpstreamConnectFailureIsTheDocumentedNodeUnreachableRow()
    {
        await using var node = await FakeNode.StartAsync(models: Models);
        await using var master = await node.Master().StartAsync();
        await StartReadyAsync(master, "chat-1");
        var golden = await Recording.LoadAsync("upstream_connect_failure");

        // The connection to the agent fails now (the master still believes the node Online).
        node.Agent.SetPartitioned(true);
        using var response = await master.Http.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, Ct);
        var actual = await Recording.FromAsync(response);

        // Same shape and content type as the golden 502; status, code and Retry-After are §7.4's node_unreachable row.
        Assert.Equal(502, golden.Status);
        Assert.Equal(golden.ContentType, actual.ContentType);
        Assert.Equal(503, actual.Status);
        Assert.Equal("10", actual.RetryAfter);
        using var body = JsonDocument.Parse(actual.Body);
        var error = body.RootElement.GetProperty("error");
        Assert.Equal("node_unreachable", error.GetProperty("code").GetString());
        Assert.Equal("service_unavailable_error", error.GetProperty("type").GetString());
        Assert.DoesNotContain("refused", error.GetProperty("message").GetString() ?? "", StringComparison.OrdinalIgnoreCase); // D54
        node.Agent.SetPartitioned(false);
    }

    /// <summary>Sets the node up as the recording's scenario needs and sends its request through the master.</summary>
    private static async Task<HttpResponseMessage> ReplayAsync(string scenario, FakeNode node, ExitMaster master)
    {
        var server = await node.Agent.GetModelServerAsync();
        switch (scenario)
        {
            case "get_models":
                await StartReadyAsync(master, "ready-1");
                await StartStartingAsync(node, master, "notready-1");
                return await master.Http.GetAsync("/v1/models", Ct);

            case "streaming_chat":
                server.SetCannedResponse("/v1/chat/completions", "text/event-stream; charset=utf-8", StreamingBody);
                await StartReadyAsync(master, "chat-1");
                return await master.Http.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model", stream = true }, Ct);

            case "embeddings":
                server.SetCannedResponse("/v1/embeddings", "application/json; charset=utf-8", EmbeddingsBody);
                await StartReadyAsync(master, "embed-1");
                return await master.Http.PostAsJsonAsync("/v1/embeddings", new { model = "embed-model", input = "test" }, Ct);

            case "malformed_body":
                await StartReadyAsync(master, "chat-1");
                return await master.Http.PostAsync("/v1/chat/completions", new StringContent("not json", Encoding.UTF8, "application/json"), Ct);

            case "unknown_model":
                return await master.Http.PostAsJsonAsync("/v1/chat/completions", new { model = "nonexistent-model" }, Ct);

            case "body_too_large":
                await StartReadyAsync(master, "chat-1");
                byte[] large = new byte[(64 * 1024 * 1024) + 1];
                large.AsSpan().Fill((byte)'a');
                using (var content = new ByteArrayContent(large))
                {
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    return await master.Http.PostAsync("/v1/chat/completions", content, Ct);
                }

            case "missing_api_key":
                await StartReadyAsync(master, "chat-1");
                return await master.Http.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, Ct);

            case "invalid_api_key":
                await StartReadyAsync(master, "chat-1");
                using (var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = JsonContent.Create(new { model = "chat-model" }) })
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-secret");
                    return await master.Http.SendAsync(request, Ct);
                }

            case "model_not_ready":
                await StartStartingAsync(node, master, "chat-1");
                return await master.Http.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, Ct);

            case "maintenance_mode":
                await StartReadyAsync(master, "chat-1");
                // As MN-009 recorded it: a benchmark whose estimated end is already reached (Retry-After floor 30 s).
                master.Get<MaintenanceModeService>().Enter(new MaintenanceState(
                    "job-1", "benchmark", "qwen", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(-1)));
                return await master.Http.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, Ct);

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "No replay set-up.");
        }
    }

    /// <summary>Starts the model through the admin API and waits until the router can serve it.</summary>
    private static async Task StartReadyAsync(ExitMaster master, string modelId)
    {
        var (status, body) = await SendAsync(master.Http.PostAsync($"/api/models/{modelId}/start", content: null, Ct));
        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        await master.WaitForModelAsync(modelId, ModelState.Ready);
    }

    /// <summary>Starts the model through the admin API with the node's starts held: it stays Starting.</summary>
    private static async Task StartStartingAsync(FakeNode node, ExitMaster master, string modelId)
    {
        await node.Agent.SetStartsHeldAsync(true);
        var (status, body) = await SendAsync(master.Http.PostAsync($"/api/models/{modelId}/start", content: null, Ct));
        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        await master.WaitForModelAsync(modelId, ModelState.Starting);
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
