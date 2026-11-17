using System.Reflection;
using System.Text.Json;

namespace CloneDeploy_Server.Backend.Services;

public sealed record RunningRuntimeVersions(Version Runtime, Version? AspNetCore)
{
    public static RunningRuntimeVersions Read()
    {
        var info = typeof(WebApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Version.TryParse(info?.Split('+')[0], out var aspNet);
        return new(Environment.Version, aspNet);
    }
}

public sealed record RuntimeUpdateStatus(Version? LatestRuntime, Version? LatestAspNetCore,
    bool UpdateAvailable, DateTimeOffset CheckedAt, string? Error);

/// <summary>Checks stable patches for the main server's running runtime channel.</summary>
public sealed class RuntimeUpdateChecker(IHttpClientFactory clients, TimeProvider clock,
    RunningRuntimeVersions running, ILogger<RuntimeUpdateChecker> logger)
{
    public const string ClientName = "RuntimeUpdates";
    public RunningRuntimeVersions Running => running;
    public string Channel => $"{running.Runtime.Major}.{running.Runtime.Minor}";
    public string DownloadPage => $"https://dotnet.microsoft.com/en-us/download/dotnet/{Channel}";
    private readonly object gate = new();
    private Task<RuntimeUpdateStatus>? pending;
    private RuntimeUpdateStatus? cached;

    public Task<RuntimeUpdateStatus> CheckAsync(bool force = false)
    {
        lock (gate)
        {
            if (pending is { IsCompleted: false }) return pending;
            if (cached is not null)
            {
                var lifetime = force ? TimeSpan.FromMinutes(1) : cached.Error is null ? TimeSpan.FromHours(24) : TimeSpan.FromMinutes(15);
                if (clock.GetUtcNow() - cached.CheckedAt < lifetime) return Task.FromResult(cached);
            }
            return pending = FetchAsync();
        }
    }

    private async Task<RuntimeUpdateStatus> FetchAsync()
    {
        RuntimeUpdateStatus result;
        try
        {
            using var client = clients.CreateClient(ClientName);
            using var response = await client.GetAsync($"https://builds.dotnet.microsoft.com/dotnet/release-metadata/{Channel}/releases.json");
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (json.RootElement.GetProperty("channel-version").GetString() != Channel)
                throw new InvalidDataException("Unexpected runtime channel.");
            Version? latest = null, latestAspNet = null;
            foreach (var release in json.RootElement.GetProperty("releases").EnumerateArray())
            {
                if (!Stable(release.GetProperty("release-version").GetString(), out _)) continue;
                ReadComponent(release, "runtime", ref latest);
                ReadComponent(release, "aspnetcore-runtime", ref latestAspNet);
            }
            if (latest is null || latestAspNet is null) throw new InvalidDataException("Stable runtime versions are missing.");
            result = new(latest, latestAspNet, latest > running.Runtime ||
                (running.AspNetCore is not null && latestAspNet > running.AspNetCore), clock.GetUtcNow(),
                running.AspNetCore is null ? "The running ASP.NET Core version could not be determined." : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            logger.LogWarning(ex, "Could not check for .NET runtime updates");
            result = new(null, null, false, clock.GetUtcNow(), "Microsoft release information could not be reached or returned an invalid response.");
        }
        lock (gate) cached = result;
        return result;
    }

    private bool Stable(string? value, out Version? version) => Version.TryParse(value, out version) &&
        version.Major == running.Runtime.Major && version.Minor == running.Runtime.Minor && version.Build >= 0;

    private void ReadComponent(JsonElement release, string name, ref Version? latest)
    {
        if (release.TryGetProperty(name, out var component) &&
            Stable(component.GetProperty("version").GetString(), out var version) && (latest is null || version > latest))
            latest = version;
    }
}

public sealed class RuntimeUpdateWorker(RuntimeUpdateChecker updates) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await updates.CheckAsync().WaitAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
