namespace CloneDeploy_Server.Backend.Services.Scheduler;

/// <summary>Runs a complete pass with shared dependency ordering. Null errors indicate success.</summary>
public interface IScheduledBatchTaskHandler : IScheduledTaskHandler
{
    Task<IReadOnlyDictionary<int, string?>> RunAllAsync(CancellationToken ct);
}
