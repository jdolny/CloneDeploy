using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

// Trusted internal job entry point; UI callers go through the admin-authorized facade.
/// <summary>
/// Runs directory synchronization and reports its outcome.
/// </summary>
public sealed class DirectorySyncRunner(
    ICloneDeployDbFactory dbFactory,
    IEnumerable<IDirectorySyncProvider> providers,
    ILogger<DirectorySyncRunner> logger)
{
    public async Task<DirectorySyncOutcomeDto> RunAsync(int sourceId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateContextAsync();
            var source = await db.DirectorySources.AsNoTracking().Include(x => x.LdapSettings)
                .SingleOrDefaultAsync(x => x.Id == sourceId, ct);
            if (source is null)
                return new(null, "Select a saved directory source.");
            if (!source.IsEnabled)
                return new(null, "This directory source is disabled.");

            if (await db.DirectorySources.AnyAsync(x => x.Id != source.Id && (x.Provider == source.Provider || (!source.ApplicationAccessOnly && !x.ApplicationAccessOnly)), ct))
                return new(null, "Only one full sync and one connection per provider are supported.");
            var provider = providers.SingleOrDefault(x => x.Provider == source.Provider);
            if (provider is null)
                return new(null, "The directory provider is not implemented yet.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (source.Provider == DirectoryProvider.ActiveDirectory && source.LdapSettings is null)
                return new(null, "LDAP settings are missing.");
            if (source.LdapSettings is { } ldap)
            {
                var error = DirectorySourceService.ToOptions(source).GetValidationError();
                if (error is not null)
                    return new(null, error);
                timeout.CancelAfter(TimeSpan.FromSeconds(ldap.TimeoutSeconds));
            }

            if (source.Provider == DirectoryProvider.EntraId) timeout.CancelAfter(TimeSpan.FromMinutes(30));
            var token = timeout.Token;
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock_shared(hashtextextended('directory-configuration', 0))", token);
            if (!await db.DirectorySources.AnyAsync(x => x.Id == source.Id && x.IsEnabled && x.Provider == source.Provider && x.ApplicationAccessOnly == source.ApplicationAccessOnly, token) ||
                await db.DirectorySources.AnyAsync(x => x.Id != source.Id && (x.Provider == source.Provider || (!source.ApplicationAccessOnly && !x.ApplicationAccessOnly)), token))
                return new(null, "Directory configuration changed. Retry the sync.");
            var lockName = "ad-sync:" + source.Id;
            var acquired = await db.Database.SqlQuery<bool>(
                $"SELECT pg_try_advisory_xact_lock(hashtextextended({lockName}, 0)) AS \"Value\"").SingleAsync(token);
            if (!acquired)
                return new(null, "A directory sync is already running. Try again after it finishes.");

            var summary = await provider.SyncAsync(source, db, token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return new(summary, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(exception, "Directory sync timed out for source {SourceId}", sourceId);
            return new(null, "Directory sync timed out. The previous completed sync was retained.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Directory sync failed for source {SourceId}", sourceId);
            return new(null, "Directory sync failed. Check the server log, connection settings, and certificate trust.");
        }
    }
}