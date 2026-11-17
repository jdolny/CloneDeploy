using System.Diagnostics;
using System.Security.Claims;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;
using CloneDeploy_Server.Backend.Common;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DatabaseBackupFile(string Name, string Path, DateTime CompletedUtc, long Bytes);
public sealed record DatabaseBackupStatus(bool Running, string Directory, string? Error, IReadOnlyList<DatabaseBackupFile> Files);

// A server-owned job continues when the requesting browser leaves the page.
public sealed class DatabaseBackupService(
    IConfiguration configuration,
    IServiceScopeFactory scopes,
    ILogger<DatabaseBackupService> logger,
    ServerRuntimePaths? runtimePaths) : BackgroundService
{
    private readonly Channel<bool> requests = Channel.CreateBounded<bool>(1);
    private readonly object gate = new();
    private bool running;
    private string? error;
    public string BackupDirectory => Path.Combine(
        runtimePaths?.DataRoot ??
        Environment.GetEnvironmentVariable("CLONEDEPLOY_DATA") ??
        (OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CloneDeploy", "Server") : AppContext.BaseDirectory), "Backups");

    private async Task Authorize(ClaimsPrincipal user)
    {
        using var scope = scopes.CreateScope();
        if (!(await scope.ServiceProvider.GetRequiredService<IAuthorizationService>()
                .AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded)
            throw new UnauthorizedAccessException("Administrator access is required.");
    }

    public async Task<bool> StartBackupAsync(ClaimsPrincipal user)
    {
        await Authorize(user);
        lock (gate)
        {
            if (running) return false;
            running = true;
            error = null;
            if (!requests.Writer.TryWrite(true)) { running = false; return false; }
        }
        logger.LogInformation("Database backup requested by administrator {UserId}", user.FindFirstValue(ClaimTypes.NameIdentifier));
        return true;
    }

    public async Task<DatabaseBackupStatus> GetStatusAsync(ClaimsPrincipal user)
    {
        await Authorize(user);
        var directory = BackupDirectory;
        RejectLinks(directory);
        var files = Directory.Exists(directory) ? new DirectoryInfo(directory).EnumerateFiles("database-*.dump")
            .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new DatabaseBackupFile(f.Name, f.FullName, f.LastWriteTimeUtc, f.Length)).ToArray() : [];
        lock (gate) return new(running, directory, error, files);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var _ in requests.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await DumpAsync(stoppingToken);
                    logger.LogInformation("Database backup completed");
                }
                catch (Exception ex)
                {
                    // Neither connection strings nor child output belong in logs or the browser.
                    lock (gate) error = ex is BackupFailure ? ex.Message :
                        ex is OperationCanceledException ? "Database backup stopped because the server is shutting down." :
                        "Database backup failed. Check database connectivity, PostgreSQL tool availability, disk space, and backup folder permissions.";
                    logger.LogWarning("Database backup failed ({ExceptionType})", ex.GetType().Name);
                }
                finally { lock (gate) running = false; }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task DumpAsync(CancellationToken ct)
    {
        var bin = OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "PostgreSQL", "18", "bin") : "/usr/bin";
        var dump = Path.Combine(bin, OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump");
        var restore = Path.Combine(bin, OperatingSystem.IsWindows() ? "pg_restore.exe" : "pg_restore");
        if (!File.Exists(dump) || !File.Exists(restore))
            throw new BackupFailure("PostgreSQL pg_dump/pg_restore tools are missing. Repair the server installation (or install PostgreSQL client tools on Linux).");
        ProtectDirectory(BackupDirectory);
        var id = $"database-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var partial = Path.Combine(BackupDirectory, id + ".partial");
        var passwordFile = Path.Combine(BackupDirectory, id + ".pgpass");
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("CloneDeploy-Server-Database"));
            if (string.IsNullOrWhiteSpace(connection.Host) || string.IsNullOrWhiteSpace(connection.Database) || string.IsNullOrWhiteSpace(connection.Username))
                throw new BackupFailure("Database backup requires an explicit host, database name, and username in the server connection settings.");
            // A wildcard entry is limited to this process's private, randomly named password file.
            static string Escape(string value) => value.Replace("\\", "\\\\").Replace(":", "\\:");
            if ((connection.Password ?? "").Contains('\n') || (connection.Password ?? "").Contains('\r'))
                throw new BackupFailure("Database backup does not support a database password containing line breaks.");
            await File.WriteAllTextAsync(passwordFile, "*:*:*:*:" + Escape(connection.Password ?? "") + "\n", ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(passwordFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var info = CreateDumpStartInfo(dump, connection, passwordFile, partial);
            await RunAsync(info, ct);
            var verify = new ProcessStartInfo(restore);
            verify.ArgumentList.Add("--list"); verify.ArgumentList.Add(partial);
            await RunAsync(verify, ct);
            if (new FileInfo(partial).Length == 0) throw new BackupFailure("PostgreSQL produced an empty backup.");
            File.SetLastWriteTimeUtc(partial, DateTime.UtcNow);
            File.Move(partial, Path.Combine(BackupDirectory, id + ".dump"));
        }
        finally
        {
            File.Delete(passwordFile);
            File.Delete(partial);
        }
    }

    public static ProcessStartInfo CreateDumpStartInfo(string executable, NpgsqlConnectionStringBuilder connection, string passwordFile, string output)
    {
        var info = new ProcessStartInfo(executable);
        // Do not inherit unrelated libpq defaults or credentials from the service environment.
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("PG", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        info.Environment["PGHOST"] = connection.Host;
        info.Environment["PGPORT"] = connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["PGDATABASE"] = connection.Database;
        info.Environment["PGUSER"] = connection.Username;
        info.Environment["PGPASSFILE"] = passwordFile;
        info.Environment["PGCONNECT_TIMEOUT"] = "30";
        info.Environment["PGSSLMODE"] = connection.SslMode switch
        {
            SslMode.Disable => "disable", SslMode.Allow => "allow", SslMode.Prefer => "prefer",
            SslMode.Require => "require", SslMode.VerifyCA => "verify-ca", SslMode.VerifyFull => "verify-full",
            _ => throw new InvalidOperationException("Unsupported database SSL mode.")
        };
        if (!string.IsNullOrWhiteSpace(connection.RootCertificate)) info.Environment["PGSSLROOTCERT"] = connection.RootCertificate;
        info.ArgumentList.Add("--format=custom");
        info.ArgumentList.Add("--no-password");
        info.ArgumentList.Add("--file"); info.ArgumentList.Add(output);
        return info;
    }

    private static async Task RunAsync(ProcessStartInfo info, CancellationToken ct)
    {
        info.UseShellExecute = false; info.CreateNoWindow = true;
        info.RedirectStandardOutput = true; info.RedirectStandardError = true;
        using var process = new Process { StartInfo = info };
        process.Start();
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try { await process.WaitForExitAsync(ct); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        finally { await Task.WhenAll(stdout, stderr); }
        if (process.ExitCode != 0)
            throw new BackupFailure($"{Path.GetFileName(info.FileName)} failed (exit code {process.ExitCode}). Check database connectivity, disk space, permissions, and that the tools support the database server version.");
    }

    private static void RejectLinks(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Backup directory must not contain symbolic links or junctions.");
    }

    private static void ProtectDirectory(string path)
    {
        RejectLinks(path);
        var directory = Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
            using var identity = WindowsIdentity.GetCurrent();
            foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(acl);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private sealed class BackupFailure(string message) : Exception(message);
}
