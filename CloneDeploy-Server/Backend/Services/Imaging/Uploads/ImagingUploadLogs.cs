using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record CaptureLogDto(string Contents);

public sealed partial class ImagingUploadService
{
    public async Task SaveCompletedLogAsync(Guid id, int userId, CaptureLogDto request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Contents) || request.Contents.Length > 131072)
            throw new InvalidDataException("Imager log must contain between 1 and 131072 characters.");
        await using var db = await factory.CreateContextAsync();
        var upload = await db.ImageUploads.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct)
            ?? throw new KeyNotFoundException();
        if (upload.CompletedAtUtc is null) throw new InvalidOperationException("Capture has not completed.");
        if (upload.DeviceId is not { } deviceId || !await db.Devices.AnyAsync(x => x.Id == deviceId, ct))
            throw new KeyNotFoundException();
        db.DeviceImagingLogs.Add(new DeviceImagingLog
        {
            DeviceId = deviceId, Type = "Imager", SubType = "Capture Completed",
            Contents = $"Capture {id}: Completed.\n\n" + request.Contents
        });
        await db.SaveChangesAsync(ct);
    }
}
