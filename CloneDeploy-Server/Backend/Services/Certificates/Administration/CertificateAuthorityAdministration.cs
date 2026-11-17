using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using CloneDeploy_Server.Localization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;


/// <summary>
/// Stores and retrieves the application's certificate authority and its details.
/// </summary>
public sealed class CertificateAuthorityAdministration(ICloneDeployDbFactory factory, ISecretProtector secrets,
    IAuthorizationService authorization, TimeProvider clock, AgentCertificateTrust trust)
{
    public async Task<ServiceResult<CertificateAuthorityDetailsDto?>> GetAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded)
            return ServiceResult<CertificateAuthorityDetailsDto?>.Fail(ErrorKeys.CertificateAuthority.Forbidden);

        await using var db = await factory.CreateContextAsync();
        var entity = await db.AppCertificates.AsNoTracking().SingleOrDefaultAsync(x => x.Type == CertificateType.Authority, ct);
        if (entity is null)
            return ServiceResult<CertificateAuthorityDetailsDto?>.Ok(null);

        using var certificate = Load(entity);
        return ServiceResult<CertificateAuthorityDetailsDto?>.Ok(Describe(certificate, entity.IsRevoked));
    }

    public async Task<ServiceResult<CertificateAuthorityDetailsDto>> GenerateAsync(ClaimsPrincipal user, string organization, CancellationToken ct = default)
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded)
            return ServiceResult<CertificateAuthorityDetailsDto>.Fail(ErrorKeys.CertificateAuthority.Forbidden);

        if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return ServiceResult<CertificateAuthorityDetailsDto>.Fail(ErrorKeys.CertificateAuthority.Forbidden);
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize generation across application instances, including the initially empty table.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903421)", ct);
        if (await db.AppCertificates.AnyAsync(x => x.Type == CertificateType.Authority, ct))
            return ServiceResult<CertificateAuthorityDetailsDto>.Fail(ErrorKeys.CertificateAuthority.AlreadyExists);

        var generated = RootCertificateGenerator.Generate(organization, clock.GetUtcNow());
        if (!generated.HasData)
            return ServiceResult<CertificateAuthorityDetailsDto>.Fail(generated.Error!.Code, generated.Error.Args);

        using var certificate = generated.Data;
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var entity = new AppCertificate
        {
            Serial = certificate.SerialNumber, SubjectName = certificate.Subject,
            NotBeforeUtc = certificate.NotBefore.ToUniversalTime(), NotAfterUtc = certificate.NotAfter.ToUniversalTime(),
            Type = CertificateType.Authority, PasswordEncrypted = secrets.Protect(password),
            PfxBlob = certificate.ExportPkcs12(new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000), password)
        };
        db.AppCertificates.Add(entity);
        await db.SaveChangesAsync(ct);
        var details = Describe(certificate, false);
        db.AuditLogs.Add(new AuditLog
        {
            AppUserId = userId, UserName = user.Identity?.Name ?? userId.ToString(), AuditType = AuditType.Create,
            ObjectType = nameof(AppCertificate), ObjectId = entity.Id, ObjectName = entity.SubjectName,
            ObjectJson = JsonSerializer.Serialize(details), LoggedAtUtc = clock.GetUtcNow().UtcDateTime
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        trust.SetRoot(certificate.RawData);
        return ServiceResult<CertificateAuthorityDetailsDto>.Ok(details);
    }

    public async Task<ServiceResult<byte[]?>> GetPublicCertificateAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (!AgentReleaseStartupPrincipal.IsStartup(user) && !(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded)
            return ServiceResult<byte[]?>.Fail(ErrorKeys.CertificateAuthority.Forbidden);

        await using var db = await factory.CreateContextAsync();
        var entity = await db.AppCertificates.AsNoTracking().SingleOrDefaultAsync(x => x.Type == CertificateType.Authority, ct);
        if (entity is null)
            return ServiceResult<byte[]?>.Ok(null);

        using var certificate = Load(entity);
        return ServiceResult<byte[]?>.Ok(certificate.Export(X509ContentType.Cert));
    }

    private X509Certificate2 Load(AppCertificate entity) => X509CertificateLoader.LoadPkcs12(entity.PfxBlob,
        secrets.Unprotect(entity.PasswordEncrypted), X509KeyStorageFlags.EphemeralKeySet);

    private static CertificateAuthorityDetailsDto Describe(X509Certificate2 certificate, bool revoked) =>
        new(certificate.Subject, certificate.SerialNumber, certificate.GetCertHashString(HashAlgorithmName.SHA256),
            certificate.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime(), revoked);

}
