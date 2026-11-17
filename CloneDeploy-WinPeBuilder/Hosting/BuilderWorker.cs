using CloneDeploy_Server.WinPe;

sealed class BuilderWorker(BuildStorage storage, ProtectedBuildRunner engine, WinPeOptions options, ILogger<BuilderWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private const int MinimumBuildTimeoutMinutes = 1;
    private const int MaximumBuildTimeoutMinutes = 1440;
    private const string SetupDirectoryPrefix = "setup-";

    private BuilderHealthDto CurrentHealth() => engine.Health();
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { engine.Initialize(); await engine.RecoverPendingAsync(); }
        catch (Exception ex)
        {
            logger.LogError(ex, "WinPE builder startup validation/recovery failed; no jobs will be executed");
            // Keep the actionable failure visible in Builder Setup until an administrator fixes it
            // and restarts the service. Never silently repair permissions or abandon a mount.
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await storage.WriteAsync(Path.Combine(storage.Root, "health.json"),
                    new BuilderHealthDto(DateTime.UtcNow, false, ex.Message + " Fix the problem and restart the builder service.", null), stoppingToken); }
                catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException or InvalidDataException) { }
                await Task.Delay(PollInterval, stoppingToken);
            }
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            if (File.Exists(Path.Combine(storage.Root, "remote-worker"))) { await Task.Delay(PollInterval, stoppingToken); continue; }
            var health = CurrentHealth();
            await storage.WriteAsync(Path.Combine(storage.Root, "health.json"), health, stoppingToken);
            await engine.Heartbeat(stoppingToken);
            // One worker holds builder.lock for both operation types. Setup is never concurrent with DISM builds.
            foreach (var directory in Directory.EnumerateDirectories(storage.Root, SetupDirectoryPrefix + "*"))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory)[SetupDirectoryPrefix.Length..], "N", out var setupId)) continue;
                using var setupDirectory = BuilderSecurity.PinDirectory(directory);
                if (storage.Read<SetupRequestDto>(SetupFiles.PathFor(storage, setupId, "request.json"))?.RemoteExecution == true) continue;
                var state = SetupFiles.Status(storage, setupId);
                if (state.IsComplete) continue;
                if (state.State != "Queued") { await SetSetupFailure(setupId, "Builder stopped during prerequisite setup. Inspect the logs and queue setup again to verify or finish installation."); continue; }
                var request = storage.Read<SetupRequestDto>(SetupFiles.PathFor(storage, setupId, "request.json"));
                if (request is null || request.Id != setupId || request.RemoteExecution) continue;
                using var monitorStop = new CancellationTokenSource();
                var monitor = SetupMonitor(monitorStop.Token);
                try
                {
                    // Recover interrupted images first; never change toolchains beneath a mounted workspace.
                    if (Directory.EnumerateDirectories(storage.Root).Any(x => Guid.TryParseExact(Path.GetFileName(x), "N", out var buildId) &&
                        (storage.Status(buildId).State == "Running" || File.Exists(storage.FilePath(buildId, "cleanup-required.txt"))))) break;
                    await engine.RunSetupAsync(request, stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Prerequisite setup {Id} failed", setupId);
                    await SetSetupFailure(setupId, ex is OperationCanceledException ? "Prerequisite setup interrupted. Inspect logs before retrying." : ex.Message);
                }
                finally { monitorStop.Cancel(); await monitor; }
                health = CurrentHealth();
            }
            foreach (var directory in Directory.EnumerateDirectories(storage.Root).OrderBy(Directory.GetCreationTimeUtc))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id)) continue;
                using var buildDirectory = BuilderSecurity.PinDirectory(directory);
                FileStream claim;
                try { claim = new FileStream(storage.FilePath(id, "execution.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { continue; }
                using var jobOwnership = claim;
                if (storage.Read<BuildRequestDto>(storage.FilePath(id, "request.json"))?.RemoteExecution == true) continue;
                var state = storage.Status(id);
                if (state.State == "Running")
                {
                    if (!engine.Health().Ready) continue;
                    await engine.RecoverAsync(id);
                    state = storage.Status(id);
                }
                if (File.Exists(storage.FilePath(id, "cleanup-required.txt")))
                    throw new InvalidOperationException("Build mount cleanup requires attention: " + directory);
                if (state.State != "Queued") continue;
                var request = storage.Read<BuildRequestDto>(storage.FilePath(id, "request.json"));
                if (request is null || request.Id != id || request.RemoteExecution) continue;
                if (File.Exists(storage.FilePath(id, "cancel"))) { await storage.SetStatus(id, "Cancelled", "Cancelled before execution."); continue; }
                if (!health.Ready) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(options.TimeoutMinutes, MinimumBuildTimeoutMinutes, MaximumBuildTimeoutMinutes)));
                using var monitorStop = new CancellationTokenSource();
                var monitor = Monitor(id, timeout, monitorStop.Token);
                try { await engine.BuildAsync(request, timeout.Token); }
                catch (OperationCanceledException)
                {
                    var cancelled = File.Exists(storage.FilePath(id, "cancel"));
                    await storage.SetStatus(id, cancelled ? "Cancelled" : "Failed", cancelled ? "Build cancelled." : "Build interrupted or exceeded its time limit.");
                }
                catch (Exception ex) { logger.LogError(ex, "WinPE build {Id} failed", id); await storage.SetStatus(id, "Failed", ex.Message); }
                finally { monitorStop.Cancel(); await monitor; }
                if (File.Exists(storage.FilePath(id, "cleanup-required.txt")))
                    throw new InvalidOperationException("Build mount cleanup requires attention: " + directory);
            }
            await Task.Delay(PollInterval, stoppingToken);
        }
    }
    private async Task Monitor(Guid id, CancellationTokenSource build, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (File.Exists(storage.FilePath(id, "cancel"))) build.Cancel();
                await storage.WriteAsync(Path.Combine(storage.Root, "health.json"), CurrentHealth(), stop);
                await engine.Heartbeat(stop);
                await Task.Delay(PollInterval, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    private async Task SetupMonitor(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await engine.Heartbeat(stop);
                await storage.WriteAsync(Path.Combine(storage.Root, "health.json"), CurrentHealth(), stop);
                await Task.Delay(PollInterval, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    private Task SetSetupFailure(Guid id, string message) => storage.WriteAsync(SetupFiles.PathFor(storage, id, "status.json"), new SetupStatusDto("Failed", message, DateTime.UtcNow));
}
