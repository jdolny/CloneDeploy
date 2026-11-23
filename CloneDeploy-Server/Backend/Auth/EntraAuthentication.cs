using System.Security.Claims;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CloneDeploy_Server.Backend.Auth;

public sealed class EntraOidcOptions(IServiceScopeFactory scopes) : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public void Configure(OpenIdConnectOptions options) { }
    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (name is not ("Entra" or "EntraPortal" or "EntraAgentAdmin")) return;
        var admin = name == "EntraAgentAdmin";
        var portal = name is "EntraPortal" or "EntraAgentAdmin";
        var callback = admin ? "signin-entra-agent-admin" : portal ? "signin-entra-portal" : "signin-entra";
        using var scope = scopes.CreateScope();
        using var db = scope.ServiceProvider.GetRequiredService<CloneDeployDbContext>();
        var settings = db.Set<EntraConnection>().AsNoTracking().SingleOrDefault();
        options.Authority = $"https://login.microsoftonline.com/{settings?.TenantId ?? Guid.Empty:D}/v2.0";
        options.ClientId = (settings?.ClientId ?? Guid.Empty).ToString();
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.CallbackPath = "/" + callback;
        options.ResponseType = "code";
        options.UsePkce = true;
        options.MapInboundClaims = false;
        options.SaveTokens = false;
        options.Scope.Clear(); options.Scope.Add("openid"); options.Scope.Add("profile"); options.Scope.Add("email");
        options.TokenValidationParameters.ValidateIssuer = true;
        options.TokenValidationParameters.ValidIssuer = $"https://login.microsoftonline.com/{settings?.TenantId ?? Guid.Empty:D}/v2.0";
        options.Events = new OpenIdConnectEvents
        {
            OnRedirectToIdentityProvider = async context =>
            {
                var current = await Settings(context.HttpContext);
                if (current is null || !current.SsoEnabled || current.CertificateEncrypted is null || current.ClientId.ToString() != context.Options.ClientId)
                {
                    var target = context.Properties.Items.TryGetValue(admin ? "admin.request" : "portal.request", out var request) && Guid.TryParse(request, out var id)
                        ? $"/agent/{(admin ? "admin-sign-in" : "portal-sign-in")}/{id:D}?error=entra" : portal ? "/" : "/login?error=unavailable";
                    context.Response.Redirect(target); context.HandleResponse(); return;
                }
                context.ProtocolMessage.RedirectUri = (portal ? await context.HttpContext.RequestServices.GetRequiredService<ServerUrlSettingsStore>().RequirePortalUrlAsync(context.HttpContext.RequestAborted) : await context.HttpContext.RequestServices.GetRequiredService<ServerUrlSettingsStore>().RequireAsync(context.HttpContext.RequestAborted)) + callback;
            },
            OnAuthorizationCodeReceived = async context =>
            {
                var current = await Settings(context.HttpContext);
                if (current is null || !current.SsoEnabled || current.CertificateEncrypted is null || current.ClientId.ToString() != context.Options.ClientId)
                { context.Fail("Entra sign-in is unavailable."); return; }
                using var cert = context.HttpContext.RequestServices.GetRequiredService<EntraCredentials>().Open(current.CertificateEncrypted);
                var endpoint = $"https://login.microsoftonline.com/{current.TenantId:D}/oauth2/v2.0/token";
                context.TokenEndpointRequest!.RedirectUri = (portal ? await context.HttpContext.RequestServices.GetRequiredService<ServerUrlSettingsStore>().RequirePortalUrlAsync(context.HttpContext.RequestAborted) : await context.HttpContext.RequestServices.GetRequiredService<ServerUrlSettingsStore>().RequireAsync(context.HttpContext.RequestAborted)) + callback;
                context.TokenEndpointRequest.ClientAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
                context.TokenEndpointRequest.ClientAssertion = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
                {
                    Issuer = current.ClientId.ToString(), Audience = endpoint,
                    Claims = new Dictionary<string, object> { ["sub"] = current.ClientId.ToString(), ["jti"] = Guid.NewGuid().ToString() },
                    NotBefore = DateTime.UtcNow.AddSeconds(-30), Expires = DateTime.UtcNow.AddMinutes(5),
                    SigningCredentials = new X509SigningCredentials(cert, SecurityAlgorithms.RsaSha256)
                });
            },
            OnTokenValidated = async context =>
            {
                var current = await Settings(context.HttpContext);
                var key = EntraAuthentication.IdentityKey(current, context.Principal);
                if (key is null || current?.ClientId.ToString() != context.Options.ClientId)
                { context.Fail("The Entra identity is not permitted."); return; }
                var identity = (ClaimsIdentity)context.Principal!.Identity!;
                foreach (var claim in identity.FindAll(ClaimTypes.NameIdentifier).ToArray()) identity.RemoveClaim(claim);
                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, key));
            },
            OnTicketReceived = async context =>
            {
                if (context.Properties is null) { context.Fail("Missing sign-in properties."); return; }
                if (context.Properties.Items.TryGetValue("admin.request", out var adminRequest))
                {
                    if (!admin || context.Properties.Items.ContainsKey("portal.request")) { context.Fail("Administrator sign-in requires its dedicated callback."); return; }
                    var adminRedirect = Guid.TryParse(adminRequest, out var adminId) && context.Principal is not null
                        ? await context.HttpContext.RequestServices.GetRequiredService<AgentAdminBrowserLogin>().EntraAsync(adminId, context.Principal, context.HttpContext) : null;
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.Headers["Referrer-Policy"] = "no-referrer";
                    context.Response.Redirect(adminRedirect ?? $"/agent/admin-sign-in/{adminId:D}?error=entra");
                    context.HandleResponse(); return;
                }
                if (admin) { context.Fail("A device-initiated administrator request is required."); return; }
                // AuthenticationProperties travel in protected OIDC state. Portal requests
                // complete their device-bound handoff without issuing an external/admin cookie.
                if (!context.Properties.Items.TryGetValue("portal.request", out var request))
                {
                    if (portal) { context.Fail("A device-initiated portal request is required."); }
                    return;
                }
                if (!portal) { context.Fail("Portal sign-in requires the portal callback."); return; }
                var redirect = Guid.TryParse(request, out var id) && context.Principal is not null
                    ? await context.HttpContext.RequestServices.GetRequiredService<AgentPortalSessions>().CompleteEntraAsync(
                        id, context.Principal, context.HttpContext.RequestServices.GetRequiredService<ICloneDeployDbFactory>(), context.HttpContext.RequestAborted)
                    : null;
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Redirect(redirect ?? $"/agent/portal-sign-in/{id:D}?error=entra");
                context.HandleResponse();
            },
            OnRemoteFailure = context =>
            {
                var target = context.Properties?.Items.TryGetValue(admin ? "admin.request" : "portal.request", out var request) == true && Guid.TryParse(request, out var id)
                    ? $"/agent/{(admin ? "admin-sign-in" : "portal-sign-in")}/{id:D}?error=entra" : portal ? "/" : "/login?error=unavailable";
                context.Response.Redirect(target); context.HandleResponse(); return Task.CompletedTask;
            }
        };
    }

    private static async Task<EntraConnection?> Settings(HttpContext context) =>
        await context.RequestServices.GetRequiredService<CloneDeployDbContext>().Set<EntraConnection>().AsNoTracking().SingleOrDefaultAsync(context.RequestAborted);
}

public static class EntraAuthentication
{
    // Called only after the OIDC middleware validates signature, audience, issuer and nonce.
    public static string? IdentityKey(EntraConnection? settings, ClaimsPrincipal? principal) =>
        settings?.SsoEnabled == true && Guid.TryParse(principal?.FindFirstValue("tid"), out var tenant) && tenant == settings.TenantId &&
        Guid.TryParse(principal?.FindFirstValue("oid"), out var oid) && oid != Guid.Empty ? $"{tenant:D}/{oid:D}" : null;

    public static IServiceCollection AddEntraAuthentication(this IServiceCollection services)
    {
        services.AddScoped<EntraAccountProvisioner>();
        services.ConfigureApplicationCookie(ConfigureSession);
        services.AddAuthentication().AddOpenIdConnect("Entra", _ => { }).AddOpenIdConnect("EntraPortal", _ => { }).AddOpenIdConnect("EntraAgentAdmin", _ => { });
        services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, EntraOidcOptions>();
        return services;
    }

    public static void ConfigureSession(CookieAuthenticationOptions options)
    {
        var validate = options.Events.OnValidatePrincipal;
        options.Events.OnValidatePrincipal = async context =>
        {
            if (int.TryParse(context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
            {
                var permissions = context.HttpContext.RequestServices.GetService<AppUserPermissionService>();
                if (permissions is not null && await permissions.GetDirectoryAccessAsync(id) is { Managed: true, Allowed: false })
                { context.RejectPrincipal(); await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme); return; }
            }
            await validate(context); // Keep Identity security-stamp validation.
            if (context.Properties.Items.TryGetValue("entra.session", out var entraSession) && entraSession == "true")
            {
                // Identity rebuilds the principal during stamp validation, dropping the
                // per-session authentication method. Restore it only for a surviving
                // authenticated principal using the protected cookie's session marker.
                if (context.Principal?.Identity is ClaimsIdentity { IsAuthenticated: true } identity &&
                    !context.Principal.HasClaim(ClaimTypes.AuthenticationMethod, "Entra"))
                    identity.AddClaim(new Claim(ClaimTypes.AuthenticationMethod, "Entra"));
            }
        };
        options.Events.OnSigningIn = async context =>
        {
            if (int.TryParse(context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
            {
                var permissions = context.HttpContext.RequestServices.GetService<AppUserPermissionService>();
                if (permissions is not null && await permissions.GetDirectoryAccessAsync(id) is { Managed: true, Allowed: false })
                    throw new InvalidOperationException("No current directory group mapping grants application access.");
            }
            if (context.Principal?.HasClaim(ClaimTypes.AuthenticationMethod, "Entra") == true)
            {
                context.Properties.Items["entra.session"] = "true";
                context.Properties.IsPersistent = false;
                context.Properties.AllowRefresh = true;
                context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1);
            }
        };
    }

    public static void MapEntraAuthentication(this WebApplication app)
    {
        app.MapGet("/auth/entra", async (HttpContext http, CloneDeployDbContext db, SignInManager<AppUser> signIn, string? returnUrl) =>
        {
            if (!await db.Set<EntraConnection>().AnyAsync(x => x.SsoEnabled && x.CertificateEncrypted != null, http.RequestAborted))
                return Results.Redirect("/login?error=unavailable");
            var redirect = "/auth/entra/complete?returnUrl=" + Uri.EscapeDataString(LocalReturn(returnUrl));
            var properties = signIn.ConfigureExternalAuthenticationProperties("Entra", redirect);
            return Results.Challenge(properties, ["Entra"]);
        }).AllowAnonymous();

        app.MapGet("/auth/entra/complete", async (HttpContext http, EntraAccountProvisioner accounts, SignInManager<AppUser> signIn, ILogger<EntraAccountProvisioner> logger, string? returnUrl) =>
        {
            try
            {
                var info = await signIn.GetExternalLoginInfoAsync();
                var user = info is null ? null : await accounts.ProvisionAsync(info, http.RequestAborted);
                if (user is null)
                {
                    if (http.RequestServices.GetService<SecurityActivity>() is { } security)
                        await security.SignInAsync("Unresolved Entra account", "Entra application access", SecurityActivity.Failure);
                    await signIn.SignOutAsync();
                    return Results.Redirect("/login?error=entra-group");
                }
                // Microsoft authenticates this session; CloneDeploy MFA applies to password sign-ins.
                var result = await signIn.ExternalLoginSignInAsync("Entra", info!.ProviderKey, isPersistent: false, bypassTwoFactor: true);
                if (http.RequestServices.GetService<SecurityActivity>() is { } activity)
                    await activity.SignInAsync(user.UserName ?? "Entra account", "Entra", result.IsLockedOut ? SecurityActivity.Lockout :
                        result.Succeeded ? SecurityActivity.Success : SecurityActivity.Failure);
                if (!result.Succeeded) return Results.Redirect("/login?error=" + (result.IsLockedOut ? "locked" : "invalid"));
                return Results.Redirect(LocalReturn(returnUrl));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Entra sign-in could not provision an application account");
                await signIn.SignOutAsync();
                return Results.Redirect("/login?error=unavailable");
            }
            finally { await http.SignOutAsync(IdentityConstants.ExternalScheme); }
        }).AllowAnonymous();
    }

    public static string LocalReturn(string? value) => !string.IsNullOrWhiteSpace(value) && value.StartsWith('/') &&
        !value.StartsWith("//") && !value.Contains('\\') && !value.Any(char.IsControl) ? value : "/";
}
