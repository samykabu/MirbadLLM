using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Web.Mcp;
using ModelContextProtocol.AspNetCore;
using MirbadLLM.Orchestration.Audit;
using MirbadLLM.Orchestration.Fencing;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Pki;
using MirbadLLM.Orchestration.Recovery;
using MirbadLLM.Pki;
using MirbadLLM.Router;
using MirbadLLM.Web;
using MirbadLLM.Web.Api;
using MirbadLLM.Web.Components;
using MirbadLLM.Web.Enrollment;

const string LoginRateLimiterPolicy = "login";

var builder = WebApplication.CreateBuilder(args);
// Pre-rename deployments still set SparkServe:* / SparkServe__* keys: read them as MirbadLLM's (new keys win).
var legacyKeys = LegacyConfiguration.Apply(builder.Configuration);

builder.Services.AddOptions<MirbadLLMOptions>()
    .Bind(builder.Configuration.GetSection(MirbadLLMOptions.SectionName));

builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(MirbadLLMOptions.SectionName));

var dataPath = builder.Configuration.GetSection(MirbadLLMOptions.SectionName).Get<MirbadLLMOptions>()?.DataPath
    ?? new MirbadLLMOptions().DataPath;

// Recovery mode first (design §9.4, D76; MN-130): finish or discard a staged fleet recovery and decide, once, how this
// master boots, before the master lock, the registry, the PKI or the bootstrap logic touch the data path. In recovery mode
// the restricted host is composed: no seeding, CA generation, bootstrap node, auto-start, scheduler or agent connections.
MasterBoot boot;
using (var bootLogs = LoggerFactory.Create(logging => logging.AddSimpleConsole(o => o.SingleLine = true)))
{
    var startupLogger = bootLogs.CreateLogger("MirbadLLM.Web.Startup");
    LegacyConfiguration.Warn(startupLogger, legacyKeys);

    boot = MasterBoot.Prepare(builder.Configuration, dataPath, startupLogger);
}

builder.Services.AddSingleton(boot);

// The master composition (MN-040): no Docker, hostd, local probes or model folders here; models, telemetry, downloads,
// folders, host services and the backup mount are reached through the node agents over mTLS.
// Enrollment (design §3.2): the enroll listener on MirbadLLM:Enrollment:Port (8443) and the bootstrap bundle for the
// bootstrap node (MirbadLLM:Nodes:BootstrapNodeId, default dgx).
builder.Services.AddMirbadLLMEnrollment(builder.Configuration);
// Development only (MN-042): join tokens for the two-agent dev setup's second node; refused in any other environment.
builder.Services.AddMirbadLLMDevJoinTokens(builder.Configuration);
// The one MasterCertificates (agent client certificate + enroll-listener certificate), with the listener's names, persisted
// under {DataPath}/pki; registered before AddMirbadLLMOrchestration, which only adds one when absent (W3 review MN-094).
// In recovery mode they stay in memory (nothing is issued to disk before the backup's CA is installed, D76).
builder.Services.AddSingleton(sp => MasterCertificates.LoadOrIssue(
    sp.GetRequiredService<CertificateAuthority>(),
    boot.IsRecovery ? null : MasterPkiOptions.DirectoryOf(sp.GetRequiredService<IOptions<MirbadLLMOptions>>().Value.DataPath),
    sp.GetRequiredService<IOptions<EnrollmentOptions>>().Value.EffectiveListenerAddresses,
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddMirbadLLMOrchestration(builder.Configuration);
builder.Services.AddMirbadLLMHostServices(builder.Configuration);
// The per-node host-services managers (Services page, MN-120), one per master (W7 review MN-126).
builder.Services.AddSingleton<INodeHostServices, NodeHostServices>();
builder.Services.AddSingleton<INodeHostServiceManagers>(sp => sp.GetRequiredService<INodeHostServices>()); // backups carry each node's units
builder.Services.AddMirbadLLMBackup(builder.Configuration);
builder.Services.AddMirbadLLMAdminReauth();
builder.Services.AddMirbadLLMRouter();
builder.Services.AddMirbadLLMAgentRouting();
// P2 APIs wired by the W7 review (MN-126) for the Wave 8 UI: node admin (MN-102), takeover (MN-115) and the audit log
// (MN-116, after the orchestration services: it decorates the operation-reference store). None changes a single-DGX
// deployment that does not call them: the takeover only runs when MirbadLLM:Fencing:Takeover is set, as in P1.
builder.Services.AddMirbadLLMNodeApi(builder.Configuration);
builder.Services.AddTakeoverService();
builder.Services.AddMirbadLLMAudit();
// Move and unplace (MN-109): resumes the move records of ownership.json at boot and stops stale source instances; idle on a
// single-DGX master (no move is ever started there, and the stale-instance sweep needs two non-revoked nodes, W8 review).
builder.Services.AddMirbadLLMMoves(builder.Configuration);
// Node-scoped maintenance (D83, decided by MN-130 as D101): MirbadLLM:Router:Maintenance:NodeScoped true/false as set;
// unset = automatic, node-scoped windows only while two or more nodes are registered (the single DGX keeps P1's global
// windows, and the router, on in that mode, answers a global window exactly as with the switch off).
builder.Services.AddOptions<RouterMaintenanceOptions>()
    .PostConfigure<IServiceProvider>((options, sp) => options.NodeScoped = sp.GetRequiredService<NodeScopedMaintenancePolicy>().RouterSwitch);
if (boot.IsRecovery)
{
    // The restricted host (D76): no enroll listener, and none of the packages' background services.
    MasterHosting.RemoveBackgroundServices(builder.Services);
}
else
{
    MasterHosting.AddEnrollListener(builder);
}

// Persist the cookie-signing keys next to the registry so logins survive controller restarts and redeploys.
builder.Services.AddDataProtection()
    .SetApplicationName(LegacyProtectionPurposes.ApplicationName)
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "dataprotection-keys")));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddScheme<AuthenticationSchemeOptions, McpApiKeyAuthenticationHandler>(McpApiKeyAuthenticationHandler.AuthenticationScheme, _ => { })
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.ReturnUrlParameter = "returnUrl";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        // Plain HTTP on the LAN, so "secure" tracks the scheme of the request that set the cookie rather
        // than always requiring HTTPS.
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Events.OnRedirectToLogin = context =>
        {
            // /api/* wants a 401 it can branch on, not a redirect to an HTML login page.
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                return context.Response.WriteAsync("""{"error":"unauthorized"}""");
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });

builder.Services.AddCascadingAuthenticationState();

// The fallback policy protects every endpoint without its own [Authorize]/AllowAnonymous (all Blazor pages
// via AuthorizeRouteView, /api/*, /keys' backing API, etc.). Whether it actually requires anything is
// decided per-request from IOptions<AuthOptions> (via AdminLoginRequirementHandler below), not once at
// startup from raw configuration — MirbadLLM:AdminPassword can only be known once the final configuration
// (including e.g. a test host's overrides) is assembled, which happens at IOptions<T> resolution time, not
// while builder.Configuration is still being built up here.
builder.Services.AddSingleton<IAuthorizationHandler, AdminLoginRequirementHandler>();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(McpApiKeyAuthenticationHandler.Policy, policy =>
    {
        policy.AddAuthenticationSchemes(McpApiKeyAuthenticationHandler.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
    })
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().AddRequirements(new AdminLoginRequirement()).Build());

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Writes a body immediately so UseStatusCodePagesWithReExecute (which only re-executes an *empty*
    // error response) leaves this 429 alone instead of replacing it with a re-executed (and, for an
    // unauthenticated caller, redirected-to-login) /not-found response.
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.ContentType = "text/plain";
        await context.HttpContext.Response.WriteAsync("Too many attempts. Try again in a minute.", ct);
    };
    // 5 attempts/minute per client IP, shared with the Services page's admin re-authentication (AdminPasswordGuard).
    options.AddAdminPasswordPolicy(LoginRateLimiterPolicy);
    options.AddFixedWindowLimiter("mcp", limiter =>
    {
        limiter.PermitLimit = 120;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});

// Enums as their names in the admin API, non-finite doubles as null (ApiJson).
builder.Services.ConfigureApiJson();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    // A short keep-alive lets the browser notice a dead circuit within a few seconds (the client's server
    // timeout is set to match in App.razor) instead of the default ~30 s of a silently frozen page.
    .AddHubOptions(options => options.KeepAliveInterval = TimeSpan.FromSeconds(1));

// One shared 5 s refresh per circuit for the status bar and the live pages (see LiveDataService).
builder.Services.AddScoped<LiveDataService>();
builder.Services.AddSingleton<ModelStartService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMcpServer(options => options.ServerInstructions = MirbadMcpTools.PlanningGuide)
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<MirbadMcpTools>();
// The release notes this build carries (docs/multinode/agent-updates.md U1): What's new, Releases, agent updates.
builder.Services.AddSingleton(_ => ReleaseNotes.Load(Path.Combine(AppContext.BaseDirectory, ReleaseNotes.FileName)));

var app = builder.Build();

// Fencing first (W1 review MN-090): take master.lock and bump the epoch before anything touches DataPath (models.json
// migration, PKI, the bootstrap node and bundle, backups). A second master on the same volume fails here (non-zero exit).
// Recovery mode keeps the lock too (D76): the install replaces master.json while this process holds it.
_ = app.Services.GetRequiredService<IMasterIdentity>();
if (!boot.IsRecovery)
{
    // A CA rotation interrupted by a restart issues under its new CA and reaches old-CA nodes with the previous client
    // certificate before any agent connection is made (MN-116).
    await app.Services.GetRequiredService<CaRotationService>().InitializeAsync();
    await MasterHosting.EnsureBootstrapNodeAsync(app.Services);
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
// Only POST /enroll on the enroll listener's connections; everything else there is a 404 (design §3.2).
app.UseEnrollListenerGate();
if (boot.IsRecovery)
{
    // Only the login, the Restore tab, /healthz and /v1 (503 "restoring") are served (design §9.4).
    MasterHosting.UseRecoveryHostGate(app);
}

// Served over plain HTTP on the LAN (TLS, when wanted, terminates at a reverse proxy), so no HSTS or HTTPS redirect.
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Explicit (rather than relying on the minimal-hosting-model's automatic insertion) because
// UseRateLimiter needs the endpoint already matched to see a per-endpoint RequireRateLimiting policy
// (e.g. the login rate limiter below) — without this, it would silently never apply one.
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Static assets, the Blazor render pipeline (incl. its SignalR hub) and /healthz authenticate themselves
// (or don't need to) rather than going through the admin cookie — see AuthorizeRouteView in Routes.razor for
// the actual per-page gate, and MapMirbadLLMRouter/MapMirbadLLMMetrics for the API-key gates on /v1 and /metrics.
app.MapStaticAssets().AllowAnonymous();
// Each routable component gets its own endpoint (that's what lets a page-level [AllowAnonymous]/[Authorize]
// attribute, e.g. Login.razor's, participate in the normal ASP.NET Core authorization middleware/fallback
// policy below) — so this is deliberately NOT .AllowAnonymous() here.
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
// The pre-P2 mutating /api routes get their audit line from one filter (MN-116); it acts only on the routes it lists, so
// the P2 routes mapped into the same group (fit, per-node jobs) write their own lines as before.
var audited = app.MapGroup("").AddEndpointFilter(new LegacyApiAuditFilter());
audited.MapAdminApi();
audited.MapJobApi();
audited.MapApiKeyEndpoints();
audited.MapHostServiceApi();
audited.MapBackupApi();
app.MapNodeApi();
// Anonymous, read-only Linux agent bundles for the join and rekey commands (outside /api and /v1; not in recovery mode).
app.MapAgentBundles();
app.MapFencingApi();
app.MapAuditApi();
app.MapReleaseNotes();
app.MapMirbadLLMRouter();
app.MapMirbadLLMMetrics();
app.MapMcp("/mcp")
    .RequireAuthorization(McpApiKeyAuthenticationHandler.Policy)
    .RequireRateLimiting("mcp")
    .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(256 * 1024));
app.MapEnrollEndpoint();

// A distinct path from the "/login" GET page (mapped by MapRazorComponents, above): Blazor's own enhanced-
// navigation form handling also listens for POSTs to a page's own route, which would otherwise collide
// with this minimal API endpoint and make routing ambiguous.
app.MapPost("/login/submit", async (HttpContext http, IAntiforgery antiforgery, IOptions<AuthOptions> authOptions) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(http);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.Redirect("/login?error=1");
    }

    var form = await http.Request.ReadFormAsync();
    string password = form["password"].ToString();
    string? returnUrl = form["returnUrl"].ToString();

    string? adminPassword = authOptions.Value.AdminPassword;
    if (string.IsNullOrEmpty(adminPassword) || !ConstantTimeCompare.Equals(password, adminPassword))
    {
        string failureRedirect = "/login?error=1" + (IsLocalUrl(returnUrl) ? $"&returnUrl={Uri.EscapeDataString(returnUrl!)}" : "");
        return Results.Redirect(failureRedirect);
    }

    // A random session id kept in the ticket, so the audit actor (AuditActorResolver: admin:{hash of sid}) stays the same
    // across sliding renewals of the cookie and never exposes the cookie itself (W7 review MN-126).
    var identity = new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Sid, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)))],
        CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

    return Results.Redirect(IsLocalUrl(returnUrl) ? returnUrl! : "/");
})
.AllowAnonymous()
.RequireRateLimiting(LoginRateLimiterPolicy);

app.MapPost("/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).AllowAnonymous();

app.Run();

// Rejects anything but a same-app relative path, so the post-login redirect can't be turned into an
// open redirect via a crafted returnUrl.
static bool IsLocalUrl(string? url) =>
    !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal);

// Allows WebApplicationFactory<Program> in tests.
public partial class Program;
