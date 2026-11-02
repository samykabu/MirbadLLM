using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MirbadLLM.UnitTests.Auth;

#pragma warning disable CA1707 // MethodUnderTest_Scenario_ExpectedBehavior test naming style, used throughout this project.

/// <summary>
/// MN-132 (D81, D98): an admin cookie issued before the login added a session id (<c>sid</c>) is signed out by the real
/// composition, so the browser logs in once and its re-checks get their own budget instead of the shared <c>unknown</c>
/// one; a cookie with a <c>sid</c> keeps working. The tickets are protected with the app's own cookie format, exactly as
/// the login writes them.
/// </summary>
public sealed class SessionCookieTests
{
    [Fact]
    public async Task ACookieWithoutASessionId_IsSignedOut_AndOneWithASessionIdStillWorks()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new AuthWebFactory { AdminPassword = "correct-password" };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        using var legacy = await GetAsync(client, Cookie(factory, [new Claim(ClaimTypes.Name, "admin")]), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, legacy.StatusCode);
        Assert.Contains(legacy.Headers.TryGetValues("Set-Cookie", out var cleared) ? cleared : [],
            c => c.StartsWith(CookieName + "=;", StringComparison.Ordinal)); // the browser drops it and logs in again

        using var current = await GetAsync(client, Cookie(factory, [new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Sid, "0123456789abcdef0123456789abcdef")]), ct);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    private const string CookieName = ".AspNetCore.Cookies";

    private static string Cookie(AuthWebFactory factory, Claim[] claims)
    {
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7) },
            CookieAuthenticationDefaults.AuthenticationScheme);
        return options.TicketDataFormat.Protect(ticket);
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string cookie, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/models");
        request.Headers.Add("Cookie", CookieName + "=" + cookie);
        return await client.SendAsync(request, ct);
    }
}
