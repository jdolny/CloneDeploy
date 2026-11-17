using System.IO.Compression;
using System.Formats.Tar;
using Microsoft.AspNetCore.Authorization;
using CloneDeploy_Server.WinPe;
using CloneDeploy_Server.Backend.Services.DynamicGroups;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Data.Models;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Localization;
using Microsoft.AspNetCore.Components.Authorization;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Lets users manage agent enrollment, request device updates, and review logs and history.
/// </summary>
public sealed class AgentService(
    AgentEnrollmentService enrollment,
    AgentLogService logs,
    AgentCheckinCommandService checkins,
    AgentInventoryCommandService inventory,
    AgentPolicyHistoryService history,
    AuthenticationStateProvider authentication,
    ILogger<AgentService> logger,
    AgentDeviceToolService tools,
    AgentImagingBootService imagingBoot,
    AgentReleaseService windowsRelease,
    MacAgentReleaseService macRelease,
    AgentWakeService wake,
    RemoteAccessRules remoteAccess,
    RemoteDesktopService desktop,
    RemoteTerminalService terminal)
{
    public Task<ServiceResult<AgentProvisioningConfigurationDto>> GetConfigurationAsync() =>
        ExecuteAsync(user => enrollment.GetConfigurationAsync(user));

    public Task<ServiceResult> SaveConfigurationAsync(AgentProvisioningOptionsDto options) =>
        ExecuteAsync(user => enrollment.SaveConfigurationAsync(user, options));

    public Task<ServiceResult<string>> GenerateProvisioningKeyAsync(bool replaceExisting = false) =>
        ExecuteAsync(user => enrollment.GenerateProvisioningKeyAsync(user, replaceExisting));

    public Task<ServiceResult<List<AgentProvisioningRowDto>>> GetProvisioningRequestsAsync() =>
        ExecuteAsync(user => enrollment.ListAsync(user));

    public Task<ServiceResult> DecideProvisioningAsync(Guid id, bool approve) =>
        ExecuteAsync(user => enrollment.DecideAsync(user, id, approve));

    public Task<ServiceResult> DismissProvisioningAsync(Guid id) =>
        ExecuteAsync(user => enrollment.DismissAsync(user, id));

    public Task<ServiceResult> DeleteProvisioningRequestAsync(Guid id) =>
        ExecuteAsync(user => enrollment.DeleteRequestAsync(user, id));

    public Task<ServiceResult> ApprovePendingProvisioningAsync(Guid id) =>
        ExecuteAsync(user => enrollment.ApprovePendingAsync(user, id));

    public Task<ServiceResult<AgentSendResultDto>> RequestCheckInAsync(int deviceId, CancellationToken ct = default) =>
        ExecuteAsync(user => checkins.RequestAsync(user, deviceId, ct));

    public Task<ServiceResult<AgentSendResultDto>> CollectInventoryAsync(int deviceId, CancellationToken ct = default) =>
        ExecuteAsync(user => inventory.CollectAsync(user, deviceId, ct));

    public Task<ServiceResult<AgentLogReply>> ReadLogAsync(int deviceId, CancellationToken ct = default) =>
        ExecuteAsync(user => logs.ReadAsync(user, deviceId, ct));

    public Task<ServiceResult<byte[]>> DownloadLogAsync(int deviceId, CancellationToken ct = default) =>
        ExecuteAsync(user => logs.DownloadAsync(user, deviceId, ct));

    public Task<ServiceResult<List<AgentPolicyHistoryRowDto>>> GetPolicyHistoryAsync(int deviceId) =>
        ExecuteAsync(_ => history.ReadAsync(deviceId));

    private async Task<ServiceResult> ExecuteAsync(Func<ClaimsPrincipal, Task> operation,
        [CallerMemberName] string operationName = "") =>
        await ExecuteAsync(async user => { await operation(user); return true; }, operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<ClaimsPrincipal, Task<T>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(async () =>
        {
            var user = (await authentication.GetAuthenticationStateAsync()).User;
            return ServiceResult<T>.Ok(await operation(user));
        }, logger, ErrorKeys.Agent.Failed, MapError, operationName);

    private static ServiceError? MapError(Exception exception) => exception switch
    {
        UnauthorizedAccessException => new(ErrorKeys.Agent.Forbidden, []),
        AgentEnrollmentException enrollment => new(ErrorKeys.Agent.Validation, [enrollment.Message]),
        _ => null
    };

    public Task<AgentDeviceToolReply> ExecuteDeviceToolAsync(ClaimsPrincipal user, int deviceId, AgentDeviceOperation operation, int offset = 0, string? serviceName = null, int processId = 0, long processStartedUtcTicks = 0, CancellationToken ct = default) =>
        tools.ExecuteAsync(user, deviceId, operation, offset, serviceName, processId, processStartedUtcTicks, ct);

    public Task<AgentImagingReview> ReviewImagingBootAsync(int deviceId) =>
        imagingBoot.ReviewAsync(deviceId);

    public Task<AgentImagingReply> CheckImagingBootAsync(AgentImagingReview review) =>
        imagingBoot.CheckAsync(review);

    public Task<Guid> StartImagingBootAsync(AgentImagingReview review) =>
        imagingBoot.StartAsync(review);

    public Task<AgentReleaseReadiness> ReadWindowsReleaseinessAsync(CancellationToken ct, string architecture = "x64") =>
        windowsRelease.ReadinessAsync(ct, architecture);

    public Task<AgentUpdateOffer?> WindowsReleaseCurrentAsync(CancellationToken ct, string architecture = "x64") =>
        windowsRelease.CurrentAsync(ct, architecture);

    public Task<AgentUpdateOffer> PublishWindowsReleaseAsync(ClaimsPrincipal user, CancellationToken ct, string architecture = "x64") =>
        windowsRelease.PublishAsync(user, ct, architecture);

    public Task<AgentUpdateOffer?> MacReleaseCurrentAsync(CancellationToken ct) =>
        macRelease.CurrentAsync(ct);

    public Task<AgentReleaseReadiness> ReadMacReleaseinessAsync(CancellationToken ct) =>
        macRelease.ReadinessAsync(ct);

    public Task<AgentUpdateOffer> PublishMacReleaseAsync(ClaimsPrincipal user, CancellationToken ct) =>
        macRelease.PublishAsync(user, ct);

    public Task<WakeResult> WakeDeviceAsync(ClaimsPrincipal user, int deviceId, CancellationToken ct = default) =>
        wake.DeviceAsync(user, deviceId, ct);

    public Task<WakeResult> WakeGroupAsync(ClaimsPrincipal user, int groupId, CancellationToken ct = default) =>
        wake.GroupAsync(user, groupId, ct);

    public Task<RemoteAccessSettings> GetRemoteAccessAsync(ClaimsPrincipal user) =>
        remoteAccess.GetAsync(user);

    public Task<(RemoteAccessTarget[] Devices, RemoteAccessTarget[] Groups)> RemoteAccessTargetsAsync(ClaimsPrincipal user) =>
        remoteAccess.TargetsAsync(user);

    public Task<RemoteAccessEffective> RemoteAccessEffectiveAsync(ClaimsPrincipal user, int deviceId, CancellationToken ct) =>
        remoteAccess.EffectiveAsync(user, deviceId, ct);

    public Task SaveRemoteAccessAsync(ClaimsPrincipal user, RemoteAccessSettings settings) =>
        remoteAccess.SaveAsync(user, settings);

    public Task<AgentDesktopSession[]> ListRemoteDesktopSessionsAsync(ClaimsPrincipal user, int deviceId, CancellationToken ct) =>
        desktop.ListSessionsAsync(user, deviceId, ct);

    public Task<Guid> StartRemoteDesktopSessionAsync(ClaimsPrincipal user, int deviceId, int? windowsSessionId, CancellationToken ct) =>
        desktop.StartSessionAsync(user, deviceId, windowsSessionId, ct);

    public Task StopRemoteDesktopAsync(ClaimsPrincipal user, Guid id) =>
        desktop.StopAsync(user, id);

    public Task<Guid> StartTerminalAsync(ClaimsPrincipal user, int deviceId, AgentTerminalShell shell, CancellationToken ct) =>
        terminal.StartAsync(user, deviceId, shell, ct);

    public Task StopTerminalAsync(ClaimsPrincipal user, Guid id) =>
        terminal.StopAsync(user, id);

    public static string RemoteAccessLabel(RemoteAccessMode mode) =>
        UiText.Label(RemoteAccessRules.Label(mode));
}
