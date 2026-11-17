using CloneDeploy_Server.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Refreshes enrollment admission settings without database reads on anonymous requests.</summary>
public sealed class AgentEnrollmentRateSettings(IServiceScopeFactory scopes,
    ILogger<AgentEnrollmentRateSettings> logger) : BackgroundService
{
    public const string Key = "Agent.EnrollmentRequestsPerMinute";
    public const int DefaultLimit = 30;
    public const int MaximumLimit = 10000;
    private int limit = DefaultLimit;
    public int RequestsPerMinute => Volatile.Read(ref limit);

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
        var value = await db.AppSettings.AsNoTracking().Where(x => x.Name == Key).Select(x => x.Value).SingleOrDefaultAsync(ct);
        Volatile.Write(ref limit, Parse(value));
    }

    internal static int Parse(string? value) => int.TryParse(value, out var parsed) && parsed is >= 1 and <= MaximumLimit
        ? parsed : DefaultLimit;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            do
            {
                try { await RefreshAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError(ex, "Could not refresh enrollment rate limit; retaining the previous limit"); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
