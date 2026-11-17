using System.Text.Json;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Services.Files;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

// One inventory supplies both edit guards and the relocation plan.
public sealed class StorageContentInventory
{
    public List<StoredFile> Files { get; } = [];
    public List<DriverFile> Drivers { get; } = [];
    public List<ImageVersion> Images { get; } = [];
    public List<ImageUpload> Uploads { get; } = [];
    public List<WinPeBuild> Builds { get; } = [];
    public List<SharedContent> Shared { get; } = [];
    public List<ImageReplicationTarget> Targets { get; } = [];
    public List<string> Blockers { get; } = [];
    public bool HasPrimary => Files.Count + Drivers.Count + Images.Count + Builds.Count + Shared.Count + Uploads.Count + Targets.Count > 0;
    public string Summary => string.Join(", ", new[] {
        $"{Images.Count} image versions", $"{Files.Count} files", $"{Drivers.Count} prepared drivers",
        $"{Builds.Count} WinPE builds", $"{Uploads.Count} upload records", $"{Targets.Count} replication targets",
        $"{Shared.Count} shared sources" }.Concat(Blockers));
    public string Fingerprint => JsonSerializer.Serialize(new {
        Files = Files.OrderBy(x => x.Id), Drivers = Drivers.OrderBy(x => x.StoredFileId).Select(x => new { x.StoredFileId, x.PreparedArchiveId, x.PreparedSha256, x.PreparationStatus, x.PreparedStorageProviderId }),
        Images = Images.OrderBy(x => x.Id), Uploads = Uploads.OrderBy(x => x.Id),
        Builds = Builds.OrderBy(x => x.Id), Shared = Shared.OrderBy(x => x.Id),
        Targets = Targets.OrderBy(x => x.ImageId), Blockers });

    public static async Task<StorageContentInventory> ReadAsync(CloneDeployDbContext db, int provider, StoragePurpose purposes, CancellationToken ct = default)
    {
        var result = new StorageContentInventory();
        var files = await db.StoredFiles.Where(x => x.StorageProviderId == provider).OrderBy(x => x.Id).ToListAsync(ct);
        var ids = files.Select(x => x.Id).ToArray();
        var driverIds = await db.DriverFiles.Where(x => ids.Contains(x.StoredFileId)).Select(x => x.StoredFileId).ToListAsync(ct);
        var actionIds = await db.ActionFiles.Where(x => ids.Contains(x.StoredFileId)).Select(x => x.StoredFileId).ToListAsync(ct);
        var softwareIds = await db.SoftwareActions.Where(x => x.InstallerFileId != null && ids.Contains(x.InstallerFileId.Value)).Select(x => x.InstallerFileId!.Value).ToListAsync(ct);
        foreach (var file in files)
        {
            var usage = driverIds.Contains(file.Id) ? StoragePurpose.Imaging : StoragePurpose.DeviceManagement;
            if (actionIds.Contains(file.Id) || softwareIds.Contains(file.Id)) usage |= StoragePurpose.DeviceManagement;
            if ((usage & purposes) == 0) continue;
            result.Files.Add(file);
            if ((usage & purposes) != usage) result.Blockers.Add($"File {file.Name} is shared by Imaging and Device Management; move both purposes together.");
            if (file.Status != StoredFileStatus.Complete) result.Blockers.Add($"File {file.Name} is {file.Status}; finish or remove it first.");
        }
        if (purposes.HasFlag(StoragePurpose.Imaging))
        {
            result.Images.AddRange(await db.ImageVersions.Where(x => x.StorageProviderId == provider).OrderBy(x => x.Id).ToListAsync(ct));
            result.Uploads.AddRange(await db.ImageUploads.Where(x => x.StorageProviderId == provider).OrderBy(x => x.Id).ToListAsync(ct));
            var pending = result.Uploads.Count(x => x.CompletedAtUtc == null);
            if (pending > 0) result.Blockers.Add($"{pending} unfinished image uploads; finish or cancel them first.");
            result.Drivers.AddRange(await db.DriverFiles.Where(x => x.PreparedStorageProviderId == provider).OrderBy(x => x.StoredFileId).ToListAsync(ct));
            if (result.Drivers.Any(x => x.PreparationStatus == DriverPreparationStatus.Extracting))
                result.Blockers.Add("Driver preparation is running.");
            result.Builds.AddRange(await db.WinPeBuilds.Where(x => x.StorageProviderId == provider).OrderBy(x => x.Id).ToListAsync(ct));
            if (result.Builds.Any(x => x.StoredAtUtc == null)) result.Blockers.Add("Unfinished WinPE builds still reference this provider.");
            result.Targets.AddRange(await db.Set<ImageReplicationTarget>().Where(x => x.StorageProviderId == provider).OrderBy(x => x.ImageId).ToListAsync(ct));
            var deletionCount = await db.Set<ImageContentDeletion>().CountAsync(x => x.StorageProviderId == provider, ct);
            if (deletionCount > 0) result.Blockers.Add($"{deletionCount} pending image deletions.");
        }
        var shared = await db.Set<SharedContent>().Where(x => x.SourceProviderId == provider).OrderBy(x => x.Id).ToListAsync(ct);
        result.Shared.AddRange(shared.Where(x => (SharedContentInventory.PurposeFor(x.Kind) & purposes) != 0));
        return result;
    }

    public IEnumerable<(string Relative, string? Hash)> Paths()
    {
        foreach (var f in Files) yield return ($"actions/{f.DirectoryGuid:N}/{StoredFileStorage.ValidateName(f.Name)}", f.Sha256Hash);
        foreach (var d in Drivers)
            if (d.PreparedArchiveId is { } id) yield return ($"prepared-drivers/{id:N}.zip", d.PreparedSha256);
        foreach (var image in Images)
        {
            if (image.FileName is not ("image.wim" or "image.ffu")) throw new IOException("Image version lacks a supported filename.");
            yield return ($"{image.RelativePath}/{image.FileName}", image.Sha256);
        }
        foreach (var build in Builds)
            foreach (var artifact in JsonSerializer.Deserialize<List<CloneDeploy_Server.WinPe.BuildArtifactDto>>(build.StoredArtifactsJson) ?? [])
                yield return ($"winpe/{build.Id:N}/{StoredFileStorage.ValidateName(artifact.Name)}", artifact.Sha256);
        // Retired shared identities may point at a replaced mutable file. The live
        // primary owns its bytes; retired records still follow the ownership change.
        foreach (var shared in Shared.Where(x => x.Current))
            yield return (shared.SourcePath, shared.Sha256);
    }

    public void ChangeOwner(int destination)
    {
        foreach (var f in Files) f.StorageProviderId = destination;
        foreach (var d in Drivers) d.PreparedStorageProviderId = destination;
        foreach (var i in Images) i.StorageProviderId = destination;
        foreach (var u in Uploads) u.StorageProviderId = destination;
        foreach (var b in Builds) b.StorageProviderId = destination;
        foreach (var s in Shared) s.SourceProviderId = destination;
    }
}
