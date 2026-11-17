using CloneDeploy_Agent.Contracts;
using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DeviceActionDeploymentDto(DeploymentRequestDto Request, string ConfirmationHash);

public sealed partial class ActionDeploymentService
{
    private async Task<ClaimsPrincipal?> GetDeviceDeploymentUserAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return (await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceDeployActions)).Succeeded &&
            int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out _) ? user : null;
    }

    public Task<ServiceResult<List<PolicyActionOptionDto>>> GetDeviceActionCatalogAsync() => ExecuteAsync(async () =>
    {
        if (await GetDeviceDeploymentUserAsync() is null) return ServiceResult<List<PolicyActionOptionDto>>.Fail(ErrorKeys.Deployment.Forbidden);
        await using var db = await factory.CreateContextAsync();
        var actions = new List<PolicyActionOptionDto>();
        actions.AddRange(await db.CommandActions.AsNoTracking().Where(x => x.ActionReference!.ArchivedAtUtc == null).Select(x => new PolicyActionOptionDto(x.Guid, x.Id, x.Name, ActionType.Command)).ToListAsync());
        actions.AddRange(await db.FileActions.AsNoTracking().Where(x => x.ActionReference!.ArchivedAtUtc == null).Select(x => new PolicyActionOptionDto(x.Guid, x.Id, x.Name, ActionType.File)).ToListAsync());
        actions.AddRange(await db.NotificationActions.AsNoTracking().Where(x => x.ActionReference!.ArchivedAtUtc == null).Select(x => new PolicyActionOptionDto(x.Guid, x.Id, x.Name, ActionType.Notification)).ToListAsync());
        actions.AddRange(await db.DomainJoinActions.AsNoTracking().Where(x => x.ActionReference!.ArchivedAtUtc == null).Select(x => new PolicyActionOptionDto(x.Guid, x.Id, x.Name, ActionType.DomainJoin)).ToListAsync());
        actions.AddRange(await db.WindowsUpdateActions.AsNoTracking().Where(x => x.ActionReference!.ArchivedAtUtc == null).Select(x => new PolicyActionOptionDto(x.Guid, x.Id, x.Name, ActionType.WindowsUpdate)).ToListAsync());
        actions.AddRange(await db.PrinterActions.AsNoTracking().Where(x => x.ActionReference!.ArchivedAtUtc == null).Select(x => new PolicyActionOptionDto(x.Guid, x.Id, x.Name, ActionType.Printer)).ToListAsync());
        actions.AddRange(await db.ScriptActions.AsNoTracking().Where(x => !x.IsCondition && x.ActionReference!.ArchivedAtUtc == null).Select(x => new PolicyActionOptionDto(x.Guid, x.Id, x.Name, ActionType.Script)).ToListAsync());
        actions.AddRange(await db.SoftwareActions.AsNoTracking().Where(x => x.ActionReference!.ArchivedAtUtc == null).Select(x => new PolicyActionOptionDto(x.Guid, x.Id, x.Name, ActionType.Software)).ToListAsync());
        return ServiceResult<List<PolicyActionOptionDto>>.Ok(actions.OrderBy(x => x.Name).ThenBy(x => x.Type).ToList());
    });

    public Task<ServiceResult<List<Guid>>> DeployDeviceActionsAsync(List<DeviceActionDeploymentDto> selections) => ExecuteAsync(async () =>
    {
        var user = await GetDeviceDeploymentUserAsync();
        if (user is null) return ServiceResult<List<Guid>>.Fail(ErrorKeys.Deployment.Forbidden);
        if (selections.Count is < 1 or > 100 || selections.Any(x => !IsValid(x.Request) || x.Request.ActionGuid == Guid.Empty ||
            x.Request.SoftwareUninstall is not null || x.Request.TestUpdates.Count != 0 || x.Request.Targets.Count != 0 ||
            x.Request.DeviceIds.Count != 1 || x.Request.Timing != DeviceTaskTiming.Now) ||
            selections.Select(x => x.Request.DeviceIds[0]).Distinct().Count() != 1 ||
            selections.Select(x => x.Request.ActionGuid).Distinct().Count() != selections.Count ||
            selections.Select(x => x.Request.RequestId).Distinct().Count() != selections.Count)
            return ServiceResult<List<Guid>>.Fail(ErrorKeys.Deployment.Validation, UiText.Get(LocKeys.Ui.DeviceActionDeployments.Choose1To100DistinctActionsForOneDevice));
        var userId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await DeviceTaskQueue.LockAsync(db);
        var ids = selections.Select(x => x.Request.RequestId).ToList();
        string Targets(DeploymentRequestDto request) => JsonSerializer.Serialize(new { Batch = ids, Request = RequestJson(request) });
        var existing = await db.ActionDeployments.Where(x => ids.Contains(x.Id)).ToListAsync();
        if (existing.Count > 0)
        {
            if (existing.Count != selections.Count || selections.Any(s => !existing.Any(x => x.Id == s.Request.RequestId &&
                    x.RequestedByUserId == userId && x.TargetsJson == Targets(s.Request))))
                return ServiceResult<List<Guid>>.Fail(ErrorKeys.Deployment.Validation, UiText.Get(LocKeys.Ui.DeviceActionDeployments.ARequestIDWasAlreadyUsedForADifferent));
            return ServiceResult<List<Guid>>.Ok(ids);
        }
        var now = clock.GetUtcNow().UtcDateTime;
        for (var index = 0; index < selections.Count; index++)
        {
            var selection = selections[index];
            var request = selection.Request;
            var compiled = await CompileDeploymentAsync(db, request);
            if (compiled.Error is not null) return ServiceResult<List<Guid>>.Fail(ErrorKeys.Deployment.Validation, compiled.Error);
            var (devices, error) = await DeploymentTargets.ResolveAsync(db, request);
            if (error is not null) return ServiceResult<List<Guid>>.Fail(ErrorKeys.Deployment.Validation, error);
            var action = compiled.Value!;
            var hash = Hash(JsonSerializer.Serialize(action));
            if (devices.Count != 1 || devices[0].ProvisionStatus == DeviceProvisionStatus.Archived ||
                Confirmation(request, hash, devices) != selection.ConfirmationHash)
                return ServiceResult<List<Guid>>.Fail(ErrorKeys.Deployment.Validation, UiText.Get(LocKeys.Ui.DeviceActionDeployments.AnActionOrTheDeviceChangedReviewTheSelected));
            var device = devices[0];
            var deployment = new ActionDeployment
            {
                Id = request.RequestId, ActionGuid = action.Guid, ActionName = action.Name,
                ActionJson = JsonSerializer.Serialize(new { SchemaVersion = 1, Action = action }), ContentHash = hash,
                TargetsJson = Targets(request), Timing = DeviceTaskTiming.Now, Source = DeviceTaskSource.Administrator,
                RequestedByUserId = userId, RequestedBy = user.Identity?.Name ?? userId.ToString(),
                // PostgreSQL timestamps retain microseconds; preserve the chosen order in
                // the existing queue without changing the agent's single-action protocol.
                CreatedAtUtc = now.AddTicks(index * 10L), ExpiresAtUtc = now.AddDays(request.ExpireAfterDays),
                Tasks = [new DeviceTask { Id = Guid.NewGuid(), DeviceId = device.Id, DeviceGuid = device.Guid,
                    DeviceName = device.Name, Status = DeviceTaskStatus.Queued }]
            };
            db.ActionDeployments.Add(deployment);
            db.AuditLogs.Add(new AuditLog { AppUserId = userId, UserName = deployment.RequestedBy,
                AuditType = AuditType.Create, ObjectType = nameof(ActionDeployment), ObjectName = deployment.Id.ToString(),
                ObjectJson = JsonSerializer.Serialize(new { action.Name, request.Timing, Devices = 1, DeviceId = device.Id }) });
        }
        // Commit the whole selection together; invalid later actions cannot leave a partial batch.
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult<List<Guid>>.Ok(ids);
    });
}
