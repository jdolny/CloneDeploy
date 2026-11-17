using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Authorizes administrator access to TFTP configuration and runtime status.
/// </summary>
public sealed class TftpSettingsAdministration(TftpSettingsStore store, AuthenticationStateProvider authentication,
    IAuthorizationService authorization, TftpWorker worker)
{
    private async Task<bool> IsAdministratorAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return (await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded;
    }

    public async Task<ServiceResult<TftpSettingsDto>> ReadAsync(CancellationToken ct = default)
    {
        if (!await IsAdministratorAsync())
            return ServiceResult<TftpSettingsDto>.Fail(ErrorKeys.Tftp.Forbidden);
        return ServiceResult<TftpSettingsDto>.Ok(await store.ReadAsync(ct));
    }

    public async Task<ServiceResult<TftpSettingsDto>> SaveAsync(TftpSettingsDto settings, CancellationToken ct = default)
    {
        if (!await IsAdministratorAsync())
            return ServiceResult<TftpSettingsDto>.Fail(ErrorKeys.Tftp.Forbidden);
        var snapshot = settings with { Port = 69 };
        if (snapshot.Validate() is { } error) return ServiceResult<TftpSettingsDto>.Fail(error);
        await store.SaveAsync(snapshot, ct);
        return ServiceResult<TftpSettingsDto>.Ok(snapshot);
    }

    public async Task<ServiceResult<TftpStatusDto>> GetStatusAsync()
    {
        if (!await IsAdministratorAsync())
            return ServiceResult<TftpStatusDto>.Fail(ErrorKeys.Tftp.Forbidden);
        return ServiceResult<TftpStatusDto>.Ok(worker.Status);
    }

    public async Task<ServiceResult<List<TftpBindAddressDto>>> GetBindAddressesAsync()
    {
        if (!await IsAdministratorAsync())
            return ServiceResult<List<TftpBindAddressDto>>.Fail(ErrorKeys.Tftp.Forbidden);

        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => new TftpBindAddressDto(address.Address.ToString(), adapter.Name)))
            .OrderBy(address => address.AdapterName)
            .ThenBy(address => address.Address)
            .DistinctBy(address => address.Address)
            .ToList();
        return ServiceResult<List<TftpBindAddressDto>>.Ok(addresses);
    }

    public async Task<ServiceResult<BootActivitySnapshot>> GetActivityAsync()
    {
        if (!await IsAdministratorAsync())
            return ServiceResult<BootActivitySnapshot>.Fail(ErrorKeys.Tftp.Forbidden);
        var status = worker.Status;
        return ServiceResult<BootActivitySnapshot>.Ok(new(
            $"{status.State}: {status.Detail}", worker.Activity.Snapshot(), worker.Transfers.OrderByDescending(x => x.Id).Take(100).ToArray()));
    }
}
