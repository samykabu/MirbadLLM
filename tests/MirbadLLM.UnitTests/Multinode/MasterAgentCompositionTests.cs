using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.UnitTests.Agent;
using MirbadLLM.UnitTests.Multinode.Contract;

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// W4 review (MN-096): the real master composition (<c>MirbadLLM.Web</c>'s <c>Program</c> on Kestrel, with its enroll
/// listener) and the real agent composition (<c>MirbadLLM.Agent.Program.CreateAsync</c>) work together in one process,
/// as on the DGX: the master writes the bootstrap bundle for <c>dgx</c>, the agent enrolls with it over the enroll
/// listener (CA hash, bootstrap token), the node comes Online, a model starts through the agent (fake Docker engine,
/// <see cref="Fakes.FakeModelServer"/> behind the container) and <c>/v1/chat/completions</c> streams through the master's
/// router and the agent's proxy over mTLS.
/// </summary>
public sealed class MasterAgentCompositionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheMasterBootstrapsTheRealAgentAsDgxAndServesV1ThroughIt()
    {
        int enrollPort = TestPorts.Reserve();
        int agentPort = TestPorts.Reserve();
        await using var node = await ContractNode.CreateAsync(enroll: false, Ct, nodeId: "dgx");
        string dataDir = Path.Combine(node.Root, "master-data");
        string bootstrapDir = Path.Combine(node.Root, "bootstrap");
        await SeedModelAsync(dataDir);

        await using var master = new KestrelMasterFactory(dataDir, new Dictionary<string, string?>
        {
            ["MirbadLLM:Enrollment:Port"] = enrollPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["MirbadLLM:Enrollment:BootstrapDirectory"] = bootstrapDir,
            ["MirbadLLM:Enrollment:BootstrapEnrollEndpoint"] = $"https://127.0.0.1:{enrollPort}",
            ["MirbadLLM:Enrollment:BootstrapAgentAddress"] = $"https://127.0.0.1:{agentPort}",
        });
        master.UseKestrel();
        master.StartServer();
        Uri baseAddress = master.HttpAddress();
        using var http = new HttpClient { BaseAddress = baseAddress };

        // The master created the bootstrap node's record and its bundle; nothing is enrolled yet.
        var registry = master.Services.GetRequiredService<INodeRegistry>();
        var record = await registry.GetAsync("dgx", Ct);
        Assert.NotNull(record);
        Assert.Null(record.CertSpkiSha256);

        // The agent starts in bootstrap mode (as agent.json on the DGX), enrolls from the bundle and opens its listener.
        await using var agent = await RealAgentHarness.StartAsync(node, o =>
        {
            o.Port = agentPort;
            o.BootstrapDirectory = bootstrapDir;
            o.BootstrapWait = TimeSpan.FromSeconds(60);
            o.Addresses.Add("127.0.0.1");
            o.AllowTagPulls = true; // the P1 DGX setting (today's images are tags)
        }, Ct);

        // Enrolled with the bootstrap token (pre-approved, D48); the bundle is gone.
        var enrolled = await registry.GetAsync("dgx", Ct);
        Assert.NotNull(enrolled?.CertSpkiSha256);
        Assert.True(enrolled.Approved);
        Assert.False(File.Exists(Path.Combine(bootstrapDir, BootstrapBundle.FileName)));

        // The node comes Online through the registry sync and the node monitor.
        var cache = master.Services.GetRequiredService<INodeStatusCache>();
        await WaitUntilAsync(() => Task.FromResult(cache.GetNode("dgx")?.State == NodeState.Online), "dgx never came Online");

        // Folders come from the real agent's models root.
        using (var folders = await http.GetAsync("/api/model-folders", Ct))
        {
            string body = await folders.Content.ReadAsStringAsync(Ct);
            Assert.True(folders.StatusCode == HttpStatusCode.OK, $"{(int)folders.StatusCode} {body}");
            Assert.Contains(AgentContractSeed.ModelFolder, body, StringComparison.Ordinal);
        }

        // A start through the admin API launches a container through the agent.
        using (var start = await http.PostAsync("/api/models/qwen/start", content: null, Ct))
        {
            string body = await start.Content.ReadAsStringAsync(Ct);
            Assert.True(start.StatusCode == HttpStatusCode.OK, $"{(int)start.StatusCode} {body}");
        }

        Assert.Contains(node.Docker.ModelContainers, name => name.Contains("qwen", StringComparison.Ordinal));
        await WaitUntilAsync(async () =>
        {
            using var models = await http.GetAsync("/v1/models", Ct);
            return models.StatusCode == HttpStatusCode.OK && (await models.Content.ReadAsStringAsync(Ct)).Contains("\"qwen\"", StringComparison.Ordinal);
        }, "qwen never appeared in /v1/models");

        // /v1 through the master's router and the agent's proxy, buffered and streamed.
        using (var chat = await http.PostAsJsonAsync(
            "/v1/chat/completions",
            new { model = "qwen", messages = new[] { new { role = "user", content = "hi" } }, max_tokens = 4 },
            Ct))
        {
            string body = await chat.Content.ReadAsStringAsync(Ct);
            Assert.True(chat.StatusCode == HttpStatusCode.OK, $"{(int)chat.StatusCode}: {body}");
            using var json = JsonDocument.Parse(body);
            Assert.True(json.RootElement.TryGetProperty("choices", out _), body);
        }

        using (var stream = await http.PostAsJsonAsync(
            "/v1/chat/completions",
            new { model = "qwen", messages = new[] { new { role = "user", content = "hi" } }, max_tokens = 4, stream = true },
            Ct))
        {
            string body = await stream.Content.ReadAsStringAsync(Ct);
            Assert.True(stream.StatusCode == HttpStatusCode.OK, $"{(int)stream.StatusCode}: {body}");
            Assert.Equal("text/event-stream", stream.Content.Headers.ContentType?.MediaType);
            Assert.Contains("data: [DONE]", body, StringComparison.Ordinal);
        }

        // A stop through the admin API removes the container on the node.
        using (var stop = await http.PostAsync("/api/models/qwen/stop", content: null, Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        }

        await WaitUntilAsync(() => Task.FromResult(node.Docker.ModelContainers.Count == 0), "the model container was not removed");
    }

    private static async Task SeedModelAsync(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        using var registry = new JsonModelRegistry(Options.Create(new MirbadLLMOptions { DataPath = dataDir }));
        foreach (var existing in await registry.ListAsync(Ct))
        {
            await registry.DeleteAsync(existing.Id, Ct);
        }

        await registry.UpsertAsync(new ModelDefinition
        {
            Id = "qwen",
            ServedName = "qwen",
            Image = AgentContractSeed.Image,
            ModelPath = AgentContractSeed.ModelFolder,
            MemoryBudgetGiB = 1,
            MaxModelLen = 8192,
            MaxNumSeqs = 16,
            Placement = new ModelPlacement("dgx"),
        }, Ct);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string message)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, message);
            await Task.Delay(100, Ct);
        }
    }

    /// <summary>
    /// <c>Program</c> on real Kestrel (so the enroll listener exists) over <paramref name="dataDir"/>, with the background
    /// services on and no admin password; legacy host settings point nowhere.
    /// </summary>
    private sealed class KestrelMasterFactory(string dataDir, Dictionary<string, string?> settings) : WebApplicationFactory<Program>
    {
        public Uri HttpAddress()
        {
            var addresses = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
            string address = addresses.First(a => a.StartsWith("http://", StringComparison.Ordinal));
            return new Uri(address.Replace("://[::]", "://127.0.0.1", StringComparison.Ordinal).Replace("://0.0.0.0", "://127.0.0.1", StringComparison.Ordinal));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("urls", "http://127.0.0.1:0");
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var all = new Dictionary<string, string?>
                {
                    ["urls"] = "http://127.0.0.1:0",
                    ["MirbadLLM:DataPath"] = dataDir,
                    ["MirbadLLM:AdminPassword"] = null,
                    ["MirbadLLM:DockerEndpoint"] = "unix:///nonexistent/mirbadllm-tests/docker.sock",
                    ["MirbadLLM:HostAgentSocket"] = Path.Combine(dataDir, "no-hostd.sock"),
                    ["MirbadLLM:Backup:Directory"] = Path.Combine(dataDir, "backups"),
                };
                foreach ((string key, string? value) in settings)
                {
                    all[key] = value;
                }

                config.AddInMemoryCollection(all);
            });
        }
    }
}
