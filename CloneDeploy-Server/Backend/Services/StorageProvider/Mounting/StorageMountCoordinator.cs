using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Principal;
using CloneDeploy.SiteServices;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Connects storage locations and checks whether they are ready to use.
/// </summary>
public sealed class StorageMountCoordinator(IServiceScopeFactory scopes, StorageMountPaths paths, ILogger<StorageMountCoordinator> logger)
{
    private readonly ConcurrentDictionary<int, (string Path, string Unc)> _linuxMounts = new();
    public void VerifyLinuxMount(int id)
    { if (_linuxMounts.TryGetValue(id, out var mount)) LinuxSmbMount.Verify(mount.Path, mount.Unc); }

    private readonly ConcurrentDictionary<int, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<int, StorageMountStatusDto> _status = new();
    public string RootPath => paths.RootPath;
    public string ProcessIdentity
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return Environment.UserName;
            using var identity = WindowsIdentity.GetCurrent();
            return identity.Name;
        }
    }
    public List<StorageMountStatusDto> GetStatuses() => _status.Values.OrderBy(x => x.ProviderId).ToList();

    public async Task CheckAllAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
        var ids = await db.StorageProviders.Select(x => x.Id).ToListAsync(ct);
        var allIds = ids.Concat(paths.OwnedProviderIds()).Concat(_status.Keys).Distinct().ToArray();
        await Task.WhenAll(allIds.Select(id => CheckAsync(id, ct)));
    }

    public Task CheckAsync(int id, CancellationToken ct = default) => Task.Run(async () =>
    {
        var gate = _gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        var targetAccessible = false; var mounted = false;
        var mount = paths.ProviderPath(id);
        await gate.WaitAsync(ct);
        _status.TryGetValue(id, out var previous);
        var providerMissing = false;
        try
        {
            // A probe in progress is not an outage. Keep the last verified access
            // flags until this check produces a result; uploads consult them mid-write.
            _status[id] = new(id, mount, "Checking", "Connecting and checking provider access…",
                previous?.TargetAccessible ?? false, previous?.Mounted ?? false, previous?.Writable ?? false,
                previous?.CheckedAtUtc ?? DateTime.UtcNow);
            using var scope = scopes.CreateScope();
            await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
            var provider = await db.StorageProviders.AsNoTracking().Include(x => x.ManagedCredential).SingleOrDefaultAsync(x => x.Id == id, ct);
            providerMissing = provider is null;
            if (provider?.SiteServiceId is not null)
            {
                paths.RemoveOwnedLink(id);
                _status[id] = new(id, "", "Site managed", "Replication runs on the site service. This provider is not mounted by the main application.", false, false, false, DateTime.UtcNow);
                return;
            }
            if (provider is null || !provider.IsEnabled)
            {
                paths.RemoveOwnedLink(id);
                if (provider is null) _status.TryRemove(id, out _);
                else _status[id] = new(id, mount, "Disabled", "Provider disabled; its application-owned folder link has been removed.", false, false, false, DateTime.UtcNow);
                return;
            }
            var target = paths.ValidateTarget(provider);
            if (provider.Type == StorageProviderType.Smb && OperatingSystem.IsLinux())
            {
                _linuxMounts[id] = (provider.LinuxMountPath ?? "", provider.Path);
                VerifyLinuxMount(id);
                target = provider.LinuxMountPath!;
            }
            else _linuxMounts.TryRemove(id, out _);
            if (provider.Type == StorageProviderType.Smb && !OperatingSystem.IsLinux())
            {
                var credential = provider.ManagedCredential;
                if (provider.ManagedCredentialId.HasValue && credential is null) throw new InvalidOperationException("The configured managed credential no longer exists.");
                string? password = null;
                if (credential is not null)
                    password = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(credential.PasswordEncrypted);
                WindowsStorageLinks.ConnectShare(target, credential?.Username, password);
            }
            else if (provider.Type == StorageProviderType.Local) Directory.CreateDirectory(target);
            // Force a real directory-access request; Directory.Exists alone hides permission failures.
            using (var entries = Directory.EnumerateFileSystemEntries(target).GetEnumerator()) entries.MoveNext();
            targetAccessible = true;
            paths.EnsureLink(id, target);
            mounted = true;
            await ProbeAsync(mount, ct);
            _status[id] = new(id, mount, "Ready", "Folder linked; read, write and delete checks passed.", true, true, true, DateTime.UtcNow);
            logger.LogDebug("Storage provider {ProviderId} ready at {MountPath}", id, mount);
        }
        catch (OperationCanceledException)
        {
            _status[id] = new(id, mount, "Interrupted", "The storage check was interrupted.",
                targetAccessible, mounted, false, DateTime.UtcNow);
            throw;
        }
        catch (Exception exception)
        {
            var message = exception switch
            {
                CryptographicException => "The managed credential could not be decrypted. Ensure the service can read the existing data-protection keys and uses the same application content root.",
                UnauthorizedAccessException when targetAccessible && !mounted => "The target is reachable, but Windows denied folder-link creation. For IDE development, use an elevated IDE or enable Windows Developer Mode. The future service needs the Create symbolic links privilege.",
                IOException when (exception.HResult & 0xffff) == 1314 => "The target is reachable, but this process lacks the Create symbolic links privilege. Use an elevated IDE or Windows Developer Mode for development, or grant the service identity that privilege.",
                _ => exception.Message
            };
            _status[id] = new(id, mount, mounted ? "Access check failed" : targetAccessible ? "Link unavailable" : "Unavailable", message,
                targetAccessible, mounted, false, DateTime.UtcNow);
            // Do not log credential values, connection strings, or serialized provider objects.
            // Reconciliation retries cleanup for deleted providers too. Report a persistent
            // failure once, while continuing to retry and updating its status each time.
            if (previous?.State != _status[id].State || previous.Message != message)
            {
                if (providerMissing)
                    logger.LogWarning("Storage cleanup for removed provider {ProviderId} at {MountPath}: {Message}", id, mount, message);
                else
                    logger.LogWarning("Storage provider {ProviderId}: {Message}", id, message);
            }
        }
        finally { gate.Release(); }
    }, ct);

    private static async Task ProbeAsync(string mount, CancellationToken ct)
    {
        var probe = Path.Combine(mount, $".clonedeploy-probe-{Guid.NewGuid():N}.tmp");
        await using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await file.WriteAsync(new byte[] { 0x43 }, ct); await file.FlushAsync(ct);
        file.Position = 0;
        var bytes = new byte[1];
        if (await file.ReadAsync(bytes, ct) != 1 || bytes[0] != 0x43) throw new IOException("The storage read/write probe did not return the expected data.");
    }
}
