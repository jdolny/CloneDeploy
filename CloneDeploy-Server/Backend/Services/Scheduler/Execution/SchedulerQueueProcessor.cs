using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CloneDeploy_Server.Backend.Services.Scheduler;

/// <summary>
/// Processes waiting scheduled tasks and records their outcomes.
/// </summary>
public sealed class SchedulerQueueProcessor(ICloneDeployDbFactory dbFactory, IScheduledTaskExecutor executor, TimeProvider clock, ILogger<SchedulerQueueProcessor> logger, IStringLocalizer<LocalizationResources> loc)
{
    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateContextAsync();

        var execution = await ClaimNextAsync(db, ct);

        if (execution is null)
            return false;

        await ExecuteAsync(db, execution, ct);

        return true;
    }

    private async Task<ScheduledTaskExecution?> ClaimNextAsync(CloneDeployDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        await MarkInterruptedExecutionsAsync(db, ct);

        var execution = await db.ScheduledTaskExecutions.Where(x => x.Status == ScheduledTaskStatus.Queued).OrderBy(x => x.QueuedAtUtc)
            .ThenBy(x => x.TaskId).ThenBy(x => x.Id).FirstOrDefaultAsync(ct);

        if (execution is null)
        {
            await transaction.CommitAsync(ct);
            return null;
        }

        execution.Status = ScheduledTaskStatus.Running;
        execution.StartedAtUtc = clock.GetUtcNow().UtcDateTime;

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return execution;
    }

    private Task<int> MarkInterruptedExecutionsAsync(CloneDeployDbContext db, CancellationToken ct)
    {
        //nothing should be running at this point since tasks can only run 1 at a time, if something is flagged as running, mark it interrupted 
        return db.ScheduledTaskExecutions.Where(x => x.Status == ScheduledTaskStatus.Running).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, ScheduledTaskStatus.Interrupted)
            .SetProperty(x => x.FinishedAtUtc, clock.GetUtcNow().UtcDateTime)
            .SetProperty(x => x.Summary, loc[ErrorKeys.Scheduler.Interrupted]), ct);
    }


    private async Task ExecuteAsync(CloneDeployDbContext db, ScheduledTaskExecution execution, CancellationToken stoppingToken)
    {
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

            cancellation.CancelAfter(TimeSpan.FromHours(1));

            var result = execution.TargetId.HasValue
                ? await executor.RunTargetAsync(execution.TaskId, execution.TargetId.Value, cancellation.Token)
                : await executor.RunAsync(execution.TaskId, cancellation.Token);

            execution.Status = result.Success ? ScheduledTaskStatus.Succeeded : ScheduledTaskStatus.Failed;
            execution.Summary = result.Summary;
        }
        catch (OperationCanceledException)
        {
            execution.Status = ScheduledTaskStatus.Interrupted;
            execution.Summary = loc[stoppingToken.IsCancellationRequested
                ? ErrorKeys.Scheduler.Interrupted
                : ErrorKeys.Scheduler.TimeLimitExceeded];
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Scheduler executor {ExecutionId} failed", execution.Id);

            execution.Status = ScheduledTaskStatus.Failed;
            execution.Summary = loc[ErrorKeys.Scheduler.Failed];
        }

        execution.FinishedAtUtc = clock.GetUtcNow().UtcDateTime;

        await SaveExecutionResultAsync(db, execution);
    }

    private static async Task SaveExecutionResultAsync(CloneDeployDbContext db, ScheduledTaskExecution execution)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await db.ScheduledTaskExecutions.Where(x => x.Id == execution.Id && x.Status == ScheduledTaskStatus.Running)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, execution.Status)
                    .SetProperty(x => x.Summary, execution.Summary)
                    .SetProperty(x => x.FinishedAtUtc, execution.FinishedAtUtc),
                cleanup.Token);
    }
}