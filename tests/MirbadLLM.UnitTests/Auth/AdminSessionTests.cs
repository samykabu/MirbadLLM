using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Web;

namespace MirbadLLM.UnitTests.Auth;

public sealed class AdminSessionTests
{
    [Fact]
    public async Task AnonymousCircuitDoesNotInheritThePrerenderRequestsAuthenticatedUser()
    {
        var http = new DefaultHttpContext { User = TestAdmin.User };
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        using var services = new ServiceCollection()
            .AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = http }).BuildServiceProvider();
        var anonymous = new ClaimsPrincipal();
        var caller = await AdminCallers.ResolveAsync(services, Task.FromResult(new AuthenticationState(anonymous)));
        Assert.Same(anonymous, caller.User);
        Assert.Equal("127.0.0.1", caller.ClientKey);
        var authorizer = new AdminReauthenticator(Options.Create(new AuthOptions { AdminPassword = "configured" }));
        Assert.Equal(AdminReauthOutcome.LoginRequired, authorizer.Verify(caller.User));
    }

    [Fact]
    public void RequestPayloadCannotSupplyThePrincipalUsedForAuthorization()
    {
        var request = System.Text.Json.JsonSerializer.Deserialize<HostServiceWhitelistUpdate>(
            """{"Confirm":"service","User":{"IsAuthenticated":true}}""");
        Assert.NotNull(request);
        Assert.Null(request.User);
        var trusted = request with { User = TestAdmin.User };
        Assert.DoesNotContain("test-admin-session", System.Text.Json.JsonSerializer.Serialize(trusted), StringComparison.Ordinal);
    }
}
