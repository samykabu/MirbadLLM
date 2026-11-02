using System.Net;
using System.Text;
using System.Text.Json;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using FakeHttpMessageHandler = MirbadLLM.UnitTests.Orchestration.Fakes.FakeHttpMessageHandler;

namespace MirbadLLM.UnitTests.HostServices;

/// <summary>A scripted Docker Engine: inspect, stats, start/stop/restart and restart-policy updates on named containers.</summary>
internal sealed class FakeDocker
{
    public sealed class Container
    {
        public required string Name { get; init; }

        public string Id => "id-" + Name;

        public string Status { get; set; } = "running";

        public int ExitCode { get; set; }

        public string RestartPolicy { get; set; } = "always";

        public long Usage { get; set; } = 600L * 1024 * 1024;

        public long InactiveFile { get; set; } = 100L * 1024 * 1024;

        public Dictionary<string, string> Labels { get; init; } = [];
    }

    public Dictionary<string, Container> Containers { get; } = new(StringComparer.Ordinal);

    public FakeHttpMessageHandler Handler { get; } = new();

    public List<string> Posts { get; } = [];

    public FakeDocker()
    {
        Handler.Handler = Respond;
    }

    public Container Add(string name, Action<Container>? configure = null)
    {
        var c = new Container { Name = name };
        configure?.Invoke(c);
        Containers[name] = c;
        return c;
    }

    public DockerServiceClient Client() => new(new HttpClient(Handler) { BaseAddress = new Uri("http://docker/v1.47/") });

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        string path = request.RequestUri!.AbsolutePath["/v1.47/".Length..];
        if (request.Method == HttpMethod.Get && path == "containers/json")
        {
            return Json(Containers.Values.Select(c => new
            {
                c.Id,
                Names = new[] { "/" + c.Name },
                Image = "img/" + c.Name,
                State = c.Status,
                Status = c.Status == "running" ? "Up 2 hours" : "Exited (0) 1 hour ago",
                c.Labels,
            }));
        }

        string[] parts = path.Split('/');
        string key = Uri.UnescapeDataString(parts[1]);
        var container = Containers.Values.FirstOrDefault(c => c.Name == key || c.Id == key);
        if (container is null)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"No such container"}""") };
        }

        string op = parts[2];
        if (request.Method == HttpMethod.Get && op == "json")
        {
            return Json(new
            {
                Id = container.Id,
                Name = "/" + container.Name,
                State = new { Status = container.Status, container.ExitCode, OOMKilled = false, StartedAt = "2026-09-29T10:00:00.123456789Z" },
                HostConfig = new { RestartPolicy = new { Name = container.RestartPolicy } },
                Config = new { container.Labels, Image = "img/" + container.Name },
            });
        }

        if (request.Method == HttpMethod.Get && op == "stats")
        {
            return container.Status == "running"
                ? Json(new { memory_stats = new { usage = container.Usage, stats = new { inactive_file = container.InactiveFile } } })
                : Json(new { memory_stats = new { } });
        }

        Posts.Add($"{op} {container.Name}");
        string body = request.Content is null ? "" : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        switch (op)
        {
            case "update":
                using (var doc = JsonDocument.Parse(body))
                {
                    container.RestartPolicy = doc.RootElement.GetProperty("RestartPolicy").GetProperty("Name").GetString()!;
                }

                return Json(new { Warnings = Array.Empty<string>() });
            case "stop":
                container.Status = "exited";
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            case "start":
            case "restart":
                container.Status = "running";
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            default:
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }
    }

    public static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}

/// <summary>A scripted mirbadllm-hostd, including discovery, the whitelist routes and deny-list refusals.</summary>
internal sealed class FakeAgent
{
    public FakeHttpMessageHandler Handler { get; } = new();

    private static readonly string[] DenyPatterns = ["ssh*"];

    /// <summary>Units the fake reports in discovery that are not whitelisted.</summary>
    public List<HostAgentDiscoveredUnit> Unmanaged { get; } =
    [
        new("smartmontools", "smartmontools.service", "system", null, "SMART daemon", "active", "running", "enabled", 1_790_443_886, 2_269_184, 0, false, false, null),
        new("ssh", "ssh.service", "system", null, "OpenBSD Secure Shell server", "active", "running", "disabled", 1_790_443_900, 17_473_536, 0, false, true, "ssh*"),
    ];

    public bool Installed { get; set; } = true;

    public List<HostAgentUnit> Units { get; } =
    [
        Unit("comfyui", "comfyui.service", "system", isProtected: false, memory: 1_649_680_384, gpu: 40144L * 1024 * 1024),
        Unit("lmstudio-server", "lmstudio-server.service", "user", isProtected: false, memory: 142_835_712, gpu: 0),
        Unit("rustdesk", "rustdesk.service", "system", isProtected: true, memory: 105_582_592, gpu: 0),
    ];

    public FakeAgent()
    {
        Handler.Handler = request =>
        {
            string path = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (request.Method == HttpMethod.Get && path == "services" && request.RequestUri.Query.Length == 0)
            {
                return FakeDocker.Json(new { services = Units });
            }

            if (request.Method == HttpMethod.Get && path == "services" && request.RequestUri.Query == "?include=all")
            {
                var discovered = Units.Select(u => new HostAgentDiscoveredUnit(u.Name, u.Unit, u.Scope, u.User, u.Description, u.ActiveState,
                        u.SubState, u.UnitFileState, u.ActiveSinceUnix, u.MemoryBytes, u.GpuMemoryBytes, true, false, null))
                    .Concat(Unmanaged);
                return FakeDocker.Json(new { services = Units, discovered, discoveryErrors = Array.Empty<string>(), denyPatterns = DenyPatterns });
            }

            string[] parts = path.Split('/');
            if (parts[0] == "whitelist")
            {
                return Whitelist(request, parts);
            }

            var unit = Units.FirstOrDefault(u => u.Name == parts[1]);
            return unit is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"not in the allowlist"}""") }
                : FakeDocker.Json(unit);
        };
    }

    private HttpResponseMessage Whitelist(HttpRequestMessage request, string[] parts)
    {
        if (request.Method == HttpMethod.Post)
        {
            using var doc = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            string unitName = doc.RootElement.GetProperty("unit").GetString()!;
            var found = Unmanaged.FirstOrDefault(u => u.Unit == unitName);
            if (found is { Denied: true })
            {
                return Error(HttpStatusCode.Forbidden, "denied", $"{unitName} is a system service (matches '{found.DeniedBy}') and cannot be managed.");
            }

            if (found is null)
            {
                return Error(HttpStatusCode.NotFound, "not-found", "no such unit");
            }

            if (doc.RootElement.GetProperty("confirm").GetString() != found.Name)
            {
                return Error(HttpStatusCode.Forbidden, "confirm", "type the name");
            }

            var added = Unit(found.Name, found.Unit, found.Scope, doc.RootElement.GetProperty("protected").GetBoolean(), found.MemoryBytes, 0);
            Units.Add(added);
            Unmanaged.Remove(found);
            return FakeDocker.Json(added);
        }

        var existing = Units.FirstOrDefault(u => u.Name == parts[1]);
        if (existing is null)
        {
            return Error(HttpStatusCode.NotFound, "not-found", "not in the whitelist");
        }

        if (request.Method == HttpMethod.Delete)
        {
            Units.Remove(existing);
            return FakeDocker.Json(new { removed = existing.Name });
        }

        using var patch = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        bool? isProtected = patch.RootElement.TryGetProperty("protected", out var p) && p.ValueKind != JsonValueKind.Null ? p.GetBoolean() : null;
        string? confirm = patch.RootElement.TryGetProperty("confirm", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        if (existing.Protected && isProtected == false && confirm != existing.Name)
        {
            return Error(HttpStatusCode.Forbidden, "confirm", "confirm to unprotect");
        }

        var updated = existing with { Protected = isProtected ?? existing.Protected };
        Units[Units.IndexOf(existing)] = updated;
        return FakeDocker.Json(updated);
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code, string message) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(new { error = message, code }), Encoding.UTF8, "application/json") };

    public HostAgentClient Client() =>
        new(new HttpClient(Handler) { BaseAddress = new Uri("http://hostd/") }, () => Installed);

    public static HostAgentUnit Unit(string name, string unit, string scope, bool isProtected, long? memory, long? gpu, string active = "active") =>
        new(name, unit, scope, scope == "user" ? "dgxuser" : null, name, "", isProtected, isProtected ? "It matters." : null,
            active, active == "active" ? "running" : "dead", "enabled", 42, 1_790_687_285, memory, gpu, null);
}

internal static class ServicesTestSetup
{
    public static (HostServiceManager Manager, FakeDocker Docker, FakeAgent Agent, string DataFile) Create(
        IEnumerable<ManagedContainerOptions>? containers = null,
        IAdminReauthenticator? reauth = null)
    {
        var docker = new FakeDocker();
        var agent = new FakeAgent();
        string dataFile = Path.Combine(Path.GetTempPath(), "mirbadllm-host-services-" + Guid.NewGuid().ToString("N") + ".json");
        var options = new HostServicesOptions();
        if (containers is not null)
        {
            options.ManagedContainers.AddRange(containers);
        }

        var manager = new HostServiceManager(
            agent.Client(),
            docker.Client(),
            new HostServicesStore(dataFile),
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HostServiceManager>.Instance,
            reauth);
        return (manager, docker, agent, dataFile);
    }
}

/// <summary>Accepts one password; counts calls; can pretend login is off or the budget is used up.</summary>
internal sealed class FakeReauth : IAdminReauthenticator
{
    public bool LoginEnabled { get; set; } = true;

    public bool Exhausted { get; set; }

    public int Calls { get; private set; }

    public AdminReauthOutcome Verify(System.Security.Claims.ClaimsPrincipal? user)
    {
        Calls++;
        if (!LoginEnabled)
        {
            return AdminReauthOutcome.LoginDisabled;
        }

        if (Exhausted)
        {
            return AdminReauthOutcome.RateLimited;
        }

        return user?.Identity?.IsAuthenticated == true ? AdminReauthOutcome.Verified : AdminReauthOutcome.LoginRequired;
    }
}
