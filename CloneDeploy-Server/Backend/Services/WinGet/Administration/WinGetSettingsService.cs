using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Provides the fixed WinGet catalog source and provides details of its latest import.
/// </summary>
public sealed class WinGetSettingsService(ICloneDeployDbFactory factory)
{
    public const string DefaultSourceUrl = "https://github.com/microsoft/winget-pkgs/archive/refs/heads/master.zip";

    public Task<string> GetSourceUrlAsync(CancellationToken ct = default) => Task.FromResult(DefaultSourceUrl);

    public async Task<WinGetManifestDownload?> GetLatestImportAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateContextAsync();
        return await db.WinGetManifestDownloads.AsNoTracking().OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
    }
}