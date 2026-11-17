using System.Security.Claims;
using System.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authorization;
using CloneDeploy_Server.Localization;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Agent.Contracts;
using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Web UI entry point for ActionDeployments operations.</summary>
public sealed class ActionDeploymentUiService(ActionDeploymentService deployments,
    ILogger<ActionDeploymentUiService> logger)
{
    public Task<ServiceResult<DeploymentPreviewDto>> PreviewAsync(DeploymentRequestDto request) =>
        ExecuteAsync(() => deployments.PreviewAsync(request));

    public Task<ServiceResult<Guid>> DeployAsync(DeploymentRequestDto request, string confirmationHash) =>
        ExecuteAsync(() => deployments.DeployAsync(request, confirmationHash));

    public Task<ServiceResult<BulkUninstallPreviewDto>> PreviewBulkUninstallAsync(DeploymentRequestDto request) =>
        ExecuteAsync(() => deployments.PreviewBulkUninstallAsync(request));

    public Task<ServiceResult<BulkUninstallResultDto>> DeployBulkUninstallAsync(DeploymentRequestDto request, BulkUninstallPreviewDto preview) =>
        ExecuteAsync(() => deployments.DeployBulkUninstallAsync(request, preview));

    public Task<ServiceResult<List<PolicyActionOptionDto>>> GetDeviceActionCatalogAsync() =>
        ExecuteAsync(() => deployments.GetDeviceActionCatalogAsync());

    public Task<ServiceResult<List<Guid>>> DeployDeviceActionsAsync(List<DeviceActionDeploymentDto> selections) =>
        ExecuteAsync(() => deployments.DeployDeviceActionsAsync(selections));

    public Task<ServiceResult<List<DeviceActionHistoryRowDto>>> GetDeviceHistoryAsync(int deviceId) =>
        ExecuteAsync(() => deployments.GetDeviceHistoryAsync(deviceId));

    public Task<ServiceResult<string>> GetActionNameAsync(Guid actionGuid) =>
        ExecuteAsync(() => deployments.GetActionNameAsync(actionGuid));

    public Task<ServiceResult<DeploymentCatalogDto>> GetCatalogAsync() =>
        ExecuteAsync(() => deployments.GetCatalogAsync());

    public Task<ServiceResult<List<PolicyTargetDeviceDto>>> SearchDevicesAsync(string? search) =>
        ExecuteAsync(() => deployments.SearchDevicesAsync(search));

    public Task<ServiceResult<List<DeploymentRowDto>>> GetDeploymentsAsync() =>
        ExecuteAsync(() => deployments.GetDeploymentsAsync());

    public Task<ServiceResult<List<DeploymentRowDto>>> GetDeploymentsAsync(bool? history) =>
        ExecuteAsync(() => deployments.GetDeploymentsAsync(history));

    public Task<ServiceResult<List<DeploymentRowDto>>> GetDeploymentsAsync(bool? history, bool allTasks) =>
        ExecuteAsync(() => deployments.GetDeploymentsAsync(history, allTasks));

    public Task<ServiceResult<List<DeploymentRowDto>>> GetUnknownDeploymentsAsync() =>
        ExecuteAsync(() => deployments.GetUnknownDeploymentsAsync());

    public Task<ServiceResult<List<DeploymentRowDto>>> GetUnknownDeploymentsAsync(bool allTasks) =>
        ExecuteAsync(() => deployments.GetUnknownDeploymentsAsync(allTasks));

    public Task<ServiceResult<List<DeviceTask>>> GetTasksAsync(Guid deploymentId) =>
        ExecuteAsync(() => deployments.GetTasksAsync(deploymentId));

    public Task<ServiceResult<DeviceTaskLimitsDto>> GetLimitsAsync() =>
        ExecuteAsync(() => deployments.GetLimitsAsync());

    public Task<ServiceResult> SaveLimitsAsync(DeviceTaskLimitsDto limits) =>
        ExecuteAsync(() => deployments.SaveLimitsAsync(limits));

    public Task<ServiceResult> ResolveUnknownAsync(Guid taskId) =>
        ExecuteAsync(() => deployments.ResolveUnknownAsync(taskId));

    public Task<ServiceResult> CancelAsync(Guid deploymentId) =>
        ExecuteAsync(() => deployments.CancelAsync(deploymentId));

    public Task<ServiceResult> CancelTaskAsync(Guid taskId) =>
        ExecuteAsync(() => deployments.CancelTaskAsync(taskId));

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, operationName: operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, operationName: operationName);
}
