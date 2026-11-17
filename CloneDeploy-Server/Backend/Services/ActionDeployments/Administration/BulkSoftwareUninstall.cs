using System.Data;
using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Localization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record BulkUninstallDeviceDto(int Id, Guid Guid, string Name);
public sealed record BulkUninstallSkippedDto(int Id, string Name, string Reason);
public sealed record BulkUninstallPreviewDto(string ConfirmationHash, List<BulkUninstallDeviceDto> Devices, List<BulkUninstallSkippedDto> Skipped);
public sealed record BulkUninstallResultDto(Guid? DeploymentId, int Queued, List<BulkUninstallSkippedDto> Skipped);

public sealed partial class ActionDeploymentService
{
    public Task<ServiceResult<BulkUninstallPreviewDto>> PreviewBulkUninstallAsync(DeploymentRequestDto request) => ExecuteAsync(async () =>
    {
        if (await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.ReportSoftwareRemovalAll) is null) return ServiceResult<BulkUninstallPreviewDto>.Fail(ErrorKeys.Deployment.Forbidden);
        if (!IsValid(request) || request.SoftwareUninstall is null)
            return ServiceResult<BulkUninstallPreviewDto>.Fail(ErrorKeys.Deployment.Validation, UiText.Get(LocKeys.Ui.BulkSoftwareUninstall.SelectASoftwareVersionAndDevices));
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var (devices, skipped) = await BulkUninstallTargetsAsync(db, request);
        var compilation = await CompileDeploymentAsync(db, BulkRequest(request, []));
        if (compilation.Error is not null)
            return ServiceResult<BulkUninstallPreviewDto>.Fail(ErrorKeys.Deployment.Validation, compilation.Error);
        return ServiceResult<BulkUninstallPreviewDto>.Ok(new(BulkConfirmation(request, compilation.Value!, devices, skipped), devices, skipped));
    });

    public Task<ServiceResult<BulkUninstallResultDto>> DeployBulkUninstallAsync(DeploymentRequestDto request, BulkUninstallPreviewDto preview) => ExecuteAsync(async () =>
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.ReportSoftwareRemovalAll);
        if (user is null) return ServiceResult<BulkUninstallResultDto>.Fail(ErrorKeys.Deployment.Forbidden);
        if (!IsValid(request) || request.SoftwareUninstall is null)
            return ServiceResult<BulkUninstallResultDto>.Fail(ErrorKeys.Deployment.Validation, UiText.Get(LocKeys.Ui.BulkSoftwareUninstall.SelectASoftwareVersionAndDevices));
        var userId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await DeviceTaskQueue.LockAsync(db);
        var requestJson = JsonSerializer.Serialize(new { Request = RequestJson(request), preview.ConfirmationHash });
        var previous = await db.ActionDeployments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.RequestId);
        if (previous is not null)
        {
            using var document = JsonDocument.Parse(previous.TargetsJson);
            if (previous.RequestedByUserId != userId || !document.RootElement.TryGetProperty("BulkRequest", out var savedRequest) || savedRequest.GetString() != requestJson)
                return ServiceResult<BulkUninstallResultDto>.Fail(ErrorKeys.Deployment.Validation, UiText.Get(LocKeys.Ui.BulkSoftwareUninstall.ThisRequestIDWasAlreadyUsedForADifferent));
            return ServiceResult<BulkUninstallResultDto>.Ok(document.RootElement.GetProperty("BulkUninstallResult").Deserialize<BulkUninstallResultDto>()!);
        }

        var reviewedIds = preview.Devices.Select(x => x.Id).Concat(preview.Skipped.Select(x => x.Id)).ToArray();
        if (reviewedIds.Distinct().Count() != reviewedIds.Length || !reviewedIds.Order().SequenceEqual(request.DeviceIds.Distinct().Order()))
            return ServiceResult<BulkUninstallResultDto>.Fail(ErrorKeys.Deployment.Validation, UiText.Get(LocKeys.Ui.BulkSoftwareUninstall.TheConfirmedDeviceSelectionChangedReviewTheUninstallAgain));

        // Recheck only the reviewed eligible set. A previously skipped or newly matching
        // device can never be added to this confirmation, even if its inventory changes.
        var (current, newlySkipped) = await BulkUninstallTargetsAsync(db, BulkRequest(request, preview.Devices));
        var devices = current.Where(x => preview.Devices.Any(p => p.Id == x.Id && p.Guid == x.Guid)).ToList();
        newlySkipped.AddRange(current.Where(x => devices.All(d => d.Id != x.Id))
            .Select(x => new BulkUninstallSkippedDto(x.Id, x.Name, "Device identity changed")));
        var compilation = await CompileDeploymentAsync(db, BulkRequest(request, []));
        if (compilation.Error is not null)
            return ServiceResult<BulkUninstallResultDto>.Fail(ErrorKeys.Deployment.Validation, compilation.Error);
        var action = compilation.Value!;
        if (BulkConfirmation(request, action, preview.Devices, preview.Skipped) != preview.ConfirmationHash)
            return ServiceResult<BulkUninstallResultDto>.Fail(ErrorKeys.Deployment.Validation, UiText.Get(LocKeys.Ui.BulkSoftwareUninstall.TheSoftwareSelectionOrConfirmedTargetsChangedReviewThe));
        var skipped = preview.Skipped.Concat(newlySkipped).OrderBy(x => x.Name).ToList();
        var result = new BulkUninstallResultDto(devices.Count == 0 ? null : request.RequestId, devices.Count, skipped);
        // Persist even a zero-task outcome so retries cannot later uninstall a recovered device.
        var now = clock.GetUtcNow().UtcDateTime;
        var deployment = new ActionDeployment
        {
            Id = request.RequestId, ActionGuid = action.Guid, ActionName = action.Name,
            ActionJson = JsonSerializer.Serialize(new { SchemaVersion = 1, Action = action }),
            ContentHash = Hash(JsonSerializer.Serialize(action)), TargetsJson = JsonSerializer.Serialize(new { BulkRequest = requestJson, BulkUninstallResult = result }),
            Timing = request.Timing, Source = DeviceTaskSource.Administrator,
            RequestedByUserId = userId, RequestedBy = user.Identity?.Name ?? userId.ToString(),
            CreatedAtUtc = now, ExpiresAtUtc = now.AddDays(request.ExpireAfterDays),
            Tasks = devices.Select(x => new DeviceTask { Id = Guid.NewGuid(), DeviceId = x.Id, DeviceGuid = x.Guid,
                DeviceName = x.Name, Status = DeviceTaskStatus.Queued }).ToList()
        };
        db.ActionDeployments.Add(deployment);
        db.AuditLogs.Add(new AuditLog { AppUserId = userId, UserName = deployment.RequestedBy, AuditType = AuditType.Create,
            ObjectType = nameof(ActionDeployment), ObjectName = deployment.Id.ToString(),
            ObjectJson = JsonSerializer.Serialize(new { action.Name, request.Timing, Devices = devices.Count, Skipped = skipped }) });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult<BulkUninstallResultDto>.Ok(result);
    });

    private static DeploymentRequestDto BulkRequest(DeploymentRequestDto request, List<BulkUninstallDeviceDto> devices) => new()
    {
        RequestId = request.RequestId, SoftwareUninstall = request.SoftwareUninstall, Timing = request.Timing,
        ExpireAfterDays = request.ExpireAfterDays, DeviceIds = devices.Select(x => x.Id).ToList()
    };

    private static string BulkConfirmation(DeploymentRequestDto request, CompiledPolicyActionDto action,
        List<BulkUninstallDeviceDto> devices, List<BulkUninstallSkippedDto> skipped) =>
        Hash(JsonSerializer.Serialize(new { Request = RequestJson(request), Action = action, Devices = devices, Skipped = skipped }));

    private static async Task<(List<BulkUninstallDeviceDto> Devices, List<BulkUninstallSkippedDto> Skipped)> BulkUninstallTargetsAsync(
        CloneDeployDbContext db, DeploymentRequestDto request)
    {
        var ids = request.DeviceIds.Distinct().Order().ToArray();
        var rows = await db.Devices.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new
        {
            x.Id, x.Guid, x.Name, Archived = x.ArchivedAtUtc != null || x.ProvisionStatus == DeviceProvisionStatus.Archived,
            Installed = db.DeviceSoftware.Any(s => s.DeviceId == x.Id && s.SoftwareId == request.SoftwareUninstall!.SoftwareId),
            Command = db.DeviceSoftware.Any(s => s.DeviceId == x.Id && s.SoftwareId == request.SoftwareUninstall!.SoftwareId && s.UninstallString != null && s.UninstallString.Trim() != "")
        }).ToDictionaryAsync(x => x.Id);
        var devices = new List<BulkUninstallDeviceDto>();
        var skipped = new List<BulkUninstallSkippedDto>();
        foreach (var id in ids)
        {
            rows.TryGetValue(id, out var row);
            var reason = row is null ? "Device no longer exists" : row.Archived ? "Device is archived" :
                !row.Installed ? "This version is no longer inventoried" : !row.Command ? "No uninstall command" : null;
            if (reason is not null) skipped.Add(new(id, row?.Name ?? $"Device {id}", reason));
            else devices.Add(new(id, row!.Guid, row.Name));
        }
        return (devices, skipped);
    }
}
