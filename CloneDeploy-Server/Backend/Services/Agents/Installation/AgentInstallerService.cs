using System.Security.AccessControl;
using System.Security.Claims;
using System.Security.Principal;
using System.IO.Compression;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Bundles an unchanged generic installer with private enrollment configuration.</summary>
public sealed class AgentInstallerService(IWebHostEnvironment environment, ServerUrlSettingsStore urls,
    AgentEnrollmentService enrollment, CertificateAuthorityService certificates, ICloneDeployDbFactory factory,
    IAuthorizationService authorization, TimeProvider clock)
{
    private static readonly SemaphoreSlim Generation = new(1, 1);

    public async Task<FileStream> GenerateAsync(ClaimsPrincipal user, CancellationToken ct, string architecture = "x64")
    {
        architecture = CloneDeploy_Agent.Contracts.WindowsArchitecture.Require(architecture);
        if ((!AgentReleaseStartupPrincipal.IsStartup(user) && !(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded) ||
            !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            throw new UnauthorizedAccessException();
        if (!await Generation.WaitAsync(0, ct)) throw new InvalidDataException("Another installer is being generated. Try again shortly.");
        string? path = null;
        try
        {
            var url = await urls.ReadServiceUrlAsync(ct) ?? throw new InvalidDataException("Configure the public application URL in Server Settings first.");
            var key = await enrollment.GetInstallerKeyAsync(user, ct);
            await using var db = await factory.CreateContextAsync();
            var now = clock.GetUtcNow().UtcDateTime;
            if (!await db.AppCertificates.AnyAsync(x => x.Type == CertificateType.Authority && !x.IsRevoked && x.NotBeforeUtc <= now && x.NotAfterUtc > now.AddDays(1), ct))
                throw new InvalidDataException("Create a valid certificate authority before generating an installer.");
            var certificate = await certificates.GetPublicCertificateAsync(user, ct);
            if (!certificate.Success || certificate.Data is null) throw new InvalidDataException("The public CA certificate could not be loaded.");
            var template = Path.Combine(environment.ContentRootPath, "Packaging", "Agent", $"CloneDeploy-Agent-{architecture}.msi");
            if (!File.Exists(template))
                template = Path.Combine(AppContext.BaseDirectory, "Packaging", "Agent", $"CloneDeploy-Agent-{architecture}.msi");
            if (!File.Exists(template) && environment.IsDevelopment())
                template = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "artifacts", "agent-installer", $"CloneDeploy-Agent-{architecture}.msi"));
            if (!File.Exists(template)) throw new InvalidDataException("Agent MSI template is missing. Run Build-AgentInstaller.ps1 and publish the server again.");
            var folder = Directory.CreateDirectory(new ServerDataPaths(environment).AgentInstallers);
            WindowsWorkQueue.PrivateDirectory(folder.FullName);
            path = Path.Combine(folder.FullName, Guid.NewGuid().ToString("N") + ".zip");
            await using var metadata = File.OpenRead(template + ".json");
            var manifest = await AgentPackageManifest.ReadAsync(metadata, architecture, ct);
            await using var package = File.OpenRead(template);
            await manifest.VerifyAsync(package, ct);
            using (var bundle = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                await using (var output = bundle.CreateEntry($"CloneDeploy-Agent-{architecture}.msi", CompressionLevel.NoCompression).Open())
                    await package.CopyToAsync(output, ct);
                await using (var output = bundle.CreateEntry($"CloneDeploy-Agent-{architecture}.msi.json").Open())
                    await JsonSerializer.SerializeAsync(output, manifest, cancellationToken: ct);
                await using var configuration = bundle.CreateEntry("enrollment.json").Open();
                await JsonSerializer.SerializeAsync(configuration, new
                {
                    SchemaVersion = 1, ServerUrl = url, ProvisioningKey = key,
                    CaCertificate = Convert.ToBase64String(certificate.Data)
                }, cancellationToken: ct);
            }
            if (!AgentReleaseStartupPrincipal.IsStartup(user)) db.AuditLogs.Add(new AuditLog
            {
                AppUserId = userId, UserName = user.Identity?.Name ?? userId.ToString(), AuditType = AuditType.Create,
                ObjectType = "AgentInstaller", ObjectName = $"Windows {architecture} agent installer", LoggedAtUtc = clock.GetUtcNow().UtcDateTime,
                ObjectJson = JsonSerializer.Serialize(new { ServerUrl = url, Format = "GenericMsiWithEnrollment" })
            });
            await db.SaveChangesAsync(ct);
            ct.ThrowIfCancellationRequested();
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            path = null; // MVC disposes the stream after download, deleting the credential-bearing copy.
            return stream;
        }
        finally
        {
            try { if (path is not null) File.Delete(path); }
            finally { Generation.Release(); }
        }
    }
}
