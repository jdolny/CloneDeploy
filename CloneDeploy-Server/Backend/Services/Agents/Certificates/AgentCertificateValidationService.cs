using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Checks whether an agent's identity certificate is registered and allowed to connect.
/// </summary>
public sealed class AgentCertificateValidationService(ICloneDeployDbFactory factory,
    AgentCertificateTrust trust, TimeProvider clock, ILogger<AgentCertificateValidationService> logger)
{
    public async Task<AgentCertificateIdentityDto?> ValidateAsync(X509Certificate2 certificate, CancellationToken ct)
    {
        try
        {
            var now = clock.GetUtcNow();
            if (!trust.Validate(certificate, now)) return null;
            var fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            await using var db = await factory.CreateContextAsync();
            var registered = await db.AgentCertificates.AsNoTracking().Include(x => x.Device).Include(x => x.Issuer)
                .SingleOrDefaultAsync(x => x.Sha256Fingerprint == fingerprint, ct);
            if (registered is null || registered.RevokedAtUtc is not null || registered.RetireAfterUtc <= now.UtcDateTime || registered.Issuer.IsRevoked ||
                registered.Issuer.Type != CertificateType.Authority || registered.Issuer.NotAfterUtc <= now.UtcDateTime ||
                registered.Issuer.NotBeforeUtc > now.UtcDateTime || registered.NotBeforeUtc > now.UtcDateTime || registered.NotAfterUtc <= now.UtcDateTime ||
                registered.Device.ArchivedAtUtc is not null || registered.Device.ProvisionStatus != DeviceProvisionStatus.Provisioned ||
                !certificate.RawData.AsSpan().SequenceEqual(registered.CertificateDer))
                return null;
            return new(registered.Device.Guid, registered.DeviceId, registered.Id, registered.Device.Name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Agent certificate validation failed");
            return null;
        }
    }
}
