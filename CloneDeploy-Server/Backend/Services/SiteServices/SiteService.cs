using System.Security.Claims;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy.SiteServices;
using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Web UI entry point for SiteServices operations.</summary>
public sealed class SiteService(SiteServiceAdministration sites,
    ILogger<SiteService> logger)
{
    public Task<bool> CanCreateWorkerAsync(ClaimsPrincipal user) => sites.CanCreateWorkerAsync(user);
    public Task<SiteEnrollmentPackage> CreateWorkerAsync(ClaimsPrincipal user, string name) => sites.CreateWorkerAsync(user, name);
    public Task<string> PublicUrlAsync(ClaimsPrincipal user) =>
        sites.PublicUrlAsync(user);

    public Task<int> GetProxyRateLimitAsync(ClaimsPrincipal user, Guid id) =>
        sites.GetProxyRateLimitAsync(user, id);

    public Task<SiteProxyAccess> GetProxyAccessAsync(ClaimsPrincipal user, Guid id) =>
        sites.GetProxyAccessAsync(user, id);

    public Task SaveProxyAccessAsync(ClaimsPrincipal user, Guid id, int revision, int enrollmentLimit, SiteProxyAccess access) =>
        sites.SaveProxyAccessAsync(user, id, revision, enrollmentLimit, access);

    public Task<IReadOnlyList<SiteServiceDetails>> ListAsync(ClaimsPrincipal user) =>
        sites.ListAsync(user);

    public Task<SiteServiceDetails?> GetByIdAsync(ClaimsPrincipal user, Guid id) =>
        sites.GetByIdAsync(user, id);

    public Task SaveWindowsWorkerAsync(ClaimsPrincipal user, Guid id, int revision, WindowsWorkerRoles roles) =>
        sites.SaveWindowsWorkerAsync(user, id, revision, roles);

    public Task<SiteEnrollmentPackage> CreateAsync(ClaimsPrincipal user, string name) =>
        sites.CreateAsync(user, name);

    public Task<SiteEnrollmentPackage> ExportAsync(ClaimsPrincipal user, Guid id, int revision) =>
        sites.ExportAsync(user, id, revision);

    public Task SaveAsync(ClaimsPrincipal user, Guid id, int revision, string listenAddress, bool proxyEnabled, bool approve) =>
        sites.SaveAsync(user, id, revision, listenAddress, proxyEnabled, approve);

    public Task<SiteReplicationOptions?> ReadReplicationAsync(ClaimsPrincipal user, Guid id) =>
        sites.ReadReplicationAsync(user, id);

    public Task SaveConfigurationAsync(ClaimsPrincipal user, Guid id, int revision, SiteConfigurationUpdate value) =>
        sites.SaveConfigurationAsync(user, id, revision, value);

    public Task SaveBootAsync(ClaimsPrincipal user, Guid id, int revision, SiteBootSettings settings) =>
        sites.SaveBootAsync(user, id, revision, settings);

    public Task RevokeAsync(ClaimsPrincipal user, Guid id, int revision) =>
        sites.RevokeAsync(user, id, revision);

    public Task DeleteAsync(ClaimsPrincipal user, Guid id, int revision) =>
        sites.DeleteAsync(user, id, revision);

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, operationName: operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, operationName: operationName);
}
