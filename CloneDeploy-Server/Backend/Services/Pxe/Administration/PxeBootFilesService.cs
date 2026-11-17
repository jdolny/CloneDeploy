using CloneDeploy_Server.Backend.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace CloneDeploy_Server.Backend.Services;

public sealed class PxeBootFilesService(
    PxeBootFileManager files,
    PxeSettingsStore settings,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization,
    ILogger<PxeBootFilesService> logger,
    ServerUrlSettingsStore urls)
{
    private async Task<bool> IsAdministratorAsync() =>
        (await authorization.AuthorizeAsync((await authentication.GetAuthenticationStateAsync()).User, Constants.Permissions.AdminOnly)).Succeeded;

    public async Task<ServiceResult<PxeBootFilesDto?>> ReadAsync()
    {
        if (!await IsAdministratorAsync()) return ServiceResult<PxeBootFilesDto?>.Fail("Pxe.Forbidden");
        return await ServiceOperations.RunAsync(() => Task.FromResult(ServiceResult<PxeBootFilesDto?>.Ok(files.Read())), logger, "Pxe.Failed");
    }

    public async Task<ServiceResult<PxeBootFilesDto>> PrepareAsync(CancellationToken ct = default)
    {
        if (!await IsAdministratorAsync()) return ServiceResult<PxeBootFilesDto>.Fail("Pxe.Forbidden");
        try
        {
            var prepared = await files.PrepareAsync(await urls.RequireBootUrlAsync(ct), ct);
            var current = await settings.ReadAsync(ct);
            var updated = current with
            {
                BiosBootFile = current.BiosBootFile == "proxy/bios/pxeboot.0" ? "ipxeboot/undionly.kpxe" : current.BiosBootFile,
                Efi32BootFile = current.Efi32BootFile == "proxy/efi32/pxeboot.0" ? "ipxeboot/i386/ipxe.efi" : current.Efi32BootFile,
                Efi64BootFile = current.Efi64BootFile is "proxy/efi64/pxeboot.0" or "ipxeboot/x86_64/ipxe.efi"
                    ? "ipxeboot/x86_64-sb/ipxe-shim.efi" : current.Efi64BootFile
            };
            if (updated != current) await settings.SaveAsync(updated, ct);
            return ServiceResult<PxeBootFilesDto>.Ok(prepared);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "PXE boot file preparation failed");
            return ServiceResult<PxeBootFilesDto>.Fail("Pxe.BootPreparationFailed",
                ex is InvalidDataException or InvalidOperationException ? ex.Message : UiText.Get(LocKeys.Ui.PxeBootFilesService.CheckOutboundGitHubAccessDiskPermissionsAndTheServer));
        }
    }
}
