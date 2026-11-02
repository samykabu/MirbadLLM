using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Web;

/// <summary>The app's single fallback-authorization requirement: "the admin cookie must be presented,
/// unless admin login is disabled (no <c>MirbadLLM:AdminPassword</c> configured)". Used as the
/// <c>AddAuthorizationBuilder().SetFallbackPolicy(...)</c> policy in <c>Program.cs</c>, so it protects every
/// endpoint that doesn't declare its own <c>[Authorize]</c>/<c>AllowAnonymous</c>.</summary>
public sealed class AdminLoginRequirement : IAuthorizationRequirement;

/// <summary>
/// Satisfies <see cref="AdminLoginRequirement"/> when the user is authenticated, or unconditionally when
/// <see cref="AuthOptions.AdminPassword"/> is empty (admin login disabled). Deciding this from
/// <see cref="IOptions{AuthOptions}"/> at evaluation time — rather than once from raw configuration at
/// startup — means it always reflects the final, fully-assembled configuration (including e.g. a test
/// host's configuration overrides), and would pick up a changed password without a restart if it were ever
/// bound via <see cref="IOptionsMonitor{AuthOptions}"/> instead.
/// </summary>
public sealed class AdminLoginRequirementHandler(IOptions<AuthOptions> authOptions) : IAuthorizationHandler
{
    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        bool adminLoginEnabled = !string.IsNullOrEmpty(authOptions.Value.AdminPassword);
        if (!adminLoginEnabled || context.User.Identity?.IsAuthenticated == true)
        {
            foreach (var requirement in context.PendingRequirements.OfType<AdminLoginRequirement>().ToList())
            {
                context.Succeed(requirement);
            }
        }

        return Task.CompletedTask;
    }
}
