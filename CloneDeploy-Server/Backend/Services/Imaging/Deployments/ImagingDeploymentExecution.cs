using CloneDeploy_Agent.Contracts;
using System.Text.Json;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DeploymentExecutionDto(Guid DeploymentId, string Format, long SizeBytes, string Sha256, bool Pipable)
{
    public CloneDeploy.SiteServices.MulticastConnection? Multicast { get; init; }
    public string FfuTransport { get; init; } = "Smb";
    public string WimTransport { get; init; } = "Https";
    public DeploymentPerformanceOptions Performance { get; init; } = new();
    public List<DeploymentDriverArchiveDto> Drivers { get; init; } = [];
    public string? DriverWarning { get; init; }
    public string ComputerName { get; init; } = "";
    public bool ConfigureDomainJoinOu { get; init; }
    public bool SetComputerName { get; init; }
    public DeploymentCompletionAction AfterDeployment { get; init; }
}
public sealed record DeploymentFinishDto(bool Success)
{
    public bool? AgentInstalled { get; init; }
    [System.ComponentModel.DataAnnotations.StringLength(131072)]
    public string? Diagnostics { get; init; }
}

public sealed partial class ImagingDeploymentClaims
{
    private async Task<ImagingDeployment> RequireExecutionAsync(CloneDeployDbContext db, string? token, CancellationToken ct, bool completed = false)
    {
        var ticket = ReadTicket(token) ?? throw new UnauthorizedAccessException();
        var deployment = await db.Set<ImagingDeployment>().SingleOrDefaultAsync(x => x.Id == ticket.DeploymentId && x.DeviceId == ticket.DeviceId &&
            x.ImagerClaimId == ticket.ClaimId && x.ImagerClaimExpiresAtUtc > DateTime.UtcNow && x.CancelledAtUtc == null &&
            (completed || x.ImagerFailedAtUtc == null && x.ImagingCompletedAtUtc == null && x.SetupCompletedAtUtc == null) &&
            db.Devices.Any(d => d.Id == ticket.DeviceId && d.ArchivedAtUtc == null && d.ProvisionStatus != DeviceProvisionStatus.Archived), ct);
        if (deployment is null) throw new UnauthorizedAccessException();
        if (!completed) _ = await MulticastAsync(db, deployment, ct);
        return deployment;
    }

    public async Task<ImagingDeploymentSessionDto> RenewExecutionAsync(string? token, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ticket = ReadTicket(token) ?? throw new UnauthorizedAccessException();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(682947233, {ticket.DeviceId})", ct);
        var deployment = await RequireExecutionAsync(db, token, ct);
        deployment.ImagerClaimExpiresAtUtc = DateTime.UtcNow.AddSeconds(LifetimeSeconds);
        await DeploymentTaskTracking.SyncAsync(db, deployment, ct);
        await db.SaveChangesAsync(ct);
        var assignment = await AssignmentAsync(db, deployment, ct);
        await tx.CommitAsync(ct);
        if (readSelection is not null) await readSelection.RenewAsync(deployment.Id, TimeSpan.FromSeconds(LifetimeSeconds), ct);
        return new("Bearer", Prefix + _tokens.Protect(JsonSerializer.Serialize(ticket), TimeSpan.FromSeconds(LifetimeSeconds)), LifetimeSeconds, assignment);
    }

    public Task SetExecutionStateAsync(string? token, string operation, CancellationToken ct, string? diagnostics = null) =>
        SetExecutionStateAsync(token, operation, ct, diagnostics, null);

    public async Task SetExecutionStateAsync(string? token, string operation, CancellationToken ct, string? diagnostics, bool? agentInstalled)
    {
        if (diagnostics?.Length > 131072) throw new InvalidOperationException("Deployment diagnostics exceed the allowed size.");
        if (operation == "Started")
        {
            if (!(await EnterQueueAsync(token, ct)).Admitted)
                throw new InvalidOperationException("The deployment queue is full. Update Imager to wait for an imaging slot.");
            return;
        }
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ticket = ReadTicket(token) ?? throw new UnauthorizedAccessException();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(682947233, {ticket.DeviceId})", ct);
        if (operation is not ("Completed" or "Failed")) throw new ArgumentException("Invalid deployment state.");
        var deployment = await RequireExecutionAsync(db, token, ct, completed: true);
        if (deployment.ImagerStartedAtUtc == null) throw new InvalidOperationException("Deployment has not started.");
        if (deployment.ImagingCompletedAtUtc != null || deployment.ImagerFailedAtUtc != null)
        {
            if ((operation == "Completed") == (deployment.ImagingCompletedAtUtc != null)) return;
            throw new InvalidOperationException("Deployment already has a different final result.");
        }
        if (operation == "Completed")
        {
            _ = await MulticastAsync(db, deployment, ct);
            deployment.ImagingCompletedAtUtc = DateTime.UtcNow;
            var steps = await db.Set<ImagingDeploymentPolicy>().Where(x => x.ImagingDeploymentId == deployment.Id).ToListAsync(ct);
            if (steps.Count > 0 && agentInstalled is null)
                throw new InvalidOperationException("Agent detection is required for post-deployment policies. Update Imager and retry completion.");
            if (agentInstalled == false)
            {
                deployment.SetupReadinessMessage = "Imaging completed. CloneDeploy agent was not detected; post-deployment policies were skipped.";
                foreach (var step in steps)
                {
                    step.Result = (int)CloneDeploy_Agent.Contracts.PolicyRunResult.Skipped;
                    step.FinishedAtUtc = deployment.ImagingCompletedAtUtc;
                }
            }
            if (steps.Count == 0 || agentInstalled == false) deployment.SetupCompletedAtUtc = deployment.ImagingCompletedAtUtc;
        }
        else if (operation == "Failed") deployment.ImagerFailedAtUtc = DateTime.UtcNow;
        else throw new ArgumentException("Invalid deployment state.");
        var settings = JsonSerializer.Deserialize<DeploymentSettingsDto>(deployment.SettingsJson)!;
        db.DeviceImagingLogs.Add(new DeviceImagingLog { DeviceId = deployment.DeviceId, Type = "Imager", SubType = "Deployment " + operation,
            Contents = $"Deployment {deployment.Id:D}: {operation}." +
                (string.IsNullOrWhiteSpace(settings.DriverWarning) ? "" : "\n\nDriver warning: " + settings.DriverWarning) +
                (operation == "Completed" && agentInstalled == false ? "\n\n" + deployment.SetupReadinessMessage : "") +
                (string.IsNullOrWhiteSpace(diagnostics) ? "" : "\n\nImager diagnostics (including DISM):\n" + diagnostics) });
        await DeploymentTaskTracking.SyncAsync(db, deployment, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (readSelection is not null) await readSelection.ReleaseAsync(deployment.Id, ct);
    }

    public async Task ReportExecutionProgressAsync(string? token, ImagingProgressDto progress, CancellationToken ct)
    {
        if (progress.Phase is < 0 or > 2 || progress.Percent is { } percent && (!double.IsFinite(percent) || percent < 0 || percent > 100) ||
            progress.NetworkBytesPerSecond is { } rate && (!double.IsFinite(rate) || rate < 0 || rate > 1e12) ||
            progress.StatusMessage?.Length > 512)
            throw new InvalidOperationException("Invalid deployment progress.");
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ticket = ReadTicket(token) ?? throw new UnauthorizedAccessException();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(682947233, {ticket.DeviceId})", ct);
        var deployment = await RequireExecutionAsync(db, token, ct);
        var task = await DeploymentTaskTracking.SyncAsync(db, deployment, ct);
        task.Rate = progress.NetworkBytesPerSecond is { } bytes
            ? FormattableString.Invariant($"{bytes / 1048576:0.0} MiB/s ({bytes * 60 / 1073741824:0.00} GiB/min)") : null;
        if (progress.Phase >= task.Phase) task.StatusMessage = progress.StatusMessage;
        if (progress.Phase > task.Phase) { task.Phase = progress.Phase; task.ProgressPercent = null; }
        if (progress.Phase == task.Phase && progress.Percent is { } value) task.ProgressPercent = Math.Max(task.ProgressPercent ?? 0, Math.Min(99, value));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (readSelection is not null) await readSelection.RenewAsync(deployment.Id, TimeSpan.FromSeconds(LifetimeSeconds), ct);
    }

    private async Task<int> ReserveExecutionStorageAsync(ImagingDeployment deployment, ImageVersion version, CancellationToken ct)
    {
        PinExecutionStorage(deployment.Id);
        if (readSelection is null) return version.StorageProviderId;
        await using var db = await factory.CreateContextAsync();
        var providers = await ImageReplicaCatalog.Providers(db, version).Select(x => x.Id).ToListAsync(ct);
        // A withdrawn copy remains usable only by a transfer already pinned to it.
        var pinned = await db.Set<StorageReadLease>().Where(x => x.Id == deployment.Id && x.ExpiresAtUtc > DateTime.UtcNow)
            .Select(x => (int?)x.StorageProviderId).SingleOrDefaultAsync(ct);
        if (pinned is { } provider && await db.Set<ImageReplica>().AnyAsync(x => x.ImageVersionId == version.Id &&
            x.StorageProviderId == provider && (x.State == ImageReplicaState.Ready || x.State == ImageReplicaState.Deleting), ct))
            providers.Add(provider);
        return await readSelection.ReserveAsync(deployment.Id, $"image:{version.Id:N}", StoragePurpose.Imaging,
            providers, requests?.HttpContext?.Connection.RemoteIpAddress,
            TimeSpan.FromSeconds(LifetimeSeconds), ct);
    }

    private void PinExecutionStorage(Guid deployment)
    {
        if (requests?.HttpContext is { } context)
        {
            context.Items[SiteDownloadDelivery.TransferKey] = deployment;
            context.Items[SiteDownloadDelivery.PreserveLeaseKey] = true;
        }
    }

    private static async Task<ImageVersion> ExecutionVersionAsync(CloneDeployDbContext db, ImagingDeployment deployment, CancellationToken ct)
    {
        var version = await db.ImageVersions.SingleAsync(x => x.Id == deployment.ImageVersionId, ct);
        if (version.SizeBytes <= 0 || version.Sha256 is not { Length: 64 } ||
            !version.Sha256.All(Uri.IsHexDigit) || version.Format is not (ImageFormat.Wim or ImageFormat.Ffu) ||
            version.FileName != (version.Format == ImageFormat.Wim ? "image.wim" : "image.ffu") ||
            version.Layout != (version.Format == ImageFormat.Wim ? "WindowsPartition" : "WholeDisk"))
            throw new InvalidOperationException("The assigned image lacks supported capture metadata.");
        if (version.Format == ImageFormat.Wim && version.Pipable != true)
            throw new InvalidOperationException("HTTPS deployment requires a pipable WIM captured by Imager.");
        return version;
    }

    private static string ExecutionFile(StorageContentStorage storage, ImageVersion version, int providerId)
    {
        var root = storage.GetRoot(providerId);
        var file = Path.Combine(root, "Images", version.ImageId.ToString(), version.Id.ToString("N"), version.FileName!);
        for (var path = file; !string.Equals(path, root, StringComparison.OrdinalIgnoreCase); path = Path.GetDirectoryName(path)!)
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Image paths cannot contain links below the storage root.");
        if (new FileInfo(file).Length != version.SizeBytes) throw new IOException("Stored image size no longer matches its published version.");
        return file;
    }

    public async Task<Dictionary<string, string>> ResolveSysprepCredentialsAsync(string? token, string ticket,
        ImagePrepCredentialService credentials, ISecretProtector secrets, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var deployment = await RequireExecutionAsync(db, token, ct);
        if (deployment.ImagerStartedAtUtc is null) throw new InvalidOperationException("Start the deployment before resolving Sysprep credentials.");
        if (string.IsNullOrWhiteSpace(ticket) || ticket.Length > 32768) throw new UnauthorizedAccessException();
        return await credentials.ResolveAsync(db, ticket, secrets, ct);
    }

    public async Task<DeploymentExecutionDto> ExecutionAsync(string? token, StorageContentStorage storage, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var deployment = await RequireExecutionAsync(db, token, ct);
        var version = await ExecutionVersionAsync(db, deployment, ct);
        var multicast = await MulticastAsync(db, deployment, ct);
        if (multicast is not null && (multicast.State != MulticastState.Waiting || multicast.StartRequested))
            throw new InvalidOperationException("Multicast transmission has already started; this device cannot join late.");
        var providerId = multicast?.StorageProviderId ?? await ReserveExecutionStorageAsync(deployment, version, ct);
        var provider = await db.StorageProviders.SingleAsync(x => x.Id == providerId, ct);
        if (provider.SiteServiceId is null)
        {
            await storage.EnsureReadyAsync(providerId, ct);
            _ = ExecutionFile(storage, version, providerId);
        }
        var settings = JsonSerializer.Deserialize<DeploymentSettingsDto>(deployment.SettingsJson)!;
        return new(deployment.Id, version.Format!.Value.ToString(), version.SizeBytes, version.Sha256!, version.Pipable == true)
        {
            Multicast = multicast is null ? null : new(multicast.Id, multicast.SenderAddress, multicast.GroupAddress, multicast.Port),
            FfuTransport = provider?.Type == StorageProviderType.Smb && provider.ImagingDirectConnect ? "Smb" : "Https",
            WimTransport = multicast is not null ? "Multicast" : provider?.Type == StorageProviderType.Smb && provider.ImagingDirectConnect ? "Smb" : "Https",
            Performance = await DeploymentPerformanceOptions.ReadAsync(db, ct),
            Drivers = settings.Drivers,
            DriverWarning = settings.DriverWarning,
            ComputerName = settings.ComputerName,
            SetComputerName = settings.SetComputerName,
            AfterDeployment = settings.AfterDeployment,
            ConfigureDomainJoinOu = settings.OuNamingRuleSetId.HasValue || settings.OuPrompt != DeploymentOuPrompt.Never,
        };
    }

    public async Task<(Stream Stream, DeploymentExecutionDto Image)> OpenExecutionFfuAsync(string? token, StorageContentStorage storage, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var deployment = await RequireExecutionAsync(db, token, ct);
        if (deployment.ImagerStartedAtUtc is null) throw new InvalidOperationException("Wait for an imaging slot before reading FFU content.");
        var version = await ExecutionVersionAsync(db, deployment, ct);
        if (version.Format != ImageFormat.Ffu) throw new InvalidOperationException("FFU range reads require an FFU deployment.");
        PinExecutionStorage(deployment.Id);
        if (Site is not null && await Site.TryOpenAsync("image-" + version.Id.ToString("N"), version.SizeBytes, version.Sha256!, StoragePurpose.Imaging, ct) is { } remote)
            return (remote, new(deployment.Id, "Ffu", version.SizeBytes, version.Sha256!, false));
        var providerId = await ReserveExecutionStorageAsync(deployment, version, ct);
        await storage.EnsureReadyAsync(providerId, ct);
        var stream = new FileStream(ExecutionFile(storage, version, providerId), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (stream.Length != version.SizeBytes) { await stream.DisposeAsync(); throw new IOException("FFU size changed."); }
        return (stream, new(deployment.Id, "Ffu", version.SizeBytes, version.Sha256!, false));
    }

    public async Task<SmbCaptureDto> ExecutionSmbAsync(string? token, ISecretProtector secrets, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var deployment = await RequireExecutionAsync(db, token, ct);
        var version = await ExecutionVersionAsync(db, deployment, ct);
        var providerId = await ReserveExecutionStorageAsync(deployment, version, ct);
        var provider = await db.StorageProviders.Include(x => x.ManagedCredential).SingleAsync(x => x.Id == providerId, ct);
        if (!provider.IsEnabled || provider.Type != StorageProviderType.Smb || !provider.ImagingDirectConnect)
            throw new InvalidOperationException("Direct deployment requires an enabled SMB provider with Imaging direct connect.");
        if (provider.ManagedCredential is not { } credential || string.IsNullOrWhiteSpace(credential.Username))
            throw new InvalidOperationException("The assigned SMB provider requires a managed credential.");
        return new(provider.ImagingSmbPath, $"Images\\{version.ImageId}\\{version.Id:N}\\{version.FileName}", credential.Username, secrets.Unprotect(credential.PasswordEncrypted));
    }

    public async Task<Stream> OpenExecutionImageAsync(string? token, StorageContentStorage storage, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var deployment = await RequireExecutionAsync(db, token, ct);
        var version = await ExecutionVersionAsync(db, deployment, ct);
        if (version.Format != ImageFormat.Wim) throw new InvalidOperationException("FFU deployment requires direct SMB access.");
        PinExecutionStorage(deployment.Id);
        if (Site is not null && await Site.TryOpenAsync("image-" + version.Id.ToString("N"), version.SizeBytes, version.Sha256!, StoragePurpose.Imaging, ct) is { } remote)
            return remote;
        var providerId = await ReserveExecutionStorageAsync(deployment, version, ct);
        await storage.EnsureReadyAsync(providerId, ct);
        return new FileStream(ExecutionFile(storage, version, providerId), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
}
