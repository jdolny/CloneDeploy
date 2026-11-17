using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Lets users manage storage locations, check their availability, and choose the default.
/// </summary>
public sealed class StorageProviderService(
    StorageProviderAdministration providers,
    StorageMountService mounts,
    ILogger<StorageProviderService> logger,
    SharedReplicationAdministration replication,
    ReplicationSettingsAdministration replicationSettings)
{
    public string ProcessIdentity => mounts.ProcessIdentity;
    public string RootPath => mounts.RootPath;
    public Task<ServiceResult<List<StorageSiteDto>>> GetSitesAsync() => ExecuteAsync(providers.GetSitesAsync);
    public Task<ServiceResult<List<StorageProviderDto>>> GetAsync(string? searchText = null) => ExecuteAsync(() => providers.GetAsync(searchText));
    public Task<ServiceResult<StorageProvider>> CreateAsync(StorageProvider provider) => ExecuteAsync(() => providers.CreateAsync(provider));
    public Task<ServiceResult<StorageProvider>> UpdateAsync(StorageProvider provider) => ExecuteAsync(() => providers.UpdateAsync(provider));
    public Task<ServiceResult> DeleteAsync(int id) => ExecuteAsync(() => providers.DeleteAsync(id));
    public Task<ServiceResult> DeleteManyAsync(IEnumerable<int> ids) => ExecuteAsync(() => providers.DeleteManyAsync(ids));
    public Task<ServiceResult<List<StorageMountStatusDto>>> GetMountsAsync(bool check = false) => ExecuteAsync(() => mounts.GetAsync(check));
    public Task<ServiceResult<List<StorageMountStatusDto>>> CheckMountAsync(int providerId) => ExecuteAsync(() => mounts.CheckAsync(providerId));

    public Task<ServiceResult<ReplicationOverview>> ReadReplicationActivityAsync(CancellationToken ct = default) =>
        ExecuteAsync(() => replication.ReadActivityAsync(ct));

    public Task<ServiceResult<SharedReplicationSettings>> ReadReplicationAsync() =>
        ExecuteAsync(replication.ReadAsync);

    public Task<ServiceResult<ReplicationSettingsSnapshot>> ReadReplicationLimitsAsync() =>
        ExecuteAsync(replicationSettings.ReadAsync);

    public Task<ServiceResult<bool>> SaveReplicationLimitsAsync(ReplicationOptions value, ReplicationOptions expected) =>
        ExecuteAsync(() => replicationSettings.SaveAsync(value, expected));

    public Task<ServiceResult<bool>> SaveReplicationLimitsSiteAsync(Guid site, SiteReplicationOptions value, SiteReplicationOptions? expected) =>
        ExecuteAsync(() => replicationSettings.SaveSiteAsync(site, value, expected));

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "StorageMount.Failed", null, operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "StorageMount.Failed", null, operationName);
}
