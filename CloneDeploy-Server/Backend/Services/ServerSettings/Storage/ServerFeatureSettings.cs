using System.Text.Json;
using CloneDeploy_Server.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record ServerFeatures(bool Management = true, bool Imaging = true);

public sealed class ServerFeatureSettings(ICloneDeployDbFactory factory)
{
    public const string Key = "Server.EnabledFeatures";
    public event Action? Changed;
    public void NotifyChanged() => Changed?.Invoke();

    public async Task<ServerFeatures> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateContextAsync();
        var json = await db.AppSettings.AsNoTracking().Where(x => x.Name == Key)
            .Select(x => x.Value).SingleOrDefaultAsync(cancellationToken);
        return json is null ? new() : JsonSerializer.Deserialize<ServerFeatures>(json)
            ?? throw new InvalidDataException("Invalid server feature settings.");
    }
}
