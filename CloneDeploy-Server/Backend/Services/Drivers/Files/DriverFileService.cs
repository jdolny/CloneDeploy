using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Localization;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Services.Files;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Manages driver uploads, vendor downloads, and preparation requests.
/// </summary>
public sealed class DriverFileService(
    ICloneDeployDbFactory factory,
    StoredFileStorage storage,
    ChunkedUploadStore uploads,
    DriverCatalogClient catalogs,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization,
    ILogger<DriverFileService> logger)
{
    public async Task<ServiceResult<List<DriverCatalogModelDto>>> GetCatalogModelsAsync(DriverVendor vendor, bool refresh = false)
    {
        try
        {
            var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DriverPackView);
            if (user is null)
                return ServiceResult<List<DriverCatalogModelDto>>.Fail(ErrorKeys.Driver.Forbidden);
            return ServiceResult<List<DriverCatalogModelDto>>.Ok(await catalogs.GetModelsAsync(vendor, refresh));
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "Driver catalog request failed");
            return ServiceResult<List<DriverCatalogModelDto>>.Fail(ErrorKeys.Driver.Validation, exception.Message);
        }
    }

    public async Task<ServiceResult<List<DriverCatalogPackageDto>>> GetCatalogPackagesAsync(DriverVendor vendor, DriverCatalogModelDto model, bool refresh = false)
    {
        try
        {
            var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DriverPackView);
            if (user is null)
                return ServiceResult<List<DriverCatalogPackageDto>>.Fail(ErrorKeys.Driver.Forbidden);
            return ServiceResult<List<DriverCatalogPackageDto>>.Ok(await catalogs.GetPackagesAsync(vendor, model.Name, model.Source, refresh));
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "Driver catalog request failed");
            return ServiceResult<List<DriverCatalogPackageDto>>.Fail(ErrorKeys.Driver.Validation, exception.Message);
        }
    }

    public async Task<ServiceResult<List<DriverFileItemDto>>> GetAsync(int profileId)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DriverPackView);
        if (user is null)
            return ServiceResult<List<DriverFileItemDto>>.Fail(ErrorKeys.Driver.Forbidden);
        await using var db = await factory.CreateContextAsync();
        var rows = await db.DriverFiles.AsNoTracking().Include(x => x.File).Where(x => x.DriverProfileId == profileId).OrderBy(x => x.File.Name).ToListAsync();
        var result = rows.Select(x =>
        {
            var file = x.File;
            var size = storage.NamedSize(file);
            var status = file.Status == StoredFileStatus.Complete && !size.HasValue ? "Missing" : file.Status.ToString();
            return new DriverFileItemDto(new ActionFileItemDto(file.Id, file.SourceType == FileSourceType.Url, file.Name, file.Url, size,
                status, file.Progress, file.ErrorMessage, file.Sha256Hash, file.AvailableAtUtc, file.ExpectedSha256Hash, ActionFileService.IsBusy(file.Status))
            {
                AutoRefresh = file.AutoRefresh,
                LastCheckedAtUtc = file.LastCheckedAtUtc
            }, x.CatalogVendor, x.CatalogModel, x.CatalogOs, x.CatalogRelease, x.FollowLatest)
            {
                PreparationStatus = file.Status == StoredFileStatus.Complete && x.AttemptedSourceHash != file.Sha256Hash ? DriverPreparationStatus.Queued : x.PreparationStatus,
                PreparationError = x.PreparationError,
                HasPreparedArchive = x.PreparedArchiveId.HasValue,
                PreparedIsCurrent = x.PreparedSourceHash == file.Sha256Hash && file.Status == StoredFileStatus.Complete && x.PreparationStatus == DriverPreparationStatus.Ready,
                InfCount = x.PreparedInfCount,
                PreparedAtUtc = x.PreparedAtUtc
            };
        }).ToList();
        return ServiceResult<List<DriverFileItemDto>>.Ok(result);
    }

    public async Task<ServiceResult> ImportAsync(int profileId, DriverCatalogPackageDto selection, bool followLatest)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DriverPackEdit);
        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Driver.Forbidden);
        List<DriverCatalogPackageDto> candidates;
        try
        {
            candidates = await catalogs.GetPackagesAsync(selection.Vendor, selection.Model, selection.Source);
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "Driver catalog selection rejected");
            return ServiceResult.Fail(ErrorKeys.Driver.Validation, exception.Message);
        }
        var package = candidates.SingleOrDefault(x => x.Key == selection.Key);
        if (package is null)
            return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverFileService.TheSelectedPackageIsNoLongerInTheCatalog));
        return await SaveExternalCoreAsync(user, profileId, null, package.FileName, package.Url, package.Sha256, false, package, followLatest);
    }

    public async Task<ServiceResult> SaveExternalAsync(int profileId, int? fileId, string name, string url, string? expectedHash, bool autoRefresh)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DriverPackEdit);
        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Driver.Forbidden);
        return await SaveExternalCoreAsync(user, profileId, fileId, name, url, expectedHash, autoRefresh, null, false);
    }

    public async Task<ServiceResult> AttachUploadAsync(int profileId, Guid uploadId)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DriverPackEdit);
        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Driver.Forbidden);
        var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        return await uploads.AttachAsync(owner, uploadId, async (source, name) =>
        {
            try
            {
                name = StoredFileStorage.ValidateName(name);
            }
            catch (InvalidOperationException exception)
            {
                return ServiceResult.Fail(ErrorKeys.Driver.Validation, exception.Message);
            }
            string hash;
            await using (var stream = File.OpenRead(source)) hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
            await storage.Gate.WaitAsync();
            try
            {
                await using var db = await factory.CreateContextAsync();
                var profile = await db.DriverProfiles.SingleOrDefaultAsync(x => x.Id == profileId);
                if (profile is null)
                    return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverFileService.DriverProfileNotFound));
                if (await Owned(db, profileId).AnyAsync(x => x.Name == name))
                    return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverFileService.AFileWithThisNameAlreadyExistsRemoveIt));
                await using var transaction = await db.Database.BeginTransactionAsync();
                var file = new StoredFile
                {
                    StorageProviderId = await storage.SelectProviderAsync(CloneDeploy_Server.Backend.Enum.StoragePurpose.Imaging),
                    DirectoryGuid = profile.DirectoryGuid,
                    Name = name,
                    SourceType = FileSourceType.Upload,
                    Status = StoredFileStatus.Complete,
                    Sha256Hash = hash,
                    Size = new FileInfo(source).Length,
                    AvailableAtUtc = DateTime.UtcNow
                };
                db.DriverFiles.Add(new DriverFile
                {
                    DriverProfileId = profileId,
                    File = file
                });
                Audit(db, user, profileId, "Upload", name);
                await db.SaveChangesAsync();
                var destination = storage.NamedPath(file);
                using var publication = await FilePublication.CreateAsync(source, destination);
                await transaction.CommitAsync();
                publication.Commit();
                return ServiceResult.Ok();
            }
            finally
            {
                storage.Gate.Release();
            }
        });
    }

    public async Task<ServiceResult> SetFollowLatestAsync(int profileId, int fileId, bool followLatest)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DriverPackEdit);
        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Driver.Forbidden);
        await storage.Gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateContextAsync();
            var link = await db.DriverFiles.SingleOrDefaultAsync(x => x.DriverProfileId == profileId && x.StoredFileId == fileId);
            if (link is null)
                return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverFileService.DriverFileNotFound));
            if (link.CatalogVendor is null)
                return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverFileService.OnlyVendorImportsCanFollowCatalogReleases));
            link.FollowLatest = followLatest;
            Audit(db, user, profileId, "Vendor updates", new { fileId, followLatest });
            await db.SaveChangesAsync();
        }
        finally
        {
            storage.Gate.Release();
        }

        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> RetryPreparationAsync(int profileId, int fileId)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DriverPackEdit);
        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Driver.Forbidden);
        await storage.Gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateContextAsync();
            var link = await db.DriverFiles.Include(x => x.File).SingleOrDefaultAsync(x => x.DriverProfileId == profileId && x.StoredFileId == fileId);
            if (link is null)
                return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverFileService.DriverFileNotFound));
            if (link.File.Status != StoredFileStatus.Complete)
                return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverFileService.WaitForTheSourceDownloadToCompleteBeforePreparing));
            if (link.PreparationStatus == DriverPreparationStatus.Extracting)
                return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverFileService.ThisPackageIsAlreadyBeingExtracted));
            link.PreparationStatus = DriverPreparationStatus.Queued;
            link.PreparationError = null;
            Audit(db, user, profileId, "Prepare drivers", link.File.Name);
            await db.SaveChangesAsync();
        }
        finally
        {
            storage.Gate.Release();
        }

        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> QueueAsync(int profileId, int fileId)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DriverPackEdit);
        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Driver.Forbidden);
        await storage.Gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateContextAsync();
            var file = await Owned(db, profileId).SingleOrDefaultAsync(x => x.Id == fileId && x.SourceType == FileSourceType.Url);
            if (file is null)
                return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverFileService.ExternalFileNotFound));
            if (!ActionFileService.IsBusy(file.Status))
            {
                file.Status = StoredFileStatus.Queued;
                file.ErrorMessage = null;
                file.Progress = null;
                Audit(db, user, profileId, "Download", file.Name);
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            storage.Gate.Release();
        }

        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> CancelAsync(int profileId, int fileId)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DriverPackEdit);
        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Driver.Forbidden);
        await storage.Gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateContextAsync();
            var file = await Owned(db, profileId).SingleOrDefaultAsync(x => x.Id == fileId);
            if (file is null)
                return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverFileService.FileNotFound));
            if (file.SourceType == FileSourceType.Url && ActionFileService.IsBusy(file.Status))
            {
                file.Status = StoredFileStatus.Error;
                file.ErrorMessage = "Download cancelled.";
                file.Progress = null;
                Audit(db, user, profileId, "Cancel download", file.Name);
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            storage.Gate.Release();
        }

        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> DeleteAsync(int profileId, int fileId)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DriverPackEdit);
        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Driver.Forbidden);
        await storage.Gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateContextAsync();
            var link = await db.DriverFiles.Include(x => x.File).SingleOrDefaultAsync(x => x.DriverProfileId == profileId && x.StoredFileId == fileId);
            if (link is null)
                return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverFileService.FileNotFound));
            if (link.PreparationStatus == DriverPreparationStatus.Extracting)
                return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverFileService.WaitForDriverPreparationToFinishBeforeDeletingThis));
            if (ActionFileService.IsBusy(link.File.Status))
                return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverFileService.CancelTheDownloadBeforeDeletingThisFile));
            var path = storage.NamedPath(link.File);
            Audit(db, user, profileId, "Delete file", link.File.Name);
            db.DriverFiles.Remove(link);
            db.StoredFiles.Remove(link.File);
            await db.SaveChangesAsync();
            try
            {
                File.Delete(path);
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Could not remove unused driver file {Path}", path);
            }
            catch (UnauthorizedAccessException exception)
            {
                logger.LogWarning(exception, "Could not remove unused driver file {Path}", path);
            }
        }
        finally
        {
            storage.Gate.Release();
        }

        return ServiceResult.Ok();
    }

    private async Task<ServiceResult> SaveExternalCoreAsync(ClaimsPrincipal user, int profileId, int? id, string name, string url, string? hash,
        bool autoRefresh, DriverCatalogPackageDto? package, bool followLatest)
    {
        try
        {
            name = StoredFileStorage.ValidateExternalName(name);
            url = StoredFileStorage.ValidateUrl(url);
            hash = StoredFileStorage.ValidateHash(hash);
        }
        catch (InvalidOperationException exception)
        {
            return ServiceResult.Fail(ErrorKeys.Driver.Validation, exception.Message);
        }
        await storage.Gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateContextAsync();
            var profile = await db.DriverProfiles.SingleOrDefaultAsync(x => x.Id == profileId);
            if (profile is null)
                return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverFileService.DriverProfileNotFound));
            if (await Owned(db, profileId).AnyAsync(x => x.Name == name && (!id.HasValue || x.Id != id)))
                return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverFileService.AFileWithThisNameAlreadyExistsRemoveIt));
            var link = id.HasValue ? await db.DriverFiles.Include(x => x.File).SingleOrDefaultAsync(x => x.DriverProfileId == profileId && x.StoredFileId == id) : null;
            if (id.HasValue && (link is null || link.File.SourceType != FileSourceType.Url))
                return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverFileService.ExternalFileNotFound));
            if (link is not null && ActionFileService.IsBusy(link.File.Status))
                return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.ActionFileService.CancelTheDownloadBeforeEditingIt));
            if (link is null)
            {
                link = new DriverFile
                {
                    DriverProfileId = profileId,
                    File = new StoredFile
                    {
                        StorageProviderId = await storage.SelectProviderAsync(CloneDeploy_Server.Backend.Enum.StoragePurpose.Imaging),
                        DirectoryGuid = profile.DirectoryGuid,
                        Name = name,
                        SourceType = FileSourceType.Url
                    }
                };
                db.DriverFiles.Add(link);
            }

            var file = link.File;
            var oldPath = storage.NamedPath(file);
            var newPath = storage.NamedPath(file, name);
            if ((!id.HasValue || !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) && File.Exists(newPath))
                return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverFileService.AFileWithThisNameAlreadyExists));
            var rename = id.HasValue && oldPath != newPath && File.Exists(oldPath);
            file.Name = name;
            file.Url = url;
            file.ExpectedSha256Hash = hash;
            file.AutoRefresh = autoRefresh;
            file.Status = StoredFileStatus.Queued;
            file.Progress = null;
            file.ErrorMessage = null;
            link.CatalogVendor = package?.Vendor;
            link.CatalogModel = package?.Model;
            link.CatalogOs = package?.OperatingSystem;
            link.CatalogArchitecture = package?.Architecture;
            link.CatalogRelease = package?.Release;
            link.CatalogSource = package?.Source;
            link.FollowLatest = followLatest;
            Audit(db, user, profileId, "Save external file", new { name, url, hash, autoRefresh, followLatest, link.CatalogVendor });
            if (rename) File.Move(oldPath, newPath);
            try
            {
                await db.SaveChangesAsync();
            }
            catch
            {
                if (rename) File.Move(newPath, oldPath);
                throw;
            }
        }
        finally
        {
            storage.Gate.Release();
        }

        return ServiceResult.Ok();
    }

    private static IQueryable<StoredFile> Owned(CloneDeployDbContext db, int id) => db.DriverFiles.Where(x => x.DriverProfileId == id).Select(x => x.File);

    private static void Audit(CloneDeployDbContext db, ClaimsPrincipal user, int id, string operation, object detail) =>
        db.AuditLogs.Add(new AuditLog
        {
            AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
            UserName = user.Identity!.Name!,
            AuditType = AuditType.Update,
            ObjectType = nameof(DriverPack),
            ObjectId = id,
            ObjectName = "Driver profile files",
            ObjectJson = JsonSerializer.Serialize(new { operation, detail })
        });

}
