using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Audit;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.Orchestration.Fencing;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.UnitTests.Auth;

namespace MirbadLLM.UnitTests.Multinode;

#pragma warning disable CA1707 // MethodUnderTest_Scenario_ExpectedBehavior test naming style, used throughout this project.

/// <summary>
/// The Wave 7 APIs wired into the real master by the W7 review (MN-126): node admin (MN-102), takeover (MN-115), audit
/// with the pre-P2 route filter (MN-116), placement validators and the ownership store (MN-108), per-node job options
/// (MN-111). The Wave 8 UI tasks rely on these being composed.
/// </summary>
public sealed class W7CompositionTests
{
    private static readonly Regex AntiforgeryTokenPattern = new(
        """name="__RequestVerificationToken" value="([^"]+)""", RegexOptions.Compiled);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheMaster_ComposesTheWave7Services()
    {
        using var factory = new AuthWebFactory();
        factory.Settings["MirbadLLM:Jobs:MaxConcurrent"] = "3";
        _ = factory.Server;
        IServiceProvider services = factory.Services;

        Assert.IsType<JsonlAuditLog>(services.GetRequiredService<IAuditLog>());
        Assert.IsType<AuditingOperationReferenceStore>(services.GetRequiredService<MirbadLLM.Orchestration.Agents.IAgentOperationReferenceStore>());
        Assert.IsType<TakeoverService>(services.GetRequiredService<IFencingCoordinator>());
        Assert.IsType<OwnershipStore>(services.GetRequiredService<IOwnershipStore>());
        Assert.Equal(
            [typeof(OwnershipValidator), typeof(PlacementValidator)],
            services.GetServices<IModelDefinitionValidator>().Select(v => v.GetType()).ToArray());

        Assert.IsType<MirbadLLM.Web.NodeHostServices>(services.GetRequiredService<MirbadLLM.Web.INodeHostServices>());

        var jobs = services.GetRequiredService<ModelJobService>();
        Assert.Equal(3, jobs.MaxConcurrent);
        Assert.Equal("dgx", jobs.BootstrapNodeId);
        Assert.Equal(3, services.GetRequiredService<IOptions<JobOptions>>().Value.MaxConcurrent);
    }

    [Fact]
    public async Task ThePreP2Routes_AreAudited_WithOneStableActorPerLogin_AndTheP2RoutesAnswer()
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);

        // Two pre-P2 mutating calls in one session: one actor, derived from the sid claim (not from the cookie).
        using (var created = await client.PostAsJsonAsync("/api/keys/", new { name = "w7-composition" }, Ct))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using (var refused = await client.PostAsJsonAsync("/api/keys/", new { name = " " }, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        using var audit = await client.GetAsync("/api/audit", Ct);
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        using var lines = JsonDocument.Parse(await audit.Content.ReadAsStringAsync(Ct));
        var keyLines = lines.RootElement.EnumerateArray()
            .Where(l => l.GetProperty("action").GetString() == AuditActions.ApiKey)
            .ToList();
        Assert.Equal(2, keyLines.Count);
        string actor = keyLines[0].GetProperty("actor").GetString()!;
        Assert.StartsWith("admin:", actor, StringComparison.Ordinal);
        Assert.Equal(actor, keyLines[1].GetProperty("actor").GetString());
        Assert.Contains(keyLines, l => l.GetProperty("result").GetString() == AuditResults.Refused);

        // The P2 routes the Wave 8 pages read.
        using var nodes = await client.GetAsync("/api/nodes", Ct);
        Assert.Equal(HttpStatusCode.OK, nodes.StatusCode);
        Assert.Contains("\"dgx\"", await nodes.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using var fencing = await client.GetAsync("/api/fencing", Ct);
        Assert.Equal(HttpStatusCode.OK, fencing.StatusCode);

        using var jobs = await client.GetAsync("/api/jobs", Ct);
        Assert.Equal(HttpStatusCode.OK, jobs.StatusCode);
        Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(await jobs.Content.ReadAsStringAsync(Ct)).RootElement.ValueKind);
    }

    [Fact]
    public async Task TheP2Routes_AreAdminOnly()
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (string path in new[] { "/api/nodes", "/api/fencing", "/api/audit", "/api/jobs" })
        {
            using var response = await client.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    private static async Task LoginAsync(HttpClient client)
    {
        using var page = await client.GetAsync("/login", Ct);
        page.EnsureSuccessStatusCode();
        var match = AntiforgeryTokenPattern.Match(await page.Content.ReadAsStringAsync(Ct));
        Assert.True(match.Success);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = match.Groups[1].Value,
            ["password"] = "correct-password",
            ["returnUrl"] = "",
        });
        using var login = await client.PostAsync("/login/submit", form, Ct);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location?.ToString());
    }
}
