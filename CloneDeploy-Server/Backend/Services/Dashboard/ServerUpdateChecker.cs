using System.Net;
using System.Text.Json;

namespace CloneDeploy_Server.Backend.Services;

public sealed record ServerUpdateStatus(string InstalledVersion, string? LatestVersion, bool UpdateAvailable,
    DateTimeOffset CheckedAt, string? Error);

/// <summary>Shared, demand-driven cache for the public stable server release.</summary>
public sealed class ServerUpdateChecker(IHttpClientFactory clients, TimeProvider clock, ILogger<ServerUpdateChecker> logger)
{
    public const string ClientName = "ServerUpdates";
    public const string LatestApi = "https://api.github.com/repos/jdolny/CloneDeploy/releases/latest";
    public const string ReleasePage = "https://github.com/jdolny/CloneDeploy/releases/latest";
    private readonly object gate = new();
    private Task<ServerUpdateStatus>? pending;
    private ServerUpdateStatus? cached;
    private static readonly Version Installed = Normalize(typeof(ServerUpdateChecker).Assembly.GetName().Version!);

    public Task<ServerUpdateStatus> CheckAsync(bool force = false)
    {
        lock (gate)
        {
            if (pending is { IsCompleted: false }) return pending;
            if (cached is not null)
            {
                // Manual checks are also throttled across all administrator sessions.
                var lifetime = force ? TimeSpan.FromMinutes(1) : cached.Error is null ? TimeSpan.FromHours(24) : TimeSpan.FromMinutes(15);
                if (clock.GetUtcNow() - cached.CheckedAt < lifetime) return Task.FromResult(cached);
            }
            return pending = FetchAsync();
        }
    }

    private async Task<ServerUpdateStatus> FetchAsync()
    {
        ServerUpdateStatus result;
        try
        {
            using var client = clients.CreateClient(ClientName);
            using var response = await client.GetAsync(LatestApi);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidDataException("The repository is private, unavailable, or has no published stable release.");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new InvalidDataException("GitHub refused the request or its request limit was reached. Try again later.");
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var release = json.RootElement;
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
                throw new InvalidDataException("GitHub did not return a published stable release.");
            var tag = release.GetProperty("tag_name").GetString();
            if (tag is null || tag.Length > 64 || !Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
                throw new InvalidDataException("The latest release tag is not a numeric version.");
            latest = Normalize(latest);
            result = new(Display(Installed), Display(latest), latest > Installed, clock.GetUtcNow(), null);
            logger.LogInformation("Server update check completed. Installed {Installed}; latest {Latest}", result.InstalledVersion, result.LatestVersion);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            var reason = ex is InvalidDataException ? ex.Message : "GitHub could not be reached or returned an invalid response.";
            logger.LogWarning(ex, "Could not check for server updates: {Reason}", reason);
            result = new(Display(Installed), null, false, clock.GetUtcNow(), reason);
        }
        lock (gate) cached = result;
        return result;
    }

    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    private static string Display(Version version) => version.Revision == 0 ? version.ToString(3) : version.ToString(4);
}
