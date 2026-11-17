using System.Globalization;
using System.Text;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed partial class EmailDigestBuilder
{
    private static async Task<int> AppendHelpDeskAsync(CloneDeployDbContext db, StringBuilder body,
        HelpDeskService.Access access, DateTime now, string baseUrl, CancellationToken ct)
    {
        // Reuse ticket visibility, including queue membership, at delivery time.
        var tickets = HelpDeskService.Visible(db.HelpTickets.AsNoTracking(), access)
            .Where(x => x.Status != HelpTicketStatus.Closed);
        var count = 0;
        await Section("Unclaimed helpdesk tickets", tickets.Where(x => x.AssignedToId == null), false);
        await Section("Helpdesk tickets awaiting your reply", tickets.Where(x => x.AssignedToId == access.UserId &&
            db.HelpTicketMessages.Where(m => m.TicketId == x.Id).OrderByDescending(m => m.Id)
                .Select(m => (bool?)m.FromStaff).FirstOrDefault() == false), true);
        return count;

        async Task Section(string title, IQueryable<HelpTicket> query, bool awaitingReply)
        {
            var total = await query.CountAsync(ct);
            if (total == 0) return;
            count += total;
            var rows = await query.Select(t => new
            {
                t.Id, t.Subject, t.RequesterName,
                Queue = t.Queue == null ? "No queue" : t.Queue.Name,
                WaitingSince = awaitingReply ? db.HelpTicketMessages.Where(m => m.TicketId == t.Id)
                    .OrderByDescending(m => m.Id).Select(m => (DateTime?)m.CreatedAtUtc).FirstOrDefault() ?? t.CreatedAtUtc : t.CreatedAtUtc
            }).OrderBy(x => x.WaitingSince).ThenBy(x => x.Id).Take(10).ToListAsync(ct);
            body.AppendLine().AppendLine($"{title}: {total:N0} (current backlog)");
            foreach (var row in rows)
            {
                var waiting = TimeSpan.FromTicks(Math.Max(0, (now - row.WaitingSince).Ticks));
                body.AppendLine($"  - #{row.Id}: {Line(row.Subject)} | Requester: {Line(row.RequesterName)} | Queue: {Line(row.Queue)}")
                    .AppendLine(FormattableString.Invariant($"    Waiting: {waiting.Days}d {waiting.Hours}h {waiting.Minutes}m (since {row.WaitingSince:yyyy-MM-dd HH:mm} UTC)"))
                    .AppendLine($"    {baseUrl.TrimEnd('/')}/helpdesk/{row.Id.ToString(CultureInfo.InvariantCulture)}");
            }
            if (total > rows.Count) body.AppendLine($"  ... and {total - rows.Count:N0} more");
            body.AppendLine(baseUrl.TrimEnd('/') + "/helpdesk");
        }
    }

    private static string Line(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
}
