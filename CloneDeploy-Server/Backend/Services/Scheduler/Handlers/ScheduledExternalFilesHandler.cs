using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services.Scheduler;

/// <summary>
/// Coordinates scheduled refreshes of action files and driver packages.
/// </summary>
public sealed class ScheduledExternalFilesHandler(ExternalFileRefreshTaskHandler actions, DriverRefreshService drivers) : IScheduledTaskHandler
{
    public ScheduledTaskType Type => actions.Type;
    public string Name => actions.Name;
    public string Description => "Checks external action files and driver profile download schedules.";
    public async Task<List<int>> GetTargetsAsync(CancellationToken ct) =>
        (await actions.GetTargetsAsync(ct)).Union(await drivers.GetTargetsAsync(ct)).Order().ToList();
    public Task<string?> GetTargetNameAsync(int targetId, CancellationToken ct) => actions.GetTargetNameAsync(targetId, ct);
    public async Task RunAsync(int targetId, CancellationToken ct)
    {
        await actions.RunAsync(targetId, ct);
        await drivers.RunAsync(targetId, ct);
    }
}
