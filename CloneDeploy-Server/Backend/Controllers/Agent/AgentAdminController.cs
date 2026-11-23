using System.Security.Claims;
using System.Text.Encodings.Web;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CloneDeploy_Server.Backend.Controllers.Agent;

[ApiController]
public sealed class AgentAdminController(AgentAdminSessions sessions, ICloneDeployDbFactory factory, IConfiguration? configuration = null) : ControllerBase
{
    private const int MaxRequestBytes = 8192;

    private Guid DeviceGuid => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost("api/agent/v1/admin/sign-in"), Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
    public ActionResult<AgentAdminSignInResponse> Start(AgentAdminSignInRequest request)
    {
        if (!Request.IsHttps) return BadRequest();
        Response.Headers.CacheControl = "no-store";
        try { return sessions.Start(DeviceGuid, request) with { BrowserPort = CloneDeploy.SiteServices.ListenerPorts.Read(configuration ?? new ConfigurationBuilder().Build()).PortalPort }; }
        catch (ArgumentException) { return BadRequest("Invalid administrator sign-in request."); }
        catch (InvalidOperationException) { return StatusCode(StatusCodes.Status429TooManyRequests, "Try again shortly."); }
    }

    [HttpGet("agent/admin-sign-in/{id:guid}"), AllowAnonymous, EnableRateLimiting("PortalBrowser")]
    public Task<IActionResult> Page(Guid id, [FromServices] IAntiforgery csrf, CancellationToken ct) => Form(id, csrf, Request.Query.ContainsKey("error") ? "Microsoft sign-in could not authorize this request. Check application access and MFA enrollment." : null, ct);

    [HttpPost("agent/admin-sign-in/{id:guid}"), AllowAnonymous, EnableRateLimiting("PortalPassword"), RequestSizeLimit(MaxRequestBytes)]
    public async Task<IActionResult> Login(Guid id, [FromServices] IAntiforgery csrf,
        [FromServices] AgentAdminBrowserLogin login, CancellationToken ct)
    {
        if (!Request.IsHttps || sessions.PendingDevice(id) is null) return BadRequest("Start sign-in again from the agent.");
        try { await csrf.ValidateRequestAsync(HttpContext); }
        catch (AntiforgeryValidationException) { return BadRequest("The sign-in form expired."); }
        Response.Headers.CacheControl = "no-store"; Response.Headers["Referrer-Policy"] = "no-referrer";
        var form = await Request.ReadFormAsync(ct);
        var redirect = Request.Cookies[AgentAdminBrowserLogin.CookieName(id)] is { } proof
            ? await login.CompleteEntraMfaAsync(id, proof, form["code"].ToString(), HttpContext)
            : await login.CompleteAsync(id, form["username"].ToString(), form["password"].ToString(), form["code"].ToString(), ct);
        return redirect is null ? await Form(id, csrf, "Unable to sign in. Check your credentials, verification code and access to agent tools. Required MFA must first be enrolled in the management site.", ct) : Redirect(redirect);
    }
    [HttpPost("agent/admin-sign-in/{id:guid}/entra"), AllowAnonymous, EnableRateLimiting("PortalBrowser"), RequestSizeLimit(MaxRequestBytes)]
    public async Task<IActionResult> Entra(Guid id, [FromServices] IAntiforgery csrf)
    {
        if (!Request.IsHttps || sessions.PendingDevice(id) is null) return BadRequest();
        try { await csrf.ValidateRequestAsync(HttpContext); }
        catch (AntiforgeryValidationException) { return BadRequest(); }
        var properties = new AuthenticationProperties();
        properties.Items["admin.request"] = id.ToString("D");
        return Challenge(properties, "EntraAgentAdmin");
    }
    private async Task<IActionResult> Form(Guid id, IAntiforgery csrf, string? error, CancellationToken ct)
    {
        if (!Request.IsHttps || sessions.PendingDevice(id) is null) return BadRequest("Start sign-in again from the agent.");
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Frame-Options"] = "DENY";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; form-action 'self' https://login.microsoftonline.com http://127.0.0.1:*; frame-ancestors 'none'; base-uri 'none'";
        await using var db = await factory.CreateContextAsync();
        var branding = await BrandingSettings.ReadAsync(db, ct);
        var token = csrf.GetAndStoreTokens(HttpContext);
        var mfa = Request.Cookies.ContainsKey(AgentAdminBrowserLogin.CookieName(id));
        static string E(string? value) => HtmlEncoder.Default.Encode(value ?? "");
        var background = AgentBranding.ValidColor(branding.BackgroundColor) ? branding.BackgroundColor : "#2459D6";
        var accent = AgentBranding.ValidColor(branding.AccentColor) ? branding.AccentColor : "#2459D6";
        var logo = branding.LogoPng is { Length: > 0 } png
            ? $"<img class=\"logo\" src=\"data:image/png;base64,{Convert.ToBase64String(png)}\" alt=\"{E(branding.OrganizationName)}\">" : "";
        return Content($$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Agent tools sign-in — {{E(branding.PortalTitleText)}}</title><style>
            :root{color-scheme:light dark;font-family:system-ui,sans-serif}body{margin:0;min-height:100vh;display:grid;place-items:center;background:light-dark(#f3f5f9,#17191f);color:light-dark(#172033,#f3f5f9)}
            main{max-width:390px;margin:24px;padding:0;border-radius:16px;overflow:hidden;width:calc(100% - 48px);background:light-dark(white,#242730);box-shadow:0 10px 40px #0002}h1{font-size:24px}p{line-height:1.5}label{display:block;margin-top:20px}input{box-sizing:border-box;width:100%;padding:12px;margin-top:7px;border:1px solid #888;border-radius:7px;font:inherit}button{width:100%;margin-top:24px;border:0;border-radius:7px;padding:13px;background:{{accent}};color:{{branding.AccentForegroundColor}};cursor:pointer;font:inherit;font-weight:600}.error{color:light-dark(#b42318,#ffb4ab)}small{opacity:.8}.brand{padding:28px 32px;background:{{background}};color:{{branding.ForegroundColor}}}.brand h1{margin:12px 0 0;font-size:22px}.logo{display:block;max-width:180px;max-height:56px;object-fit:contain;margin-bottom:16px}.body{padding:8px 32px 32px}.sso{background:transparent;color:inherit;border:1px solid #888;margin-top:16px}input:focus-visible,button:focus-visible{outline:2px solid {{accent}};outline-offset:3px}
            </style></head><body><main><header class="brand">{{logo}}<small>{{E(branding.OrganizationName)}}</small><h1>Agent tools sign-in</h1></header><div class="body">
            <p>Sign in with an account authorized to use agent tools.</p><p class="error" role="alert">{{E(error)}}</p>
            <form method="post" action="/agent/admin-sign-in/{{id:D}}"><input type="hidden" name="{{E(token.FormFieldName)}}" value="{{E(token.RequestToken)}}">
            <label>Username<input name="username" autocomplete="username" maxlength="256" {{(mfa ? "disabled" : "required autofocus")}}></label>
            <label>Password<input name="password" type="password" autocomplete="current-password" maxlength="1024" {{(mfa ? "disabled" : "required")}}></label>
            <label>Authenticator code (when enabled)<input name="code" autocomplete="one-time-code" inputmode="numeric" maxlength="16"></label>
            <button type="submit">Authorize agent</button></form>
            <form method="post" action="/agent/admin-sign-in/{{id:D}}/entra"><input type="hidden" name="{{E(token.FormFieldName)}}" value="{{E(token.RequestToken)}}"><button class="sso" type="submit">Sign in with Microsoft</button></form></div></main></body></html>
            """, "text/html; charset=utf-8");
    }

    [HttpPost("api/agent/v1/admin/exchange"), Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
    public async Task<ActionResult<AgentAdminSessionResponse>> Exchange(AgentAdminCodeRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await sessions.ExchangeAsync(DeviceGuid, request, factory, ct);
        return result is null ? Unauthorized() : result;
    }

    [HttpPost("api/agent/v1/admin/session"), Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
    public async Task<ActionResult<AgentAdminSessionResponse>> Session(AgentAdminSessionRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await sessions.ValidateAsync(DeviceGuid, request, factory, ct);
        return result is null ? Unauthorized() : result;
    }

    [HttpPost("api/agent/v1/admin/driver-job"), Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
    public async Task<ActionResult<AgentAdminSessionResponse>> DriverJob(AgentAdminSessionRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await sessions.StartDriverJobAsync(DeviceGuid, request, factory, ct);
        return result is null ? Unauthorized() : result;
    }

    [HttpPost("api/agent/v1/admin/sign-out"), Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
    public IActionResult SignOut(AgentAdminSessionRequest request) { sessions.Revoke(DeviceGuid, request); return NoContent(); }
}
