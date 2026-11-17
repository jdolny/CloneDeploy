using System.Text.Json;
using CloneDeploy.SiteServices;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.WinPe;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;

namespace CloneDeploy_Server.Backend.Services;

// Durable main-owned inputs/results. Every operation is bound to both the enrolled site and attempt lease.
public sealed class WindowsWorkQueue(IServiceScopeFactory scopes, IWebHostEnvironment environment, IDataProtectionProvider protection)
{
    public WindowsWorkerPolicy WorkerPolicy { get; init; } = new();
    private readonly IDataProtector secrets = protection.CreateProtector("WindowsWorker.Request.v1");
    public string RootPath { get; init; } = new ServerDataPaths(environment).WindowsWorkerJobs;
    public string TemporaryDirectory() => new ServerDataPaths(environment).EnsureTempDirectory();
    private string Root => RootPath;
    public string Folder(Guid id) => Path.Combine(Root, id.ToString("N"));
    public string Output(Guid id, Guid lease, string name) => Path.Combine(Folder(id), lease.ToString("N"), WindowsWorkFiles.Name(name));
    public async Task<Guid?> HostAsync(WindowsWorkerRoles role, CancellationToken ct)
    {
        if (!WorkerPolicy.RemoteAllowed) return null;
        using var scope = scopes.CreateScope(); await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
        return await db.SiteServices.Where(x => x.IsWindowsWorker && x.State == SiteRegistrationState.Approved && x.HostOs == "Windows" &&
            (x.WindowsWorkerRoles & (int)role) != 0).OrderBy(x => x.Id).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
    }
    public async Task<Guid> EnqueueAsync(string kind, string request, IReadOnlyDictionary<string, string> inputs, CancellationToken ct, Guid? id = null)
    {
        var site = await HostAsync(WindowsWorkFiles.Role(kind), ct) ?? throw new InvalidOperationException("Enable this Windows worker capability on an approved Windows site service first.");
        if (request.Length > 131072 || inputs.Count > 256) throw new InvalidDataException("Work request exceeds the limit.");
        var jobId = id ?? Guid.NewGuid();
        using var scope = scopes.CreateScope(); await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
        if (await db.Set<WindowsWorkerJob>().AnyAsync(x => x.Id == jobId, ct)) return jobId;
        PrivateDirectory(Root); Directory.CreateDirectory(Folder(jobId)); BuildStorage.RejectLinks(Folder(jobId));
        var directory = Path.Combine(Folder(jobId), "inputs"); Directory.CreateDirectory(directory);
        var files = new List<WindowsWorkFile>();
        foreach (var (name, source) in inputs)
        {
            var description = await WindowsWorkFiles.DescribeAsync(source, name, ct);
            await using var input = File.OpenRead(source);
            await WindowsWorkFiles.CopyAsync(input, Path.Combine(directory, description.Name), description, ct); files.Add(description);
        }
        db.Add(new WindowsWorkerJob { Id = jobId, SiteId = site, Kind = kind, RequestEncrypted = secrets.Protect(request),
            InputsJson = JsonSerializer.Serialize(files), ExpiresAtUtc = DateTime.UtcNow.AddHours(6) });
        await db.SaveChangesAsync(ct); return jobId;
    }
    public async Task<WindowsWorkerJob?> ReadAsync(Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope(); await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
        return await db.Set<WindowsWorkerJob>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    }
    public async Task<WindowsWorkerJob> WaitAsync(Guid id, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var scope = scopes.CreateScope(); await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
            var job = await db.Set<WindowsWorkerJob>().AsNoTracking().SingleAsync(x => x.Id == id, ct);
            if (job.State == "Ready") return job;
            if (job.State is "Failed" or "Cancelled" || job.ExpiresAtUtc < DateTime.UtcNow)
                throw new InvalidOperationException(job.Error ?? "Windows work expired or was cancelled.");
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }
    public async Task<WindowsWorkOffer?> ClaimAsync(Guid site, CancellationToken ct)
    {
        using var scope = scopes.CreateScope(); await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync(); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903425)", ct);
        var host = await db.SiteServices.SingleAsync(x => x.Id == site, ct);
        if (!WorkerPolicy.RemoteAllowed || !host.IsWindowsWorker || host.HostOs != "Windows" || host.State != SiteRegistrationState.Approved || host.WindowsWorkerRoles != 7) throw new UnauthorizedAccessException();
        var now = DateTime.UtcNow;
        if (await db.Set<WindowsWorkerJob>().AnyAsync(x => x.SiteId == site && x.State == "Running" && x.LeaseUntilUtc > now, ct)) return null;
        var jobs = await db.Set<WindowsWorkerJob>().Where(x => x.SiteId == site && (x.State == "Queued" || x.State == "Running" && x.LeaseUntilUtc <= now))
            .OrderBy(x => x.ExpiresAtUtc).ToListAsync(ct);
        WindowsWorkerJob? picked = null;
        foreach (var job in jobs)
        {
            if (job.ExpiresAtUtc <= now || job.Attempts >= 3 || (host.WindowsWorkerRoles & (int)WindowsWorkFiles.Role(job.Kind)) == 0)
            { job.State = "Failed"; job.Error = "Windows worker unavailable, disabled, or exceeded its retry limit."; continue; }
            picked = job; break;
        }
        if (picked is not null)
        {
            picked.State = "Running"; picked.Lease = Guid.NewGuid(); picked.LeaseUntilUtc = now.AddMinutes(2); picked.Attempts++;
            Directory.CreateDirectory(Path.Combine(Folder(picked.Id), picked.Lease.ToString("N")));
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return picked is null ? null : new(picked.Id, picked.Lease, picked.Kind, secrets.Unprotect(picked.RequestEncrypted),
            JsonSerializer.Deserialize<WindowsWorkFile[]>(picked.InputsJson)!);
    }
    private static async Task<WindowsWorkerJob> Owned(CloneDeployDbContext db, Guid site, Guid id, Guid lease, CancellationToken ct)
    {
        var row = await db.Set<WindowsWorkerJob>().SingleOrDefaultAsync(x => x.Id == id && x.SiteId == site && x.Lease == lease, ct);
        if (row is null || row.State != "Running" || row.LeaseUntilUtc <= DateTime.UtcNow || row.ExpiresAtUtc <= DateTime.UtcNow ||
            !await db.SiteServices.AnyAsync(x => x.Id == site && x.IsWindowsWorker && x.State == SiteRegistrationState.Approved && x.HostOs == "Windows" &&
                (x.WindowsWorkerRoles & (int)WindowsWorkFiles.Role(row.Kind)) != 0, ct)) throw new UnauthorizedAccessException();
        return row;
    }
    public async Task RenewAsync(Guid site, Guid id, Guid lease, string? progress, CancellationToken ct)
    {
        if (progress?.Length > 1000) throw new InvalidDataException("Progress too long.");
        using var scope = scopes.CreateScope(); await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync(); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903425)", ct);
        var job = await Owned(db, site, id, lease, ct); job.LeaseUntilUtc = DateTime.UtcNow.AddMinutes(2); job.Progress = progress;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task<Stream> InputAsync(Guid site, Guid id, Guid lease, string name, CancellationToken ct)
    {
        using var scope = scopes.CreateScope(); await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync(); var job = await Owned(db, site, id, lease, ct);
        if (!JsonSerializer.Deserialize<WindowsWorkFile[]>(job.InputsJson)!.Any(x => x.Name == name)) throw new UnauthorizedAccessException();
        return File.OpenRead(Path.Combine(Folder(id), "inputs", WindowsWorkFiles.Name(name)));
    }
    public async Task UploadAsync(Guid site, Guid id, Guid lease, WindowsWorkFile file, Stream input, CancellationToken ct)
    {
        using var scope = scopes.CreateScope(); await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync(); _ = await Owned(db, site, id, lease, ct);
        using var gate = new FileStream(Path.Combine(Folder(id), lease.ToString("N"), "transfer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        db.ChangeTracker.Clear(); var job = await Owned(db, site, id, lease, ct);
        if (!AllowedOutputs(job).Contains(file.Name)) throw new InvalidDataException("Unexpected work output.");
        var path = Output(id, lease, file.Name); BuildStorage.RejectLinks(path);
        await WindowsWorkFiles.CopyAsync(input, path, file, ct);
        // Expired attempts can only write into their own directory, never publish another attempt's result.
        _ = await Owned(db, site, id, lease, ct);
    }
    public async Task CompleteAsync(Guid site, Guid id, Guid lease, WindowsWorkCompletion completion, CancellationToken ct)
    {
        if (completion.Outputs is null || completion.Outputs.Length > 256 || completion.Outputs.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != completion.Outputs.Length ||
            completion.Error?.Length > 2000 || completion.Result?.Length > 131072) throw new InvalidDataException("Invalid work result.");
        using var scope = scopes.CreateScope(); await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync(); _ = await Owned(db, site, id, lease, ct);
        using var gate = new FileStream(Path.Combine(Folder(id), lease.ToString("N"), "transfer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        db.ChangeTracker.Clear(); var assigned = await Owned(db, site, id, lease, ct);
        var allowed = AllowedOutputs(assigned);
        foreach (var file in completion.Outputs)
        {
            WindowsWorkFiles.Validate(file);
            if (!allowed.Contains(file.Name)) throw new InvalidDataException("Unexpected work output.");
            var actual = await WindowsWorkFiles.DescribeAsync(Output(id, lease, file.Name), file.Name, ct);
            if (actual.Length != file.Length || !actual.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Returned output does not match its manifest.");
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903425)", ct);
        db.ChangeTracker.Clear(); var job = await Owned(db, site, id, lease, ct);
        job.OutputsJson = JsonSerializer.Serialize(completion.Outputs); job.Result = completion.Result; job.Error = completion.Error;
        job.State = completion.Error is null ? "Ready" : "Failed"; job.RequestEncrypted = "";
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    private HashSet<string> AllowedOutputs(WindowsWorkerJob job) => (job.Kind switch
    {
        "Driver" => new[] { "prepared.zip" }, "Catalog" => ["catalog.xml"], "MsiVersion" or "MsiArchitecture" => [],
        "WinPeSetup" => ["setup.log"],
        "WinPe" => WinPeBootFiles.RequiredOutputs(JsonSerializer.Deserialize<BuildRequestDto>(secrets.Unprotect(job.RequestEncrypted))!)
            .Concat(WinPeBootFiles.NativeOutputs).Concat(WinPeBootFiles.Arm64NativeOutputs).Append("build.log").ToArray(),
        _ => throw new InvalidDataException("Unknown work type.")
    }).ToHashSet(StringComparer.Ordinal);
    public async Task CancelAsync(Guid id, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope(); await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync(); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903425)", ct);
        await db.Set<WindowsWorkerJob>().Where(x => x.Id == id && x.State != "Ready").ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Cancelled").SetProperty(x => x.RequestEncrypted, ""), ct);
        await tx.CommitAsync(ct);
    }
    public async Task ForgetAsync(Guid id)
    {
        await CancelAsync(id);
        using var scope = scopes.CreateScope(); await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
        var folder = Folder(id); BuildStorage.RejectLinks(folder);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        await db.Set<WindowsWorkerJob>().Where(x => x.Id == id).ExecuteDeleteAsync();
    }
    public async Task CleanupAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope(); await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
        var expired = await db.Set<WindowsWorkerJob>().Where(x => x.ExpiresAtUtc < DateTime.UtcNow).Select(x => x.Id).ToListAsync(ct);
        foreach (var id in expired)
        { try { await ForgetAsync(id); } catch (IOException) { /* Active streams will close; retry next pass. */ } }
    }
    public static void PrivateDirectory(string path)
    {
        var directory = Directory.CreateDirectory(path); BuildStorage.RejectLinks(path);
        if (OperatingSystem.IsWindows())
        {
            var acl = new System.Security.AccessControl.DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            foreach (var sid in new[] { identity.User!, new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null) })
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            System.IO.FileSystemAclExtensions.SetAccessControl(directory, acl);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
