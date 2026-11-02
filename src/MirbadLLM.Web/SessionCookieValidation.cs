using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace MirbadLLM.Web;

/// <summary>
/// Signs out an admin cookie whose ticket has no session id (<see cref="ClaimTypes.Sid"/>, MN-132). The login adds a random
/// <c>sid</c> since the W7 review (MN-126, D81); a cookie issued before that deploy has none, and with the 7-day sliding
/// expiry it would be renewed without one for as long as it is used. Such a session has no session hash: its audit actor
/// would be the shared <c>admin:{hash of "admin"}</c> and, on a Blazor circuit (no request, so no remote IP), its password
/// re-check budget the shared <c>unknown</c> bucket (D98), where one admin's typos lock out every other such session. So the
/// principal is rejected and the cookie removed: the browser is sent to the login once and gets a ticket with a <c>sid</c>.
/// <para>Wraps the cookie scheme's <see cref="CookieAuthenticationEvents.OnValidatePrincipal"/> (whatever the composition set
/// runs first). Registered by <see cref="AdminReauthenticationServiceCollectionExtensions.AddMirbadLLMAdminReauth"/> as a
/// post-configuration of the default cookie scheme, so <c>Program.cs</c> keeps its cookie options as they are.</para>
/// </summary>
public sealed class SessionCookieValidation : IPostConfigureOptions<CookieAuthenticationOptions>
{
    /// <inheritdoc/>
    public void PostConfigure(string? name, CookieAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(name, CookieAuthenticationDefaults.AuthenticationScheme, StringComparison.Ordinal))
        {
            return;
        }

        options.Events ??= new CookieAuthenticationEvents();
        var inner = options.Events.OnValidatePrincipal;
        options.Events.OnValidatePrincipal = async context =>
        {
            if (inner is not null)
            {
                await inner(context).ConfigureAwait(false);
            }

            if (context.Principal is { } principal && !HasSessionId(principal))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(context.Scheme.Name).ConfigureAwait(false);
            }
        };
    }

    /// <summary>True when <paramref name="principal"/> is signed in with a non-empty <c>sid</c> claim (or is not signed in).</summary>
    public static bool HasSessionId(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.Identity?.IsAuthenticated != true || principal.FindFirst(ClaimTypes.Sid)?.Value is { Length: > 0 };
    }
}
