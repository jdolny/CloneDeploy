using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Provides the overview and supporting details shown on the dashboard.
/// </summary>
public sealed class DashboardService(DashboardQueries queries, ILogger<DashboardService> logger)
{
    public Task<ServiceResult<DashboardSnapshotDto>> GetAsync(int days) => ExecuteAsync(() => queries.GetAsync(days));
    public Task<ServiceResult<List<DashboardDeviceDto>>> GetDevicesAsync(string bucket, DateTime asOf) => ExecuteAsync(() => queries.GetDevicesAsync(bucket, asOf));
    public Task<ServiceResult<List<DashboardFleetDeviceDto>>> GetFleetDevicesAsync(string bucket, DateTime asOf, string? version) => ExecuteAsync(() => queries.GetFleetDevicesAsync(bucket, asOf, version));
    public Task<ServiceResult<List<DashboardTaskDto>>> GetTasksAsync(DeviceTaskStatus status, DateTime from, DateTime to) => ExecuteAsync(() => queries.GetTasksAsync(status, from, to));

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "Dashboard.LoadFailed", null, operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "Dashboard.LoadFailed", null, operationName);
}
