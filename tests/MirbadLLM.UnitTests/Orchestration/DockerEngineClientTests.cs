using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using MirbadLLM.Orchestration;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

namespace MirbadLLM.UnitTests.Orchestration;

public sealed class DockerEngineClientTests
{
    private static DockerEngineClient CreateClient(FakeHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://docker/v1.47/") });

    private static byte[] Frame(byte streamType, string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        var header = new byte[8];
        header[0] = streamType;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), payload.Length);
        return [.. header, .. payload];
    }

    [Fact]
    public async Task GetLogsAsync_DemuxesMultiplexedFrames_IntoLines()
    {
        var handler = new FakeHttpMessageHandler();
        byte[] bytes = [.. Frame(1, "line one\n"), .. Frame(2, "line two\n"), .. Frame(1, "partial-no-newline")];
        handler.Handler = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        var client = CreateClient(handler);

        var lines = new List<string>();
        await foreach (var line in client.GetLogsAsync("abc123", tail: 20, follow: false))
        {
            lines.Add(line);
        }

        Assert.Equal(["line one", "line two", "partial-no-newline"], lines);
    }

    [Fact]
    public async Task GetLogsAsync_LineSplitAcrossTwoFrames_IsJoinedCorrectly()
    {
        var handler = new FakeHttpMessageHandler();
        byte[] bytes = [.. Frame(1, "hello "), .. Frame(1, "world\n")];
        handler.Handler = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        var client = CreateClient(handler);

        var lines = new List<string>();
        await foreach (var line in client.GetLogsAsync("abc123", tail: 20, follow: false))
        {
            lines.Add(line);
        }

        Assert.Equal(["hello world"], lines);
    }

    [Fact]
    public async Task ListContainersAsync_ParsesContainerSummaries()
    {
        var handler = new FakeHttpMessageHandler();
        const string json = """
            [
              {
                "Id": "abc123",
                "Names": ["/ss-ornith-9b-fp8"],
                "State": "running",
                "Status": "Up 5 minutes",
                "Labels": { "mirbadllm.model": "ornith-9b-fp8", "mirbadllm.port": "8101" }
              }
            ]
            """;
        handler.Handler = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        var client = CreateClient(handler);

        var containers = await client.ListContainersAsync(["mirbadllm.model"]);

        var container = Assert.Single(containers);
        Assert.Equal("abc123", container.Id);
        Assert.Equal("ss-ornith-9b-fp8", container.Name);
        Assert.Equal("running", container.State);
        Assert.Equal("8101", container.Labels["mirbadllm.port"]);
    }

    [Fact]
    public async Task CreateContainerAsync_SendsGpuBindsPortLabelsAndNetworkAlias()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"Id":"deadbeef"}"""),
        };
        var client = CreateClient(handler);

        var request = new CreateContainerRequest(
            Name: "ss-ornith-9b-fp8",
            Image: "local/qwen38-vllm-dflash2:66e5414c6d75",
            Cmd: ["--model", "/models/x"],
            Labels: new Dictionary<string, string>
            {
                ["mirbadllm.model"] = "ornith-9b-fp8",
                ["mirbadllm.served-name"] = "Ornith-Uncensored-FP8",
                ["mirbadllm.port"] = "8101",
            },
            Binds: ["/srv/mirbadllm/models:/models:ro", "/srv/mirbadllm/cache:/root/.cache"],
            ContainerPort: 8000,
            HostPort: 8101,
            HostIp: "127.0.0.1",
            Network: "mirbadllm",
            NetworkAlias: "ss-ornith-9b-fp8",
            IpcMode: "host");

        string containerId = await client.CreateContainerAsync(request);

        Assert.Equal("deadbeef", containerId);

        var createRequest = handler.Requests.Single(r => r.RequestUri!.AbsolutePath.EndsWith("containers/create", StringComparison.Ordinal));
        Assert.Contains("name=ss-ornith-9b-fp8", createRequest.RequestUri!.Query);

        int bodyIndex = handler.Requests.IndexOf(createRequest);
        using var body = JsonDocument.Parse(handler.RequestBodies[bodyIndex]);
        var root = body.RootElement;

        Assert.Equal("local/qwen38-vllm-dflash2:66e5414c6d75", root.GetProperty("Image").GetString());

        var hostConfig = root.GetProperty("HostConfig");
        var deviceRequest = hostConfig.GetProperty("DeviceRequests")[0];
        Assert.Equal("nvidia", deviceRequest.GetProperty("Driver").GetString());
        Assert.Equal(-1, deviceRequest.GetProperty("Count").GetInt32());
        Assert.Equal("gpu", deviceRequest.GetProperty("Capabilities")[0][0].GetString());

        Assert.Equal("host", hostConfig.GetProperty("IpcMode").GetString());
        Assert.Equal("no", hostConfig.GetProperty("RestartPolicy").GetProperty("Name").GetString());

        var binds = hostConfig.GetProperty("Binds").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("/srv/mirbadllm/models:/models:ro", binds);
        Assert.Contains("/srv/mirbadllm/cache:/root/.cache", binds);

        var portBinding = hostConfig.GetProperty("PortBindings").GetProperty("8000/tcp")[0];
        Assert.Equal("127.0.0.1", portBinding.GetProperty("HostIp").GetString());
        Assert.Equal("8101", portBinding.GetProperty("HostPort").GetString());

        var labels = root.GetProperty("Labels");
        Assert.Equal("ornith-9b-fp8", labels.GetProperty("mirbadllm.model").GetString());
        Assert.Equal("8101", labels.GetProperty("mirbadllm.port").GetString());

        var aliases = root.GetProperty("NetworkingConfig").GetProperty("EndpointsConfig").GetProperty("mirbadllm").GetProperty("Aliases");
        Assert.Equal("ss-ornith-9b-fp8", aliases[0].GetString());
    }
}
