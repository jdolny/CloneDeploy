using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Authorizes administrator access to PXE configuration and runtime status.
/// </summary>
public sealed class PxeSettingsAdministration(PxeSettingsStore store, AuthenticationStateProvider authentication,
    IAuthorizationService authorization, PxeWorker worker)
{
    private async Task<bool> IsAdministratorAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return (await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded;
    }

    public async Task<ServiceResult<PxeSettingsDto>> ReadAsync(CancellationToken ct = default)
    {
        if (!await IsAdministratorAsync())
            return ServiceResult<PxeSettingsDto>.Fail(ErrorKeys.Pxe.Forbidden);
        return ServiceResult<PxeSettingsDto>.Ok(await store.ReadAsync(ct));
    }

    public async Task<ServiceResult<PxeSettingsDto>> SaveAsync(PxeSettingsDto settings, CancellationToken ct = default)
    {
        if (!await IsAdministratorAsync())
            return ServiceResult<PxeSettingsDto>.Fail(ErrorKeys.Pxe.Forbidden);
        var snapshot = settings with { };
        if (snapshot.Validate() is { } error) return ServiceResult<PxeSettingsDto>.Fail(error);
        await store.SaveAsync(snapshot, ct);
        return ServiceResult<PxeSettingsDto>.Ok(snapshot);
    }

    public async Task<ServiceResult<PxeStatusDto>> GetStatusAsync()
    {
        if (!await IsAdministratorAsync())
            return ServiceResult<PxeStatusDto>.Fail(ErrorKeys.Pxe.Forbidden);
        return ServiceResult<PxeStatusDto>.Ok(worker.Status);
    }

    public async Task<ServiceResult<List<PxeBindAddressDto>>> GetBindAddressesAsync()
    {
        if (!await IsAdministratorAsync())
            return ServiceResult<List<PxeBindAddressDto>>.Fail(ErrorKeys.Pxe.Forbidden);

        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => new PxeBindAddressDto(address.Address.ToString(), adapter.Name)))
            .OrderBy(address => address.AdapterName)
            .ThenBy(address => address.Address)
            .DistinctBy(address => address.Address)
            .ToList();
        return ServiceResult<List<PxeBindAddressDto>>.Ok(addresses);
    }

    public async Task<ServiceResult<BootActivitySnapshot>> GetActivityAsync()
    {
        if (!await IsAdministratorAsync())
            return ServiceResult<BootActivitySnapshot>.Fail(ErrorKeys.Pxe.Forbidden);
        var status = worker.Status;
        return ServiceResult<BootActivitySnapshot>.Ok(new(
            $"{status.State}: {status.Detail}", worker.Activity.Snapshot(), []));
    }
}
