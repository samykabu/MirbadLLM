using System.Net;
using System.Text.Json;
using MirbadLLM.HostAgent;

namespace MirbadLLM.UnitTests.HostAgent;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// <c>GET /gpu-processes</c>: nvidia-smi's compute processes (run by argument vector, no shell) mapped to the Docker
/// container or systemd unit that owns each through <c>/proc/&lt;pid&gt;/cgroup</c>. Fixtures are the DGX's real output.
/// </summary>
public sealed class GpuProcessesTests
{
    /// <summary>The DGX's GB10 (placeholder UUID).</summary>
    private const string Gb10 = "GPU-00000000-0000-0000-0000-00000000gb10";

    /// <summary>
    /// <c>nvidia-smi --query-compute-apps=gpu_uuid,pid,process_name,used_memory --format=csv,noheader,nounits</c> on the DGX:
    /// the recorded rows with the GPU UUID column MN-106 put in front (D91).
    /// </summary>
    private const string ComputeApps = """
        GPU-00000000-0000-0000-0000-00000000gb10, 2945172, /home/dgxuser/comfyui-env/bin/python, 12327
        GPU-00000000-0000-0000-0000-00000000gb10, 3062106, VLLM::EngineCore, 3646
        GPU-00000000-0000-0000-0000-00000000gb10, 2780671, VLLM::EngineCore, 5759
        GPU-00000000-0000-0000-0000-00000000gb10, 3414913, VLLM::EngineCore, 58098
        GPU-00000000-0000-0000-0000-00000000gb10, 3419104, VLLM::EngineCore, 17037
        """;

    /// <summary>The 35B's container (real id), and made-up ids for the other model containers.</summary>
    private const string Qwen35Container = "960769d2c75febb6efba9d52da72b559ec2d1165c8bfb0410b3052ec6b63ff91";
    private const string EmbedContainer = "1111111111111111111111111111111111111111111111111111111111111111";

    private static FakeCgroupReader DgxCgroups() => new(new Dictionary<int, string>
    {
        // Captured from /proc/<pid>/cgroup (cgroup v2, systemd driver).
        [3414913] = $"/system.slice/docker-{Qwen35Container}.scope",
        [2945172] = "/system.slice/comfyui.service",
        [3419104] = $"/system.slice/docker-{EmbedContainer}.scope",
        [2780671] = "/user.slice/user-1000.slice/user@1000.service/app.slice/lmstudio-server.service",
        // 3062106: exited between nvidia-smi and the cgroup read → no owner.
    });

    [Fact]
    public void Parse_ReadsPidNameAndMiB_NameMayContainCommas_SkipsNA()
    {
        var parsed = GpuProcessInventory.Parse(ComputeApps + $"\n{Gb10}, 4000, python, worker, 1,2\n{Gb10}, 4001, x, [N/A]\nnot a line\n[N/A], 4002, VLLM::EngineCore, 700\n");

        Assert.Equal(7, parsed.Count);
        // No GPU UUID in the first column (W8 review MN-128): the process still counts, with no GPU (an even split).
        Assert.Equal(new GpuProcessInfo(4002, "VLLM::EngineCore", 700, null, null, GpuUuid: null), parsed[6]);
        Assert.Equal(new GpuProcessInfo(2945172, "/home/dgxuser/comfyui-env/bin/python", 12327, null, null, GpuUuid: Gb10), parsed[0]);
        Assert.Equal(new GpuProcessInfo(3414913, "VLLM::EngineCore", 58098, null, null, GpuUuid: Gb10), parsed[3]);
        Assert.Equal(new GpuProcessInfo(4000, "python, worker, 1", 2, null, null, GpuUuid: Gb10), parsed[5]);
    }

    [Theory]
    [InlineData("/system.slice/docker-960769d2c75febb6efba9d52da72b559ec2d1165c8bfb0410b3052ec6b63ff91.scope", "960769d2c75febb6efba9d52da72b559ec2d1165c8bfb0410b3052ec6b63ff91", null)]
    [InlineData("/docker/960769d2c75febb6efba9d52da72b559ec2d1165c8bfb0410b3052ec6b63ff91", "960769d2c75febb6efba9d52da72b559ec2d1165c8bfb0410b3052ec6b63ff91", null)]
    [InlineData("/system.slice/docker-960769d2c75febb6efba9d52da72b559ec2d1165c8bfb0410b3052ec6b63ff91.scope/init", "960769d2c75febb6efba9d52da72b559ec2d1165c8bfb0410b3052ec6b63ff91", null)]
    [InlineData("/system.slice/comfyui.service", null, "comfyui.service")]
    [InlineData("/system.slice/ollama.service", null, "ollama.service")]
    [InlineData("/user.slice/user-1000.slice/user@1000.service/app.slice/lmstudio-server.service", null, "lmstudio-server.service")]
    [InlineData("/user.slice/user-1000.slice/session-3.scope", null, null)]
    [InlineData("/system.slice/docker.service", null, "docker.service")]
    [InlineData(null, null, null)]
    public void Owner_FromCgroupV2Path(string? path, string? container, string? unit) =>
        Assert.Equal((container, unit), GpuProcessInventory.Owner(path));

    [Fact]
    public void Owner_ParsesTheRealProcCgroupFileLine() =>
        Assert.Equal(
            (Qwen35Container, (string?)null),
            GpuProcessInventory.Owner(GpuCgroupMapper.ParseCgroupFile($"0::/system.slice/docker-{Qwen35Container}.scope\n")));

    [Fact]
    public async Task Endpoint_ListsProcessesWithOwners_AndTheWhitelistDisplayName()
    {
        await using var host = await HostAgentTests.StartAsync(
            (file, _) => file == SystemctlCommands.NvidiaSmiPath ? new ProcessResult(0, ComputeApps, "") : new ProcessResult(0, "", ""),
            cgroups: DgxCgroups());

        using var response = await host.Client.GetAsync(new Uri("/gpu-processes", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var items = json.RootElement.EnumerateArray().ToList();
        Assert.Equal(5, items.Count);

        var qwen = items.Single(i => i.GetProperty("pid").GetInt32() == 3414913);
        Assert.Equal(58098, qwen.GetProperty("usedMiB").GetInt64());
        Assert.Equal("VLLM::EngineCore", qwen.GetProperty("name").GetString());
        Assert.Equal(Qwen35Container, qwen.GetProperty("containerId").GetString());
        Assert.Equal(JsonValueKind.Null, qwen.GetProperty("unit").ValueKind);
        Assert.Equal(Gb10, qwen.GetProperty("gpuUuid").GetString());

        var comfy = items.Single(i => i.GetProperty("pid").GetInt32() == 2945172);
        Assert.Equal("comfyui.service", comfy.GetProperty("unit").GetString());
        Assert.Equal("ComfyUI", comfy.GetProperty("displayName").GetString()); // from the whitelist
        Assert.Equal(JsonValueKind.Null, comfy.GetProperty("containerId").ValueKind);

        var gone = items.Single(i => i.GetProperty("pid").GetInt32() == 3062106);
        Assert.Equal(JsonValueKind.Null, gone.GetProperty("containerId").ValueKind);
        Assert.Equal(JsonValueKind.Null, gone.GetProperty("unit").ValueKind);

        // nvidia-smi ran once, by argument vector.
        var call = Assert.Single(host.Runner.Calls, c => c.FileName == SystemctlCommands.NvidiaSmiPath);
        Assert.Equal(["--query-compute-apps=gpu_uuid,pid,process_name,used_memory", "--format=csv,noheader,nounits"], call.Arguments);
    }

    [Fact]
    public async Task Endpoint_NvidiaSmiFails_Is503_NotAnEmptyList()
    {
        await using var host = await HostAgentTests.StartAsync(
            (file, _) => file == SystemctlCommands.NvidiaSmiPath ? new ProcessResult(9, "", "NVIDIA-SMI has failed") : new ProcessResult(0, "", ""));

        using var response = await host.Client.GetAsync(new Uri("/gpu-processes", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("unavailable", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Endpoint_NoGpuProcesses_IsAnEmptyList()
    {
        await using var host = await HostAgentTests.StartAsync();

        using var response = await host.Client.GetAsync(new Uri("/gpu-processes", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ServiceUnit_DoesNotHideProcessesOrPids_FromTheAgent()
    {
        // The agent reads other processes' /proc/<pid>/cgroup and runs nvidia-smi: neither may be sandboxed away.
        string unit = File.ReadAllText(FindRepoFile(Path.Combine("deploy", "hostd", "mirbadllm-hostd.service")));
        Assert.DoesNotContain("ProtectProc=invisible", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("ProtectProc=noaccess", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcSubset=pid", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateDevices=yes", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivatePIDs=yes", unit, StringComparison.Ordinal);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
