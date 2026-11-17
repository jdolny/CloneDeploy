using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Applies global rules to exact inventoried revisions, coordinating with manual decisions.</summary>
public sealed class WindowsUpdateAutoApproval(ICloneDeployDbFactory factory, TimeProvider clock)
{
    private const long ApprovalLock = 741982617032;
    public static readonly string[] Severities = ["Critical", "Important", "Moderate", "Low"];

    public static Task LockAsync(CloneDeployDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({ApprovalLock})", ct);

    public static List<InventoryUpdateCategory> Classifications(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<InventoryUpdateCategory>>(json) ?? [])
                .Where(x => x is not null && x.Type == "UpdateClassification" && Guid.TryParse(x.Id, out _)).ToList();
        }
        catch (JsonException) { return []; }
    }

    public static bool Matches(WindowsUpdateAutoApprovalRule rule, WindowsUpdate update, DateTime now)
    {
        if (!rule.Enabled || rule.DelayDays is < 0 or > 365 || update.RevisionFirstSeenAtUtc is not { } firstSeen ||
            firstSeen > now.AddDays(-rule.DelayDays) || update.RevisionNumber is not >= 0 ||
            !Guid.TryParse(update.UpdateId, out var id) || id == Guid.Empty ||
            update.ApprovedRevisionNumber == update.RevisionNumber || update.AutoApprovalBlockedRevision == update.RevisionNumber ||
            rule.Severities.Length + rule.ClassificationIds.Length == 0) return false;
        if (rule.Severities.Length > 0 && !rule.Severities.Contains(update.MsrcSeverity ?? "", StringComparer.OrdinalIgnoreCase)) return false;
        return rule.ClassificationIds.Length == 0 || Classifications(update.CategoriesJson)
            .Any(x => rule.ClassificationIds.Contains(Guid.Parse(x.Id).ToString(), StringComparer.OrdinalIgnoreCase));
    }

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, ct);
        var rules = await db.WindowsUpdateAutoApprovalRules.AsNoTracking().Where(x => x.Enabled).OrderBy(x => x.Id).ToListAsync(ct);
        if (rules.Count == 0) return 0;
        var now = clock.GetUtcNow().UtcDateTime;
        var approved = 0;
        var lastId = 0;
        // Page the catalog so large inventories do not require loading every row at once.
        while (true)
        {
            var updates = await db.WindowsUpdates.AsNoTracking().Where(x => x.Id > lastId && x.RevisionNumber >= 0 &&
                (x.ApprovedRevisionNumber == null || x.ApprovedRevisionNumber != x.RevisionNumber) &&
                (x.AutoApprovalBlockedRevision == null || x.AutoApprovalBlockedRevision != x.RevisionNumber))
                .OrderBy(x => x.Id).Take(500).ToListAsync(ct);
            if (updates.Count == 0) break;
            lastId = updates[^1].Id;
            foreach (var update in updates)
            {
                var rule = rules.FirstOrDefault(x => Matches(x, update, now));
                if (rule is null) continue;
                // Inventory can discover a new revision during evaluation; never approve that unseen revision.
                var changed = await db.WindowsUpdates.Where(x => x.Id == update.Id && x.RevisionNumber == update.RevisionNumber &&
                        x.RevisionFirstSeenAtUtc == update.RevisionFirstSeenAtUtc &&
                        (x.AutoApprovalBlockedRevision == null || x.AutoApprovalBlockedRevision != x.RevisionNumber) &&
                        (x.ApprovedRevisionNumber == null || x.ApprovedRevisionNumber != x.RevisionNumber))
                    .ExecuteUpdateAsync(x => x.SetProperty(u => u.ApprovedRevisionNumber, update.RevisionNumber)
                        .SetProperty(u => u.ApprovedAtUtc, now).SetProperty(u => u.ApprovedBy, "Auto: " + rule.Name)
                        .SetProperty(u => u.ApprovedByRuleId, rule.Id), ct);
                if (changed == 0) continue;
                approved++;
                db.AuditLogs.Add(new AuditLog
                {
                    AppUserId = rule.AppUserId, UserName = "Auto-approval", AuditType = AuditType.Update,
                    ObjectType = nameof(WindowsUpdate), ObjectId = update.Id, ObjectName = update.Title ?? update.UpdateId!,
                    LoggedAtUtc = now, ObjectJson = JsonSerializer.Serialize(new
                    {
                        update.UpdateId, update.RevisionNumber, Approved = true, RuleId = rule.Id,
                        RuleName = rule.Name, rule.Severities, rule.ClassificationIds, rule.DelayDays, update.RevisionFirstSeenAtUtc
                    })
                });
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
        await transaction.CommitAsync(ct);
        return approved;
    }
}