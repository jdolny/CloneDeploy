using System.Runtime.Versioning;

namespace CloneDeploy_Server.WinPe;

/// <summary>Copies untrusted requests into a private workspace before any privileged execution.</summary>
[SupportedOSPlatform("windows")]
public sealed class ProtectedBuildRunner(BuildStorage queue, WinPeOptions options, string defaultPrivateFolder = "CloneDeploy-WinPeBuilder") : IDisposable
{
    private readonly object gate = new();
    private BuildStorage? workspace;
    private IDisposable? queueLease, workspaceLease;
    private FileStream? ownership, queueOwnership;
    private BuildEngine? engine;
    private BuildSetupEngine? setup;
    public BuildSetupEngine Setup { get { Initialize(); return setup!; } }

    public void Initialize()
    {
        lock (gate)
        {
            if (workspace is not null) return;
            BuilderSecurity.ValidateInstallation(AppContext.BaseDirectory);
            // Queue creation is only appropriate under an already protected, existing parent.
            if (!Directory.Exists(queue.Root))
            {
                BuilderSecurity.ValidateDirectory(Path.GetDirectoryName(queue.Root)!, options.QueueWriterSid);
                Directory.CreateDirectory(queue.Root);
            }
            BuilderSecurity.ValidateDirectory(queue.Root, options.QueueWriterSid);
            var root = string.IsNullOrWhiteSpace(options.PrivateRootPath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), defaultPrivateFolder)
                : options.PrivateRootPath;
            if (!Path.IsPathFullyQualified(root)) throw new InvalidOperationException("WinPe:PrivateRootPath must be absolute.");
            root = Path.GetFullPath(root);
            if (Within(root, queue.Root) || Within(queue.Root, root))
                throw new InvalidOperationException("Private WinPE storage must be separate from the incoming queue.");
            BuilderSecurity.CreatePrivateDirectory(root);
            IDisposable? incoming = null, privateLease = null;
            FileStream? claim = null, queueClaim = null;
            try
            {
                incoming = BuilderSecurity.PinDirectory(queue.Root);
                privateLease = BuilderSecurity.PinDirectory(root);
                claim = new FileStream(Path.Combine(root, "builder.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                BuilderSecurity.AsQueueWriterAsync(options.QueueWriterSid, () =>
                {
                    queueClaim = new FileStream(Path.Combine(queue.Root, "builder.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return Task.CompletedTask;
                }).GetAwaiter().GetResult();
                var storage = new BuildStorage(root);
                engine = new BuildEngine(storage, options);
                setup = new BuildSetupEngine(storage, engine);
                queueLease = incoming; workspaceLease = privateLease; ownership = claim; queueOwnership = queueClaim; workspace = storage;
            }
            catch { incoming?.Dispose(); privateLease?.Dispose(); claim?.Dispose(); queueClaim?.Dispose(); throw; }
        }
    }

    public BuilderHealthDto Health()
    {
        try
        {
            Initialize();
            var health = engine!.Health();
            if (Setup.BlocksBuilds) return health with { Ready = false, Message = "Prerequisite setup requires verification or a Windows restart." };
            if (Directory.EnumerateDirectories(workspace!.Root).Any(x => File.Exists(Path.Combine(x, "cleanup-required.txt"))))
                return health with { Ready = false, Message = "A private WinPE mount needs manual cleanup before building again." };
            return health;
        }
        catch (Exception ex) { return new(DateTime.UtcNow, false, "WinPE security check failed: " + ex.Message, null); }
    }

    public async Task Heartbeat(CancellationToken ct)
    {
        await queue.WriteAsync(Path.Combine(queue.Root, "health.json"), Health(), ct);
        await queue.WriteAsync(Path.Combine(queue.Root, "setup-health.json"), SetupHealth(), ct);
    }

    public SetupHealthDto SetupHealth()
    {
        try { return Setup.Health(); }
        catch { return new(DateTime.UtcNow, WinPeToolchainPolicy.Protocol, null, null, false, false)
            { RequiredVersion = ToolchainSpecDto.MicrosoftDefault.Version }; }
    }

    public async Task BuildAsync(BuildRequestDto request, CancellationToken ct)
    {
        Initialize();
        ValidateTools();
        WinPeToolchainPolicy.ValidateBuildVersion(request.RequiredToolchainVersion);
        if (request.Drivers is null || request.Drivers.Count > 256 || request.Drivers.Any(x => x.FileId <= 0) ||
            request.Drivers.Select(x => x.FileId).Distinct().Count() != request.Drivers.Count)
            throw new InvalidDataException("Invalid driver input list.");
        var id = request.Id;
        using var incoming = BuilderSecurity.PinDirectory(queue.Job(id));
        var directory = workspace!.Job(id);
        if (Directory.Exists(directory)) throw new InvalidOperationException("This build has an interrupted private workspace. Recover it before retrying.");
        Directory.CreateDirectory(directory);
        await workspace.WriteAsync(workspace.FilePath(id, "request.json"), request, ct);
        using var monitorStop = new CancellationTokenSource();
        var monitor = Monitor(id, false, monitorStop.Token);
        try
        {
            await workspace.SetStatus(id, "Running", "Copying inputs into the protected workspace.");
            await SnapshotInputsAsync(queue, workspace, request, ct);
            // All checksum validation and execution now operate exclusively on private copies.
            await engine!.BuildAsync(request, ct);
            var status = workspace.Status(id);
            foreach (var artifact in status.Artifacts)
                await Publish(workspace.FilePath(id, artifact.Name), queue.FilePath(id, artifact.Name), ct, options.QueueWriterSid);
        }
        catch (Exception ex)
        {
            await workspace.SetStatus(id, ct.IsCancellationRequested ? "Cancelled" : "Failed", ex.Message);
            throw;
        }
        finally
        {
            monitorStop.Cancel(); await monitor;
            await PublishProgress(id, false, true, CancellationToken.None);
            // Keep failed cleanup and interrupted publishing state for recovery; never delete a live mount.
            if (!File.Exists(workspace.FilePath(id, "cleanup-required.txt"))) DeletePrivateDirectory(directory);
        }
    }

    public static IReadOnlyList<string> InputNames(BuildRequestDto request)
    {
        var names = new List<string>();
        if (!request.PrepareMediaAutomatically && !request.IsBase)
        {
            names.Add("base.zip");
            if (request.BuildArm64) names.Add("base-arm64.zip");
        }
        names.AddRange(request.Drivers.Select(x => "driver-" + x.FileId + ".zip"));
        return names;
    }

    public static async Task SnapshotInputsAsync(BuildStorage queue, BuildStorage workspace, BuildRequestDto request, CancellationToken ct)
    {
        using var directory = BuilderSecurity.PinDirectory(queue.Job(request.Id));
        long total = 0;
        foreach (var name in InputNames(request))
        {
            await using var input = BuilderSecurity.OpenInput(queue.FilePath(request.Id, name));
            total = checked(total + input.Length);
            if (total > 40L * 1024 * 1024 * 1024) throw new InvalidDataException("WinPE inputs exceed the 40 GB snapshot limit.");
            var path = workspace.FilePath(request.Id, name);
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output, ct);
            var expected = name switch
            {
                "base.zip" => request.BaseSha256,
                "base-arm64.zip" => request.BaseArm64Sha256,
                _ => request.Drivers.Single(x => name == "driver-" + x.FileId + ".zip").Sha256
            };
            if (!string.Equals(expected, await BuildStorage.HashAsync(path, ct), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Private input checksum mismatch: " + name);
        }
    }

    public async Task RunSetupAsync(SetupRequestDto request, CancellationToken ct)
    {
        Initialize();
        BuilderSecurity.ValidateDirectory(queue.Root, options.QueueWriterSid);
        BuilderSecurity.ValidateDirectory(workspace!.Root);
        WinPeToolchainPolicy.ValidateRequest(request.Toolchain);
        using var incoming = BuilderSecurity.PinDirectory(SetupFiles.DirectoryPath(queue, request.Id));
        var directory = SetupFiles.DirectoryPath(workspace!, request.Id);
        if (Directory.Exists(directory)) throw new InvalidOperationException("Queue a new prerequisite setup after an interrupted attempt.");
        Directory.CreateDirectory(directory);
        using var monitorStop = new CancellationTokenSource();
        var monitor = Monitor(request.Id, true, monitorStop.Token);
        try { await Setup.RunAsync(request, ct); }
        catch (Exception ex) { await Setup.SetStatus(request.Id, "Failed", ex.Message); throw; }
        finally
        {
            monitorStop.Cancel(); await monitor;
            foreach (var name in new[] { "adk-install.log", "winpe-install.log" })
            {
                var file = SetupFiles.PathFor(workspace!, request.Id, name);
                if (File.Exists(file)) await Publish(file, SetupFiles.PathFor(queue, request.Id, name), CancellationToken.None, options.QueueWriterSid);
            }
            await PublishProgress(request.Id, true, true, CancellationToken.None);
            // Installer children can outlive a failed bootstrapper. Keep its private files for diagnosis.
            if (SetupFiles.Status(workspace!, request.Id).State == "Succeeded") DeletePrivateDirectory(directory);
        }
    }

    public async Task RecoverPendingAsync()
    {
        Initialize();
        foreach (var directory in Directory.EnumerateDirectories(queue.Root))
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out var queuedId) &&
                HasLegacyWorkspace(directory) && !queue.Status(queuedId).IsComplete)
                throw new InvalidOperationException("Legacy WinPE workspace requires administrator inspection and targeted mount cleanup: " + directory);
        // Recovery is driven by private state, not a server-writable status file.
        foreach (var directory in Directory.EnumerateDirectories(workspace!.Root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id)) continue;
            if (File.Exists(Path.Combine(directory, "cleanup-required.txt")))
                throw new InvalidOperationException("Private WinPE mount cleanup requires administrator attention: " + directory);
            await RecoverAsync(id);
        }
    }

    public async Task RecoverAsync(Guid id)
    {
        Initialize();
        ValidateTools();
        var directory = workspace!.Job(id);
        if (!Directory.Exists(directory))
        {
            if (HasLegacyWorkspace(queue.Job(id)))
                throw new InvalidOperationException("Legacy WinPE workspace requires administrator inspection and mount cleanup. Queue a new build afterward.");
            await queue.SetStatus(id, "Failed", "Build interrupted. Queue a new build.");
            return;
        }
        await engine!.RecoverAsync(id);
        Directory.CreateDirectory(queue.Job(id));
        await PublishProgress(id, false, true, CancellationToken.None);
        DeletePrivateDirectory(directory);
    }

    public bool CanDeleteQueueJob(Guid id) => workspace is not null && !Directory.Exists(workspace.Job(id)) &&
        !HasLegacyWorkspace(queue.Job(id)) && !File.Exists(queue.FilePath(id, "cleanup-required.txt"));

    private static bool HasLegacyWorkspace(string directory) =>
        new[] { "mount", "work", "mount-arm64", "work-arm64" }.Any(name => Directory.Exists(Path.Combine(directory, name)));

    private void ValidateTools()
    {
        BuilderSecurity.ValidateDirectory(queue.Root, options.QueueWriterSid);
        BuilderSecurity.ValidateDirectory(workspace!.Root);
        BuilderSecurity.ValidateInstallation(options.AdkPath);
    }

    private async Task Monitor(Guid id, bool isSetup, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                await PublishProgress(id, isSetup, false, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        // Publishing is not allowed to abandon a running privileged process or its cleanup.
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task PublishProgress(Guid id, bool isSetup, bool final, CancellationToken ct)
    {
        string Source(string name) => isSetup ? SetupFiles.PathFor(workspace!, id, name) : workspace!.FilePath(id, name);
        string Destination(string name) => isSetup ? SetupFiles.PathFor(queue, id, name) : queue.FilePath(id, name);
        var log = isSetup ? "setup.log" : "build.log";
        if (File.Exists(Source(log))) await Publish(Source(log), Destination(log), ct, options.QueueWriterSid);
        if (File.Exists(Source("cleanup-required.txt"))) await Publish(Source("cleanup-required.txt"), Destination("cleanup-required.txt"), ct, options.QueueWriterSid);
        if (isSetup)
        {
            var status = SetupFiles.Status(workspace!, id);
            if (final || !status.IsComplete) await queue.WriteAsync(Destination("status.json"), status, ct);
        }
        else
        {
            var status = workspace!.Status(id);
            if (final || !status.IsComplete) await queue.WriteAsync(Destination("status.json"), status, ct);
        }
    }

    public static async Task Publish(string source, string destination, CancellationToken ct, string? queueWriterSid = null)
    {
        using var parent = BuilderSecurity.PinDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await BuilderSecurity.AsQueueWriterAsync(queueWriterSid, async () =>
        {
            var pending = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await input.CopyToAsync(output, ct);
                // Replace the directory entry, never open/truncate an existing queue file (which could be a hard link).
                File.Move(pending, destination, true);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        });
    }

    private void DeletePrivateDirectory(string path)
    {
        if (!Within(path, workspace!.Root) || Path.GetFullPath(path) == workspace.Root) throw new InvalidOperationException("Invalid private cleanup path.");
        BuildStorage.RejectLinks(path);
        Directory.Delete(path, true);
    }
    private static bool Within(string path, string root) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public void Dispose() { queueOwnership?.Dispose(); ownership?.Dispose(); workspaceLease?.Dispose(); queueLease?.Dispose(); }
}
