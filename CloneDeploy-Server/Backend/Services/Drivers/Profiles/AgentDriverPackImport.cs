using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Services.Files;
using CloneDeploy_Server.Localization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

// Called only after device certificate and current administrator capability validation.
public sealed class AgentDriverPackImport(ICloneDeployDbFactory factory, ChunkedUploadStore uploads,
    StoredFileStorage storage, ILogger<AgentDriverPackImport> logger)
{
    public Task<ServiceResult<AgentDriverPackCreated>> CreateAsync(string owner, int userId, Guid deviceGuid,
        AgentDriverPackCreateRequest request, CancellationToken ct) => ServiceOperations.RunAsync(async () =>
    {
        var name = request.Name?.Trim(); var manufacturer = request.Manufacturer?.Trim(); var model = request.Model?.Trim();
        if (request.UploadId == Guid.Empty || string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(manufacturer) || manufacturer.Length > 500 || manufacturer.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(model) || model.Length > 500 || model.Any(char.IsControl))
            return ServiceResult<AgentDriverPackCreated>.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.AgentDriverPackImport.EnterAPackNameAndValidHardwareManufacturerAnd));
        // The upload and authenticated session define one publication, including lost-response retries.
        var directory = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(owner + ":" + request.UploadId))[..16]);
        await storage.Gate.WaitAsync(ct);
        try
        {
            await using var existingDb = await factory.CreateContextAsync();
            var existing = await existingDb.DriverProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.DirectoryGuid == directory, ct);
            if (existing is not null) return ServiceResult<AgentDriverPackCreated>.Ok(new(existing.Id, existing.Name));
            AgentDriverPackCreated? created = null;
            var result = await uploads.AttachAsync(owner, request.UploadId, async (source, fileName) =>
            {
                if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.AgentDriverPackImport.UploadTheExportedDriversAsAZIPArchive));
                try
                {
                    using var archive = ZipFile.OpenRead(source);
                    if (archive.Entries.Count > 100000 || !archive.Entries.Any(e => e.Length > 0 && e.FullName.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)))
                        return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.AgentDriverPackImport.TheArchiveContainsNoDriverINFFilesOrToo));
                }
                catch (InvalidDataException) { return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.AgentDriverPackImport.TheDriverArchiveIsNotAValidZIPFile)); }
                await using var db = await factory.CreateContextAsync();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                if (await db.DriverProfiles.AnyAsync(p => p.Name == name, ct))
                    return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.AgentDriverPackImport.ADriverPackWithThisNameAlreadyExistsChoose));
                var admin = await db.AppUsers.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
                var pack = new DriverPack { Name = name, DirectoryGuid = directory,
                    Description = $"Drivers exported from enrolled device {deviceGuid:D}.",
                    Models = [new() { Manufacturer = manufacturer, Model = model }] };
                string hash;
                await using (var stream = File.OpenRead(source)) hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
                var file = new StoredFile { StorageProviderId = await storage.SelectProviderAsync(CloneDeploy_Server.Backend.Enum.StoragePurpose.Imaging), DirectoryGuid = directory,
                    Name = StoredFileStorage.ValidateName(fileName), SourceType = FileSourceType.Upload,
                    Status = StoredFileStatus.Complete, Sha256Hash = hash, Size = new FileInfo(source).Length, AvailableAtUtc = DateTime.UtcNow };
                db.DriverProfiles.Add(pack); await db.SaveChangesAsync(ct);
                db.DriverFiles.Add(new() { DriverProfileId = pack.Id, File = file });
                db.AuditLogs.Add(new() { AppUserId = userId, UserName = admin.UserName ?? "Administrator", AuditType = AuditType.Create,
                    ObjectType = nameof(DriverPack), ObjectId = pack.Id, ObjectName = pack.Name,
                    ObjectJson = JsonSerializer.Serialize(new { Source = "Agent driver export", deviceGuid, request.UploadId, manufacturer, model, Sha256 = hash }) });
                await db.SaveChangesAsync(ct);
                using var publication = await FilePublication.CreateAsync(source, storage.NamedPath(file), ct: ct);
                await transaction.CommitAsync(ct); publication.Commit();
                created = new(pack.Id, pack.Name);
                return ServiceResult.Ok();
            });
            return result.Success ? ServiceResult<AgentDriverPackCreated>.Ok(created!) : ServiceResult<AgentDriverPackCreated>.Fail(result.Error.Code, result.Error.Args);
        }
        finally { storage.Gate.Release(); }
    }, logger, ErrorKeys.Driver.Failed);
}
