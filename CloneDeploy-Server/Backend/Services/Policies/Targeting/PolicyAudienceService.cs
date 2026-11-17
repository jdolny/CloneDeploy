using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
namespace CloneDeploy_Server.Backend.Services;

public sealed record PolicyAudienceOption(int SourceId, Guid ObjectGuid, string Name, string DirectoryName, DirectoryObjectKind Kind, bool Available);
public sealed class PolicyAudienceState
{
    public int PolicyId { get; set; }
    public Guid Revision { get; set; }
    public bool SelfServiceEnabled { get; set; }
    public bool AnyInteractiveUser { get; set; }
    public bool RestrictSelfServiceToDeviceTargets { get; set; }
    public int? CategoryId { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public byte[]? IconPng { get; set; }
    public List<SelfServiceCategory> Categories { get; set; } = [];
    public int? LoginAudienceId { get; set; }
    public int? SelfServiceAudienceId { get; set; }
    public List<UserAudience> Audiences { get; set; } = [];
    public List<PolicyAudienceOption> Options { get; set; } = [];
}
/// <summary>Edits directory audiences independently from policy actions and device assignments.</summary>
public sealed class PolicyAudienceService(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication, IAuthorizationService authorization)
{
    public async Task<ServiceResult<string>> PreviewAsync(PolicyAudienceState state, int sourceId, Guid objectGuid)
    {
        if (await UserAsync(Constants.Permissions.UserAudienceView) is null) return ServiceResult<string>.Fail("SharedResource.Forbidden");
        if (await UserAsync(Constants.Permissions.PolicyView) is null) return ServiceResult<string>.Fail("Policy.Forbidden");
        await using var db = await factory.CreateContextAsync();
        var sid = await db.DirectoryObjects.Where(x => x.DirectorySourceId == sourceId && x.ObjectGuid == objectGuid && x.Kind == DirectoryObjectKind.User)
            .Select(x => x.ObjectSid).SingleOrDefaultAsync();
        var match = await PolicyAudienceDelivery.PreviewUserAsync(db, sid, state.LoginAudienceId, state.SelfServiceAudienceId, default);
        var trigger = await db.Policies.Where(x => x.Id == state.PolicyId).Select(x => x.Trigger).SingleOrDefaultAsync();
        var audienceLabel = trigger == PolicyTrigger.Login ? "Login user audience" : "Notification recipient audience";
        return ServiceResult<string>.Ok($"{audienceLabel}: {(match.Login ? "matches" : "does not match")}. Self-service audience: {(state.AnyInteractiveUser ? "any interactive user on targeted devices (no directory match required)" : match.SelfService ? "matches" : "does not match")}. This checks current selections and directory membership only; activation, device restrictions and self-service availability still apply.");
    }

    private async Task<ClaimsPrincipal?> UserAsync(string permission)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return (await authorization.AuthorizeAsync(user, permission)).Succeeded ? user : null;
    }
    public async Task<ServiceResult<PolicyAudienceState>> GetAsync(int policyId)
    {
        if (await UserAsync(Constants.Permissions.PolicyView) is null) return ServiceResult<PolicyAudienceState>.Fail("Policy.Forbidden");
        await using var db = await factory.CreateContextAsync();
        if (!await db.Policies.AnyAsync(x => x.Id == policyId)) return ServiceResult<PolicyAudienceState>.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyAudienceService.PolicyNotFound));
        var access = await db.Set<PolicyUserAccess>().AsNoTracking().SingleOrDefaultAsync(x => x.PolicyId == policyId);
        var canViewCategories = await UserAsync(Constants.Permissions.SelfServiceCategoryView) is not null;
        var canViewAudiences = await UserAsync(Constants.Permissions.UserAudienceView) is not null;
        var categoryId = access?.CategoryId;
        var audienceIds = new[] { access?.LoginAudienceId, access?.SelfServiceAudienceId }.OfType<int>().ToArray();
        var state = new PolicyAudienceState { PolicyId = policyId, Revision = access?.Revision ?? Guid.Empty,
            Icon = access?.Icon, IconPng = access?.IconPng, CategoryId = access?.CategoryId, DisplayName = access?.DisplayName, Description = access?.Description,
            Categories = await db.Set<SelfServiceCategory>().AsNoTracking().Where(x => canViewCategories || x.Id == categoryId).OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(),
            SelfServiceEnabled = access?.SelfServiceEnabled ?? false, AnyInteractiveUser = access?.AnyInteractiveUser ?? false, RestrictSelfServiceToDeviceTargets = access?.RestrictSelfServiceToDeviceTargets ?? false,
            LoginAudienceId = access?.LoginAudienceId, SelfServiceAudienceId = access?.SelfServiceAudienceId,
            Audiences = await db.Set<UserAudience>().AsNoTracking().Where(x => canViewAudiences || audienceIds.Contains(x.Id)).OrderBy(x => x.Name).ToListAsync() };
        state.Options = canViewAudiences ? await UserAudienceService.ReadOptionsAsync(db) : [];
        return ServiceResult<PolicyAudienceState>.Ok(state);
    }
    public async Task<ServiceResult> SaveAsync(PolicyAudienceState state)
    {
        var user = await UserAsync(Constants.Permissions.PolicyEdit);
        if (user is null) return ServiceResult.Fail("Policy.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(682947234, {state.PolicyId})");
        await UserAudienceService.LockAsync(db);
        var policy = await db.Policies.SingleOrDefaultAsync(x => x.Id == state.PolicyId);
        if (policy is null || policy.Archived || policy.ArchivedAtUtc != null) return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyAudienceService.PolicyIsUnavailable));
        var current = await db.Set<PolicyUserAccess>().SingleOrDefaultAsync(x => x.PolicyId == state.PolicyId);
        if ((current?.Revision ?? Guid.Empty) != state.Revision) return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyAudienceService.AudiencesChangedElsewhereReloadBeforeSaving));
        var selectedIds = new[] { state.LoginAudienceId, state.SelfServiceAudienceId }.OfType<int>().Distinct().ToArray();
        if (await db.Set<UserAudience>().CountAsync(x => selectedIds.Contains(x.Id)) != selectedIds.Length)
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyAudienceService.ASelectedUserAudienceNoLongerExistsReloadBefore));
        if (state.LoginAudienceId != null && policy.Trigger != PolicyTrigger.Login && !await NotificationPolicyAudience.EligibleAsync(db, policy.Id))
            return ServiceResult.Fail("Policy.Validation", NotificationPolicyAudience.ValidationMessage);
        if (state.SelfServiceEnabled && !state.AnyInteractiveUser && state.SelfServiceAudienceId is null)
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyAudienceService.SelectAUserAudienceForDirectoryBasedSelfService));
        if (state.SelfServiceEnabled && state.AnyInteractiveUser &&
            !await db.DevicePolicies.AnyAsync(x => x.PolicyId == state.PolicyId) && !await db.GroupPolicies.AnyAsync(x => x.PolicyId == state.PolicyId) && !await db.DirectoryOuPolicies.AnyAsync(x => x.PolicyId == state.PolicyId))
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyAudienceService.SaveDeviceTargetsBeforeAllowingAnyInteractiveUser));
        if (state.DisplayName?.Trim().Length > 120 || state.Description?.Trim().Length > 1000)
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyAudienceService.PortalNameMustBeAtMost120CharactersAnd));
        if (state.CategoryId.HasValue && !await db.Set<SelfServiceCategory>().AnyAsync(x => x.Id == state.CategoryId))
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyAudienceService.TheSelectedCategoryNoLongerExistsReloadUserAccess));
        if (state.Icon is not null && !CloneDeploy_Agent.Contracts.PortalCategoryIcons.Keys.Contains(state.Icon))
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyAudienceService.SelectAListedPortalIcon));
        if (state.IconPng is not null && !PortalIcon.IsValid(state.IconPng))
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyAudiencesTab.UseAPNGUpTo64KiBAnd512));
        if (current is null) { current = new() { PolicyId = state.PolicyId }; db.Add(current); }
        current.LoginAudienceId = state.LoginAudienceId; current.SelfServiceAudienceId = state.SelfServiceAudienceId;
        current.Revision = Guid.NewGuid(); current.SelfServiceEnabled = state.SelfServiceEnabled;
        current.CategoryId = state.CategoryId;
        current.Icon = state.Icon;
        current.IconPng = state.IconPng;
        current.IconHash = state.IconPng is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(state.IconPng));
        current.DisplayName = string.IsNullOrWhiteSpace(state.DisplayName) ? null : state.DisplayName.Trim();
        current.Description = string.IsNullOrWhiteSpace(state.Description) ? null : state.Description.Trim();
        current.AnyInteractiveUser = state.AnyInteractiveUser;
        current.RestrictSelfServiceToDeviceTargets = state.AnyInteractiveUser || state.RestrictSelfServiceToDeviceTargets;
        state.RestrictSelfServiceToDeviceTargets = current.RestrictSelfServiceToDeviceTargets;
        db.AuditLogs.Add(new AuditLog { AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "",
            AuditType = AuditType.Update, ObjectType = nameof(Policy), ObjectId = policy.Id, ObjectName = policy.Guid.ToString(),
            ObjectJson = JsonSerializer.Serialize(new { Section = "User audiences", state.SelfServiceEnabled, state.AnyInteractiveUser, state.CategoryId, state.DisplayName, state.Description, state.Icon, current.IconHash, state.RestrictSelfServiceToDeviceTargets, state.LoginAudienceId, state.SelfServiceAudienceId }) });
        await db.SaveChangesAsync(); await tx.CommitAsync();
        state.Revision = current.Revision;
        return ServiceResult.Ok();
    }
}
