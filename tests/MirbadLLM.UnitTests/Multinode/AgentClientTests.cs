using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Pki;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required here.

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-021: the master's agent client factory, typed client, operation references and error mapping against an in-process
/// mTLS agent stub (Kestrel, the production <see cref="PkiTls"/> shapes): HTTP/2 exact with no downgrade, two independent
/// handlers per node, retries with the same body bytes, no retry of HTTP errors, <c>operations.json</c> before send,
/// handler rebuild + drain, per-handshake pins.
/// </summary>
public sealed class AgentClientTests
{
    private const string NodeId = "dgx";
    private const string NodeUid = "0f9a1b2c3d4e5f60718293a4b5c6d7e8";

    // ---- Transport: HTTP/2, headers, handlers ----

    [Fact]
    public async Task EveryRequest_IsHttp2Exact_WithProtocolMasterAndRequestTimeHeaders()
    {
        await using Env env = await Env.StartAsync();
        env.Stub.Handler = (ctx, rec) => rec.Path switch
        {
            "/agent/v1/info" => Json(ctx, 200, Info()),
            "/agent/v1/runtimes/qwen" => Error(ctx, 404, ErrorCodes.NotFound),
            "/agent/v1/runtimes/qwen/stop" => Json(ctx, 202, Op("op-1", OperationState.Accepted)),
            _ => Error(ctx, 404, ErrorCodes.NotFound),
        };
        IAgentClient client = env.Factory.GetClient(NodeId);
        env.Factory.SetNegotiatedProtocol(NodeId, 1);

        InfoResponse info = await client.GetInfoAsync();
        RuntimeStatus? runtime = await client.GetRuntimeAsync("qwen");
        AgentEnvelope envelope = AgentEnvelope.Create(TimeProvider.System);
        OperationStatus stop = await client.StopAsync("qwen", Stop(envelope));

        Assert.Equal(NodeId, info.NodeId);
        Assert.Null(runtime); // 404 not_found = no instance = Stopped
        Assert.Equal(OperationState.Accepted, stop.State);
        Recorded[] requests = [.. env.Stub.Requests];
        Assert.Equal(3, requests.Length);
        Assert.All(requests, r => Assert.Equal("HTTP/2", r.Protocol));

        Assert.Null(requests[0].Header(Protocol.ProtocolHeader)); // /info negotiates, no protocol header
        Assert.Null(requests[0].Header(Protocol.MasterHeader));
        Assert.Equal("1", requests[1].Header(Protocol.ProtocolHeader));
        Assert.Null(requests[1].Header(Protocol.MasterHeader)); // reads carry no master triple

        Assert.Equal("1", requests[2].Header(Protocol.ProtocolHeader));
        Assert.Equal(env.Identity.Current.ToHeaderValue(), requests[2].Header(Protocol.MasterHeader));
        Assert.True(DateTimeOffset.TryParse(requests[2].Header(Protocol.RequestTimeHeader), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _));
        using JsonDocument body = JsonDocument.Parse(requests[2].Body);
        Assert.Equal(envelope.OperationId, body.RootElement.GetProperty("operationId").GetString());
        Assert.Equal(envelope.ExpiresAt, body.RootElement.GetProperty("expiresAt").GetDateTimeOffset());
    }

    /// <summary>P3 (MN-202): the GGUF route sends <c>?file=</c> only when a file is named and reads the summary.</summary>
    [Fact]
    public async Task InspectGguf_SendsTheFileQuery_AndReadsTheSummary()
    {
        await using Env env = await Env.StartAsync();
        var summary = new GgufSummary { Folder = "f", File = "m.gguf", GgufVersion = 3, Architecture = "qwen3", BlockCount = 36, TensorBytes = 1, FileBytes = 2 };
        env.Stub.Handler = (ctx, rec) => rec.Path == "/agent/v1/model-folders/f/gguf" ? Json(ctx, 200, summary) : Error(ctx, 404, ErrorCodes.NotFound);
        env.Factory.SetNegotiatedProtocol(NodeId, 1);
        IAgentClient client = env.Factory.GetClient(NodeId);

        Assert.Equal(summary, await client.InspectGgufAsync("f", "m.gguf"));
        Assert.Equal("?file=m.gguf", env.Stub.Requests.Last().Query);
        Assert.Equal(summary, await client.InspectGgufAsync("f", null));
        Assert.Equal("", env.Stub.Requests.Last().Query);
    }

    [Fact]
    public async Task NegotiatedProtocol_IsSentOnLaterRequests()
    {
        await using Env env = await Env.StartAsync();
        env.Stub.Handler = (ctx, _) => Json(ctx, 200, new PkiTrustResponse());
        env.Factory.SetNegotiatedProtocol(NodeId, 7);

        await env.Factory.GetClient(NodeId).GetTrustAsync();

        Assert.Equal("7", Assert.Single(env.Stub.Requests).Header(Protocol.ProtocolHeader));
        Assert.Equal(7, env.Factory.GetNegotiatedProtocol(NodeId));
        Assert.Equal(Protocol.CurrentVersion, env.Factory.GetNegotiatedProtocol("other"));
    }

    [Fact]
    public async Task Http1OnlyAgent_IsANodeError_NeverADowngrade()
    {
        await using Env env = await Env.StartAsync(protocols: HttpProtocols.Http1);
        env.Stub.Handler = (ctx, _) => Json(ctx, 200, Info());

        var ex = await Assert.ThrowsAsync<AgentUnreachableException>(() => env.Factory.GetClient(NodeId).GetInfoAsync());

        Assert.True(ex.Http2NotNegotiated, ex.Message);
        Assert.Equal(NodeId, ex.NodeId);
        Assert.Contains("HTTP/2 not negotiated", ex.Message, StringComparison.Ordinal);
        Assert.Empty(env.Stub.Requests); // nothing was served over HTTP/1.1
    }

    [Fact]
    public async Task AgentWithAnUnpinnedKey_IsACertificateRefusal_NotAnHttp2Problem()
    {
        using ECDsa otherKey = PkiDefaults.CreateKey();
        string otherPin = Spki.Sha256(otherKey);
        await using Env env = await Env.StartAsync(registeredPin: otherPin);
        env.Stub.Handler = (ctx, _) => Json(ctx, 200, Info());

        var ex = await Assert.ThrowsAsync<AgentUnreachableException>(() => env.Factory.GetClient(NodeId).GetInfoAsync());

        Assert.False(ex.Http2NotNegotiated);
        Assert.Contains(nameof(PeerValidationFailure.PinMismatch), ex.Message, StringComparison.Ordinal);
        Assert.Empty(env.Stub.Requests);
    }

    [Fact]
    public async Task ControlCalls_AreUnaffected_WhileTheDataChannelIsSaturated()
    {
        // One stream per HTTP/2 connection on the stub, so every in-flight data request holds a whole connection, and the
        // per-node data bound at 16: the data channel is saturated at 16 requests. (SocketsHttpHandler does not apply
        // MaxConnectionsPerServer to HTTP/2 connections, so the in-flight bound is what actually caps the data channel.)
        const int Saturation = AgentClientDefaults.DataMaxConnections;
        await using Env env = await Env.StartAsync(maxStreamsPerConnection: 1, options: new AgentClientFactoryOptions { MaxDataInFlightPerNode = Saturation });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int proxyInFlight = 0;
        env.Stub.Handler = async (ctx, rec) =>
        {
            if (rec.Path.EndsWith("/v1/slow", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref proxyInFlight);
                await release.Task;
                await ctx.Response.WriteAsync("done");
                return;
            }

            await Json(ctx, 200, Info());
        };
        IAgentClient client = env.Factory.GetClient(NodeId);
        IAgentDataClient data = client.Data;

        // Warm up so the pool has seen the stub's SETTINGS (1 stream per connection) before it is saturated; requests that
        // race the first SETTINGS frame would be refused by Kestrel, which is a property of the stub, not of the client.
        using (HttpResponseMessage warm = await data.SendAsync(new HttpRequestMessage(HttpMethod.Get, data.BuildProxyUri("qwen", "v1/warm"))))
        {
            Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
        }

        List<Task<HttpResponseMessage>> proxied = [];
        for (int i = 0; i < Saturation; i++)
        {
            proxied.Add(data.SendAsync(new HttpRequestMessage(HttpMethod.Get, data.BuildProxyUri("qwen", "v1/slow"))));
            int expected = i + 1;
            await WaitUntilAsync(() => Volatile.Read(ref proxyInFlight) == expected, TimeSpan.FromSeconds(10));
        }

        using HttpResponseMessage excess = await data.SendAsync(new HttpRequestMessage(HttpMethod.Get, data.BuildProxyUri("qwen", "v1/slow")));
        InfoResponse info = await client.GetInfoAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, excess.StatusCode); // server_busy, decided on the master
        Assert.Equal(Saturation, Volatile.Read(ref proxyInFlight));
        Assert.Equal(NodeId, info.NodeId);
        Recorded infoRequest = env.Stub.Requests.Single(r => r.Path == "/agent/v1/info");
        var dataConnections = env.Stub.Requests.Where(r => r.Path.EndsWith("/v1/slow", StringComparison.Ordinal)).Select(r => r.ConnectionId).ToHashSet();
        Assert.Equal(Saturation, dataConnections.Count); // each busy data connection carries its one stream…
        Assert.DoesNotContain(infoRequest.ConnectionId, dataConnections); // …and the control call used its own handler

        release.SetResult();
        foreach (Task<HttpResponseMessage> task in proxied)
        {
            using HttpResponseMessage response = await task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("done", await response.Content.ReadAsStringAsync());
        }
    }

    // ---- Retries (§4.5) ----

    [Fact]
    public async Task MutationRetry_AfterATransportFailure_ResendsTheSameBodyBytes()
    {
        await using Env env = await Env.StartAsync();
        int calls = 0;
        env.Stub.Handler = (ctx, rec) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                ctx.Abort(); // the agent "died" after reading the request, before any response
                return Task.CompletedTask;
            }

            return Json(ctx, 202, Op("op-retry", OperationState.Accepted));
        };
        AgentEnvelope envelope = AgentEnvelope.Create(TimeProvider.System);

        OperationStatus status = await env.Factory.GetClient(NodeId).StopAsync("qwen", Stop(envelope));

        Assert.Equal(OperationState.Accepted, status.State);
        Recorded[] requests = [.. env.Stub.Requests];
        Assert.Equal(2, requests.Length);
        Assert.Equal(requests[0].Body, requests[1].Body); // byte for byte: same id, same envelope, same payload
        Assert.Contains(envelope.OperationId, Encoding.UTF8.GetString(requests[1].Body), StringComparison.Ordinal);
        Assert.All(requests, r => Assert.Equal(env.Identity.Current.ToHeaderValue(), r.Header(Protocol.MasterHeader)));
    }

    [Fact]
    public async Task Reads_RetryATransportFailureBeforeAResponse()
    {
        await using Env env = await Env.StartAsync();
        int calls = 0;
        env.Stub.Handler = (ctx, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                ctx.Abort();
                return Task.CompletedTask;
            }

            return Json(ctx, 200, Op("op-read", OperationState.Running));
        };

        OperationStatus? status = await env.Factory.GetClient(NodeId).GetOperationAsync("op-read");

        Assert.Equal(OperationState.Running, status?.State);
        Assert.Equal(2, env.Stub.Requests.Count);
    }

    [Fact]
    public async Task HttpErrorResponses_AreNeverRetried()
    {
        await using Env env = await Env.StartAsync();
        env.Stub.Handler = (ctx, rec) => rec.Path.EndsWith("/stop", StringComparison.Ordinal)
            ? Error(ctx, 409, ErrorCodes.OperationIdConflict, "id reused")
            : Error(ctx, 503, ErrorCodes.StateUnavailable, "disk full");
        IAgentClient client = env.Factory.GetClient(NodeId);

        var conflict = await Assert.ThrowsAsync<AgentException>(() => client.StopAsync("qwen", Stop(AgentEnvelope.Create(TimeProvider.System))));
        var unavailable = await Assert.ThrowsAsync<AgentException>(() => client.ListOperationsAsync(new OperationsQuery { State = OperationsQuery.ActiveState }));

        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal(ErrorCodes.OperationIdConflict, conflict.Code);
        Assert.Equal(NodeId, conflict.NodeId);
        Assert.Equal(503, unavailable.StatusCode);
        Assert.Equal(ErrorCodes.StateUnavailable, unavailable.Code);
        Assert.Equal(2, env.Stub.Requests.Count); // one each
        Assert.Equal("?state=active", env.Stub.Requests.Last().Query);
    }

    [Fact]
    public async Task ANonAgentErrorBody_StillThrowsAnAgentException()
    {
        await using Env env = await Env.StartAsync();
        env.Stub.Handler = async (ctx, _) =>
        {
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsync("<html>boom</html>");
        };

        var ex = await Assert.ThrowsAsync<AgentException>(() => env.Factory.GetClient(NodeId).GetStatusAsync());

        Assert.Equal(500, ex.StatusCode);
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
    }

    // ---- Operation references (§4.4 master side) ----

    [Fact]
    public async Task OperationReference_IsWrittenBeforeSend_AndClosedOnATerminalStatus()
    {
        await using Env env = await Env.StartAsync();
        using var store = env.CreateStore();
        var runner = new AgentOperationRunner(env.Factory, store, env.Registry, TimeProvider.System);
        AgentEnvelope envelope = runner.CreateEnvelope();
        string? fileAtSend = null;
        env.Stub.Handler = (ctx, _) =>
        {
            fileAtSend = File.ReadAllText(Path.Combine(env.DataDir, AgentOperationReferenceDocument.FileName));
            return Json(ctx, 200, Op(envelope.OperationId, OperationState.Succeeded));
        };

        OperationStatus status = await runner.SubmitAsync(NodeId, AgentRoutes.RuntimeStop, "qwen", Stop(envelope),
            (client, request, ct) => client.StopAsync("qwen", request, ct), owner: "job-1");

        Assert.Equal(OperationState.Succeeded, status.State);
        Assert.NotNull(fileAtSend);
        Assert.Contains(envelope.OperationId, fileAtSend, StringComparison.Ordinal);
        Assert.Contains("\"status\": \"open\"", fileAtSend, StringComparison.Ordinal);

        using var reloaded = env.CreateStore(); // what a restarted master reads
        AgentOperationReference? reference = await reloaded.GetAsync(envelope.OperationId);
        Assert.NotNull(reference);
        Assert.Equal(AgentOperationReferenceStatus.Closed, reference.Status);
        Assert.Equal(AgentOperationOutcome.Succeeded, reference.Outcome);
        Assert.Equal(NodeUid, reference.NodeUid);
        Assert.Equal(OperationKinds.Stop, reference.Action);
        Assert.Equal("qwen", reference.Target);
        Assert.Equal("job-1", reference.Owner);
        Assert.Equal(envelope.IssuedAt, reference.IssuedAt);
        Assert.Equal(envelope.ExpiresAt, reference.ExpiresAt);
        Assert.Equal(AgentPayloadDigest.Compute(Stop(envelope), typeof(StopRequest)), reference.PayloadDigest);
    }

    [Fact]
    public async Task OperationReference_ClosesAsRefused_OnAnHttpError()
    {
        await using Env env = await Env.StartAsync();
        using var store = env.CreateStore();
        var runner = new AgentOperationRunner(env.Factory, store, env.Registry, TimeProvider.System);
        env.Stub.Handler = (ctx, _) => Error(ctx, 409, ErrorCodes.AdmissionRefused, "Not enough memory on node dgx.");
        AgentEnvelope envelope = runner.CreateEnvelope();

        var ex = await Assert.ThrowsAsync<AgentException>(() => runner.SubmitAsync(NodeId, AgentRoutes.RuntimeStop, "qwen", Stop(envelope),
            (client, request, ct) => client.StopAsync("qwen", request, ct)));

        AgentOperationReference? reference = await store.GetAsync(envelope.OperationId);
        Assert.Equal(ErrorCodes.AdmissionRefused, ex.Code);
        Assert.Equal(AgentOperationOutcome.Refused, reference?.Outcome);
        Assert.Equal(ErrorCodes.AdmissionRefused, reference?.Error);
        Assert.Single(env.Stub.Requests);
    }

    [Theory]
    [InlineData(500, "internal_error")]
    [InlineData(504, "outcome_unknown")]
    public async Task OperationReference_StaysOpen_OnAnErrorThatMayFollowTheDurableAccept(int status, string code)
    {
        await using Env env = await Env.StartAsync();
        using var store = env.CreateStore();
        var runner = new AgentOperationRunner(env.Factory, store, env.Registry, TimeProvider.System);
        env.Stub.Handler = (ctx, _) => Error(ctx, status, code, "The outcome is not a refusal.");
        AgentEnvelope envelope = runner.CreateEnvelope();

        var ex = await Assert.ThrowsAsync<AgentException>(() => runner.SubmitAsync(NodeId, AgentRoutes.RuntimeStop, "qwen", Stop(envelope),
            (client, request, ct) => client.StopAsync("qwen", request, ct)));

        Assert.Equal(code, ex.Code);
        AgentOperationReference? reference = await store.GetAsync(envelope.OperationId);
        Assert.Equal(AgentOperationReferenceStatus.Open, reference?.Status);
        Assert.Single(env.Stub.Requests); // still never retried automatically
    }

    [Fact]
    public async Task OpenReferences_AreObservedAfterAMasterRestart_NotReissued()
    {
        await using Env env = await Env.StartAsync();
        string? runningId = null;
        bool finished = false;
        env.Stub.Handler = (ctx, rec) =>
        {
            if (rec.Method == "POST")
            {
                return Json(ctx, 202, Op(runningId!, OperationState.Running));
            }

            if (rec.Path == "/agent/v1/operations/" + runningId)
            {
                return Json(ctx, 200, Op(runningId!, finished ? OperationState.Failed : OperationState.Running, error: ErrorCodes.Superseded));
            }

            return Error(ctx, 404, ErrorCodes.NotFound); // the agent never saw the others
        };

        // Before the "restart": a running stop, plus references a crashed master left behind.
        using (var store = env.CreateStore())
        {
            var runner = new AgentOperationRunner(env.Factory, store, env.Registry, TimeProvider.System);
            AgentEnvelope envelope = runner.CreateEnvelope();
            runningId = envelope.OperationId;
            OperationStatus accepted = await runner.SubmitAsync(NodeId, AgentRoutes.RuntimeStop, "qwen", Stop(envelope),
                (client, request, ct) => client.StopAsync("qwen", request, ct));
            Assert.Equal(OperationState.Running, accepted.State);
            Assert.Equal(AgentOperationReferenceStatus.Open, (await store.GetAsync(runningId))?.Status);

            DateTimeOffset now = DateTimeOffset.UtcNow;
            await store.AddAsync(Reference("expired", NodeUid, now.AddHours(-30), createdAt: now.AddHours(-30)));
            await store.AddAsync(Reference("never-sent", NodeUid, now.AddMinutes(-10), createdAt: now.AddMinutes(-10)));
            await store.AddAsync(Reference("just-written", NodeUid, now, createdAt: now));
            await store.AddAsync(Reference("old-enrollment", "an-older-uid", now, createdAt: now));
        }

        using var restartedStore = env.CreateStore();
        var restarted = new AgentOperationRunner(env.Factory, restartedStore, env.Registry, TimeProvider.System);
        int postsBefore = env.Stub.Requests.Count(r => r.Method == "POST");

        IReadOnlyList<AgentOperationObservation> first = await restarted.ResumeOpenAsync();
        finished = true;
        IReadOnlyList<AgentOperationObservation> second = await restarted.ResumeOpenAsync();

        Assert.Equal(5, first.Count);
        Assert.Equal(postsBefore, env.Stub.Requests.Count(r => r.Method == "POST")); // observed, never re-sent
        Assert.Equal(OperationState.Running, first.Single(o => o.Reference.OperationId == runningId).Status?.State);
        Assert.Equal(AgentOperationOutcome.Expired, Outcome(first, "expired"));
        Assert.Equal(AgentOperationOutcome.NotAccepted, Outcome(first, "never-sent"));
        Assert.Null(Outcome(first, "just-written")); // may still be in flight
        Assert.Equal(AgentOperationOutcome.Lost, Outcome(first, "old-enrollment"));

        Assert.Equal(2, second.Count); // the running stop and the fresh one
        AgentOperationReference? closed = await restartedStore.GetAsync(runningId!);
        Assert.Equal(AgentOperationOutcome.Failed, closed?.Outcome);
        Assert.Equal(ErrorCodes.Superseded, closed?.Error);

        static AgentOperationOutcome? Outcome(IReadOnlyList<AgentOperationObservation> list, string id) =>
            list.Single(o => o.Reference.OperationId == id).Reference.Outcome;
    }

    [Fact]
    public async Task ATerminalOperationEvent_ClosesTheReference()
    {
        await using Env env = await Env.StartAsync();
        using var store = env.CreateStore();
        var runner = new AgentOperationRunner(env.Factory, store, env.Registry, TimeProvider.System);
        await store.AddAsync(Reference("op-event", NodeUid, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        bool ignored = await runner.CloseFromStatusAsync(Op("op-event", OperationState.Running));
        bool closed = await runner.CloseFromStatusAsync(Op("op-event", OperationState.Cancelled));
        bool again = await runner.CloseFromStatusAsync(Op("op-event", OperationState.Succeeded));

        Assert.False(ignored);
        Assert.True(closed);
        Assert.False(again);
        Assert.Equal(AgentOperationOutcome.Cancelled, (await store.GetAsync("op-event"))?.Outcome);
    }

    [Fact]
    public async Task ReferenceStore_RefusesAReusedId_AndPrunesExpiredClosedReferences()
    {
        string dir = NewTempDir();
        try
        {
            var time = new MutableTime(DateTimeOffset.UtcNow);
            using var store = new JsonAgentOperationReferenceStore(Options.Create(new MirbadLLMOptions { DataPath = dir }), time);
            DateTimeOffset now = time.GetUtcNow();
            await store.AddAsync(Reference("a", NodeUid, now, now));
            await store.AddAsync(Reference("b", NodeUid, now, now));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync(Reference("a", NodeUid, now, now)));
            await store.CloseAsync("a", AgentOperationOutcome.Succeeded, null);

            time.Now = now + OperationRetention.EnvelopeLifetime + OperationRetention.RetentionMargin + TimeSpan.FromMinutes(1);
            await store.AddAsync(Reference("c", NodeUid, time.Now, time.Now)); // a write prunes

            IReadOnlyList<AgentOperationReference> all = await store.ListAsync();
            Assert.Equal(["b", "c"], all.Select(r => r.OperationId));
            Assert.Null(await store.CloseAsync("missing", AgentOperationOutcome.Failed, null));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- Rebuild, drain, pins (§7.2, §10.2) ----

    [Fact]
    public async Task RebuildHandlers_OpensANewTlsSession_AndTheOldGenerationDrains()
    {
        await using Env env = await Env.StartAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int slowStarted = 0;
        env.Stub.Handler = async (ctx, rec) =>
        {
            if (rec.Path.StartsWith("/agent/v1/proxy/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref slowStarted);
                await ctx.Response.WriteAsync("first ");
                await ctx.Response.Body.FlushAsync();
                await release.Task;
                await ctx.Response.WriteAsync("last");
                return;
            }

            await Json(ctx, 200, Info());
        };
        IAgentClient before = env.Factory.GetClient(NodeId);
        AgentHandlerGeneration oldGeneration = env.Factory.GetGeneration(NodeId);
        await before.GetInfoAsync();
        string oldConnection = env.Stub.Requests.Last().ConnectionId;
        HttpResponseMessage streaming = await before.Data.SendAsync(new HttpRequestMessage(HttpMethod.Get, before.Data.BuildProxyUri("qwen", "v1/stream")));
        await WaitUntilAsync(() => Volatile.Read(ref slowStarted) == 1, TimeSpan.FromSeconds(10));
        int handshakesBefore = env.Stub.Handshakes;

        env.Factory.RebuildHandlers(NodeId);
        IAgentClient after = env.Factory.GetClient(NodeId);
        await after.GetInfoAsync();

        Assert.NotSame(before, after);
        Assert.NotEqual(oldConnection, env.Stub.Requests.Last().ConnectionId); // a new connection…
        Assert.True(env.Stub.Handshakes > handshakesBefore); // …with a new TLS handshake (no resumption)
        Assert.True(oldGeneration.IsDraining);
        Assert.False(oldGeneration.IsDisposed); // the streamed response is still in flight

        release.SetResult();
        string text = await streaming.Content.ReadAsStringAsync();
        streaming.Dispose();
        await oldGeneration.Disposed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("first last", text);
        Assert.True(oldGeneration.IsDisposed);
        Assert.False(env.Factory.GetGeneration(NodeId).IsDraining);
    }

    [Fact]
    public async Task DrainTimeout_DisposesTheOldGeneration_EvenWithRequestsInFlight()
    {
        await using Env env = await Env.StartAsync(options: new AgentClientFactoryOptions { DrainTimeout = TimeSpan.FromMilliseconds(200) });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Stub.Handler = async (ctx, _) =>
        {
            await ctx.Response.WriteAsync("x");
            await ctx.Response.Body.FlushAsync();
            await release.Task;
        };
        IAgentDataClient data = env.Factory.GetClient(NodeId).Data;
        using HttpResponseMessage held = await data.SendAsync(new HttpRequestMessage(HttpMethod.Get, data.BuildProxyUri("qwen", "v1/held")));
        AgentHandlerGeneration old = env.Factory.GetGeneration(NodeId);

        env.Factory.RebuildHandlers(NodeId);
        await old.Disposed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(old.IsDisposed);
        release.SetResult();
    }

    [Fact]
    public async Task APendingPin_AppliesAfterRefresh_WithoutRebuild_AndDroppingAPinRebuilds()
    {
        await using Env env = await Env.StartAsync();
        env.Stub.Handler = (ctx, _) => Json(ctx, 200, Info());
        string csr = PkiTestSupport.NewCsr(out ECDsa newKey, NodeId);
        using ECDsa ownedKey = newKey;
        using IssuedCertificate rotated = env.Ca.IssueAgentCertificate(csr, NodeId, ["127.0.0.1"]);
        // Owned by the env: disposed after the stub stopped. A `using` here would dispose it while the stub still selects it
        // for handshakes (SChannel would read a freed certificate context, MN-047).
        X509Certificate2 rotatedTls = env.Own(TlsCredentials.FromPem(rotated.CertificatePem, newKey.ExportPkcs8PrivateKeyPem()));
        env.Stub.Certificate = rotatedTls; // §10.2 step 3: the agent presents the new certificate on new connections
        long generation = env.Factory.GetGeneration(NodeId).Id;

        await Assert.ThrowsAsync<AgentUnreachableException>(() => env.Factory.GetClient(NodeId).GetInfoAsync());
        await env.Registry.UpdateAsync(NodeId, n => n with { PendingCertSpkiSha256 = rotated.SpkiSha256 });
        await env.Factory.RefreshNodeAsync(NodeId);
        InfoResponse info = await env.Factory.GetClient(NodeId).GetInfoAsync();

        Assert.Equal(NodeId, info.NodeId);
        Assert.Equal(generation, env.Factory.GetGeneration(NodeId).Id); // source.Set, no rebuild (MN-090)

        await env.Registry.UpdateAsync(NodeId, n => n with { CertSpkiSha256 = rotated.SpkiSha256, PendingCertSpkiSha256 = null });
        await env.Factory.RefreshNodeAsync(NodeId);

        Assert.NotEqual(generation, env.Factory.GetGeneration(NodeId).Id); // the old pin is gone: both handlers replaced
        await env.Factory.GetClient(NodeId).GetInfoAsync();
    }

    [Fact]
    public async Task FreshClient_UsesNewHandlers_AndOnlyTheGivenPins()
    {
        await using Env env = await Env.StartAsync();
        env.Stub.Handler = (ctx, _) => Json(ctx, 200, Info());
        await env.Factory.GetClient(NodeId).GetInfoAsync();
        int handshakes = env.Stub.Handshakes;
        using ECDsa otherKey = PkiDefaults.CreateKey();

        await using (IAgentClientLease lease = await env.Factory.CreateFreshClientAsync(NodeId))
        {
            Assert.NotSame(env.Factory.GetClient(NodeId), lease.Client);
            await lease.Client.GetInfoAsync();
        }

        await using (IAgentClientLease wrongPin = await env.Factory.CreateFreshClientAsync(NodeId, [Spki.Sha256(otherKey)]))
        {
            var ex = await Assert.ThrowsAsync<AgentUnreachableException>(() => wrongPin.Client.GetInfoAsync());
            Assert.Contains(nameof(PeerValidationFailure.PinMismatch), ex.Message, StringComparison.Ordinal);
        }

        Assert.True(env.Stub.Handshakes > handshakes);
        await env.Factory.GetClient(NodeId).GetInfoAsync(); // the pooled client is untouched
    }

    [Fact]
    public async Task UnknownRevokedAndUnpinnedNodes_HaveNoClient()
    {
        await using Env env = await Env.StartAsync();
        await env.Registry.UpsertAsync(new NodeRecord { Id = "pending", Uid = "u2", Address = "https://10.0.0.2:9443" });

        await env.Factory.RefreshAsync();
        Assert.Equal([NodeId], env.Factory.NodeIds);
        Assert.Throws<KeyNotFoundException>(() => env.Factory.GetClient("pending")); // no pin yet
        Assert.Throws<KeyNotFoundException>(() => env.Factory.GetClient("nope"));

        AgentHandlerGeneration generation = env.Factory.GetGeneration(NodeId);
        await env.Registry.RevokeAsync(NodeId);
        await env.Factory.RefreshAsync();

        Assert.Throws<KeyNotFoundException>(() => env.Factory.GetClient(NodeId));
        Assert.True(generation.IsDisposed); // idle: drained at once
        await Assert.ThrowsAsync<KeyNotFoundException>(() => env.Factory.CreateFreshClientAsync(NodeId));
    }

    [Fact]
    public async Task UpdateMasterTls_RebuildsEveryNode_WithTheNewClientCertificate()
    {
        await using Env env = await Env.StartAsync();
        env.Stub.Handler = (ctx, _) => Json(ctx, 200, Info());
        await env.Factory.GetClient(NodeId).GetInfoAsync();
        long generation = env.Factory.GetGeneration(NodeId).Id;
        using IssuedCredential reissued = env.Ca.IssueMasterClientCertificate();

        env.Factory.UpdateMasterTls(reissued, env.Trust);
        await env.Factory.GetClient(NodeId).GetInfoAsync();

        Assert.NotEqual(generation, env.Factory.GetGeneration(NodeId).Id);
        Assert.Equal(reissued.SpkiSha256, env.Stub.LastClientSpki);
    }

    [Fact]
    public void ReleasedTlsMaterial_LeavesTheClientCertificateToTheFinalizer()
    {
        // MN-047: a connection attempt can outlive its disposed handler and still be in its TLS handshake; on Windows
        // SChannel reads the certificate's native context without a reference, so disposing it crashed the process.
        using var ca = CertificateAuthority.Create();
        using IssuedCredential credential = ca.IssueMasterClientCertificate();
        using var trust = TrustAnchorSet.Create([ca.Certificate]);
        var material = new MasterTlsMaterial(credential, trust);

        material.Release();

        Assert.True(material.IsDisposed);
        Assert.NotEqual(IntPtr.Zero, material.Context.TargetCertificate.Handle);
        Assert.True(material.Context.TargetCertificate.HasPrivateKey);
    }

    // ---- Data channel ----

    [Fact]
    public async Task Proxy_BuildsTheUri_AndReturnsAgentErrorsBeforeHeadersAsResponses()
    {
        await using Env env = await Env.StartAsync();
        env.Stub.Handler = (ctx, _) => Error(ctx, 503, ErrorCodes.ModelNotRunning, "no instance");
        IAgentDataClient data = env.Factory.GetClient(NodeId).Data;

        Uri uri = data.BuildProxyUri("qwen 3", "/v1/chat/completions?stream=true");
        using HttpResponseMessage response = await data.SendAsync(new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent("{}") });

        Assert.Equal(env.Stub.BaseUri.AbsoluteUri.TrimEnd('/') + "/agent/v1/proxy/qwen%203/v1/chat/completions?stream=true", uri.AbsoluteUri);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AgentErrorResponse? error = await response.Content.ReadFromJsonAsync<AgentErrorResponse>(AgentJson.Options);
        Assert.Equal(ErrorCodes.ModelNotRunning, error?.Error.Code);
        Assert.Equal(ErrorCodes.ModelNotReady, ErrorMappings.TranslateProxyError(error!.Error.Code).ClientCode);
        Recorded request = Assert.Single(env.Stub.Requests);
        Assert.Equal("HTTP/2", request.Protocol);
        Assert.Equal("1", request.Header(Protocol.ProtocolHeader)); // added on the data channel too
        Assert.Equal("?stream=true", request.Query);
    }

    [Fact]
    public async Task DataInFlightLimit_AnswersServerBusyLocally()
    {
        await using Env env = await Env.StartAsync(options: new AgentClientFactoryOptions { MaxDataInFlightPerNode = 1 });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Stub.Handler = async (ctx, _) =>
        {
            await ctx.Response.WriteAsync("x");
            await ctx.Response.Body.FlushAsync();
            await release.Task;
        };
        IAgentDataClient data = env.Factory.GetClient(NodeId).Data;
        HttpResponseMessage first = await data.SendAsync(new HttpRequestMessage(HttpMethod.Get, data.BuildProxyUri("qwen", "v1/a")));

        using HttpResponseMessage busy = await data.SendAsync(new HttpRequestMessage(HttpMethod.Get, data.BuildProxyUri("qwen", "v1/b")));
        release.SetResult();
        await first.Content.ReadAsStringAsync();
        first.Dispose();
        using HttpResponseMessage third = await data.SendAsync(new HttpRequestMessage(HttpMethod.Get, data.BuildProxyUri("qwen", "v1/c")));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(5), busy.Headers.RetryAfter?.Delta);
        Assert.Equal(ErrorCodes.ServerBusy, (await busy.Content.ReadFromJsonAsync<AgentErrorResponse>(AgentJson.Options))?.Error.Code);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode); // the slot was released with the first body
        Assert.Equal(2, env.Stub.Requests.Count); // the busy one never left the master
    }

    // ---- Streams ----

    [Fact]
    public async Task Events_AreReadFromSse_UnknownTypesSkipped()
    {
        await using Env env = await Env.StartAsync();
        env.Stub.Handler = async (ctx, _) =>
        {
            ctx.Response.ContentType = AgentJson.EventStreamContentType;
            await WriteEventAsync(ctx, new HeartbeatEvent { BootId = "b1", Seq = 4 });
            await ctx.Response.WriteAsync("event: future\ndata: {\"type\":\"future\",\"seq\":5}\n\n: comment\n\n");
            await WriteEventAsync(ctx, new OperationEvent { BootId = "b1", Seq = 6, Operation = Op("op-e", OperationState.Succeeded) });
            await WriteEventAsync(ctx, new ResetEvent { BootId = "b1", Seq = 6, Reason = ResetEvent.Evicted });
        };

        List<AgentEvent> events = [];
        await foreach (AgentEvent e in env.Factory.GetClient(NodeId).SubscribeEventsAsync(new EventsQuery { BootId = "b1", AfterSeq = 3 }))
        {
            events.Add(e);
        }

        Assert.Collection(
            events,
            e => Assert.Equal(4, Assert.IsType<HeartbeatEvent>(e).Seq),
            e => Assert.Equal("op-e", Assert.IsType<OperationEvent>(e).Operation.Id),
            e => Assert.Equal(ResetEvent.Evicted, Assert.IsType<ResetEvent>(e).Reason));
        Assert.Equal("?bootId=b1&afterSeq=3", Assert.Single(env.Stub.Requests).Query);
    }

    [Fact]
    public async Task Logs_YieldOneStringPerDataLine_AndAMissingInstanceThrows()
    {
        await using Env env = await Env.StartAsync();
        env.Stub.Handler = async (ctx, rec) =>
        {
            if (rec.Path.Contains("/missing/", StringComparison.Ordinal))
            {
                await Error(ctx, 404, ErrorCodes.NotFound);
                return;
            }

            ctx.Response.ContentType = AgentJson.EventStreamContentType;
            await ctx.Response.WriteAsync("data: INFO starting\n\ndata: WARN slow\n\ndata: \n\n");
        };
        IAgentClient client = env.Factory.GetClient(NodeId);

        List<string> lines = [];
        await foreach (string line in client.StreamLogsAsync("qwen", new LogsQuery { Tail = 5, Follow = false }))
        {
            lines.Add(line);
        }

        Assert.Equal(["INFO starting", "WARN slow", ""], lines);
        Assert.Equal("?tail=5&follow=false", env.Stub.Requests.First().Query);
        var ex = await Assert.ThrowsAsync<AgentException>(async () =>
        {
            await foreach (string unused in client.StreamLogsAsync("missing", new LogsQuery()))
            {
            }
        });
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task GpuIdle_SendsListsCommaSeparated()
    {
        await using Env env = await Env.StartAsync();
        env.Stub.Handler = (ctx, _) => Json(ctx, 200, new GpuIdleResponse { AveragePercent = 1.5, Samples = 3 });

        GpuIdleResponse idle = await env.Factory.GetClient(NodeId).SampleGpuIdleAsync(new GpuIdleQuery { Seconds = 1, Gpus = [0, 1] });

        Assert.Equal(1.5, idle.AveragePercent);
        Assert.Equal("?seconds=1&gpus=0%2C1", Assert.Single(env.Stub.Requests).Query);
    }

    // ---- Pure units ----

    [Fact]
    public void AgentErrors_MapToTheExceptionsTodaysCallersHandle()
    {
        foreach ((string code, Type expected) in ErrorMappings.MasterExceptions)
        {
            Exception mapped = AgentErrorMapper.ToMasterException(new AgentException(409, new AgentError { Code = code, Message = "Refused." }, NodeId));
            Assert.IsType(expected, mapped);
            Assert.Equal("Refused. (on node dgx)", mapped.Message);
            Assert.IsType<AgentException>(mapped.InnerException);
        }

        var conflict = new AgentError
        {
            Code = ErrorCodes.StartConflict,
            Message = "Model 'b' is still starting on node dgx.",
            Details = AgentJson.ToElement(new StartConflictDetails { StartingModelId = "b" }),
        };
        var start = Assert.IsType<ModelStartConflictException>(AgentErrorMapper.ToMasterException(new AgentException(409, conflict, NodeId)));
        Assert.Equal("b", start.StartingModelId);
        Assert.Equal("Model 'b' is still starting on node dgx.", start.Message); // the agent already named the node

        // A fencing refusal is "not serving" (Pending takeover, D52): 503 at the admin API (MN-047).
        var fenced = Assert.IsType<NodeUnavailableException>(AgentErrorMapper.ToMasterException(
            new AgentException(409, new AgentError { Code = ErrorCodes.StaleMaster, Message = "stale" }, NodeId)));
        Assert.Equal(NodeUnavailableReason.NotServing, fenced.Reason);
        Assert.Equal(NodeId, fenced.NodeId);
        Assert.IsType<InvalidOperationException>(AgentErrorMapper.ToMasterException(
            new AgentException(409, new AgentError { Code = ErrorCodes.InvalidState, Message = "state" }, NodeId)));
    }

    /// <summary>W8 review (MN-128): the per-GPU numbers of a discrete refusal reach the master's "Start anyway?" dialog.</summary>
    [Fact]
    public void AdmissionRefusals_CarryTheAgentsNumbers_IncludingTheRefusingGpu()
    {
        var error = new AgentError
        {
            Code = ErrorCodes.AdmissionRefused,
            Message = "Model 'm' needs 8.0 GiB but only 2.5 GiB is free on node pc (GPU 1).",
            Details = AgentJson.ToElement(new AdmissionRefusedDetails { NeedGiB = 8, FreeGiB = 2.5, AvailableGiB = 4, ReserveGiB = 1.5, Gpu = "GPU-b" }),
        };
        var refused = Assert.IsType<ModelMemoryAdmissionException>(AgentErrorMapper.ToMasterException(new AgentException(409, error, "pc")));

        var details = AgentErrorMapper.AdmissionDetails(refused);

        Assert.NotNull(details);
        Assert.Equal((8.0, 2.5, 4.0, 1.5, "GPU-b"), (details.NeedGiB, details.FreeGiB, details.AvailableGiB, details.ReserveGiB, details.Gpu));
        Assert.Null(AgentErrorMapper.AdmissionDetails(new ModelMemoryAdmissionException("single host")));
        Assert.Null(AgentErrorMapper.AdmissionDetails(null));
    }

    [Fact]
    public void Envelope_ExpiresAfter24Hours_AndIdsAreFresh()
    {
        var time = new MutableTime(new DateTimeOffset(2026, 10, 3, 12, 0, 0, 123, TimeSpan.Zero).AddTicks(4567));

        AgentEnvelope a = AgentEnvelope.Create(time);
        AgentEnvelope b = AgentEnvelope.Create(time);

        Assert.Equal(new DateTimeOffset(2026, 10, 3, 12, 0, 0, 123, TimeSpan.Zero), a.IssuedAt);
        Assert.Equal(a.IssuedAt + TimeSpan.FromHours(24), a.ExpiresAt);
        Assert.Matches("^[0-9a-f]{32}$", a.OperationId);
        Assert.NotEqual(a.OperationId, b.OperationId);
    }

    [Fact]
    public void PayloadDigest_IgnoresSecrets_ButNotOtherFields()
    {
        AgentEnvelope e = AgentEnvelope.Create(TimeProvider.System);
        DownloadStartRequest Download(string? token, string folder) => new()
        {
            OperationId = e.OperationId, IssuedAt = e.IssuedAt, ExpiresAt = e.ExpiresAt, RepoId = "org/model", Folder = folder, Token = token,
        };

        string withToken = AgentPayloadDigest.Compute(Download("hf_secret", "m"), typeof(DownloadStartRequest));
        string otherToken = AgentPayloadDigest.Compute(Download("hf_other", "m"), typeof(DownloadStartRequest));
        string otherFolder = AgentPayloadDigest.Compute(Download("hf_secret", "n"), typeof(DownloadStartRequest));
        string noToken = AgentPayloadDigest.Compute(Download(null, "m"), typeof(DownloadStartRequest));

        Assert.Equal(withToken, otherToken);
        Assert.NotEqual(withToken, otherFolder);
        Assert.NotEqual(withToken, noToken); // "no token" and "a redacted token" are different payloads
        Assert.Equal(44, withToken.Length); // base64 SHA-256
    }

    [Theory]
    [InlineData("https://127.0.0.1:9443", "https://127.0.0.1:9443/")]
    [InlineData("https://10.0.0.5:9443/", "https://10.0.0.5:9443/")]
    [InlineData("10.0.0.5:9443", "https://10.0.0.5:9443/")]
    [InlineData("http://10.0.0.5:9443", null)]
    [InlineData("https://10.0.0.5:9443/?x=1", null)]
    [InlineData("", null)]
    public void Addresses_AreHttpsBaseUris(string address, string? expected)
    {
        bool ok = AgentClientFactory.TryParseAddress(address, out Uri? uri);

        Assert.Equal(expected is not null, ok);
        Assert.Equal(expected, uri?.AbsoluteUri);
    }

    // ---- Helpers ----

    private static StopRequest Stop(AgentEnvelope e) => new() { OperationId = e.OperationId, IssuedAt = e.IssuedAt, ExpiresAt = e.ExpiresAt };

    private static InfoResponse Info() => new()
    {
        NodeId = NodeId,
        NodeUid = NodeUid,
        AgentVersion = "1.0.0",
        Protocol = Protocol.Range,
        BootId = "boot-1",
        StartedAt = DateTimeOffset.UtcNow,
        Time = DateTimeOffset.UtcNow,
    };

    private static OperationStatus Op(string id, OperationState state, string? error = null) => new()
    {
        Id = id,
        Kind = OperationKinds.Stop,
        Target = "qwen",
        PayloadDigest = "digest",
        State = state,
        StartedAt = DateTimeOffset.UtcNow,
        Error = state == OperationState.Failed && error is not null ? new AgentError { Code = error, Message = error } : null,
    };

    private static AgentOperationReference Reference(string id, string uid, DateTimeOffset issuedAt, DateTimeOffset createdAt) => new()
    {
        OperationId = id,
        NodeId = NodeId,
        NodeUid = uid,
        Action = OperationKinds.Stop,
        Target = "qwen",
        PayloadDigest = "digest",
        IssuedAt = issuedAt,
        ExpiresAt = issuedAt + OperationRetention.EnvelopeLifetime,
        CreatedAt = createdAt,
    };

    private static async Task Json(HttpContext ctx, int status, object value)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = AgentJson.JsonContentType;
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(value, value.GetType(), AgentJson.Options));
    }

    private static Task Error(HttpContext ctx, int status, string code, string message = "error") =>
        Json(ctx, status, new AgentErrorResponse(new AgentError { Code = code, Message = message }));

    private static async Task WriteEventAsync(HttpContext ctx, AgentEvent e)
    {
        await ctx.Response.WriteAsync($"event: {e.EventType}\ndata: {JsonSerializer.Serialize(e, AgentJson.Options)}\n\n");
        await ctx.Response.Body.FlushAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(20);
        }
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mirbadllm-agent-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeMasterIdentity : IMasterIdentity
    {
        public string InstanceId => "instance-1";

        public long Epoch => 3;

        public string BootId => "boot-9";

        public MasterTriple Current => new(InstanceId, Epoch, BootId);

        public bool TakeoverRequested => false;

        public IReadOnlyList<TakeoverRecord> Takeovers => [];

        public Task<MasterTriple> RaiseEpochToAsync(long epoch, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<MasterTriple> CompleteTakeoverAsync(TakeoverRecord record, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>One request as the stub saw it.</summary>
    internal sealed record Recorded(string Method, string Path, string Query, string Protocol, string ConnectionId, IReadOnlyDictionary<string, string> Headers, byte[] Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out string? value) ? value : null;
    }

    /// <summary>
    /// An in-process agent: Kestrel with mutual TLS (the agent certificate, <see cref="PeerValidator.ForMasterClient"/>),
    /// <c>Http1AndHttp2</c> with ALPN by default, recording every request (protocol, connection, headers, body).
    /// </summary>
    private sealed class AgentStub : IAsyncDisposable
    {
        private X509Certificate2 _certificate;
        private int _handshakes;
        private string? _lastClientSpki;

        private AgentStub(X509Certificate2 certificate) => _certificate = certificate;

        public WebApplication App { get; private set; } = null!;

        public Uri BaseUri { get; private set; } = null!;

        public ConcurrentQueue<Recorded> Requests { get; } = new();

        public Func<HttpContext, Recorded, Task> Handler { get; set; } = (ctx, _) => Error(ctx, 404, ErrorCodes.NotFound);

        /// <summary>Full TLS handshakes (Kestrel validates the client certificate once per handshake).</summary>
        public int Handshakes => Volatile.Read(ref _handshakes);

        /// <summary>SPKI of the last client certificate presented.</summary>
        public string? LastClientSpki => Volatile.Read(ref _lastClientSpki);

        /// <summary>The certificate presented on new connections (hot rotation, §10.2 step 3).</summary>
        public X509Certificate2 Certificate
        {
            set => Volatile.Write(ref _certificate, value);
        }

        public static async Task<AgentStub> StartAsync(X509Certificate2 certificate, PeerValidatorSource masterValidators, HttpProtocols protocols, int? maxStreamsPerConnection)
        {
            var stub = new AgentStub(certificate);
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseShutdownTimeout(TimeSpan.FromSeconds(2));
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                if (maxStreamsPerConnection is { } max)
                {
                    kestrel.Limits.Http2.MaxStreamsPerConnection = max;
                }

                kestrel.Listen(IPAddress.Loopback, 0, listen =>
                {
                    listen.Protocols = protocols;
                    listen.UseHttps(new HttpsConnectionAdapterOptions
                    {
                        ServerCertificateSelector = (_, _) => Volatile.Read(ref stub._certificate),
                        ClientCertificateMode = ClientCertificateMode.RequireCertificate,
                        CheckCertificateRevocation = false,
                        ClientCertificateValidation = (cert, chain, errors) =>
                        {
                            Interlocked.Increment(ref stub._handshakes);
                            Volatile.Write(ref stub._lastClientSpki, Spki.Sha256(cert));
                            return masterValidators.ValidateClientCertificate(cert, chain, errors);
                        },
                    });
                });
            });

            WebApplication app = builder.Build();
            app.Run(async ctx =>
            {
                using var buffer = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(buffer);
                var recorded = new Recorded(
                    ctx.Request.Method,
                    ctx.Request.Path.Value ?? "",
                    ctx.Request.QueryString.Value ?? "",
                    ctx.Request.Protocol,
                    ctx.Connection.Id,
                    ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                    buffer.ToArray());
                stub.Requests.Enqueue(recorded);
                await stub.Handler(ctx, recorded);
            });
            await app.StartAsync();
            string address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
            stub.App = app;
            stub.BaseUri = new Uri($"https://127.0.0.1:{new Uri(address).Port}");
            return stub;
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>CA, agent and master certificates, a registry with node <c>dgx</c> pointing at the stub, and the factory.</summary>
    private sealed class Env : IAsyncDisposable
    {
        private readonly ECDsa _agentKey;
        private readonly IssuedCertificate _agentCert;
        private readonly X509Certificate2 _agentTls;
        private readonly IssuedCredential _master;
        private readonly List<IDisposable> _owned = [];

        private Env()
        {
            Ca = CertificateAuthority.Create();
            Trust = TrustAnchorSet.Create([Ca.Certificate]);
            string csr = PkiTestSupport.NewCsr(out _agentKey, NodeId);
            _agentCert = Ca.IssueAgentCertificate(csr, NodeId, ["127.0.0.1"]);
            _agentTls = TlsCredentials.FromPem(_agentCert.CertificatePem, _agentKey.ExportPkcs8PrivateKeyPem());
            _master = Ca.IssueMasterClientCertificate();
            DataDir = NewTempDir();
            Registry = new JsonNodeRegistry(Options.Create(new MirbadLLMOptions { DataPath = DataDir }), TimeProvider.System);
        }

        public CertificateAuthority Ca { get; }

        public TrustAnchorSet Trust { get; }

        public string DataDir { get; }

        public JsonNodeRegistry Registry { get; }

        public FakeMasterIdentity Identity { get; } = new();

        public AgentStub Stub { get; private set; } = null!;

        public AgentClientFactory Factory { get; private set; } = null!;

        public static async Task<Env> StartAsync(
            HttpProtocols protocols = HttpProtocols.Http1AndHttp2,
            int? maxStreamsPerConnection = null,
            string? registeredPin = null,
            AgentClientFactoryOptions? options = null)
        {
            var env = new Env();
            env.Stub = await AgentStub.StartAsync(env._agentTls, new PeerValidatorSource(PeerValidator.ForMasterClient(env.Trust)), protocols, maxStreamsPerConnection);
            await env.Registry.UpsertAsync(new NodeRecord
            {
                Id = NodeId,
                Uid = NodeUid,
                Address = env.Stub.BaseUri.AbsoluteUri,
                Approved = true,
                CertSpkiSha256 = registeredPin ?? env._agentCert.SpkiSha256,
            });
            env.Factory = new AgentClientFactory(env.Registry, env.Identity, env._master, env.Trust, TimeProvider.System, options: options);
            await env.Factory.RefreshAsync();
            return env;
        }

        /// <summary>Disposes <paramref name="resource"/> with the env, after the stub (e.g. a certificate the stub presents).</summary>
        public T Own<T>(T resource)
            where T : IDisposable
        {
            _owned.Add(resource);
            return resource;
        }

        public JsonAgentOperationReferenceStore CreateStore() =>
            new(Options.Create(new MirbadLLMOptions { DataPath = DataDir }), TimeProvider.System);

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            await Stub.DisposeAsync();
            foreach (IDisposable owned in _owned)
            {
                owned.Dispose();
            }

            Registry.Dispose();
            _master.Dispose();
            _agentTls.Dispose();
            _agentCert.Dispose();
            _agentKey.Dispose();
            Trust.Dispose();
            Ca.Dispose();
            try
            {
                Directory.Delete(DataDir, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }
}
