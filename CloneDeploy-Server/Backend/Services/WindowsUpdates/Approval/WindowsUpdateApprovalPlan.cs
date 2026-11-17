using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;
namespace CloneDeploy_Server.Backend.Services;

/// <summary>Resolves current global approvals or the exact allowlist frozen in a one-off test deployment.</summary>
internal static class WindowsUpdateApprovalPlan
{
    internal static bool RequiresApproval(CompiledPolicyActionDto action) => action.Type == ActionType.WindowsUpdate &&
        action.Settings.TryGetProperty("Mode", out var mode) && mode.GetInt32() == (int)WindowsUpdateMode.InstallApproved;

    internal static async Task<List<WindowsUpdateIdentity>> ReadAsync(CloneDeployDbContext db, CompiledPolicyActionDto action, CancellationToken ct)
    {
        if (!RequiresApproval(action)) throw new InvalidOperationException("This action does not use an approved update plan.");
        if (action.Settings.TryGetProperty("TestUpdates", out var selected))
            return selected.Deserialize<List<WindowsUpdateIdentity>>() ?? throw new InvalidDataException("Missing test update selection.");
        var approved = await db.WindowsUpdates.AsNoTracking().Where(x => x.ApprovedRevisionNumber != null &&
            x.ApprovedRevisionNumber == x.RevisionNumber && x.UpdateId != null).Select(x => new { x.UpdateId, x.RevisionNumber }).ToListAsync(ct);
        return approved.Where(x => Guid.TryParse(x.UpdateId, out var id) && id != Guid.Empty && x.RevisionNumber is >= 0)
            .Select(x => new WindowsUpdateIdentity(Guid.Parse(x.UpdateId!), x.RevisionNumber!.Value)).ToList();
    }
}
