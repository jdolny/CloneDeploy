using CloneDeploy_Server.Localization;
using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.WinPe;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Reports setup status and queues the prerequisites pinned by this application release.
/// </summary>
public sealed class WinPeSetupService(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication,
    IAuthorizationService authorization, BuildStorage storage)
{
    public WindowsWorkQueue? Windows { get; set; }
    public async Task<ServiceResult<SetupCatalogDto>> GetAsync()
    {
        if (await RequireAdmin() is null) return ServiceResult<SetupCatalogDto>.Fail(ErrorKeys.WinPe.Forbidden);
        await using var db = await factory.CreateContextAsync();
        var jobs = await db.WinPeSetupJobs.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc).ToListAsync();
        foreach (var job in jobs.Where(x => SetupFiles.Status(storage, x.Id).State == "Queued" && !File.Exists(SetupFiles.PathFor(storage, x.Id, "request.json"))))
            await storage.WriteAsync(SetupFiles.PathFor(storage, job.Id, "request.json"), JsonSerializer.Deserialize<SetupRequestDto>(job.RequestJson)!);
        return ServiceResult<SetupCatalogDto>.Ok(new(jobs.Select(x => new SetupJobRowDto(x, SetupFiles.Status(storage, x.Id), JsonSerializer.Deserialize<SetupRequestDto>(x.RequestJson)!.Toolchain.Version)).ToList(),
            storage.Read<SetupHealthDto>(Path.Combine(storage.Root, "setup-health.json"))));
    }

    public async Task<ServiceResult<Guid>> QueueAsync(bool acceptLicense)
    {
        var user = await RequireAdmin();
        if (user is null) return ServiceResult<Guid>.Fail(ErrorKeys.WinPe.Forbidden);
        if (!acceptLicense) return ServiceResult<Guid>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeSetupService.AcceptTheMicrosoftADKAndWinPELicenseTermsBefore));
        var health = storage.Read<SetupHealthDto>(Path.Combine(storage.Root, "setup-health.json"));
        if (health is null || !WinPeToolchainPolicy.SupportsApplication(health) || DateTime.UtcNow - health.SeenAtUtc > TimeSpan.FromSeconds(30))
            return ServiceResult<Guid>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeSetupService.StartTheWindowsBuilderFromThisApplicationReleaseTo));
        if (!health.Elevated) return ServiceResult<Guid>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeSetupService.RunTheBuilderAsAdministratorBeforeInstallingPrerequisites));
        if (health.RestartRequired) return ServiceResult<Guid>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeSetupService.RestartWindowsBeforeContinuingPrerequisiteSetup));
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"WinPeSetupJobs\" IN SHARE ROW EXCLUSIVE MODE");
        if ((await db.WinPeSetupJobs.ToListAsync()).Any(x => !SetupFiles.Status(storage, x.Id).IsComplete)) return ServiceResult<Guid>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeSetupService.APrerequisiteInstallationIsAlreadyQueuedOrRunning));
        var spec = ToolchainSpecDto.MicrosoftDefault;
        try { BuildSetupEngine.ValidateTransition(spec); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        { return ServiceResult<Guid>.Fail(ErrorKeys.WinPe.Validation, exception.Message); }
        // Retain the existing FK and historical jobs without exposing editable toolchains.
        // The request is always made from compiled constants, never database installer values.
        var name = "Application WinPE " + spec.Version;
        var toolchain = await db.WinPeToolchains.SingleOrDefaultAsync(x => x.Name == name);
        if (toolchain is null)
        {
            toolchain = WinPeToolchain.From(spec with { Name = name });
            db.WinPeToolchains.Add(toolchain);
            await db.SaveChangesAsync();
        }
        var job = new WinPeSetupJob { ToolchainId = toolchain.Id, CreatedBy = user.Identity?.Name ?? "Administrator" };
        var request = new SetupRequestDto(job.Id, spec, job.CreatedAtUtc)
        { RemoteExecution = Windows is not null && await Windows.HostAsync(CloneDeploy.SiteServices.WindowsWorkerRoles.WinPe, default) is not null };
        job.RequestJson = JsonSerializer.Serialize(request);
        Directory.CreateDirectory(SetupFiles.DirectoryPath(storage, job.Id)); BuildStorage.RejectLinks(SetupFiles.DirectoryPath(storage, job.Id));
        db.WinPeSetupJobs.Add(job); Audit(db, user, toolchain.Id, spec.Name, new { Operation = "InstallPrerequisites", job.Id, request.Toolchain.Version, AcceptedLicense = true });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await storage.WriteAsync(SetupFiles.PathFor(storage, job.Id, "request.json"), request);
        return ServiceResult<Guid>.Ok(job.Id);
    }

    private async Task<ClaimsPrincipal?> RequireAdmin()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded || !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out _)) return null;
        return user;
    }

    private static void Audit(CloneDeployDbContext db, ClaimsPrincipal user, int id, string name, object detail) => db.AuditLogs.Add(new AuditLog
    {
        AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "Administrator",
        AuditType = AuditType.Update, ObjectType = nameof(WinPeToolchain), ObjectId = id, ObjectName = name, ObjectJson = JsonSerializer.Serialize(detail)
    });

}
