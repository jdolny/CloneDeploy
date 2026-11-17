using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Localization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

internal static class ActionEditGuard
{
    // Call inside the save transaction so activation cannot publish stale action content.
    internal static async Task<ServiceResult> CheckAsync(CloneDeployDbContext db, Guid action)
    {
        var scripts = db.ScriptActions.Where(x => x.Guid == action).Select(x => x.Id);
        var active = db.ActiveCompiledPolicies.Select(x => x.PolicyId);
        var inUse = await db.Policies.AnyAsync(x => active.Contains(x.Id) &&
            (x.ConditionId != null && scripts.Contains(x.ConditionId.Value) ||
             x.PolicyActions.Any(a => a.ActionGuid == action || a.ConditionNextAction == action ||
                 a.ConditionId != null && scripts.Contains(a.ConditionId.Value))));
        return inUse
            ? ServiceResult.Fail(ErrorKeys.Action.Validation,
                UiText.Get(LocKeys.Ui.ActionEditGuard.ThisActionIsUsedByAnActivePolicyDeactivate))
            : ServiceResult.Ok();
    }
}
