using System.Security.Cryptography.X509Certificates;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Loads the public agent CA trust during server startup. Initial CA creation updates trust directly.
/// </summary>
public sealed class AgentTrustRefresh(IServiceScopeFactory scopes, AgentCertificateTrust trust)
{
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
        var entity = await db.AppCertificates.AsNoTracking().SingleOrDefaultAsync(x => x.Type == CertificateType.Authority, ct);
        if (entity is null || entity.IsRevoked) { trust.SetRoot(null); return; }
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        using var certificate = X509CertificateLoader.LoadPkcs12(entity.PfxBlob, secrets.Unprotect(entity.PasswordEncrypted), X509KeyStorageFlags.EphemeralKeySet);
        trust.SetRoot(certificate.RawData);
    }

}
