using System.Text.Json;
using CloneDeploy.SiteServices;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.WinPe;
using Microsoft.EntityFrameworkCore;
namespace CloneDeploy_Server.Backend.Services;

// Returned artifacts enter the existing local spool; WinPeTransferWorker remains the publication/storage authority.
public sealed class WindowsWinPeBridge(WindowsWorkQueue queue, IServiceScopeFactory scopes, BuildStorage storage,
    ILogger<WindowsWinPeBridge> logger) : BackgroundService
{
    public async Task HealthAsync(Guid site, WindowsWorkerHealth health, CancellationToken ct)
    {
        if (await queue.HostAsync(WindowsWorkerRoles.WinPe, ct) != site) throw new UnauthorizedAccessException();
        var build = JsonSerializer.Deserialize<BuilderHealthDto>(health.BuildHealth ?? "null") ?? throw new InvalidDataException("Missing builder health.");
        var setup = JsonSerializer.Deserialize<SetupHealthDto>(health.SetupHealth ?? "null") ?? throw new InvalidDataException("Missing setup health.");
        if (!WinPeToolchainPolicy.SupportsApplication(setup)) throw new InvalidDataException("Update the Windows worker to this main application release.");
        await storage.WriteAsync(Path.Combine(storage.Root, "health.json"), build with { SeenAtUtc = DateTime.UtcNow }, ct);
        await storage.WriteAsync(Path.Combine(storage.Root, "setup-health.json"), setup with { SeenAtUtc = DateTime.UtcNow }, ct);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await queue.CleanupAsync(stoppingToken);
                var host = await queue.HostAsync(WindowsWorkerRoles.WinPe, stoppingToken);
                Directory.CreateDirectory(storage.Root);
                var marker = Path.Combine(storage.Root, "remote-worker");
                if (host is not null) await File.WriteAllTextAsync(marker, host.ToString(), stoppingToken);
                else if (File.Exists(marker)) File.Delete(marker);
                if (host is not null)
                {
                    using var scope = scopes.CreateScope(); await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
                    var setups = await db.WinPeSetupJobs.AsNoTracking().OrderBy(x => x.CreatedAtUtc).ToListAsync(stoppingToken);
                    foreach (var job in setups)
                    {
                        var request = JsonSerializer.Deserialize<SetupRequestDto>(job.RequestJson)!;
                        if (!request.RemoteExecution && SetupFiles.Status(storage, job.Id).State == "Queued")
                        {
                            request = request with { RemoteExecution = true }; job.RequestJson = JsonSerializer.Serialize(request);
                            await db.WinPeSetupJobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.RequestJson, job.RequestJson), stoppingToken);
                            await storage.WriteAsync(SetupFiles.PathFor(storage, job.Id, "request.json"), request, stoppingToken);
                        }
                        if (request.RemoteExecution && !SetupFiles.Status(storage, job.Id).IsComplete)
                            await Run(job.Id, "WinPeSetup", job.RequestJson, new Dictionary<string, string>(), stoppingToken);
                    }
                    var builds = await db.WinPeBuilds.AsNoTracking().Where(x => x.StoredAtUtc == null).OrderBy(x => x.CreatedAtUtc).ToListAsync(stoppingToken);
                    foreach (var job in builds)
                    {
                        var request = JsonSerializer.Deserialize<BuildRequestDto>(job.RequestJson)!;
                        if (!request.RemoteExecution && storage.Status(job.Id).State == "Queued")
                        {
                            request = request with { RemoteExecution = true }; job.RequestJson = JsonSerializer.Serialize(request);
                            await db.WinPeBuilds.Where(x => x.Id == job.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.RequestJson, job.RequestJson), stoppingToken);
                            await storage.WriteAsync(storage.FilePath(job.Id, "request.json"), request, stoppingToken);
                        }
                        if (!request.RemoteExecution || storage.Status(job.Id).IsComplete) continue;
                        var inputs = new Dictionary<string, string>();
                        foreach (var name in request.Drivers.Select(x => "driver-" + x.FileId + ".zip")
                            .Concat(request.IsBase || request.PrepareMediaAutomatically ? [] : new[] { "base.zip", "base-arm64.zip" })
                            .Distinct())
                            inputs.Add(name, storage.FilePath(job.Id, name));
                        await Run(job.Id, "WinPe", job.RequestJson, inputs, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Windows build dispatch will retry"); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
    private async Task Run(Guid id, string kind, string request, Dictionary<string, string> inputs, CancellationToken ct)
    {
        var build = kind == "WinPe";
        using var claim = new FileStream(build ? storage.FilePath(id, "execution.lock") : SetupFiles.PathFor(storage, id, "execution.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? monitor = null;
        try
        {
            if (build && File.Exists(storage.FilePath(id, "cancel"))) { await storage.SetStatus(id, "Cancelled", "Build cancelled."); return; }
            await queue.EnqueueAsync(kind, request, inputs, ct, id);
            if (build) await storage.SetStatus(id, "Running", "Waiting for the Windows site worker.");
            else await storage.WriteAsync(SetupFiles.PathFor(storage, id, "status.json"), new SetupStatusDto("Running", "Waiting for the Windows site worker.", DateTime.UtcNow), ct);
            monitor = Monitor();
            var result = await queue.WaitAsync(id, ct);
            monitorStop.Cancel(); await monitor; monitor = null;
            var files = JsonSerializer.Deserialize<WindowsWorkFile[]>(result.OutputsJson)!;
            if (build)
            {
                var status = JsonSerializer.Deserialize<BuildStatusDto>(result.Result!) ?? throw new InvalidDataException("Missing build status.");
                var original = JsonSerializer.Deserialize<BuildRequestDto>(request)!;
                if (status.State != "Ready" || WinPeBootFiles.RequiredOutputs(original).Any(name => !files.Any(x => x.Name == name)))
                    throw new InvalidDataException("Windows worker did not return all required build outputs.");
                var allowed = WinPeBootFiles.RequiredOutputs(original).Concat(WinPeBootFiles.NativeOutputs).Concat(WinPeBootFiles.Arm64NativeOutputs).Append("build.log").ToHashSet(StringComparer.Ordinal);
                foreach (var file in files)
                {
                    if (!allowed.Contains(file.Name)) throw new InvalidDataException("Unexpected build output.");
                    await using var input = File.OpenRead(queue.Output(id, result.Lease, file.Name));
                    await WindowsWorkFiles.CopyAsync(input, storage.FilePath(id, file.Name), file, ct);
                }
                await storage.SetStatus(id, "Ready", "Build completed on the Windows site worker.", files.Where(x => x.Name != "build.log").Select(x => new BuildArtifactDto(x.Name, x.Length, x.Sha256)).ToList());
            }
            else
            {
                var status = JsonSerializer.Deserialize<SetupStatusDto>(result.Result!) ?? throw new InvalidDataException("Missing setup status.");
                if (!status.IsComplete) throw new InvalidDataException("Incomplete setup result.");
                foreach (var file in files)
                {
                    if (file.Name != "setup.log") throw new InvalidDataException("Unexpected setup output.");
                    await using var input = File.OpenRead(queue.Output(id, result.Lease, file.Name));
                    await WindowsWorkFiles.CopyAsync(input, SetupFiles.PathFor(storage, id, file.Name), file, ct);
                }
                await storage.WriteAsync(SetupFiles.PathFor(storage, id, "status.json"), status, ct);
            }
            await queue.ForgetAsync(id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            monitorStop.Cancel(); if (monitor is not null) { await monitor; monitor = null; }
            try
            {
                var failed = await queue.ReadAsync(id, default);
                var name = build ? "build.log" : "setup.log";
                var log = failed is null ? null : JsonSerializer.Deserialize<WindowsWorkFile[]>(failed.OutputsJson)!.SingleOrDefault(x => x.Name == name);
                if (log is not null)
                {
                    await using var input = File.OpenRead(queue.Output(id, failed!.Lease, name));
                    await WindowsWorkFiles.CopyAsync(input, build ? storage.FilePath(id, name) : SetupFiles.PathFor(storage, id, name), log, default);
                }
            }
            catch (Exception logError) { logger.LogWarning(logError, "Could not retrieve Windows worker log for {JobId}", id); }
            if (build) await storage.SetStatus(id, File.Exists(storage.FilePath(id, "cancel")) ? "Cancelled" : "Failed", ex.Message);
            else await storage.WriteAsync(SetupFiles.PathFor(storage, id, "status.json"), new SetupStatusDto("Failed", ex.Message, DateTime.UtcNow));
            await queue.CancelAsync(id);
        }
        finally { monitorStop.Cancel(); if (monitor is not null) await monitor; }
        async Task Monitor()
        {
            try
            {
                while (!monitorStop.IsCancellationRequested)
                {
                    if (build && File.Exists(storage.FilePath(id, "cancel"))) await queue.CancelAsync(id, monitorStop.Token);
                    using var scope = scopes.CreateScope(); await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
                    var progress = await db.Set<WindowsWorkerJob>().Where(x => x.Id == id).Select(x => x.Progress).SingleOrDefaultAsync(monitorStop.Token);
                    if (progress is not null)
                    {
                        if (build && !storage.Status(id).IsComplete) await storage.SetStatus(id, "Running", progress);
                        else if (!build && !SetupFiles.Status(storage, id).IsComplete) await storage.WriteAsync(SetupFiles.PathFor(storage, id, "status.json"), new SetupStatusDto("Running", progress, DateTime.UtcNow), monitorStop.Token);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), monitorStop.Token);
                }
            }
            catch (OperationCanceledException) when (monitorStop.IsCancellationRequested) { }
        }
    }
}
