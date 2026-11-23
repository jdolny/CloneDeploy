using System.Text;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed partial class EmailDigestBuilder
{
    private static async Task<int> AppendSecurityAsync(CloneDeployDbContext db, StringBuilder body,
        DateTime since, DateTime now, CancellationToken ct)
    {
        var events = db.Set<SecurityActivityEvent>().AsNoTracking();
        var period = events.Where(x => x.OccurredAtUtc >= since && x.OccurredAtUtc <= now);
        var attention = 0;
        async Task AuthSection(string title, IQueryable<SecurityActivityEvent> source, int minimum)
        {
            var grouped = source.GroupBy(x => new { Account = x.Account.ToUpper(), x.SourceIp })
                .Select(g => new { g.Key.Account, g.Key.SourceIp, Count = g.Count(), First = g.Min(x => x.OccurredAtUtc), Last = g.Max(x => x.OccurredAtUtc) })
                .Where(x => x.Count >= minimum);
            var count = await grouped.CountAsync(ct);
            if (count == 0) return;
            attention += count;
            body.AppendLine().AppendLine($"{title}: {count:N0} account/source groups");
            foreach (var row in await grouped.OrderByDescending(x => x.Count).ThenBy(x => x.Account).ThenBy(x => x.SourceIp).Take(10).ToListAsync(ct))
                body.AppendLine($"  - {Line(row.Account)} | Source: {(row.SourceIp == "" ? "unavailable" : Line(row.SourceIp))} | {row.Count:N0} events | {row.First:yyyy-MM-dd HH:mm} to {row.Last:yyyy-MM-dd HH:mm} UTC");
            if (count > 10) body.AppendLine($"  ... and {count - 10:N0} more groups");
        }

        await AuthSection("Repeated failed sign-ins (5 or more in this period)", period.Where(x => x.Kind == SecurityActivity.Failure), 5);
        await AuthSection("Account lockouts / attempts while locked", period.Where(x => x.Kind == SecurityActivity.Lockout), 1);
        await AuthSection("Successful sign-ins following repeated failures (within 15 minutes)", period.Where(s => s.Kind == SecurityActivity.Success &&
            events.Count(f => (f.Kind == SecurityActivity.Failure || f.Kind == SecurityActivity.Lockout) && f.Account.ToUpper() == s.Account.ToUpper() &&
                f.SourceIp == s.SourceIp && f.OccurredAtUtc >= s.OccurredAtUtc.AddMinutes(-15) && f.OccurredAtUtc < s.OccurredAtUtc) >= 5), 1);

        var changes = period.Where(x => x.Kind == SecurityActivity.Change);
        var changeCount = await changes.CountAsync(ct);
        if (changeCount > 0)
        {
            attention += changeCount;
            body.AppendLine().AppendLine($"Account, access, MFA and sensitive configuration changes: {changeCount:N0}");
            foreach (var row in await changes.OrderByDescending(x => x.OccurredAtUtc).ThenByDescending(x => x.Id).Take(20).ToListAsync(ct))
                body.AppendLine($"  - {row.OccurredAtUtc:yyyy-MM-dd HH:mm} UTC | {Line(row.Account)} | {Line(row.Category)} | {Line(row.Target)}");
            if (changeCount > 20) body.AppendLine($"  ... and {changeCount - 20:N0} more changes");
        }
        // Existing transactional audits cover administrator MFA resets and certificate operations.
        // Never render ObjectJson: it may contain sensitive application configuration.
        var audited = db.AuditLogs.AsNoTracking().Where(x => x.LoggedAtUtc >= since && x.LoggedAtUtc <= now &&
            (x.ObjectType == nameof(AppCertificate) || x.ObjectType == nameof(Certificate) ||
             x.ObjectType == nameof(AppUser) && x.ObjectJson == "{\"Operation\":\"ResetMfa\"}"));
        var auditCount = await audited.CountAsync(ct);
        if (auditCount > 0)
        {
            attention += auditCount;
            body.AppendLine().AppendLine($"Certificate changes and administrator MFA resets: {auditCount:N0}");
            foreach (var row in await audited.OrderByDescending(x => x.LoggedAtUtc).ThenByDescending(x => x.Id)
                .Select(x => new { x.LoggedAtUtc, x.UserName, x.ObjectType, x.ObjectName, x.AuditType }).Take(20).ToListAsync(ct))
                body.AppendLine($"  - {row.LoggedAtUtc:yyyy-MM-dd HH:mm} UTC | {Line(row.UserName)} | {(row.ObjectType == nameof(AppUser) ? "MFA reset" : "Certificate " + row.AuditType)} | {Line(row.ObjectName)}");
            if (auditCount > 20) body.AppendLine($"  ... and {auditCount - 20:N0} more changes");
        }
        return attention;
    }
}
