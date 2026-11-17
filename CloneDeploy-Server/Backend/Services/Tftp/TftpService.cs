using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Localization;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Lets administrators configure TFTP boot responses and inspect the background service.
/// </summary>
public sealed class TftpService(TftpSettingsAdministration administration, ILogger<TftpService> logger)
{
    public Task<ServiceResult<TftpSettingsDto>> ReadAsync(CancellationToken ct = default) =>
        ExecuteAsync(() => administration.ReadAsync(ct));

    public Task<ServiceResult<TftpSettingsDto>> SaveAsync(TftpSettingsDto settings, CancellationToken ct = default) =>
        ExecuteAsync(() => administration.SaveAsync(settings, ct));

    public Task<ServiceResult<TftpStatusDto>> GetStatusAsync() =>
        ExecuteAsync(administration.GetStatusAsync);

    public Task<ServiceResult<List<TftpBindAddressDto>>> GetBindAddressesAsync() =>
        ExecuteAsync(administration.GetBindAddressesAsync);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, ErrorKeys.Tftp.Failed, operationName: operationName);

    public Task<ServiceResult<BootActivitySnapshot>> GetActivityAsync() =>
        ExecuteAsync(administration.GetActivityAsync);
}
