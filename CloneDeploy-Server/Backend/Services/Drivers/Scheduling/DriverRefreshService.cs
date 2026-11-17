using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Services.Files;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

// Uses the same scheduler and durable StoredFile download queue as external action files.
/// <summary>
/// Checks for updated driver packages and queues their downloads.
/// </summary>
public sealed class DriverRefreshService(ICloneDeployDbFactory factory, ScheduleCalculator schedules, TimeProvider clock,
    StoredFileStorage storage, DriverCatalogClient catalogs, ILogger<DriverRefreshService> logger)
{
    public async Task<List<int>> GetTargetsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        return await db.DriverProfiles.Where(x => x.Enabled && x.UpdateScheduleId != null).Select(x => x.UpdateScheduleId!.Value).Distinct().ToListAsync(ct);
    }
    public async Task RunAsync(int scheduleId, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var schedule = await db.Schedules.FindAsync([scheduleId], ct);
        if (schedule is null || !schedules.IsValid(schedule)) return;
        var now = clock.GetUtcNow().UtcDateTime;
        var profiles = await db.DriverProfiles.AsNoTracking().Where(x => x.Enabled && x.UpdateScheduleId == scheduleId).ToListAsync(ct);
        var errors = new List<string>();
        var refreshedCatalogs = new HashSet<string>();
        foreach (var profile in profiles)
        {
            if (profile.LastEvaluatedAtUtc > now) continue;
            var due = profile.LastEvaluatedAtUtc.HasValue ? schedules.GetNextScheduledTime(schedule, profile.LastEvaluatedAtUtc.Value) : null;
            if (due.HasValue && due <= now)
            {
                var links = await db.DriverFiles.AsNoTracking().Include(x => x.File).Where(x => x.DriverProfileId == profile.Id && x.File.SourceType == FileSourceType.Url).ToListAsync(ct);
                foreach (var link in links)
                {
                    try
                    {
                        DriverCatalogPackageDto? latest = null;
                        if (link.FollowLatest && link.CatalogVendor.HasValue)
                        {
                            var catalogKey = link.CatalogVendor + (link.CatalogVendor == DriverVendor.Microsoft || link.CatalogSource == DriverCatalogClient.HpWinPeCatalog ? link.CatalogSource : "");
                            var packages = await catalogs.GetPackagesAsync(link.CatalogVendor.Value, link.CatalogModel!, link.CatalogSource!, refreshedCatalogs.Add(catalogKey), ct);
                            latest = packages.FirstOrDefault(x => x.OperatingSystem == link.CatalogOs && x.Architecture == link.CatalogArchitecture)
                                ?? throw new InvalidOperationException("No driver package remains for the selected model, OS and architecture.");
                        }
                        else if (!link.File.AutoRefresh || link.File.ExpectedSha256Hash is not null) continue;
                        await storage.Gate.WaitAsync(ct);
                        try
                        {
                            await using var update = await factory.CreateContextAsync();
                            if (!await update.DriverProfiles.AnyAsync(x => x.Id == profile.Id && x.Enabled && x.UpdateScheduleId == scheduleId && x.LastEvaluatedAtUtc == profile.LastEvaluatedAtUtc, ct)) continue;
                            var current = await update.DriverFiles.Include(x => x.File).SingleOrDefaultAsync(x => x.DriverProfileId == profile.Id && x.StoredFileId == link.StoredFileId, ct);
                            if (current is null || ActionFileService.IsBusy(current.File.Status) || current.File.Url != link.File.Url ||
                                current.FollowLatest != link.FollowLatest || current.File.AutoRefresh != link.File.AutoRefresh || current.File.ExpectedSha256Hash != link.File.ExpectedSha256Hash) continue;
                            if (latest is not null)
                            {
                                // Without a vendor hash, re-download to detect replacements at an unchanged URL.
                                if (current.File.Url == latest.Url && current.File.ExpectedSha256Hash == latest.Sha256 &&
                                    current.CatalogRelease == latest.Release && latest.Sha256 is not null &&
                                    current.File.Status == StoredFileStatus.Complete && storage.NamedSize(current.File).HasValue) continue;
                                current.File.Url = latest.Url; current.File.ExpectedSha256Hash = latest.Sha256; current.CatalogRelease = latest.Release;
                                // Keep the logical filename stable; the worker atomically replaces its bytes only after hash verification.
                            }
                            current.File.Status = StoredFileStatus.Queued; current.File.Progress = null; current.File.ErrorMessage = null;
                            await update.SaveChangesAsync(ct);
                        }
                        finally { storage.Gate.Release(); }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception exception) { logger.LogWarning(exception, "Driver refresh failed for file {Id}", link.StoredFileId); errors.Add(link.File.Name); }
                }
            }
            await db.DriverProfiles.Where(x => x.Id == profile.Id && x.UpdateScheduleId == scheduleId && x.LastEvaluatedAtUtc == profile.LastEvaluatedAtUtc)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastEvaluatedAtUtc, now), ct);
        }
        if (errors.Count > 0) throw new InvalidOperationException("Some driver updates could not be checked: " + string.Join(", ", errors));
    }
}
