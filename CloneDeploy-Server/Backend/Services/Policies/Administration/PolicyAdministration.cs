using System.Data;
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

/// <summary>
/// Manages policy details, selected actions, archiving, and history.
/// </summary>
public sealed class PolicyAdministration(
    ICloneDeployDbFactory factory,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization)
{
    public async Task<ServiceResult<List<Policy>>> GetAsync(string? search = null, bool includeArchived = false)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.PolicyView)).Succeeded)
            return ServiceResult<List<Policy>>.Fail("Policy.Forbidden");

        await using var db = await factory.CreateContextAsync();
        var query = db.Policies.AsNoTracking();
        if (!includeArchived) query = query.Where(x => !x.Archived && x.ArchivedAtUtc == null);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(x => EF.Functions.ILike(x.Name, pattern) || (x.Description != null && EF.Functions.ILike(x.Description, pattern)));
        }

        return ServiceResult<List<Policy>>.Ok(await query.Include(x => x.PolicyTags).OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync());
    }

    public async Task<ServiceResult<Policy>> CreateAsync(string name, string? description)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.PolicyView)).Succeeded)
            return ServiceResult<Policy>.Fail("Policy.Forbidden");
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.PolicyEdit)).Succeeded)
            return ServiceResult<Policy>.Fail("Policy.Forbidden");

        if (string.IsNullOrWhiteSpace(name)) return ServiceResult<Policy>.Fail("Policy.Validation", UiText.Get(LocKeys.CustomAttribute.Validation));
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        name = name.Trim();
        if (await db.Policies.AnyAsync(x => x.Name.ToLower() == name.ToLower())) return ServiceResult<Policy>.Fail(ErrorKeys.Common.AlreadyExists, name);
        var policy = new Policy { Name = name, Description = description, Guid = Guid.NewGuid(), StartDateUtc = DateTime.UtcNow.Date };
        db.Policies.Add(policy);
        await db.SaveChangesAsync();
        if (!await AuditAsync(db, policy, AuditType.Create, "General"))
            return ServiceResult<Policy>.Fail("Policy.Forbidden");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult<Policy>.Ok(policy);
    }

    public async Task<ServiceResult<PolicyDetailsDto>> GetDetailsAsync(int id)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.PolicyView)).Succeeded)
            return ServiceResult<PolicyDetailsDto>.Fail("Policy.Forbidden");
        await using var db = await factory.CreateContextAsync();
        var policy = await db.Policies.AsNoTracking().AsSplitQuery().Include(x => x.PolicyActions)
            .Include(x => x.PolicyTags).SingleOrDefaultAsync(x => x.Id == id);
        if (policy is null) return ServiceResult<PolicyDetailsDto>.Fail(ErrorKeys.Common.NotFound, id);
        var canViewTags = (await authorization.AuthorizeAsync(user, Constants.Permissions.TagView)).Succeeded;
        return ServiceResult<PolicyDetailsDto>.Ok(new PolicyDetailsDto
        {
            Policy = policy,
            Active = await db.ActiveCompiledPolicies.AnyAsync(x => x.PolicyId == id),
            SelectedTags = policy.PolicyTags.Select(x => x.TagId).ToArray(),
            Tags = await db.Tags.AsNoTracking().Where(x => canViewTags || policy.PolicyTags.Select(t => t.TagId).Contains(x.Id)).OrderBy(x => x.Name).ToListAsync()
        });
    }

    public async Task<ServiceResult<List<PolicyRelationDto>>> GetRelationsAsync(int id)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.PolicyView)).Succeeded)
            return ServiceResult<List<PolicyRelationDto>>.Fail("Policy.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var policy = await db.Policies.AsNoTracking().Include(x => x.PolicyActions).SingleOrDefaultAsync(x => x.Id == id);
        if (policy is null) return ServiceResult<List<PolicyRelationDto>>.Fail(ErrorKeys.Common.NotFound, id);
        var details = new PolicyDetailsDto { Policy = policy };
        await ReadActionOptionsAsync(details, db, true);
        var canViewDevices = (await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceView)).Succeeded;
        return ServiceResult<List<PolicyRelationDto>>.Ok(await PolicyRelations.ReadAsync(db, details, canViewDevices));
    }

    // Returns only the requested section. The page merges it into its existing draft.
    public async Task<ServiceResult<PolicyDetailsDto>> GetSectionAsync(int id, string section)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.PolicyView)).Succeeded)
            return ServiceResult<PolicyDetailsDto>.Fail("Policy.Forbidden");
        await using var db = await factory.CreateContextAsync();
        var policy = await db.Policies.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        if (policy is null) return ServiceResult<PolicyDetailsDto>.Fail(ErrorKeys.Common.NotFound, id);
        var details = new PolicyDetailsDto { Policy = policy };
        switch (section)
        {
            case "schedule":
            case "triggers":
                break;
            case "execution":
                details.Conditions = await db.ScriptActions.AsNoTracking().Where(x => x.IsCondition &&
                                                                                      (x.ActionReference!.ArchivedAtUtc == null || x.Id == policy.ConditionId)).OrderBy(x => x.Name).ToListAsync();
                break;
            case "actions":
            case "action-catalog":
                policy.PolicyActions = await db.PolicyActions.AsNoTracking().Where(x => x.PolicyId == id).ToListAsync();
                var assignedOnly = section == "actions";
                var conditionIds = policy.PolicyActions.Where(x => x.ConditionId.HasValue).Select(x => x.ConditionId!.Value).ToArray();
                details.Conditions = await db.ScriptActions.AsNoTracking().Where(x => x.IsCondition &&
                                                                                      (assignedOnly ? conditionIds.Contains(x.Id) : x.ActionReference!.ArchivedAtUtc == null)).OrderBy(x => x.Name).ToListAsync();
                await ReadActionOptionsAsync(details, db, assignedOnly);
                break;
            case "client-history":
                details.ClientHistory = await db.PolicyHistories.AsNoTracking().Include(x => x.Device).Where(x => x.PolicyId == id)
                    .OrderByDescending(x => x.LastRanAtUtc).ThenByDescending(x => x.Id).Take(200).ToListAsync();
                break;
            case "policy-history":
                details.PolicyHistory = await db.PolicyHashHistories.AsNoTracking().Where(x => x.PolicyId == id)
                    .OrderByDescending(x => x.ModifiedAtUtc).ThenByDescending(x => x.Id).Take(200).ToListAsync();
                break;
            case "audit":
                details.Audits = await db.AuditLogs.AsNoTracking().Where(x => x.ObjectType == nameof(Policy) && x.ObjectName == policy.Guid.ToString())
                    .OrderByDescending(x => x.LoggedAtUtc).ThenByDescending(x => x.Id).Take(200).ToListAsync();
                break;
            default: return ServiceResult<PolicyDetailsDto>.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyAdministration.UnknownPolicySection));
        }

        return ServiceResult<PolicyDetailsDto>.Ok(details);
    }

    private static async Task ReadActionOptionsAsync(PolicyDetailsDto details, CloneDeployDbContext db, bool assignedOnly)
    {
        var assigned = details.Policy.PolicyActions.Select(x => x.ActionGuid).ToArray();
        details.Actions.AddRange(await db.CommandActions.Where(x => (!assignedOnly && x.ActionReference!.ArchivedAtUtc == null) || (assignedOnly && assigned.Contains(x.Guid))).Select(x => new PolicyActionOptionDto(x.Guid, x.Id, x.Name, ActionType.Command)).ToListAsync());
        details.Actions.AddRange(await db.FileActions.Where(x => (!assignedOnly && x.ActionReference!.ArchivedAtUtc == null) || (assignedOnly && assigned.Contains(x.Guid))).Select(x => new PolicyActionOptionDto(x.Guid, x.Id, x.Name, ActionType.File)).ToListAsync());
        details.Actions.AddRange(await db.NotificationActions.Where(x => (!assignedOnly && x.ActionReference!.ArchivedAtUtc == null) || (assignedOnly && assigned.Contains(x.Guid))).Select(x => new PolicyActionOptionDto(x.Guid, x.Id, x.Name, ActionType.Notification)).ToListAsync());
        details.Actions.AddRange(await db.DomainJoinActions.Where(x => (!assignedOnly && x.ActionReference!.ArchivedAtUtc == null) || (assignedOnly && assigned.Contains(x.Guid))).Select(x => new PolicyActionOptionDto(x.Guid, x.Id, x.Name, ActionType.DomainJoin)).ToListAsync());
        details.Actions.AddRange(await db.WindowsUpdateActions.Where(x => (!assignedOnly && x.ActionReference!.ArchivedAtUtc == null) || (assignedOnly && assigned.Contains(x.Guid))).Select(x => new PolicyActionOptionDto(x.Guid, x.Id, x.Name, ActionType.WindowsUpdate)).ToListAsync());
        details.Actions.AddRange(await db.PrinterActions.Where(x => (!assignedOnly && x.ActionReference!.ArchivedAtUtc == null) || (assignedOnly && assigned.Contains(x.Guid))).Select(x => new PolicyActionOptionDto(x.Guid, x.Id, x.Name, ActionType.Printer)).ToListAsync());
        details.Actions.AddRange(await db.ScriptActions.Where(x => !x.IsCondition && ((!assignedOnly && x.ActionReference!.ArchivedAtUtc == null) || (assignedOnly && assigned.Contains(x.Guid)))).Select(x => new PolicyActionOptionDto(x.Guid, x.Id, x.Name, ActionType.Script)).ToListAsync());
        details.Actions.AddRange(await db.SoftwareActions.Where(x => (!assignedOnly && x.ActionReference!.ArchivedAtUtc == null) || (assignedOnly && assigned.Contains(x.Guid))).Select(x => new PolicyActionOptionDto(x.Guid, x.Id, x.Name, ActionType.Software)).ToListAsync());
        details.Actions = details.Actions.OrderBy(x => x.Name).ToList();
    }

    public async Task<ServiceResult> SaveAsync(PolicyDetailsDto details, string section)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.PolicyView)).Succeeded)
            return ServiceResult.Fail("Policy.Forbidden");
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.PolicyEdit)).Succeeded)
            return ServiceResult.Fail("Policy.Forbidden");

        var input = details.Policy;
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var entity = await db.Policies.Include(x => x.PolicyActions).SingleOrDefaultAsync(x => x.Id == input.Id && x.Guid == input.Guid);
        if (entity is null) return ServiceResult.Fail(ErrorKeys.Common.NotFound, input.Id);
        if (entity.Archived || entity.ArchivedAtUtc.HasValue) return Invalid("Restore this policy before editing it.");
        if (await db.ActiveCompiledPolicies.AnyAsync(x => x.PolicyId == input.Id)) return Invalid("Deactivate this policy before editing its settings or actions.");
        switch (section)
        {
            case "general":
                if (string.IsNullOrWhiteSpace(input.Name)) return Invalid("Enter a name.");
                if (await db.Policies.AnyAsync(x => x.Id != input.Id && x.Name.ToLower() == input.Name.Trim().ToLower())) return ServiceResult.Fail(ErrorKeys.Common.AlreadyExists, input.Name);
                entity.Name = input.Name.Trim();
                entity.Description = input.Description;
                entity.RunInventory = input.RunInventory;
                var tags = details.SelectedTags.Distinct().ToArray();
                if (await db.Tags.CountAsync(x => tags.Contains(x.Id)) != tags.Length) return Invalid("One or more tags no longer exist. Reload the page.");
                var existingTags = await db.PolicyTags.Where(x => x.PolicyId == input.Id).ToListAsync();
                db.PolicyTags.RemoveRange(existingTags.Where(x => !tags.Contains(x.TagId)));
                db.PolicyTags.AddRange(tags.Except(existingTags.Select(x => x.TagId)).Select(x => new PolicyTag { PolicyId = input.Id, TagId = x }));
                break;
            case "execution":
                if (input.AutoArchiveType == PolicyAutoArchiveType.AfterXdays && (!int.TryParse(input.AutoArchiveSub, out var days) || days < 1)) return Invalid("Enter a positive number of archive days.");
                if (input.ExecutionPriority < 0) return Invalid("Execution priority must be zero or greater.");
                entity.ExecutionPriority = input.ExecutionPriority;
                entity.CompletedAction = input.CompletedAction;
                entity.ExecutionType = input.ExecutionType;
                entity.ErrorAction = input.ErrorAction;
                entity.LogLevel = input.LogLevel;
                entity.RemoveInstallCache = input.RemoveInstallCache;
                entity.SkipServerResult = input.SkipServerResult;
                entity.AutoArchiveType = input.AutoArchiveType;
                entity.AutoArchiveSub = input.AutoArchiveType == PolicyAutoArchiveType.AfterXdays ? input.AutoArchiveSub : null;
                goto case "conditions";
            case "conditions":
                if (!await ValidConditionAsync(db, input.ConditionId)) return Invalid("Choose an available condition script.");
                if (input.ConditionFailedAction == PolicyConditionFailedAction.GotoModule) return Invalid("Go to action is only valid for action conditions.");
                entity.ConditionId = input.ConditionId;
                entity.ConditionFailedAction = input.ConditionFailedAction;
                break;
            case "schedule":
            case "triggers":
                var error = ValidateTrigger(input);
                if (error is not null) return Invalid(error);
                if (input.StartWindowEnabled && input.Frequency == PolicyFrequency.OncePerWeek
                    && input.MissedAction == PolicyFrequencyMissedAction.ScheduleDayOnly)
                    input.StartWindowDays = (PolicyWindowDays)(1 << input.SubFrequency);
                if (input.StartWindowEnabled)
                {
                    if (input.StartWindowFrom is null || input.StartWindowUntil is null)
                        return Invalid("Choose From and Until times for the allowed start window.");
                    try { new PolicyStartWindow(input.StartWindowDays, input.StartWindowFrom.Value, input.StartWindowUntil.Value).Validate(); }
                    catch (InvalidDataException ex) { return Invalid(ex.Message); }
                }
                entity.Trigger = input.Trigger;
                entity.SelfServiceRunAsUser = input.SelfServiceRunAsUser;
                entity.Frequency = input.Frequency;
                entity.SubFrequency = input.SubFrequency;
                entity.StartDateUtc = DateTime.SpecifyKind(input.StartDateUtc.Date, DateTimeKind.Utc);
                entity.MissedAction = input.MissedAction;
                entity.StartWindowEnabled = input.StartWindowEnabled;
                entity.StartWindowDays = input.StartWindowDays;
                entity.StartWindowFrom = input.StartWindowFrom;
                entity.StartWindowUntil = input.StartWindowUntil;
                break;
            case "actions":
            case "assigned-actions":
            case "available-actions":
                var assignments = input.PolicyActions.ToArray();
                if (assignments.Select(x => x.ActionGuid).Distinct().Count() != assignments.Length) return Invalid("An action can only be assigned once.");
                foreach (var assignment in assignments)
                {
                    if (!await db.ActionReferences.AnyAsync(x => x.ActionGuid == assignment.ActionGuid && x.ActionType == assignment.ActionType && x.ArchivedAtUtc == null)) return Invalid("An assigned action is unavailable. Reload the page.");
                    if (assignment.Order < 0 || !await ValidConditionAsync(db, assignment.ConditionId)) return Invalid("Use a non-negative order and an available condition script.");
                    if (assignment.ConditionFailedAction == PolicyConditionFailedAction.GotoModule && (!assignment.ConditionId.HasValue || !assignments.Any(x => x.ActionGuid == assignment.ConditionNextAction && x.ActionGuid != assignment.ActionGuid))) return Invalid("Choose a different assigned action as the condition target.");
                }

                db.PolicyActions.RemoveRange(entity.PolicyActions);
                entity.PolicyActions = assignments.Select(x => new PolicyAction { PolicyId = input.Id, ActionGuid = x.ActionGuid, ActionType = x.ActionType, Order = x.Order, ConditionId = x.ConditionId, ConditionFailedAction = x.ConditionFailedAction, ConditionNextAction = x.ConditionFailedAction == PolicyConditionFailedAction.GotoModule ? x.ConditionNextAction : null }).ToList();
                break;
            default: return Invalid("Unknown policy section.");
        }

        if (entity.Trigger != PolicyTrigger.Login && await db.Set<PolicyUserAccess>().AnyAsync(x => x.PolicyId == entity.Id && x.LoginAudienceId != null) &&
            !NotificationPolicyAudience.Eligible(entity, entity.PolicyActions)) return Invalid(NotificationPolicyAudience.ValidationMessage);
        if (!await AuditAsync(db, entity, AuditType.Update, section))
            return ServiceResult.Fail("Policy.Forbidden");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public Task<ServiceResult> ArchiveManyAsync(IEnumerable<Guid> guids) => LifecycleAsync(guids, "Archive");
    public Task<ServiceResult> RestoreAsync(Guid guid) => LifecycleAsync([guid], "Restore");
    public Task<ServiceResult> DeleteManyAsync(IEnumerable<Guid> guids) => LifecycleAsync(guids, "Delete");

    private async Task<ServiceResult> LifecycleAsync(IEnumerable<Guid> guids, string operation)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.PolicyView)).Succeeded)
            return ServiceResult.Fail("Policy.Forbidden");
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.PolicyEdit)).Succeeded)
            return ServiceResult.Fail("Policy.Forbidden");

        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var keys = guids.Distinct().ToArray();
        var policies = await db.Policies.Where(x => keys.Contains(x.Guid)).ToListAsync();
        if (policies.Count != keys.Length) return ServiceResult.Fail(ErrorKeys.Common.NotFound);
        var ids = policies.Select(x => x.Id).ToArray();
        if (operation != "Restore" && (await db.DevicePolicies.AnyAsync(x => ids.Contains(x.PolicyId)) || await db.GroupPolicies.AnyAsync(x => ids.Contains(x.PolicyId)) || await db.DirectoryOuPolicies.AnyAsync(x => ids.Contains(x.PolicyId)) || await db.ActiveCompiledPolicies.AnyAsync(x => ids.Contains(x.PolicyId)))) return Invalid("Remove device, group and OU assignments and deactivate this policy before archiving or deleting it.");
        foreach (var policy in policies)
        {
            if (operation == "Delete") db.Policies.Remove(policy);
            else
            {
                policy.Archived = operation == "Archive";
                policy.ArchivedAtUtc = policy.Archived ? DateTime.UtcNow : null;
            }

            if (!await AuditAsync(db, policy, operation == "Delete" ? AuditType.Delete : operation == "Archive" ? AuditType.Archive : AuditType.Restore, operation))
                return ServiceResult.Fail("Policy.Forbidden");
        }

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public static IEnumerable<PolicyFrequency> Frequencies(PolicyTrigger trigger) => System.Enum.GetValues<PolicyFrequency>().Where(x => trigger == PolicyTrigger.Login ? x is PolicyFrequency.Ongoing or PolicyFrequency.OncePerUserPerComputer : x != PolicyFrequency.OncePerUserPerComputer);

    public static string? ValidateTrigger(Policy policy)
    {
        if (policy.SelfServiceRunAsUser && policy.Trigger != PolicyTrigger.SelfServiceOnly) return "Requesting-user execution requires the self-service-only trigger.";
        if (!System.Enum.IsDefined(policy.Trigger) || !Frequencies(policy.Trigger).Contains(policy.Frequency)) return "Choose a frequency supported by this trigger.";
        if (policy.Trigger == PolicyTrigger.SelfServiceOnly) return null;
        if (policy.Frequency == PolicyFrequency.OncePerWeek && policy.SubFrequency is < 0 or > 6) return "Choose a day of the week.";
        if (policy.Frequency == PolicyFrequency.OncePerMonth && policy.SubFrequency is < 1 or > 31) return "Choose a day from 1 through 31.";
        if (policy.Frequency is PolicyFrequency.EveryXdays or PolicyFrequency.EveryXhours && policy.SubFrequency < 1) return "Enter a positive interval.";
        return null;
    }

    private static Task<bool> ValidConditionAsync(CloneDeployDbContext db, int? id) => id.HasValue ? db.ScriptActions.AnyAsync(x => x.Id == id && x.IsCondition && x.ActionReference!.ArchivedAtUtc == null) : Task.FromResult(true);
    private static ServiceResult Invalid(string text) => ServiceResult.Fail("Policy.Validation", text);

    private async Task<bool> AuditAsync(CloneDeployDbContext db, Policy policy, AuditType type, string section)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return false;
        db.AuditLogs.Add(new AuditLog
        {
            AppUserId = id, UserName = user.Identity?.Name ?? id.ToString(), AuditType = type,
            ObjectType = nameof(Policy), ObjectId = policy.Id, ObjectName = policy.Guid.ToString(), ObjectJson = JsonSerializer.Serialize(new { policy.Name, Section = section })
        });
        return true;
    }
}
