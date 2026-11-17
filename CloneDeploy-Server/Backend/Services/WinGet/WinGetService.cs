using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

// Single UI entry point; the scheduler uses the importer directly.
/// <summary>
/// Lets users browse the WinGet software catalog and manage its import settings.
/// </summary>
public sealed class WinGetService(WinGetSettingsService settings, WinGetCatalogService catalog, ILogger<WinGetService> logger)
{
    public Task<ServiceResult<string>> GetSourceUrlAsync(CancellationToken ct = default) =>
        ExecuteAsync(async () => ServiceResult<string>.Ok(await settings.GetSourceUrlAsync(ct)));

    public Task<ServiceResult<WinGetManifestDownload?>> GetLatestImportAsync(CancellationToken ct = default) =>
        ExecuteAsync(async () => ServiceResult<WinGetManifestDownload?>.Ok(await settings.GetLatestImportAsync(ct)));

    public Task<ServiceResult<WinGetPackagePageDto>> SearchAsync(string? search, int page, int pageSize, CancellationToken ct = default) =>
        ExecuteAsync(async () => ServiceResult<WinGetPackagePageDto>.Ok(await catalog.SearchAsync(search, page, pageSize, ct)));

    public Task<ServiceResult<List<WinGetPackageVersionDto>>> GetVersionsAsync(string packageIdentifier, CancellationToken ct = default) =>
        ExecuteAsync(async () => ServiceResult<List<WinGetPackageVersionDto>>.Ok(await catalog.GetVersionsAsync(packageIdentifier, ct)));

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "WinGet.Failed", null, operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "WinGet.Failed", null, operationName);
}
