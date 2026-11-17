using System.Security.Claims;
using System.Security.Cryptography;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Services.Files;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class DeviceAttachmentService(ICloneDeployDbFactory factory, StoredFileStorage storage,
    ChunkedUploadStore uploads, AuthenticationStateProvider authentication, IAuthorizationService authorization,
    ILogger<DeviceAttachmentService> logger)
{
    public Task<ServiceResult<Attachment>> AttachUploadAsync(int deviceId, Guid uploadId) =>
        ServiceOperations.RunAsync(async () =>
        {
            var user = (await authentication.GetAuthenticationStateAsync()).User;
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (user.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(owner) ||
                !(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceEdit)).Succeeded)
                return ServiceResult<Attachment>.Fail("Device.Forbidden");

            Attachment? attachment = null;
            var result = await uploads.AttachAsync(owner, uploadId, async (source, name) =>
            {
                name = StoredFileStorage.ValidateName(name);
                string hash;
                await using (var input = File.OpenRead(source))
                    hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input));
                await storage.Gate.WaitAsync();
                try
                {
                    await using var db = await factory.CreateContextAsync();
                    var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId);
                    if (device is null)
                        return ServiceResult.Fail("Common.NotFound", deviceId);
                    await using var transaction = await db.Database.BeginTransactionAsync();
                    var now = DateTime.UtcNow;
                    var file = new StoredFile
                    {
                        DirectoryGuid = uploadId, Name = name, StorageProviderId = await storage.SelectProviderAsync(),
                        SourceType = FileSourceType.Upload, Status = StoredFileStatus.Complete,
                        Size = new FileInfo(source).Length, Sha256Hash = hash, AvailableAtUtc = now
                    };
                    attachment = new Attachment
                    {
                        DirectoryGuid = uploadId, Name = name, UserName = user.Identity?.Name ?? owner, AttachedAtUtc = now
                    };
                    db.StoredFiles.Add(file);
                    db.DeviceAttachments.Add(new DeviceAttachment { DeviceId = deviceId, Attachment = attachment });
                    DeviceAuditWriter.Add(db, user, device, AuditType.Upload, "Attachment uploaded: " + name);
                    await db.SaveChangesAsync();
                    using var publication = await FilePublication.CreateAsync(source, storage.NamedPath(file));
                    await transaction.CommitAsync();
                    publication.Commit();
                    return ServiceResult.Ok();
                }
                finally { storage.Gate.Release(); }
            });
            return result.Success ? ServiceResult<Attachment>.Ok(attachment!)
                : ServiceResult<Attachment>.Fail(result.Error!.Code, result.Error.Args);
        }, logger, "Device.Failed");

    public Task<ServiceResult<DeviceAttachmentDownloadDto>> DownloadAsync(ClaimsPrincipal user, int deviceId, int attachmentId, CancellationToken ct) =>
        ServiceOperations.RunAsync(async () =>
        {
            if (user.Identity?.IsAuthenticated != true ||
                !(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceView)).Succeeded)
                return ServiceResult<DeviceAttachmentDownloadDto>.Fail(ServiceErrorCodes.Forbidden);
            await using var db = await factory.CreateContextAsync();
            var attachment = await db.DeviceAttachments.AsNoTracking()
                .Where(x => x.DeviceId == deviceId && x.AttachmentId == attachmentId)
                .Select(x => x.Attachment).SingleOrDefaultAsync(ct);
            if (attachment is null) return ServiceResult<DeviceAttachmentDownloadDto>.Fail(ServiceErrorCodes.NotFound);
            var file = await db.StoredFiles.AsNoTracking().SingleOrDefaultAsync(x =>
                x.DirectoryGuid == attachment.DirectoryGuid && x.Name == attachment.Name && x.Status == StoredFileStatus.Complete, ct);
            if (file is null) return ServiceResult<DeviceAttachmentDownloadDto>.Fail(ServiceErrorCodes.NotFound);
            await storage.EnsureReadyAsync(file, ct);
            var path = storage.NamedPath(file);
            return File.Exists(path) ? ServiceResult<DeviceAttachmentDownloadDto>.Ok(new(path, attachment.Name))
                : ServiceResult<DeviceAttachmentDownloadDto>.Fail(ServiceErrorCodes.NotFound);
        }, logger);
}

public sealed record DeviceAttachmentDownloadDto(string Path, string Name);
