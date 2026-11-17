using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Prepares Mac enrollment downloads independently of Windows MSI update offers.</summary>
public sealed class MacAgentReleaseService(IWebHostEnvironment environment, ServerUrlSettingsStore urls,
    AgentEnrollmentService enrollment, CertificateAuthorityService certificates, ICloneDeployDbFactory factory,
    IAuthorizationService authorization)
{
    private const string SettingName = "AgentInstaller.CurrentRelease.macOS.arm64.pkg";
    private const string FileName = "CloneDeploy-Agent-macOS-arm64.pkg.json";
    private static readonly SemaphoreSlim Publication = new(1, 1);
    private string Folder => Path.Combine(new ServerDataPaths(environment).AgentInstallers, "MacReleases");
    private string PackagePath(AgentUpdateOffer release) => Path.Combine(Folder, release.Sha256 + ".pkg");
    private string BundlePath(AgentUpdateOffer release) => Path.Combine(Folder, release.Sha256 + ".zip");
    private string Template()
    {
        foreach (var root in new[] { environment.ContentRootPath, AppContext.BaseDirectory })
        {
            var path = Path.Combine(root, "Packaging", "Agent", FileName);
            if (File.Exists(path)) return path;
        }
        var development = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "artifacts", "mac-agent", FileName));
        if (environment.IsDevelopment() && File.Exists(development)) return development;
        throw new InvalidDataException("Mac agent template is missing. Run Build/Build-MacAgent.ps1 and publish the server again.");
    }
    private static string ReadVersion(string template)
    {
        if (new FileInfo(template).Length > 4096) throw new InvalidDataException("Mac package manifest is too large.");
        using var json = JsonDocument.Parse(File.ReadAllText(template));
        return AgentUpdateOffer.ParseVersion(json.RootElement.GetProperty("Version").GetString()!).ToString();
    }

    public async Task<AgentUpdateOffer?> CurrentAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var json = await db.AppSettings.Where(x => x.Name == SettingName).Select(x => x.Value).SingleOrDefaultAsync(ct);
        if (json is null) return null;
        var release = JsonSerializer.Deserialize<AgentUpdateOffer>(json) ?? throw new InvalidDataException("Invalid Mac release metadata.");
        release.Validate();
        if (release.Architecture != "arm64") throw new InvalidDataException("Invalid Mac architecture.");
        return File.Exists(PackagePath(release)) ? release : null;
    }
    public async Task<AgentReleaseReadiness> ReadinessAsync(CancellationToken ct)
    {
        var current = await CurrentAsync(ct);
        try { return AgentReleaseService.CompareReadiness(ReadVersion(Template()), current?.Version); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or KeyNotFoundException)
        { return new("Unavailable", PreparedVersion: current?.Version); }
    }
    public async Task<AgentUpdateOffer> PublishAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        if ((!AgentReleaseStartupPrincipal.IsStartup(user) && !(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded) ||
            !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) throw new UnauthorizedAccessException();
        await Publication.WaitAsync(ct);
        string? temporary = null;
        string? installerTemporary = null;
        try
        {
            var template = Template();
            var installer = Path.ChangeExtension(template, null);
            if (!File.Exists(installer)) throw new InvalidDataException("Mac installer package is missing. Publish the build-time package with the release.");
            var version = ReadVersion(template);
            var current = await CurrentAsync(ct);
            if (AgentReleaseStartupPrincipal.IsStartup(user) && current is not null &&
                AgentUpdateOffer.ParseVersion(version) <= AgentUpdateOffer.ParseVersion(current.Version) &&
                File.Exists(BundlePath(current))) return current;
            if (current is not null && AgentUpdateOffer.ParseVersion(version) < AgentUpdateOffer.ParseVersion(current.Version))
                throw new InvalidDataException("The Mac template is older than the prepared release.");
            var url = await urls.ReadServiceUrlAsync(ct) ?? throw new InvalidDataException("Configure the public application URL in Server Settings first.");
            var key = await enrollment.GetInstallerKeyAsync(user, ct);
            await using var db = await factory.CreateContextAsync();
            var now = DateTime.UtcNow;
            if (!await db.AppCertificates.AnyAsync(x => x.Type == CertificateType.Authority && !x.IsRevoked && x.NotBeforeUtc <= now && x.NotAfterUtc > now.AddDays(1), ct))
                throw new InvalidDataException("Create a valid certificate authority before preparing an installer.");
            var ca = await certificates.GetPublicCertificateAsync(user, ct);
            if (!ca.Success || ca.Data is null) throw new InvalidDataException("The public CA certificate could not be loaded.");
            Directory.CreateDirectory(Folder);
            WindowsWorkQueue.PrivateDirectory(Folder);
            temporary = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".tmp");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var input = File.OpenRead(installer))
                await input.CopyToAsync(output, ct);
            await using var hashed = File.OpenRead(temporary);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashed, ct));
            var release = new AgentUpdateOffer(version, hash, hashed.Length) { Architecture = "arm64" };
            release.Validate();
            using var packageManifest = JsonDocument.Parse(File.ReadAllText(template));
            if (!string.Equals(packageManifest.RootElement.GetProperty("Sha256").GetString(), hash, StringComparison.OrdinalIgnoreCase)
                || packageManifest.RootElement.GetProperty("Size").GetInt64() != hashed.Length)
                throw new InvalidDataException("Mac installer does not match its release manifest. Rebuild and publish the matching package and manifest.");
            await hashed.DisposeAsync();
            installerTemporary = temporary + ".zip";
            using (var bundle = ZipFile.Open(installerTemporary, ZipArchiveMode.Create))
            {
                bundle.CreateEntryFromFile(temporary, "CloneDeploy-Agent-macOS-arm64.pkg", CompressionLevel.NoCompression);
                await using (var configuration = bundle.CreateEntry("enrollment.json").Open())
                    await JsonSerializer.SerializeAsync(configuration, new { ServerUrl = url, ProvisioningKey = key,
                        ServerCaCertificateBase64 = Convert.ToBase64String(ca.Data) }, cancellationToken: ct);
            }
            File.Move(installerTemporary, BundlePath(release), true);
            installerTemporary = null;
            File.Move(temporary, PackagePath(release), true);
            temporary = null;
            var setting = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == SettingName, ct);
            if (setting is null) db.AppSettings.Add(setting = new AppSetting { Name = SettingName });
            setting.Value = JsonSerializer.Serialize(release);
            if (!AgentReleaseStartupPrincipal.IsStartup(user)) db.AuditLogs.Add(new AuditLog { AppUserId = userId, UserName = user.Identity?.Name ?? userId.ToString(),
                AuditType = AuditType.Create, ObjectType = "AgentRelease", ObjectName = "macOS ARM64 " + version,
                ObjectJson = JsonSerializer.Serialize(release), LoggedAtUtc = now });
            await db.SaveChangesAsync(ct);
            return release;
        }
        finally
        {
            try { if (temporary is not null) File.Delete(temporary); if (installerTemporary is not null) File.Delete(installerTemporary); }
            finally { Publication.Release(); }
        }
    }
    private static async Task AddFile(TarWriter writer, string name, byte[] bytes, CancellationToken ct)
    {
        using var data = new MemoryStream(bytes);
        await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name)
        { DataStream = data, Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite }, ct);
    }
    internal async Task<bool> PrepareAtStartupAsync(CancellationToken ct)
    {
        var previous = await CurrentAsync(ct);
        var complete = previous is not null && File.Exists(BundlePath(previous));
        var release = await PublishAsync(AgentReleaseStartupPrincipal.Instance, ct);
        return !complete || previous != release;
    }
    public async Task<FileStream?> OpenInstallerAsync(CancellationToken ct)
    {
        var release = await CurrentAsync(ct);
        if (release is null) return null;
        try { return new FileStream(BundlePath(release), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true); }
        catch (FileNotFoundException) { return null; }
    }
    public Task<FileStream?> OpenAsync(CancellationToken ct) => OpenMatchingAsync(null, ct);
    public async Task<FileStream?> OpenMatchingAsync(string? hash, CancellationToken ct)
    {
        var release = await CurrentAsync(ct);
        if (release is null || (hash is not null && !string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase))) return null;
        try { return new FileStream(PackagePath(release), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true); }
        catch (FileNotFoundException) { return null; }
    }
}
