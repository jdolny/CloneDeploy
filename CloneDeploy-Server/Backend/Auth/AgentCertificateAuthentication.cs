using System.Security.Claims;
using System.Text.Encodings.Web;
using CloneDeploy.SiteServices;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CloneDeploy_Server.Backend.Auth;

public sealed class AgentCertificateAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, AgentCertificateValidationService validation)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AgentCertificate";
    public const string PolicyName = "EnrolledAgent";
    public const string DeviceIdClaim = "AgentDeviceId";
    public const string CertificateIdClaim = "AgentCertificateId";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.IsHttps) return AuthenticateResult.Fail("HTTPS is required.");
        // Direct TLS identity, or a request-local identity verified by the trusted SiteService middleware.
        var certificate = await TrustedSiteProxy.GetAgentCertificateAsync(Context);
        if (certificate is null) return AuthenticateResult.NoResult();
        var registered = await validation.ValidateAsync(certificate, Context.RequestAborted);
        if (registered is null) return AuthenticateResult.Fail("Agent certificate is not active.");
        if (!await AgentRequestVerification.VerifyAsync(Context, certificate))
            return AuthenticateResult.Fail("A fresh signed agent request is required.");
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, registered.DeviceGuid.ToString("D")),
            new Claim(DeviceIdClaim, registered.DeviceId.ToString()),
            new Claim(CertificateIdClaim, registered.CertificateId.ToString("D")),
            new Claim(ClaimTypes.Name, registered.DeviceName)
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
