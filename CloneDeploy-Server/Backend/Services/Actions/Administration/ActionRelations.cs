using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

internal static class ActionRelations
{
    internal static async Task<List<ActionRelationDto>> ReadAsync(CloneDeployDbContext db, ActionReference reference)
    {
        var guid = reference.ActionGuid;
        var rows = new List<ActionRelationDto>();
        var scriptId = await db.ScriptActions.Where(x => x.Guid == guid).Select(x => (int?)x.Id).SingleOrDefaultAsync();
        var policies = await db.Policies.AsNoTracking().Include(x => x.PolicyActions)
            .Where(x => x.PolicyActions.Any(a => a.ActionGuid == guid || scriptId != null && a.ConditionId == scriptId)
                || scriptId != null && x.ConditionId == scriptId).OrderBy(x => x.Name).ToListAsync();
        foreach (var policy in policies)
        {
            var roles = new List<string>();
            if (policy.PolicyActions.Any(x => x.ActionGuid == guid)) roles.Add(UiText.Get(LocKeys.Ui.ActionRelations.AssignedAction));
            if (scriptId.HasValue && policy.ConditionId == scriptId) roles.Add(UiText.Get(LocKeys.Ui.ActionRelations.PolicyCondition));
            if (scriptId.HasValue && policy.PolicyActions.Any(x => x.ConditionId == scriptId)) roles.Add(UiText.Get(LocKeys.Ui.ActionRelations.ActionCondition));
            if (policy.Archived || policy.ArchivedAtUtc.HasValue) roles.Add(UiText.Get(LocKeys.Ui.ActionRelations.ArchivedPolicy));
            rows.Add(new("Policies", policy.Name, string.Join(" · ", roles), $"/policies/{policy.Id}/actions"));
        }
        var tags = await db.ActionTags.Where(x => x.ActionGuid == guid).Select(x => x.Tag!).AsNoTracking().OrderBy(x => x.Name).ToListAsync();
        foreach (var tag in tags) rows.Add(new("Tags", tag.Name, UiText.Get(LocKeys.Ui.ActionRelations.AssignedToThisAction), "/shared/tags"));
        var files = await db.ActionFiles.Where(x => x.ActionGuid == guid).Select(x => x.File).AsNoTracking().OrderBy(x => x.Name).ToListAsync();
        foreach (var file in files)
            rows.Add(new("Files", file.Name, UiText.Get(LocKeys.Ui.ActionRelations.AttachedFile0, (UiText.Label(file.Status))), null));
        int? credentialId = reference.ActionType switch
        {
            ActionType.Command => await db.CommandActions.Where(x => x.Guid == guid).Select(x => x.ManagedCredentialId).SingleOrDefaultAsync(),
            ActionType.Script => await db.ScriptActions.Where(x => x.Guid == guid).Select(x => x.ManagedCredentialId).SingleOrDefaultAsync(),
            ActionType.Software => await db.SoftwareActions.Where(x => x.Guid == guid).Select(x => x.ManagedCredentialId).SingleOrDefaultAsync(),
            ActionType.DomainJoin => await db.DomainJoinActions.Where(x => x.Guid == guid).Select(x => (int?)x.ManagedCredentialId).SingleOrDefaultAsync(),
            _ => null
        };
        if (credentialId.HasValue)
        {
            var credential = await db.ManagedCredentials.AsNoTracking().Where(x => x.Id == credentialId).Select(x => x.Name).SingleOrDefaultAsync();
            rows.Add(new("Credentials", credential ?? UiText.Get(LocKeys.Ui.ActionRelations.UnavailableCredential),
                reference.ActionType == ActionType.DomainJoin ? UiText.Get(LocKeys.Ui.DomainJoinActionConfigurationTab.DomainJoinCredential) : UiText.Get(LocKeys.Ui.ActionRelations.RunAsCredentialIgnoredByUserContextPolicies), "/administration/server/credentials"));
        }
        if (reference.ExternalFileUpdateSchedule is { } schedule)
            rows.Add(new("Schedules", schedule.Name, UiText.Get(LocKeys.Ui.ActionRelations.URLFileRefreshSchedule), "/shared/schedules"));
        return rows.OrderBy(x => x.Kind).ThenBy(x => x.Name).ToList();
    }
}
