using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Recovery;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Contracts;

/// <summary>
/// Shape checks of the frozen P1 contracts (MN-001, plan §2): the route catalogue is complete and consistent, error codes
/// are unique, protocol helpers behave as the design says, and the golden fixtures (mutation envelope, <c>nodes.json</c>
/// v1, <c>models.json</c> v2, operation-log lines, <c>master.json</c>) round-trip. Per-DTO golden files are MN-003.
/// </summary>
public sealed partial class ContractShapeTests
{
    /// <summary>The options <c>JsonModelRegistry</c> writes <c>models.json</c> with today.</summary>
    private static readonly JsonSerializerOptions RegistryFileOptions = new() { WriteIndented = true };

    // ---------------------------------------------------------------- route catalogue

    [Fact]
    public void Routes_EveryRouteHasRequestAndResponseTypes()
    {
        foreach (var route in AllRoutes())
        {
            Assert.NotNull(route.RequestType);
            Assert.NotNull(route.ResponseType);

            switch (route.Input)
            {
                case AgentRouteInput.None:
                    Assert.Equal(typeof(NoBody), route.RequestType);
                    break;
                case AgentRouteInput.Raw:
                    Assert.Equal(typeof(RawBody), route.RequestType);
                    break;
                case AgentRouteInput.Query:
                case AgentRouteInput.Body:
                    Assert.False(IsMarker(route.RequestType), $"{route.Name}: a {route.Input} route needs a DTO request type.");
                    break;
            }

            switch (route.Output)
            {
                case AgentRouteOutput.Json:
                    Assert.False(IsMarker(route.ResponseType), $"{route.Name}: a JSON route needs a DTO response type.");
                    break;
                case AgentRouteOutput.EventStream:
                    Assert.True(route.ResponseType == typeof(AgentEvent) || route.ResponseType == typeof(LogLine), route.Name);
                    break;
                case AgentRouteOutput.Raw:
                    Assert.Equal(typeof(RawBody), route.ResponseType);
                    break;
            }
        }
    }

    [Fact]
    public void Routes_NamesAndMethodPathPairsAreUnique()
    {
        var routes = AllRoutes().ToList();
        Assert.Equal(routes.Count, routes.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(routes.Count, routes.Select(r => r.Method + " " + r.Path).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Routes_MutationsCarryTheEnvelopeAndOneKindEach()
    {
        foreach (var route in AllRoutes())
        {
            bool envelope = typeof(AgentMutation).IsAssignableFrom(route.RequestType);
            Assert.True(envelope == route.IsMutation, $"{route.Name}: envelope request ⇔ operation kind.");
            if (route.IsMutation)
            {
                Assert.NotEqual(AgentHttpMethods.Get, route.Method);
                Assert.Contains(ErrorCodes.StaleMaster, route.ErrorCodes);
                Assert.Contains(ErrorCodes.OperationExpired, route.ErrorCodes);
                Assert.Contains(ErrorCodes.OperationIdConflict, route.ErrorCodes);
            }
            else
            {
                Assert.Null(route.ResultType);
            }
        }

        var kinds = AgentRoutes.All.Where(r => r.IsMutation).Select(r => r.OperationKind!).ToList();
        Assert.Equal(kinds.Count, kinds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ConstantValues(typeof(OperationKinds)).Order(StringComparer.Ordinal), kinds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Routes_HostServiceKindsAreClassifiedForRecovery()
    {
        var hostKinds = AgentRoutes.All
            .Where(r => r.IsMutation && r.CallClass == AgentCallClass.HostService)
            .Select(r => r.OperationKind!)
            .Order(StringComparer.Ordinal);

        Assert.Equal(hostKinds, OperationKinds.DesiredState.Concat(OperationKinds.Unverifiable).Order(StringComparer.Ordinal));
        Assert.Empty(OperationKinds.DesiredState.Intersect(OperationKinds.Unverifiable, StringComparer.Ordinal));
    }

    [Fact]
    public void Routes_ErrorCodesAreDefinedConstants()
    {
        var known = ConstantValues(typeof(ErrorCodes)).ToHashSet(StringComparer.Ordinal);
        foreach (var route in AllRoutes())
        {
            foreach (string code in route.ErrorCodes)
            {
                Assert.True(known.Contains(code), $"{route.Name}: unknown error code {code}.");
            }

            Assert.Equal(route.ErrorCodes.Count, route.ErrorCodes.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void Routes_PathsAreRelativeTemplatesThatExpand()
    {
        foreach (var route in AllRoutes())
        {
            Assert.StartsWith("/", route.Path, StringComparison.Ordinal);
            Assert.DoesNotContain(Protocol.BasePath, route.Path, StringComparison.Ordinal);
            Assert.Equal(route.Path.Count(c => c == '{'), route.Path.Count(c => c == '}'));

            string[] values = route.PathParameters.Select(p => "v-" + p).ToArray();
            string expanded = route.Expand(values);
            Assert.DoesNotContain("{", expanded, StringComparison.Ordinal);
        }

        Assert.Equal("/runtimes/qwen36-35b/launch", AgentRoutes.RuntimeLaunch.Expand("qwen36-35b"));
        Assert.Equal("/proxy/qwen/v1/chat/completions", AgentRoutes.Proxy.Expand("qwen", "v1/chat/completions"));
        Assert.Equal("/model-folders/a%20b/delete", AgentRoutes.ModelFolderDelete.Expand("a b"));
        Assert.Equal(["modelId", "path"], AgentRoutes.Proxy.PathParameters);
        Assert.Throws<ArgumentException>(() => AgentRoutes.RuntimeGet.Expand());
    }

    [Fact]
    public void Routes_OnlyIdentityAndPkiAreAllowedWhileBlocked()
    {
        var blocked = AgentRoutes.All.Where(r => r.AllowedWhenBlocked).Select(r => r.Name).ToList();
        Assert.All(blocked, name => Assert.True(name is "Info" or "Capabilities" || name.StartsWith("Pki", StringComparison.Ordinal), name));
        Assert.Contains("Info", blocked);
        Assert.Contains("Capabilities", blocked);
        Assert.Equal(7, blocked.Count(n => n.StartsWith("Pki", StringComparison.Ordinal)));
    }

    [Fact]
    public void Routes_OnlyInfoAndEnrollSkipTheProtocolHeader_AndOnlyTheProxyUsesTheDataChannel()
    {
        Assert.Equal(["Info", "Enroll"], AllRoutes().Where(r => !r.RequiresProtocolHeader).Select(r => r.Name));
        Assert.Equal(["Proxy"], AllRoutes().Where(r => r.Channel == AgentChannel.Data).Select(r => r.Name));
        Assert.All(AllRoutes(), r => Assert.NotNull(AgentCallPolicies.For(r.CallClass)));
    }

    [Fact]
    public void Routes_EveryDesignedAgentEndpointIsCatalogued()
    {
        // design §4.3, P1 subset: method + path of every row.
        string[] expected =
        [
            "GET /info", "GET /capabilities", "GET /status", "GET /events", "GET /telemetry", "GET /gpu/idle",
            "GET /runtimes", "GET /runtimes/{modelId}", "POST /runtimes/{modelId}/launch", "POST /runtimes/{modelId}/stop",
            "GET /runtimes/{modelId}/launch-config", "GET /runtimes/{modelId}/logs", "* /proxy/{modelId}/{**path}",
            "GET /images", "POST /images/pull", "GET /model-folders", "GET /model-folders/{folder}",
            "POST /model-folders/{folder}/delete", "POST /downloads", "GET /downloads", "GET /downloads/{folder}",
            "POST /downloads/{folder}/cancel", "GET /operations/{operationId}", "GET /operations",
            "POST /operations/{operationId}/cancel", "GET /host/services", "POST /host/services/{name}/start",
            "POST /host/services/{name}/stop", "POST /host/services/{name}/restart", "POST /host/services/{name}/autostart",
            "POST /host/whitelist", "PATCH /host/whitelist/{name}", "DELETE /host/whitelist/{name}", "GET /host/gpu-processes",
            "POST /host/containers/{name}/start", "POST /host/containers/{name}/stop", "POST /host/containers/{name}/restart",
            "POST /host/containers/{name}/autostart", "GET /host/backup-mount", "PUT /host/backup-mount",
            "DELETE /host/backup-mount", "POST /host/backup-mount/test", "POST /pki/rotations",
            "PUT /pki/rotations/{rotationId}/cert", "POST /pki/rotations/{rotationId}/commit",
            "POST /pki/rotations/{rotationId}/abort", "GET /pki/rotations/{rotationId}", "GET /pki/trust", "PUT /pki/trust",
        ];

        var catalogued = AgentRoutes.All.Select(r => r.Method + " " + r.Path).ToHashSet(StringComparer.Ordinal);
        Assert.All(expected, e => Assert.Contains(e, catalogued));
    }

    [Fact]
    public void DtoTypes_AreConcreteSealedRecordsThatSerialize()
    {
        Assert.NotEmpty(AgentRoutes.DtoTypes);
        foreach (var type in AgentRoutes.DtoTypes)
        {
            Assert.False(IsMarker(type), type.Name);
            Assert.True(type.IsSealed, $"{type.Name} must be sealed.");
            Assert.NotNull(type.GetMethod("<Clone>$")); // a record
        }

        Assert.Contains(typeof(LaunchRequest), AgentRoutes.DtoTypes);
        Assert.Contains(typeof(RuntimeStatus), AgentRoutes.DtoTypes);
        Assert.Contains(typeof(NodeCapabilities), AgentRoutes.DtoTypes);
        Assert.Contains(typeof(PkiRotationCsr), AgentRoutes.DtoTypes);
        Assert.Contains(typeof(ResetEvent), AgentRoutes.DtoTypes);
        Assert.Contains(typeof(EnrollResponse), AgentRoutes.DtoTypes);
    }

    // ---------------------------------------------------------------- error codes

    [Fact]
    public void ErrorCodes_ConstantsAreUniqueSnakeCase()
    {
        var values = ConstantValues(typeof(ErrorCodes)).ToList();
        Assert.NotEmpty(values);
        Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
        Assert.All(values, v => Assert.Matches(SnakeCase(), v));
    }

    [Fact]
    public void ErrorCodes_IncludeEveryDesignedCode()
    {
        string[] designed =
        [
            "operation_expired", "already_committed", "stale_master", "competing_master", "gpu_missing", "protocol_mismatch",
            "operation_id_conflict", "state_unavailable", "admission_refused", "start_conflict", "spec_rejected", "in_use",
            "already_running", "folder_exists", "no_files_match", "insufficient_disk", "model_not_running", "server_busy",
            "not_found", "model_not_found", "model_not_ready", "maintenance", "node_offline", "node_incompatible",
            "node_unreachable", "model_unplaced", "upstream_unavailable", "use_unplace", "placement_required", "node_required",
            "node_draining", "model_busy", "superseded",
        ];

        var values = ConstantValues(typeof(ErrorCodes)).ToHashSet(StringComparer.Ordinal);
        Assert.All(designed, code => Assert.Contains(code, values));
    }

    [Fact]
    public void ErrorCodes_SurfaceListsUseDefinedCodesWithoutDuplicates()
    {
        var values = ConstantValues(typeof(ErrorCodes)).ToHashSet(StringComparer.Ordinal);
        foreach (var list in new[] { ErrorCodes.AgentCodes, ErrorCodes.RouterCodes, ErrorCodes.ApiCodes, ErrorCodes.EnrollCodes })
        {
            Assert.All(list, c => Assert.Contains(c, values));
            Assert.Equal(list.Count, list.Distinct(StringComparer.Ordinal).Count());
        }

        foreach (var route in AgentRoutes.All)
        {
            Assert.All(route.ErrorCodes, c => Assert.Contains(c, ErrorCodes.AgentCodes));
        }
    }

    [Fact]
    public void ErrorMappings_CoverTheDesignTables()
    {
        Assert.Equal(typeof(ModelMemoryAdmissionException), ErrorMappings.MasterExceptions[ErrorCodes.AdmissionRefused]);
        Assert.Equal(typeof(ModelStartConflictException), ErrorMappings.MasterExceptions[ErrorCodes.StartConflict]);
        Assert.Equal(typeof(KeyNotFoundException), ErrorMappings.MasterExceptions[ErrorCodes.NotFound]);
        Assert.All(ErrorMappings.MasterExceptions.Keys, k => Assert.Contains(k, ErrorCodes.AgentCodes));
        Assert.All(ErrorMappings.MasterExceptions.Values, t => Assert.True(typeof(Exception).IsAssignableFrom(t)));

        Assert.Equal(ErrorCodes.RouterCodes.Order(StringComparer.Ordinal), ErrorMappings.ClientErrors.Keys.Order(StringComparer.Ordinal));
        Assert.All(ErrorMappings.ProxyAgentToClient, kv =>
        {
            Assert.Contains(kv.Key, ErrorCodes.AgentCodes);
            Assert.Contains(kv.Value, ErrorCodes.RouterCodes);
        });

        // design §7.4
        Assert.Equal(new ClientErrorTranslation(ErrorCodes.ModelNotReady, 503, null, 30), ErrorMappings.TranslateProxyError(ErrorCodes.ModelNotRunning));
        Assert.Equal(new ClientErrorTranslation(ErrorCodes.ServerBusy, 503, null, 5), ErrorMappings.TranslateProxyError(ErrorCodes.ServerBusy));
        Assert.Equal(new ClientErrorTranslation(ErrorCodes.UpstreamUnavailable, 502, "api_error", null), ErrorMappings.TranslateProxyError(ErrorCodes.InternalError));
        Assert.Equal(300, ErrorMappings.ClientErrors[ErrorCodes.NodeIncompatible].RetryAfterSeconds);
        Assert.Equal(10, ErrorMappings.ClientErrors[ErrorCodes.NodeUnreachable].RetryAfterSeconds);
    }

    [Fact]
    public void AgentErrorResponse_HasTheDesignShape()
    {
        var details = new AdmissionRefusedDetails { NeedGiB = 36, FreeGiB = 12.5, AvailableGiB = 20.5, ReserveGiB = 8 };
        var response = new AgentErrorResponse(new AgentError
        {
            Code = ErrorCodes.AdmissionRefused,
            Message = "Not enough memory on node dgx",
            Details = AgentJson.ToElement(details),
        });

        var node = JsonSerializer.SerializeToNode(response, AgentJson.Options)!;
        Assert.Equal("admission_refused", (string?)node["error"]!["code"]);
        Assert.Equal(36, (double)node["error"]!["details"]!["needGiB"]!);

        var back = JsonSerializer.Deserialize<AgentErrorResponse>(node.ToJsonString(), AgentJson.Options)!;
        Assert.Equal(details, back.Error.ReadDetails<AdmissionRefusedDetails>());
    }

    // ---------------------------------------------------------------- protocol helpers

    [Theory]
    [InlineData(1, 1, 1, 1, 1)]
    [InlineData(1, 2, 1, 1, 1)]
    [InlineData(1, 2, 2, 3, 2)]
    [InlineData(2, 3, 1, 1, null)]
    [InlineData(1, 1, 2, 2, null)]
    public void Protocol_NegotiatesPerDesign(int masterMin, int masterCurrent, int agentMin, int agentCurrent, int? expected)
    {
        Assert.Equal(expected, Protocol.Negotiate(new ProtocolRange(masterMin, masterCurrent), new ProtocolRange(agentMin, agentCurrent)));
    }

    [Fact]
    public void Protocol_ConstantsMatchTheDesign()
    {
        Assert.Equal(new ProtocolRange(1, 1), Protocol.Range);
        Assert.Equal("MirbadLLM-Protocol", Protocol.ProtocolHeader);
        Assert.Equal("MirbadLLM-Master", Protocol.MasterHeader);
        Assert.Equal("MirbadLLM-Request-Time", Protocol.RequestTimeHeader);
        Assert.Equal("/agent/v1", Protocol.BasePath);
        Assert.Equal("spiffe://mirbadllm/master", Protocol.MasterSpiffeId);
        Assert.Equal("spiffe://mirbadllm/node/dgx", Protocol.NodeSpiffeId("dgx"));
        Assert.Equal(["mirbadllm.model", "mirbadllm.node", "mirbadllm.operation"], ContainerLabels.Reserved);
        Assert.Equal("mirbadllm.exec-fingerprint", ContainerLabels.ExecutionFingerprint);
        Assert.Equal("mirbadllm.gpu-uuids", ContainerLabels.GpuUuids);
        Assert.Equal("mirbadllm.gpu-total-gib", ContainerLabels.GpuTotalGiB);
        Assert.Equal(TimeSpan.FromHours(24), OperationRetention.EnvelopeLifetime);
        Assert.Equal(TimeSpan.FromMinutes(10), OperationRetention.RetentionMargin);
        Assert.Equal(1_000, OperationRetention.MinRetainedRecords);
        Assert.Equal(AgentEventTypes.All.Count, AgentEventTypes.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("spiffe://mirbadllm/node/dgx", true, "dgx")]
    [InlineData("spiffe://mirbadllm/node/pc-office", true, "pc-office")]
    [InlineData("spiffe://mirbadllm/node/PC", false, "")]
    [InlineData("spiffe://mirbadllm/node/", false, "")]
    [InlineData("spiffe://mirbadllm/master", false, "")]
    [InlineData("spiffe://spark-serve/node/dgx", true, "dgx")] // issued before (and still after) the rename
    [InlineData("spiffe://spark-serve/master", false, "")]
    [InlineData("spiffe://other/node/dgx", false, "")]
    [InlineData(null, false, "")]
    public void Protocol_ParsesNodeSpiffeIds(string? spiffeId, bool ok, string nodeId)
    {
        Assert.Equal(ok, Protocol.TryParseNodeSpiffeId(spiffeId, out string parsed));
        Assert.Equal(nodeId, parsed);
    }

    [Fact]
    public void Spiffe_IssuesThePreRenameTrustDomain_AndAcceptsBoth()
    {
        // Every pre-rename agent requires exactly spark-serve's ids, so the CA keeps issuing them (LegacyNames.SpiffeTrustDomain).
        Assert.Equal("spiffe://spark-serve/master", Protocol.IssuedMasterSpiffeId);
        Assert.Equal("spiffe://spark-serve/node/dgx", Protocol.IssuedNodeSpiffeId("dgx"));
        Assert.True(Protocol.SpiffeIdMatches("spiffe://mirbadllm/master", Protocol.MasterSpiffeId));
        Assert.True(Protocol.SpiffeIdMatches("spiffe://spark-serve/master", Protocol.MasterSpiffeId));
        Assert.True(Protocol.SpiffeIdMatches("spiffe://spark-serve/node/dgx", Protocol.NodeSpiffeId("dgx")));
        Assert.False(Protocol.SpiffeIdMatches("spiffe://spark-serve/node/pc", Protocol.NodeSpiffeId("dgx")));
        Assert.False(Protocol.SpiffeIdMatches("spiffe://other/master", Protocol.MasterSpiffeId));
        Assert.False(Protocol.SpiffeIdMatches(null, Protocol.MasterSpiffeId));
    }

    [Theory]
    [InlineData("MirbadLLM-Master", "SparkServe-Master", null)]
    [InlineData("mirbadllm-protocol", "SparkServe-protocol", null)]
    [InlineData("SparkServe-Agent-Error", null, "MirbadLLM-Agent-Error")]
    [InlineData("Content-Type", null, null)]
    public void ProtocolHeaders_HaveAPreRenameTwin(string name, string? legacy, string? current)
    {
        Assert.Equal(legacy, Protocol.LegacyHeaderOf(name));
        Assert.Equal(current, Protocol.CurrentHeaderOf(name));
    }

    [Fact]
    public void MasterTriple_HeaderValueRoundTrips()
    {
        var triple = new MasterTriple("5b1f0c9e2d8a4f6b9e3c7a1d0f2e4b6c", 12, "e0d1c2b3a4958677685940312a1b2c3d");
        string header = triple.ToHeaderValue();
        Assert.Equal("5b1f0c9e2d8a4f6b9e3c7a1d0f2e4b6c/12/e0d1c2b3a4958677685940312a1b2c3d", header);
        Assert.True(MasterTriple.TryParse(header, out var parsed));
        Assert.Equal(triple, parsed);
        Assert.True(triple.SameBoot(parsed));
        Assert.False(triple.SameBoot(parsed with { Epoch = 13 }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a/1")]
    [InlineData("a/1/b/c")]
    [InlineData("a/-1/b")]
    [InlineData("a/x/b")]
    [InlineData("/1/b")]
    [InlineData("a/1/")]
    public void MasterTriple_RejectsMalformedHeaders(string? value)
    {
        Assert.False(MasterTriple.TryParse(value, out _));
    }

    // ---------------------------------------------------------------- serialization rules

    [Fact]
    public void Enums_UseTheDesignWireNames()
    {
        Assert.Equal("\"vllm\"", JsonSerializer.Serialize(RuntimeKind.Vllm));
        Assert.Equal("\"llamacpp\"", JsonSerializer.Serialize(RuntimeKind.LlamaCpp, AgentJson.Options));
        Assert.Equal("\"llamacpp\"", JsonSerializer.Serialize(RuntimeKind.LlamaCpp, BackupDocument.JsonOptions));
        Assert.Equal("\"container\"", JsonSerializer.Serialize(LaunchDelivery.Container));
        Assert.Equal("\"unified\"", JsonSerializer.Serialize(MemoryModel.Unified));
        Assert.Equal("\"ensureStarted\"", JsonSerializer.Serialize(LaunchMode.EnsureStarted, AgentJson.Options));
        Assert.Equal("\"unknown\"", JsonSerializer.Serialize(OperationState.Unknown, AgentJson.Options));
        Assert.Equal("\"certInstalled\"", JsonSerializer.Serialize(PkiRotationStage.CertInstalled, AgentJson.Options));
        Assert.Equal("\"pendingApproval\"", JsonSerializer.Serialize(NodeState.PendingApproval, MasterStateJson.Options));
        Assert.Equal(RuntimeKind.LlamaCpp, JsonSerializer.Deserialize<RuntimeKind>("\"llamacpp\""));
    }

    [Fact]
    public void ModelDefinition_WithoutMultiNodeFields_SerializesExactlyAsBefore()
    {
        var definition = new ModelDefinition { Id = "m", ServedName = "m", ModelPath = "M" };
        foreach (var options in new[] { RegistryFileOptions, BackupDocument.JsonOptions, AgentJson.Options })
        {
            var node = JsonSerializer.SerializeToNode(definition, options)!.AsObject();
            foreach (string name in new[] { "Placement", "Runtime", "LlamaCpp", "HostMemoryBudgetGiB", "KeepServingDuringJobs", "StartOnRequest", "IdleStopMinutes", "StopToMakeRoom" })
            {
                Assert.DoesNotContain(node, p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));
            }
        }

        var read = JsonSerializer.Deserialize<ModelDefinition>("""{"Id":"m","ServedName":"m","ModelPath":"M"}""")!;
        Assert.Null(read.Placement);
        Assert.Equal(RuntimeKind.Vllm, read.Runtime);
        Assert.Null(read.LlamaCpp);
        Assert.Null(read.HostMemoryBudgetGiB);
        Assert.False(read.KeepServingDuringJobs);
        Assert.Equal((false, (int?)null, false), (read.StartOnRequest, read.IdleStopMinutes, read.StopToMakeRoom));
        Assert.Equal(definition, read with { ExtraArgs = definition.ExtraArgs, Tags = definition.Tags, Sources = definition.Sources });
    }

    [Fact]
    public void ModelPlacement_ComparesGpuListsByValue()
    {
        var a = new ModelPlacement("pc", ["GPU-1", "GPU-2"], [0, 1]);
        var b = new ModelPlacement("pc", new List<string> { "GPU-1", "GPU-2" }, new List<int> { 0, 1 });
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, b with { GpuUuids = ["GPU-2", "GPU-1"] });
        Assert.Equal(new ModelPlacement("dgx"), new ModelPlacement("dgx", [], []));
        Assert.NotEqual(new ModelPlacement("dgx"), new ModelPlacement("pc"));
    }

    [Fact]
    public void AgentEvents_RoundTripPolymorphicallyWithTheirTypeDiscriminator()
    {
        AgentEvent[] events =
        [
            new HeartbeatEvent { BootId = "b", Seq = 4, At = DateTimeOffset.UnixEpoch },
            new RuntimeEvent { ModelId = "qwen", Runtime = null, BootId = "b", Seq = 5 },
            new ReservationEvent
            {
                Reservation = new ReservationInfo { OperationId = "o", ModelId = "qwen", BudgetGiB = 36, CreatedAt = DateTimeOffset.UnixEpoch },
                Released = true,
            },
            new ResetEvent { Reason = ResetEvent.Evicted },
            new OverflowEvent(),
        ];

        foreach (var evt in events)
        {
            string json = JsonSerializer.Serialize(evt, AgentJson.Options);
            Assert.Equal(evt.EventType, (string?)JsonNode.Parse(json)!["type"]);
            Assert.Contains(evt.EventType, AgentEventTypes.All);
            var back = JsonSerializer.Deserialize<AgentEvent>(json, AgentJson.Options)!;
            Assert.Equal(evt.GetType(), back.GetType());
            Assert.Equal(json, JsonSerializer.Serialize(back, AgentJson.Options));
        }

        // The discriminator may come anywhere in the object (AllowOutOfOrderMetadataProperties).
        var reordered = JsonSerializer.Deserialize<AgentEvent>("""{"seq":9,"type":"heartbeat","bootId":"b"}""", AgentJson.Options);
        Assert.IsType<HeartbeatEvent>(reordered);
    }

    [Fact]
    public void Secrets_NeverAppearInToString()
    {
        var envelope = new { OperationId = "op", IssuedAt = DateTimeOffset.UnixEpoch, ExpiresAt = DateTimeOffset.UnixEpoch.AddDays(1) };
        string[] texts =
        [
            new DownloadStartRequest
            {
                OperationId = envelope.OperationId, IssuedAt = envelope.IssuedAt, ExpiresAt = envelope.ExpiresAt,
                RepoId = "org/repo", Folder = "repo", Token = "hf_secret_value",
            }.ToString(),
            new EnrollRequest { Token = "hf_secret_value", NodeId = "dgx", CsrPem = "csr" }.ToString(),
            new BootstrapBundle
            {
                NodeId = "dgx", Token = "hf_secret_value", CaCertPem = "ca", CaSpkiSha256 = "h", EnrollEndpoint = new Uri("https://mirbadllm:8443"),
            }.ToString(),
            new BackupMountSetRequest
            {
                OperationId = envelope.OperationId, IssuedAt = envelope.IssuedAt, ExpiresAt = envelope.ExpiresAt,
                Settings = new BackupMountSettings { Password = "hf_secret_value" },
            }.ToString(),
            new BackupMountTestRequest { Settings = new BackupMountSettings { Password = "hf_secret_value" } }.ToString(),
            new JoinTokenIssue("hf_secret_value", new JoinTokenRecord { Sha256 = "h", NodeId = "dgx", ExpiresAt = DateTimeOffset.UnixEpoch }).ToString(),
            new BackupSecrets([]) { Pki = new BackupPkiSecrets { CaKeyPem = "hf_secret_value" } }.ToString(),
        ];

        Assert.All(texts, t => Assert.DoesNotContain("hf_secret_value", t, StringComparison.Ordinal));
        Assert.Equal(["token", "password"], Protocol.SecretFieldNames);
    }

    [Fact]
    public void OperationStatus_ResultReadsAsTheRouteResultType()
    {
        var csr = new PkiRotationCsr("rot-1", "-----BEGIN CERTIFICATE REQUEST-----");
        var status = new OperationStatus
        {
            Id = "op",
            Kind = OperationKinds.PkiRotationStart,
            Target = "rot-1",
            PayloadDigest = "d",
            State = OperationState.Succeeded,
            StartedAt = DateTimeOffset.UnixEpoch,
            Result = AgentJson.ToElement(csr),
        };

        string json = JsonSerializer.Serialize(status, AgentJson.Options);
        var back = JsonSerializer.Deserialize<OperationStatus>(json, AgentJson.Options)!;
        Assert.Equal(typeof(PkiRotationCsr), AgentRoutes.PkiRotationStart.ResultType);
        Assert.Equal(csr, back.ReadResult<PkiRotationCsr>());
        Assert.True(back.State.IsTerminal());
        Assert.False(OperationState.Running.IsTerminal());
    }

    [Fact]
    public void ModelFolderInspection_MovedToCoreKeepsTheOverheadConstant()
    {
        Assert.Equal(ModelFolderInspection.RuntimeOverheadGiB, ModelFolderInspector.RuntimeOverheadGiB);
    }

    // ---------------------------------------------------------------- golden fixtures

    [Fact]
    public void Fixture_MutationEnvelope_RoundTrips()
    {
        string json = Fixture("mutation-envelope.json");
        var request = JsonSerializer.Deserialize<StopRequest>(json, AgentJson.Options)!;
        Assert.Equal("7c4e9a1b2d3f4e5a8b6c7d8e9f0a1b2c", request.OperationId);
        Assert.Equal(request.IssuedAt + OperationRetention.EnvelopeLifetime, request.ExpiresAt);
        AssertSameJson(json, JsonSerializer.Serialize(request, AgentJson.Options));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StopRequest>("""{"operationId":"x"}""", AgentJson.Options));
    }

    [Fact]
    public void Fixture_NodesJsonV1_RoundTrips()
    {
        string json = Fixture("nodes.v1.json");
        var document = JsonSerializer.Deserialize<NodeRegistryDocument>(json, MasterStateJson.Options)!;
        Assert.Equal(NodeRegistryDocument.CurrentSchemaVersion, document.SchemaVersion);
        var dgx = Assert.Single(document.Nodes, n => n.Id == "dgx");
        Assert.Equal(MemoryModel.Unified, dgx.LastCapabilities!.Memory.Model);
        Assert.Equal(CertificateRotationStage.CertInstalled, dgx.Rotation!.Stage);
        Assert.Contains(document.JoinTokens, t => t.Purpose == JoinTokenPurpose.Bootstrap && t.UsedAt is not null);
        AssertSameJson(json, JsonSerializer.Serialize(document, MasterStateJson.Options));
    }

    [Fact]
    public void Fixture_ModelsJsonV2_RoundTripsWithTheRegistryOptions()
    {
        string json = Fixture("models.v2.json");
        var document = JsonSerializer.Deserialize<ModelRegistryDocument>(json, RegistryFileOptions)!;
        Assert.Equal(ModelRegistryDocument.CurrentSchemaVersion, document.SchemaVersion);
        Assert.Equal(new ModelPlacement("dgx"), document.Models[0].Placement);
        Assert.True(document.Models[1].KeepServingDuringJobs);
        Assert.Null(document.Models[2].Placement); // v2: unplaced stays unplaced
        Assert.Equal(RuntimeKind.LlamaCpp, document.Models[3].Runtime);
        Assert.Equal(LaunchDelivery.Process, document.Models[3].LlamaCpp!.Delivery);
        Assert.Equal(["GPU-1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d"], document.Models[3].Placement!.GpuUuids!);
        AssertSameJson(json, JsonSerializer.Serialize(document, RegistryFileOptions));

        // A bare array is version 1 (no envelope); it must not read as an envelope.
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ModelRegistryDocument>("[]", RegistryFileOptions));
    }

    [Fact]
    public void Fixture_OperationLogLines_RoundTrip()
    {
        string[] lines = Fixture("operation-log.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);

        var entries = lines.Select(l => JsonSerializer.Deserialize<OperationLogEntry>(l, AgentJson.Options)!).ToList();
        Assert.Equal(OperationLogEntryType.Accepted, entries[0].Type);
        Assert.Equal(3, entries[0].Master!.Epoch);
        Assert.Equal(Protocol.RedactedValue, entries[0].Payload!.Value.GetProperty("spec").GetProperty("env").GetProperty("HF_TOKEN").GetString());
        Assert.Equal(8101, entries[1].Reservation!.HostPort);
        Assert.Equal(ModelState.Starting, entries[3].Result!.Value.Deserialize<RuntimeStatus>(AgentJson.Options)!.State);
        Assert.Equal(OperationState.Unknown, entries[4].State);
        Assert.Equal(ErrorCodes.Timeout, entries[4].Error!.Code);

        // The accepted line's payload is itself a launch request.
        var launch = entries[0].Payload!.Value.Deserialize<LaunchRequest>(AgentJson.Options)!;
        Assert.Equal(LaunchMode.EnsureStarted, launch.Mode);

        for (int i = 0; i < lines.Length; i++)
        {
            string written = JsonSerializer.Serialize(entries[i], AgentJson.Options);
            Assert.DoesNotContain('\n', written);
            AssertSameJson(lines[i], written);
        }
    }

    [Fact]
    public void Fixture_MasterJsonV1_RoundTrips()
    {
        string json = Fixture("master.v1.json");
        var document = JsonSerializer.Deserialize<MasterStateDocument>(json, MasterStateJson.Options)!;
        Assert.Equal(4, document.Epoch);
        var takeover = Assert.Single(document.Takeovers);
        Assert.Equal(["pc-office"], takeover.Unreachable);
        AssertSameJson(json, JsonSerializer.Serialize(document, MasterStateJson.Options));
    }

    // ---------------------------------------------------------------- backup v2 fields

    [Fact]
    public void BackupDocument_V2FieldsAreOptionalAndOmittedWhenAbsent()
    {
        var v1 = new BackupDocument { CreatedAt = DateTimeOffset.UnixEpoch, HostServices = new HostServicesBackup(null, []) };
        var node = JsonSerializer.SerializeToNode(v1, BackupDocument.JsonOptions)!.AsObject();
        Assert.False(node.ContainsKey("master"));
        Assert.False(node.ContainsKey("nodes"));
        Assert.False(node["hostServices"]!.AsObject().ContainsKey("nodes"));
        Assert.Equal(1, BackupDocument.CurrentSchemaVersion);

        var v2 = v1 with
        {
            SchemaVersion = BackupDocument.MultiNodeSchemaVersion,
            Master = new BackupMasterInfo("instance", 3),
            Nodes = [new BackupNode { Id = "dgx", Uid = "uid", Address = "https://127.0.0.1:9443", Approved = true, MemoryReserveGiB = 8 }],
            HostServices = new HostServicesBackup(null, null)
            {
                Nodes = new Dictionary<string, HostServicesBackup> { ["dgx"] = new(new Dictionary<string, string> { ["n8n"] = "always" }, []) },
            },
        };

        string json = JsonSerializer.Serialize(v2, BackupDocument.JsonOptions);
        var back = JsonSerializer.Deserialize<BackupDocument>(json, BackupDocument.JsonOptions)!;
        Assert.Equal(v2.Master, back.Master);
        Assert.Equal("dgx", Assert.Single(back.Nodes!).Id);
        Assert.Equal("always", back.HostServices!.Nodes!["dgx"].RestartPolicies!["n8n"]);
        Assert.Equal(3, (long)JsonNode.Parse(json)!["master"]!["epoch"]!);
    }

    // ---------------------------------------------------------------- helpers

    private static IEnumerable<AgentRoute> AllRoutes() => AgentRoutes.All.Append(AgentRoutes.Enroll);

    private static bool IsMarker(Type type) => type == typeof(NoBody) || type == typeof(RawBody) || type == typeof(LogLine);

    private static IEnumerable<string> ConstantValues(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    private static void AssertSameJson(string expected, string actual)
    {
        var expectedNode = JsonNode.Parse(expected);
        var actualNode = JsonNode.Parse(actual);
        Assert.True(JsonNode.DeepEquals(expectedNode, actualNode),
            $"JSON differs.{Environment.NewLine}Expected: {expectedNode?.ToJsonString()}{Environment.NewLine}Actual:   {actualNode?.ToJsonString()}");
    }

    private static string Fixture(string name, [CallerFilePath] string sourceFile = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures", name));

    [GeneratedRegex("^[a-z][a-z0-9]*(_[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SnakeCase();
}
