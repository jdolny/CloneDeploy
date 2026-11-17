using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class ImagingComputerAccountLookup(ISecretProtector secrets)
{
    public async Task<string?> FindAsync(CloneDeployDbContext db, OuNamingCatalog catalog,
        int? ruleSourceId, string domain, string name, CancellationToken ct)
    {
        var sourceIds = catalog.Domains.Where(x => x.Name.Equals(domain, StringComparison.OrdinalIgnoreCase)
                && (ruleSourceId is null || x.SourceId == ruleSourceId))
            .Select(x => x.SourceId).Distinct().ToArray();
        if (sourceIds.Length != 1)
            throw new InvalidOperationException(sourceIds.Length == 0
                ? "Cannot check the AD computer account: configure and sync an enabled AD connection for the target domain."
                : "Cannot check the AD computer account: multiple connections serve this domain. Select an OU naming rule set to identify the connection.");
        var source = await db.DirectorySources.AsNoTracking().Include(x => x.LdapSettings).Include(x => x.SyncState)
            .SingleAsync(x => x.Id == sourceIds[0] && x.IsEnabled && !x.ApplicationAccessOnly
                && x.Provider == DirectoryProvider.ActiveDirectory, ct);
        var settings = DirectorySourceService.ToOptions(source);
        settings.ExpectedDomainGuid = source.SyncState?.DomainObjectGuid;
        var credential = await db.ManagedCredentials.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == settings.ManagedCredentialId, ct)
            ?? throw new InvalidOperationException("Cannot check the AD computer account: the directory managed credential is missing.");
        try
        {
            return await LdapActiveDirectoryReader.FindComputerAsync(settings, credential.Username,
                secrets.Unprotect(credential.PasswordEncrypted), domain, name, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("AD computer account lookup failed. Check the directory connection, certificate, and read permissions; OU selection was stopped.", ex);
        }
    }
}
