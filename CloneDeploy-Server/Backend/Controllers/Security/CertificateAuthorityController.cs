using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Services;
using CloneDeploy_Server.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloneDeploy_Server.Backend.Controllers.Security;

[ApiController]
[Authorize(Policy = Constants.Permissions.AdminOnly)]
[Route("api/ui/administration/server/certificates")]
public sealed class CertificateAuthorityController(CertificateAuthorityService certificates) : ControllerBase
{
    [HttpGet("certificate")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Download(CancellationToken ct)
    {
        var certificate = await certificates.GetPublicCertificateAsync(User, ct);
        if (!certificate.Success)
            return certificate.Error.Code == ErrorKeys.CertificateAuthority.Forbidden
                ? Forbid()
                : Problem(statusCode: StatusCodes.Status500InternalServerError, detail: "Could not retrieve the public certificate.");

        return certificate.Data is null ? NotFound() : File(certificate.Data, "application/pkix-cert", "CloneDeploy-Root-CA.cer");
    }
}
