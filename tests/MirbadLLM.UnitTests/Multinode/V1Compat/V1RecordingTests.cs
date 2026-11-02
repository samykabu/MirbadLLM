using System;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Router;
using MirbadLLM.UnitTests.Router.Fakes;

#pragma warning disable CA1707 // Allow underscore test names

namespace MirbadLLM.UnitTests.Multinode.V1Compat;

/// <summary>
/// Golden recordings of /v1 API behaviour to prove client-facing changes don't occur.
/// MN-043 will replay these against the P1 master to validate client compatibility.
/// </summary>
public sealed class V1RecordingTests
{
    private static string GetRecordingsDirectory()
    {
        // Find the project root by looking for the .slnx file
        var currentDir = System.IO.Directory.GetCurrentDirectory();
        var projectRoot = currentDir;

        // Walk up until we find MirbadLLM.slnx
        while (!System.IO.File.Exists(System.IO.Path.Combine(projectRoot, "MirbadLLM.slnx")))
        {
            var parent = System.IO.Path.GetDirectoryName(projectRoot);
            if (string.IsNullOrEmpty(parent) || parent == projectRoot) break; // reached root
            projectRoot = parent ?? projectRoot;
        }

        var recordingsPath = System.IO.Path.Combine(projectRoot, "tests", "MirbadLLM.UnitTests", "Multinode", "V1Compat", "Recordings");
        return recordingsPath;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Set to 1 to (re)write the golden files from today's code; otherwise every test compares against them.</summary>
    private const string RecordVariable = "MIRBADLLM_RECORD_GOLDEN";

    /// <summary>
    /// Wall-clock parts of a response (e.g. "until ~20:20 UTC") vary per run, and the fake upstream's line endings follow
    /// this file's checkout (CRLF on Windows, LF on Linux CI); normalize both before comparing.
    /// </summary>
    private static string Normalize(string body) =>
        System.Text.RegularExpressions.Regex.Replace(body.ReplaceLineEndings("\n"), @"\d{1,2}:\d{2}( UTC)", "HH:MM$1");

    /// <summary>
    /// Golden-file check: the recording must equal the committed file (after <see cref="Normalize"/>). The files are only
    /// rewritten when <see cref="RecordVariable"/> is 1, so a behaviour change fails the test instead of silently re-recording.
    /// </summary>
    private static async Task AssertMatchesGoldenAsync(V1Recording actual, string filename)
    {
        actual = actual with { Body = Normalize(actual.Body) };
        if (Environment.GetEnvironmentVariable(RecordVariable) == "1")
        {
            await actual.WriteAsync(filename);
            return;
        }

        Assert.True(File.Exists(filename), $"Golden file missing: {filename}. Record it with {RecordVariable}=1.");
        var expected = await V1Recording.LoadAsync(filename);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.ContentType, actual.ContentType);
        Assert.Equal(expected.RetryAfter, actual.RetryAfter);
        Assert.Equal(Normalize(expected.Body), actual.Body);
    }

    /// <summary>Recording of a single API response for replay validation.</summary>
    private sealed record V1Recording
    {
        [JsonPropertyName("scenario")]
        public string Scenario { get; init; } = "";

        [JsonPropertyName("method")]
        public string Method { get; init; } = "GET";

        [JsonPropertyName("path")]
        public string Path { get; init; } = "";

        [JsonPropertyName("status")]
        public int Status { get; init; }

        [JsonPropertyName("contentType")]
        public string? ContentType { get; init; }

        [JsonPropertyName("retryAfter")]
        public string? RetryAfter { get; init; }

        [JsonPropertyName("body")]
        public string Body { get; init; } = "";

        /// <summary>Write this recording to a JSON file.</summary>
        public async Task WriteAsync(string filename)
        {
            var directory = System.IO.Path.GetDirectoryName(filename);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(this, V1RecordingTests.JsonOptions);
            await System.IO.File.WriteAllTextAsync(filename, json);
        }

        /// <summary>Load a recording from a JSON file.</summary>
        public static async Task<V1Recording> LoadAsync(string filename)
        {
            var json = await File.ReadAllTextAsync(filename);
            return JsonSerializer.Deserialize<V1Recording>(json)
                ?? throw new InvalidOperationException($"Failed to deserialize recording from {filename}");
        }

        /// <summary>Extract a recording from an HTTP response.</summary>
        public static async Task<V1Recording> FromResponseAsync(
            string scenario,
            string method,
            string path,
            HttpResponseMessage response)
        {
            var body = await response.Content.ReadAsStringAsync();
            var contentType = response.Content.Headers.ContentType?.ToString();
            var retryAfter = response.Headers.RetryAfter?.ToString();

            return new V1Recording
            {
                Scenario = scenario,
                Method = method,
                Path = path,
                Status = (int)response.StatusCode,
                ContentType = contentType,
                RetryAfter = retryAfter,
                Body = body,
            };
        }
    }

    [Fact]
    public async Task Record_GetModels_WithReadyAndNotReadyModels()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("ready-1", "ready-model", new Uri("http://upstream/"), ModelState.Ready));
        resolver.Set(new ModelEndpoint("notready-1", "notready-model", new Uri("http://upstream/"), ModelState.Starting));

        // The registry behind the list (as in the real master): max model len and capabilities come from it.
        var registry = new MirbadLLM.UnitTests.Web.Fakes.FakeModelRegistry([
            new ModelDefinition { Id = "ready-1", ServedName = "ready-model", ModelPath = "ready", MaxModelLen = 8192 },
            new ModelDefinition { Id = "notready-1", ServedName = "notready-model", ModelPath = "notready", MaxModelLen = 8192 },
        ]);
        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime(),
            configureServices: services => services.AddSingleton<IModelRegistry>(registry));
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/v1/models", ct);
        var recording = await V1Recording.FromResponseAsync("get_models", "GET", "/v1/models", response);
        var filename = Path.Combine(GetRecordingsDirectory(), "get_models.json");
        await AssertMatchesGoldenAsync(recording, filename);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ready-model", recording.Body);
    }

    [Fact]
    public async Task Record_StreamingChat_WithMultipleEvents()
    {
        var ct = TestContext.Current.CancellationToken;
        var sseBody = """
            data: {"choices":[{"delta":{"content":"Hello"},"finish_reason":null}]}

            data: {"choices":[{"delta":{"content":" "},"finish_reason":null}]}

            data: {"choices":[{"delta":{"content":"world"},"finish_reason":null}]}

            data: {"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":2}}

            data: [DONE]

            """;

        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));

        var handler = new FakeUpstreamHandler
        {
            Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream"),
            }),
        };

        using var app = await RouterHostFactory.CreateAsync(
            resolver,
            new FakeModelRuntime(),
            configureServices: services => RouterProxyTests.ConfigureUpstream(services, handler));
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            "/v1/chat/completions",
            new { model = "chat-model", stream = true },
            ct);

        var recording = await V1Recording.FromResponseAsync(
            "streaming_chat",
            "POST",
            "/v1/chat/completions",
            response);
        var filename = Path.Combine(GetRecordingsDirectory(), "streaming_chat.json");
        await AssertMatchesGoldenAsync(recording, filename);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Hello", recording.Body);
    }

    [Fact]
    public async Task Record_Embeddings_WithVectorOutput()
    {
        var ct = TestContext.Current.CancellationToken;
        var embeddingsBody = """
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

        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("embed-1", "embed-model", new Uri("http://upstream/"), ModelState.Ready));

        var handler = new FakeUpstreamHandler
        {
            Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(embeddingsBody, Encoding.UTF8, "application/json"),
            }),
        };

        using var app = await RouterHostFactory.CreateAsync(
            resolver,
            new FakeModelRuntime(),
            configureServices: services => RouterProxyTests.ConfigureUpstream(services, handler));
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            "/v1/embeddings",
            new { model = "embed-model", input = "test" },
            ct);

        var recording = await V1Recording.FromResponseAsync(
            "embeddings",
            "POST",
            "/v1/embeddings",
            response);
        var filename = Path.Combine(GetRecordingsDirectory(), "embeddings.json");
        await AssertMatchesGoldenAsync(recording, filename);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("embedding", recording.Body);
    }

    [Fact]
    public async Task Record_MalformedBody_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));

        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime());
        using var client = app.GetTestClient();

        using var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent("not json", Encoding.UTF8, "application/json"),
            ct);

        var recording = await V1Recording.FromResponseAsync(
            "malformed_body",
            "POST",
            "/v1/chat/completions",
            response);
        var filename = Path.Combine(GetRecordingsDirectory(), "malformed_body.json");
        await AssertMatchesGoldenAsync(recording, filename);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Record_UnknownModel_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();

        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime());
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            "/v1/chat/completions",
            new { model = "nonexistent-model" },
            ct);

        var recording = await V1Recording.FromResponseAsync(
            "unknown_model",
            "POST",
            "/v1/chat/completions",
            response);
        var filename = Path.Combine(GetRecordingsDirectory(), "unknown_model.json");
        await AssertMatchesGoldenAsync(recording, filename);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Record_BodyTooLarge_Returns413()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));

        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime());
        using var client = app.GetTestClient();

        // Just over the router's 64 MiB request limit (ProxyHandler.MaxBodyBytes): rejected with 413 before any upstream call,
        // so the recording does not depend on DNS or the network.
        // MN-090: one byte over the limit in a single buffer (the old 65 MiB string + copies peaked near 400 MB).
        byte[] largeBody = new byte[64 * 1024 * 1024 + 1];
        largeBody.AsSpan().Fill((byte)'a');
        using var content = new ByteArrayContent(largeBody);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await client.PostAsync("/v1/chat/completions", content, ct);

        var recording = await V1Recording.FromResponseAsync(
            "body_too_large",
            "POST",
            "/v1/chat/completions",
            response);
        var filename = Path.Combine(GetRecordingsDirectory(), "body_too_large.json");
        await AssertMatchesGoldenAsync(recording, filename);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Record_MissingApiKey_Returns401()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));

        using var app = await RouterHostFactory.CreateAsync(
            resolver,
            new FakeModelRuntime(),
            configureOptions: o => o.ApiKey = "required-secret");
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            "/v1/chat/completions",
            new { model = "chat-model" },
            ct);

        var recording = await V1Recording.FromResponseAsync(
            "missing_api_key",
            "POST",
            "/v1/chat/completions",
            response);
        var filename = Path.Combine(GetRecordingsDirectory(), "missing_api_key.json");
        await AssertMatchesGoldenAsync(recording, filename);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Record_InvalidApiKey_Returns401()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));

        using var app = await RouterHostFactory.CreateAsync(
            resolver,
            new FakeModelRuntime(),
            configureOptions: o => o.ApiKey = "required-secret");
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong-secret");

        using var response = await client.PostAsJsonAsync(
            "/v1/chat/completions",
            new { model = "chat-model" },
            ct);

        var recording = await V1Recording.FromResponseAsync(
            "invalid_api_key",
            "POST",
            "/v1/chat/completions",
            response);
        var filename = Path.Combine(GetRecordingsDirectory(), "invalid_api_key.json");
        await AssertMatchesGoldenAsync(recording, filename);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Record_ModelNotReady_Returns503WithRetryAfter()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Starting));

        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime(), o => o.AutoStartOnRequest = false);
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            "/v1/chat/completions",
            new { model = "chat-model" },
            ct);

        var recording = await V1Recording.FromResponseAsync(
            "model_not_ready",
            "POST",
            "/v1/chat/completions",
            response);
        var filename = Path.Combine(GetRecordingsDirectory(), "model_not_ready.json");
        await AssertMatchesGoldenAsync(recording, filename);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(recording.RetryAfter);
    }

    [Fact]
    public async Task Record_MaintenanceMode_Returns503WithRetryAfter()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));

        var maintenanceMode = new FakeMaintenance
        {
            // MN-090: an estimated end already reached makes Retry-After the deterministic floor (30 s); an end one hour
            // ahead recorded "3600" or "3599" depending on how long the request took.
            Current = new MaintenanceState("job-1", "benchmark", "qwen", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(-1))
        };

        using var app = await RouterHostFactory.CreateAsync(
            resolver,
            new FakeModelRuntime(),
            configureServices: s => s.AddSingleton<IMaintenanceMode>(maintenanceMode));
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            "/v1/chat/completions",
            new { model = "chat-model" },
            ct);

        var recording = await V1Recording.FromResponseAsync(
            "maintenance_mode",
            "POST",
            "/v1/chat/completions",
            response);
        var filename = Path.Combine(GetRecordingsDirectory(), "maintenance_mode.json");
        await AssertMatchesGoldenAsync(recording, filename);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(recording.RetryAfter);
    }

    [Fact]
    public async Task Record_UpstreamConnectFailure_Returns502()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));

        var handler = new FakeUpstreamHandler
        {
            Handler = (_, _) => throw new HttpRequestException("Connection refused"),
        };

        using var app = await RouterHostFactory.CreateAsync(
            resolver,
            new FakeModelRuntime(),
            configureServices: services => RouterProxyTests.ConfigureUpstream(services, handler));
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            "/v1/chat/completions",
            new { model = "chat-model" },
            ct);

        var recording = await V1Recording.FromResponseAsync(
            "upstream_connect_failure",
            "POST",
            "/v1/chat/completions",
            response);
        var filename = Path.Combine(GetRecordingsDirectory(), "upstream_connect_failure.json");
        await AssertMatchesGoldenAsync(recording, filename);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    /// <summary>
    /// Replay test: verify all recordings can be loaded and contain the expected structure.
    /// This proves that the golden files are valid for MN-043 to consume.
    /// </summary>
    [Fact]
    public async Task Replay_AllRecordings_AreValid()
    {
        var ct = TestContext.Current.CancellationToken;
        var recordingsPath = Path.Combine(GetRecordingsDirectory());

        Assert.True(Directory.Exists(recordingsPath), $"Recordings directory does not exist: {Path.GetFullPath(recordingsPath)}");

        var recordingFiles = Directory.GetFiles(recordingsPath, "*.json");
        Assert.NotEmpty(recordingFiles);

        var recordings = new List<V1Recording>();
        foreach (var file in recordingFiles)
        {
            var recording = await V1Recording.LoadAsync(file);
            recordings.Add(recording);

            // Verify required fields
            Assert.NotEmpty(recording.Scenario);
            Assert.NotEmpty(recording.Method);
            Assert.NotEmpty(recording.Path);
            Assert.True(recording.Status > 0, "Status code must be positive");
        }

        // We expect at least the scenarios from the task
        var scenarios = recordings.Select(r => r.Scenario).ToHashSet();
        Assert.Contains("get_models", scenarios);
        Assert.Contains("streaming_chat", scenarios);
        Assert.Contains("embeddings", scenarios);
        Assert.Contains("malformed_body", scenarios);
        Assert.Contains("unknown_model", scenarios);
        Assert.Contains("body_too_large", scenarios);
        Assert.Contains("missing_api_key", scenarios);
        Assert.Contains("invalid_api_key", scenarios);
        Assert.Contains("model_not_ready", scenarios);
        Assert.Contains("maintenance_mode", scenarios);
        Assert.Contains("upstream_connect_failure", scenarios);
    }

    /// <summary>Fake implementation of IMaintenanceMode for testing.</summary>
    private sealed class FakeMaintenance : IMaintenanceMode
    {
        public MaintenanceState? Current { get; set; }
    }
}
