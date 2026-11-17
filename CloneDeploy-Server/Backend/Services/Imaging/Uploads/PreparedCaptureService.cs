using System.Security.Cryptography;
using CloneDeploy_Server.Backend.Common;
using System.Text;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.WinPe;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class PreparedCaptureService(ICloneDeployDbFactory factory, ImagingUploadService uploads,
    WinPeArtifactStorage artifacts, IDataProtectionProvider protection)
{
    private IDataProtector Protector => protection.CreateProtector("CloneDeploy.PreparedCapture.v1");

    public async Task<AgentPreparedCapture> PrepareAsync(Guid deviceGuid, int userId, AgentImagePrepRequest selection, CancellationToken ct)
    {
        if ((selection.ImageId is > 0) == !string.IsNullOrWhiteSpace(selection.NewImageName))
            throw new InvalidOperationException("Select an image or enter a new image name.");
        await using var db = await factory.CreateContextAsync();
        if (!await ImagingPermissions.HasAsync(db, userId, Constants.Permissions.ImageCapture, ct) ||
            selection.ImageId is > 0 && !await ImagingPermissions.HasAsync(db, userId, Constants.Permissions.ImageView, ct))
            throw new InvalidOperationException("Capture image and, for an existing image, View images permissions are required.");
        var device = await db.Devices
            .SingleOrDefaultAsync(x => x.Guid == deviceGuid && x.ArchivedAtUtc == null, ct)
            ?? throw new InvalidOperationException("Device is unavailable.");
        var architecture = selection.NativeArchitecture is "x64" or "arm64" ? selection.NativeArchitecture
            : throw new InvalidOperationException("Image capture requires the agent to report native x64 or ARM64 Windows.");
        var manufacturer = selection.Manufacturer;
        var model = selection.Model;
        if (manufacturer?.Length > 500 || model?.Length > 500)
            throw new InvalidOperationException("Computer manufacturer and model must be at most 500 characters each.");
        var variants = await db.WinPeDefinitions.Include(x => x.PublishedBuild).Where(x => !x.IsBase).ToListAsync(ct);
        var variant = AgentWinPeSelection.Resolve(variants, architecture, manufacturer, model);
        var build = variant.PublishedBuild ?? throw new InvalidOperationException("Build the matching or default WinPE environment first.");
        var url = JsonSerializer.Deserialize<BuildRequestDto>(build.RequestJson)?.Imager?.ServerUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var server) || server.Scheme != "https" || server.UserInfo.Length != 0)
            throw new InvalidOperationException("Rebuild WinPE with an HTTPS Imager server address.");
        var status = artifacts.Status(build);
        if (status.State != "Ready") throw new InvalidOperationException("The selected WinPE build is not ready.");
        var files = new[] { "boot.wim", "boot.sdi" }.Select(name =>
        {
            var source = architecture == "arm64" ? name.Replace("boot.", "boot-arm64.") : name;
            var file = status.Artifacts.SingleOrDefault(x => x.Name == source);
            if (file is null || file.Size <= 0 || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidOperationException("Rebuild WinPE to include verified " + name + ".");
            return new AgentBootFile(name, file.Size, file.Sha256);
        }).ToArray();
        var image = selection.ImageId is > 0
            ? (await uploads.ImagesAsync(ct)).SingleOrDefault(x => x.Id == selection.ImageId)
              ?? throw new InvalidOperationException("The selected image is disabled, protected, or missing.")
            : await uploads.CreateImageAsync(new(selection.NewImageName!, selection.Format ?? ""), userId, ct);
        var upload = await uploads.BeginAsync(image.Id, userId, device.Id, ct, image.Format);
        try
        {
            var row = await db.ImageUploads.SingleAsync(x => x.Id == upload.Id, ct);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var boot = new AgentImagingReview(device.Id, device.Guid, device.Name, image.Id, upload.Id, image.Name,
                0, "Image capture", build.Id, variant.Name, "Image preparation", server.AbsoluteUri, files) { Architecture = architecture };
            row.CaptureTokenHash = Hash(token);
            row.CaptureBootPlanJson = JsonSerializer.Serialize(boot);
            row.CaptureUploadProtected = Protector.Protect(JsonSerializer.Serialize(upload));
            var task = await db.ActiveImagingTasks.SingleAsync(x => x.UploadId == upload.Id, ct);
            task.StatusMessage = "Preparing Windows and WinPE for image capture";
            await db.SaveChangesAsync(ct);
            return new(upload.Id, token, row.ExpiresAtUtc, boot) { Format = image.Format };
        }
        catch
        {
            await uploads.AbortAsync(upload.Id, userId, CancellationToken.None, "Failed");
            throw;
        }
    }

    public async Task<ImageUpload?> ForAgentAsync(Guid id, Guid device, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        return await db.ImageUploads.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.CaptureBootPlanJson != null
            && x.CompletedAtUtc == null && x.ExpiresAtUtc > DateTime.UtcNow
            && db.Devices.Any(d => d.Id == x.DeviceId && d.Guid == device && d.ArchivedAtUtc == null)
            && db.ActiveImagingTasks.Any(t => t.UploadId == x.Id && t.Status < ImagingStatus.Succeeded), ct);
    }

    public async Task<ImageUpload?> AuthorizeAsync(Guid id, string token, CancellationToken ct)
    {
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) return null;
        await using var db = await factory.CreateContextAsync();
        var row = await db.ImageUploads.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.CaptureReady
            && x.CompletedAtUtc == null && x.ExpiresAtUtc > DateTime.UtcNow
            && db.Devices.Any(d => d.Id == x.DeviceId && d.ArchivedAtUtc == null)
            && db.ActiveImagingTasks.Any(t => t.UploadId == x.Id && t.Status < ImagingStatus.Succeeded), ct);
        return row?.CaptureTokenHash is { Length: 64 } hash
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(Hash(token))) ? row : null;
    }

    public ImageUploadDto Destination(ImageUpload row) => JsonSerializer.Deserialize<ImageUploadDto>(
        Protector.Unprotect(row.CaptureUploadProtected ?? throw new InvalidOperationException("Capture destination is missing.")))!;

    // Only the diagnostics endpoint accepts a completed capture capability; writes remain closed.
    public async Task<ImageUpload?> AuthorizeCompletedLogAsync(Guid id, string token, CancellationToken ct)
    {
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) return null;
        await using var db = await factory.CreateContextAsync();
        var row = await db.ImageUploads.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.CaptureReady
            && x.CompletedAtUtc != null && x.ExpiresAtUtc > DateTime.UtcNow
            && db.Devices.Any(d => d.Id == x.DeviceId && d.ArchivedAtUtc == null), ct);
        return row?.CaptureTokenHash is { Length: 64 } hash
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(Hash(token))) ? row : null;
    }

    public async Task ReadyAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var upload = (await db.ImageUploads.FromSqlInterpolated($"SELECT * FROM \"ImageUploads\" WHERE \"Id\" = {id} FOR UPDATE").ToListAsync(ct)).SingleOrDefault()
            ?? throw new InvalidOperationException("Capture was cancelled or expired.");
        var task = await db.ActiveImagingTasks.SingleOrDefaultAsync(x => x.UploadId == id && x.Status < ImagingStatus.Succeeded, ct);
        if (task is null || upload.CompletedAtUtc != null || upload.ExpiresAtUtc <= DateTime.UtcNow)
            throw new InvalidOperationException("Capture was cancelled or expired.");
        upload.CaptureReady = true;
        task.StatusMessage = "Preparation complete; waiting for WinPE capture";
        task.LastUpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
}
