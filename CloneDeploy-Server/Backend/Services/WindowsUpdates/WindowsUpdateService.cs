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

/// <summary>Web UI entry point for WindowsUpdates operations.</summary>
public sealed class WindowsUpdateService(WindowsUpdateApprovalService approval,
    WindowsUpdateAutoApprovalService autoApproval,
    ILogger<WindowsUpdateService> logger)
{
    public Task<ServiceResult<List<WindowsUpdateReportRow>>> GetApprovalAsync(string filter, string? search) =>
        ExecuteAsync(() => approval.GetAsync(filter, search));

    public Task<ServiceResult> SetApprovalApprovalAsync(IReadOnlyList<WindowsUpdateIdentity> selections, bool approved) =>
        ExecuteAsync(() => approval.SetApprovalAsync(selections, approved));

    public Task<ServiceResult> AllowApprovalAutoApprovalAsync(IReadOnlyList<WindowsUpdateIdentity> selections) =>
        ExecuteAsync(() => approval.AllowAutoApprovalAsync(selections));

    public Task<ServiceResult<List<WindowsUpdateAutoApprovalRule>>> GetAutoApprovalAsync() =>
        ExecuteAsync(() => autoApproval.GetAsync());

    public Task<ServiceResult<List<InventoryUpdateCategory>>> AutoApprovalClassificationsAsync() =>
        ExecuteAsync(() => autoApproval.ClassificationsAsync());

    public Task<ServiceResult> SaveAutoApprovalAsync(WindowsUpdateAutoApprovalRule input) =>
        ExecuteAsync(() => autoApproval.SaveAsync(input));

    public Task<ServiceResult> DeleteAutoApprovalAsync(int id) =>
        ExecuteAsync(() => autoApproval.DeleteAsync(id));

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, operationName: operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, operationName: operationName);

    public static IReadOnlyList<string> AutoApprovalSeverities => WindowsUpdateAutoApproval.Severities;
}
