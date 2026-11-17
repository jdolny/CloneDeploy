using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CloneDeploy_Server.Backend.Services;

public sealed class EntraSettingsDto
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public bool SsoEnabled { get; set; }
    public string? RedirectUri { get; init; }
    public string? AgentAdminRedirectUri { get; init; }
    public string? PortalRedirectUri { get; init; }
    public string? CertificatePem { get; set; }
    public string? PendingCertificatePem { get; set; }
    public DateTime? CertificateExpiresUtc { get; set; }
    public DateTime? PendingCertificateExpiresUtc { get; set; }
}

public sealed class EntraAdministration(
    ICloneDeployDbFactory factory,
    EntraCredentials credentials,
    EntraGraphReader graph,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization,
    IOptionsMonitorCache<OpenIdConnectOptions> optionsCache,
    ILogger<EntraAdministration> logger,
    ServerUrlSettingsStore urls)
{
    private async Task<ServiceResult<T>> Run<T>(Func<Task<T>> action) => await ServiceOperations.RunAsync(async () =>
    {
        if (!(await authorization.AuthorizeAsync((await authentication.GetAuthenticationStateAsync()).User, Constants.Permissions.AdminOnly)).Succeeded)
            return ServiceResult<T>.Fail("Directory.Forbidden");
        try { return ServiceResult<T>.Ok(await action()); }
        catch (InvalidDataException ex) { return ServiceResult<T>.Fail("Directory.Validation", ex.Message); }
        catch (InvalidOperationException ex) { return ServiceResult<T>.Fail("Directory.Validation", ex.Message); }
        catch (Microsoft.Identity.Client.MsalException) { return ServiceResult<T>.Fail("Directory.Validation", UiText.Get(LocKeys.Ui.EntraAdministration.EntraRefusedCertificateAuthenticationVerifyTenantAndClientIDs)); }
        catch (OperationCanceledException) { return ServiceResult<T>.Fail("Directory.Validation", UiText.Get(LocKeys.Ui.EntraAdministration.TheEntraOperationTimedOutRetryAfterCheckingConnectivity)); }
    }, logger, "Directory.Failed", null, "Entra administration");

    public Task<ServiceResult<EntraSettingsDto>> GetAsync() => Run(async () =>
    {
        await using var db = await factory.CreateContextAsync();
        var stored = await db.Set<EntraConnection>().AsNoTracking().SingleOrDefaultAsync();
        var publicUrl = await urls.ReadAsync();
        if (stored is null) return new EntraSettingsDto { AgentAdminRedirectUri = publicUrl is null ? null : (await urls.RequirePortalUrlAsync()) + "signin-entra-agent-admin", PortalRedirectUri = publicUrl is null ? null : (await urls.RequirePortalUrlAsync()) + "signin-entra-portal", RedirectUri = publicUrl is null ? null : publicUrl + "signin-entra" };
        var dto = new EntraSettingsDto { TenantId = stored.TenantId.ToString(), ClientId = stored.ClientId.ToString(),
            SsoEnabled = stored.SsoEnabled, AgentAdminRedirectUri = publicUrl is null ? null : (await urls.RequirePortalUrlAsync()) + "signin-entra-agent-admin", PortalRedirectUri = publicUrl is null ? null : (await urls.RequirePortalUrlAsync()) + "signin-entra-portal", RedirectUri = publicUrl is null ? null : publicUrl + "signin-entra" };
        if (stored.CertificateEncrypted is { } encrypted)
        {
            using var certificate = credentials.Open(encrypted);
            dto.CertificatePem = certificate.ExportCertificatePem(); dto.CertificateExpiresUtc = certificate.NotAfter.ToUniversalTime();
        }
        if (stored.PendingCertificateEncrypted is { } pending)
        {
            using var certificate = credentials.Open(pending);
            dto.PendingCertificatePem = certificate.ExportCertificatePem(); dto.PendingCertificateExpiresUtc = certificate.NotAfter.ToUniversalTime();
        }
        return dto;
    });

    public Task<ServiceResult<bool>> SaveAsync(EntraSettingsDto input) => Run(async () =>
    {
        if (!Guid.TryParse(input.TenantId, out var tenant) || tenant == Guid.Empty || !Guid.TryParse(input.ClientId, out var client) || client == Guid.Empty)
            throw new InvalidOperationException("Enter valid tenant and application (client) IDs.");
        if (input.SsoEnabled) await urls.RequireAsync();
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await LockAsync(db);
        var stored = await db.Set<EntraConnection>().SingleOrDefaultAsync();
        if (stored is null) { stored = new(); db.Add(stored); }
        if (stored.TenantId != Guid.Empty && stored.TenantId != tenant &&
            (await db.DirectorySources.AnyAsync(x => x.Provider == DirectoryProvider.EntraId) || await db.UserLogins.AnyAsync(x => x.LoginProvider == "Entra")))
            throw new InvalidOperationException("Remove the Entra directory and Entra application accounts before changing tenants.");
        if (input.SsoEnabled && stored.CertificateEncrypted is null)
            throw new InvalidOperationException("Generate, upload and activate a certificate before enabling SSO.");
        stored.TenantId = tenant; stored.ClientId = client; stored.SsoEnabled = input.SsoEnabled;
        await db.SaveChangesAsync();
        // Invalidate existing external sessions when their authentication configuration changes.
        await db.Users.Where(x => db.UserLogins.Any(l => l.UserId == x.Id && l.LoginProvider == "Entra"))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, Guid.NewGuid().ToString()));
        await tx.CommitAsync();
        optionsCache.TryRemove("Entra"); optionsCache.TryRemove("EntraPortal"); optionsCache.TryRemove("EntraAgentAdmin");
        return true;
    });

    public Task<ServiceResult<bool>> GenerateAsync() => Run(async () =>
    {
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(); await LockAsync(db);
        var stored = await db.Set<EntraConnection>().SingleOrDefaultAsync() ?? throw new InvalidOperationException("Save tenant and client IDs first.");
        if (stored.PendingCertificateEncrypted is not null) throw new InvalidOperationException("Activate or discard the pending certificate first.");
        stored.PendingCertificateEncrypted = credentials.Generate();
        await db.SaveChangesAsync(); await tx.CommitAsync(); return true;
    });

    public Task<ServiceResult<bool>> ActivateAsync(bool testGraph) => Run(async () =>
    {
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(); await LockAsync(db);
        var stored = await db.Set<EntraConnection>().SingleAsync();
        if (stored.PendingCertificateEncrypted is null) throw new InvalidOperationException("Generate a pending certificate first.");
        using var certificate = credentials.Open(stored.PendingCertificateEncrypted);
        if (certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow) throw new InvalidOperationException("Generate a new certificate; this certificate has expired.");
        if (testGraph)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var token = await credentials.GetGraphTokenAsync(stored, true, timeout.Token);
            await graph.TestAsync(token, timeout.Token, await db.DirectorySources.AnyAsync(x => x.Provider == DirectoryProvider.EntraId && !x.ApplicationAccessOnly));
        }
        // SSO-only setup must not require any Graph permissions. Its credential is verified
        // by the first OIDC code exchange; keep the previous Entra certificate until that succeeds.
        stored.CertificateEncrypted = stored.PendingCertificateEncrypted; stored.PendingCertificateEncrypted = null;
        await db.SaveChangesAsync(); await tx.CommitAsync(); optionsCache.TryRemove("Entra"); optionsCache.TryRemove("EntraPortal"); optionsCache.TryRemove("EntraAgentAdmin"); return true;
    });

    public Task<ServiceResult<bool>> DiscardPendingAsync() => Run(async () =>
    {
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(); await LockAsync(db);
        var stored = await db.Set<EntraConnection>().SingleAsync(); stored.PendingCertificateEncrypted = null;
        await db.SaveChangesAsync(); await tx.CommitAsync(); return true;
    });

    public Task<ServiceResult<bool>> TestAsync() => Run(async () =>
    {
        await using var db = await factory.CreateContextAsync();
        var stored = await db.Set<EntraConnection>().SingleOrDefaultAsync();
        if (stored is null || stored.TenantId == Guid.Empty || stored.ClientId == Guid.Empty ||
            string.IsNullOrWhiteSpace(stored.CertificateEncrypted))
            throw new InvalidOperationException(UiText.Get(LocKeys.Ui.DirectorySourceService.ConfigureAndActivateTheEntraApplicationCertificateFirst));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await graph.TestAsync(await credentials.GetGraphTokenAsync(stored, false, timeout.Token), timeout.Token, await db.DirectorySources.AnyAsync(x => x.Provider == DirectoryProvider.EntraId && !x.ApplicationAccessOnly)); return true;
    });

    public static Task LockAsync(CloneDeployDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('directory-configuration', 0))", ct);
}
