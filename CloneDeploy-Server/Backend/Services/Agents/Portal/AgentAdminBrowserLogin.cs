using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Auth;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.AspNetCore.Identity;
namespace CloneDeploy_Server.Backend.Services;

// Verifies credentials without issuing management cookies or trusting existing browser sessions.
public sealed class AgentAdminBrowserLogin(UserManager<AppUser> users, SignInManager<AppUser> signIn,
    AdLoginService adLogin, AgentAdminSessions sessions, ICloneDeployDbFactory factory, IDataProtectionProvider protection, EntraAccountProvisioner accounts)
{
    private sealed record Proof(Guid Request, int User, string? Stamp);
    public static string CookieName(Guid id) => "__Secure-CD.AgentAdmin." + id.ToString("N");
    private static CookieOptions Cookie(Guid id) => new() { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = $"/agent/admin-sign-in/{id:D}", MaxAge = TimeSpan.FromMinutes(5), IsEssential = true };
    public async Task<string?> EntraAsync(Guid id, ClaimsPrincipal principal, HttpContext context)
    {
        if (sessions.PendingDevice(id) is null || principal.FindFirstValue(ClaimTypes.NameIdentifier) is not { } key) return null;
        var user = await accounts.ProvisionAsync(new ExternalLoginInfo(principal, "Entra", key, "Microsoft"), context.RequestAborted);
        if (user is null || !await signIn.CanSignInAsync(user) || await users.IsLockedOutAsync(user)) return null;
        return await sessions.CompleteAsync(id, user.Id, factory, context.RequestAborted, true);
    }
    public async Task<string?> CompleteEntraMfaAsync(Guid id, string value, string code, HttpContext context)
    {
        if (sessions.PendingDevice(id) is null || code.Length > 16 || value.Length > 4096) return null;
        Proof? proof;
        try { proof = JsonSerializer.Deserialize<Proof>(protection.CreateProtector("AgentAdminBrowserMfa.v1").ToTimeLimitedDataProtector().Unprotect(value)); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException) { return null; }
        if (proof is null || proof.Request != id) return null;
        var user = await users.FindByIdAsync(proof.User.ToString());
        if (user is null || user.SecurityStamp != proof.Stamp || !user.TwoFactorEnabled || !await signIn.CanSignInAsync(user) || await users.IsLockedOutAsync(user)) return null;
        if (!await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code.Replace(" ", "").Replace("-", "")))
        { await users.AccessFailedAsync(user); return null; }
        var redirect = await sessions.CompleteAsync(id, user.Id, factory, context.RequestAborted, false);
        if (redirect is not null) { await users.ResetAccessFailedCountAsync(user); context.Response.Cookies.Delete(CookieName(id), Cookie(id)); }
        return redirect;
    }
    public async Task<string?> CompleteAsync(Guid id, string username, string password, string code, CancellationToken ct)
    {
        if (sessions.PendingDevice(id) is null || username.Length is 0 or > 256 || password.Length is 0 or > 1024 || code.Length > 16) return null;
        var ad = await adLogin.AuthenticateAsync(username, password, ct);
        var user = ad.Handled ? ad.User : await users.FindByNameAsync(username);
        if (user is null || !await signIn.CanSignInAsync(user) || await users.IsLockedOutAsync(user)) return null;
        if (!ad.Handled)
        {
            if ((await users.GetLoginsAsync(user)).Any(x => x.LoginProvider is "ActiveDirectory" or "Entra")) return null;
            if (!await users.CheckPasswordAsync(user, password)) { await users.AccessFailedAsync(user); return null; }
        }
        await using var db = await factory.CreateContextAsync();
        if (await MfaPolicy.RequiresWebAsync(db, user) && !user.TwoFactorEnabled) return null;
        if (user.TwoFactorEnabled && !await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code.Replace(" ", "").Replace("-", "")))
        { await users.AccessFailedAsync(user); return null; }
        var redirect = await sessions.CompleteAsync(id, user.Id, factory, ct, false);
        if (redirect is not null) await users.ResetAccessFailedCountAsync(user);
        return redirect;
    }
}
