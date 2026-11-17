using System.Security.Claims;
using System.Security.Cryptography;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Services.Files;
using CloneDeploy_Server.Localization;

namespace CloneDeploy_Server.Backend.Services;


/// <summary>
/// Manages action file uploads, external downloads, and refresh schedules.
/// </summary>
public sealed class ActionFileService(
    ICloneDeployDbFactory factory,
    StoredFileStorage storage,
    ChunkedUploadStore uploads,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization,
    ILogger<ActionFileService> logger)
{
    public async Task<ServiceResult<List<ActionFileItemDto>>> GetAsync(Guid action)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyView);
        if (user is null)
            return ServiceResult<List<ActionFileItemDto>>.Fail(ErrorKeys.Action.Forbidden);

        await using var db = await factory.CreateContextAsync();
        var files = await OwnedFiles(db, action).AsNoTracking().OrderBy(x => x.Name).ToListAsync();
        var items = files.Select(x =>
        {
            var size = storage.NamedSize(x);
            var status = x.Status == StoredFileStatus.Complete ? (size.HasValue ? "Complete" : "Missing") : x.Status.ToString();
            return new ActionFileItemDto(x.Id, x.SourceType == FileSourceType.Url, x.Name, x.Url, size,
                    status, x.Progress, x.ErrorMessage, x.Sha256Hash, x.AvailableAtUtc, x.ExpectedSha256Hash, IsBusy(x.Status))
                { AutoRefresh = x.AutoRefresh, LastCheckedAtUtc = x.LastCheckedAtUtc };
        }).ToList();
        return ServiceResult<List<ActionFileItemDto>>.Ok(items);
    }

    public static bool IsBusy(StoredFileStatus status) => status is StoredFileStatus.Queued
        or StoredFileStatus.Downloading or StoredFileStatus.CalculatingSha256 or StoredFileStatus.CalculatingMd5;

    public async Task<ServiceResult<int?>> GetRefreshScheduleAsync(Guid action)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyView);
        if (user is null)
            return ServiceResult<int?>.Fail(ErrorKeys.Action.Forbidden);

        await using var db = await factory.CreateContextAsync();
        var scheduleId = await db.ActionReferences.Where(x => x.ActionGuid == action)
            .Select(x => x.ExternalFileUpdateScheduleId).SingleOrDefaultAsync();
        return ServiceResult<int?>.Ok(scheduleId);
    }

    public async Task<ServiceResult> SaveRefreshScheduleAsync(Guid action, int? scheduleId)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyEdit);
        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Action.Forbidden);

        await storage.Gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            var editable = await ActionEditGuard.CheckAsync(db, action);
            if (!editable.Success) return editable;
            if (scheduleId.HasValue)
            {
                var schedule = await db.Schedules.FindAsync(scheduleId.Value);
                if (schedule is null || !new ScheduleCalculator().IsValid(schedule))
                    return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.ActionFileService.ChooseAValidSchedule));
            }

            var reference = await db.ActionReferences.SingleOrDefaultAsync(x => x.ActionGuid == action);
            if (reference is null)
                return ServiceResult.Fail(ErrorKeys.Common.NotFound, action);
            if (reference.ExternalFileUpdateScheduleId != scheduleId)
            {
                reference.ExternalFileUpdateScheduleId = scheduleId;
                reference.ExternalFilesLastEvaluatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
        }
        finally
        {
            storage.Gate.Release();
        }

        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> AttachUploadAsync(Guid action, Guid uploadId)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyEdit);
        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Action.Forbidden);

        var owner = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(owner))
            return ServiceResult.Fail(ErrorKeys.Action.Forbidden);
        try
        {
            return await uploads.AttachAsync(owner, uploadId, async (source, name) =>
            {
                try
                {
                    name = StoredFileStorage.ValidateName(name);
                }
                catch (InvalidOperationException ex)
                {
                    return ServiceResult.Fail(ErrorKeys.Action.Validation, ex.Message);
                }

                string hash;
                await using (var stream = File.OpenRead(source))
                    hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
                await storage.Gate.WaitAsync();
                try
                {
                    await using var db = await factory.CreateContextAsync();
                    await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                    var editable = await ActionEditGuard.CheckAsync(db, action);
                    if (!editable.Success) return editable;
                    var actionResult = await EnsureActionAsync(db, action);
                    if (!actionResult.Success)
                        return actionResult;
                    if (await OwnedFiles(db, action).AnyAsync(x => x.Name == name))
                        return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.ActionFileService.AFileWithThisNameAlreadyExistsRemoveIt));
                    var file = new StoredFile
                    {
                        StorageProviderId = await storage.SelectProviderAsync(), DirectoryGuid = action, Name = name, Sha256Hash = hash,
                        AvailableAtUtc = DateTime.UtcNow, SourceType = FileSourceType.Upload,
                        Status = StoredFileStatus.Complete, Size = new FileInfo(source).Length
                    };
                    db.ActionFiles.Add(new ActionFile { ActionGuid = action, File = file });
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
        catch (UploadException ex)
        {
            return ServiceResult.Fail(ErrorKeys.Action.Validation, ex.Message);
        }
    }

    public async Task<ServiceResult> SaveExternalAsync(Guid action, int? id, string name, string url, string? expectedHash, bool autoRefresh)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyEdit);
        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Action.Forbidden);

        try
        {
            name = StoredFileStorage.ValidateExternalName(name);
            url = StoredFileStorage.ValidateUrl(url);
            expectedHash = StoredFileStorage.ValidateHash(expectedHash);
        }
        catch (InvalidOperationException ex)
        {
            return ServiceResult.Fail(ErrorKeys.Action.Validation, ex.Message);
        }

        await storage.Gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            var editable = await ActionEditGuard.CheckAsync(db, action);
            if (!editable.Success) return editable;
            var actionResult = await EnsureActionAsync(db, action);
            if (!actionResult.Success)
                return actionResult;
            if (await OwnedFiles(db, action).AnyAsync(x => x.Name == name && x.Id != id))
                return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.ActionFileService.AFileWithThisNameAlreadyExistsRemoveIt));
            StoredFile file;
            if (id.HasValue)
            {
                var existing = await OwnedFiles(db, action).SingleOrDefaultAsync(x => x.Id == id && x.SourceType == FileSourceType.Url);
                if (existing is null)
                    return ServiceResult.Fail(ErrorKeys.Common.NotFound, id.Value);

                file = existing;
                if (IsBusy(file.Status))
                    return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.ActionFileService.CancelTheDownloadBeforeEditingIt));
            }
            else
            {
                file = new StoredFile { StorageProviderId = await storage.SelectProviderAsync(), DirectoryGuid = action, Name = name, Url = url, SourceType = FileSourceType.Url };
                db.ActionFiles.Add(new ActionFile { ActionGuid = action, File = file });
            }

            var oldPath = storage.NamedPath(file);
            var newPath = storage.NamedPath(file, name);
            if ((!id.HasValue || !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) && File.Exists(newPath))
                return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.ActionFileService.AFileAlreadyExistsAtTheDestination));
            var rename = id.HasValue && oldPath != newPath && File.Exists(oldPath);
            if (rename) File.Move(oldPath, newPath);
            file.Name = name;
            file.Url = url;
            file.ExpectedSha256Hash = expectedHash;
            file.AutoRefresh = autoRefresh;
            file.Status = StoredFileStatus.Queued;
            file.Progress = null;
            file.ErrorMessage = null;
            try
            {
                await db.SaveChangesAsync();
                await db.SoftwareActions.Where(x => x.InstallerFileId == file.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Command, name));
                await transaction.CommitAsync();
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

    public async Task<ServiceResult> QueueAsync(Guid action, int id)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyEdit);
        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Action.Forbidden);

        await storage.Gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateContextAsync();
            var file = await OwnedFiles(db, action).SingleOrDefaultAsync(x => x.Id == id && x.SourceType == FileSourceType.Url);
            if (file is null)
                return ServiceResult.Fail(ErrorKeys.Common.NotFound, id);
            if (IsBusy(file.Status))
                return ServiceResult.Ok();
            file.Status = StoredFileStatus.Queued;
            file.Progress = null;
            file.ErrorMessage = null;
            await db.SaveChangesAsync();
        }
        finally
        {
            storage.Gate.Release();
        }

        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> CancelAsync(Guid action, int id)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyEdit);
        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Action.Forbidden);

        await storage.Gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateContextAsync();
            await OwnedFiles(db, action).Where(x => x.Id == id && x.SourceType == FileSourceType.Url &&
                                                    (x.Status == StoredFileStatus.Queued || x.Status == StoredFileStatus.Downloading))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, StoredFileStatus.Error)
                    .SetProperty(x => x.ErrorMessage, "Download canceled.").SetProperty(x => x.Progress, (string?)null));
        }
        finally
        {
            storage.Gate.Release();
        }

        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> DeleteAsync(Guid action, int id)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyEdit);
        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Action.Forbidden);

        await storage.Gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            var editable = await ActionEditGuard.CheckAsync(db, action);
            if (!editable.Success) return editable;
            var file = await OwnedFiles(db, action).SingleOrDefaultAsync(x => x.Id == id);
            if (file is null)
                return ServiceResult.Ok();
            await DeviceTaskQueue.LockAsync(db);
            if (await db.DeviceTasks.AnyAsync(x => x.Deployment.ActionGuid == action &&
                                                   (x.Status == DeviceTaskStatus.Queued || x.Status == DeviceTaskStatus.Offered || x.Status == DeviceTaskStatus.Running || x.Status == DeviceTaskStatus.Unknown)))
                return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.ActionFileService.ThisActionHasUnfinishedDeviceTasksCancelOrFinish));
            await db.ActionFiles.Where(x => x.ActionGuid == action && x.StoredFileId == id).ExecuteDeleteAsync();
            await db.SoftwareActions.Where(x => x.Guid == action && x.InstallerFileId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.InstallerFileId, (int?)null));
            var stillLinked = await db.ActionFiles.AnyAsync(x => x.StoredFileId == id);
            if (!stillLinked) await db.StoredFiles.Where(x => x.Id == id).ExecuteDeleteAsync();
            await transaction.CommitAsync();
            if (!stillLinked) TryDelete(storage.NamedPath(file));
        }
        finally
        {
            storage.Gate.Release();
        }

        return ServiceResult.Ok();
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not remove unused action file {Path}", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Could not remove unused action file {Path}", path);
        }
    }

    private static IQueryable<StoredFile> OwnedFiles(CloneDeployDbContext db, Guid action) =>
        db.ActionFiles.Where(x => x.ActionGuid == action).Select(x => x.File);

    private static async Task<ServiceResult> EnsureActionAsync(CloneDeployDbContext db, Guid action)
    {
        if (await db.ActionReferences.AnyAsync(x => x.ActionGuid == action))
            return ServiceResult.Ok();
        if (!await db.SoftwareActions.AnyAsync(x => x.Guid == action))
            return ServiceResult.Fail(ErrorKeys.Common.NotFound, action);
        db.ActionReferences.Add(new ActionReference { ActionGuid = action, ActionType = ActionType.Software });
        await db.SaveChangesAsync();
        return ServiceResult.Ok();
    }
}