using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Manages storage location settings and checks whether locations can be deleted.
/// </summary>
public sealed class StorageProviderAdministration(
    ICloneDeployDbFactory dbFactory,
    StorageMountPaths paths,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization)
{
    private readonly ICloneDeployDbFactory _dbFactory = dbFactory;

    public async Task<ServiceResult<List<StorageSiteDto>>> GetSitesAsync()
    {
        if (!await IsAdminAsync()) return ServiceResult<List<StorageSiteDto>>.Fail("StorageMount.Forbidden");
        await using var db = await _dbFactory.CreateContextAsync();
        return ServiceResult<List<StorageSiteDto>>.Ok(await db.SiteServices.AsNoTracking()
            .Where(x => !x.IsWindowsWorker && x.State != SiteRegistrationState.Revoked).OrderBy(x => x.Name)
            .Select(x => new StorageSiteDto(x.Id, x.Name, x.HostOs)).ToListAsync());
    }

    public async Task<ServiceResult<List<StorageProviderDto>>> GetAsync(string? searchText = null)
    {
        if (!await IsAdminAsync()) return ServiceResult<List<StorageProviderDto>>.Fail("StorageMount.Forbidden");
        await using var db = await _dbFactory.CreateContextAsync();

        var query = db.StorageProviders
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            searchText = searchText.Trim();
            query = query.Where(x =>
                EF.Functions.ILike(x.Name, $"%{searchText}%") ||
                EF.Functions.ILike(x.Path, $"%{searchText}%") ||
                (x.ManagedCredential != null && EF.Functions.ILike(x.ManagedCredential.Name, $"%{searchText}%")) ||
                (x.ManagedCredential != null && EF.Functions.ILike(x.ManagedCredential.Username, $"%{searchText}%")));
        }

        return ServiceResult<List<StorageProviderDto>>.Ok(await query.OrderBy(x => x.Name).Select(x => new StorageProviderDto
        {
            Id = x.Id, Name = x.Name, Path = x.Path, LinuxMountPath = x.LinuxMountPath, Type = x.Type, IsEnabled = x.IsEnabled,
            ImagingDirectConnect = x.ImagingDirectConnect,
            WinPeSmbPath = x.WinPeSmbPath,
            Purposes = x.Purposes, PreferredClientNetworks = x.PreferredClientNetworks, SiteServiceId = x.SiteServiceId,
            SiteServiceName = x.SiteService == null ? null : x.SiteService.Name,
            IsImagingDefault = x.IsImagingDefault, IsManagementDefault = x.IsManagementDefault, ManagedCredentialId = x.ManagedCredentialId,
            ManagedCredentialName = x.ManagedCredential == null ? null : x.ManagedCredential.Name
        }).ToListAsync());
    }

    public async Task<ServiceResult<StorageProvider>> CreateAsync(StorageProvider storageProvider)
    {
        if (!await IsAdminAsync()) return ServiceResult<StorageProvider>.Fail("StorageMount.Forbidden");
        storageProvider = new StorageProvider
        {
            Id = 0, Name = storageProvider.Name, Path = storageProvider.Path, LinuxMountPath = storageProvider.LinuxMountPath,
            ImagingDirectConnect = storageProvider.ImagingDirectConnect,
            WinPeSmbPath = storageProvider.WinPeSmbPath,
            Purposes = storageProvider.Purposes, PreferredClientNetworks = storageProvider.PreferredClientNetworks, SiteServiceId = storageProvider.SiteServiceId,
            IsImagingDefault = storageProvider.IsImagingDefault, IsManagementDefault = storageProvider.IsManagementDefault,
            Type = storageProvider.Type, IsEnabled = storageProvider.IsEnabled,
            ManagedCredentialId = storageProvider.ManagedCredentialId
        };
        await using var db = await _dbFactory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await LockAsync(db);

        var validation = await ValidateUniqueAsync(db, storageProvider);
        if (!validation.Success)
            return validation;

        if (storageProvider.SiteServiceId == null && storageProvider.IsEnabled)
        {
            storageProvider.IsImagingDefault |= storageProvider.Purposes.HasFlag(StoragePurpose.Imaging) && !await db.StorageProviders.AnyAsync(x => x.IsImagingDefault);
            storageProvider.IsManagementDefault |= storageProvider.Purposes.HasFlag(StoragePurpose.DeviceManagement) && !await db.StorageProviders.AnyAsync(x => x.IsManagementDefault);
        }
        await ClearPreviousDefaultsAsync(db, storageProvider);
        db.StorageProviders.Add(storageProvider);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult<StorageProvider>.Ok(storageProvider);
    }

    public async Task<ServiceResult<StorageProvider>> UpdateAsync(StorageProvider storageProvider)
    {
        if (!await IsAdminAsync()) return ServiceResult<StorageProvider>.Fail("StorageMount.Forbidden");
        storageProvider = new StorageProvider
        {
            Id = storageProvider.Id, Name = storageProvider.Name, Path = storageProvider.Path, LinuxMountPath = storageProvider.LinuxMountPath,
            ImagingDirectConnect = storageProvider.ImagingDirectConnect,
            WinPeSmbPath = storageProvider.WinPeSmbPath,
            Purposes = storageProvider.Purposes, PreferredClientNetworks = storageProvider.PreferredClientNetworks, SiteServiceId = storageProvider.SiteServiceId,
            IsImagingDefault = storageProvider.IsImagingDefault, IsManagementDefault = storageProvider.IsManagementDefault,
            Type = storageProvider.Type, IsEnabled = storageProvider.IsEnabled,
            ManagedCredentialId = storageProvider.ManagedCredentialId
        };
        await using var db = await _dbFactory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await LockAsync(db);

        var validation = await ValidateUniqueAsync(db, storageProvider);
        if (!validation.Success)
            return validation;

        var existing = await db.StorageProviders.FindAsync(storageProvider.Id);

        if (existing is null)
            return ServiceResult<StorageProvider>.Fail(ErrorKeys.Common.NotFound, storageProvider.Id);

        if ((existing.IsImagingDefault && !storageProvider.IsImagingDefault || existing.IsManagementDefault && !storageProvider.IsManagementDefault) && !storageProvider.IsEnabled)
            return ServiceResult<StorageProvider>.Fail("Storage.Validation", UiText.Get(LocKeys.Ui.StorageProviderAdministration.ChooseAnotherDefaultBeforeDisablingThisProvider));
        if (existing.SiteServiceId != storageProvider.SiteServiceId)
            return ServiceResult<StorageProvider>.Fail("Storage.Validation", UiText.Get(LocKeys.Ui.StorageProviderAdministration.AProviderSHostCannotChangeCreateAProvider));
        var removedPurposes = existing.Purposes & ~storageProvider.Purposes;
        if (removedPurposes != 0)
        {
            var inventory = await StorageContentInventory.ReadAsync(db, existing.Id, removedPurposes);
            if (inventory.HasPrimary || inventory.Blockers.Count > 0)
                return ServiceResult<StorageProvider>.Fail("Storage.Validation", "Move or remove the content for the removed purpose first: " + inventory.Summary);
        }
        if (await HasMoveAsync(db, [existing.Id]))
            return ServiceResult<StorageProvider>.Fail("Storage.Validation", "A content move or cleanup is pending for this provider. Finish it before changing provider settings.");
        var pathChanged = !string.Equals(existing.Path, storageProvider.Path, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existing.LinuxMountPath, storageProvider.LinuxMountPath, StringComparison.Ordinal);
        if ((existing.IsEnabled && !storageProvider.IsEnabled || existing.Type != storageProvider.Type || pathChanged) &&
            await HasContentAsync(db, [existing.Id]))
        {
            // Failed/pending shared replicas are rebuildable copies, not primary files.
            // Site workers have separate leases, so this exception is main-server only.
            var canRetryAtNewPath = pathChanged && existing.SiteServiceId is null &&
                existing.Type == StorageProviderType.Smb && storageProvider.Type == existing.Type &&
                existing.IsEnabled == storageProvider.IsEnabled;
            if (canRetryAtNewPath)
            {
                // Do not wait while holding the provider table lock: the runner acquires
                // its session lock first. A failed try releases our transaction on return.
                canRetryAtNewPath = await db.Database.SqlQueryRaw<bool>(
                    "SELECT pg_try_advisory_xact_lock(903421) AS \"Value\"").SingleAsync();
                if (!canRetryAtNewPath)
                    return ServiceResult<StorageProvider>.Fail("Storage.Validation", "Shared replication is running. Retry the path change when it finishes.");
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903419)");
                canRetryAtNewPath = await CanRetargetSharedRetriesAsync(db, existing.Id);
            }
            if (!canRetryAtNewPath)
                return ServiceResult<StorageProvider>.Fail("Storage.Validation", "This change would disconnect existing content. Move it first: " + (await StorageContentInventory.ReadAsync(db, existing.Id, StoragePurpose.All)).Summary + $"; {await db.Set<StorageReadLease>().CountAsync(x => x.StorageProviderId == existing.Id && x.ExpiresAtUtc > DateTime.UtcNow)} active transfers; {await db.Set<SharedContentReplica>().CountAsync(x => x.StorageProviderId == existing.Id)} shared replicas; {await db.Set<ImageReplica>().CountAsync(x => x.StorageProviderId == existing.Id)} image replicas.");
            foreach (var replica in await db.Set<SharedContentReplica>().Where(x => x.StorageProviderId == existing.Id).ToListAsync())
            {
                replica.State = ImageReplicaState.Pending;
                replica.Error = null;
                replica.RetryAtUtc = null;
                replica.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        await ClearPreviousDefaultsAsync(db, storageProvider);
        existing.IsImagingDefault = storageProvider.IsImagingDefault;
        existing.IsManagementDefault = storageProvider.IsManagementDefault;
        existing.Name = storageProvider.Name;
        existing.Type = storageProvider.Type;
        existing.Path = storageProvider.Path;
        existing.LinuxMountPath = storageProvider.LinuxMountPath;
        existing.IsEnabled = storageProvider.IsEnabled;
        existing.ManagedCredentialId = storageProvider.ManagedCredentialId;
        existing.ImagingDirectConnect = storageProvider.ImagingDirectConnect;
        existing.WinPeSmbPath = storageProvider.WinPeSmbPath;
        existing.Purposes = storageProvider.Purposes;
        existing.PreferredClientNetworks = storageProvider.PreferredClientNetworks;

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult<StorageProvider>.Ok(existing);
    }

    private static Task<bool> HasMoveAsync(CloneDeployDbContext db, int[] ids) =>
        db.Set<StorageMove>().AnyAsync(x => (ids.Contains(x.SourceId) || ids.Contains(x.DestinationId)) &&
            x.State != "Completed" && (x.State != "Failed" || x.CleanupAfterUtc != null));

    private static async Task ClearPreviousDefaultsAsync(CloneDeployDbContext db, StorageProvider provider)
    {
        if (provider.IsImagingDefault)
            await db.StorageProviders.Where(x => x.Id != provider.Id && x.IsImagingDefault).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsImagingDefault, false));
        if (provider.IsManagementDefault)
            await db.StorageProviders.Where(x => x.Id != provider.Id && x.IsManagementDefault).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsManagementDefault, false));
    }

    private async Task<bool> IsAdminAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return (await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded;
    }

    internal static Task LockAsync(CloneDeployDbContext db) =>
        db.Database.ExecuteSqlRawAsync("LOCK TABLE \"StorageProviders\" IN SHARE ROW EXCLUSIVE MODE");

    private static async Task<bool> HasContentAsync(CloneDeployDbContext db, int[] ids) =>
        await db.Set<StorageReadLease>().AnyAsync(x => ids.Contains(x.StorageProviderId) && x.ExpiresAtUtc > DateTime.UtcNow) ||
        await db.Set<SharedContentReplica>().AnyAsync(x => ids.Contains(x.StorageProviderId)) ||
        await HasNonSharedReplicaContentAsync(db, ids);

    private static async Task<bool> CanRetargetSharedRetriesAsync(CloneDeployDbContext db, int id) =>
        !await db.Set<StorageReadLease>().AnyAsync(x => x.StorageProviderId == id && x.ExpiresAtUtc > DateTime.UtcNow) &&
        !await db.Set<SharedContentReplica>().AnyAsync(x => x.StorageProviderId == id &&
            x.State != ImageReplicaState.Pending && x.State != ImageReplicaState.Failed) &&
        !await HasNonSharedReplicaContentAsync(db, [id]);

    private static async Task<bool> HasNonSharedReplicaContentAsync(CloneDeployDbContext db, int[] ids) =>
        await db.Set<StorageReadLease>().AnyAsync(x => ids.Contains(x.StorageProviderId) && !x.ContentKey.StartsWith("shared:") && x.ExpiresAtUtc > DateTime.UtcNow) ||
        await db.Set<SharedContent>().AnyAsync(x => x.SourceProviderId != null && ids.Contains(x.SourceProviderId.Value)) ||
        await db.Set<ImageReplica>().AnyAsync(x => ids.Contains(x.StorageProviderId)) ||
        await db.Set<ImageReplicationTarget>().AnyAsync(x => ids.Contains(x.StorageProviderId)) ||
        await db.ImageUploads.AnyAsync(x => ids.Contains(x.StorageProviderId)) ||
        await db.StoredFiles.AnyAsync(x => x.StorageProviderId != null && ids.Contains(x.StorageProviderId.Value)) ||
        await db.DriverFiles.AnyAsync(x => x.PreparedStorageProviderId != null && ids.Contains(x.PreparedStorageProviderId.Value)) ||
        await db.ImageVersions.AnyAsync(x => ids.Contains(x.StorageProviderId)) ||
        await db.Set<ImageContentDeletion>().AnyAsync(x => ids.Contains(x.StorageProviderId)) ||
        await db.WinPeBuilds.AnyAsync(x => x.StorageProviderId != null && ids.Contains(x.StorageProviderId.Value));

    public Task<ServiceResult> DeleteAsync(int id) => DeleteManyAsync([id]);

    public async Task<ServiceResult> DeleteManyAsync(IEnumerable<int> ids)
    {
        if (!await IsAdminAsync()) return ServiceResult.Fail("StorageMount.Forbidden");
        var selected = ids.Distinct().ToArray();
        if (selected.Length == 0) return ServiceResult.Ok();
        await using var db = await _dbFactory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await LockAsync(db);
        var providers = await db.StorageProviders.Where(x => selected.Contains(x.Id)).ToListAsync();
        if (providers.Count != selected.Length) return ServiceResult.Fail(ErrorKeys.Common.NotFound);
        if (await HasMoveAsync(db, selected))
            return ServiceResult.Fail("Storage.Validation", "Finish pending content moves and cleanup before deleting a provider.");
        await db.Set<SharedContentReplica>().Where(x => selected.Contains(x.StorageProviderId)).ExecuteDeleteAsync();
        await db.Set<ImageReplica>().Where(x => selected.Contains(x.StorageProviderId)).ExecuteDeleteAsync();
        await db.Set<ImageReplicationTarget>().Where(x => selected.Contains(x.StorageProviderId)).ExecuteDeleteAsync();
        await db.Set<ImageContentDeletion>().Where(x => selected.Contains(x.StorageProviderId)).ExecuteDeleteAsync();
        await db.Set<StorageReadLease>().Where(x => selected.Contains(x.StorageProviderId)).ExecuteDeleteAsync();
        foreach (var provider in providers)
        {
            provider.IsDeleted = true;
            provider.IsEnabled = false;
            provider.IsImagingDefault = false;
            provider.IsManagementDefault = false;
            provider.SiteServiceId = null;
            provider.ManagedCredentialId = null;
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    private async Task<ServiceResult<StorageProvider>> ValidateUniqueAsync(
        CloneDeployDbContext db,
        StorageProvider storageProvider)
    {
        if (string.IsNullOrWhiteSpace(storageProvider.Name))
            return ServiceResult<StorageProvider>.Fail("Storage.Validation", UiText.Get(LocKeys.Ui.StorageProviderAdministration.EnterAProviderName));
        if ((storageProvider.IsImagingDefault || storageProvider.IsManagementDefault) &&
            (!storageProvider.IsEnabled || storageProvider.SiteServiceId.HasValue) ||
            storageProvider.IsImagingDefault && !storageProvider.Purposes.HasFlag(StoragePurpose.Imaging) ||
            storageProvider.IsManagementDefault && !storageProvider.Purposes.HasFlag(StoragePurpose.DeviceManagement))
            return ServiceResult<StorageProvider>.Fail("Storage.Validation", "A default must be enabled, hosted by the main application, and support its selected purpose.");
        if (storageProvider.Purposes is < StoragePurpose.Imaging or > StoragePurpose.All)
            return ServiceResult<StorageProvider>.Fail("Storage.Validation", UiText.Get(LocKeys.Ui.StorageProviderAdministration.SelectImagingDeviceManagementOrBoth));
        try { storageProvider.PreferredClientNetworks = StorageClientNetworks.Normalize(storageProvider.PreferredClientNetworks); }
        catch (ArgumentException ex) { return ServiceResult<StorageProvider>.Fail("Storage.Validation", ex.Message); }
        if (storageProvider.SiteServiceId is { } siteId)
        {
            if (!await db.SiteServices.AnyAsync(x => x.Id == siteId && !x.IsWindowsWorker && x.State != SiteRegistrationState.Revoked))
                return ServiceResult<StorageProvider>.Fail("Storage.Validation", UiText.Get(LocKeys.Ui.StorageProviderAdministration.SelectARegisteredSiteServiceThatHasNotBeen));
            if (storageProvider.ImagingDirectConnect)
                return ServiceResult<StorageProvider>.Fail("Storage.Validation", UiText.Get(LocKeys.Ui.StorageProviderAdministration.SiteStorageServesClientsOverHTTPSAndDoesNot));
            if (storageProvider.IsEnabled && !await db.SiteServices.AnyAsync(x => x.Id == siteId && x.State == SiteRegistrationState.Approved))
                return ServiceResult<StorageProvider>.Fail("Storage.Validation", UiText.Get(LocKeys.Ui.StorageProviderAdministration.ApproveTheSiteServiceBeforeEnablingStorageReplication));
        }
        storageProvider.LinuxMountPath = string.IsNullOrWhiteSpace(storageProvider.LinuxMountPath) ? null : storageProvider.LinuxMountPath.Trim().TrimEnd('/');
        if (storageProvider.Type != StorageProviderType.Smb) storageProvider.LinuxMountPath = null;
        var hostOs = storageProvider.SiteServiceId is { } hostId
            ? await db.SiteServices.Where(x => x.Id == hostId).Select(x => x.HostOs).SingleAsync()
            : OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsWindows() ? "Windows" : "Other";
        if (storageProvider.Type == StorageProviderType.Smb && hostOs == "Linux" && storageProvider.LinuxMountPath is null)
            return ServiceResult<StorageProvider>.Fail("Storage.Validation", UiText.Get(LocKeys.Ui.StorageProviderAdministration.LinuxSMBStorageRequiresAnOSManagedMountPath));
        if (storageProvider.Type == StorageProviderType.Smb && hostOs == "Windows" && storageProvider.LinuxMountPath is not null)
            return ServiceResult<StorageProvider>.Fail("Storage.Validation", UiText.Get(LocKeys.Ui.StorageProviderAdministration.ALinuxMountPathCannotBeUsedOnA));
        var target = paths.ValidateTargetInput(storageProvider);
        if (target.Error is not null)
            return ServiceResult<StorageProvider>.Fail("Storage.Validation", target.Error);
        storageProvider.Name = storageProvider.Name.Trim();
        storageProvider.Path = target.Path!;
        if (!storageProvider.ImagingDirectConnect || string.IsNullOrWhiteSpace(storageProvider.WinPeSmbPath))
            storageProvider.WinPeSmbPath = null;
        else
        {
            var clientPath = StorageMountPaths.ValidateWinPeSmbPath(storageProvider.WinPeSmbPath);
            if (clientPath.Error is not null)
                return ServiceResult<StorageProvider>.Fail("Storage.Validation", clientPath.Error);
            storageProvider.WinPeSmbPath = clientPath.Path;
        }
        if (storageProvider.ImagingDirectConnect && storageProvider.Type != CloneDeploy_Server.Backend.Enum.StorageProviderType.Smb)
            return ServiceResult<StorageProvider>.Fail("Storage.Validation", UiText.Get(LocKeys.Ui.StorageProviderAdministration.ImagingDirectConnectRequiresAnSMBProvider));
        if (storageProvider.ImagingDirectConnect && !storageProvider.Purposes.HasFlag(StoragePurpose.Imaging))
            return ServiceResult<StorageProvider>.Fail("Storage.Validation", UiText.Get(LocKeys.Ui.StorageProviderAdministration.ImagingDirectConnectRequiresTheImagingPurpose));
        if (storageProvider.ManagedCredentialId.HasValue &&
            !await db.ManagedCredentials.AnyAsync(x => x.Id == storageProvider.ManagedCredentialId.Value))
            return ServiceResult<StorageProvider>.Fail(ErrorKeys.Common.NotFound, storageProvider.ManagedCredentialId.Value);
        if (await db.StorageProviders.AnyAsync(x => x.Id != storageProvider.Id && x.Name == storageProvider.Name))
            return ServiceResult<StorageProvider>.Fail(ErrorKeys.Common.AlreadyExists, storageProvider.Name);

        if (await db.StorageProviders.AnyAsync(x => x.Id != storageProvider.Id && x.SiteServiceId == storageProvider.SiteServiceId && x.Path.ToLower() == storageProvider.Path.ToLower()))
            return ServiceResult<StorageProvider>.Fail(ErrorKeys.Common.AlreadyExists, storageProvider.Path);

        return ServiceResult<StorageProvider>.Ok(storageProvider);
    }
}
