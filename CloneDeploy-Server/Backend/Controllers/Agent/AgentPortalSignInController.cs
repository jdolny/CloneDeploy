using System.Security.Claims;
using System.Text.Encodings.Web;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Controllers.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Controllers.Agent;

[ApiController]
public sealed class AgentPortalSignInController(AgentPortalSessions sessions, ICloneDeployDbFactory factory, IConfiguration? configuration = null) : ControllerBase
{
    private const int MaxRequestBytes = 8192;

    private Guid Device => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpPost("api/agent/v1/portal-auth/start"), Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
    public ActionResult<AgentPortalSignInResponse> Start(AgentPortalSignInRequest input)
    {
        if (!Request.IsHttps) return BadRequest();
        Response.Headers.CacheControl = "no-store";
        try { return sessions.Start(Device, input) with { BrowserPort = CloneDeploy.SiteServices.ListenerPorts.Read(configuration ?? new ConfigurationBuilder().Build()).PortalPort }; }
        catch (ArgumentException) { return BadRequest(); }
        catch (InvalidOperationException) { return StatusCode(StatusCodes.Status429TooManyRequests); }
    }
    [HttpPost("api/agent/v1/portal-auth/exchange"), Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
    public async Task<ActionResult<AgentPortalUserSession>> Exchange(AgentPortalCodeRequest input, CancellationToken ct)
    {
        if (!Request.IsHttps) return BadRequest(); Response.Headers.CacheControl = "no-store";
        var result = await sessions.ExchangeAsync(Device, input, factory, ct);
        return result is null ? Unauthorized() : result;
    }
    [HttpPost("api/agent/v1/portal-auth/session"), Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
    public async Task<ActionResult<AgentPortalUserSession>> Session(AgentPortalSessionRequest input, CancellationToken ct)
    {
        if (!Request.IsHttps) return BadRequest(); Response.Headers.CacheControl = "no-store";
        var result = await sessions.ValidateAsync(Device, input, factory, ct);
        return result is null ? Unauthorized() : result;
    }
    [HttpPost("api/agent/v1/portal-auth/sign-out"), Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
    public async Task<IActionResult> SignOut(AgentPortalSessionRequest input, CancellationToken ct)
    {
        if (!Request.IsHttps) return BadRequest(); await sessions.RevokeAsync(Device, input, factory, ct); return NoContent();
    }
    [HttpPost("api/agent/v1/portal-auth/policies"), Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
    public async Task<IActionResult> Policies(AgentPortalSessionRequest input, [FromServices] AgentPolicyExecutionService policies, CancellationToken ct)
    {
        if (!Request.IsHttps) return BadRequest();
        Response.Headers.CacheControl = "no-store";
        var session = await sessions.ValidateAsync(Device, input, factory, ct);
        if (session?.UserSid is not { Length: > 0 } sid) return Unauthorized();
        var result = await policies.GetForUserAsync(int.Parse(User.FindFirstValue(AgentCertificateAuthentication.DeviceIdClaim)!), sid, false, ct);
        return result.Success ? new JsonResult(result.Data, new System.Text.Json.JsonSerializerOptions()) : this.ServiceError(result);
    }
    [HttpPost("api/agent/v1/portal-auth/policy-start"), Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
    public async Task<IActionResult> PolicyStart(AgentPortalPolicyStart input, [FromServices] AgentPolicyExecutionService policies, CancellationToken ct)
    {
        if (!Request.IsHttps) return BadRequest();
        var session = await sessions.ValidateAsync(Device, input.Session, factory, ct);
        if (session?.UserSid is not { Length: > 0 } sid) return Unauthorized();
        return this.ToHttp(await policies.StartAsync(int.Parse(User.FindFirstValue(AgentCertificateAuthentication.DeviceIdClaim)!),
            input.Start with { UserSid = sid, UserName = session.User.Name, SelfService = false }, ct));
    }
    [HttpPost("api/agent/v1/portal-auth/self-service-policies"), Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
    public async Task<IActionResult> SelfServicePolicies(AgentPortalSessionRequest input, [FromServices] AgentPolicyExecutionService policies, CancellationToken ct)
    {
        if (!Request.IsHttps) return BadRequest();
        Response.Headers.CacheControl = "no-store";
        var session = await sessions.ValidateAsync(Device, input, factory, ct);
        if (session?.UserSid is not { Length: > 0 } sid) return Unauthorized();
        var result = await policies.GetForUserAsync(int.Parse(User.FindFirstValue(AgentCertificateAuthentication.DeviceIdClaim)!), sid, true, ct);
        return result.Success ? new JsonResult(result.Data, new System.Text.Json.JsonSerializerOptions()) : this.ServiceError(result);
    }
    [HttpPost("api/agent/v1/portal-auth/self-service-start"), Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
    public async Task<IActionResult> SelfServiceStart(AgentPortalPolicyStart input, [FromServices] AgentPolicyExecutionService policies, CancellationToken ct)
    {
        if (!Request.IsHttps) return BadRequest();
        var session = await sessions.ValidateAsync(Device, input.Session, factory, ct);
        if (session?.UserSid is not { Length: > 0 } sid) return Unauthorized();
        return this.ToHttp(await policies.StartAsync(int.Parse(User.FindFirstValue(AgentCertificateAuthentication.DeviceIdClaim)!),
            input.Start with { UserSid = sid, UserName = session.User.Name, SelfService = true }, ct));
    }
    [HttpPost("api/agent/v1/portal-auth/help"), Authorize(Policy = AgentCertificateAuthentication.PolicyName), RequestSizeLimit(HelpAttachmentLimits.PayloadBytes)]
    public async Task<ActionResult<HelpResponse>> Help(AgentPortalHelpRequest input, [FromServices] HelpDeskService help, CancellationToken ct)
    {
        if (!Request.IsHttps || input.Request is null ||
            !int.TryParse(User.FindFirstValue(AgentCertificateAuthentication.DeviceIdClaim), out var deviceId)) return BadRequest();
        Response.Headers.CacheControl = "no-store";
        var session = await sessions.ValidateAsync(Device, new(input.LocalUser, input.Token), factory, ct);
        if (session is null) return Unauthorized();
        // Resolve the SID on the server from the validated immutable directory identity.
        // The portal cannot choose a SID or another requester, and gets the same ownership
        // keys as that AD user on Windows, including existing cross-device tickets.
        await using var db = await factory.CreateContextAsync();
        var sid = await db.DirectoryObjects.AsNoTracking().Where(x => x.DirectorySourceId == session.User.DirectorySourceId &&
            x.ObjectGuid == session.User.ObjectGuid).Select(x => x.ObjectSid).SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(sid)) return BadRequest(new HelpResponse("Your directory account does not have a synced SID. Contact your administrator."));
        try { return await help.AgentAsync(deviceId, sid, input.Request, ct); }
        catch (UnauthorizedAccessException) { return StatusCode(StatusCodes.Status403Forbidden, new HelpResponse("This help request is not available to the signed-in user.")); }
        catch (InvalidOperationException ex) { return BadRequest(new HelpResponse(ex.Message)); }
    }
    [HttpGet("agent/portal-sign-in/{id:guid}"), AllowAnonymous, EnableRateLimiting("PortalBrowser")]
    public Task<IActionResult> Page(Guid id, [FromServices] IAntiforgery csrf, CancellationToken ct) => FormAsync(id, csrf, Request.Query.ContainsKey("error") ? "Unable to sign in with Microsoft. Your account must be enabled and synced to the directory. Contact your administrator if this continues." : null, ct);

    [HttpPost("agent/portal-sign-in/{id:guid}"), AllowAnonymous, EnableRateLimiting("PortalPassword"), RequestSizeLimit(MaxRequestBytes)]
    public async Task<IActionResult> Login(Guid id, [FromServices] IAntiforgery csrf, [FromServices] IAdPasswordValidator validator, CancellationToken ct)
    {
        if (!Request.IsHttps) return BadRequest(); Headers();
        try { await csrf.ValidateRequestAsync(HttpContext); }
        catch (AntiforgeryValidationException) { return BadRequest("The sign-in form expired. Start sign-in again from the portal."); }
        var form = await Request.ReadFormAsync(ct);
        string? redirect = null;
        try { redirect = await sessions.CompleteAsync(id, form["username"].ToString(), form["password"].ToString(), factory, validator, ct); }
        catch (Exception ex) when (ex is System.DirectoryServices.Protocols.LdapException or System.DirectoryServices.Protocols.DirectoryOperationException or InvalidOperationException)
        { /* Do not expose directory endpoints, account existence or credentials. */ }
        return redirect is null ? await FormAsync(id, csrf, "Unable to sign in. Check your work account details, or try again later.", ct) : Redirect(redirect);
    }
    [HttpPost("agent/portal-sign-in/{id:guid}/entra"), AllowAnonymous, EnableRateLimiting("PortalBrowser"), RequestSizeLimit(MaxRequestBytes)]
    public async Task<IActionResult> Entra(Guid id, [FromServices] IAntiforgery csrf, CancellationToken ct)
    {
        if (!Request.IsHttps) return BadRequest();
        Headers();
        try { await csrf.ValidateRequestAsync(HttpContext); }
        catch (AntiforgeryValidationException) { return BadRequest("The sign-in form expired. Start sign-in again from the portal."); }
        if (sessions.PendingDevice(id) is null) return BadRequest("Start sign-in again from the portal.");
        await using var db = await factory.CreateContextAsync();
        if (!await HasPortalMicrosoftSignInAsync(db, ct))
            return await FormAsync(id, csrf, "Microsoft sign-in is unavailable.", ct);
        var properties = new AuthenticationProperties();
        properties.Items["portal.request"] = id.ToString("D");
        return Challenge(properties, "EntraPortal");
    }
    private static Task<bool> HasPortalMicrosoftSignInAsync(CloneDeployDbContext db, CancellationToken ct) =>
        db.Set<EntraConnection>().AnyAsync(connection => connection.SsoEnabled && connection.CertificateEncrypted != null &&
            db.DirectorySources.Any(source => source.Provider == CloneDeploy_Server.Backend.Enum.DirectoryProvider.EntraId &&
                source.IsEnabled && (!source.ApplicationAccessOnly || db.DirectorySources.Any(ldap =>
                    ldap.Provider == CloneDeploy_Server.Backend.Enum.DirectoryProvider.ActiveDirectory &&
                    ldap.IsEnabled && !ldap.ApplicationAccessOnly && ldap.SyncState != null)) && source.SyncState != null &&
                source.SyncState.RootObjectGuid == connection.TenantId), ct);

    private void Headers()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Frame-Options"] = "DENY";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; form-action 'self' https://login.microsoftonline.com http://127.0.0.1:*; frame-ancestors 'none'; base-uri 'none'";
    }
    private async Task<IActionResult> FormAsync(Guid id, IAntiforgery csrf, string? error, CancellationToken ct)
    {
        if (!Request.IsHttps) return BadRequest(); Headers();
        var device = sessions.PendingDevice(id);
        if (device is null) return BadRequest("This request expired or was already used. Start sign-in again from the portal.");
        await using var db = await factory.CreateContextAsync();
        var name = await db.Devices.Where(x => x.Guid == device).Select(x => x.Name).SingleOrDefaultAsync(ct);
        if (name is null) return BadRequest("Device unavailable.");
        var branding = await BrandingSettings.ReadAsync(db, ct);
        var token = csrf.GetAndStoreTokens(HttpContext);
        static string E(string? text) => HtmlEncoder.Default.Encode(text ?? "");
        var background = AgentBranding.ValidColor(branding.BackgroundColor) ? branding.BackgroundColor : "#2459D6";
        var accent = AgentBranding.ValidColor(branding.AccentColor) ? branding.AccentColor : "#2459D6";
        var logo = branding.LogoPng is { Length: > 0 } png
            ? $"<img class=\"logo\" src=\"data:image/png;base64,{Convert.ToBase64String(png)}\" alt=\"{E(branding.OrganizationName)}\">" : "";
        var entra = await HasPortalMicrosoftSignInAsync(db, ct);
        var sso = entra ? $"<form method=\"post\" action=\"/agent/portal-sign-in/{id:D}/entra\"><input type=\"hidden\" name=\"{E(token.FormFieldName)}\" value=\"{E(token.RequestToken)}\"><button class=\"sso\" type=\"submit\">Sign in with Microsoft</button></form>" : "";
        return Content($$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Sign in — {{E(branding.PortalTitleText)}}</title><style>
            :root{color-scheme:light dark;font-family:system-ui,sans-serif}body{margin:0;min-height:100vh;display:grid;place-items:center;background:light-dark(#f3f5f9,#17191f);color:light-dark(#172033,#f3f5f9)}
            main{max-width:390px;margin:24px;padding:0;border-radius:16px;overflow:hidden;width:calc(100% - 48px);background:light-dark(white,#242730);box-shadow:0 10px 40px #0002}h1{font-size:24px}p{line-height:1.5}label{display:block;margin-top:20px}input{box-sizing:border-box;width:100%;padding:12px;margin-top:7px;border:1px solid #888;border-radius:7px;font:inherit}button{width:100%;margin-top:24px;border:0;border-radius:7px;padding:13px;background:{{accent}};color:{{branding.AccentForegroundColor}};cursor:pointer;font:inherit;font-weight:600}.error{color:light-dark(#b42318,#ffb4ab)}small{opacity:.8}.brand{padding:28px 32px;background:{{background}};color:{{branding.ForegroundColor}}}.brand h1{margin:12px 0 0;font-size:22px}.logo{display:block;max-width:180px;max-height:56px;object-fit:contain;margin-bottom:16px}.body{padding:8px 32px 32px}.sso{background:transparent;color:inherit;border:1px solid #888;margin-top:16px}input:focus-visible,button:focus-visible{outline:2px solid {{accent}};outline-offset:3px}
            </style></head><body><main><header class="brand">{{logo}}<small>{{E(branding.OrganizationName)}}</small><h1>Sign in to {{E(branding.PortalTitleText)}}</h1></header><div class="body">
            <p>Use your work account to connect the portal on <strong>{{E(name)}}</strong>.</p><p class="error" role="alert">{{E(error)}}</p>
            <form method="post" action="/agent/portal-sign-in/{{id:D}}">
            <input type="hidden" name="{{E(token.FormFieldName)}}" value="{{E(token.RequestToken)}}">
            <label>Username<input name="username" autocomplete="username" maxlength="256" placeholder="Username" required autofocus></label>
            <label>Password<input name="password" type="password" autocomplete="current-password" maxlength="1024" required></label>
            <button type="submit">Sign in to portal</button></form>{{sso}}
            </div></main></body></html>
            """, "text/html; charset=utf-8");
    }
}
