using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Web.Mcp;

/// <summary>Bearer authentication used exclusively by the Streamable HTTP MCP endpoint.</summary>
public sealed class McpApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyStore keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string AuthenticationScheme = "MirbadMcpBearer";
    public const string Policy = "MirbadMcp";
    public const string ScopeClaim = "mirbad_mcp_scope";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string authorization = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        string presented = authorization[prefix.Length..].Trim();
        if (presented.Length == 0 || presented.Contains(' '))
        {
            return AuthenticateResult.Fail("Invalid bearer credential.");
        }

        ApiKeyRecord? record = await keys.FindByPresentedKeyAsync(presented, Context.RequestAborted).ConfigureAwait(false);
        if (record is null || !(record.Grants(ApiKeyScope.McpRead) || record.Grants(ApiKeyScope.McpOperator)))
        {
            return AuthenticateResult.Fail("Invalid bearer credential.");
        }

        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, record.Id),
            new(ClaimTypes.Name, record.Name),
            new(ScopeClaim, (record.Grants(ApiKeyScope.McpOperator) ? ApiKeyScope.McpOperator : ApiKeyScope.McpRead).ToString()),
        ];
        var identity = new ClaimsIdentity(claims, AuthenticationScheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthenticationScheme));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"mirbad-mcp\"";
        Response.ContentType = "application/json";
        await Response.WriteAsync("{\"error\":\"unauthorized\"}", Context.RequestAborted).ConfigureAwait(false);
    }
}
