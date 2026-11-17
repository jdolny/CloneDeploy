using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services.Scheduler;

/// <summary>Evaluates global update approval rules through the shared scheduler.</summary>
public sealed class WindowsUpdateAutoApprovalTaskHandler(WindowsUpdateAutoApproval approvals,
    ILogger<WindowsUpdateAutoApprovalTaskHandler> logger) : IScheduledTaskHandler
{
    private const int CatalogTarget = 1;
    public ScheduledTaskType Type => ScheduledTaskType.WindowsUpdateAutoApproval;
    public string Name => "Windows Update auto-approval";
    public string Description => "Approves inventoried update revisions matching enabled global rules. Does not install updates.";
    public Task<List<int>> GetTargetsAsync(CancellationToken ct) => Task.FromResult(new List<int> { CatalogTarget });
    public Task<string?> GetTargetNameAsync(int targetId, CancellationToken ct) => Task.FromResult<string?>("Windows Update catalog");

    public async Task RunAsync(int targetId, CancellationToken ct)
    {
        if (targetId != CatalogTarget) throw new ArgumentOutOfRangeException(nameof(targetId));
        var count = await approvals.RunAsync(ct);
        logger.Log(count == 0 ? LogLevel.Debug : LogLevel.Information,
            "Automatically approved {Count} Windows Update revisions", count);
    }
}
