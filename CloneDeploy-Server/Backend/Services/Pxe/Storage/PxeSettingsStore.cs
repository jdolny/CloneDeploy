using System.Text.Json;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Persists PXE configuration for administration and the background worker.
/// </summary>
public sealed class PxeSettingsStore(ICloneDeployDbFactory factory)
{
    public const string SettingsKey = "Pxe.Settings";

    public async Task<PxeSettingsDto> ReadAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateContextAsync();
        var json = await db.AppSettings.Where(x => x.Name == SettingsKey).Select(x => x.Value).SingleOrDefaultAsync(ct);
        var settings = json is null ? new() : JsonSerializer.Deserialize<PxeSettingsDto>(json)
            ?? throw new InvalidOperationException("PXE settings are invalid.");
        return settings;
    }

    internal async Task SaveAsync(PxeSettingsDto settings, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var row = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == SettingsKey, ct);
        var json = JsonSerializer.Serialize(settings);
        if (row is null) db.AppSettings.Add(new AppSetting { Name = SettingsKey, Value = json });
        else row.Value = json;
        await db.SaveChangesAsync(ct);
    }
}

