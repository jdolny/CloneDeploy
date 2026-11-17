using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Finds software packages, versions, and installers in the imported WinGet catalog.
/// </summary>
public sealed class WinGetCatalogService(ICloneDeployDbFactory factory)
{
    public async Task<WinGetPackagePageDto> SearchAsync(string? search, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(0, page);
        pageSize = Math.Clamp(pageSize, 1, 50);
        await using var db = await factory.CreateContextAsync();
        var query = db.WinGetLocaleManifests.AsNoTracking()
            .Where(x => x.PackageIdentifier != null && x.PackageIdentifier != "" && x.PackageVersion != null && x.PackageVersion != "");
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(x => EF.Functions.ILike(x.PackageIdentifier!, pattern, "\\") ||
                EF.Functions.ILike(x.PackageName!, pattern, "\\") || EF.Functions.ILike(x.Publisher!, pattern, "\\") ||
                EF.Functions.ILike(x.Moniker!, pattern, "\\") || EF.Functions.ILike(x.Tags!, pattern, "\\"));
        }

        var identifiers = query.Select(x => x.PackageIdentifier!).Distinct();
        var total = await identifiers.CountAsync(ct);
        var ids = await identifiers.OrderBy(x => x).Skip(page * pageSize).Take(pageSize).ToListAsync(ct);
        // Only fetch display metadata for the current page, not every version in the catalog.
        var rows = await db.WinGetLocaleManifests.AsNoTracking().Where(x => ids.Contains(x.PackageIdentifier!) && x.PackageVersion != null && x.PackageVersion != "")
            .GroupBy(x => x.PackageIdentifier)
            .Select(g => g.OrderByDescending(x => x.Major).ThenByDescending(x => x.Minor)
                .ThenByDescending(x => x.Build).ThenByDescending(x => x.Revision)
                .ThenByDescending(x => x.PackageVersion).ThenByDescending(x => x.Id).First())
            .ToListAsync(ct);
        return new WinGetPackagePageDto(total, rows.OrderBy(x => x.PackageIdentifier, StringComparer.Ordinal)
            .Select(x => new WinGetPackageSummaryDto(x.PackageIdentifier!, x.PackageName ?? x.PackageIdentifier!, x.Publisher)).ToList());
    }

    public async Task<List<WinGetPackageVersionDto>> GetVersionsAsync(string packageIdentifier, CancellationToken ct = default)
    {
        await using var db = await factory.CreateContextAsync();
        var locales = await db.WinGetLocaleManifests.AsNoTracking()
            .Where(x => x.PackageIdentifier == packageIdentifier && x.PackageVersion != null && x.PackageVersion != "")
            .OrderByDescending(x => x.Major).ThenByDescending(x => x.Minor)
            .ThenByDescending(x => x.Build).ThenByDescending(x => x.Revision)
            .ThenByDescending(x => x.PackageVersion).ThenByDescending(x => x.Id).ToListAsync(ct);
        var installers = await db.WinGetInstallerManifests.AsNoTracking()
            .Where(x => x.PackageIdentifier == packageIdentifier).ToListAsync(ct);
        var scopes = installers.Where(x => x.PackageVersion != null).GroupBy(x => x.PackageVersion!)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => x.Scope ?? "Not Specified").Distinct()));
        return locales.DistinctBy(x => x.PackageVersion).Select(x => new WinGetPackageVersionDto(
            packageIdentifier, x.PackageVersion!, x.PackageName ?? packageIdentifier, x.Publisher,
            x.ShortDescription, x.License, scopes.GetValueOrDefault(x.PackageVersion!, "Not Specified"))).ToList();
    }
}
