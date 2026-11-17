using System.Globalization;
using System.Net;
using System.Text.Json;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record ProfessionalSupportStatus(Guid InstallationId, DateOnly? ValidUntil = null,
    DateTimeOffset? VerifiedAt = null, string? Error = null)
{
    public bool Active => ValidUntil is { } date && date >= DateOnly.FromDateTime(DateTime.UtcNow);
    public string Label => VerifiedAt is null ? UiText.Get(LocKeys.Ui.ProfessionalSupport.NotYetVerified) : ValidUntil is null
        ? UiText.Get(LocKeys.Ui.ProfessionalSupport.NoProfessionalSupportCoverageFound) : Active ? UiText.Get(LocKeys.Policy.Active) : UiText.Get(LocKeys.Ui.DeviceNotificationHistoryTab.Expired);

    public string? ExpirationNotice(DateOnly today)
    {
        if (VerifiedAt is null || ValidUntil is not { } date || date > today.AddMonths(1)) return null;
        var formatted = date.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
        return date < today ? UiText.Get(LocKeys.Ui.ProfessionalSupport.ProfessionalSupportExpiredOn0, formatted)
            : UiText.Get(LocKeys.Ui.ProfessionalSupport.ProfessionalSupportExpiresOn0EndOfDayUTC, formatted);
    }

    public static DateOnly ParseCoverage(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("validUntil", out var value) ||
            value.ValueKind != JsonValueKind.String ||
            !DateOnly.TryParseExact(value.GetString(), "MM-dd-yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            throw new InvalidDataException("Support validUntil must use MM-dd-yyyy.");
        return date;
    }
}

public sealed class ProfessionalSupport(IServiceScopeFactory scopes,
    IHttpClientFactory clients, IConfiguration configuration, ILogger<ProfessionalSupport> logger)
{
    public const string ClientName = "ProfessionalSupport";
    private const string IdentityKey = "Server.InstallationId";
    private const string CacheKey = "ProfessionalSupport.Coverage";
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private ProfessionalSupportStatus status = new(Guid.Empty);
    public ProfessionalSupportStatus Status => Volatile.Read(ref status);
    public string? Email => configuration["ProfessionalSupport:Email"]?.Trim();

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CloneDeployDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"AppSettings\" IN SHARE ROW EXCLUSIVE MODE", ct);
        var identity = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == IdentityKey, ct);
        if (identity is null)
        {
            identity = new AppSetting { Name = IdentityKey, Value = Guid.NewGuid().ToString("D") };
            db.AppSettings.Add(identity);
            await db.SaveChangesAsync(ct);
        }
        if (!Guid.TryParse(identity.Value, out var id) || id == Guid.Empty)
            throw new InvalidDataException("The stored server installation ID is invalid.");
        var cached = await db.AppSettings.Where(x => x.Name == CacheKey).Select(x => x.Value).SingleOrDefaultAsync(ct);
        status = new(id);
        if (cached is not null)
        {
            try
            {
                var previous = JsonSerializer.Deserialize<ProfessionalSupportStatus>(cached);
                if (previous?.InstallationId == id) status = previous;
            }
            catch (JsonException ex) { logger.LogWarning(ex, "Ignoring invalid support status cache"); }
        }
        await transaction.CommitAsync(ct);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!await refreshLock.WaitAsync(0, ct)) return;
        try
        {
            if (Status.InstallationId == Guid.Empty) return;
            using var client = clients.CreateClient(ClientName);
            using var response = await client.GetAsync($"http://clonedeploy.com/valid_support/{Status.InstallationId:D}.json", ct);
            DateOnly? validUntil = null;
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
                validUntil = ProfessionalSupportStatus.ParseCoverage(await response.Content.ReadAsStringAsync(ct));
            }
            var updated = new ProfessionalSupportStatus(Status.InstallationId, validUntil, DateTimeOffset.UtcNow);
            await using var scope = scopes.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CloneDeployDbContext>>();
            await using var db = await factory.CreateDbContextAsync(ct);
            var cache = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == CacheKey, ct);
            if (cache is null) db.AppSettings.Add(cache = new AppSetting { Name = CacheKey });
            cache.Value = JsonSerializer.Serialize(updated);
            await db.SaveChangesAsync(ct);
            Volatile.Write(ref status, updated);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not refresh professional support status");
            Volatile.Write(ref status, Status with { Error = "Unable to verify support coverage. Any previous result is shown below." });
        }
        finally { refreshLock.Release(); }
    }
}

public sealed class ProfessionalSupportWorker(ProfessionalSupport support, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        do { await support.RefreshAsync(stoppingToken); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
