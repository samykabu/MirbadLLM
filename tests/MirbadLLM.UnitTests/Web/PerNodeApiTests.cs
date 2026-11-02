using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Audit;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Recovery;
using MirbadLLM.UnitTests.Auth;
using MirbadLLM.UnitTests.Downloads.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Api;

namespace MirbadLLM.UnitTests.Web;

/// <summary>
/// MN-229 (D106 follow-ups, design §5.6): <c>?node=</c> and <c>node_required</c> on <c>/api/downloads*</c>,
/// <c>/api/model-folders*</c> (with the new GGUF route) and <c>/api/catalog</c>, <c>nodes</c> in <c>/api/status</c>, and the
/// refusal of Ollama downloads (MN-221 deferred). The hosts map the real <see cref="AdminApiEndpoints.MapAdminApi"/> inside
/// the real <see cref="LegacyApiAuditFilter"/> group, over per-node fakes: a downloader and an agent per node, so every
/// assertion can say which node was asked. The last tests boot the real master composition (<see cref="AuthWebFactory"/>).
/// </summary>
public sealed class PerNodeApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static NodeRecord Node(string id, bool approved = true, bool revoked = false, params string[] features) => new()
    {
        Id = id,
        Uid = id + "-uid",
        DisplayName = id.ToUpperInvariant(),
        Address = "https://127.0.0.1:9443",
        Approved = approved,
        Revoked = revoked,
        DefaultVllmImage = id == "dgx" ? null : "vllm/vllm-openai:" + id, // the DGX keeps the configured default, as in P2
        LastCapabilities = new NodeCapabilities
        {
            Protocol = Protocol.Range,
            AgentVersion = "1.0.0",
            Features = features,
            Os = new NodeOsInfo("linux", "test"),
            Arch = "x64",
            Memory = new NodeMemoryInfo(MemoryModel.Unified, 64),
        },
    };

    // ============================================================== single node: exactly P2

    [Fact]
    public async Task ASingleNodeFleetAnswersEveryRouteWithoutNodeExactlyAsAP2Master()
    {
        // The P2 shape: no node registry composed (AdminApiTests' host). The P3 single-DGX master: a registry with the DGX
        // (and a PC waiting for approval, which is not a fleet member, D102).
        await using var p2 = await Host.StartAsync(nodes: null);
        await using var p3 = await Host.StartAsync([Node("dgx"), Node("pc", approved: false)]);

        foreach (Host host in new[] { p2, p3 })
        {
            host.Bootstrap.Seed(new DownloadStatus("Seeded", "o/Seeded", null, DownloadState.Downloading, 5, 10, null, null));
        }

        foreach (string path in new[] { "/api/downloads", "/api/downloads/Seeded", "/api/downloads/missing", "/api/model-folders",
                     "/api/model-folders/ckpt", "/api/model-folders/missing", "/api/catalog", "/api/catalog?currentImage=mine:1" })
        {
            (HttpStatusCode s2, string b2) = await GetAsync(p2.Client, path);
            (HttpStatusCode s3, string b3) = await GetAsync(p3.Client, path);
            Assert.True(s2 == s3 && b2 == b3, $"GET {path}: P2 {(int)s2} {b2} / P3 {(int)s3} {b3}");
        }

        foreach (Host host in new[] { p2, p3 })
        {
            using var post = await host.Client.PostAsJsonAsync("/api/downloads", new DownloadRequest("o/Repo"), Json, Ct);
            Assert.Equal(HttpStatusCode.Created, post.StatusCode);
            Assert.Equal("/api/downloads/Repo", post.Headers.Location?.ToString());
            using var delete = await host.Client.DeleteAsync("/api/downloads/Repo", Ct);
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            Assert.Equal(["o/Repo"], host.Bootstrap.StartCalls.Select(r => r.RepoId));
            Assert.Equal(["Repo"], host.Bootstrap.CancelCalls);
        }

        // Everything went to the DGX: its registered downloader and its agent; no other node was asked.
        Assert.Empty(p3.Downloaders);
        Assert.Equal(p2.Agent("dgx").Calls, p3.Agent("dgx").Calls);
        Assert.Equal(["folders", "inspect:ckpt", "inspect:missing"], p3.Agent("dgx").Calls);
        Assert.False(p3.HasAgent("pc"));

        // Naming the only node is the same call.
        (HttpStatusCode named, string namedBody) = await GetAsync(p3.Client, "/api/downloads?node=dgx");
        Assert.Equal((HttpStatusCode.OK, (await GetAsync(p3.Client, "/api/downloads")).Body), (named, namedBody));
    }

    [Fact]
    public async Task StatusKeepsEveryP2FieldAndAddsTheNodes()
    {
        var model = new ModelDefinition { Id = "qwen", ServedName = "Qwen", ModelPath = "qwen", MemoryBudgetGiB = 20 };
        await using var p2 = await Host.StartAsync(nodes: null, models: [model]);
        await using var p3 = await Host.StartAsync([Node("pc"), Node("dgx"), Node("old", revoked: true), Node("new", approved: false)], models: [model]);
        p3.Cache.Set(new NodeLiveStatus
        {
            NodeId = "pc",
            NodeUid = "pc-uid",
            State = NodeState.Degraded,
            Reasons = ["gpu_external_use_high"],
            Memory = new NodeMemorySnapshot { Model = MemoryModel.Discrete, HostTotalGiB = 32, HostAvailableGiB = 20 },
        });

        JsonObject before = JsonNode.Parse((await GetAsync(p2.Client, "/api/status")).Body)!.AsObject();
        JsonObject after = JsonNode.Parse((await GetAsync(p3.Client, "/api/status")).Body)!.AsObject();

        Assert.Equal(["memory", "models", "router"], before.Select(p => p.Key)); // P2: no nodes member at all
        foreach ((string key, JsonNode? value) in before)
        {
            Assert.True(JsonNode.DeepEquals(value, after[key]), $"/api/status {key} changed: {value?.ToJsonString()} -> {after[key]?.ToJsonString()}");
        }

        JsonArray nodes = after["nodes"]!.AsArray();
        Assert.Equal(["dgx", "new", "pc"], nodes.Select(n => (string)n!["id"]!)); // revoked left out, id order
        JsonNode pc = nodes[2]!;
        Assert.Equal(("PC", true, false, "Degraded", "gpu_external_use_high", 20.0),
            ((string)pc["displayName"]!, (bool)pc["approved"]!, (bool)pc["draining"]!, (string)pc["state"]!, (string)pc["reasons"]![0]!,
                (double)pc["memory"]!["hostAvailableGiB"]!));
        Assert.Equal((false, (string?)null), ((bool)nodes[1]!["approved"]!, nodes[1]!["state"]?.GetValue<string>())); // not in the cache yet
    }

    // ============================================================== two nodes

    public static TheoryData<string, string> EveryPerNodeRoute => new()
    {
        { "GET", "/api/downloads" },
        { "POST", "/api/downloads" },
        { "GET", "/api/downloads/Repo" },
        { "DELETE", "/api/downloads/Repo" },
        { "GET", "/api/model-folders" },
        { "GET", "/api/model-folders/ckpt" },
        { "GET", "/api/model-folders/ckpt/gguf" },
        { "GET", "/api/catalog" },
    };

    [Theory]
    [MemberData(nameof(EveryPerNodeRoute))]
    public async Task TwoNodesWithoutNodeAre400NodeRequiredAndNothingIsAsked(string method, string path)
    {
        await using var host = await Host.StartAsync([Node("dgx"), Node("pc", features: AgentFeatures.GgufInspect)]);

        using var response = await host.SendAsync(method, path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.NodeRequired, await CodeOf(response));
        Assert.Empty(host.Bootstrap.StartCalls);
        Assert.Empty(host.Bootstrap.CancelCalls);
        Assert.Empty(host.Downloaders);
        Assert.Empty(host.AllAgentCalls);
    }

    [Theory]
    [MemberData(nameof(EveryPerNodeRoute))]
    public async Task UnknownRevokedOrMalformedNodesAreRefusedAndNothingIsAsked(string method, string path)
    {
        await using var host = await Host.StartAsync([Node("dgx"), Node("pc"), Node("old", revoked: true), Node("new", approved: false)]);
        string sep = path.Contains('?', StringComparison.Ordinal) ? "&" : "?";

        foreach ((string node, HttpStatusCode status, string code) in new[]
                 {
                     ("lab", HttpStatusCode.NotFound, ErrorCodes.NotFound),
                     ("old", HttpStatusCode.NotFound, ErrorCodes.NotFound),
                     // Not approved: nothing is sent to it, so a download never hands it the Hugging Face token (MN-230).
                     ("new", HttpStatusCode.Conflict, ErrorCodes.InvalidState),
                     ("Not_A_Node!", HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest),
                 })
        {
            using var response = await host.SendAsync(method, path + sep + "node=" + Uri.EscapeDataString(node));
            Assert.Equal(status, response.StatusCode);
            Assert.Equal(code, await CodeOf(response));
        }

        Assert.Empty(host.Bootstrap.StartCalls);
        Assert.Empty(host.Downloaders);
        Assert.Empty(host.AllAgentCalls);
    }

    [Fact]
    public async Task TwoNodesWithNodeDownloadOnThatNode()
    {
        await using var host = await Host.StartAsync([Node("dgx"), Node("pc")]);

        using (var post = await host.Client.PostAsJsonAsync("/api/downloads?node=pc", new DownloadRequest("o/Repo"), Json, Ct))
        {
            Assert.Equal(HttpStatusCode.Created, post.StatusCode);
            Assert.Equal("/api/downloads/Repo?node=pc", post.Headers.Location?.ToString());
        }

        FakeHuggingFaceDownloader pc = host.Downloaders["pc"];
        Assert.Equal(["o/Repo"], pc.StartCalls.Select(r => r.RepoId));
        Assert.Empty(host.Bootstrap.StartCalls);

        var listed = JsonSerializer.Deserialize<List<DownloadStatus>>((await GetAsync(host.Client, "/api/downloads?node=pc")).Body, Json)!;
        Assert.Equal("Repo", Assert.Single(listed).Folder);
        Assert.Equal("[]", (await GetAsync(host.Client, "/api/downloads?node=dgx")).Body); // the DGX's own (registered) downloader
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(host.Client, "/api/downloads/Repo?node=pc")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(host.Client, "/api/downloads/Repo?node=dgx")).Status);

        using (var delete = await host.Client.DeleteAsync("/api/downloads/Repo?node=pc", Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        Assert.Equal(["Repo"], pc.CancelCalls);
        Assert.Empty(host.Bootstrap.CancelCalls);
        Assert.Equal(["pc"], host.Downloaders.Keys); // the DGX is never served by a per-node downloader
    }

    [Fact]
    public async Task ANodeThatCannotBeReachedAnswers503OnEveryDownloadRouteNotAServerError()
    {
        // MN-230: the list, read and cancel routes had no catch, so NodeUnavailableException (and a missing client)
        // reached the error handler and the caller got 500 where the folder routes answer 503.
        await using var host = await Host.StartAsync([Node("dgx"), Node("pc")]);
        using (var post = await host.Client.PostAsJsonAsync("/api/downloads?node=pc", new DownloadRequest("o/Repo"), Json, Ct))
        {
            Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        }

        host.Downloaders["pc"].ThrowOnRead = new NodeUnavailableException("The agent of node pc is unreachable.") { NodeId = "pc", Reason = NodeUnavailableReason.NotServing };

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await GetAsync(host.Client, "/api/downloads?node=pc")).Status);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await GetAsync(host.Client, "/api/downloads/Repo?node=pc")).Status);
        using var delete = await host.Client.DeleteAsync("/api/downloads/Repo?node=pc", Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, delete.StatusCode);
    }

    [Fact]
    public async Task TwoNodesWithNodeFoldersAndCatalogComeFromThatNode()
    {
        await using var host = await Host.StartAsync([Node("dgx"), Node("pc")]);

        var folders = JsonSerializer.Deserialize<List<ModelFolderSummary>>((await GetAsync(host.Client, "/api/model-folders?node=pc")).Body, Json)!;
        Assert.Equal("pc-arch", Assert.Single(folders).Architecture);
        var inspection = JsonSerializer.Deserialize<ModelFolderInspection>((await GetAsync(host.Client, "/api/model-folders/ckpt?node=pc")).Body, Json)!;
        Assert.Equal("pc-arch", inspection.Architecture);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(host.Client, "/api/model-folders/missing?node=pc")).Status);
        var catalog = JsonSerializer.Deserialize<VllmCatalog>((await GetAsync(host.Client, "/api/catalog?node=pc&currentImage=mine:1")).Body, Json)!;
        Assert.Equal(["mine:1", "vllm/vllm-openai:pc"], catalog.Images);

        Assert.Equal(["folders", "inspect:ckpt", "inspect:missing"], host.Agent("pc").Calls);
        Assert.False(host.HasAgent("dgx"));

        // A node the status cache knows is not serving: 503, not an empty list.
        host.Cache.Set(new NodeLiveStatus { NodeId = "pc", NodeUid = "pc-uid", State = NodeState.Offline });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await GetAsync(host.Client, "/api/model-folders?node=pc")).Status);
    }

    // ============================================================== GGUF route

    [Fact]
    public async Task GgufAnswersTheNodesSummaryOnlyForANodeReportingGgufInspect()
    {
        await using var host = await Host.StartAsync([Node("dgx"), Node("pc", features: AgentFeatures.GgufInspect)]);

        (HttpStatusCode status, string body) = await GetAsync(host.Client, "/api/model-folders/planner/gguf?node=pc&file=model.gguf");
        Assert.Equal(HttpStatusCode.OK, status);
        var summary = JsonSerializer.Deserialize<GgufSummary>(body, Json)!;
        Assert.Equal(("planner", "model.gguf", "qwen35", 24), (summary.Folder, summary.File, summary.Architecture, summary.BlockCount));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(host.Client, "/api/model-folders/planner/gguf?node=pc")).Status);
        Assert.Equal(["gguf:planner:model.gguf", "gguf:planner:"], host.Agent("pc").Calls); // no file = the folder's only GGUF

        // The DGX reports P2's features only: 501 not_supported, nothing sent to its agent.
        using var dgx = await host.SendAsync("GET", "/api/model-folders/planner/gguf?node=dgx");
        Assert.Equal(HttpStatusCode.NotImplemented, dgx.StatusCode);
        Assert.Equal(ErrorCodes.NotSupported, await CodeOf(dgx));
        Assert.False(host.HasAgent("dgx"));
    }

    [Fact]
    public async Task GgufOnASingleNodeNeedsNoNodeAndTheLiveCapabilitiesDecide()
    {
        await using var host = await Host.StartAsync([Node("pc")]);
        using (var refused = await host.SendAsync("GET", "/api/model-folders/planner/gguf"))
        {
            Assert.Equal(HttpStatusCode.NotImplemented, refused.StatusCode); // the stored report lacks the flag
        }

        host.Cache.Set(new NodeLiveStatus
        {
            NodeId = "pc",
            NodeUid = "pc-uid",
            State = NodeState.Online,
            Capabilities = Node("pc", features: AgentFeatures.GgufInspect).LastCapabilities,
        });
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(host.Client, "/api/model-folders/planner/gguf")).Status);
        Assert.Equal(["gguf:planner:"], host.Agent("pc").Calls);
    }

    [Fact]
    public async Task GgufKeepsTheAgentsRefusalsAndAnUnreachableOrIdleNodeIs503()
    {
        await using var host = await Host.StartAsync([Node("dgx"), Node("pc", features: AgentFeatures.GgufInspect)]);
        FakeNodeAgent pc = host.Agent("pc");

        foreach ((Exception failure, HttpStatusCode status, string? code) in new (Exception, HttpStatusCode, string?)[]
                 {
                     (Agent(ErrorCodes.NotFound, "no such file"), HttpStatusCode.NotFound, ErrorCodes.NotFound),
                     (Agent(ErrorCodes.InvalidRequest, "two GGUFs: a.gguf, b.gguf"), HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest),
                     (Agent(ErrorCodes.GgufInvalid, "not a GGUF"), HttpStatusCode.UnprocessableEntity, ErrorCodes.GgufInvalid),
                     (Agent(ErrorCodes.NotSupported, "off"), HttpStatusCode.NotImplemented, ErrorCodes.NotSupported),
                     (Agent(ErrorCodes.InternalError, "boom"), HttpStatusCode.ServiceUnavailable, null),
                     (new AgentUnreachableException("connection refused"), HttpStatusCode.ServiceUnavailable, null),
                 })
        {
            pc.GgufFailure = failure;
            using var response = await host.SendAsync("GET", "/api/model-folders/planner/gguf?node=pc");
            Assert.Equal(status, response.StatusCode);
            Assert.Equal(code, await CodeOf(response));
            if (failure is AgentException agent && code is not null)
            {
                Assert.Contains(agent.Error.Message, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
            }
        }

        int calls = pc.Calls.Count;
        host.Cache.Set(new NodeLiveStatus { NodeId = "pc", NodeUid = "pc-uid", State = NodeState.Offline });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await GetAsync(host.Client, "/api/model-folders/planner/gguf?node=pc")).Status);
        Assert.Equal(calls, pc.Calls.Count); // not sent
    }

    // ============================================================== Ollama downloads (MN-221 deferred)

    private static readonly DownloadRequest OllamaPlanner = new("guoxuter/ov_intent_analysis_sft:v7_q8")
    {
        Source = ModelSourceKind.Ollama,
        Ollama = new OllamaSource(OllamaSource.DefaultRegistry, "guoxuter/ov_intent_analysis_sft", "v7_q8"),
    };

    [Fact]
    public async Task AnOllamaSourceIsRefused501OnANodeWithoutTheFeatureAndNothingStarts()
    {
        await using var single = await Host.StartAsync([Node("dgx")]);
        await using var two = await Host.StartAsync([Node("dgx"), Node("pc")]);

        foreach ((Host host, string path, string node) in new[] { (single, "/api/downloads", "dgx"), (two, "/api/downloads?node=pc", "pc") })
        {
            using var response = await host.Client.PostAsJsonAsync(path, OllamaPlanner, Json, Ct);
            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
            Assert.Equal(ErrorCodes.NotSupported, await CodeOf(response));
            Assert.Contains($"Node '{node}' does not support Ollama-registry downloads", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        // Two nodes without ?node=: node_required comes first.
        using (var unnamed = await two.Client.PostAsJsonAsync("/api/downloads", OllamaPlanner, Json, Ct))
        {
            Assert.Equal(ErrorCodes.NodeRequired, await CodeOf(unnamed));
        }

        Assert.Empty(single.Bootstrap.StartCalls);
        Assert.Empty(two.Bootstrap.StartCalls);
        Assert.All(two.Downloaders.Values, d => Assert.Empty(d.StartCalls));

        // The same body as a Hugging Face source starts (the refusal is the source kind's, not the route's).
        using var hf = await single.Client.PostAsJsonAsync("/api/downloads", OllamaPlanner with { Source = ModelSourceKind.HuggingFace, Ollama = null }, Json, Ct);
        Assert.Equal(HttpStatusCode.Created, hf.StatusCode);
    }

    [Fact]
    public async Task AnOllamaSourceIsRefusedEvenForANodeReportingTheFeatureSinceThisMasterDoesNotForwardIt()
    {
        await using var host = await Host.StartAsync([Node("dgx"), Node("pc", features: AgentFeatures.DownloadsOllama)]);

        using var response = await host.Client.PostAsJsonAsync("/api/downloads?node=pc", OllamaPlanner, Json, Ct);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Equal(ErrorCodes.NotSupported, await CodeOf(response));
        Assert.Contains("does not start Ollama-registry downloads", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Empty(host.Downloaders.GetValueOrDefault("pc")?.StartCalls ?? []);
    }

    // ============================================================== audit

    [Fact]
    public async Task TheAuditLinesNameTheNodeAndRecordTheRefusals()
    {
        await using var two = await Host.StartAsync([Node("dgx"), Node("pc")]);
        await using var single = await Host.StartAsync([Node("dgx")]);

        Assert.Equal(HttpStatusCode.Created, (await two.Client.PostAsJsonAsync("/api/downloads?node=pc", new DownloadRequest("o/Repo"), Json, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await two.Client.DeleteAsync("/api/downloads/Repo?node=pc", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await two.Client.PostAsJsonAsync("/api/downloads", new DownloadRequest("o/Repo"), Json, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotImplemented, (await two.Client.PostAsJsonAsync("/api/downloads?node=pc", OllamaPlanner, Json, Ct)).StatusCode);
        using (await two.Client.GetAsync("/api/model-folders/ckpt/gguf?node=pc", Ct))
        {
        }

        Assert.Equal(HttpStatusCode.Created, (await single.Client.PostAsJsonAsync("/api/downloads", new DownloadRequest("o/Repo"), Json, Ct)).StatusCode);

        var lines = two.Audit.Lines;
        Assert.Equal(4, lines.Count); // the GGUF read is a GET: no line
        Assert.Equal((AuditActions.Download, "pc", null, AuditResults.Succeeded, null, "start"),
            (lines[0].Action, lines[0].NodeId, lines[0].Target, lines[0].Result, lines[0].ErrorCode, lines[0].Detail));
        Assert.Equal((AuditActions.Download, "pc", "Repo", AuditResults.Succeeded, "cancel"),
            (lines[1].Action, lines[1].NodeId, lines[1].Target, lines[1].Result, lines[1].Detail));
        Assert.Equal((null, AuditResults.Refused, ErrorCodes.NodeRequired, "start; HTTP 400"),
            (lines[2].NodeId, lines[2].Result, lines[2].ErrorCode, lines[2].Detail));
        Assert.Equal(("pc", AuditResults.Failed, ErrorCodes.NotSupported), (lines[3].NodeId, lines[3].Result, lines[3].ErrorCode));

        AuditRecord p2 = Assert.Single(single.Audit.Lines);
        Assert.Equal((AuditActions.Download, null, AuditResults.Succeeded), (p2.Action, p2.NodeId, p2.Result)); // P2's line
    }

    // ============================================================== the real master composition

    private static readonly Regex AntiforgeryToken = new("""name="__RequestVerificationToken" value="([^"]+)""", RegexOptions.Compiled);

    [Fact]
    public async Task TheRealMasterNeedsTheAdminLoginForThePerNodeRoutesAndTheDgxAnswersWithoutNode()
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (string path in new[] { "/api/model-folders/ckpt/gguf", "/api/model-folders/ckpt/gguf?node=dgx", "/api/downloads?node=dgx", "/api/catalog?node=dgx" })
        {
            using var anonymous = await client.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        using (var anonymousPost = await client.PostAsJsonAsync("/api/downloads?node=dgx", OllamaPlanner, Json, Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousPost.StatusCode);
        }

        await LoginAsync(client);

        // The single DGX: no ?node= needed, the same answers with it. (This host starts no node monitor, so the DGX's agent
        // client is not built and its folder read is the 503 of a node that cannot answer, with or without ?node=.)
        foreach ((string path, HttpStatusCode expected) in new[] { ("/api/catalog", HttpStatusCode.OK), ("/api/model-folders", HttpStatusCode.ServiceUnavailable) })
        {
            (HttpStatusCode plain, string plainBody) = await GetAsync(client, path);
            (HttpStatusCode named, string namedBody) = await GetAsync(client, path + "?node=dgx");
            Assert.Equal(expected, plain);
            Assert.Equal((plain, plainBody), (named, namedBody));
        }

        // The DGX agent reports no gguf.inspect: 501, and an Ollama source is refused and audited.
        using (var gguf = await client.GetAsync("/api/model-folders/ckpt/gguf", Ct))
        {
            Assert.Equal(HttpStatusCode.NotImplemented, gguf.StatusCode);
            Assert.Equal(ErrorCodes.NotSupported, await CodeOf(gguf));
        }

        using (var ollama = await client.PostAsJsonAsync("/api/downloads?node=dgx", OllamaPlanner, Json, Ct))
        {
            Assert.Equal(HttpStatusCode.NotImplemented, ollama.StatusCode);
        }

        using var audit = JsonDocument.Parse((await GetAsync(client, "/api/audit?max=5")).Body);
        JsonElement line = audit.RootElement.EnumerateArray().First(l => l.GetProperty("action").GetString() == AuditActions.Download);
        Assert.Equal(("dgx", AuditResults.Failed, ErrorCodes.NotSupported),
            (line.GetProperty("nodeId").GetString(), line.GetProperty("result").GetString(), line.GetProperty("errorCode").GetString()));
    }

    private static async Task LoginAsync(HttpClient client)
    {
        using var page = await client.GetAsync("/login", Ct);
        page.EnsureSuccessStatusCode();
        var match = AntiforgeryToken.Match(await page.Content.ReadAsStringAsync(Ct));
        Assert.True(match.Success);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = match.Groups[1].Value,
            ["password"] = "correct-password",
            ["returnUrl"] = "",
        });
        using var login = await client.PostAsync("/login/submit", form, Ct);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
    }

    // ============================================================== helpers

    private static AgentException Agent(string code, string message) =>
        new(ErrorMappings.AgentStatus(code), new AgentError { Code = code, Message = message }, "pc");

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync(Ct);
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly FakeAgents _agents;
        private readonly string _dataDir;

        private Host(WebApplication app, FakeAgents agents, string dataDir)
        {
            _app = app;
            _agents = agents;
            _dataDir = dataDir;
        }

        public HttpClient Client => field ??= _app.GetTestClient();

        public FakeHuggingFaceDownloader Bootstrap { get; } = new();

        /// <summary>Per-node downloaders handed out (never the bootstrap node's, which is <see cref="Bootstrap"/>).</summary>
        public Dictionary<string, FakeHuggingFaceDownloader> Downloaders { get; } = new(StringComparer.Ordinal);

        public FakeStatusCache Cache { get; } = new();

        public RecordingAuditLog Audit { get; } = new();

        public IEnumerable<string> AllAgentCalls => _agents.All.SelectMany(a => a.Calls);

        public FakeNodeAgent Agent(string nodeId) => _agents.Get(nodeId);

        public bool HasAgent(string nodeId) => _agents.Has(nodeId);

        public static async Task<Host> StartAsync(NodeRecord[]? nodes, IReadOnlyList<ModelDefinition>? models = null)
        {
            string dataDir = Path.Combine(Path.GetTempPath(), "mirbadllm-pernode-" + Guid.NewGuid().ToString("N"));
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
            var registry = new FakeModelRegistry(models ?? []);
            builder.Services.AddSingleton<IModelRegistry>(registry);
            builder.Services.AddSingleton<IModelRuntime>(new FakeModelRuntime());
            builder.Services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(new MemorySnapshot(128, 64)));
            builder.Services.AddSingleton<IModelMetricsReader>(new FakeModelMetricsReader());
            builder.Services.AddSingleton<IRouterStats>(new FakeRouterStats([]));
            builder.Services.AddSingleton<IHuggingFaceRepoInspector>(new FakeHuggingFaceRepoInspector());

            var nodeRegistry = new NodeTable(nodes ?? []);
            var agents = new FakeAgents();
            var settings = Options.Create(new MirbadLLMOptions { DataPath = dataDir });
            var runner = new AgentOperationRunner(agents, new JsonAgentOperationReferenceStore(settings, TimeProvider.System), nodeRegistry, TimeProvider.System);
            builder.Services.AddSingleton(sp => new RemoteModelFolders(agents, runner, registry));
            builder.Services.AddSingleton<IAgentClientFactory>(agents);
            builder.Services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<HostRef>().Host!.Audit);

            var hostRef = new HostRef();
            builder.Services.AddSingleton(hostRef);
            builder.Services.AddSingleton<IHuggingFaceDownloader>(sp => sp.GetRequiredService<HostRef>().Host!.Bootstrap);
            if (nodes is null)
            {
                // P2's shape: no registry, no status cache, no per-node resources; the catalog reads an empty registry.
                builder.Services.AddSingleton(new RemoteVllmCatalogService(new NodeTable([]), settings));
            }
            else
            {
                builder.Services.AddSingleton<INodeRegistry>(nodeRegistry);
                builder.Services.AddSingleton<INodeStatusCache>(sp => sp.GetRequiredService<HostRef>().Host!.Cache);
                builder.Services.AddSingleton(sp => new RemoteVllmCatalogService(nodeRegistry, settings, sp.GetRequiredService<INodeStatusCache>()));
                builder.Services.AddSingleton<IRestoreNodeResources>(sp => new PerNodeResources(sp.GetRequiredService<HostRef>().Host!));
            }

            var app = builder.Build();
            app.MapGroup("").AddEndpointFilter(new LegacyApiAuditFilter()).MapAdminApi();
            var host = new Host(app, agents, dataDir);
            hostRef.Host = host;
            await app.StartAsync(Ct);
            return host;
        }

        public async Task<HttpResponseMessage> SendAsync(string method, string path)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method == "POST")
            {
                request.Content = new StringContent("""{"repoId":"o/Repo"}""", Encoding.UTF8, "application/json");
            }

            return await Client.SendAsync(request, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync(Ct);
            await _app.DisposeAsync();
            try
            {
                Directory.Delete(_dataDir, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private sealed class HostRef
    {
        public Host? Host { get; set; }
    }

    private sealed class PerNodeResources(Host host) : IRestoreNodeResources
    {
        public ICheckpointFolders FoldersFor(string nodeId) => throw new NotSupportedException();

        public IHuggingFaceDownloader DownloaderFor(string nodeId)
        {
            Assert.NotEqual("dgx", nodeId); // the endpoint serves the bootstrap node with the registered downloader
            lock (host.Downloaders)
            {
                return host.Downloaders.TryGetValue(nodeId, out var d) ? d : host.Downloaders[nodeId] = new FakeHuggingFaceDownloader();
            }
        }
    }

    internal sealed class RecordingAuditLog : IAuditLog
    {
        private readonly List<AuditRecord> _lines = [];

        public List<AuditRecord> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public Task WriteAsync(AuditRecord record, CancellationToken ct = default)
        {
            lock (_lines)
            {
                _lines.Add(record);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditRecord>> ReadAsync(AuditQuery query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditRecord>>(Lines);
    }

    private sealed class NodeTable(NodeRecord[] nodes) : INodeRegistry
    {
        public Task<IReadOnlyList<NodeRecord>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NodeRecord>>(nodes);

        public Task<NodeRecord?> GetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(nodes.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.Ordinal)));

        public Task UpsertAsync(NodeRecord node, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<NodeRecord?> UpdateAsync(string id, Func<NodeRecord, NodeRecord> update, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> RevokeAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<JoinTokenIssue> CreateJoinTokenAsync(string nodeId, TimeSpan lifetime, JoinTokenPurpose purpose, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<JoinTokenConsumption> TryConsumeJoinTokenAsync(string token, string nodeId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<JoinTokenRecord>> ListJoinTokensAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    internal sealed class FakeStatusCache : INodeStatusCache
    {
        private readonly Dictionary<string, NodeLiveStatus> _nodes = new(StringComparer.Ordinal);

        public long Version { get; private set; }

        public void Set(NodeLiveStatus status)
        {
            lock (_nodes)
            {
                _nodes[status.NodeId] = status;
                Version++;
            }
        }

        public IReadOnlyList<NodeLiveStatus> ListNodes()
        {
            lock (_nodes)
            {
                return [.. _nodes.Values];
            }
        }

        public NodeLiveStatus? GetNode(string nodeId)
        {
            lock (_nodes)
            {
                return _nodes.GetValueOrDefault(nodeId);
            }
        }

        public NodeModelRuntime? GetRuntime(string modelId) => null;

        public IReadOnlyList<NodeModelRuntime> ListRuntimes(string? nodeId = null) => [];

        public ServedModelRoute? ResolveServedName(string servedName) => null;

        public Task RefreshModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RefreshNodeAsync(string nodeId, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>One agent client per node id, made on first use (so a test can say a node was never asked).</summary>
    private sealed class FakeAgents : IAgentClientFactory
    {
        private readonly Dictionary<string, FakeNodeAgent> _agents = new(StringComparer.Ordinal);

        public IEnumerable<FakeNodeAgent> All
        {
            get
            {
                lock (_agents)
                {
                    return [.. _agents.Values];
                }
            }
        }

        public bool Has(string nodeId)
        {
            lock (_agents)
            {
                return _agents.ContainsKey(nodeId);
            }
        }

        public FakeNodeAgent Get(string nodeId)
        {
            lock (_agents)
            {
                return _agents.TryGetValue(nodeId, out var agent) ? agent : _agents[nodeId] = new FakeNodeAgent(nodeId);
            }
        }

        public IAgentClient GetClient(string nodeId) => NodeAgentProxy.Create(Get(nodeId));

        public Task<IAgentClientLease> CreateFreshClientAsync(string nodeId, CancellationToken ct = default) => throw new NotSupportedException();

        public void RebuildHandlers(string nodeId) => throw new NotSupportedException();

        public void SetNegotiatedProtocol(string nodeId, int version) => throw new NotSupportedException();
    }

    /// <summary>A node's agent: folder reads and GGUF summaries tagged with the node id; every call recorded.</summary>
    internal sealed class FakeNodeAgent(string nodeId)
    {
        private readonly List<string> _calls = [];

        public string NodeId => nodeId;

        public Exception? GgufFailure { get; set; }

        public List<string> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        public void Record(string call)
        {
            lock (_calls)
            {
                _calls.Add(call);
            }
        }

        public IReadOnlyList<ModelFolderEntry> Folders() =>
            [new ModelFolderEntry { Folder = "ckpt", HasConfig = true, WeightsGiB = 9.5, Architecture = nodeId + "-arch", DetectedFormat = "BF16" }];

        public ModelFolderInspection? Inspect(string folder) => folder == "ckpt"
            ? new ModelFolderInspection("ckpt", nodeId + "-arch", 4096, "BF16", 9.5, false, new ModelFolderSuggestion("Ckpt", null, null, null, 20, "test"))
            : null;

        public GgufSummary Gguf(string folder, string? file) => GgufFailure is { } failure
            ? throw failure
            : new GgufSummary
            {
                Folder = folder,
                File = file ?? "model.gguf",
                GgufVersion = 3,
                Architecture = "qwen35",
                BlockCount = 24,
                TensorBytes = 811_843_072,
                FileBytes = 811_843_072,
            };
    }

#pragma warning disable CA1852 // DispatchProxy derives from it at run time
    public class NodeAgentProxy : DispatchProxy
#pragma warning restore CA1852
    {
        private FakeNodeAgent? _agent;

        internal static IAgentClient Create(FakeNodeAgent agent)
        {
            IAgentClient client = DispatchProxy.Create<IAgentClient, NodeAgentProxy>();
            ((NodeAgentProxy)(object)client)._agent = agent;
            return client;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            FakeNodeAgent agent = _agent!;
            switch (targetMethod?.Name)
            {
                case "get_NodeId":
                    return agent.NodeId;
                case nameof(IAgentClient.ListModelFoldersAsync):
                    agent.Record("folders");
                    return Task.FromResult(agent.Folders());
                case nameof(IAgentClient.InspectModelFolderAsync):
                    agent.Record("inspect:" + args![0]);
                    return Task.FromResult(agent.Inspect((string)args[0]!));
                case nameof(IAgentClient.InspectGgufAsync):
                    agent.Record($"gguf:{args![0]}:{args[1]}");
                    try
                    {
                        return Task.FromResult(agent.Gguf((string)args[0]!, (string?)args[1]));
                    }
                    catch (Exception ex)
                    {
                        return Task.FromException<GgufSummary>(ex);
                    }

                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }
}
