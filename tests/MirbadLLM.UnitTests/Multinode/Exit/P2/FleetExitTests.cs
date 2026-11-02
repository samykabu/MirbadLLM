using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Enrollment;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Pki;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Agent;
using MirbadLLM.UnitTests.Multinode.P1Exit;
using MirbadLLM.UnitTests.Pki;
using static MirbadLLM.UnitTests.Multinode.P2Exit.P2Env;

namespace MirbadLLM.UnitTests.Multinode.P2Exit;

/// <summary>
/// MN-123, fleet and approval (design §3.2, §3.5, §5.6, §9.4 step 5, §10.2 item 6, §14 P2), through the real master and
/// real agents: a PC joins with an operator token over the enroll listener, waits for approval (no command reaches it), is
/// approved with the pin the node API shows and comes Online beside the DGX; a pending node is rejected; a revoke refuses
/// placed models unless they are unplaced, bars new placements while it runs (W7 review: revoke racing a placement, D87)
/// and leaves the node out of the fleet; an expired certificate and a lost state volume are repaired with
/// <c>join --rekey</c>, keeping the node's uid, approval and running model.
/// </summary>
public sealed class FleetExitTests
{
    private static readonly string[] AgentEkus = [PkiDefaults.ServerAuthOid, PkiDefaults.ClientAuthOid];

    /// <summary>
    /// MN-124 (D105): the DGX compose publishes the enroll port to the LAN; on the real master it answers only
    /// <c>POST /enroll</c> and <c>POST /enroll/rekey</c> (a refused token there), never the UI, <c>/api</c>, <c>/v1</c> or
    /// <c>/metrics</c>.
    /// </summary>
    [Fact]
    public async Task TheEnrollPortServesOnlyEnrollAndRekeyOnTheRealMaster()
    {
        await using var fleet = await P2Fleet.StartAsync();
        // The listener certificate is issued by the master's CA (not sent in its chain, D35): checked by issuer.
        using var ca = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(
            Path.Combine(fleet.DataDir, MirbadLLM.Orchestration.MasterPkiOptions.DirectoryName, CaStore.CertificateFileName), Ct));
        string issuer = ca.Subject;
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = { RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null && certificate.Issuer == issuer },
        };
        using var tls = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{fleet.EnrollPort}") };

        foreach (string path in new[] { "/", "/login", "/nodes", "/api/status", "/api/nodes", "/v1/models", "/metrics", "/healthz" })
        {
            using var get = await tls.GetAsync(path, Ct);
            Assert.True(get.StatusCode == HttpStatusCode.NotFound, $"GET {path} on the enroll port: {(int)get.StatusCode}");
        }

        using (var chat = await tls.PostAsync("/v1/chat/completions", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, chat.StatusCode);
        }

        // The two enroll routes answer (an unknown token is refused, not 404).
        foreach (string path in new[] { AgentRoutes.Enroll.Path, "/enroll/rekey" })
        {
            using var post = await tls.PostAsync(path, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), Ct);
            Assert.True(post.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden, $"POST {path}: {(int)post.StatusCode}");
        }
    }

    [Fact]
    public async Task APcJoinsWithATokenWaitsForApprovalAndComesOnlineWhenApprovedWithItsPin()
    {
        await using var fleet = await P2Fleet.StartAsync();
        JoinTokenCreateResponse token = await fleet.CreateJoinTokenAsync("pc");
        Assert.Equal(JoinTokenPurpose.Join, token.Purpose);
        Assert.Contains(token.Token, token.Commands.Linux, StringComparison.Ordinal);
        var pc = await fleet.JoinAsync("pc", PcGpus, approve: false);

        // Enrolled with a pending pin: not approved, no command accepted for it, nothing sent to its agent.
        var pending = (await fleet.Master.Nodes.GetAsync("pc", Ct))!;
        Assert.False(pending.Approved);
        Assert.NotNull(pending.PendingCertSpkiSha256);
        Assert.Null(pending.CertSpkiSha256);
        await fleet.Master.ExpectAsync("/api/nodes/pc/drain", null, HttpStatusCode.Conflict, ErrorCodes.InvalidState);
        Assert.NotEqual(NodeState.Online, fleet.Master.Cache.GetNode("pc")?.State);
        Assert.Empty(pc.Docker.CreateBodies);

        // The dialog showed another pin than the pending one: refused, still pending.
        await fleet.Master.ExpectAsync("/api/nodes/pc/approve", new NodeApproveRequest { ExpectedSpkiSha256 = Convert.ToBase64String(new byte[32]) },
            HttpStatusCode.Conflict, ErrorCodes.InvalidState);
        Assert.False((await fleet.Master.Nodes.GetAsync("pc", Ct))!.Approved);

        NodeRecord approved = await fleet.ApproveAsync("pc");
        Assert.Equal(pending.PendingCertSpkiSha256, approved.CertSpkiSha256);
        Assert.Equal(pending.Uid, approved.Uid);
        await fleet.Master.WaitForNodeAsync("pc", n => n.State == NodeState.Online, "pc Online");
        Assert.Equal(NodeState.Online, fleet.Master.Cache.GetNode("dgx")!.State);

        // The approved PC reports as a discrete node, and the master's client reaches it over mTLS with the new pin.
        var info = await fleet.Master.Client("pc").GetInfoAsync(Ct);
        Assert.Equal(approved.Uid, info.NodeUid);
        await ExitEnv.UntilAsync(async () => (await fleet.Master.Nodes.GetAsync("pc", Ct))?.LastCapabilities?.Memory.Model == MemoryModel.Discrete,
            "pc's discrete capability report");

        // The token was used once: running join with it again is refused and changes nothing.
        await using var other = await ContractNode.CreateAsync(enroll: false, Ct, nodeId: "pc");
        int exit = await fleet.RunJoinAsync(new P2Agent(other, TestPorts.Reserve(), FakeNvidiaSmi.Discrete(PcGpus), _ => { }), token, expectOk: false);
        Assert.NotEqual(JoinCommand.ExitOk, exit);
        Assert.Equal(approved.CertSpkiSha256, (await fleet.Master.Nodes.GetAsync("pc", Ct))!.CertSpkiSha256);

        var audit = await fleet.AuditAsync();
        Assert.Contains(audit, r => r is { Action: AuditActions.NodeApprove, Result: AuditResults.Succeeded, NodeId: "pc" });
        Assert.Contains(audit, r => r is { Action: AuditActions.NodeApprove, Result: AuditResults.Refused, NodeId: "pc" });
        Assert.DoesNotContain(token.Token, fleet.AuditText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, fleet.AuditText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectedPendingNodeIsDeletedAndCannotBeApprovedOrReEnrolledWithItsToken()
    {
        await using var fleet = await P2Fleet.StartAsync();
        var pc = await fleet.JoinAsync("pc", PcGpus, approve: false, start: false);
        Assert.NotNull((await fleet.Master.Nodes.GetAsync("pc", Ct))?.PendingCertSpkiSha256);
        JoinTokenCreateResponse spare = await fleet.CreateJoinTokenAsync("pc"); // a second, unused token for the pending node

        var (status, body) = await fleet.Master.PostAsync("/api/nodes/pc/reject");
        Assert.True(status == HttpStatusCode.NoContent, $"reject: {(int)status} {body}");

        Assert.Null(await fleet.Master.Nodes.GetAsync("pc", Ct));
        var (get, _) = await SendAsync(fleet.Master.Admin.GetAsync("/api/nodes/pc", Ct));
        Assert.Equal(HttpStatusCode.NotFound, get);
        await fleet.Master.ExpectAsync("/api/nodes/pc/approve", null, HttpStatusCode.NotFound);

        // The unused token died with the record: the node cannot enroll again with it.
        Directory.Delete(pc.Node.StateDirectory, recursive: true);
        Directory.CreateDirectory(pc.Node.StateDirectory);
        Assert.NotEqual(JoinCommand.ExitOk, await fleet.RunJoinAsync(pc, spare, expectOk: false));
        Assert.Null(await fleet.Master.Nodes.GetAsync("pc", Ct));

        // The DGX never noticed: the fleet is still the DGX alone.
        Assert.Equal(NodeState.Online, fleet.Master.Cache.GetNode("dgx")!.State);
        Assert.Contains(await fleet.AuditAsync(), r => r is { Action: AuditActions.NodeReject, Result: AuditResults.Succeeded, NodeId: "pc" });
    }

    [Fact]
    public async Task ARevokeRefusesPlacedModelsUnlessUnplacedBarsPlacementsWhileItRunsAndLeavesTheNodeOutOfTheFleet()
    {
        await using var fleet = await P2Fleet.StartAsync();
        var pc = await fleet.JoinAsync("pc", PcGpus);
        await fleet.Master.CreateModelAsync(ModelBody("pcm", "pc", [PcGpus[0]]));
        await fleet.Master.StartModelAsync("pcm");
        Assert.Single(pc.Docker.ModelContainers);

        // Refusals: the typed id, then the placed model — neither revokes nor stops anything.
        await AssertRevokeRefusedAsync(fleet, new { password = Password, confirm = "dgx", unplaceModels = true }, HttpStatusCode.Conflict, ErrorCodes.ConfirmationRequired);
        string placed = await AssertRevokeRefusedAsync(fleet, new { password = Password, confirm = "pc", unplaceModels = false }, HttpStatusCode.Conflict, ErrorCodes.ModelsPlaced);
        Assert.Contains("pcm", placed, StringComparison.Ordinal);
        var stillThere = (await fleet.Master.Nodes.GetAsync("pc", Ct))!;
        Assert.False(stillThere.Revoked);
        Assert.False(stillThere.Draining); // the barrier is put back after a refusal
        Assert.Single(pc.Docker.ModelContainers);
        Assert.Equal("pc", (await fleet.Master.Models.GetAsync("pcm", Ct))!.Placement?.NodeId);

        // The revoke with unplace: its stop of pcm is held on the PC's Docker, so the revoke is in progress.
        pc.Docker.SetStopsHeld(true);
        var revoke = SendAsync(DeleteAsync(fleet.Master.Admin, "/api/nodes/pc", new { confirm = "pc", unplaceModels = true }));
        await ExitEnv.UntilAsync(() => pc.Docker.Calls.Any(c => c.StartsWith("POST /containers/", StringComparison.Ordinal) && c.Contains("/stop", StringComparison.Ordinal)),
            "the revoke's stop of pcm to reach the PC");
        Assert.True((await fleet.Master.Nodes.GetAsync("pc", Ct))!.Draining, "the revoke barrier (draining) is not set while it runs");

        // A placement racing the revoke is refused by the registry: the node is draining from the start (D87).
        var (racing, racingBody) = await fleet.Master.PostAsync("/api/models", ModelBody("late", "pc", [PcGpus[1]]));
        Assert.True(racing == HttpStatusCode.BadRequest, $"a placement during the revoke: {(int)racing} {racingBody}");
        Assert.Equal(ErrorCodes.PlacementInvalid, CodeOf(racingBody));
        Assert.Contains(PlacementProblemCodes.NodeDraining, racingBody, StringComparison.Ordinal);
        Assert.Null(await fleet.Master.Models.GetAsync("late", Ct));

        pc.Docker.SetStopsHeld(false);
        var (revoked, revokedBody) = await revoke;
        Assert.True(revoked == HttpStatusCode.OK, $"revoke: {(int)revoked} {revokedBody}");
        using (var result = JsonDocument.Parse(revokedBody))
        {
            Assert.Equal(["pcm"], result.RootElement.GetProperty("unplacedModelIds").EnumerateArray().Select(e => e.GetString()));
        }

        // Revoked: pins deleted, the model stopped and unplaced, the node out of the list and the fleet, no new token.
        var record = (await fleet.Master.Nodes.GetAsync("pc", Ct))!;
        Assert.True(record.Revoked);
        Assert.Null(record.CertSpkiSha256);
        Assert.Null(record.PendingCertSpkiSha256);
        Assert.Null((await fleet.Master.Models.GetAsync("pcm", Ct))!.Placement);
        await ExitEnv.UntilAsync(() => pc.Docker.ModelContainers.Count == 0, "pcm's container removed from the PC");
        var list = await fleet.Master.GetJsonAsync("/api/nodes");
        Assert.DoesNotContain(list.EnumerateArray(), n => n.GetProperty("node").GetProperty("id").GetString() == "pc");
        var (token, tokenBody) = await fleet.Master.PostAsync("/api/nodes/join-tokens", new { nodeId = "pc", password = Password });
        Assert.True(token == HttpStatusCode.Conflict, $"join token for a revoked node: {(int)token} {tokenBody}");
        Assert.Equal(ErrorCodes.NodeRevoked, CodeOf(tokenBody));
        await ExitEnv.UntilAsync(() => fleet.Master.Cache.GetNode("pc") is not { State: NodeState.Online }, "pc no longer Online in the master's view");
        Assert.Equal(NodeState.Online, fleet.Master.Cache.GetNode("dgx")!.State);

        var audit = await fleet.AuditAsync();
        Assert.Contains(audit, r => r is { Action: AuditActions.NodeRevoke, Result: AuditResults.Succeeded, NodeId: "pc" });
        Assert.Contains(audit, r => r is { Action: AuditActions.ModelUnplace, Result: AuditResults.Succeeded, ModelId: "pcm" });
        Assert.Contains(audit, r => r is { Action: AuditActions.NodeRevoke, Result: AuditResults.Refused, ErrorCode: ErrorCodes.ModelsPlaced });
        Assert.DoesNotContain(Password, fleet.AuditText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExpiredCertificateIsRepairedWithJoinRekeyKeepingTheUidApprovalAndTheRunningModel()
    {
        await using var fleet = await P2Fleet.StartAsync();
        var pc = await fleet.JoinAsync("pc", PcGpus);
        await fleet.Master.CreateModelAsync(ModelBody("pcm", "pc", [PcGpus[1]]));
        await fleet.Master.StartModelAsync("pcm");
        NodeRecord before = (await fleet.Master.Nodes.GetAsync("pc", Ct))!;

        // The node was off for months: its certificate (same key) expired.
        await pc.StopAsync();
        await fleet.Master.WaitForNodeAsync("pc", n => n.State == NodeState.Offline, "pc Offline");
        string certPath = Path.Combine(pc.Node.StateDirectory, FileAgentCredentials.CertificateFileName);
        CertificateAuthority ca = fleet.Master.Get<MasterCertificates>().Authority;
        using (ECDsa key = ECDsa.Create())
        {
            key.ImportFromPem(await File.ReadAllTextAsync(Path.Combine(pc.Node.StateDirectory, FileAgentCredentials.KeyFileName), Ct));
            using X509Certificate2 expired = PkiTestSupport.SignCustom(ca.Certificate, ca.ExportPrivateKeyPem(),
                DateTimeOffset.UtcNow.AddDays(-200), DateTimeOffset.UtcNow.AddDays(-110), [Protocol.NodeSpiffeId("pc")], AgentEkus, subjectKey: key,
                ips: [IPAddress.Loopback]);
            await File.WriteAllTextAsync(certPath, expired.ExportCertificatePem() + "\n", Ct);
        }

        // Back on with the expired certificate: the master refuses it, so the node stays out of service.
        await pc.StartAsync();
        var refused = await Assert.ThrowsAsync<AgentUnreachableException>(() => fleet.Master.Client("pc").GetInfoAsync(Ct));
        Assert.Contains(nameof(PeerValidationFailure.NotTimeValid), refused.Message, StringComparison.Ordinal);
        Assert.NotEqual(NodeState.Online, fleet.Master.Cache.GetNode("pc")?.State);
        await pc.StopAsync();

        // The repair as the API's Linux line runs it: a rekey token, join --rekey between stop and start.
        JoinTokenCreateResponse rekey = await fleet.CreateJoinTokenAsync("pc", rekey: true);
        Assert.Equal(JoinTokenPurpose.Rekey, rekey.Purpose);
        await fleet.RunJoinAsync(pc, rekey, rekey: true);
        await pc.StartAsync();
        await fleet.Master.WaitForNodeAsync("pc", n => n.State == NodeState.Online, "pc Online after the rekey");

        NodeRecord after = (await fleet.Master.Nodes.GetAsync("pc", Ct))!;
        Assert.Equal(before.Uid, after.Uid);
        Assert.True(after.Approved);
        Assert.Equal(before.ApprovedAt, after.ApprovedAt);
        Assert.NotEqual(before.CertSpkiSha256, after.CertSpkiSha256); // a rekey writes a new key: the pin is replaced
        Assert.Null(after.PendingCertSpkiSha256);
        using (var renewed = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(certPath, Ct)))
        {
            Assert.True(renewed.NotAfter > DateTime.UtcNow.AddDays(30), $"the new certificate ends {renewed.NotAfter:u}");
        }

        // The runtimes were untouched: the container adopted, not recreated, and still served.
        Assert.Single(pc.CreateBodiesOf("pcm"));
        Assert.Equal(ModelState.Ready, (await fleet.Master.Client("pc").GetRuntimeAsync("pcm", Ct))?.State);
        Assert.DoesNotContain(rekey.Token, fleet.AuditText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALostStateVolumeIsRepairedWithJoinRekeyAndTheNodeUidReplacingThePins()
    {
        await using var fleet = await P2Fleet.StartAsync();
        var pc = await fleet.JoinAsync("pc", PcGpus);
        await fleet.Master.CreateModelAsync(ModelBody("pcm", "pc", [PcGpus[0]]));
        await fleet.Master.StartModelAsync("pcm");
        NodeRecord before = (await fleet.Master.Nodes.GetAsync("pc", Ct))!;

        // The state volume is gone (key, certificate, uid, operation log); Docker and the models folder are not.
        await pc.StopAsync();
        await fleet.Master.WaitForNodeAsync("pc", n => n.State == NodeState.Offline, "pc Offline");
        Directory.Delete(pc.Node.StateDirectory, recursive: true);
        Directory.CreateDirectory(pc.Node.StateDirectory);

        // join --rekey needs the uid now (from the API's line); without it the agent refuses before sending anything.
        JoinTokenCreateResponse rekey = await fleet.CreateJoinTokenAsync("pc", rekey: true);
        Assert.Contains(before.Uid, rekey.Commands.Linux, StringComparison.Ordinal);
        Assert.NotEqual(JoinCommand.ExitOk, await fleet.RunJoinAsync(pc, rekey, rekey: true, expectOk: false));
        await fleet.RunJoinAsync(pc, rekey, rekey: true, nodeUid: before.Uid);
        await pc.StartAsync();
        await fleet.Master.WaitForNodeAsync("pc", n => n.State == NodeState.Online, "pc Online after the rekey");

        NodeRecord after = (await fleet.Master.Nodes.GetAsync("pc", Ct))!;
        Assert.Equal(before.Uid, after.Uid);
        Assert.True(after.Approved);
        Assert.NotEqual(before.CertSpkiSha256, after.CertSpkiSha256); // a new key: the old pin is replaced
        Assert.Equal(before.Uid, (await fleet.Master.Client("pc").GetInfoAsync(Ct)).NodeUid);

        // The running model is adopted from Docker's labels, not relaunched, and the master drives it again.
        await ExitEnv.UntilAsync(async () => (await fleet.Master.Client("pc").GetRuntimeAsync("pcm", Ct))?.State == ModelState.Ready, "pcm adopted Ready");
        Assert.Single(pc.CreateBodiesOf("pcm"));
        var (stop, stopBody) = await fleet.Master.PostAsync("/api/models/pcm/stop");
        Assert.True(stop == HttpStatusCode.NoContent, $"stop pcm: {(int)stop} {stopBody}");
        await ExitEnv.UntilAsync(() => pc.Docker.ModelContainers.Count == 0, "pcm removed");
        Assert.Contains(await fleet.AuditAsync(), r => r is { Action: AuditActions.NodeEnroll, Result: AuditResults.Succeeded, NodeId: "pc" }
            && r.Detail?.StartsWith("rekey", StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// MN-124 (D105): the DGX's container agent after its state volume was lost. The master no longer writes a bootstrap
    /// bundle for an enrolled node, so the agent cannot enroll again by itself; the documented procedure
    /// (<c>ContainerAgentRekey</c>: <c>join --rekey</c> in the agent image against the state volume, the enroll URL on the
    /// Docker network, the uid from the rekey dialog) brings it back as the same node, still exactly P1.
    /// </summary>
    [Fact]
    public async Task TheDgxContainerAgentWithALostStateVolumeIsRePairedByTheDocumentedRekey()
    {
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions { Models = [Model("qwen", "dgx")] });
        await fleet.Master.StartModelAsync("qwen");
        NodeRecord before = (await fleet.Master.Nodes.GetAsync("dgx", Ct))!;
        var dgx = fleet.Dgx;

        await dgx.StopAsync();
        await fleet.Master.WaitForNodeAsync("dgx", n => n.State == NodeState.Offline, "dgx Offline");
        Directory.Delete(dgx.Node.StateDirectory, recursive: true);
        Directory.CreateDirectory(dgx.Node.StateDirectory);

        // No way back through the bundle: the node is enrolled, so the master writes none.
        await fleet.Master.Get<MirbadLLM.Web.Enrollment.BootstrapBundleWriter>().EnsureAsync(Ct);
        Assert.Empty(Directory.Exists(fleet.BootstrapDir) ? Directory.EnumerateFiles(fleet.BootstrapDir) : []);

        // The rekey dialog's container lines carry the uid and CA hash; the agent runs exactly that join --rekey.
        JoinTokenCreateResponse rekey = await fleet.CreateJoinTokenAsync("dgx", rekey: true);
        string script = MirbadLLM.Web.Api.ContainerAgentRekey.Script("dgx", before.Uid, rekey.CaHash);
        Assert.Contains($"agent join --rekey --enroll-url {MirbadLLM.Web.Api.ContainerAgentRekey.NetworkEnrollUrl} --ca-hash '{rekey.CaHash}' --node-id 'dgx' --node-uid '{before.Uid}'",
            script, StringComparison.Ordinal);
        Assert.DoesNotContain(rekey.Token, script, StringComparison.Ordinal);
        Assert.DoesNotContain("install-agent.sh", script, StringComparison.Ordinal);
        await fleet.RunJoinAsync(dgx, rekey, rekey: true, nodeUid: before.Uid);
        await dgx.StartAsync();
        await fleet.Master.WaitForNodeAsync("dgx", n => n.State == NodeState.Online, "dgx Online after the rekey");

        NodeRecord after = (await fleet.Master.Nodes.GetAsync("dgx", Ct))!;
        Assert.Equal(before.Uid, after.Uid);
        Assert.True(after.Approved);
        Assert.NotEqual(before.CertSpkiSha256, after.CertSpkiSha256);
        await ExitEnv.UntilAsync(async () => (await fleet.Master.Client("dgx").GetRuntimeAsync("qwen", Ct))?.State == ModelState.Ready, "qwen adopted Ready");
        await P2Checks.AssertDgxIsP1Async(fleet);
    }

    private static async Task<string> AssertRevokeRefusedAsync(P2Fleet fleet, object body, HttpStatusCode status, string code)
    {
        var (actual, text) = await SendAsync(DeleteAsync(fleet.Master.Admin, "/api/nodes/pc", body));
        Assert.True(actual == status, $"revoke: expected {(int)status} {code}, got {(int)actual} {text}");
        Assert.Equal(code, CodeOf(text));
        return text;
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient http, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path) { Content = JsonContent.Create(body) };
        return await http.SendAsync(request, Ct);
    }
}
