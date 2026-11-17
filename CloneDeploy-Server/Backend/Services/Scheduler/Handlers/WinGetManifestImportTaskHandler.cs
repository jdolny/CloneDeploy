using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services.Scheduler;

/// <summary>
/// Refreshes the WinGet software catalog when requested by the scheduler.
/// </summary>
public sealed class WinGetManifestImportTaskHandler(WinGetManifestImporter importer) : IScheduledTaskHandler
{
    public ScheduledTaskType Type => ScheduledTaskType.WinGetManifestImport;
    public string Name => "Import WinGet manifests";
    public string Description => "Refreshes the WinGet package catalog from Microsoft’s winget-pkgs repository.";
    public Task<List<int>> GetTargetsAsync(CancellationToken ct) => Task.FromResult(new List<int> { 1 });
    public Task<string?> GetTargetNameAsync(int targetId, CancellationToken ct) => Task.FromResult<string?>("WinGet catalog");
    public Task RunAsync(int targetId, CancellationToken ct)
    {
        if (targetId != 1) throw new ArgumentOutOfRangeException(nameof(targetId));
        return importer.RunAsync(ct);
    }
}
