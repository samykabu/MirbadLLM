using System.Security.Claims;

namespace MirbadLLM.UnitTests;

internal static class TestAdmin
{
    internal static ClaimsPrincipal User => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim(ClaimTypes.Sid, "test-admin-session")], "Cookies"));
}
