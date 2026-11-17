using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.WinPe;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

// Build IDs identify immutable files for transfers already in progress. Each variant has
// only one current image, replaced automatically after storage and native PXE preparation.
public sealed class WinPeVariantActivation(ICloneDeployDbFactory factory, WinPeArtifactStorage artifacts,
    NativePxePublication publication, NativePxeChanges changes, ILogger<WinPeVariantActivation> logger)
{
    public async Task ActivatePendingAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateContextAsync();
        var pending = await db.WinPeBuilds.AsNoTracking()
            .Where(x => !x.Definition.IsBase && x.StoredAtUtc != null && x.StorageError == null &&
                (x.Definition.PublishedBuildId == null || x.Number > x.Definition.PublishedBuild!.Number) &&
                !db.WinPeBuilds.Any(newer => newer.DefinitionId == x.DefinitionId && newer.StoredAtUtc != null && newer.Number > x.Number))
            .Select(x => x.Id).ToListAsync(ct);
        foreach (var id in pending) await ActivateAsync(id, ct);
    }

    private async Task ActivateAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await using var db = await factory.CreateContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Use the same lock as edits/deletion; a boot-method change cannot race staging.
            await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"WinPeDefinitions\" IN SHARE ROW EXCLUSIVE MODE", ct);
            var build = await db.WinPeBuilds.Include(x => x.Definition).ThenInclude(x => x.PublishedBuild)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (build is null || build.Definition.IsBase || build.StoredAtUtc is null || build.StorageError is not null ||
                build.Definition.PublishedBuild is { } current && current.Number >= build.Number) return;
            if (await db.WinPeBuilds.AnyAsync(x => x.DefinitionId == build.DefinitionId && x.StoredAtUtc != null && x.Number > build.Number, ct)) return;
            var status = artifacts.Status(build);
            foreach (var name in WinPeBootFiles.VariantOutputs)
                if (!status.Artifacts.Any(x => x.Name == name) || !File.Exists(await artifacts.PathAsync(build, name, ct)))
                    throw new InvalidOperationException("Rebuild this boot environment: missing boot output " + name + ".");
            if (build.Definition.NativePxe) await publication.PrepareAsync(build, ct);
            build.Definition.PublishedBuildId = build.Id;
            build.Definition.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            changes.RulesChanged();
            logger.LogInformation("WinPE boot environment {DefinitionId} now uses build {BuildId}", build.DefinitionId, id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not prepare WinPE boot environment build {BuildId} for boot", id);
            await using var db = await factory.CreateContextAsync();
            var message = ex is IOException or InvalidOperationException ? ex.Message : "Could not prepare boot files. Check storage and retry.";
            await db.WinPeBuilds.Where(x => x.Id == id && x.Definition.PublishedBuildId != id)
                .ExecuteUpdateAsync(x => x.SetProperty(b => b.StorageError, message), ct);
        }
    }
}
