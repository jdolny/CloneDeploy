using System.Security.Claims;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
namespace CloneDeploy_Server.Backend.Services;

public sealed class SmtpSettingsForm
{
    public EmailServerSettings Settings { get; set; } = new();
    public bool HasPassword { get; set; }
    public string Password { get; set; } = "";
    public bool ClearPassword { get; set; }
}
public sealed class EmailNotificationService(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication,
    IAuthorizationService authorization, ISecretProtector secrets, IEmailTransport transport, TimeProvider clock, ILogger<EmailNotificationService> logger)
{
    private Task<ServiceResult<T>> Run<T>(Func<Task<ServiceResult<T>>> action) => ServiceOperations.RunAsync(action, logger, "Device.Failed");
    private static ServiceResult<T> Error<T>(string text) => ServiceResult<T>.Fail("ServerSettings.Validation", text);
    private async Task<int?> CallerAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return user.Identity?.IsAuthenticated == true && int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    }
    private async Task<bool> AdminAsync() => await CallerAsync() != null &&
        (await authorization.AuthorizeAsync((await authentication.GetAuthenticationStateAsync()).User, Constants.Permissions.AdminOnly)).Succeeded;
    public Task<ServiceResult<SmtpSettingsForm>> ReadServerAsync() => Run(async () =>
    {
        if (!await AdminAsync()) return Error<SmtpSettingsForm>("Administrator access is required.");
        await using var db = await factory.CreateContextAsync();
        var settings = await db.Set<EmailServerSettings>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1) ?? new();
        var hasPassword = settings.PasswordEncrypted.Length > 0;
        settings.PasswordEncrypted = "";
        return ServiceResult<SmtpSettingsForm>.Ok(new() { Settings = settings, HasPassword = hasPassword });
    });
    public Task<ServiceResult<bool>> SaveServerAsync(SmtpSettingsForm form) => Run(async () =>
    {
        if (!await AdminAsync()) return Error<bool>("Administrator access is required.");
        var s = form.Settings;
        if (s.Host.Length > 253 || s.Host.Any(char.IsWhiteSpace) || s.Host.Contains('/') || s.Port is < 1 or > 65535 || s.Port == 465
            || s.UserName.Length > 254 || s.FromName.Length > 200 || s.FromName.Any(c => c is '\r' or '\n') || form.Password.Length > 4096)
            return Error<bool>("Enter a valid SMTP host and port. Use STARTTLS (usually port 587); implicit TLS on port 465 is not supported.");
        if (s.Enabled && (string.IsNullOrWhiteSpace(s.Host) || !SmtpEmailTransport.ValidAddress(s.FromAddress.Trim())))
            return Error<bool>("Enter an SMTP host and a valid sender email address.");
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(68330, 0)");
        var current = await db.Set<EmailServerSettings>().SingleOrDefaultAsync(x => x.Id == 1);
        if ((current?.Revision ?? Guid.Empty) != s.Revision) return Error<bool>("Email settings changed. Reload before saving.");
        if (current is null) { current = new(); db.Add(current); }
        var password = form.ClearPassword ? "" : current.PasswordEncrypted;
        if (form.Password.Length > 0) password = secrets.Protect(form.Password);
        if (s.Enabled && s.UserName.Length > 0 && password.Length == 0) return Error<bool>("Enter the SMTP account password.");
        current.Enabled = s.Enabled; current.Host = s.Host.Trim(); current.Port = s.Port; current.StartTls = s.StartTls;
        current.UserName = s.UserName.Trim(); current.PasswordEncrypted = password; current.FromName = s.FromName.Trim(); current.FromAddress = s.FromAddress.Trim(); current.Revision = Guid.NewGuid();
        db.AuditLogs.Add(new AuditLog { AppUserId = (await CallerAsync())!.Value, UserName = (await authentication.GetAuthenticationStateAsync()).User.Identity?.Name ?? "Administrator",
            AuditType = Backend.Enum.AuditType.Update, ObjectType = "ServerSettings", ObjectName = "Email settings", ObjectJson = "{\"Section\":\"Email\"}" });
        await db.SaveChangesAsync(); await tx.CommitAsync();
        return ServiceResult<bool>.Ok(true);
    });
    public Task<ServiceResult<bool>> TestServerAsync(string address) => Run(async () =>
    {
        if (!await AdminAsync()) return Error<bool>("Administrator access is required.");
        if (!SmtpEmailTransport.ValidAddress(address.Trim())) return Error<bool>("Enter a valid recipient email address.");
        await using var db = await factory.CreateContextAsync();
        var settings = await db.Set<EmailServerSettings>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1 && x.Enabled);
        if (settings is null) return Error<bool>("Save and enable SMTP settings first.");
        var outcome = await transport.SendAsync(settings, address.Trim(), "CloneDeploy test email", "CloneDeploy SMTP delivery is configured successfully.", Guid.NewGuid().ToString("N"), default);
        return outcome == EmailSendOutcome.Accepted ? ServiceResult<bool>.Ok(true) : Error<bool>("SMTP did not confirm delivery. Check the server configuration and recipient.");
    });
    public Task<ServiceResult<UserEmailSubscription>> ReadSubscriptionAsync(int userId) => Run(async () =>
    {
        if (await CallerAsync() != userId && !await AdminAsync()) return Error<UserEmailSubscription>("Manage email notifications from your own account options.");
        await using var db = await factory.CreateContextAsync();
        var value = await db.Set<UserEmailSubscription>().AsNoTracking().SingleOrDefaultAsync(x => x.AppUserId == userId)
            ?? new() { AppUserId = userId, TimeZoneId = TimeZoneInfo.Local.Id };
        value.Address = (await db.AppUsers.Where(x => x.Id == userId).Select(x => x.Email).SingleOrDefaultAsync())?.Trim() ?? "";
        var access = await DirectoryApplicationAccess.ForUserAsync(db, userId);
        value.AdminDigestsAvailable = access.Allowed && access.Administrator;
        return ServiceResult<UserEmailSubscription>.Ok(value);
    });
    public Task<ServiceResult<bool>> SaveSubscriptionAsync(UserEmailSubscription input) => SavePreferencesAsync(input, null);

    public Task<ServiceResult<bool>> SaveUserOptionsAsync(UserEmailSubscription input, AppUserOptions options) => SavePreferencesAsync(input, options);

    private Task<ServiceResult<bool>> SavePreferencesAsync(UserEmailSubscription input, AppUserOptions? options) => Run(async () =>
    {
        if (await CallerAsync() != input.AppUserId && !await AdminAsync()) return Error<bool>("You can only change your own subscriptions.");
        if (options is not null && !UiLanguages.IsSupported(options.Language)) return ServiceResult<bool>.Fail(LocKeys.User.UnsupportedLanguage);
        if (input.DeliveryMinute is < 0 or >= 1440 || !System.Enum.IsDefined(input.WeeklyDay)) return Error<bool>("Choose a valid delivery time and weekly day.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(input.TimeZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return Error<bool>("Choose a valid timezone."); }
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(68331, {input.AppUserId})");
        var current = await db.Set<UserEmailSubscription>().SingleOrDefaultAsync(x => x.AppUserId == input.AppUserId);
        if (current is null && input.Revision == Guid.Empty)
        {
            current = new() { AppUserId = input.AppUserId };
            db.Add(current);
        }
        if (current is null || current.Revision != input.Revision) return Error<bool>("Reload changed notification options before saving.");
        var address = (await db.AppUsers.Where(x => x.Id == input.AppUserId).Select(x => x.Email).SingleOrDefaultAsync())?.Trim() ?? "";
        if ((input.Daily || input.Weekly) && !SmtpEmailTransport.ValidAddress(address))
            return ServiceResult<bool>.Fail("Email.AccountAddressRequired");
        if ((!current.Daily && input.Daily) || (!current.Weekly && input.Weekly)) current.EnabledAtUtc = clock.GetUtcNow().UtcDateTime;
        current.Daily = input.Daily; current.Weekly = input.Weekly; current.SkipEmpty = input.SkipEmpty;
        current.Approvals = input.Approvals; current.Imaging = input.Imaging; current.Policies = input.Policies; current.Actions = input.Actions;
        current.DirectorySync = input.DirectorySync; current.DeviceWarnings = input.DeviceWarnings;
        current.HelpDesk = input.HelpDesk;
        var access = await DirectoryApplicationAccess.ForUserAsync(db, input.AppUserId);
        current.ServerUpdates = input.ServerUpdates && access.Allowed && access.Administrator;
        current.Security = input.Security && access.Allowed && access.Administrator;
        current.TimeZoneId = input.TimeZoneId; current.DeliveryMinute = input.DeliveryMinute; current.WeeklyDay = input.WeeklyDay; current.Revision = Guid.NewGuid();
        if (options is not null)
        {
            var appearance = await db.AppUserOptions.SingleOrDefaultAsync(x => x.AppUserId == input.AppUserId);
            if (appearance is null) { appearance = new() { AppUserId = input.AppUserId }; db.Add(appearance); }
            appearance.ColorMode = options.ColorMode;
            appearance.Language = UiLanguages.Normalize(options.Language);
        }
        await db.SaveChangesAsync(); await tx.CommitAsync(); return ServiceResult<bool>.Ok(true);
    });

    public Task<ServiceResult<List<EmailDigestDelivery>>> HistoryAsync(int userId) => Run(async () =>
    {
        if (await CallerAsync() != userId && !await AdminAsync()) return Error<List<EmailDigestDelivery>>("You can only view your own delivery history.");
        await using var db = await factory.CreateContextAsync();
        return ServiceResult<List<EmailDigestDelivery>>.Ok(await db.Set<EmailDigestDelivery>().AsNoTracking().Where(x => x.AppUserId == userId).OrderByDescending(x => x.UpdatedAtUtc).Take(10).ToListAsync());
    });
}
