using Npgsql;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

// UI entry point; implementations retain authorization and validation, while this facade handles unexpected failures.
/// <summary>
/// Lets users manage driver profiles, packages, downloads, and device matching rules.
/// </summary>
public sealed class DriverService(DriverPackService packs, DriverFileService files, ILogger<DriverService> logger)
{
    public Task<ServiceResult<List<DriverPack>>> GetAsync(string? search = null, bool includeDisabled = false) =>
        ExecuteAsync(() => packs.GetAsync(search, includeDisabled));

    public Task<ServiceResult<DriverPack>> GetByIdAsync(int id) =>
        ExecuteAsync(() => packs.GetByIdAsync(id));

    public Task<ServiceResult<DriverPack>> CreateAsync(string name, string? description) =>
        ExecuteAsync(() => packs.CreateAsync(name, description));

    public Task<ServiceResult> SaveGeneralAsync(DriverPack input) =>
        ExecuteAsync(() => packs.SaveGeneralAsync(input));

    public Task<ServiceResult<List<InventoriedModelDto>>> GetInventoryAsync() =>
        ExecuteAsync(() => packs.GetInventoryAsync());

    public Task<ServiceResult<List<InventoriedModelDto>>> PreviewMatchingAsync(DriverPack input) =>
        ExecuteAsync(() => packs.PreviewMatchingAsync(input));

    public Task<ServiceResult> SaveMatchingAsync(DriverPack input) =>
        ExecuteAsync(() => packs.SaveMatchingAsync(input));

    public Task<ServiceResult> SaveConfigurationAsync(DriverPack input) =>
        ExecuteAsync(() => packs.SaveConfigurationAsync(input));

    public Task<ServiceResult<List<Schedule>>> GetSchedulesAsync() =>
        ExecuteAsync(() => packs.GetSchedulesAsync());

    public Task<ServiceResult<List<DeploymentProfile>>> GetUsedByAsync(int id) =>
        ExecuteAsync(() => packs.GetUsedByAsync(id));

    public Task<ServiceResult> DeleteAsync(int id) =>
        ExecuteAsync(() => packs.DeleteAsync(id));

    public Task<ServiceResult<List<AuditLog>>> GetAuditAsync(int id) =>
        ExecuteAsync(() => packs.GetAuditAsync(id));

    public Task<ServiceResult<List<DriverCatalogModelDto>>> GetCatalogModelsAsync(DriverVendor vendor, bool refresh = false) =>
        ExecuteAsync(() => files.GetCatalogModelsAsync(vendor, refresh));

    public Task<ServiceResult<List<DriverCatalogPackageDto>>> GetCatalogPackagesAsync(DriverVendor vendor, DriverCatalogModelDto model, bool refresh = false) =>
        ExecuteAsync(() => files.GetCatalogPackagesAsync(vendor, model, refresh));

    public Task<ServiceResult<List<DriverFileItemDto>>> GetFilesAsync(int profileId) =>
        ExecuteAsync(() => files.GetAsync(profileId));

    public Task<ServiceResult> ImportAsync(int profileId, DriverCatalogPackageDto selection, bool followLatest) =>
        ExecuteAsync(() => files.ImportAsync(profileId, selection, followLatest));

    public Task<ServiceResult> SaveExternalAsync(int profileId, int? fileId, string name, string url, string? expectedHash, bool autoRefresh) =>
        ExecuteAsync(() => files.SaveExternalAsync(profileId, fileId, name, url, expectedHash, autoRefresh));

    public Task<ServiceResult> AttachUploadAsync(int profileId, Guid uploadId) =>
        ExecuteAsync(() => files.AttachUploadAsync(profileId, uploadId));

    public Task<ServiceResult> SetFollowLatestAsync(int profileId, int fileId, bool followLatest) =>
        ExecuteAsync(() => files.SetFollowLatestAsync(profileId, fileId, followLatest));

    public Task<ServiceResult> RetryPreparationAsync(int profileId, int fileId) =>
        ExecuteAsync(() => files.RetryPreparationAsync(profileId, fileId));

    public Task<ServiceResult> QueueAsync(int profileId, int fileId) =>
        ExecuteAsync(() => files.QueueAsync(profileId, fileId));

    public Task<ServiceResult> CancelAsync(int profileId, int fileId) =>
        ExecuteAsync(() => files.CancelAsync(profileId, fileId));

    public Task<ServiceResult> DeleteFileAsync(int profileId, int fileId) =>
        ExecuteAsync(() => files.DeleteAsync(profileId, fileId));

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "Driver.Failed", MapError, operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "Driver.Failed", MapError, operationName);

    private static ServiceError? MapError(Exception exception) => exception switch
    {
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } => new("Driver.Validation", ["A record with this name already exists."]),
        CloneDeploy_Server.Backend.Services.Files.UploadException upload => new("Driver.Validation", [upload.Message]),
        _ => null
    };
}
