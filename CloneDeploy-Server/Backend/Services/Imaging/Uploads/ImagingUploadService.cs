using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record CreateCaptureImageDto(string Name, string Format);
public sealed record CaptureImageDto(int Id, string Name, string Format);
public sealed record SmbCaptureDto(string Root, string RelativePath, string Username, string Password)
{
    public override string ToString() => "SMB capture connection (credentials redacted)";
}
public sealed record ImageUploadDto(Guid Id, int ChunkBytes, string Format = "Wim", SmbCaptureDto? Direct = null)
{
    public string? FfuTransport { get; init; }
}
public sealed record CompleteImageUploadDto(long SizeBytes, bool Optimized = false, long? MinimumDiskSizeBytes = null);
public sealed record CompletedImageUploadDto(Guid Id, int Number, bool IsActive);
public sealed record BeginImageUploadDto(string DeviceLinkToken, string Format = "Wim");

// Each request has a short database transaction owning one upload row. No browser identity is used.
public sealed partial class ImagingUploadService(ICloneDeployDbFactory factory, StorageContentStorage storage, ISecretProtector? secrets = null)
{
    public const int ChunkBytes = 8 * 1024 * 1024;
    public const long MaximumBytes = 2L * 1024 * 1024 * 1024 * 1024;

    public async Task<bool> CanRenewCaptureAsync(Guid id, int userId, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        // Do not lock the upload row: final checksum verification holds that lock.
        return await db.ImageUploads.AnyAsync(x => x.Id == id && x.UserId == userId &&
            x.ExpiresAtUtc > DateTime.UtcNow && x.CompletedAtUtc == null, ct) &&
            await db.ActiveImagingTasks.AnyAsync(x => x.UploadId == id && x.AppUserId == userId && x.Status < ImagingStatus.Succeeded, ct);
    }

    public async Task<bool> AllowedAsync(ClaimsPrincipal user, string permission, CancellationToken ct)
    {
        if (user.FindFirstValue("imaging_kind") != "user" || !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return false;
        await using var db = await factory.CreateContextAsync();
        return await ImagingPermissions.HasAsync(db, id, Constants.Permissions.ImagingAccess, ct) &&
            await ImagingPermissions.HasAsync(db, id, permission, ct);
    }

    public async Task<bool> AllowedAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        if (user.FindFirstValue("imaging_kind") != "user" || !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return false;
        await using var db = await factory.CreateContextAsync();
        var access = await DirectoryApplicationAccess.ForUserAsync(db, id, ct);
        return access.Allowed && access.Administrator;
    }

    public async Task<List<CaptureImageDto>> ImagesAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        return await db.Images.AsNoTracking().Where(x => x.Enabled && !x.Protected).OrderBy(x => x.Name)
            .Select(x => new CaptureImageDto(x.Id, x.Name, x.Format.ToString())).ToListAsync(ct);
    }

    public async Task<CaptureImageDto> CreateImageAsync(CreateCaptureImageDto request, int userId, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 200 || name.Any(char.IsControl))
            throw new InvalidDataException("Enter an image name between 1 and 200 characters without control characters.");
        var format = request.Format switch
        {
            "Wim" => ImageFormat.Wim,
            "Ffu" => ImageFormat.Ffu,
            _ => throw new InvalidDataException("Choose WIM or FFU.")
        };
        await using var db = await factory.CreateContextAsync();
        if (!await ImagingPermissions.HasAsync(db, userId, Constants.Permissions.ImageCreate, ct))
            throw new InvalidOperationException("The requested imaging permission is required.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await db.Images.AnyAsync(x => x.Name == name, ct))
            throw new InvalidOperationException("An image with this name already exists. Choose another name or upload to the existing image.");
        var image = new Image { Name = name, Format = format };
        db.Images.Add(image);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            throw new InvalidOperationException("An image with this name already exists. Choose another name or upload to the existing image.");
        }
        db.AuditLogs.Add(new AuditLog { AppUserId = userId,
            UserName = (await db.Users.SingleAsync(x => x.Id == userId, ct)).UserName ?? "Administrator",
            AuditType = AuditType.Create, ObjectType = nameof(Image), ObjectId = image.Id, ObjectName = image.Name,
            ObjectJson = JsonSerializer.Serialize(new { Operation = "ImagerCreate", image.Name, image.Format }) });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(image.Id, image.Name, image.Format.ToString());
    }

    public async Task<ImageUploadDto> BeginAsync(int imageId, int userId, int deviceId, CancellationToken ct, string format = "Wim")
    {
        var provider = await storage.SelectProviderAsync(StoragePurpose.Imaging, ct);
        await using var db = await factory.CreateContextAsync();
        if (!await ImagingPermissions.HasAsync(db, userId, Constants.Permissions.ImageCapture, ct))
            throw new InvalidOperationException("The requested imaging permission is required.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(682947233, {deviceId})", ct);
        await StorageProviderAdministration.LockAsync(db);
        var device = (await db.Devices.FromSqlInterpolated($"SELECT * FROM \"Devices\" WHERE \"Id\" = {deviceId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        if (device is null || device.ArchivedAtUtc is not null || device.ProvisionStatus == DeviceProvisionStatus.Archived)
            throw new InvalidOperationException("The linked device is missing or archived. Link the device again.");
        if (await db.Set<ImagingDeployment>().AnyAsync(x => x.DeviceId == deviceId && x.CancelledAtUtc == null && x.SetupCompletedAtUtc == null, ct))
            throw new InvalidOperationException("This device has an unfinished deployment. Finish or cancel it first.");
        var task = await db.ActiveImagingTasks.SingleOrDefaultAsync(x => x.DeviceId == deviceId, ct);
        if (task is not null && task.Status < ImagingStatus.Succeeded)
            throw new InvalidOperationException("This device already has an imaging task. Finish or cancel it in Tasks > Imaging Tasks before starting another.");
        if (!await db.StorageProviders.AnyAsync(x => x.Id == provider && x.IsEnabled && x.SiteServiceId == null &&
            (x.Purposes & StoragePurpose.Imaging) == StoragePurpose.Imaging, ct))
            throw new InvalidOperationException("Storage provider is no longer enabled for imaging.");
        var image = await db.Images.SingleOrDefaultAsync(x => x.Id == imageId && x.Enabled && !x.Protected, ct);
        if (image is null || image.Format.ToString() != format) throw new InvalidOperationException("The image type changed or is unavailable. Refresh the image list.");
        var upload = new ImageUpload { Id = Guid.NewGuid(), ImageId = imageId, StorageProviderId = provider,
            UserId = userId, DeviceId = deviceId, DeviceName = device.Name, Format = image.Format, ExpiresAtUtc = DateTime.UtcNow.AddHours(24) };
        SmbCaptureDto? direct = null;
        var destination = await db.StorageProviders.Include(x => x.ManagedCredential).SingleAsync(x => x.Id == provider, ct);
        if (destination.Type == StorageProviderType.Smb && destination.ImagingDirectConnect)
        {
            if (destination.ManagedCredential is not { } credential || string.IsNullOrWhiteSpace(credential.Username))
                throw new InvalidOperationException("Select a managed SMB credential on the storage provider before starting capture.");
            if (secrets is null) throw new InvalidOperationException("The server credential service is unavailable.");
            var filename = image.Format == ImageFormat.Ffu ? "capture.ffu" : "capture.wim";
            direct = new(destination.ImagingSmbPath, $"Images\\{imageId}\\{upload.Id:N}\\{filename}", credential.Username, secrets.Unprotect(credential.PasswordEncrypted));
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath(upload, filename))!);
        }
        if (image.Format == ImageFormat.Ffu)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath(upload, "capture.ffu"))!);
            if (direct is null) await SaveFfuStateAsync(upload, new(0, "", false, 0), ct);
        }
        db.ImageUploads.Add(upload);
        if (task is null) { task = new ActiveImagingTask { DeviceId = deviceId }; db.ActiveImagingTasks.Add(task); }
        task.DeploymentId = null;
        task.UploadId = upload.Id;
        task.AppUserId = userId;
        task.ImageName = (await db.Images.SingleAsync(x => x.Id == imageId, ct)).Name;
        task.Type = "Upload";
        task.Status = ImagingStatus.Imaging;
        task.Phase = 0;
        task.ProgressPercent = null;
        task.BytesTransferred = 0;
        task.Rate = null;
        task.StatusMessage = null;
        task.StartedAtUtc = task.LastUpdatedAtUtc = DateTime.UtcNow;
        task.FinishedAtUtc = null;
        task.ErrorMessage = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(upload.Id, ChunkBytes, image.Format.ToString(), direct) { FfuTransport = image.Format == ImageFormat.Ffu ? direct is null ? "Https" : "Smb" : null };
    }

    public async Task AppendAsync(Guid id, int userId, long offset, Stream input, CancellationToken ct)
    {
        // Bound memory and finish reading before locking the row or changing its file.
        using var chunk = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (chunk.Length + count > ChunkBytes) throw new InvalidDataException("Upload chunk is too large.");
            chunk.Write(buffer, 0, count);
        }
        if (chunk.Length == 0) throw new InvalidDataException("Upload chunk is empty.");
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var upload = await LockAsync(db, id, userId, ct);
        Pending(upload);
        if (upload.Format != ImageFormat.Wim) throw new InvalidOperationException("FFU capture writes directly to SMB.");
        if (offset != upload.SizeBytes || offset > MaximumBytes - chunk.Length) throw new InvalidDataException("Upload offset or size is invalid. Restart capture.");
        await ReadyAsync(db, upload, ct);
        var path = FilePath(upload, "image.wim.partial");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 81920, true))
        {
            if (file.Length < offset) throw new IOException("Stored upload is incomplete. Restart capture.");
            file.SetLength(offset); // Remove uncommitted bytes left by an interrupted previous request.
            file.Position = offset;
            chunk.Position = 0;
            await chunk.CopyToAsync(file, ct);
            await file.FlushAsync(ct);
        }
        upload.SizeBytes += chunk.Length;
        var task = await db.ActiveImagingTasks.SingleOrDefaultAsync(x => x.UploadId == id, ct);
        if (task is not null) { task.BytesTransferred = upload.SizeBytes; task.LastUpdatedAtUtc = DateTime.UtcNow; }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<CompletedImageUploadDto> CompleteAsync(Guid id, int userId, CompleteImageUploadDto request, CancellationToken ct)
    {
        await ProgressAsync(id, userId, new ImagingProgressDto(2, null), ct);
        if (request.SizeBytes < 416 || request.SizeBytes > MaximumBytes) throw new InvalidDataException("Invalid capture size.");
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var upload = await LockAsync(db, id, userId, ct);
        if (upload.CompletedAtUtc is not null)
        {
            if (upload.SizeBytes != request.SizeBytes)
                throw new InvalidDataException("Completed upload details do not match.");
            var existing = await db.ImageVersions.SingleAsync(x => x.Id == id, ct);
            if (existing.Optimized != (upload.Format == ImageFormat.Ffu && request.Optimized))
                throw new InvalidDataException("Completed upload optimization status does not match.");
            if (existing.MinimumDiskSizeBytes != request.MinimumDiskSizeBytes)
                throw new InvalidDataException("Completed upload minimum disk size does not match.");
            return new(existing.Id, existing.Number, existing.IsActive);
        }
        Pending(upload);
        await ReadyAsync(db, upload, ct);
        var ffu = upload.Format == ImageFormat.Ffu;
        if (request.MinimumDiskSizeBytes is <= 0 || (!ffu && request.MinimumDiskSizeBytes is not null))
            throw new InvalidDataException("Minimum disk size must be positive and is only supported for FFU captures.");
        var activeTask = await db.ActiveImagingTasks.AsNoTracking().SingleOrDefaultAsync(x => x.UploadId == id, ct);
        if (activeTask is not null && activeTask.Status >= ImagingStatus.Succeeded) throw new InvalidOperationException("Capture is no longer active.");
        // Direct WIM captures use a separate staging file, so HTTP chunk accounting remains strict.
        var directWim = !ffu && (File.Exists(FilePath(upload, "capture.wim")) || File.Exists(FilePath(upload, "image.wim")));
        if (!ffu && !directWim && upload.SizeBytes != request.SizeBytes) throw new InvalidDataException("Capture size does not match the received upload.");
        var partial = FilePath(upload, ffu ? "capture.ffu" : directWim ? "capture.wim" : "image.wim.partial");
        var final = FilePath(upload, ffu ? "image.ffu" : "image.wim");
        // A rename before a failed DB commit is recoverable by repeating completion.
        var source = File.Exists(final) ? final : partial;
        string verifiedHash;
        await using (var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
        {
            if (file.Length != request.SizeBytes) throw new InvalidDataException("Stored capture size does not match.");
            var header = new byte[208];
            await file.ReadExactlyAsync(header, ct);
            if (ffu)
            {
                if (!header.AsSpan(4, 12).SequenceEqual("SignedImage "u8) || BitConverter.ToUInt32(header, 0) < 32)
                    throw new InvalidDataException("Expected an FFU capture.");
            }
            else
            {
                if (!header.AsSpan(0, 8).SequenceEqual("WLPWM\0\0\0"u8)) throw new InvalidDataException("Expected a wimlib pipable WIM capture.");
                file.Position = file.Length - 208;
                var footer = new byte[208];
                await file.ReadExactlyAsync(footer, ct);
                if (!footer.AsSpan(0, 8).SequenceEqual("WLPWM\0\0\0"u8) || BitConverter.ToUInt32(footer, 8) != 208 ||
                    BitConverter.ToUInt32(footer, 44) != 1 || !header.AsSpan(24, 16).SequenceEqual(footer.AsSpan(24, 16)))
                    throw new InvalidDataException("Capture is incomplete or is not a single-image pipable WIM.");
            }
            file.Position = 0;
            verifiedHash = await CaptureUploadChecksum.ComputeAsync(file, ct);
        }
        // Hashing a large image must not hold the global provider or image-catalog lock.
        await StorageProviderAdministration.LockAsync(db);
        await ReadyAsync(db, upload, ct);
        var image = (await db.Images.FromSqlInterpolated($"SELECT * FROM \"Images\" WHERE \"Id\" = {upload.ImageId} FOR UPDATE").ToListAsync(ct)).Single();
        if (!image.Enabled || image.Protected) throw new InvalidOperationException("The image is disabled or protected.");
        if (image.Format != upload.Format) throw new InvalidOperationException("The image type changed during capture.");
        upload.SizeBytes = request.SizeBytes;
        var number = (await db.ImageVersions.Where(x => x.ImageId == image.Id).MaxAsync(x => (int?)x.Number, ct) ?? 0) + 1;
        var version = new ImageVersion { Id = id, ImageId = image.Id, Number = number, Name = $"Version {number}",
            UploadedAtUtc = DateTime.UtcNow, SizeBytes = upload.SizeBytes, StorageProviderId = upload.StorageProviderId,
            Format = upload.Format, Layout = ffu ? "WholeDisk" : "WindowsPartition", Pipable = !ffu, Optimized = ffu && request.Optimized,
            MinimumDiskSizeBytes = request.MinimumDiskSizeBytes,
            FileName = ffu ? "image.ffu" : "image.wim", Sha256 = verifiedHash,
            DeviceId = upload.DeviceId, DeviceName = upload.DeviceName,
            IsActive = !await db.ImageVersions.AnyAsync(x => x.ImageId == image.Id && x.IsActive, ct) };
        if (!File.Exists(final)) File.Move(partial, final);
        version.ReplicationApproved = false;
        db.ImageVersions.Add(version);
        image.LastUploadGuid = id;
        upload.CompletedAtUtc = DateTime.UtcNow;
        upload.CaptureUploadProtected = null;
        // Completed capabilities authorize only the diagnostics endpoint, briefly after publication.
        if (upload.CaptureTokenHash is not null && upload.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(15))
            upload.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15);
        upload.Sha256 = verifiedHash;
        var task = await db.ActiveImagingTasks.SingleOrDefaultAsync(x => x.UploadId == id, ct);
        if (task is not null)
        {
            task.Status = ImagingStatus.Succeeded;
            task.Phase = 2;
            task.ProgressPercent = 100;
            task.BytesTransferred = upload.SizeBytes;
            task.FinishedAtUtc = task.LastUpdatedAtUtc = DateTime.UtcNow;
        }
        db.AuditLogs.Add(new AuditLog { AppUserId = userId, UserName = (await db.Users.SingleAsync(x => x.Id == userId, ct)).UserName ?? "Administrator",
            AuditType = AuditType.Update, ObjectType = nameof(Image), ObjectId = image.Id, ObjectName = image.Name,
            ObjectJson = JsonSerializer.Serialize(new { Section = "Versions", Operation = "CaptureUpload", version.Id, version.Number, version.IsActive }) });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(id, number, version.IsActive);
    }

    public async Task AbortAsync(Guid id, int userId, CancellationToken ct, string reason = "Cancelled")
    {
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var upload = await LockAsync(db, id, userId, ct);
        if (upload.CompletedAtUtc is not null) throw new InvalidOperationException("A completed image version cannot be cancelled.");
        await ReadyAsync(db, upload, ct);
        // Only fixed files owned by this unfinished upload; never recursively delete storage.
        var cleanupPending = false;
        foreach (var name in new[] { "image.wim.partial", "image.wim", "capture.wim", "capture.ffu", "image.ffu", "capture.state.json", "capture.state.tmp" })
        {
            try { File.Delete(FilePath(upload, name)); }
            catch (IOException) { cleanupPending = true; }
        }
        if (cleanupPending) upload.ExpiresAtUtc = DateTime.UtcNow;
        else db.ImageUploads.Remove(upload);
        var task = await db.ActiveImagingTasks.SingleOrDefaultAsync(x => x.UploadId == id, ct);
        if (task is not null && task.Status < ImagingStatus.Succeeded)
        {
            task.Status = reason switch { "Failed" => ImagingStatus.Failed, "Interrupted" => ImagingStatus.Interrupted, _ => ImagingStatus.Cancelled };
            task.ErrorMessage = reason switch { "Failed" => "Capture or upload failed. See the Imager log.", "Interrupted" => "The upload expired without completing.", _ => "Upload cancelled." };
            task.FinishedAtUtc = task.LastUpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task CleanupExpiredAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var expired = await db.ImageUploads.AsNoTracking().Where(x => x.CompletedAtUtc == null && x.ExpiresAtUtc < DateTime.UtcNow)
            .OrderBy(x => x.ExpiresAtUtc).Take(100).ToListAsync(ct);
        foreach (var upload in expired)
        {
            try { await AbortAsync(upload.Id, upload.UserId, ct, "Interrupted"); }
            catch (KeyNotFoundException) { } // Another server already removed the upload.
        }
    }

    public async Task ProgressAsync(Guid id, int userId, ImagingProgressDto progress, CancellationToken ct)
    {
        if (progress.Phase is < 0 or > 2 || progress.Percent is { } value && (!double.IsFinite(value) || value < 0 || value > 100) ||
            progress.NetworkBytesPerSecond is { } rate && (!double.IsFinite(rate) || rate < 0 || rate > 1e12) || progress.StatusMessage?.Length > 512)
            throw new InvalidDataException("Invalid imaging progress.");
        await using var db = await factory.CreateContextAsync();
        // Only lock the task row. Heartbeats must remain possible during long WIM verification.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ImageUploads.AnyAsync(x => x.Id == id && x.UserId == userId, ct)) throw new KeyNotFoundException();
        var task = (await db.ActiveImagingTasks.FromSqlInterpolated($"SELECT * FROM \"ActiveImagingTasks\" WHERE \"UploadId\" = {id} AND \"AppUserId\" = {userId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        if (task is not null && task.Status > ImagingStatus.Succeeded) throw new InvalidOperationException("The imaging task was stopped on the server.");
        if (task is null || task.Status == ImagingStatus.Succeeded) return;
        if (progress.Phase > task.Phase) { task.Phase = progress.Phase; task.ProgressPercent = null; }
        if (progress.Phase == task.Phase && progress.Percent is { } percent)
            task.ProgressPercent = Math.Max(task.ProgressPercent ?? 0, Math.Min(99, percent));
        if (progress.Phase == task.Phase) task.StatusMessage = progress.StatusMessage;
        task.LastUpdatedAtUtc = DateTime.UtcNow;
        task.Rate = progress.NetworkBytesPerSecond is { } bytes
            ? FormattableString.Invariant($"{bytes / 1048576:0.0} MiB/s ({bytes * 60 / 1073741824:0.00} GiB/min)") : null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static async Task<ImageUpload> LockAsync(CloneDeployDbContext db, Guid id, int userId, CancellationToken ct)
    {
        var upload = (await db.ImageUploads.FromSqlInterpolated($"SELECT * FROM \"ImageUploads\" WHERE \"Id\" = {id} AND \"UserId\" = {userId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        return upload ?? throw new KeyNotFoundException("Upload not found.");
    }
    private static void Pending(ImageUpload upload)
    {
        if (upload.CompletedAtUtc is not null || upload.ExpiresAtUtc <= DateTime.UtcNow) throw new InvalidOperationException("Upload is completed or expired. Start a new capture.");
    }
    private async Task ReadyAsync(CloneDeployDbContext db, ImageUpload upload, CancellationToken ct)
    {
        if (!await db.StorageProviders.AnyAsync(x => x.Id == upload.StorageProviderId && x.IsEnabled, ct)) throw new InvalidOperationException("Upload storage is disabled.");
        await storage.EnsureReadyAsync(upload.StorageProviderId, ct);
    }
    private string FilePath(ImageUpload upload, string name)
    {
        var root = storage.GetRoot(upload.StorageProviderId);
        var path = Path.Combine(root, "Images", upload.ImageId.ToString(), upload.Id.ToString("N"), name);
        for (var current = path; !string.Equals(current, root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Image upload paths cannot contain links beneath the storage root."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return path;
    }
}
