using System.Security.Claims;
using System.Text;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record HelpReportFilter(DateTime FromUtc, DateTime UntilUtc, int? QueueId = null,
    int? AssignedToId = null, int? CategoryId = null, HelpTicketStatus? Status = null)
{
    public string[]? RequesterKeys { get; init; }
    public int? RequesterTicketId { get; init; }
}
public sealed record HelpReportCount(string Label, int Count);
public sealed record HelpReportDay(DateTime Day, int Created, int Closures);
public sealed record HelpReportChoice(int Id, string Name);
public sealed record HelpReportOptions(HelpReportChoice[] Queues, HelpReportChoice[] Staff, HelpReportChoice[] Categories);
public sealed record HelpReportSummary(int Created, int Closures, int Open, int Unassigned, DateTime AsOfUtc,
    HelpReportDay[] Trend, HelpReportCount[] Queues, HelpReportCount[] Staff, HelpReportCount[] Categories, HelpReportCount[] Aging);
public sealed record HelpReportRow(int Id, string Subject, string Requester, string Device, string Queue,
    string Assignee, string Category, HelpTicketStatus Status, DateTime CreatedAtUtc, DateTime? ClosedAtUtc);
public sealed record HelpReportPage(int Total, HelpReportRow[] Rows);

public sealed partial class HelpDeskReportService(ICloneDeployDbFactory factory, HelpDeskService help, TimeProvider clock)
{
    private static void Validate(HelpReportFilter f)
    {
        if (f.FromUtc.Kind != DateTimeKind.Utc || f.UntilUtc.Kind != DateTimeKind.Utc || f.FromUtc >= f.UntilUtc ||
            f.UntilUtc - f.FromUtc > TimeSpan.FromDays(366) || f.QueueId < 0 || f.AssignedToId < 0 || f.CategoryId < 0 ||
            f.Status.HasValue && !System.Enum.IsDefined(f.Status.Value))
            throw new InvalidOperationException("Choose a valid date range of up to 366 days and valid filters.");
    }

    private static IQueryable<HelpTicket> Filter(IQueryable<HelpTicket> q, HelpReportFilter f)
    {
        if (f.QueueId.HasValue) q = q.Where(x => f.QueueId == 0 ? x.QueueId == null : x.QueueId == f.QueueId);
        if (f.AssignedToId.HasValue) q = q.Where(x => f.AssignedToId == 0 ? x.AssignedToId == null : x.AssignedToId == f.AssignedToId);
        if (f.CategoryId.HasValue) q = q.Where(x => f.CategoryId == 0 ? x.CategoryId == null : x.CategoryId == f.CategoryId);
        if (f.Status.HasValue) q = q.Where(x => x.Status == f.Status);
        if (f.RequesterKeys is { } keys) q = q.Where(x => x.RequesterKeys.Any(k => keys.Contains(k)) || x.Id == f.RequesterTicketId);
        return q;
    }

    private static IQueryable<HelpReportRow> Rows(CloneDeployDbContext db, IQueryable<HelpTicket> q) =>
        from t in q
        join u in db.Users on t.AssignedToId equals u.Id into users
        from u in users.DefaultIfEmpty()
        join c in db.Set<HelpCategory>() on t.CategoryId equals c.Id into categories
        from c in categories.DefaultIfEmpty()
        select new HelpReportRow(t.Id, t.Subject, t.RequesterName, t.DeviceName,
            t.Queue == null ? "No queue" : t.Queue.Name, u == null ? "Unassigned" : u.UserName ?? "Unknown technician",
            c == null ? "Uncategorized" : c.Name, t.Status, t.CreatedAtUtc,
            t.Status == HelpTicketStatus.Closed ? db.Set<HelpTicketStatusEvent>()
                .Where(e => e.TicketId == t.Id && e.Status == HelpTicketStatus.Closed).Max(e => (DateTime?)e.OccurredAtUtc) : null);

    public async Task<HelpReportOptions> OptionsAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var access = await help.ReportAccessAsync(user);
        await using var db = await factory.CreateContextAsync();
        var q = HelpDeskService.Visible(db.HelpTickets.AsNoTracking(), access);
        return new(
            await db.Set<HelpQueue>().Where(x => q.Any(t => t.QueueId == x.Id)).OrderBy(x => x.Name).Select(x => new HelpReportChoice(x.Id, x.Name)).ToArrayAsync(ct),
            await db.Users.Where(x => q.Any(t => t.AssignedToId == x.Id)).OrderBy(x => x.UserName).Select(x => new HelpReportChoice(x.Id, x.UserName ?? "Unknown technician")).ToArrayAsync(ct),
            await db.Set<HelpCategory>().Where(x => q.Any(t => t.CategoryId == x.Id)).OrderBy(x => x.Name).Select(x => new HelpReportChoice(x.Id, x.Name)).ToArrayAsync(ct));
    }

    public async Task<HelpReportSummary> SummaryAsync(ClaimsPrincipal user, HelpReportFilter f, CancellationToken ct = default)
    {
        Validate(f);
        var access = await help.ReportAccessAsync(user);
        await using var db = await factory.CreateContextAsync();
        var q = Filter(HelpDeskService.Visible(db.HelpTickets.AsNoTracking(), access), f);
        var period = q.Where(x => x.CreatedAtUtc >= f.FromUtc && x.CreatedAtUtc < f.UntilUtc);
        var open = q.Where(x => x.Status != HelpTicketStatus.Closed);
        var now = clock.GetUtcNow().UtcDateTime;
        var created = await period.GroupBy(x => x.CreatedAtUtc.Date).Select(g => new { Day = g.Key, Count = g.Count() }).ToArrayAsync(ct);
        var closed = await db.Set<HelpTicketStatusEvent>().Where(e => e.Status == HelpTicketStatus.Closed &&
            e.OccurredAtUtc >= f.FromUtc && e.OccurredAtUtc < f.UntilUtc && q.Any(t => t.Id == e.TicketId))
            .GroupBy(e => e.OccurredAtUtc.Date).Select(g => new { Day = g.Key, Count = g.Count() }).ToArrayAsync(ct);
        var trend = Enumerable.Range(0, (int)Math.Ceiling((f.UntilUtc - f.FromUtc.Date).TotalDays))
            .Select(i => f.FromUtc.Date.AddDays(i)).Select(day => new HelpReportDay(day,
                created.FirstOrDefault(x => x.Day == day)?.Count ?? 0, closed.FirstOrDefault(x => x.Day == day)?.Count ?? 0)).ToArray();
        var queues = await open.GroupBy(x => x.Queue == null ? "No queue" : x.Queue.Name)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => new HelpReportCount(g.Key, g.Count())).ToArrayAsync(ct);
        var staff = await (from t in open join u in db.Users on t.AssignedToId equals u.Id into users
            from u in users.DefaultIfEmpty() select new { Name = u == null ? "Unassigned" : u.UserName ?? "Unknown technician" })
            .GroupBy(x => x.Name).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => new HelpReportCount(g.Key, g.Count())).ToArrayAsync(ct);
        var categories = await (from t in period join c in db.Set<HelpCategory>() on t.CategoryId equals c.Id into cats
            from c in cats.DefaultIfEmpty() select new { Name = c == null ? "Uncategorized" : c.Name })
            .GroupBy(x => x.Name).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => new HelpReportCount(g.Key, g.Count())).ToArrayAsync(ct);
        var day1 = now.AddDays(-1); var day3 = now.AddDays(-3); var day7 = now.AddDays(-7);
        var age = await open.GroupBy(x => x.CreatedAtUtc > day1 ? 0 : x.CreatedAtUtc > day3 ? 1 : x.CreatedAtUtc > day7 ? 2 : 3)
            .Select(g => new { Bucket = g.Key, Count = g.Count() }).ToArrayAsync(ct);
        var labels = new[] { "Under 1 day", "1–3 days", "3–7 days", "7+ days" };
        return new(created.Sum(x => x.Count), closed.Sum(x => x.Count), age.Sum(x => x.Count),
            await open.CountAsync(x => x.AssignedToId == null, ct), now, trend, queues, staff, categories,
            labels.Select((label, i) => new HelpReportCount(label, age.FirstOrDefault(x => x.Bucket == i)?.Count ?? 0)).ToArray());
    }

    public async Task<HelpReportPage> PageAsync(ClaimsPrincipal user, HelpReportFilter f, int page, int size, CancellationToken ct = default)
    {
        Validate(f);
        if (page < 0 || page > 1000000 || size is < 1 or > 100) throw new InvalidOperationException("Invalid report page.");
        var access = await help.ReportAccessAsync(user);
        await using var db = await factory.CreateContextAsync();
        var q = Filter(HelpDeskService.Visible(db.HelpTickets.AsNoTracking(), access), f)
            .Where(x => x.CreatedAtUtc >= f.FromUtc && x.CreatedAtUtc < f.UntilUtc);
        return new(await q.CountAsync(ct), await Rows(db, q.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Skip(page * size).Take(size)).ToArrayAsync(ct));
    }

    public async Task<MemoryStream> ExportAsync(ClaimsPrincipal user, HelpReportFilter f, CancellationToken ct = default)
    {
        Validate(f);
        var access = await help.ReportAccessAsync(user);
        await using var db = await factory.CreateContextAsync();
        var q = Filter(HelpDeskService.Visible(db.HelpTickets.AsNoTracking(), access), f)
            .Where(x => x.CreatedAtUtc >= f.FromUtc && x.CreatedAtUtc < f.UntilUtc);
        var stream = new MemoryStream();
        try
        {
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true))
            {
                await writer.WriteLineAsync("Ticket,Subject,Requester,Device,Queue,Assignee,Category,Status,Created (UTC),Last closed (UTC)");
                await foreach (var row in Rows(db, q.OrderBy(x => x.Id)).AsAsyncEnumerable().WithCancellation(ct))
                {
                    var values = new[] { row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), row.Subject, row.Requester, row.Device,
                        row.Queue, row.Assignee, row.Category, HelpDeskService.StatusLabel(row.Status), row.CreatedAtUtc.ToString("O"), row.ClosedAtUtc?.ToString("O") };
                    await writer.WriteLineAsync(string.Join(",", values.Select(DeviceReportColumns.CsvCell)).AsMemory(), ct);
                }
            }
            stream.Position = 0;
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
}
