using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Permission-controlled management of global auto-approval rules and their audit history.</summary>
public sealed class WindowsUpdateAutoApprovalService(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication,
    IAuthorizationService authorization, ILogger<WindowsUpdateAutoApprovalService> logger)
{
    public Task<ServiceResult<List<WindowsUpdateAutoApprovalRule>>> GetAsync() => ServiceOperations.RunAsync(async () =>
    {
        if (await AdminAsync() is null) return ServiceResult<List<WindowsUpdateAutoApprovalRule>>.Fail(ErrorKeys.Action.Forbidden);
        await using var db = await factory.CreateContextAsync();
        return ServiceResult<List<WindowsUpdateAutoApprovalRule>>.Ok(await db.WindowsUpdateAutoApprovalRules.AsNoTracking().OrderBy(x => x.Id).ToListAsync());
    }, logger);

    public Task<ServiceResult<List<InventoryUpdateCategory>>> ClassificationsAsync() => ServiceOperations.RunAsync(async () =>
    {
        if (await AdminAsync() is null) return ServiceResult<List<InventoryUpdateCategory>>.Fail(ErrorKeys.Action.Forbidden);
        await using var db = await factory.CreateContextAsync();
        var catalog = await db.WindowsUpdates.AsNoTracking().Where(x => x.CategoriesJson != null).Select(x => x.CategoriesJson).Distinct().ToListAsync();
        return ServiceResult<List<InventoryUpdateCategory>>.Ok(catalog.SelectMany(WindowsUpdateAutoApproval.Classifications)
            .DistinctBy(x => Guid.Parse(x.Id)).OrderBy(x => x.Name).ToList());
    }, logger);

    public Task<ServiceResult> SaveAsync(WindowsUpdateAutoApprovalRule input) => ServiceOperations.RunAsync(async () =>
    {
        var user = await AdminAsync();
        if (user is null) return ServiceResult.Fail(ErrorKeys.Action.Forbidden);
        if (input.Id < 0 || string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 128 ||
            input.DelayDays is < 0 or > 365 || input.Severities is null || input.ClassificationIds is null ||
            input.Severities.Length > 4 || input.ClassificationIds.Length > 100 ||
            input.Severities.Length + input.ClassificationIds.Length == 0 ||
            input.Severities.Any(x => !WindowsUpdateAutoApproval.Severities.Contains(x)) ||
            input.ClassificationIds.Any(x => !Guid.TryParse(x, out var id) || id == Guid.Empty))
            return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.WindowsUpdateAutoApprovalService.EnterANameADelayOf0365Days));
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await WindowsUpdateAutoApproval.LockAsync(db);
        var rule = input.Id == 0 ? new WindowsUpdateAutoApprovalRule() : await db.WindowsUpdateAutoApprovalRules.FindAsync(input.Id);
        if (rule is null) return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.WindowsUpdateAutoApprovalService.ThisRuleNoLongerExists));
        var create = rule.Id == 0;
        rule.Name = input.Name.Trim(); rule.Enabled = input.Enabled; rule.DelayDays = input.DelayDays;
        rule.Severities = input.Severities.Distinct().Order().ToArray();
        rule.ClassificationIds = input.ClassificationIds.Select(x => Guid.Parse(x).ToString()).Distinct().Order().ToArray();
        rule.AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (create) db.Add(rule);
        await db.SaveChangesAsync();
        db.AuditLogs.Add(Audit(user, rule, create ? AuditType.Create : AuditType.Update));
        await db.SaveChangesAsync(); await transaction.CommitAsync();
        return ServiceResult.Ok();
    }, logger);

    public Task<ServiceResult> DeleteAsync(int id) => ServiceOperations.RunAsync(async () =>
    {
        var user = await AdminAsync();
        if (user is null) return ServiceResult.Fail(ErrorKeys.Action.Forbidden);
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await WindowsUpdateAutoApproval.LockAsync(db);
        var rule = await db.WindowsUpdateAutoApprovalRules.FindAsync(id);
        if (rule is null) return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.WindowsUpdateAutoApprovalService.ThisRuleNoLongerExists));
        db.AuditLogs.Add(Audit(user, rule, AuditType.Delete)); db.Remove(rule);
        await db.SaveChangesAsync(); await transaction.CommitAsync();
        return ServiceResult.Ok();
    }, logger);

    private static AuditLog Audit(ClaimsPrincipal user, WindowsUpdateAutoApprovalRule rule, AuditType type) => new()
    {
        AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "",
        AuditType = type, ObjectType = nameof(WindowsUpdateAutoApprovalRule), ObjectId = rule.Id, ObjectName = rule.Name,
        ObjectJson = JsonSerializer.Serialize(new { rule.Id, rule.Name, rule.Enabled, rule.DelayDays, rule.Severities, rule.ClassificationIds })
    };

    private async Task<ClaimsPrincipal?> AdminAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return user.Identity?.IsAuthenticated == true && int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out _) &&
            (await authorization.AuthorizeAsync(user, Constants.Permissions.ReportWindowsUpdates)).Succeeded ? user : null;
    }
}
