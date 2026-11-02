using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Agent;
using MirbadLLM.UnitTests.Multinode.Contract;
using static MirbadLLM.UnitTests.Multinode.P1Exit.ExitEnv;

namespace MirbadLLM.UnitTests.Multinode.P1Exit;

/// <summary>
/// W3 review (MN-094) follow-ups on MN-043 that need the real master and the real agent together: an older bootstrap
/// token cannot re-enroll the enrolled <c>dgx</c> (D48), and a gated Hugging Face repo answers 422 through the agent (D55).
/// (D49 is in <see cref="RotationExitTests"/>, D51 in <see cref="UnavailableNodeExitTests"/>, D52 in
/// <see cref="FencingExitTests"/>.)
/// </summary>
public sealed class ReviewFollowUpExitTests
{
    [Fact]
    public async Task AnotherUnusedBootstrapTokenCannotReEnrollTheEnrolledDgx()
    {
        int enrollPort = TestPorts.Reserve();
        int agentPort = TestPorts.Reserve();
        await using var node = await ContractNode.CreateAsync(enroll: false, Ct, nodeId: NodeId);
        await using var intruder = await ContractNode.CreateAsync(enroll: false, Ct, nodeId: NodeId);
        string dataDir = NewDirectory("master");
        try
        {
            string bootstrapDir = Path.Combine(node.Root, "bootstrap");
            await SeedModelsAsync(dataDir);

            // The DGX set-up: the master (own CA in /data/pki) on Kestrel with its enroll listener writes dgx's bundle.
            await using var master = new ExitMaster(dataDir, ca: null, new Dictionary<string, string?>
            {
                ["urls"] = "http://127.0.0.1:0",
                ["MirbadLLM:Enrollment:Port"] = enrollPort.ToString(CultureInfo.InvariantCulture),
                ["MirbadLLM:Enrollment:BootstrapDirectory"] = bootstrapDir,
                ["MirbadLLM:Enrollment:BootstrapEnrollEndpoint"] = $"https://127.0.0.1:{enrollPort}",
                ["MirbadLLM:Enrollment:BootstrapAgentAddress"] = $"https://127.0.0.1:{agentPort}",
            });
            master.UseKestrel();
            master.StartServer();
            string bundlePath = Path.Combine(bootstrapDir, BootstrapBundle.FileName);
            await UntilAsync(() => File.Exists(bundlePath), "the bootstrap bundle");
            var bundle = JsonNode.Parse(await File.ReadAllTextAsync(bundlePath, Ct))!.AsObject();
            Assert.True(bundle.ContainsKey("token"));

            // Another bootstrap token for dgx exists, unused and unexpired (an older bundle's).
            var registry = master.Get<INodeRegistry>();
            var other = await registry.CreateJoinTokenAsync(NodeId, TimeSpan.FromHours(1), JoinTokenPurpose.Bootstrap, Ct);

            // The real agent enrolls with the current bundle (pre-approved) and serves.
            await using var agent = await RealAgentHarness.StartAsync(node, o =>
            {
                o.Port = agentPort;
                o.BootstrapDirectory = bootstrapDir;
                o.BootstrapWait = TimeSpan.FromSeconds(60);
                o.Addresses.Add("127.0.0.1");
                o.AllowTagPulls = true;
            }, Ct);
            var enrolled = (await registry.GetAsync(NodeId, Ct))!;
            Assert.NotNull(enrolled.CertSpkiSha256);
            await master.WaitForNodeAsync(n => n.State == NodeState.Online, "dgx Online");

            // A node without identity presents the other token in a bundle: enrollment is refused, nothing changes.
            string intruderBootstrap = Path.Combine(intruder.Root, "bootstrap");
            Directory.CreateDirectory(intruderBootstrap);
            bundle["token"] = other.Token;
            await File.WriteAllTextAsync(Path.Combine(intruderBootstrap, BootstrapBundle.FileName), bundle.ToJsonString(), Ct);
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await using var refused = await RealAgentHarness.StartAsync(intruder, o =>
                {
                    o.Port = 0; // any free port: nothing needs to know it in advance
                    o.BootstrapDirectory = intruderBootstrap;
                    o.BootstrapWait = TimeSpan.FromSeconds(3);
                    o.Addresses.Add("127.0.0.1");
                }, Ct);
            });

            var after = (await registry.GetAsync(NodeId, Ct))!;
            Assert.Equal(enrolled.Uid, after.Uid);
            Assert.Equal(enrolled.CertSpkiSha256, after.CertSpkiSha256);
            Assert.Equal(enrolled.Uid, (await master.Client.GetInfoAsync(Ct)).NodeUid);
            Assert.Equal(NodeState.Online, master.Cache.GetNode(NodeId)?.State);
        }
        finally
        {
            DeleteDirectory(dataDir);
        }
    }

    [Fact]
    public async Task AGatedHuggingFaceRepoAnswers422ThroughTheAgent()
    {
        const string refusal = "Access to model contract/tiny-model is restricted. You must have access to it and be authenticated to access it.";
        await using var node = await RealNode.StartAsync(prepare: n => n.Downloads.HubRefusal = refusal);
        await using var master = await node.Master().StartAsync();

        // The agent marks the Hub's refusal invalid_request with its source (D55) …
        var agentAnswer = await Assert.ThrowsAsync<AgentException>(() => master.Client.StartDownloadAsync(AgentContractSuite.Download("gated-a"), Ct));
        Assert.Equal(ErrorCodes.InvalidRequest, agentAnswer.Code);
        Assert.Contains("huggingface", agentAnswer.Error.Details?.GetRawText() ?? "", StringComparison.Ordinal);

        // … and the master's download API answers today's 422 with the Hub's text.
        var (status, body) = await SendAsync(master.Http.PostAsync("/api/downloads",
            JsonContent.Create(new DownloadRequest(AgentContractSeed.DownloadRepo, Folder: "gated-b")), Ct));
        Assert.True(status == HttpStatusCode.UnprocessableEntity, $"{(int)status} {body}");
        Assert.Contains(refusal, AdmissionExitTests.Detail(body), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(node.Node.ModelsRoot, "gated-b")));
    }
}
