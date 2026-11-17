using System.Text.Json;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed partial class ImagingDeploymentClaims
{
    public SharedContentDelivery? Shared { get; init; }

    private static async Task ResolveDriversAsync(CloneDeployDbContext db, ImagingDeployment deployment,
        ImagingHardwareDto hardware, CancellationToken ct)
    {
        var settings = JsonSerializer.Deserialize<DeploymentSettingsDto>(deployment.SettingsJson)!;
        if (settings.DriversResolved) return;

        int? packId = settings.DriverPackId;
        string? warning = null;
        if (settings.AutomaticDrivers)
        {
            var packs = await db.DriverProfiles.AsNoTracking().Where(x => x.Enabled)
                .Include(x => x.Models).Include(x => x.RegexRules).AsSplitQuery().ToListAsync(ct);
            var matches = packs.Where(x => DriverMatching.MatchesPack(x, hardware.Manufacturer, hardware.Model)).ToList();
            packId = matches.Count == 1 ? matches[0].Id : null;
            if (packId is null)
            {
                warning = matches.Count == 0
                    ? $"No matching driver pack found for {hardware.Manufacturer} / {hardware.Model}. Continuing without additional drivers."
                    : "Multiple driver packs match this device. Continuing without additional drivers.";
            }
        }

        var archives = new List<DeploymentDriverArchiveDto>();
        if (packId is { } id)
        {
            var pack = await db.DriverProfiles.AsNoTracking().Include(x => x.Files).ThenInclude(x => x.File)
                .SingleOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new InvalidOperationException("The selected driver pack no longer exists.");
            if (pack.Files.Count == 0) throw new InvalidOperationException("The selected driver pack has no prepared files.");
            foreach (var file in pack.Files)
            {
                if (file.PreparationStatus != DriverPreparationStatus.Ready || file.PreparedArchiveId is null
                    || file.PreparedSha256 is null || file.PreparedSourceHash != file.File.Sha256Hash)
                    throw new InvalidOperationException($"Driver package {file.File.Name} is not ready for download.");
                archives.Add(new(file.PreparedArchiveId.Value, file.PreparedStorageProviderId, file.PreparedSha256, pack.Name));
            }
        }
        deployment.SettingsJson = JsonSerializer.Serialize(settings with
        {
            DriverPackId = packId,
            DriversResolved = true,
            Drivers = archives,
            DriverWarning = warning
        });
    }

    public async Task<Stream> OpenDriverAsync(string? token, Guid archiveId, DriverPreparationStorage drivers, StorageContentStorage storage, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var deployment = await RequireExecutionAsync(db, token, ct);
        var settings = JsonSerializer.Deserialize<DeploymentSettingsDto>(deployment.SettingsJson)!;
        var archive = settings.Drivers.SingleOrDefault(x => x.Id == archiveId) ?? throw new UnauthorizedAccessException();
        async Task<Stream> Primary()
        {
            if (archive.StorageProviderId is { } provider) await storage.EnsureReadyAsync(provider, ct);
            return new FileStream(drivers.ArchivePath(archive.Id, archive.StorageProviderId), FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        return Shared is null ? await Primary() : await Shared.OpenAsync(SharedContentKind.DriverArchive, archive.Id.ToString("N"), archive.Sha256,
            Primary, requests?.HttpContext?.Connection.RemoteIpAddress, ct);
    }
}
