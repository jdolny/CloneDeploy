using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CloneDeploy_Server.Backend.Auth;

public sealed class ImagingAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    ImagingTokens tokens, UserManager<AppUser> users, ImagingAccess access)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ImagingBearer";
    public const string PolicyName = "ImagingApi";
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (header.Length == 0) return AuthenticateResult.NoResult();
        if (!Request.IsHttps) return AuthenticateResult.Fail("HTTPS is required.");
        if (header.Length > 8192 || !AuthenticationHeaderValue.TryParse(header, out var value) ||
            !string.Equals(value.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(value.Parameter))
            return AuthenticateResult.Fail("Invalid bearer token.");
        var token = value.Parameter;
        if (tokens.IsAutomation(token)) return Success("automation", "automation", "automation");
        var payload = tokens.Read(token);
        if (payload is null) return AuthenticateResult.Fail("Invalid or expired token.");
        var user = await users.FindByIdAsync(payload.UserId);
        if (user is null || string.IsNullOrEmpty(user.SecurityStamp) || user.SecurityStamp != payload.SecurityStamp ||
            (await access.RequiresMfaAsync(user) && (!user.TwoFactorEnabled || !payload.MfaVerified)) || !await access.Allowed(user))
            return AuthenticateResult.Fail("Session is no longer valid.");
        return Success(user.Id.ToString(), user.UserName ?? "", "user", await access.GrantsAsync(user));
    }
    private AuthenticateResult Success(string id, string name, string kind) => Success(id, name, kind, []);

    private AuthenticateResult Success(string id, string name, string kind, IEnumerable<string> permissions)
    {
        // Deliberately omit browser/admin/agent claims. This identity is only for imaging endpoints.
        var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, id), new(ClaimTypes.Name, name), new("imaging_kind", kind)], SchemeName);
        identity.AddClaims(permissions.Select(x => new Claim(CloneDeploy_Server.Backend.Common.Constants.Claims.Permission, x)));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
