using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authorization;
using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Localization;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Lets administrators configure PXE boot responses and inspect the background service.
/// </summary>
public sealed class PxeService(PxeSettingsAdministration administration, ILogger<PxeService> logger, PxeBootFilesService bootFiles)
{
    public Task<ServiceResult<PxeSettingsDto>> ReadAsync(CancellationToken ct = default) =>
        ExecuteAsync(() => administration.ReadAsync(ct));

    public Task<ServiceResult<PxeSettingsDto>> SaveAsync(PxeSettingsDto settings, CancellationToken ct = default) =>
        ExecuteAsync(() => administration.SaveAsync(settings, ct));

    public Task<ServiceResult<PxeStatusDto>> GetStatusAsync() =>
        ExecuteAsync(administration.GetStatusAsync);

    public Task<ServiceResult<List<PxeBindAddressDto>>> GetBindAddressesAsync() =>
        ExecuteAsync(administration.GetBindAddressesAsync);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, ErrorKeys.Pxe.Failed, operationName: operationName);

    public Task<ServiceResult<PxeBootFilesDto?>> ReadBootFilesAsync() =>
        ExecuteAsync(() => bootFiles.ReadAsync());

    public Task<ServiceResult<PxeBootFilesDto>> PrepareBootFilesAsync(CancellationToken ct = default) =>
        ExecuteAsync(() => bootFiles.PrepareAsync(ct));

    public Task<ServiceResult<BootActivitySnapshot>> GetActivityAsync() =>
        ExecuteAsync(administration.GetActivityAsync);
}
