using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record WebCertificateDetails(string[] Hosts, DateTime ExpiresAtUtc, string Sha256, bool Enabled);

public sealed class WebCertificateService(ICloneDeployDbFactory factory, ISecretProtector secrets, IAuthorizationService authorization,
    TimeProvider clock, ILogger<WebCertificateService> logger)
{
    public Task<ServiceResult<WebCertificateDetails?>> GetAsync(ClaimsPrincipal user) => ServiceOperations.RunAsync(async () =>
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded)
            return ServiceResult<WebCertificateDetails?>.Fail("CertificateAuthority.Forbidden");
        await using var db = await factory.CreateContextAsync();
        var row = await db.AppCertificates.AsNoTracking().SingleOrDefaultAsync(x => x.Type == CertificateType.Web);
        if (row is null) return ServiceResult<WebCertificateDetails?>.Ok(null);
        using var cert = Load(row, secrets);
        return ServiceResult<WebCertificateDetails?>.Ok(Describe(cert, !row.IsRevoked));
    }, logger, "CertificateAuthority.Failed");

    public Task<ServiceResult> SaveAsync(ClaimsPrincipal user, string addresses, bool enabled) => ServiceOperations.RunAsync(async () =>
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded ||
            !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return ServiceResult.Fail("CertificateAuthority.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903421)");
        var row = await db.AppCertificates.SingleOrDefaultAsync(x => x.Type == CertificateType.Web);
        object details;
        if (enabled)
        {
            var root = await db.AppCertificates.SingleOrDefaultAsync(x => x.Type == CertificateType.Authority);
            if (root is null || root.IsRevoked) return ServiceResult.Fail("CertificateAuthority.Validation", UiText.Get(LocKeys.Ui.WebCertificateService.CreateAValidCloneDeployCAFirst));
            using var issuer = Load(root, secrets);
            X509Certificate2 cert;
            try { cert = WebCertificateGenerator.Generate(issuer, addresses, clock.GetUtcNow()); }
            catch (InvalidDataException ex) { return ServiceResult.Fail("CertificateAuthority.Validation", ex.Message); }
            using (cert)
            {
                var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
                row ??= new AppCertificate { Serial = "", SubjectName = "", PfxBlob = [], PasswordEncrypted = "", Type = CertificateType.Web };
                if (row.Id == 0) db.AppCertificates.Add(row);
                row.Serial = cert.SerialNumber; row.SubjectName = cert.Subject;
                row.NotBeforeUtc = cert.NotBefore.ToUniversalTime(); row.NotAfterUtc = cert.NotAfter.ToUniversalTime();
                row.PasswordEncrypted = secrets.Protect(password);
                row.PfxBlob = cert.ExportPkcs12(new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100000), password);
                row.IsRevoked = false; row.RevokedDateUtc = null;
                details = Describe(cert, true);
            }
        }
        else
        {
            if (row is null) return ServiceResult.Ok();
            row.IsRevoked = true; row.RevokedDateUtc = clock.GetUtcNow().UtcDateTime;
            details = new { Enabled = false };
        }
        db.AuditLogs.Add(new AuditLog { AppUserId = userId, UserName = user.Identity?.Name ?? "Administrator", AuditType = AuditType.Update,
            ObjectType = nameof(AppCertificate), ObjectName = "Web HTTPS certificate", ObjectJson = JsonSerializer.Serialize(details) });
        await db.SaveChangesAsync(); await tx.CommitAsync();
        return ServiceResult.Ok();
    }, logger, "CertificateAuthority.Failed");

    internal static X509Certificate2 Load(AppCertificate row, ISecretProtector secrets) =>
        X509CertificateLoader.LoadPkcs12(row.PfxBlob, secrets.Unprotect(row.PasswordEncrypted), X509KeyStorageFlags.EphemeralKeySet);
    private static WebCertificateDetails Describe(X509Certificate2 cert, bool enabled)
    {
        var extension = cert.Extensions.Single(x => x.Oid?.Value == "2.5.29.17");
        var san = new X509SubjectAlternativeNameExtension(extension.RawData, extension.Critical);
        return new(san.EnumerateDnsNames().Concat(san.EnumerateIPAddresses().Select(x => x.ToString())).ToArray(),
            cert.NotAfter.ToUniversalTime(), cert.GetCertHashString(HashAlgorithmName.SHA256), enabled);
    }
}
