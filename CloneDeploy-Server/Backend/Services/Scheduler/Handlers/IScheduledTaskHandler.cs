using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services.Scheduler;

public interface IScheduledTaskHandler
{
    ScheduledTaskType Type { get; }
    string Name { get; }
    string Description { get; }
    Task<List<int>> GetTargetsAsync(CancellationToken ct);
    Task<string?> GetTargetNameAsync(int targetId, CancellationToken ct);
    Task RunAsync(int targetId, CancellationToken ct);
}