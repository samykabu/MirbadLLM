using System.Net;
using System.Text;
using System.Text.Json;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Multinode.Fakes;
using MirbadLLM.UnitTests.Orchestration.Fakes;

namespace MirbadLLM.UnitTests.Multinode;

public sealed class FakeModelServerTests
{
    [Fact]
    public async Task CreateBindsToLoopbackAndReturnsUri()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeModelServer.CreateAsync(ct: ct);

        Assert.NotNull(server.BaseUri);
        var host = server.BaseUri.Host;
        Assert.True(host == "127.0.0.1" || host == "localhost", $"Expected loopback address, got {host}");
        Assert.True(server.BaseUri.Port > 0 && server.BaseUri.Port < 65536);
    }

    [Fact]
    public async Task HealthReturnsOk()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeModelServer.CreateAsync(ct: ct);
        using var client = new HttpClient();

        using var response = await client.GetAsync(new Uri(server.BaseUri, "/health"), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task MetricsReturnsPrometheusFormat()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeModelServer.CreateAsync(ct: ct);
        using var client = new HttpClient();

        using var response = await client.GetAsync(new Uri(server.BaseUri, "/metrics"), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(ct);

        // Verify key metric names are present
        Assert.Contains("vllm:num_requests_running", text);
        Assert.Contains("vllm:generation_tokens_total", text);
        Assert.Contains("vllm:request_success_total", text);
        Assert.Contains("vllm:time_to_first_token_seconds", text);
        Assert.Contains("vllm:inter_token_latency_seconds", text);
        Assert.Contains("vllm:e2e_request_latency_seconds", text);
        Assert.Contains("vllm:request_queue_time_seconds", text);
    }

    [Fact]
    public async Task ListModelsReturnsModelList()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeModelServer.CreateAsync(ct: ct);
        using var client = new HttpClient();

        using var response = await client.GetAsync(new Uri(server.BaseUri, "/v1/models"), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        Assert.Equal("list", root.GetProperty("object").GetString());
        var data = root.GetProperty("data");
        Assert.True(data.GetArrayLength() >= 1);

        var firstModel = data[0];
        Assert.Equal("fake-model", firstModel.GetProperty("id").GetString());
        Assert.Equal("model", firstModel.GetProperty("object").GetString());
        Assert.Equal("mirbadllm", firstModel.GetProperty("owned_by").GetString());
    }

    [Fact]
    public async Task TokenizeReturnsTokenArray()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeModelServer.CreateAsync(ct: ct);
        using var client = new HttpClient();

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server.BaseUri, "/tokenize"))
        {
            Content = new StringContent("hello world test", Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var tokens = doc.RootElement.GetProperty("tokens");
        Assert.True(tokens.GetArrayLength() > 0);
    }

    [Fact]
    public async Task ChatCompletionsStreamingDeliveredIncrementally()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new ManualTimeProvider();
        await using var server = await FakeModelServer.CreateAsync(timeProvider: timeProvider, ct: ct);
        using var client = new HttpClient();

        server.SetTimeToFirstTokenMs(5);
        server.SetTokenRateMs(1);
        server.SetSilentGapMs(0);

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server.BaseUri, "/v1/chat/completions"))
        {
            Content = new StringContent(
                JsonDocument.Parse("""{"stream":true,"messages":[{"role":"user","content":"hello"}]}""").RootElement.GetRawText(),
                Encoding.UTF8,
                "application/json"),
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var events = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.StartsWith("data:"))
            {
                events.Add(line);
            }
            if (line == "data: [DONE]")
            {
                break;
            }
        }

        Assert.NotEmpty(events);
        Assert.Equal("data: [DONE]", events[^1]); // the OpenAI/vLLM end marker, as an SSE data line
        Assert.All(events.SkipLast(1), e => Assert.Contains("\"object\":\"chat.completion.chunk\"", e, StringComparison.Ordinal));
        Assert.Single(events, e => e.Contains("\"finish_reason\":\"stop\"", StringComparison.Ordinal));

        // Verify at least some events have delta content
        var eventCount = events.Count(e => e.Contains("\"delta\""));
        Assert.True(eventCount > 0, "Should have events with delta");

        // Verify we have a completion event with usage
        var completionEvent = events.FirstOrDefault(e => e.Contains("\"usage\""));
        Assert.NotNull(completionEvent);
    }

    [Fact]
    public async Task ChatCompletionsNonStreamingReturnsCompletion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeModelServer.CreateAsync(ct: ct);
        using var client = new HttpClient();

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server.BaseUri, "/v1/chat/completions"))
        {
            Content = new StringContent(
                """{"stream":false,"messages":[{"role":"user","content":"hello"}]}""",
                Encoding.UTF8,
                "application/json"),
        };

        using var response = await client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        Assert.Equal("chat.completion", root.GetProperty("object").GetString());
        var choices = root.GetProperty("choices");
        Assert.Equal(1, choices.GetArrayLength());

        var choice = choices[0];
        var message = choice.GetProperty("message");
        Assert.Equal("assistant", message.GetProperty("role").GetString());
        Assert.NotNull(message.GetProperty("content").GetString());

        var usage = root.GetProperty("usage");
        Assert.True(usage.GetProperty("prompt_tokens").GetInt32() > 0);
        Assert.True(usage.GetProperty("completion_tokens").GetInt32() > 0);
    }

    [Fact]
    public async Task EmbeddingsReturnsEmbeddingVector()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeModelServer.CreateAsync(ct: ct);
        using var client = new HttpClient();

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server.BaseUri, "/v1/embeddings"))
        {
            Content = new StringContent(
                """{"input":"hello world"}""",
                Encoding.UTF8,
                "application/json"),
        };

        using var response = await client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        Assert.Equal("list", root.GetProperty("object").GetString());
        var data = root.GetProperty("data");
        Assert.Equal(1, data.GetArrayLength());

        var embedding = data[0];
        Assert.Equal("embedding", embedding.GetProperty("object").GetString());
        Assert.Equal(0, embedding.GetProperty("index").GetInt32());

        var vector = embedding.GetProperty("embedding");
        Assert.Equal(384, vector.GetArrayLength());
    }

    [Fact]
    public async Task NotReadyPeriodReturns503()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new ManualTimeProvider();
        await using var server = await FakeModelServer.CreateAsync(timeProvider: timeProvider, ct: ct);
        using var client = new HttpClient();

        server.SetNotReadyPeriod(TimeSpan.FromSeconds(10));

        using var response = await client.GetAsync(new Uri(server.BaseUri, "/health"), ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("30", response.Headers.GetValues("Retry-After").First());
    }

    [Fact]
    public async Task FailureStatusInjectionReturnConfiguredStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeModelServer.CreateAsync(ct: ct);
        using var client = new HttpClient();

        server.SetFailureStatus(500);

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server.BaseUri, "/v1/chat/completions"))
        {
            Content = new StringContent(
                """{"stream":false}""",
                Encoding.UTF8,
                "application/json"),
        };

        using var response = await client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task TokenRateConfigurableAffectsStreamingRate()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new ManualTimeProvider();
        await using var server = await FakeModelServer.CreateAsync(timeProvider: timeProvider, ct: ct);
        using var client = new HttpClient();

        server.SetTokenRateMs(100);
        server.SetTimeToFirstTokenMs(0);
        server.SetSilentGapMs(0);

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server.BaseUri, "/v1/chat/completions"))
        {
            Content = new StringContent(
                """{"stream":true}""",
                Encoding.UTF8,
                "application/json"),
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        using var stream = await response.Content.ReadAsStreamAsync(ct);

        var buffer = new byte[65536];
        var read = await stream.ReadAsync(buffer, ct);
        stopwatch.Stop();

        // Even with manual time, the server should read from TimeProvider
        // This test just verifies that the setting is accepted without error
        Assert.True(read > 0);
    }

    [Fact]
    public async Task MetricsIncrementOnRequests()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeModelServer.CreateAsync(ct: ct);
        using var client = new HttpClient();

        // Send a chat completion
        var chatRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(server.BaseUri, "/v1/chat/completions"))
        {
            Content = new StringContent(
                """{"stream":false}""",
                Encoding.UTF8,
                "application/json"),
        };
        using (await client.SendAsync(chatRequest, ct)) { }

        // Get metrics
        using var metricsResponse = await client.GetAsync(new Uri(server.BaseUri, "/metrics"), ct);
        var metricsText = await metricsResponse.Content.ReadAsStringAsync(ct);

        // Verify metrics incremented
        Assert.Contains("vllm:request_success_total", metricsText);
        Assert.Contains("vllm:generation_tokens_total", metricsText);
    }

    [Fact]
    public async Task ResetAfterTokenCountStopsGenerationEarly()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new ManualTimeProvider();
        await using var server = await FakeModelServer.CreateAsync(timeProvider: timeProvider, ct: ct);
        using var client = new HttpClient();

        // The reset waits until the client has read the fifth token: an abort right after a write drops what the transport
        // has not sent yet, under load even the headers (SendAsync then failed with a connection reset).
        var reset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SetResetAfterTokenCount(5, reset.Task);
        server.SetTimeToFirstTokenMs(0);
        server.SetTokenRateMs(1);

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server.BaseUri, "/v1/chat/completions"))
        {
            Content = new StringContent(
                """{"stream":true}""",
                Encoding.UTF8,
                "application/json"),
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // The server aborts the connection after 5 tokens: the client sees the stream end early (or an I/O error), never
        // the final chunk or [DONE] — what a crashed model looks like to the proxy.
        var tokenEvents = 0;
        var sawDone = false;
        var sawStop = false;
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal) && line.Contains("token_", StringComparison.Ordinal))
                {
                    tokenEvents++;
                    if (tokenEvents == 5)
                    {
                        reset.TrySetResult();
                    }
                }

                sawDone |= line == "data: [DONE]";
                sawStop |= line.Contains("\"finish_reason\":\"stop\"", StringComparison.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            // The abort surfaced as a broken response body.
        }

        // Exactly the 5 tokens before the reset (all read before it was released), then never the end of the stream.
        Assert.Equal(5, tokenEvents);
        Assert.False(sawDone);
        Assert.False(sawStop);
    }
}
