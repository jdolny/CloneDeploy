using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using CloneDeploy_Server.Localization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

// Administrative boundary. The future self-service endpoint must authorize its catalog independently.
/// <summary>
/// Lets administrators review, send, and manage actions assigned to devices.
/// </summary>
public sealed partial class ActionDeploymentService(
    ICloneDeployDbFactory factory,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization,
    TimeProvider clock,
    ILogger<ActionDeploymentService> logger)
{
    private Task<ClaimsPrincipal?> GetStandaloneDeploymentUserAsync(DeploymentRequestDto request) =>
        request.ActionGuid != Guid.Empty && request.SoftwareUninstall is null && request.TestUpdates.Count == 0 &&
        System.Enum.IsDefined(request.Timing)
            ? PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.ActionDeploy)
            : Task.FromResult<ClaimsPrincipal?>(null);

    private async Task<ClaimsPrincipal?> GetUninstallOrAdminAsync(DeploymentRequestDto request)
    {
        if (request.TestUpdates.Count > 0 && request.ActionGuid == Guid.Empty && request.SoftwareUninstall is null)
            return await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.ReportWindowsUpdates);
        // Only immediate software removal from one device uses the device grant.
        if (request.SoftwareUninstall is null || request.ActionGuid != Guid.Empty ||
            request.TestUpdates.Count != 0 || request.Targets.Count != 0 || request.DeviceIds.Count != 1 ||
            request.Timing != DeviceTaskTiming.Now)
            return await GetAdminAsync();
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return user.Identity?.IsAuthenticated == true &&
            int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out _) &&
            (await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceUninstallSoftware)).Succeeded ? user : null;
    }

    public async Task<ServiceResult<DeploymentPreviewDto>> PreviewAsync(DeploymentRequestDto request)
    {
        return await ExecuteAsync(async () =>
        {
            var deviceAction = request.ActionGuid != Guid.Empty && request.SoftwareUninstall is null &&
                request.TestUpdates.Count == 0 && request.Targets.Count == 0 && request.DeviceIds.Count == 1 && request.Timing == DeviceTaskTiming.Now;
            var user = await GetStandaloneDeploymentUserAsync(request) ??
                (deviceAction ? await GetDeviceDeploymentUserAsync() : await GetUninstallOrAdminAsync(request));
            if (user is null)
                return ServiceResult<DeploymentPreviewDto>.Fail(ErrorKeys.Deployment.Forbidden);

            if (!IsValid(request))
                return ServiceResult<DeploymentPreviewDto>.Fail(ErrorKeys.Deployment.Validation, UiText.Get(LocKeys.Ui.ActionDeploymentService.ChooseAValidActionDeliveryTimeAndExpiryOf));

            await using var db = await factory.CreateContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
            var compilation = await CompileDeploymentAsync(db, request);
            if (compilation.Error is not null)
                return ServiceResult<DeploymentPreviewDto>.Fail(ErrorKeys.Deployment.Validation, compilation.Error);
            var action = compilation.Value!;
            var (devices, targetError) = await DeploymentTargets.ResolveAsync(db, request);
            if (targetError is not null)
                return ServiceResult<DeploymentPreviewDto>.Fail(ErrorKeys.Deployment.Validation, targetError);

            if (devices.Count == 0)
                return ServiceResult<DeploymentPreviewDto>.Fail(ErrorKeys.Deployment.Validation, UiText.Get(LocKeys.Ui.ActionDeploymentService.NoActiveDevicesMatchTheseTargets));

            var hash = Hash(JsonSerializer.Serialize(action));
            return ServiceResult<DeploymentPreviewDto>.Ok(new(action.Name, hash, Confirmation(request, hash, devices), devices.Count,
                devices.OrderBy(x => x.Name).Take(100).Select(x => new PolicyTargetDeviceDto(x.Id, x.Name)).ToList()));
        });
    }

    public async Task<ServiceResult<Guid>> DeployAsync(DeploymentRequestDto request, string confirmationHash)
    {
        return await ExecuteAsync(async () =>
        {
            var user = await GetStandaloneDeploymentUserAsync(request) ?? await GetUninstallOrAdminAsync(request);
            if (user is null)
                return ServiceResult<Guid>.Fail(ErrorKeys.Deployment.Forbidden);

            if (!IsValid(request))
                return ServiceResult<Guid>.Fail(ErrorKeys.Deployment.Validation, UiText.Get(LocKeys.Ui.ActionDeploymentService.ChooseAValidActionDeliveryTimeAndExpiryOf));

            var userId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            await using var db = await factory.CreateContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await DeviceTaskQueue.LockAsync(db);
            var requestJson = RequestJson(request);
            var previous = await db.ActionDeployments.SingleOrDefaultAsync(x => x.Id == request.RequestId);
            if (previous is not null)
            {
                if (previous.RequestedByUserId != userId || previous.TargetsJson != requestJson)
                    return ServiceResult<Guid>.Fail(ErrorKeys.Deployment.Validation, UiText.Get(LocKeys.Ui.ActionDeploymentService.ThisRequestIDWasAlreadyUsedForADifferent));
                return ServiceResult<Guid>.Ok(previous.Id);
            }

            var compilation = await CompileDeploymentAsync(db, request);
            if (compilation.Error is not null)
                return ServiceResult<Guid>.Fail(ErrorKeys.Deployment.Validation, compilation.Error);
            var action = compilation.Value!;
            var (devices, targetError) = await DeploymentTargets.ResolveAsync(db, request);
            if (targetError is not null)
                return ServiceResult<Guid>.Fail(ErrorKeys.Deployment.Validation, targetError);

            var json = JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                Action = action
            });
            var hash = Hash(JsonSerializer.Serialize(action));
            if (devices.Count == 0 || Confirmation(request, hash, devices) != confirmationHash)
                return ServiceResult<Guid>.Fail(ErrorKeys.Deployment.Validation, UiText.Get(LocKeys.Ui.ActionDeploymentService.TheSavedActionOrMatchingTargetsChangedReviewThe));

            var now = clock.GetUtcNow().UtcDateTime;
            var deployment = new ActionDeployment
            {
                Id = request.RequestId,
                ActionGuid = action.Guid,
                ActionName = action.Name,
                ActionJson = json,
                ContentHash = hash,
                TargetsJson = requestJson,
                Timing = request.Timing,
                Source = DeviceTaskSource.Administrator,
                RequestedByUserId = userId,
                RequestedBy = user.Identity?.Name ?? userId.ToString(),
                CreatedAtUtc = now,
                ExpiresAtUtc = now.AddDays(request.ExpireAfterDays),
                Tasks = devices.Select(x => new DeviceTask
                {
                    Id = Guid.NewGuid(),
                    DeviceId = x.Id,
                    DeviceGuid = x.Guid,
                    DeviceName = x.Name,
                    Status = DeviceTaskStatus.Queued
                }).ToList()
            };
            db.ActionDeployments.Add(deployment);
            db.AuditLogs.Add(new AuditLog
            {
                AppUserId = userId,
                UserName = deployment.RequestedBy,
                AuditType = AuditType.Create,
                ObjectType = nameof(ActionDeployment),
                ObjectName = deployment.Id.ToString(),
                ObjectJson = JsonSerializer.Serialize(new
                {
                    action.Name, request.Timing,
                    Devices = devices.Count
                })
            });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return ServiceResult<Guid>.Ok(deployment.Id);
        });
    }

    private static async Task<(CompiledPolicyActionDto? Value, string? Error)> CompileDeploymentAsync(CloneDeployDbContext db, DeploymentRequestDto request)
    {
        if (request.SoftwareUninstall is { } uninstall)
        {
            var deviceIds = request.DeviceIds.Distinct().ToArray();
            var software = await db.Software.AsNoTracking().SingleOrDefaultAsync(x => x.Id == uninstall.SoftwareId);
            var eligible = await db.DeviceSoftware.AsNoTracking()
                .Where(x => deviceIds.Contains(x.DeviceId) && x.SoftwareId == uninstall.SoftwareId &&
                    x.UninstallString != null && x.UninstallString.Trim() != "" &&
                    x.Device!.ArchivedAtUtc == null && x.Device.ProvisionStatus != DeviceProvisionStatus.Archived)
                .Select(x => x.DeviceId).Distinct().CountAsync();
            if (software is null || string.IsNullOrWhiteSpace(software.Name) || software.Name != uninstall.Name ||
                (software.Version ?? "") != uninstall.Version || eligible != deviceIds.Length)
                return (null, "A selected device no longer reports this software version, is archived, or has no uninstall command. Review the devices again.");

            return (new(request.RequestId, $"Uninstall {software.Name} {software.Version}".Trim(), ActionType.Software, 1,
                JsonSerializer.SerializeToElement(new { Type = 0, Uninstall = true, UninstallName = software.Name,
                    UninstallVersion = uninstall.Version, uninstall.Arguments, Timeout = 30, SuccessCodes = "0,3010",
                    RedirectStdOut = true, RedirectStdError = true }),
                null, PolicyConditionFailedAction.MarkFailed, null, []), null);
        }
        if (request.TestUpdates.Count == 0)
        {
            var compiled = await PolicyCompiler.CompileStandaloneAsync(db, request.ActionGuid);
            if (compiled.Value is { Type: ActionType.Notification } message)
            {
                var settings = System.Text.Json.Nodes.JsonNode.Parse(message.Settings.GetRawText())!;
                settings["NoticeId"] = request.RequestId;
                return (message with { Settings = JsonSerializer.SerializeToElement(settings) }, compiled.Error);
            }
            return compiled;
        }
        var selections = request.TestUpdates;
        if (selections.Any(x => x.UpdateId == Guid.Empty || x.RevisionNumber < 0) || selections.Select(x => x.UpdateId).Distinct().Count() != selections.Count)
            return (null, "Choose distinct updates with known revisions.");
        var ids = selections.Select(x => x.UpdateId.ToString()).ToArray();
        var catalog = await db.WindowsUpdates.AsNoTracking().Where(x => x.UpdateId != null && ids.Contains(x.UpdateId.ToLower())).ToListAsync();
        if (catalog.Count != selections.Count || catalog.Any(x => !selections.Any(s => s.UpdateId == Guid.Parse(x.UpdateId!) && s.RevisionNumber == x.RevisionNumber)))
            return (null, "An update revision changed or disappeared. Refresh the report and select it again.");
        return (new(request.RequestId, $"Test Windows updates ({selections.Count} selected)", ActionType.WindowsUpdate, 1,
            JsonSerializer.SerializeToElement(new { Mode = WindowsUpdateMode.InstallApproved, Timeout = 120, SuccessCodes = "0,3010",
                RedirectStdOut = true, RedirectStdError = true, TestUpdates = selections.OrderBy(x => x.UpdateId).ToList() }),
            null, PolicyConditionFailedAction.MarkFailed, null, []), null);
    }

    private static bool IsValid(DeploymentRequestDto request) =>
        request.RequestId != Guid.Empty && ((request.SoftwareUninstall is null &&
            ((request.ActionGuid != Guid.Empty && request.TestUpdates.Count == 0) ||
             (request.ActionGuid == Guid.Empty && request.TestUpdates.Count is > 0 and <= 1000))) ||
            (request.SoftwareUninstall is { SoftwareId: > 0 } uninstall && !string.IsNullOrWhiteSpace(uninstall.Name) &&
             uninstall.Version is not null && (uninstall.Arguments?.Length ?? 0) <= 4096 &&
             request.ActionGuid == Guid.Empty && request.TestUpdates.Count == 0 &&
             request.Targets.Count == 0 && request.DeviceIds.Count > 0)) &&
        System.Enum.IsDefined(request.Timing) && request.ExpireAfterDays is >= 1 and <= 365;

    private static string RequestJson(DeploymentRequestDto request) => JsonSerializer.Serialize(new
    {
        request.ActionGuid, request.Timing, request.ExpireAfterDays,
        request.SoftwareUninstall,
        Targets = request.Targets.OrderBy(x => x.Key.GroupId).ThenBy(x => x.Key.DirectorySourceId).ThenBy(x => x.Key.OuGuid).Select(x => new { x.Key, x.IncludeDescendants }),
        Devices = request.DeviceIds.Distinct().Order(),
        TestUpdates = request.TestUpdates.OrderBy(x => x.UpdateId).ThenBy(x => x.RevisionNumber)
    });

    private static string Confirmation(DeploymentRequestDto request, string hash, List<Device> devices) => Hash(JsonSerializer.Serialize(new
    {
        Request = RequestJson(request), hash,
        Devices = devices.Select(x => new { x.Id, x.Guid })
    }));

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
