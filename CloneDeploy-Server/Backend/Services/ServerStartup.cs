using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Services.Scheduler;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class ServerStartup(IServiceProvider services) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CloneDeployDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var knownMigrations = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var appliedMigrations = await db.Database.GetAppliedMigrationsAsync(cancellationToken);
        if (appliedMigrations.Any(id => !knownMigrations.Contains(id)))
            throw new InvalidOperationException("This database uses an unsupported development schema. This release requires a fresh database; existing data will not be converted or deleted automatically.");
        await db.Database.MigrateAsync(cancellationToken);
        await services.GetRequiredService<ProfessionalSupport>().InitializeAsync(cancellationToken);
        await services.GetRequiredService<WebCertificateRuntime>().LoadAsync(services);
        await services.GetRequiredService<AgentTrustRefresh>().RefreshAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<SchedulerAdministration>().InitializeScheduledTasksAsync();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
