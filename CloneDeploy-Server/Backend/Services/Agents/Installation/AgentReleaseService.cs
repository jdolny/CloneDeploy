using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record AgentReleaseReadiness(string State, string? BuiltVersion = null, string? PreparedVersion = null);

/// <summary>Stores the prepared installer once and advertises that exact release to enrolled agents.</summary>
public sealed class AgentReleaseService(IWebHostEnvironment environment, ICloneDeployDbFactory factory,
    AgentInstallerService installers, IAuthorizationService authorization)
{
    private const string SettingName = "AgentInstaller.CurrentRelease";
    private static readonly SemaphoreSlim Publication = new(1, 1);
    private string Folder => Path.Combine(new ServerDataPaths(environment).AgentInstallers, "Releases");

    public async Task<AgentReleaseReadiness> ReadinessAsync(CancellationToken ct, string architecture = "x64")
    {
        architecture = WindowsArchitecture.Require(architecture);
        AgentUpdateOffer? current = null;
        try
        {
            current = await CurrentAsync(ct, architecture);
            var template = Path.Combine(environment.ContentRootPath, "Packaging", "Agent", $"CloneDeploy-Agent-{architecture}.msi");
            if (!File.Exists(template))
                template = Path.Combine(AppContext.BaseDirectory, "Packaging", "Agent", $"CloneDeploy-Agent-{architecture}.msi");
            if (!File.Exists(template) && environment.IsDevelopment())
                template = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "artifacts", "agent-installer", $"CloneDeploy-Agent-{architecture}.msi"));
            if (!File.Exists(template)) return new("MissingTemplate", PreparedVersion: current?.Version);
            var manifest = await AgentPackageManifest.ValidateFileAsync(template, architecture, ct);
            return CompareReadiness(manifest.Version, current?.Version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            System.Runtime.InteropServices.COMException or JsonException or FormatException or ArgumentException)
        { return new("Unavailable", PreparedVersion: current?.Version); }
    }

    internal static AgentReleaseReadiness CompareReadiness(string builtVersion, string? preparedVersion)
    {
        var built = AgentUpdateOffer.ParseVersion(builtVersion);
        var prepared = preparedVersion is null ? null : AgentUpdateOffer.ParseVersion(preparedVersion);
        return new(prepared is null || built > prepared ? "NeedsPreparation" : built < prepared ? "OlderTemplate" : "Current",
            built.ToString(), prepared?.ToString());
    }

    public async Task<AgentUpdateOffer?> CurrentAsync(CancellationToken ct, string architecture = "x64")
    {
        architecture = WindowsArchitecture.Require(architecture);
        var settingName = architecture == "x64" ? SettingName : SettingName + ".arm64";
        await using var db = await factory.CreateContextAsync();
        var json = await db.AppSettings.Where(x => x.Name == settingName).Select(x => x.Value).SingleOrDefaultAsync(ct);
        if (json is null) return null;
        var release = JsonSerializer.Deserialize<AgentUpdateOffer>(json) ?? throw new InvalidDataException("Invalid agent release settings.");
        release.Validate();
        if (release.Architecture != architecture) throw new InvalidDataException("Agent release architecture mismatch.");
        return File.Exists(PackagePath(release)) ? release : null;
    }

    public async Task<AgentUpdateOffer> PublishAsync(ClaimsPrincipal user, CancellationToken ct, string architecture = "x64")
    {
        if ((!AgentReleaseStartupPrincipal.IsStartup(user) && !(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded) ||
            !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) throw new UnauthorizedAccessException();
        architecture = WindowsArchitecture.Require(architecture);
        var settingName = architecture == "x64" ? SettingName : SettingName + ".arm64";
        await Publication.WaitAsync(ct);
        try
        {
            if (AgentReleaseStartupPrincipal.IsStartup(user))
            {
                var readiness = await ReadinessAsync(ct, architecture);
                if (readiness.State is "Current" or "OlderTemplate")
                {
                    await using var bundle = await OpenBundleAsync(ct, architecture);
                    if (bundle is not null) return (await CurrentAsync(ct, architecture))!;
                }
            }
            // GenerateAsync applies the private installer-directory ACL before any persistent copy is created.
            await using var generated = await installers.GenerateAsync(user, ct, architecture);
            Directory.CreateDirectory(Folder);
            WindowsWorkQueue.PrivateDirectory(Folder);
            var temporary = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".tmp");
            var bundleTemporary = temporary + ".zip";
            AgentUpdateOffer release;
            try
            {
                AgentPackageManifest manifest;
                using (var bundle = new ZipArchive(generated, ZipArchiveMode.Read, leaveOpen: true))
                {
                    var metadata = bundle.GetEntry($"CloneDeploy-Agent-{architecture}.msi.json")
                        ?? throw new InvalidDataException("Missing MSI release manifest in deployment bundle.");
                    await using (var input = metadata.Open())
                        manifest = await AgentPackageManifest.ReadAsync(input, architecture, ct);
                    var entry = bundle.GetEntry($"CloneDeploy-Agent-{architecture}.msi")
                        ?? throw new InvalidDataException("Missing generic MSI in deployment bundle.");
                    await using var source = entry.Open();
                    await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                    await source.CopyToAsync(output, ct);
                }
                string hash;
                long length;
                await using (var package = File.OpenRead(temporary))
                {
                    await manifest.VerifyAsync(package, ct);
                    length = package.Length;
                    hash = manifest.Sha256;
                }
                var version = manifest.Version;
                var current = await CurrentAsync(ct, architecture);
                if (current is not null && AgentUpdateOffer.ParseVersion(version) < AgentUpdateOffer.ParseVersion(current.Version))
                    throw new InvalidDataException("The template is older than the prepared release. Build a newer agent version first.");
                release = new AgentUpdateOffer(version, hash, length) { Architecture = architecture };
                release.Validate();
                File.Move(temporary, PackagePath(release), true);
                generated.Position = 0;
                await using (var output = new FileStream(bundleTemporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    await generated.CopyToAsync(output, ct);
                File.Move(bundleTemporary, BundlePath(release), true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                if (File.Exists(bundleTemporary)) File.Delete(bundleTemporary);
            }
            // Publish the pointer only after the complete package exists. Previous downloads remain valid.
            await using var db = await factory.CreateContextAsync();
            var setting = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == settingName, ct);
            if (setting is null) db.AppSettings.Add(setting = new AppSetting { Name = settingName });
            setting.Value = JsonSerializer.Serialize(release);
            if (!AgentReleaseStartupPrincipal.IsStartup(user)) db.AuditLogs.Add(new AuditLog { AppUserId = userId, UserName = user.Identity?.Name ?? userId.ToString(),
                AuditType = AuditType.Create, ObjectType = "AgentRelease", ObjectName = release.Version,
                ObjectJson = JsonSerializer.Serialize(release), LoggedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
            return release;
        }
        finally { Publication.Release(); }
    }

    public async Task<FileStream?> OpenAsync(string? hash, CancellationToken ct, string architecture = "x64")
    {
        var release = await CurrentAsync(ct, architecture);
        if (release is null || (hash is not null && !string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase))) return null;
        try { return new FileStream(PackagePath(release), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true); }
        catch (FileNotFoundException) { return null; }
    }
    private string PackagePath(AgentUpdateOffer release) => Path.Combine(Folder, release.Sha256.ToUpperInvariant() + ".msi");
    private string BundlePath(AgentUpdateOffer release) => Path.Combine(Folder, release.Sha256.ToUpperInvariant() + ".zip");

    internal async Task<bool> PrepareAtStartupAsync(string architecture, CancellationToken ct)
    {
        var previous = await CurrentAsync(ct, architecture);
        var complete = previous is not null && File.Exists(BundlePath(previous));
        var release = await PublishAsync(AgentReleaseStartupPrincipal.Instance, ct, architecture);
        return !complete || previous != release;
    }

    public async Task<FileStream?> OpenBundleAsync(CancellationToken ct, string architecture = "x64")
    {
        var release = await CurrentAsync(ct, architecture);
        if (release is null) return null;
        try { return new FileStream(BundlePath(release), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true); }
        catch (FileNotFoundException) { return null; }
    }
}
