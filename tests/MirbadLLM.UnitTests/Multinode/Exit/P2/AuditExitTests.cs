using System.Net;
using System.Net.Http.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Pki;
using MirbadLLM.Orchestration.Recovery;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Multinode.P1Exit;
using static MirbadLLM.UnitTests.Multinode.P2Exit.P2Env;

namespace MirbadLLM.UnitTests.Multinode.P2Exit;

/// <summary>
/// MN-123, the audit log end to end (design §10.5; MN-116; D81, D86): every P2 action this test drives through the real
/// master's API and entry points — join token (join and rekey), enrollment, approve, reject, drain/undrain, takeover,
/// configuration import, CA rotation, revoke — writes its line to <c>{DataPath}/audit/*.jsonl</c> with its actor and result,
/// and no secret (admin password, join and rekey tokens, Hugging Face token, backup passphrase, private keys of the CAs,
/// the master and the agents) appears in any audit file or in the master's (or an agent's) log.
/// </summary>
public sealed class AuditExitTests
{
    private const string Passphrase = "p2-audit-backup-passphrase";
    private const string HfToken = "hf_p2AuditExitTokenNeverLogged0123456789";

    [Fact]
    public async Task EveryP2ActionIsAuditedWithItsActorAndResultAndNoSecretReachesTheAuditOrTheLogs()
    {
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions { Models = [Model("qwen", "dgx")] });
        var master = fleet.Master;
        var secrets = new List<(string What, string Value)> { ("admin password", Password), ("HF token", HfToken), ("backup passphrase", Passphrase) };

        // Join token, enrollment and approval of pc (the harness drives the API and the agent's join command).
        var tokenBefore = DateTimeOffset.UtcNow;
        var pc = await fleet.JoinAsync("pc", PcGpus);
        string actor = await SessionActorAsync(fleet, AuditActions.NodeJoinToken, "pc");
        Assert.StartsWith("admin:", actor, StringComparison.Ordinal);
        await master.CreateModelAsync(ModelBody("pcm", "pc", [PcGpus[0]]));

        // A rekey for an unknown node is audited as refused; even a legacy password field stays out of logs.
        await master.ExpectAsync("/api/nodes/join-tokens", new { nodeId = "pc2", password = "wrong-p2-audit-password", rekey = true }, HttpStatusCode.NotFound);
        secrets.Add(("wrong password", "wrong-p2-audit-password"));

        // A second PC joins and is rejected while pending.
        var pc2 = await fleet.JoinAsync("pc2", Pc2Gpus, approve: false, start: false);
        var (rejected, rejectedBody) = await master.PostAsync("/api/nodes/pc2/reject");
        Assert.True(rejected == HttpStatusCode.NoContent, $"reject: {(int)rejected} {rejectedBody}");

        // Drain and undrain.
        await master.ExpectAsync("/api/nodes/pc/drain", null, HttpStatusCode.OK);
        await master.ExpectAsync("/api/nodes/pc/undrain", null, HttpStatusCode.OK);

        // A rekey of pc: rekey token, join --rekey with the uid.
        NodeRecord pcRecord = (await master.Nodes.GetAsync("pc", Ct))!;
        await pc.StopAsync();
        JoinTokenCreateResponse rekey = await fleet.CreateJoinTokenAsync("pc", rekey: true);
        await fleet.RunJoinAsync(pc, rekey, rekey: true, nodeUid: pcRecord.Uid);
        await pc.StartAsync();
        // The registry sync (5 s) hands the new pin to the client factory; until then the old pin refuses the new certificate.
        await ExitEnv.UntilAsync(async () =>
        {
            try
            {
                await master.Client("pc").GetInfoAsync(Ct);
                return true;
            }
            catch (Exception ex) when (ex is AgentException or AgentUnreachableException or HttpRequestException)
            {
                return false;
            }
        }, "pc reachable under its new pin after the rekey");
        await master.WaitForNodeAsync("pc", n => n.State == NodeState.Online, "pc Online after the rekey");

        // A second admin session: a takeover through the API
        // (both nodes answer) and, below, the revoke.
        using var second = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false, UseProxy = false })
        {
            BaseAddress = master.HttpAddress,
        };
        await master.LoginAsync(client: second);
        var (took, takeover) = await SendAsync(second.PostAsJsonAsync("/api/fencing/takeover",
            new { password = Password, confirm = TakeoverConfirmations.PreviousMasterStopped }, Ct));
        Assert.True(took == HttpStatusCode.OK, $"takeover: {(int)took} {takeover}");
        Assert.Contains("\"takeoverId\"", takeover, StringComparison.Ordinal);

        // A configuration import (the Restore tab's call) with a passphrase and an HF token.
        var backup = BackupService.Parse(BackupService.Serialize(await master.Get<BackupService>().CreateAsync(Passphrase, Ct)));
        var backupSecrets = BackupService.DecryptSecrets(backup, Passphrase);
        await master.Get<RestorePlanner>().PreviewAsync(backup, backupSecrets, HfToken, Ct);
        var restores = master.Get<RestoreJobService>();
        restores.Start(new RestoreRequest(backup, backupSecrets, ["qwen", "pcm"], ReplaceExisting: true, ImportApiKeys: true, ImportHostServices: false,
            HuggingFaceToken: HfToken));
        await restores.Completion.WaitAsync(TimeSpan.FromSeconds(120), Ct);
        Assert.Equal(RestoreJobState.Completed, restores.Current!.State);

        // A CA rotation from its flag file, every node online: it closes in one pass.
        string pki = Path.Combine(fleet.DataDir, MasterPkiOptions.DirectoryName);
        string oldCaKey = await File.ReadAllTextAsync(Path.Combine(pki, CaStore.KeyFileName), Ct);
        string oldClientKey = await File.ReadAllTextAsync(Path.Combine(pki, MasterCertificates.ClientKeyFileName), Ct);
        await File.WriteAllTextAsync(Path.Combine(pki, CaRotationOptions.StartFlagFileName), "", Ct);
        await master.Get<CaRotationService>().RunOnceAsync(Ct);
        CaRotationRecord rotation = master.Get<MasterTrustStore>().Document.Rotation!;
        Assert.True(rotation.Stage == CaRotationStage.Closed,
            $"{rotation.Stage}: {string.Join(" | ", rotation.Nodes.Select(n => $"{n.NodeId} trust={n.TrustAdded} rotated={n.CertificateRotated} narrowed={n.OldTrustRemoved} {n.Error}"))}");

        // Revoke pc using the signed-in session and typed node id, unplacing its models.
        await RevokeAsync(second, "pc");

        // ---- every action has its line, with actor and result -------------------------------------------------------
        var audit = await fleet.AuditAsync();
        AuditRecord Line(string action, string? nodeId, string result, Func<AuditRecord, bool>? also = null)
        {
            var match = audit.FirstOrDefault(l => l.Action == action && (nodeId is null || l.NodeId == nodeId) && l.Result == result && (also?.Invoke(l) ?? true));
            Assert.True(match is not null, $"no {result} {action} line for {nodeId}: {string.Join(" | ", audit.Select(l => $"{l.Actor} {l.Action} {l.NodeId} {l.Result} {l.ErrorCode}"))}");
            Assert.False(string.IsNullOrWhiteSpace(match!.Actor));
            Assert.True(match.Time >= tokenBefore.AddMinutes(-1));
            return match;
        }

        Assert.Equal(actor, Line(AuditActions.NodeJoinToken, "pc", AuditResults.Succeeded).Actor);
        Assert.Equal(actor, Line(AuditActions.NodeJoinToken, "pc", AuditResults.Succeeded, l => l.Detail?.Contains("rekey", StringComparison.OrdinalIgnoreCase) == true).Actor);
        Assert.Equal(actor, Line(AuditActions.NodeJoinToken, "pc2", AuditResults.Refused).Actor);
        Line(AuditActions.NodeEnroll, "pc", AuditResults.Succeeded);
        Line(AuditActions.NodeEnroll, "pc2", AuditResults.Succeeded);
        Assert.Equal(actor, Line(AuditActions.NodeApprove, "pc", AuditResults.Succeeded).Actor);
        Assert.Equal(actor, Line(AuditActions.NodeReject, "pc2", AuditResults.Succeeded).Actor);
        Assert.Equal(actor, Line(AuditActions.NodeDrain, "pc", AuditResults.Succeeded).Actor);
        Assert.Equal(actor, Line(AuditActions.NodeUndrain, "pc", AuditResults.Succeeded).Actor);
        string actor2 = Line(AuditActions.FencingTakeover, null, AuditResults.Succeeded).Actor;
        Assert.StartsWith("admin:", actor2, StringComparison.Ordinal);
        Assert.NotEqual(actor, actor2); // one actor per session (D81)
        Line(AuditActions.Restore, null, AuditResults.Succeeded);
        Assert.Equal(AuditActors.Config, Line(AuditActions.PkiCaRotation, null, AuditResults.Succeeded, l => l.Detail?.Contains("started", StringComparison.Ordinal) == true).Actor);
        Line(AuditActions.PkiCaRotation, null, AuditResults.Succeeded, l => l.Target == rotation.RotationId && l.Actor == AuditActors.SystemPki);
        Assert.Equal(actor2, Line(AuditActions.NodeRevoke, "pc", AuditResults.Succeeded).Actor);
        Line(AuditActions.ModelCreate, null, AuditResults.Succeeded, l => l.ModelId == "pcm");

        // ---- no secret anywhere ----------------------------------------------------------------------------------------
        Assert.Equal(3, fleet.IssuedTokens.Count); // pc, pc2, the rekey token
        Assert.Contains(rekey.Token, fleet.IssuedTokens);
        secrets.AddRange(fleet.IssuedTokens.Select(t => ("join/rekey token", t)));
        foreach ((string what, string key) in new[]
                 {
                     ("old CA key", oldCaKey),
                     ("new CA key", await File.ReadAllTextAsync(Path.Combine(pki, CaStore.KeyFileName), Ct)),
                     ("old master client key", oldClientKey),
                     ("master client key", await File.ReadAllTextAsync(Path.Combine(pki, MasterCertificates.ClientKeyFileName), Ct)),
                     ("enroll key", await File.ReadAllTextAsync(Path.Combine(pki, MasterCertificates.EnrollKeyFileName), Ct)),
                     ("dgx agent key", await File.ReadAllTextAsync(Path.Combine(fleet.Dgx.Node.StateDirectory, FileAgentCredentials.KeyFileName), Ct)),
                     ("pc agent key", await File.ReadAllTextAsync(Path.Combine(pc.Node.StateDirectory, FileAgentCredentials.KeyFileName), Ct)),
                 })
        {
            secrets.AddRange(KeyLines(key).Select(line => (what, line)));
        }

        string auditText = fleet.AuditText();
        Assert.NotEmpty(auditText);
        string masterLog = string.Join("\n", master.Log.Records.Select(r => r.Message));
        Assert.NotEmpty(masterLog);
        string agentLogs = string.Join("\n", new[] { fleet.Dgx, pc, pc2 }.SelectMany(a => a.Log.Records).Select(r => r.Message));
        foreach ((string what, string value) in secrets)
        {
            Assert.False(auditText.Contains(value, StringComparison.Ordinal), $"the {what} is in the audit log");
            Assert.False(masterLog.Contains(value, StringComparison.Ordinal), $"the {what} is in the master log");
            Assert.False(agentLogs.Contains(value, StringComparison.Ordinal), $"the {what} is in an agent log");
        }

        Assert.DoesNotContain("PRIVATE KEY", auditText, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", masterLog, StringComparison.Ordinal);
    }

    /// <summary>The session actor of the first <paramref name="action"/> line for <paramref name="nodeId"/>.</summary>
    private static async Task<string> SessionActorAsync(P2Fleet fleet, string action, string nodeId) =>
        (await fleet.AuditAsync()).Last(l => l.Action == action && l.NodeId == nodeId).Actor;

    /// <summary>The base64 body lines of a PEM private key (any of them in a text means key material leaked).</summary>
    private static IEnumerable<string> KeyLines(string pem) =>
        pem.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("-----", StringComparison.Ordinal) && line.Length >= 32);

    private static async Task RevokeAsync(HttpClient admin, string nodeId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/nodes/{nodeId}")
        {
            Content = JsonContent.Create(new { password = Password, confirm = nodeId, unplaceModels = true }),
        };
        var (status, body) = await SendAsync(admin.SendAsync(request, Ct));
        Assert.True(status == HttpStatusCode.OK, $"revoke {nodeId}: {(int)status} {body}");
    }
}
