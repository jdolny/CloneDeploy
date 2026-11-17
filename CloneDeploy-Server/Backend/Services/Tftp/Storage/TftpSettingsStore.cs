using System.Text.Json;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Persists TFTP configuration for administration and the background worker.
/// </summary>
public sealed class TftpSettingsStore(ICloneDeployDbFactory factory)
{
    public const string SettingsKey = "Tftp.Settings";

    public async Task<TftpSettingsDto> ReadAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateContextAsync();
        var json = await db.AppSettings.Where(x => x.Name == SettingsKey).Select(x => x.Value).SingleOrDefaultAsync(ct);
        var settings = json is null ? new() : JsonSerializer.Deserialize<TftpSettingsDto>(json)
            ?? throw new InvalidOperationException("TFTP settings are invalid.");
        // Older saved custom ports no longer override the standard TFTP listener.
        return settings with { Port = 69 };
    }

    internal async Task SaveAsync(TftpSettingsDto settings, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var row = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == SettingsKey, ct);
        var json = JsonSerializer.Serialize(settings);
        if (row is null) db.AppSettings.Add(new AppSetting { Name = SettingsKey, Value = json });
        else row.Value = json;
        await db.SaveChangesAsync(ct);
    }
}

