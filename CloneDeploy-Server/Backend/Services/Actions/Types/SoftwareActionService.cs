using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Localization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Manages saved instructions for installing or removing software on devices.
/// </summary>
public class SoftwareActionService(ICloneDeployDbFactory dbFactory)
{
    public async Task<List<SoftwareAction>> GetAsync(string? searchText = null, bool includeArchived = false)
    {
        await using var db = await dbFactory.CreateContextAsync();

        var query = db.SoftwareActions.Include(t => t.ActionReference!).ThenInclude(x => x.Tags).AsNoTracking();
        if (!includeArchived)
            query = query.Where(t => t.ActionReference!.ArchivedAtUtc == null);

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            searchText = searchText.Trim();
            query = query.Where(t =>
                EF.Functions.ILike(t.Name, $"%{searchText}%") ||
                (t.Description != null && EF.Functions.ILike(t.Description, $"%{searchText}%")));
        }

        return await query.OrderBy(t => t.Name).ToListAsync();
    }

    public virtual async Task<ServiceResult<SoftwareAction>> GetByIdAsync(int id)
    {
        await using var db = await dbFactory.CreateContextAsync();
        var entity = await db.SoftwareActions
            .Include(x => x.ActionReference!).ThenInclude(x => x.Tags)
            .SingleOrDefaultAsync(x => x.Id == id);

        return entity is null
            ? ServiceResult<SoftwareAction>.Fail(ErrorKeys.Common.NotFound, id)
            : ServiceResult<SoftwareAction>.Ok(entity);
    }

    public async Task<ServiceResult<SoftwareAction>> CreateAsync(SoftwareAction softwareAction)
    {
        if (string.IsNullOrWhiteSpace(softwareAction.Name))
            return ServiceResult<SoftwareAction>.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.CustomAttribute.Validation));

        softwareAction.Name = softwareAction.Name.Trim();
        await using var db = await dbFactory.CreateContextAsync();

        if (await db.SoftwareActions.AnyAsync(x => x.Name == softwareAction.Name))
            return ServiceResult<SoftwareAction>.Fail(ErrorKeys.Common.AlreadyExists, softwareAction.Name);

        db.SoftwareActions.Add(softwareAction);
        await db.SaveChangesAsync();
        return ServiceResult<SoftwareAction>.Ok(softwareAction);
    }

    public async Task<List<SoftwareUninstallChoice>> SearchInventoryAsync(string? search)
    {
        await using var db = await dbFactory.CreateContextAsync();
        var query = db.DeviceSoftware.AsNoTracking().Where(x => x.Device!.ArchivedAtUtc == null && x.Software != null && x.Software.Name != null);
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => EF.Functions.ILike(x.Software!.Name!, $"%{search}%"));
        return await query.GroupBy(x => new { x.Software!.Name, x.Software.Version })
            .OrderBy(x => x.Key.Name).ThenBy(x => x.Key.Version).Take(100)
            .Select(x => new SoftwareUninstallChoice(x.Key.Name!, x.Key.Version, x.Select(y => y.DeviceId).Distinct().Count()))
            .ToListAsync();
    }

    public async Task<ServiceResult> UpdateAsync(SoftwareAction action)
    {
        await using var db = await dbFactory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var editable = await ActionEditGuard.CheckAsync(db, action.Guid);
        if (!editable.Success) return editable;
        var entity = await db.SoftwareActions.SingleOrDefaultAsync(x => x.Id == action.Id && x.Guid == action.Guid);
        if (entity is null)
            return ServiceResult.Fail(ErrorKeys.Common.NotFound, action.Id);

        if (action.Type == SoftwareActionType.Pkg && (action.Uninstall || action.ManagedCredentialId.HasValue ||
            !string.IsNullOrWhiteSpace(action.Arguments) || !string.IsNullOrWhiteSpace(action.AdditionalArguments)))
            return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.SoftwareActionService.PKGInstallsRunAsRootOnMacOSWithStandard));
        if (action.Uninstall)
        {
            if (string.IsNullOrWhiteSpace(action.UninstallName) || action.UninstallName.Length > 1000)
                return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.SoftwareActionService.SelectAnApplicationFromInventory));
            action.Type = SoftwareActionType.Exe;
            action.MsiInstallType = MsiInstallType.Install;
            action.WinGetInstallType = WinGetInstallType.Install;
        }
        else if (action.MsiInstallType == MsiInstallType.Uninstall || action.WinGetInstallType == WinGetInstallType.Uninstall)
            return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.SoftwareActionService.UseTheUninstallOperationAndSelectAnApplicationFrom));
        var managedUpdate = action.Type == SoftwareActionType.WinGet && action.WinGetInstallType == WinGetInstallType.UpdateManaged;
        if (action.Timeout < 0)
            return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.SoftwareActionService.TimeoutCannotBeNegative));

        if (!managedUpdate && (string.IsNullOrWhiteSpace(action.SuccessCodes) || !action.SuccessCodes.Split(',').All(x => int.TryParse(x.Trim(), out _))))
            return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.SoftwareActionService.EnterCommaSeparatedIntegerSuccessExitCodes));

        if (action.Type == SoftwareActionType.WinGet && action.WinGetInstallType is WinGetInstallType.Install or WinGetInstallType.Uninstall or WinGetInstallType.Update && string.IsNullOrWhiteSpace(action.PackageId))
            return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.SoftwareActionService.EnterAPackageID));

        if (!managedUpdate && action.ManagedCredentialId.HasValue && !await db.ManagedCredentials.AnyAsync(x => x.Id == action.ManagedCredentialId))
            return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.SoftwareActionService.TheSelectedCredentialNoLongerExists));

        StoredFile? installer = null;
        if (!action.Uninstall && action.Type != SoftwareActionType.WinGet)
        {
            installer = await db.ActionFiles.Where(x => x.ActionGuid == entity.Guid && x.StoredFileId == action.InstallerFileId)
                .Select(x => x.File).SingleOrDefaultAsync();
            if (installer is null || !SoftwareInstallerSelection.Matches(action, installer.Name))
                return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.SoftwareActionService.ChooseAnAttachedFileMatchingTheInstallerType));
        }

        if (!System.Enum.IsDefined(action.WinGetInstallType))
            return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.SoftwareActionService.ChooseAValidWinGetOperation));
        if (!action.Uninstall && action.Type == SoftwareActionType.WinGet && !managedUpdate)
        {
            try { WinGetScopeOptions.Resolve(action.WinGetScope, action.Arguments, action.AdditionalArguments); }
            catch (InvalidDataException ex) { return ServiceResult.Fail(ErrorKeys.Action.Validation, ex.Message); }
        }
        entity.Uninstall = action.Uninstall;
        entity.UninstallName = action.Uninstall ? action.UninstallName : null;
        entity.UninstallVersion = action.Uninstall ? action.UninstallVersion : null;
        entity.UseDownloadQueue = !action.Uninstall && action.UseDownloadQueue;
        entity.Type = action.Type;
        entity.Command = action.Uninstall ? null : installer?.Name ?? action.Command;
        entity.InstallerFileId = installer?.Id;
        entity.Arguments = action.Arguments;
        entity.AdditionalArguments = action.AdditionalArguments;
        entity.MsiInstallType = action.MsiInstallType;
        entity.PackageId = action.PackageId;
        entity.PackageVersion = action.PackageVersion;
        entity.WinGetInstallType = action.WinGetInstallType;
        entity.WinGetScope = managedUpdate ? WinGetScope.Default : action.WinGetScope;
        entity.Override = action.Override;
        entity.InstallLatest = action.InstallLatest;
        entity.KeepUpdated = action.KeepUpdated;
        entity.Timeout = action.Timeout;
        entity.SuccessCodes = managedUpdate ? "0,3010" : action.SuccessCodes;
        entity.ManagedCredentialId = managedUpdate ? null : action.ManagedCredentialId;
        entity.RedirectStdOut = action.RedirectStdOut;
        entity.RedirectStdError = action.RedirectStdError;

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        action.Command = entity.Command;
        action.InstallerFileId = entity.InstallerFileId;
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> UpdateGeneralAsync(SoftwareAction action)
    {
        if (string.IsNullOrWhiteSpace(action.Name))
            return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.CustomAttribute.Validation));

        await using var db = await dbFactory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var editable = await ActionEditGuard.CheckAsync(db, action.Guid);
        if (!editable.Success) return editable;
        var entity = await db.SoftwareActions.Include(x => x.ActionReference!).ThenInclude(x => x.Tags)
            .SingleOrDefaultAsync(x => x.Id == action.Id && x.Guid == action.Guid);
        if (entity is null)
            return ServiceResult.Fail(ErrorKeys.Common.NotFound, action.Id);

        var name = action.Name.Trim();
        if (await db.SoftwareActions.AnyAsync(x => x.Id != action.Id && x.Name.ToLower() == name.ToLower()))
            return ServiceResult.Fail(ErrorKeys.Common.AlreadyExists, name);

        var tagIds = action.ActionReference?.Tags.Select(x => x.TagId).ToHashSet() ?? [];
        if (await db.Tags.CountAsync(x => tagIds.Contains(x.Id)) != tagIds.Count)
            return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.CommandActionService.OneOrMoreTagsNoLongerExistReloadThe));

        entity.Name = name;
        entity.Description = action.Description;
        entity.ActionReference ??= new ActionReference { ActionGuid = entity.Guid, ActionType = ActionType.Software };
        db.ActionTags.RemoveRange(entity.ActionReference.Tags.Where(x => !tagIds.Contains(x.TagId)));
        var existing = entity.ActionReference.Tags.Select(x => x.TagId).ToHashSet();
        foreach (var id in tagIds.Except(existing))
            entity.ActionReference.Tags.Add(new ActionTag { ActionGuid = entity.Guid, TagId = id });

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        action.Name = name;
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<List<ManagedCredential>>> GetCredentialsAsync()
    {
        await using var db = await dbFactory.CreateContextAsync();
        return ServiceResult<List<ManagedCredential>>.Ok(await db.ManagedCredentials.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new ManagedCredential { Id = x.Id, Name = x.Name, Username = x.Username }).ToListAsync());
    }

    public async Task<ServiceResult<List<Tag>>> GetTagsAsync()
    {
        await using var db = await dbFactory.CreateContextAsync();
        return ServiceResult<List<Tag>>.Ok(await db.Tags.AsNoTracking().OrderBy(x => x.Name).ToListAsync());
    }

    public async Task<ServiceResult<List<Policy>>> GetUsedByAsync(Guid guid)
    {
        await using var db = await dbFactory.CreateContextAsync();
        var policyIds = db.PolicyActions.Where(x => x.ActionGuid == guid || x.ConditionNextAction == guid).Select(x => x.PolicyId);
        return ServiceResult<List<Policy>>.Ok(await db.Policies.AsNoTracking().Where(x => policyIds.Contains(x.Id)).OrderBy(x => x.Name).ToListAsync());
    }

    public async Task<ServiceResult<List<AuditLog>>> GetAuditAsync(Guid guid)
    {
        await using var db = await dbFactory.CreateContextAsync();
        return ServiceResult<List<AuditLog>>.Ok(await db.AuditLogs.AsNoTracking()
            .Where(x => x.ObjectType == nameof(ActionReference) && x.ObjectName == guid.ToString())
            .OrderByDescending(x => x.LoggedAtUtc).ThenByDescending(x => x.Id).Take(200).ToListAsync());
    }
}
public sealed record SoftwareUninstallChoice(string Name, string? Version, int Devices);
