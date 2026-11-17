using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed partial class AgentEnrollmentService
{
    // The caller supplies IDs from certificate authentication, never from the request body.
    public async Task<AgentCertificateRenewal?> RenewCertificateAsync(int deviceId, Guid certificateId, CancellationToken ct = default, string? csrPem = null)
    {
        await using var db = await factory.CreateContextAsync();
        var now = clock.GetUtcNow();
        // Most check-ins need no write or enrollment lock. Recheck all state under the
        // shared lock below before issuing or returning a replacement.
        if (await db.AgentCertificates.AnyAsync(x => x.Id == certificateId && x.DeviceId == deviceId &&
            x.RevokedAtUtc == null && (x.RetireAfterUtc == null || x.RetireAfterUtc > now.UtcDateTime) &&
            (x.RenewedFromId == null || x.RenewalConfirmedAtUtc != null) && x.NotBeforeUtc <= now.UtcDateTime && x.NotAfterUtc > now.AddDays(30).UtcDateTime &&
            !x.Issuer.IsRevoked && x.Issuer.Type == CertificateType.Authority && x.Issuer.NotBeforeUtc <= now.UtcDateTime &&
            x.Issuer.NotAfterUtc > now.UtcDateTime && x.Device.ArchivedAtUtc == null &&
            x.Device.ProvisionStatus == DeviceProvisionStatus.Provisioned, ct)) return null;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Lock(db, ct); // Serializes renewals with enrollment, resets and approval changes.
        now = clock.GetUtcNow();
        var current = await db.AgentCertificates.Include(x => x.Device).Include(x => x.Issuer)
            .SingleOrDefaultAsync(x => x.Id == certificateId && x.DeviceId == deviceId, ct);
        if (current is null || current.RevokedAtUtc is not null || current.RetireAfterUtc <= now.UtcDateTime || current.NotBeforeUtc > now.UtcDateTime ||
            current.NotAfterUtc <= now.UtcDateTime || current.Issuer.IsRevoked ||
            current.Issuer.Type != CertificateType.Authority || current.Issuer.NotBeforeUtc > now.UtcDateTime ||
            current.Issuer.NotAfterUtc <= now.UtcDateTime || current.Device.ArchivedAtUtc is not null ||
            current.Device.ProvisionStatus != DeviceProvisionStatus.Provisioned)
            throw new AgentEnrollmentException(403, "Only an active device certificate can be renewed.");
        // A check-in signed by the new key proves that the replacement was saved and works.
        if (current.RenewedFromId is { } previousId && current.RenewalConfirmedAtUtc is null)
        {
            var previous = await db.AgentCertificates.SingleAsync(x => x.Id == previousId && x.DeviceId == deviceId, ct);
            current.RenewalConfirmedAtUtc = now.UtcDateTime;
            previous.RetireAfterUtc = previous.NotAfterUtc < now.AddHours(24).UtcDateTime ? previous.NotAfterUtc : now.AddHours(24).UtcDateTime;
            using var confirmed = X509CertificateLoader.LoadCertificate(current.CertificateDer);
            var publicKeyHash = Convert.ToHexString(SHA256.HashData(confirmed.PublicKey.ExportSubjectPublicKeyInfo()));
            var enrollments = await db.AgentProvisioningRequests.Where(x => x.AgentCertificateId == previous.Id).ToListAsync(ct);
            foreach (var row in enrollments) { row.AgentCertificate = current; row.PublicKeyHash = publicKeyHash; }
            await db.SaveChangesAsync(ct);
        }
        if (current.NotAfterUtc > now.AddDays(30).UtcDateTime || csrPem is null)
        {
            await tx.CommitAsync(ct);
            return null;
        }
        CertificateRequest csr;
        try
        {
            if (csrPem.Length > 16384) throw new CryptographicException();
            csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
            using var rsa = csr.PublicKey.GetRSAPublicKey();
            if (rsa is null || rsa.KeySize is < 2048 or > 4096) throw new CryptographicException();
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        { throw new AgentEnrollmentException(400, "A signed RSA renewal CSR with a 2048–4096 bit key is required."); }
        using var old = X509CertificateLoader.LoadCertificate(current.CertificateDer);
        var publicKey = csr.PublicKey.ExportSubjectPublicKeyInfo();
        if (old.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey))
            throw new AgentEnrollmentException(400, "Certificate renewal requires a new private key.");
        using var issuer = LoadIssuer(current.Issuer);
        var replacement = await db.AgentCertificates.SingleOrDefaultAsync(x => x.RenewedFromId == current.Id, ct);
        if (replacement is not null)
        {
            using var candidate = X509CertificateLoader.LoadCertificate(replacement.CertificateDer);
            if (replacement.RevokedAtUtc is not null || replacement.NotAfterUtc <= now.UtcDateTime ||
                replacement.RetireAfterUtc <= now.UtcDateTime || !candidate.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey))
                throw new AgentEnrollmentException(409, "This certificate already has a different or unavailable replacement. Retain the original pending renewal key.");
            await tx.CommitAsync(ct);
            return new(candidate.ExportCertificatePem(), issuer.ExportCertificatePem());
        }
        var end = now.AddYears(1) < issuer.NotAfter.ToUniversalTime() ? now.AddYears(1) : new DateTimeOffset(issuer.NotAfter.ToUniversalTime());
        if (end.UtcDateTime <= current.NotAfterUtc.AddDays(1))
            throw new AgentEnrollmentException(503, "The installation CA cannot extend the device certificate. Renew or replace CA trust before device certificates expire.");
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(current.Device.Guid.ToString("D"));
        var request = new CertificateRequest(subject.Build(), csr.PublicKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(AgentCertificateTrust.ClientAuthenticationOid) }, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var start = now.AddMinutes(-5) > issuer.NotBefore.ToUniversalTime() ? now.AddMinutes(-5) : new DateTimeOffset(issuer.NotBefore.ToUniversalTime());
        using var renewed = request.Create(issuer, start, end, RandomNumberGenerator.GetBytes(20));
        var registered = new AgentCertificate { Id = Guid.NewGuid(), DeviceId = deviceId, IssuerId = current.IssuerId, RenewedFromId = current.Id,
            CertificateDer = renewed.RawData, Sha256Fingerprint = renewed.GetCertHashString(HashAlgorithmName.SHA256),
            NotBeforeUtc = renewed.NotBefore.ToUniversalTime(), NotAfterUtc = renewed.NotAfter.ToUniversalTime() };
        db.AgentCertificates.Add(registered);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(renewed.ExportCertificatePem(), issuer.ExportCertificatePem());
    }
}
