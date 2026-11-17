using System.Text.Json;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Services.Files;
using CloneDeploy_Server.WinPe;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

// Runs only in the web application identity, which owns provider mounts and credentials.
/// <summary>
/// Moves completed boot images to storage and locates their files for download.
/// </summary>
public sealed class WinPeArtifactStorage(BuildStorage local, StorageContentStorage providers)
{
    public SharedContentDelivery? Shared { get; init; }

    public Task<int> SelectProviderAsync(CancellationToken ct = default) => providers.SelectProviderAsync(CloneDeploy_Server.Backend.Enum.StoragePurpose.Imaging, ct);
    public Task EnsureProviderReadyAsync(int provider, CancellationToken ct = default) => providers.EnsureReadyAsync(provider, ct);

    public BuildStatusDto Status(WinPeBuild build)
    {
        if (build.StoredAtUtc is not null && build.StorageError is { } preparationError)
            return new("BootPreparationFailed", preparationError, build.StoredAtUtc.Value, []);
        if (build.StoredAtUtc is { } stored)
            return new("Ready", "Saved to storage provider " + build.StorageProviderId + ".", stored,
                JsonSerializer.Deserialize<List<BuildArtifactDto>>(build.StoredArtifactsJson) ?? []);
        var status = local.Status(build.Id);
        if (status.State != "Ready") return status;
        return build.StorageError is { } error
            ? new("StorageFailed", error, status.UpdatedAtUtc, [])
            : new("SavingToStorage", "Build finished. Saving outputs to storage.", status.UpdatedAtUtc, []);
    }

    public async Task<string> PathAsync(WinPeBuild build, string name, CancellationToken ct = default)
    {
        if (build.StoredAtUtc is null || build.StorageProviderId is not { } provider || !Status(build).Artifacts.Any(x => x.Name == name))
            throw new InvalidOperationException("This build has not been saved to storage.");
        async Task<string> Primary()
        {
            await providers.EnsureReadyAsync(provider, ct);
            return ProviderPath(provider, build.Id, name);
        }
        var hash = Status(build).Artifacts.Single(x => x.Name == name).Sha256;
        return Shared is null ? await Primary() : await Shared.RequestPathAsync(SharedContentKind.WinPe, $"{build.Id:N}/{name}", hash, Primary, ct);
    }

    private string ProviderPath(int provider, Guid id, string name)
    {
        // The provider root is an intentional application-managed link. Reject links beneath it.
        var root = providers.GetRoot(provider);
        var path = Path.Combine(root, "winpe", id.ToString("N"), Path.GetFileName(local.FilePath(id, name)));
        for (var current = path; !string.Equals(current, root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("WinPE artifact directories cannot contain links beneath the provider root.");
        return path;
    }

    public async Task TransferAsync(ICloneDeployDbFactory factory, Guid id, CancellationToken ct = default)
    {
        FileStream claim;
        try
        {
            claim = new FileStream(local.FilePath(id, "transfer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return;
        }

        using var ownership = claim;
        await using var db = await factory.CreateContextAsync();
        var build = await db.WinPeBuilds.Include(x => x.Definition).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (build is null || build.StoredAtUtc is not null || build.StorageError is not null || local.Status(id).State != "Ready") return;
        try
        {
            if (build.StorageProviderId is null)
            {
                // Adopt builds produced before provider storage was introduced. Pin the destination before copying.
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await StorageProviderAdministration.LockAsync(db);
                build.StorageProviderId = await SelectProviderAsync(ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }

            var provider = build.StorageProviderId.Value;
            if (!await db.StorageProviders.AnyAsync(x => x.Id == provider && x.IsEnabled, ct))
                throw new IOException("The build's storage provider is disabled or missing.");
            await providers.EnsureReadyAsync(provider, ct);
            var artifacts = local.Status(id).Artifacts.ToList();
            var request = JsonSerializer.Deserialize<BuildRequestDto>(build.RequestJson)
                ?? throw new InvalidDataException("Missing build recipe.");
            var required = WinPeBootFiles.RequiredOutputs(request with { IsBase = build.Definition.IsBase }).ToArray();
            if (artifacts.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != artifacts.Count || required.Any(name => !artifacts.Any(x => x.Name == name)))
                throw new InvalidDataException("The completed build is missing required artifacts. Local output has been retained.");
            foreach (var name in new[] { "request.json", "build.log" })
            {
                var path = local.FilePath(id, name);
                if (File.Exists(path) && !artifacts.Any(x => x.Name == name)) artifacts.Add(new(name, new FileInfo(path).Length, await BuildStorage.HashAsync(path, ct)));
            }

            foreach (var artifact in artifacts)
            {
                ct.ThrowIfCancellationRequested();
                var source = local.FilePath(id, artifact.Name);
                BuildStorage.RejectLinks(source);
                var target = ProviderPath(provider, id, artifact.Name);
                // A previous attempt may have copied some files before a network failure or restart.
                if (await Matches(target, artifact, ct)) continue;
                if (!await Matches(source, artifact, ct)) throw new InvalidDataException("Local artifact checksum mismatch: " + artifact.Name + ". Output retained for investigation.");
                using var publication = await FilePublication.CreateAsync(source, target, replace: true, ct: ct);
                if (!await Matches(target, artifact, ct)) throw new IOException("Storage verification failed: " + artifact.Name);
                publication.Commit();
            }

            build.StoredArtifactsJson = JsonSerializer.Serialize(artifacts);
            build.StoredAtUtc = DateTime.UtcNow;
            build.StorageError = null;
            await db.SaveChangesAsync(ct);
            // Keep local outputs as a recovery copy; serving and future staging now use the recorded provider.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        } // retry automatically after restart
        catch (Exception ex)
        {
            build.StorageError = ex is InvalidOperationException or InvalidDataException ? ex.Message : "Could not save the build to its storage provider. Check the provider connection and retry. Local outputs are retained.";
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    public IDisposable ClaimDeletion(IEnumerable<WinPeBuild> builds)
    {
        var claim = new DeletionClaim();
        try
        {
            foreach (var build in builds)
            {
                var job = local.Job(build.Id);
                BuildStorage.RejectLinks(job);
                if (!Directory.Exists(job)) continue;
                foreach (var name in new[] { "execution.lock", "transfer.lock" })
                {
                    var path = local.FilePath(build.Id, name);
                    BuildStorage.RejectLinks(path);
                    claim.Files.Add(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                }
            }

            return claim;
        }
        catch
        {
            claim.Dispose();
            throw;
        }
    }

    private sealed class DeletionClaim : IDisposable
    {
        public List<FileStream> Files { get; } = [];

        public void Dispose()
        {
            foreach (var file in Files) file.Dispose();
        }
    }

    public async Task DeleteFilesAsync(WinPeBuild build)
    {
        if (build.StorageProviderId is { } provider)
        {
            await providers.EnsureReadyAsync(provider);
            DeleteDirectory(Path.GetDirectoryName(ProviderPath(provider, build.Id, "manifest.json"))!);
        }

        var job = local.Job(build.Id);
        BuildStorage.RejectLinks(job);
        DeleteDirectory(job);
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        // Inspect every descendant without following links before recursively deleting.
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("WinPE cleanup cannot follow filesystem links.");
            if (!Directory.Exists(current)) continue;
            foreach (var entry in Directory.EnumerateFileSystemEntries(current)) pending.Push(entry);
        }

        Directory.Delete(path, recursive: true);
    }

    private static async Task<bool> Matches(string path, BuildArtifactDto artifact, CancellationToken ct) =>
        File.Exists(path) && new FileInfo(path).Length == artifact.Size && string.Equals(await BuildStorage.HashAsync(path, ct), artifact.Sha256, StringComparison.OrdinalIgnoreCase);
}
