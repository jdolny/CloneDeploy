using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Services.Files;
using CloneDeploy_Server.WinPe;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Selects the boot environment and supplies the files and instructions needed to start imaging.
/// </summary>
public sealed class ImagingBootService(
    ICloneDeployDbFactory factory,
    WinPeArtifactStorage artifacts,
    TftpRootDirectory root,
    ILogger<ImagingBootService> logger)
{
    public SharedContentDelivery? Shared { get; init; }

    public Task<ServiceResult<string>> IpxeAsync(ImagingBootRequestDto request, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => IpxeCoreAsync(request, ct), logger);

    public Task<ServiceResult<string>> BootAsync(ImagingBootRequestDto request, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => IpxeCoreAsync(request, ct, "boot/"), logger);

    private async Task<ServiceResult<string>> IpxeCoreAsync(ImagingBootRequestDto request, CancellationToken ct, string prefix = "")
    {
        try
        {
            var architecture = request.Arch?.ToLowerInvariant() switch
            {
                null or "" or "x64" or "x86_64" or "i386" => "x64",
                "arm64" or "aarch64" => "arm64",
                _ => "unsupported"
            };
            if (architecture == "unsupported") return BootError("Unsupported client architecture.");
            var arm64 = architecture == "arm64";
            if (arm64 && !string.IsNullOrEmpty(request.Platform) && request.Platform != "efi")
                return BootError("ARM64 WinPE requires UEFI.");
            await using var db = await factory.CreateContextAsync();
            var definitions = await db.WinPeDefinitions.AsNoTracking().Include(x => x.PublishedBuild).Where(x => !x.IsBase && x.PublishedBuildId != null).ToListAsync(ct);
            string[] resources = arm64 ? ["boot-arm64.wim", "BCD-arm64", "boot-arm64.sdi", "bootaa64.efi"] : ["boot.wim", "BCD", "boot.sdi", "bootmgr", "bootmgfw.efi"];
            if (arm64)
            {
                definitions = definitions.Where(x => x.PublishedBuild is { } b && artifacts.Status(b).State == "Ready" &&
                    resources.All(name => artifacts.Status(b).Artifacts.Any(a => a.Name == name))).ToList();
                foreach (var definition in definitions) definition.IsDefault = definition.IsArm64Default;
            }
            WinPeDefinition selected;
            try
            {
                if (string.IsNullOrWhiteSpace(request.Manufacturer) || string.IsNullOrWhiteSpace(request.Model))
                    selected = definitions.SingleOrDefault(x => x.IsDefault) ?? throw new InvalidOperationException();
                else selected = WinPeModelMatching.Resolve(definitions, request.Manufacturer, request.Model);
            }
            catch (InvalidOperationException) { return BootError("No unique ready WinPE selection is available. Check model rules and the default boot environment."); }
            var build = await db.WinPeBuilds.AsNoTracking().SingleOrDefaultAsync(x => x.Id == selected.PublishedBuildId && x.DefinitionId == selected.Id, ct);
            if (build is null || artifacts.Status(build).State != "Ready") return BootError("Selected WinPE build is not ready.");
            if (!System.IO.File.Exists(arm64 ? WimbootPath() + ".arm64" : WimbootPath())) return BootError("Prepare PXE files on the TFTP settings tab before booting.");
            foreach (var name in resources)
            {
                if (!artifacts.Status(build).Artifacts.Any(x => x.Name == name))
                    return BootError("Rebuild this WinPE boot environment to create standalone boot files.");
                var artifact = artifacts.Status(build).Artifacts.Single(x => x.Name == name);
                if (Shared is not null && await Shared.HasSiteCopyAsync(SharedContentKind.WinPe, $"{build.Id:N}/{name}", artifact.Sha256, ct)) continue;
                if (!File.Exists(await artifacts.PathAsync(build, name, ct)))
                    return BootError("Selected WinPE boot resources are unavailable.");
            }
            // Relative URLs retain the chain URL's origin and path base.
            if (arm64)
                return ServiceResult<string>.Ok($"#!ipxe\nkernel {prefix}wimboot.arm64 || goto failed\n" +
                    $"initrd {prefix}files/{build.Id:D}/arm64/bootmgfw.efi bootmgfw.efi || goto failed\n" +
                    $"initrd {prefix}files/{build.Id:D}/arm64/BCD BCD || goto failed\n" +
                    $"initrd {prefix}files/{build.Id:D}/arm64/boot.sdi boot.sdi || goto failed\n" +
                    $"initrd {prefix}files/{build.Id:D}/arm64/boot.wim boot.wim || goto failed\nboot || goto failed\n:failed\necho WinPE boot failed.\nexit\n");
            return ServiceResult<string>.Ok($"#!ipxe\niseq ${{buildarch}} arm64 && goto wrongarch ||\nkernel {prefix}wimboot || goto failed\n" +
                "iseq ${platform} efi && goto uefi || goto bios\n:bios\n" +
                $"initrd {prefix}files/{build.Id:D}/bootmgr bootmgr || goto failed\ngoto resources\n:uefi\n" +
                $"initrd {prefix}files/{build.Id:D}/bootmgfw.efi bootmgfw.efi || goto failed\n:resources\n" +
                $"initrd {prefix}files/{build.Id:D}/BCD BCD || goto failed\n" +
                $"initrd {prefix}files/{build.Id:D}/boot.sdi boot.sdi || goto failed\n" +
                $"initrd {prefix}files/{build.Id:D}/boot.wim boot.wim || goto failed\nboot || goto failed\n:wrongarch\necho Refresh PXE files to send ARM64 architecture to the boot API.\nexit\n:failed\necho WinPE boot failed.\nexit\n");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        { logger.LogWarning(ex, "Imaging boot media unavailable"); return BootError("WinPE storage is unavailable."); }
    }

    public ServiceResult<PhysicalDownloadDto> Wimboot()
        => WimbootFor("x64");

    public ServiceResult<PhysicalDownloadDto> WimbootFor(string architecture)
    {
        try
        {
            if (architecture is not ("x64" or "arm64")) return ServiceResult<PhysicalDownloadDto>.Fail(ServiceErrorCodes.NotFound);
            var path = WimbootPath() + (architecture == "arm64" ? ".arm64" : "");
            if (path != SiteDownloadDelivery.PathMarker && !File.Exists(path)) return ServiceResult<PhysicalDownloadDto>.Fail(ServiceErrorCodes.NotFound);
            BuildStorage.RejectLinks(path);
            if (Shared is not null) path = Shared.RequestBootPathAsync(path, CancellationToken.None).GetAwaiter().GetResult();
            return ServiceResult<PhysicalDownloadDto>.Ok(new(path));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not read wimboot binary");
            return ServiceResult<PhysicalDownloadDto>.Fail(ServiceErrorCodes.Unavailable);
        }
    }

    public Task<ServiceResult<PhysicalDownloadDto>> BootWimAsync(Guid id, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => BootWimCoreAsync(id, ct), logger);

    private async Task<ServiceResult<PhysicalDownloadDto>> BootWimCoreAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var build = await db.WinPeBuilds.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id &&
                                                                                  !x.Definition.IsBase && x.Definition.PublishedBuildId == x.Id, ct);
        if (build is null || artifacts.Status(build).State != "Ready") return ServiceResult<PhysicalDownloadDto>.Fail(ServiceErrorCodes.NotFound);
        try
        {
            var path = await artifacts.PathAsync(build, "boot.wim", ct);
            if (path != SiteDownloadDelivery.PathMarker && !System.IO.File.Exists(path)) return ServiceResult<PhysicalDownloadDto>.Fail(ServiceErrorCodes.NotFound);
            return ServiceResult<PhysicalDownloadDto>.Ok(new(path));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        { logger.LogWarning(ex, "Imaging boot download unavailable"); return ServiceResult<PhysicalDownloadDto>.Fail(ServiceErrorCodes.Unavailable, UiText.Get(LocKeys.Ui.ImagingBootService.BootMediaStorageIsUnavailable)); }
    }

    private string WimbootPath() => Path.Combine(root.RootPath, "wimboot");
    public Task<ServiceResult<PhysicalDownloadDto>> BootResourceAsync(Guid id, string name, CancellationToken ct) =>
        ServiceOperations.RunAsync(async () =>
        {
            if (name is not ("BCD" or "boot.sdi" or "bootmgr" or "bootmgfw.efi" or "boot-arm64.wim" or "BCD-arm64" or "boot-arm64.sdi" or "bootaa64.efi")) return ServiceResult<PhysicalDownloadDto>.Fail(ServiceErrorCodes.NotFound);
            await using var db = await factory.CreateContextAsync();
            var build = await db.WinPeBuilds.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id &&
                !x.Definition.IsBase && x.Definition.PublishedBuildId == x.Id, ct);
            if (build is null || artifacts.Status(build).State != "Ready") return ServiceResult<PhysicalDownloadDto>.Fail(ServiceErrorCodes.NotFound);
            try
            {
                var path = await artifacts.PathAsync(build, name, ct);
                if (path != SiteDownloadDelivery.PathMarker && !File.Exists(path)) return ServiceResult<PhysicalDownloadDto>.Fail(ServiceErrorCodes.NotFound);
                return ServiceResult<PhysicalDownloadDto>.Ok(new(path));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException)
            {
                logger.LogWarning(ex, "WinPE boot resource {Resource} is unavailable for {Build}", name, id);
                return ServiceResult<PhysicalDownloadDto>.Fail(ServiceErrorCodes.Unavailable);
            }
        }, logger);

    private static ServiceResult<string> BootError(string message) => ServiceResult<string>.Ok("#!ipxe\necho " + message + "\nexit\n");
}
