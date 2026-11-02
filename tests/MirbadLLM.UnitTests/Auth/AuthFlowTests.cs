using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Web.Api;

namespace MirbadLLM.UnitTests.Auth;

#pragma warning disable CA1707 // MethodUnderTest_Scenario_ExpectedBehavior test naming style, used throughout this project.

/// <summary>
/// End-to-end auth tests against the real app composition (<see cref="AuthWebFactory"/>): login/logout,
/// the admin cookie, the login rate limiter, <c>/api/*</c> returning 401 JSON instead of a redirect, pages
/// redirecting to <c>/login</c>, and auth being disabled entirely when no admin password is configured.
/// </summary>
public sealed class AuthFlowTests
{
    private static readonly Regex AntiforgeryTokenPattern = new(
        """name="__RequestVerificationToken" value="([^"]+)""", RegexOptions.Compiled);

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.GetAsync("/login", ct);
        response.EnsureSuccessStatusCode();
        string html = await response.Content.ReadAsStringAsync(ct);
        var match = AntiforgeryTokenPattern.Match(html);
        Assert.True(match.Success, $"Could not find an antiforgery token in the /login page: {html}");
        return match.Groups[1].Value;
    }

    private static FormUrlEncodedContent LoginForm(string token, string password, string? returnUrl = null) =>
        new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["password"] = password,
            ["returnUrl"] = returnUrl ?? "",
        });

    [Fact]
    public async Task LoginWithCorrectPassword_ThenProtectedPageAndApiSucceed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new AuthWebFactory { AdminPassword = "correct-password" };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        string token = await GetAntiforgeryTokenAsync(client, ct);
        using var loginResponse = await client.PostAsync("/login/submit", LoginForm(token, "correct-password"), ct);
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.Equal("/", loginResponse.Headers.Location?.ToString());

        using var api = await client.GetAsync("/api/models", ct);
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
    }

    [Fact]
    public async Task AutomaticAgentUpdates_RequireTheLogin_ButNoPasswordReentry()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new AuthWebFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var anonymous = await client.PutAsJsonAsync("/api/agent-updates/settings", new { automatic = true }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var settings = factory.Services.GetRequiredService<MirbadLLM.Orchestration.Agents.AgentUpdateSettingsStore>();
        Assert.False(settings.Automatic);

        string token = await GetAntiforgeryTokenAsync(client, ct);
        using var login = await client.PostAsync("/login/submit", LoginForm(token, "correct-password"), ct);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        using var enabled = await client.PutAsJsonAsync("/api/agent-updates/settings", new { automatic = true }, ct);
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.True(settings.Automatic);
        using var disabled = await client.PutAsJsonAsync("/api/agent-updates/settings", new { automatic = false }, ct);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.False(settings.Automatic);

        using var manual = await client.PostAsJsonAsync("/api/agent-updates", new { }, ct);
        Assert.Equal(HttpStatusCode.Accepted, manual.StatusCode);
    }

    [Fact]
    public async Task PortalMutations_RejectAnonymousRequests_EvenWithTheAdminPasswordOrForgedUser()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new AuthWebFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (string Method, string Path)[] actions =
        [
            ("POST", "/api/nodes/join-tokens"), ("DELETE", "/api/nodes/dgx"),
            ("POST", "/api/nodes/dgx/agent-update"), ("POST", "/api/agent-updates"),
            ("POST", "/api/nodes/dgx/approve"), ("POST", "/api/nodes/dgx/reject"),
            ("POST", "/api/nodes/dgx/drain"), ("POST", "/api/nodes/dgx/undrain"),
            ("POST", "/api/services/whitelist"), ("PATCH", "/api/services/whitelist/example"),
            ("POST", "/api/fencing/takeover"), ("POST", "/api/fencing/takeover/continue"),
            ("POST", "/api/models/example/move"), ("POST", "/api/models/example/move/force-commit"),
            ("POST", "/api/models/example/unplace"), ("DELETE", "/api/models/example"),
        ];
        foreach (var (method, path) in actions)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path)
            {
                Content = JsonContent.Create(new { password = factory.AdminPassword, confirm = "dgx", user = new { isAuthenticated = true } }),
            };
            using var response = await client.SendAsync(request, ct);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path}: {response.StatusCode}");
        }
    }

    [Fact]
    public async Task NodeEnrollment_UsesTheLoginSession_WithoutSendingAPassword()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new AuthWebFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        string token = await GetAntiforgeryTokenAsync(client, ct);
        using var login = await client.PostAsync("/login/submit", LoginForm(token, factory.AdminPassword!), ct);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        using var created = await client.PostAsJsonAsync("/api/nodes/join-tokens", new { nodeId = "session-node" }, ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
    }

    [Fact]
    public async Task ExpiredSession_CannotAuthorizeAPortalMutation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new AuthWebFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var scheme = Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme;
        var options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>().Get(scheme);
        var ticket = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(TestAdmin.User,
            new Microsoft.AspNetCore.Authentication.AuthenticationProperties
            {
                IssuedUtc = DateTimeOffset.UtcNow.AddDays(-8), ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            }, scheme);
        client.DefaultRequestHeaders.Add("Cookie", $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}");
        using var response = await client.PostAsJsonAsync("/api/nodes/join-tokens", new { nodeId = "expired-session", password = factory.AdminPassword }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LoginWithCorrectPassword_HonoursReturnUrl()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new AuthWebFactory { AdminPassword = "correct-password" };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        string token = await GetAntiforgeryTokenAsync(client, ct);
        using var loginResponse = await client.PostAsync("/login/submit", LoginForm(token, "correct-password", "/keys"), ct);

        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.Equal("/keys", loginResponse.Headers.Location?.ToString());
    }

    [Fact]
    public async Task LoginWithWrongPassword_RedirectsBackWithError_AndDoesNotAuthenticate()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new AuthWebFactory { AdminPassword = "correct-password" };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        string token = await GetAntiforgeryTokenAsync(client, ct);
        using var loginResponse = await client.PostAsync("/login/submit", LoginForm(token, "wrong-password"), ct);

        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.Contains("error=1", loginResponse.Headers.Location!.ToString(), StringComparison.Ordinal);

        using var api = await client.GetAsync("/api/models", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
    }

    [Fact]
    public async Task LoginRateLimiter_BlocksTheSixthAttemptWithinAMinute()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new AuthWebFactory { AdminPassword = "correct-password" };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        string token = await GetAntiforgeryTokenAsync(client, ct);

        HttpStatusCode? sixthStatus = null;
        for (int attempt = 1; attempt <= 6; attempt++)
        {
            using var response = await client.PostAsync("/login/submit", LoginForm(token, "wrong-password"), ct);
            if (attempt == 6)
            {
                sixthStatus = response.StatusCode;
            }
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, sixthStatus);
    }

    [Fact]
    public async Task LogoutClearsTheCookie()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new AuthWebFactory { AdminPassword = "correct-password" };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        string token = await GetAntiforgeryTokenAsync(client, ct);
        await client.PostAsync("/login/submit", LoginForm(token, "correct-password"), ct);

        using var apiBeforeLogout = await client.GetAsync("/api/models", ct);
        Assert.Equal(HttpStatusCode.OK, apiBeforeLogout.StatusCode);

        await client.PostAsync("/logout", content: null, ct);

        using var apiAfterLogout = await client.GetAsync("/api/models", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, apiAfterLogout.StatusCode);
    }

    [Fact]
    public async Task ApiWithoutCookie_Returns401Json_NotARedirect()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new AuthWebFactory { AdminPassword = "correct-password" };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/api/models", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/api/backup/status")]
    [InlineData("/api/backup/download/some-token")]
    [InlineData("/api/backup/files/mirbadllm-backup-dgx-20261001-030000.json")]
    [InlineData("/backup")]
    public async Task BackupEndpointsAndPage_NeedTheAdminLogin(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new AuthWebFactory { AdminPassword = "correct-password" };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync(path, ct);

        if (path.StartsWith("/api", StringComparison.Ordinal))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/login", response.Headers.Location?.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ProtectedPageWithoutCookie_RedirectsToLogin()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new AuthWebFactory { AdminPassword = "correct-password" };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/", ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login", response.Headers.Location?.ToString() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthzStaysOpenWithoutACookie()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new AuthWebFactory { AdminPassword = "correct-password" };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/healthz", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AuthDisabled_NoAdminPassword_AllowsAnonymousAccessEverywhere()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new AuthWebFactory { AdminPassword = null };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var page = await client.GetAsync("/", ct);
        using var api = await client.GetAsync("/api/models", ct);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);

        string html = await page.Content.ReadAsStringAsync(ct);
        Assert.Contains("Admin login disabled", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthDisabled_LoginStillRejectsBecauseThereIsNoPasswordToMatch()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new AuthWebFactory { AdminPassword = null };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        string token = await GetAntiforgeryTokenAsync(client, ct);
        using var loginResponse = await client.PostAsync("/login/submit", LoginForm(token, "anything"), ct);

        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.Contains("error=1", loginResponse.Headers.Location!.ToString(), StringComparison.Ordinal);
    }
}
