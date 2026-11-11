using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Pki;
using MirbadLLM.Router;
using MirbadLLM.UnitTests.Multinode.Contract;
using MirbadLLM.UnitTests.Multinode.Fakes;
using FakeModelRegistry = MirbadLLM.UnitTests.Orchestration.Fakes.FakeModelRegistry;
using MirbadLLM.UnitTests.Router.Fakes;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Router;

/// <summary>
/// MN-030: the router through the agent (design §7.1, §7.2, §7.4). Resolution reads the registry and a status-cache fake;
/// traffic goes over the node's data client, either the real <see cref="AgentClientFactory"/> against <see cref="FakeAgent"/>
/// (mTLS, HTTP/2) or an in-memory data client with canned agent answers for the translation table.
/// </summary>
public sealed class AgentRouterTests
{
    private const string NodeId = "dgx";
    private const string ModelId = "qwen";
    private const string ServedName = "qwen-chat";

    // ---- Resolution (design §7.1) ----

    [Fact]
    public async Task UnknownServedName_Is404ModelNotFound_Unchanged()
    {
        await using var env = await StubEnv.StartAsync();

        using var response = await env.PostChatAsync("nobody");

        await AssertErrorAsync(response, HttpStatusCode.NotFound, "model_not_found", "invalid_request_error", retryAfter: null);
        Assert.Contains("Model 'nobody' is not registered.", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnplacedModel_Is503ModelUnplaced()
    {
        await using var env = await StubEnv.StartAsync(placement: null);

        using var response = await env.PostChatAsync(ServedName);

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "model_unplaced", "service_unavailable_error", retryAfter: null);
        Assert.Empty(env.Data.Requests);
    }

    [Theory]
    [InlineData(NodeState.Offline, "node_offline", 30)]
    [InlineData(NodeState.PendingTakeover, "node_offline", 30)]
    [InlineData(NodeState.Incompatible, "node_incompatible", 300)]
    public async Task NodeNotServing_IsTranslated_WithoutCallingTheAgent(NodeState state, string code, int retryAfter)
    {
        await using var env = await StubEnv.StartAsync();
        env.Cache.SetNode(NodeId, state);

        using var response = await env.PostChatAsync(ServedName);

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, code, "service_unavailable_error", retryAfter);
        Assert.Empty(env.Data.Requests);
    }

    [Fact]
    public async Task UnknownNode_IsOffline()
    {
        await using var env = await StubEnv.StartAsync();
        env.Cache.RemoveNode(NodeId);

        using var response = await env.PostChatAsync(ServedName);

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "node_offline", "service_unavailable_error", 30);
    }

    [Fact]
    public async Task DegradedOrDrainingNode_StillServes()
    {
        await using var env = await StubEnv.StartAsync();
        env.Cache.SetNode(NodeId, NodeState.Degraded, draining: true);

        using var response = await env.PostChatAsync(ServedName);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NotReadyModel_Is503ModelNotReady_WithTodaysText()
    {
        await using var env = await StubEnv.StartAsync();
        env.Cache.SetRuntime(ModelId, NodeId, ModelState.Starting);

        using var response = await env.PostChatAsync(ServedName);

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "model_not_ready", "service_unavailable_error", 30);
        Assert.Contains($"Model '{ServedName}' is not ready (state: Starting).", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Empty(env.Data.Requests);
    }

    [Fact]
    public async Task InstanceOnAnotherNode_DoesNotCountAsReady()
    {
        await using var env = await StubEnv.StartAsync();
        env.Cache.SetRuntime(ModelId, "other", ModelState.Ready);

        using var response = await env.PostChatAsync(ServedName);

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "model_not_ready", "service_unavailable_error", 30);
        Assert.Contains("(state: Stopped)", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoStart_StartsThroughTheRuntime_ThenProxies()
    {
        await using var env = await StubEnv.StartAsync(configure: o => o.AutoStartOnRequest = true);
        env.Cache.SetRuntime(ModelId, NodeId, ModelState.Stopped);
        env.Runtime.StartHandler = (id, _) =>
        {
            env.Cache.SetRuntime(id, NodeId, ModelState.Ready);
            return new ModelStatus(id, ModelState.Ready);
        };

        using var response = await env.PostChatAsync(ServedName);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, env.Runtime.StartCallCount);
        Assert.Single(env.Data.Requests);
    }

    [Fact]
    public async Task ModelsList_ShowsOnlyRoutableModels()
    {
        await using var env = await StubEnv.StartAsync();
        env.Registry.Add(Model("idle", "idle-chat", NodeId));
        env.Registry.Add(Model("loose", "loose-chat", null));
        env.Registry.Add(Model("far", "far-chat", "far"));
        env.Cache.SetNode("far", NodeState.Offline);
        env.Cache.SetRuntime("far", "far", ModelState.Ready); // last known Ready, node offline

        using var response = await env.Client.GetAsync("/v1/models", Ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var ids = document.RootElement.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToArray();

        Assert.Equal(ServedName, Assert.Single(ids));
        var endpoints = await env.App.Services.GetRequiredService<IModelEndpointResolver>().ListAsync(Ct);
        Assert.Equal(ModelState.Stopped, endpoints.Single(e => e.ModelId == "far").State);
        Assert.Equal(new Uri($"https://agent.test/agent/v1/proxy/{ModelId}/"), endpoints.Single(e => e.ModelId == ModelId).BaseUri);
    }

    // ---- Request shape (design §7.2) ----

    [Fact]
    public async Task Request_IsHttp2Exact_ToTheProxyRoute_WithoutClientProtocolHeaders()
    {
        await using var env = await StubEnv.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions?x=1")
        {
            Content = JsonContent.Create(new { model = ServedName }),
        };
        request.Headers.TryAddWithoutValidation(Protocol.MasterHeader, "evil/99/boot");
        request.Headers.TryAddWithoutValidation(Protocol.ProtocolHeader, "99");
        request.Headers.TryAddWithoutValidation("X-Custom", "kept");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "client-key");

        using var response = await env.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(env.Data.Requests);
        Assert.Equal(HttpVersion.Version20, sent.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, sent.VersionPolicy);
        Assert.Equal($"https://agent.test/agent/v1/proxy/{ModelId}/v1/chat/completions?x=1", sent.Uri.AbsoluteUri);
        Assert.DoesNotContain(sent.Headers, h => h.StartsWith("MirbadLLM-", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sent.Headers, h => h.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("X-Custom", sent.Headers);
        Assert.Contains("\"model\":\"qwen-chat\"", sent.Body, StringComparison.Ordinal);
    }

    // ---- Translation before headers (design §7.4, D40) ----

    [Theory]
    [InlineData(ErrorCodes.ModelNotRunning, 503, "model_not_ready", "service_unavailable_error", 30)]
    [InlineData(ErrorCodes.NotFound, 503, "model_not_ready", "service_unavailable_error", 30)]
    [InlineData(ErrorCodes.ServerBusy, 503, "server_busy", "service_unavailable_error", 5)]
    [InlineData(ErrorCodes.ProtocolMismatch, 503, "node_incompatible", "service_unavailable_error", 300)]
    [InlineData(ErrorCodes.InternalError, 502, "upstream_unavailable", "api_error", null)]
    [InlineData(ErrorCodes.StateUnavailable, 502, "upstream_unavailable", "api_error", null)]
    public async Task MarkedAgentAnswer_IsTranslated_AndDisposed(string agentCode, int status, string clientCode, string type, int? retryAfter)
    {
        await using var env = await StubEnv.StartAsync();
        env.Data.Respond = _ => AgentErrorAnswer(agentCode, marked: true);

        using var response = await env.PostChatAsync(ServedName);

        await AssertErrorAsync(response, (HttpStatusCode)status, clientCode, type, retryAfter);
        Assert.Equal(1, env.Data.DisposedResponses);
        var stats = Assert.Single(env.Stats.Snapshot());
        Assert.Equal(1, stats.FailedRequests);
        Assert.Equal(0, stats.InFlight);
        string[] expectedRefreshes = clientCode == ErrorCodes.ModelNotReady ? [ModelId] : [];
        Assert.Equal(expectedRefreshes, env.Cache.Refreshes.ToArray());
    }

    [Fact]
    public async Task ModelNotRunning_RefreshesTheCache_AndReportsTheRefreshedState()
    {
        await using var env = await StubEnv.StartAsync();
        env.Data.Respond = _ => AgentErrorAnswer(ErrorCodes.ModelNotRunning, marked: true);
        env.Cache.OnRefresh = id => env.Cache.SetRuntime(id, NodeId, ModelState.Failed);

        using var response = await env.PostChatAsync(ServedName);

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "model_not_ready", "service_unavailable_error", 30);
        Assert.Contains($"Model '{ServedName}' is not ready (state: Failed).", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(503)]
    [InlineData(500)]
    [InlineData(400)]
    public async Task UnmarkedAnswer_IsTheModelsOwn_AndPassesThroughUnchanged(int status)
    {
        await using var env = await StubEnv.StartAsync();
        // Looks exactly like an agent error but carries no MirbadLLM-Agent-Error header: it is the model's answer.
        env.Data.Respond = _ =>
        {
            var answer = AgentErrorAnswer(ErrorCodes.ModelNotRunning, marked: false);
            answer.StatusCode = (HttpStatusCode)status;
            return answer;
        };

        using var response = await env.PostChatAsync(ServedName);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Null(response.Headers.RetryAfter);
        Assert.Equal(AgentErrorJson(ErrorCodes.ModelNotRunning), await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, env.Data.DisposedResponses);
        Assert.Empty(env.Cache.Refreshes);
    }

    [Fact]
    public async Task UnreachableAgent_Is503NodeUnreachable()
    {
        await using var env = await StubEnv.StartAsync();
        env.Data.Respond = _ => throw new AgentUnreachableException(NodeId, "connection refused", null);

        using var response = await env.PostChatAsync(ServedName);

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "node_unreachable", "service_unavailable_error", 10);
        Assert.Equal(1, Assert.Single(env.Stats.Snapshot()).FailedRequests);
    }

    [Fact]
    public async Task NodeWithoutAClient_Is503NodeUnreachable()
    {
        await using var env = await StubEnv.StartAsync();
        env.Factory.Known = false;

        using var response = await env.PostChatAsync(ServedName);

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "node_unreachable", "service_unavailable_error", 10);
    }

    // ---- After headers (design §7.4) ----

    [Fact]
    public async Task FailureAfterHeaders_AbortsTheClient_WithNoJsonAppended()
    {
        await using var env = await StubEnv.StartAsync();
        env.Data.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FailingStream(Encoding.UTF8.GetBytes("data: {\"partial\":true}\n\n"))),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = JsonContent.Create(new { model = ServedName }) };
        using var response = await env.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (received, failure) = await ReadUntilEndOrFailureAsync(response);

        // Headers (200) went out before the failure; the client then sees a reset, never a JSON error. (TestServer may drop
        // the bytes still buffered for the client when the request aborts, so the partial chunk is not asserted.)
        Assert.NotNull(failure);
        Assert.DoesNotContain("error", received, StringComparison.Ordinal);

        // The client can see the reset before the server-side handler has disposed the response and counted the failure.
        await WaitUntilAsync(() => env.Data.DisposedResponses == 1, "the upstream response to be disposed");
        await WaitUntilAsync(() => env.Stats.Snapshot().SingleOrDefault()?.FailedRequests == 1, "the failure to be counted");
    }

    // ---- Upload budget (design §7.2) ----

    [Fact]
    public async Task UploadBudget_Exhausted_Is503ServerBusy_AndFreesAfterwards()
    {
        await using var env = await StubEnv.StartAsync(router: o =>
        {
            o.MaxBufferedBodyBytes = 1000;
            o.UploadBudgetWait = TimeSpan.FromMilliseconds(200);
        });
        var budget = env.App.Services.GetRequiredService<UploadBudget>();

        // A slow upload declaring 900 bytes holds 900 of the 1000 while its body trickles in.
        var slowBody = new GatedStream(Encoding.UTF8.GetBytes($"{{\"model\":\"{ServedName}\",\"pad\":\"".PadRight(898, 'x') + "\"}"));
        using var slowRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = new StreamContent(slowBody) };
        slowRequest.Content.Headers.ContentLength = slowBody.Length;
        slowRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var slowTask = env.Client.SendAsync(slowRequest, Ct);
        await WaitUntilAsync(() => budget.Available == 100, "the slow upload to take its budget");

        using (var busy = await env.PostChatAsync(ServedName, padding: 200))
        {
            await AssertErrorAsync(busy, HttpStatusCode.ServiceUnavailable, "server_busy", "service_unavailable_error", 5);
        }

        slowBody.Release();
        using (var slow = await slowTask)
        {
            Assert.Equal(HttpStatusCode.OK, slow.StatusCode);
        }

        Assert.Equal(1000, budget.Available);
        using var after = await env.PostChatAsync(ServedName, padding: 200);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal(1000, budget.Available);
    }

    [Fact]
    public async Task DeclaredBodyOverTheLimit_Is413_BeforeAnyBudgetIsTaken()
    {
        await using var env = await StubEnv.StartAsync();
        var budget = env.App.Services.GetRequiredService<UploadBudget>();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = new ByteArrayContent(new byte[(64 * 1024 * 1024) + 1]) };

        using var response = await env.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(budget.Capacity, budget.Available);
    }

    [Fact]
    public async Task ChunkedBody_IsReadWhole_AndOverTheLimitIs413()
    {
        await using var env = await StubEnv.StartAsync();
        string json = $"{{\"model\":\"{ServedName}\",\"pad\":\"{new string('x', 100_000)}\"}}";
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(json))) })
        {
            using var response = await env.Client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(json, Assert.Single(env.Data.Requests).Body);
        }

        using var tooLarge = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = new StreamContent(new MemoryStream(new byte[(64 * 1024 * 1024) + 1])) };
        using var refused = await env.Client.SendAsync(tooLarge, Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
        var budget = env.App.Services.GetRequiredService<UploadBudget>();
        Assert.Equal(budget.Capacity, budget.Available);
    }

    [Fact]
    public async Task UploadBudget_Lease_GrowsCapsAndReleasesOnce()
    {
        var budget = new UploadBudget(100, TimeSpan.FromMilliseconds(50));
        using (var lease = budget.CreateLease())
        {
            Assert.True(await lease.EnsureAsync(40, Ct));
            Assert.True(await lease.EnsureAsync(30, Ct)); // already held
            Assert.Equal(60, budget.Available);
            Assert.True(await lease.EnsureAsync(500, Ct)); // capped at the capacity
            Assert.Equal(0, budget.Available);

            using var other = budget.CreateLease();
            Assert.False(await other.EnsureAsync(1, Ct)); // waits 50 ms, then gives up
            lease.Dispose();
            lease.Dispose();
            Assert.Equal(100, budget.Available);
        }

        using var waiter = budget.CreateLease();
        using var holder = budget.CreateLease();
        Assert.True(await holder.EnsureAsync(100, Ct));
        var waiting = waiter.EnsureAsync(10, Ct).AsTask();
        holder.Dispose();
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal(90, budget.Available);
    }

    // ---- End to end against FakeAgent (mTLS, HTTP/2) ----

    [Fact]
    public async Task FakeAgent_StreamingChat_FlowsThroughTheAgentProxy()
    {
        await using var env = await AgentEnv.StartAsync();
        await env.LaunchReadyAsync(ModelId);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new { model = ServedName, stream = true, max_tokens = 5, messages = new[] { new { role = "user", content = "hi" } } }),
        };
        using var response = await env.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("data: ", body, StringComparison.Ordinal);
        Assert.Contains("[DONE]", body, StringComparison.Ordinal);
        var stats = Assert.Single(env.Stats.Snapshot());
        Assert.Equal((1L, 0L, 0), (stats.TotalRequests, stats.FailedRequests, stats.InFlight));
    }

    [Fact]
    public async Task FakeAgent_ModelNotRunning_Is503ModelNotReady_AndRefreshesTheCache()
    {
        await using var env = await AgentEnv.StartAsync();
        env.Cache.SetRuntime(ModelId, NodeId, ModelState.Ready); // the cache is one event late: no instance on the agent

        using var response = await env.PostChatAsync(ServedName);

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "model_not_ready", "service_unavailable_error", 30);
        Assert.Equal(ModelId, Assert.Single(env.Cache.Refreshes));
    }

    [Fact]
    public async Task FakeAgent_ModelErrorWithoutTheMarker_PassesThrough()
    {
        await using var env = await AgentEnv.StartAsync();
        await env.LaunchReadyAsync(ModelId);
        (await env.Agent.GetModelServerAsync()).SetFailureStatus(500);

        using var response = await env.PostChatAsync(ServedName);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(response.Headers.Contains(Protocol.AgentErrorHeader));
        Assert.DoesNotContain("upstream_unavailable", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Empty(env.Cache.Refreshes);
    }

    [Fact]
    public async Task FakeAgent_ResetMidStream_AbortsTheClient()
    {
        await using var env = await AgentEnv.StartAsync();
        await env.LaunchReadyAsync(ModelId);
        // The model resets only once the client has read its third token: an abort right after the write could drop bytes
        // the model server's transport had not sent yet (under CPU load even the headers, which made this 502).
        var reset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        (await env.Agent.GetModelServerAsync()).SetResetAfterTokenCount(3, reset.Task);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new { model = ServedName, stream = true, max_tokens = 50, messages = new[] { new { role = "user", content = "hi" } } }),
        };
        using var response = await env.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (received, failure) = await ReadUntilEndOrFailureAsync(response, "token_2", reset);

        Assert.Contains("token_2", received, StringComparison.Ordinal);

        Assert.NotNull(failure);
        Assert.DoesNotContain("\"error\"", received, StringComparison.Ordinal);
        Assert.DoesNotContain("[DONE]", received, StringComparison.Ordinal);

        // The client can see the reset before the server-side handler has disposed the response and counted the failure.
        await WaitUntilAsync(() => env.Stats.Snapshot().SingleOrDefault()?.FailedRequests == 1, "the failure to be counted");
    }

    [Fact]
    public async Task FakeAgent_ServerBusy_Is503ServerBusy()
    {
        await using var env = await AgentEnv.StartAsync(new FakeAgentOptions { MaxInFlightProxy = 0 });
        await env.LaunchReadyAsync(ModelId);

        using var response = await env.PostChatAsync(ServedName);

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "server_busy", "service_unavailable_error", 5);
    }

    [Fact]
    public async Task FakeAgent_ProtocolMismatch_Is503NodeIncompatible()
    {
        await using var env = await AgentEnv.StartAsync();
        await env.LaunchReadyAsync(ModelId);
        env.Factory.SetNegotiatedProtocol(NodeId, 99);

        using var response = await env.PostChatAsync(ServedName);

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "node_incompatible", "service_unavailable_error", 300);
    }

    [Fact]
    public async Task FakeAgent_Partitioned_Is503NodeUnreachable()
    {
        await using var env = await AgentEnv.StartAsync();
        await env.LaunchReadyAsync(ModelId);
        env.Agent.SetPartitioned(true);

        using var response = await env.PostChatAsync(ServedName);

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "node_unreachable", "service_unavailable_error", 10);
    }

    [Fact]
    public async Task FakeAgent_ChatClient_StreamsThroughTheAgent()
    {
        await using var env = await AgentEnv.StartAsync();
        await env.LaunchReadyAsync(ModelId);
        var chat = env.App.Services.GetRequiredService<IChatClient>();

        var chunks = new List<ChatChunk>();
        await foreach (var chunk in chat.StreamAsync(new ChatRequest(ServedName, [new ChatMessage("user", "hi")], MaxTokens: 4), Ct))
        {
            chunks.Add(chunk);
        }

        Assert.NotEmpty(chunks);
        Assert.Contains(chunks, c => c.Content is not null);
        Assert.Equal(0, Assert.Single(env.Stats.Snapshot()).FailedRequests);
    }

    [Fact]
    public async Task FakeAgent_ChatClient_ModelNotRunning_ThrowsNotReady_AndRefreshes()
    {
        await using var env = await AgentEnv.StartAsync();
        env.Cache.SetRuntime(ModelId, NodeId, ModelState.Ready);
        var chat = env.App.Services.GetRequiredService<IChatClient>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in chat.StreamAsync(new ChatRequest(ServedName, [new ChatMessage("user", "hi")]), Ct))
            {
            }
        });

        Assert.Equal($"Model '{ServedName}' is not ready (state: Stopped).", ex.Message);
        Assert.Equal(ModelId, Assert.Single(env.Cache.Refreshes));
        Assert.Equal(1, Assert.Single(env.Stats.Snapshot()).FailedRequests);
    }

    [Fact]
    public async Task ChatClient_NodeOffline_Throws_WithoutCallingTheAgent()
    {
        await using var env = await StubEnv.StartAsync();
        env.Cache.SetNode(NodeId, NodeState.Offline);
        var chat = env.App.Services.GetRequiredService<IChatClient>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in chat.StreamAsync(new ChatRequest(ServedName, [new ChatMessage("user", "hi")]), Ct))
            {
            }
        });

        Assert.Contains("offline", ex.Message, StringComparison.Ordinal);
        Assert.Empty(env.Data.Requests);
    }

    // ---- The Ollama routes take the same agent path ----

    [Fact]
    public async Task OllamaChat_GoesThroughTheAgentProxy_AndComesBackAsNdjson()
    {
        await using var env = await StubEnv.StartAsync();
        env.Data.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
        };

        using var response = await env.Client.PostAsync(
            new Uri("/api/chat", UriKind.Relative),
            new StringContent($$"""{"model":"{{ServedName}}:latest","messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        string[] lines = (await response.Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"content\":\"Hi\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"done\":true", lines[1], StringComparison.Ordinal);

        var sent = Assert.Single(env.Data.Requests);
        Assert.Equal($"/agent/v1/proxy/{ModelId}/v1/chat/completions", sent.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal(ServedName, body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        var stats = Assert.Single(env.Stats.Snapshot());
        Assert.Equal((1, 0), (stats.TotalRequests, stats.FailedRequests));
        Assert.Equal(1, env.Data.DisposedResponses);
    }

    [Fact]
    public async Task OllamaChat_NodeOffline_KeepsTheStatusAndRetryAfter_InOllamasErrorShape()
    {
        await using var env = await StubEnv.StartAsync();
        env.Cache.SetNode(NodeId, NodeState.Offline);

        using var response = await env.Client.PostAsync(
            new Uri("/api/chat", UriKind.Relative),
            new StringContent($$"""{"model":"{{ServedName}}","messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(30, (int?)response.Headers.RetryAfter?.Delta?.TotalSeconds);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Contains("is offline", body.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Empty(env.Data.Requests);
    }

    [Fact]
    public async Task OllamaChat_AgentError_IsTranslatedAsOnV1_ThenShapedForOllama()
    {
        await using var env = await StubEnv.StartAsync();
        env.Data.Respond = _ => AgentErrorAnswer(ErrorCodes.ModelNotRunning, marked: true);

        using var response = await env.Client.PostAsync(
            new Uri("/api/chat", UriKind.Relative),
            new StringContent($$"""{"model":"{{ServedName}}","messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(response.Headers.Contains(Protocol.AgentErrorHeader));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal($"Model '{ServedName}' is not ready (state: Stopped).", body.RootElement.GetProperty("error").GetString());
        Assert.Equal(1, Assert.Single(env.Stats.Snapshot()).FailedRequests);
    }

    [Fact]
    public async Task OllamaChat_FailureAfterTheFirstLine_AbortsTheClient_WithNothingAppended()
    {
        await using var env = await StubEnv.StartAsync();
        env.Data.Respond = _ =>
        {
            var content = new StreamContent(new FailingStream(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n")));
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(new { model = ServedName, messages = new[] { new { role = "user", content = "hi" } } }),
        };
        using var response = await env.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (received, failure) = await ReadUntilEndOrFailureAsync(response);

        // As on /v1: the client sees a reset. No last object claims the answer is done, and no error object is glued onto
        // a started stream. (TestServer may drop the line still buffered for the client, so it is not asserted.)
        Assert.NotNull(failure);
        Assert.DoesNotContain("\"done\":true", received, StringComparison.Ordinal);
        Assert.DoesNotContain("error", received, StringComparison.Ordinal);
        await WaitUntilAsync(() => env.Stats.Snapshot().SingleOrDefault()?.FailedRequests == 1, "the failure to be counted");
    }

    [Fact]
    public async Task OllamaChat_FailureBeforeAnythingLeft_Is502_InOllamasErrorShape()
    {
        await using var env = await StubEnv.StartAsync();
        env.Data.Respond = _ =>
        {
            // A single (not streamed) answer is held until it is complete, so its failure can still be answered properly.
            var content = new StreamContent(new FailingStream(Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"content\":\"Hel")));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };

        using var response = await env.Client.PostAsync(
            new Uri("/api/chat", UriKind.Relative),
            JsonContent.Create(new { model = ServedName, stream = false, messages = new[] { new { role = "user", content = "hi" } } }), Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(
            "{\"error\":\"Upstream request failed: The HTTP/2 stream was reset by the agent.\"}",
            await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, Assert.Single(env.Stats.Snapshot()).FailedRequests);
    }

    // ---- Helpers ----

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ModelDefinition Model(string id, string servedName, string? nodeId) => new()
    {
        Id = id,
        ServedName = servedName,
        ModelPath = "/models/" + id,
        Placement = nodeId is null ? null : new ModelPlacement(nodeId),
    };

    private static string AgentErrorJson(string code) =>
        JsonSerializer.Serialize(new AgentErrorResponse(new AgentError { Code = code, Message = $"agent says {code}" }), AgentJson.Options);

    private static HttpResponseMessage AgentErrorAnswer(string code, bool marked)
    {
        var response = new HttpResponseMessage((HttpStatusCode)ErrorMappings.AgentStatus(code))
        {
            Content = new StringContent(AgentErrorJson(code), Encoding.UTF8, AgentJson.JsonContentType),
        };
        if (marked)
        {
            response.Headers.TryAddWithoutValidation(Protocol.AgentErrorHeader, code);
        }

        return response;
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code, string type, int? retryAfter)
    {
        string body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(status == response.StatusCode, $"Expected {status}, got {response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(type, error.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        int? actualRetryAfter = response.Headers.RetryAfter?.Delta is { } delta ? (int)delta.TotalSeconds : null;
        Assert.Equal(retryAfter, actualRetryAfter);
        Assert.False(response.Headers.Contains(Protocol.AgentErrorHeader));
    }

    /// <summary>Reads the body to its end or a failure; once <paramref name="seen"/> was read, <paramref name="onSeen"/> is completed.</summary>
    private static async Task<(string Received, Exception? Failure)> ReadUntilEndOrFailureAsync(
        HttpResponseMessage response, string? seen = null, TaskCompletionSource? onSeen = null)
    {
        var received = new StringBuilder();
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(Ct);
            var buffer = new byte[1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct)) > 0)
            {
                received.Append(Encoding.UTF8.GetString(buffer, 0, read));
                if (seen is not null && received.ToString().Contains(seen, StringComparison.Ordinal))
                {
                    onSeen?.TrySetResult();
                }
            }

            return (received.ToString(), null);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return (received.ToString(), ex);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(10, Ct);
        }
    }

    private static async Task<WebApplication> StartRouterAsync(
        FakeModelRegistry registry,
        FakeNodeStatusCache cache,
        IAgentClientFactory clients,
        FakeModelRuntime runtime,
        Action<MirbadLLMOptions>? configure,
        Action<RouterOptions>? router)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddMirbadLLMRouter();
        builder.Services.AddMirbadLLMAgentRouting();
        builder.Services.AddSingleton<IModelRegistry>(registry);
        builder.Services.AddSingleton<INodeStatusCache>(cache);
        builder.Services.AddSingleton(clients);
        builder.Services.AddSingleton<IModelRuntime>(runtime);
        builder.Services.AddSingleton<IApiKeyStore>(new FakeApiKeyStore());
        builder.Services.Configure<MirbadLLMOptions>(o => configure?.Invoke(o));
        builder.Services.Configure<RouterOptions>(o => router?.Invoke(o));

        var app = builder.Build();
        app.MapMirbadLLMRouter();
        await app.StartAsync(Ct);
        return app;
    }

    private abstract class RouterEnv : IAsyncDisposable
    {
        public FakeModelRegistry Registry { get; } = new();

        public FakeNodeStatusCache Cache { get; } = new();

        public FakeModelRuntime Runtime { get; } = new();

        public WebApplication App { get; protected set; } = null!;

        public HttpClient Client { get; protected set; } = null!;

        public RouterStats Stats => App.Services.GetRequiredService<RouterStats>();

        public async Task<HttpResponseMessage> PostChatAsync(string servedName, int padding = 0)
        {
            object body = padding == 0 ? new { model = servedName } : new { model = servedName, pad = new string('x', padding) };
            return await Client.PostAsJsonAsync("/v1/chat/completions", body, Ct);
        }

        public virtual async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }

    /// <summary>The router over an in-memory data client: the agent's answers are canned.</summary>
    private sealed class StubEnv : RouterEnv
    {
        public StubDataClient Data { get; } = new();

        public StubClientFactory Factory { get; private set; } = null!;

        public static async Task<StubEnv> StartAsync(
            string? placement = NodeId,
            Action<MirbadLLMOptions>? configure = null,
            Action<RouterOptions>? router = null)
        {
            var env = new StubEnv();
            env.Factory = new StubClientFactory(env.Data);
            env.Registry.Add(Model(ModelId, ServedName, placement));
            env.Cache.SetNode(NodeId, NodeState.Online);
            env.Cache.SetRuntime(ModelId, NodeId, ModelState.Ready);
            env.App = await StartRouterAsync(env.Registry, env.Cache, env.Factory, env.Runtime, configure, router);
            env.Client = env.App.GetTestClient();
            return env;
        }
    }

    /// <summary>The router over the real <see cref="AgentClientFactory"/> and a <see cref="FakeAgent"/> (mTLS, HTTP/2).</summary>
    private sealed class AgentEnv : RouterEnv
    {
        private readonly List<IDisposable> _owned = [];
        private string _dataDir = null!;

        public FakeAgent Agent { get; private set; } = null!;

        public AgentClientFactory Factory { get; private set; } = null!;

        public static async Task<AgentEnv> StartAsync(FakeAgentOptions? options = null)
        {
            var env = new AgentEnv();
            try
            {
                env.Agent = await FakeAgent.StartAsync(options, Ct);
                env._dataDir = Path.Combine(Path.GetTempPath(), "mn030-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(env._dataDir);
                var nodes = new JsonNodeRegistry(Options.Create(new MirbadLLMOptions { DataPath = env._dataDir }), TimeProvider.System);
                env._owned.Add(nodes);
                await nodes.UpsertAsync(new NodeRecord
                {
                    Id = env.Agent.NodeId,
                    Uid = env.Agent.NodeUid,
                    Address = env.Agent.BaseAddress.AbsoluteUri,
                    Approved = true,
                    CertSpkiSha256 = env.Agent.CurrentSpki,
                }, Ct);
                var master = env.Agent.Ca.IssueMasterClientCertificate();
                env._owned.Add(master);
                var trust = TrustAnchorSet.Create([env.Agent.Ca.Certificate]);
                env._owned.Add(trust);
                env.Factory = new AgentClientFactory(nodes, new TestMasterIdentity(), master, trust, TimeProvider.System);
                await env.Factory.RefreshAsync(Ct);

                env.Registry.Add(Model(ModelId, ServedName, NodeId));
                env.Cache.SetNode(NodeId, NodeState.Online);
                env.App = await StartRouterAsync(env.Registry, env.Cache, env.Factory, env.Runtime, null, null);
                env.Client = env.App.GetTestClient();
                return env;
            }
            catch
            {
                await env.DisposeAsync();
                throw;
            }
        }

        /// <summary>Launches <paramref name="modelId"/> on the agent, waits for Ready and tells the cache.</summary>
        public async Task LaunchReadyAsync(string modelId)
        {
            var client = Factory.GetClient(NodeId);
            await client.LaunchAsync(modelId, AgentContractSuite.LaunchFor(modelId), Ct);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while ((await client.GetRuntimeAsync(modelId, Ct))?.State != ModelState.Ready)
            {
                Assert.True(DateTime.UtcNow < deadline, $"{modelId} did not become Ready on the fake agent.");
                await Task.Delay(20, Ct);
            }

            Cache.SetRuntime(modelId, NodeId, ModelState.Ready);
        }

        public override async ValueTask DisposeAsync()
        {
            if (App is not null)
            {
                await base.DisposeAsync();
            }

            if (Factory is not null)
            {
                await Factory.DisposeAsync();
            }

            if (Agent is not null)
            {
                await Agent.DisposeAsync();
            }

            foreach (var owned in _owned)
            {
                owned.Dispose();
            }

            if (_dataDir is not null)
            {
                try
                {
                    Directory.Delete(_dataDir, recursive: true);
                }
                catch (IOException)
                {
                    // best effort
                }
            }
        }
    }

    private sealed class TestMasterIdentity : IMasterIdentity
    {
        public string InstanceId => "instance-mn030";

        public long Epoch => 1;

        public string BootId => "boot-mn030";

        public MasterTriple Current => new(InstanceId, Epoch, BootId);

        public bool TakeoverRequested => false;

        public IReadOnlyList<TakeoverRecord> Takeovers => [];

        public Task<MasterTriple> RaiseEpochToAsync(long epoch, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<MasterTriple> CompleteTakeoverAsync(TakeoverRecord record, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>Hand-written <see cref="INodeStatusCache"/> (MN-027 builds the real one): nodes and instances set by the test.</summary>
    private sealed class FakeNodeStatusCache : INodeStatusCache
    {
        private readonly ConcurrentDictionary<string, NodeLiveStatus> _nodes = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, NodeModelRuntime> _runtimes = new(StringComparer.Ordinal);
        private long _version;

        public ConcurrentQueue<string> Refreshes { get; } = new();

        public Action<string>? OnRefresh { get; set; }

        public long Version => Interlocked.Read(ref _version);

        public void SetNode(string nodeId, NodeState state, bool draining = false)
        {
            _nodes[nodeId] = new NodeLiveStatus { NodeId = nodeId, NodeUid = "uid-" + nodeId, State = state, Draining = draining };
            Interlocked.Increment(ref _version);
        }

        public void RemoveNode(string nodeId) => _nodes.TryRemove(nodeId, out _);

        public void SetRuntime(string modelId, string nodeId, ModelState state)
        {
            _runtimes[modelId] = new NodeModelRuntime { ModelId = modelId, NodeId = nodeId, State = state };
            Interlocked.Increment(ref _version);
        }

        public IReadOnlyList<NodeLiveStatus> ListNodes() => [.. _nodes.Values];

        public NodeLiveStatus? GetNode(string nodeId) => _nodes.GetValueOrDefault(nodeId);

        public NodeModelRuntime? GetRuntime(string modelId) =>
            _runtimes.TryGetValue(modelId, out var runtime) && runtime.State != ModelState.Stopped ? runtime : null;

        public IReadOnlyList<NodeModelRuntime> ListRuntimes(string? nodeId = null) =>
            [.. _runtimes.Values.Where(r => nodeId is null || r.NodeId == nodeId)];

        public ServedModelRoute? ResolveServedName(string servedName) => null;

        public Task RefreshModelAsync(string modelId, CancellationToken ct = default)
        {
            Refreshes.Enqueue(modelId);
            OnRefresh?.Invoke(modelId);
            return Task.CompletedTask;
        }

        public Task RefreshNodeAsync(string nodeId, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>One request the stub data client received.</summary>
    private sealed record SentRequest(Uri Uri, Version Version, HttpVersionPolicy VersionPolicy, IReadOnlyList<string> Headers, string Body);

    /// <summary>In-memory <see cref="IAgentDataClient"/>: records requests, answers with <see cref="Respond"/>, counts disposed responses.</summary>
    private sealed class StubDataClient : IAgentDataClient
    {
        private int _disposed;

        public ConcurrentQueue<SentRequest> Requests { get; } = new();

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
        };

        public int DisposedResponses => Volatile.Read(ref _disposed);

        public string NodeId => AgentRouterTests.NodeId;

        public HttpMessageInvoker Invoker => throw new NotSupportedException();

        public Uri BuildProxyUri(string modelId, string pathAndQuery) => new($"https://agent.test/agent/v1/proxy/{modelId}/{pathAndQuery}");

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var headers = request.Headers.Select(h => h.Key).Concat(request.Content?.Headers.Select(h => h.Key) ?? []).ToArray();
            Requests.Enqueue(new SentRequest(request.RequestUri!, request.Version, request.VersionPolicy, headers, body));
            var response = Respond(request);
            response.Content = new DisposalTrackingContent(response.Content, () => Interlocked.Increment(ref _disposed));
            return response;
        }
    }

    /// <summary>A content wrapper that reports its disposal (the real data client releases the in-flight slot there).</summary>
    private sealed class DisposalTrackingContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly Action _onDispose;
        private bool _disposed;

        public DisposalTrackingContent(HttpContent inner, Action onDispose)
        {
            _inner = inner;
            _onDispose = onDispose;
            foreach (var header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _inner.CopyToAsync(stream);

        protected override Task<Stream> CreateContentReadStreamAsync() => _inner.ReadAsStreamAsync();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _inner.Dispose();
                _onDispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary><see cref="IAgentClientFactory"/> whose clients only carry <see cref="StubDataClient"/>.</summary>
    private sealed class StubClientFactory(StubDataClient data) : IAgentClientFactory
    {
        public bool Known { get; set; } = true;

        public IAgentClient GetClient(string nodeId)
        {
            if (!Known || nodeId != NodeId)
            {
                throw new KeyNotFoundException(nodeId);
            }

            var client = DispatchProxy.Create<IAgentClient, DataOnlyAgentClient>();
            ((DataOnlyAgentClient)(object)client).Data = data;
            return client;
        }

        public Task<IAgentClientLease> CreateFreshClientAsync(string nodeId, CancellationToken ct = default) => throw new NotSupportedException();

        public void RebuildHandlers(string nodeId)
        {
        }

        public void SetNegotiatedProtocol(string nodeId, int version)
        {
        }
    }

    /// <summary>An <see cref="IAgentClient"/> whose only working members are <c>Data</c> and <c>NodeId</c> (the router uses no other).</summary>
#pragma warning disable CA1852 // DispatchProxy derives from it at run time
    internal class DataOnlyAgentClient : DispatchProxy
#pragma warning restore CA1852
    {
        public IAgentDataClient? Data { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_Data" => Data,
            "get_NodeId" => NodeId,
            _ => throw new NotSupportedException($"{targetMethod?.Name} is not used by the router."),
        };
    }

    /// <summary>Yields its bytes, then fails like a connection reset by the agent.</summary>
    private sealed class FailingStream(byte[] prefix) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(ReadCore(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(ReadCore(buffer.AsSpan(offset, count)));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int ReadCore(Span<byte> destination)
        {
            if (_position >= prefix.Length)
            {
                throw new IOException("The HTTP/2 stream was reset by the agent.");
            }

            int count = Math.Min(destination.Length, prefix.Length - _position);
            prefix.AsSpan(_position, count).CopyTo(destination);
            _position += count;
            return count;
        }
    }

    /// <summary>A request body that sends its first byte, then waits for <see cref="Release"/> before the rest.</summary>
    private sealed class GatedStream(byte[] content) : Stream
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => content.Length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public void Release() => _gate.TrySetResult();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= content.Length)
            {
                return 0;
            }

            if (_position > 0)
            {
                await _gate.Task.WaitAsync(cancellationToken);
            }

            int count = _position == 0 ? 1 : Math.Min(buffer.Length, content.Length - _position);
            content.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
