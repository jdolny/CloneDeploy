using CloneDeploy_Server.Backend.Services.Files;
using Microsoft.Extensions.Options;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Manages working folders and storage for prepared driver packages.
/// </summary>
public sealed class DriverPreparationStorage(IOptions<UploadOptions> options, IWebHostEnvironment environment, StorageContentStorage? providers = null)
{
    private readonly string _root = Path.Combine(Path.GetFullPath(options.Value.StoragePath, environment.ContentRootPath), "prepared-drivers");
    private readonly string _workRoot = Path.Combine(new ServerDataPaths(environment).Temp, "DriverPreparation");
    public string ArchivePath(Guid id) => Path.Combine(_root, id.ToString("N") + ".zip");
    public async Task<int?> SelectProviderAsync(CancellationToken ct = default)
    {
        return providers is null ? null : await providers.SelectProviderAsync(CloneDeploy_Server.Backend.Enum.StoragePurpose.Imaging, ct);
    }
    public string ArchivePath(Guid id, int? providerId) => providerId is { } provider
        ? Path.Combine((providers ?? throw new InvalidOperationException("Storage providers are not configured.")).GetRoot(provider), "prepared-drivers", id.ToString("N") + ".zip")
        : ArchivePath(id);
    public void RemoveExpiredArtifacts(HashSet<Guid> referenced)
    {
        var cutoff = DateTime.UtcNow.AddDays(-1);
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*.zip", SearchOption.TopDirectoryOnly))
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id) && !referenced.Contains(id) && File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(ArchivePath(id));
        }
        var work = _workRoot;
        if (!Directory.Exists(work)) return;
        foreach (var folder in Directory.EnumerateDirectories(work, "*", SearchOption.TopDirectoryOnly))
            if (Guid.TryParseExact(Path.GetFileName(folder), "N", out _) && Directory.GetLastWriteTimeUtc(folder) < cutoff)
                RemoveWorkDirectory(folder);
    }
    public string CreateWorkDirectory()
    {
        var path = Path.Combine(_workRoot, Guid.NewGuid().ToString("N"));
        CloneDeploy_Server.WinPe.BuildStorage.RejectLinks(path);
        Directory.CreateDirectory(path);
        return path;
    }
    public void RemoveWorkDirectory(string path)
    {
        var parent = Path.GetFullPath(_workRoot) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(path);
        if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
            throw new InvalidOperationException("Invalid driver work directory.");
        CloneDeploy_Server.WinPe.BuildStorage.RejectLinks(resolved);
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
