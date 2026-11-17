using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class StorageMoveService(ICloneDeployDbFactory factory, StorageContentStorage storage,
    AuthenticationStateProvider authentication, IAuthorizationService authorization)
{
    private async Task<bool> AllowedAsync() =>
        (await authorization.AuthorizeAsync((await authentication.GetAuthenticationStateAsync()).User, Constants.Permissions.AdminOnly)).Succeeded;

    public async Task<List<StorageMove>> ListAsync(int provider)
    {
        if (!await AllowedAsync()) throw new UnauthorizedAccessException();
        await using var db = await factory.CreateContextAsync();
        return await db.Set<StorageMove>().AsNoTracking().Where(x => x.SourceId == provider || x.DestinationId == provider)
            .OrderByDescending(x => x.CreatedAtUtc).Take(20).ToListAsync();
    }

    public async Task<ServiceResult> StartAsync(int source, int destination, StoragePurpose purposes)
    {
        if (!await AllowedAsync()) return ServiceResult.Fail("StorageMount.Forbidden");
        if (source == destination || purposes is < StoragePurpose.Imaging or > StoragePurpose.All)
            return ServiceResult.Fail("Storage.Validation", "Choose a different destination and a content purpose.");
        await storage.EnsureReadyAsync(source);
        await storage.EnsureReadyAsync(destination);
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await StorageProviderAdministration.LockAsync(db);
        var providers = await db.StorageProviders.Where(x => x.Id == source || x.Id == destination).ToListAsync();
        if (providers.Count != 2 || providers.Any(x => !x.IsEnabled || x.SiteServiceId.HasValue) ||
            providers.Single(x => x.Id == destination) is not { } target || (target.Purposes & purposes) != purposes)
            return ServiceResult.Fail("Storage.Validation", "Choose enabled main-application providers. The destination must support every selected purpose.");
        if (await db.Set<StorageMove>().AnyAsync(x => x.State != "Completed" && (x.State != "Failed" || x.CleanupAfterUtc != null) &&
            (x.SourceId == source || x.DestinationId == source || x.SourceId == destination || x.DestinationId == destination)))
            return ServiceResult.Fail("Storage.Validation", "A move or cleanup already involves one of these providers.");
        var inventory = await StorageContentInventory.ReadAsync(db, source, purposes);
        if (inventory.Blockers.Count > 0) return ServiceResult.Fail("Storage.Validation", string.Join(" ", inventory.Blockers));
        if (!inventory.HasPrimary) return ServiceResult.Fail("Storage.Validation", "No primary content or imaging replication targets match this selection.");
        db.Add(new StorageMove { SourceId = source, DestinationId = destination, Purposes = purposes });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> RetryAsync(Guid id)
    {
        if (!await AllowedAsync()) return ServiceResult.Fail("StorageMount.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await StorageProviderAdministration.LockAsync(db);
        var move = await db.Set<StorageMove>().SingleAsync(x => x.Id == id);
        if (move.State != "Failed") return ServiceResult.Fail("Storage.Validation", "Only failed moves can be retried.");
        if (await db.Set<StorageMove>().AnyAsync(x => x.Id != id && x.State != "Completed" && (x.State != "Failed" || x.CleanupAfterUtc != null) &&
            (x.SourceId == move.SourceId || x.DestinationId == move.SourceId || x.SourceId == move.DestinationId || x.DestinationId == move.DestinationId)))
            return ServiceResult.Fail("Storage.Validation", "Another move is using these providers.");
        move.State = move.CleanupAfterUtc.HasValue ? "Cleaning" : "Queued";
        move.Error = null;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> KeepOriginalsAsync(Guid id)
    {
        if (!await AllowedAsync()) return ServiceResult.Fail("StorageMount.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await StorageProviderAdministration.LockAsync(db);
        var move = await db.Set<StorageMove>().SingleAsync(x => x.Id == id);
        if (move.State != "Failed" || move.CleanupAfterUtc is null)
            return ServiceResult.Fail("Storage.Validation", "Only failed cleanup can be finished by keeping its remaining originals.");
        move.State = "Completed";
        move.Error = "Ownership moved. Remaining original files were kept on the source by administrator choice.";
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return ServiceResult.Ok();
    }
}
