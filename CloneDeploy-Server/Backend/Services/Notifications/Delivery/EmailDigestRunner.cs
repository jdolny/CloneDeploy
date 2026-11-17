using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;
namespace CloneDeploy_Server.Backend.Services;

public static class EmailDigestSchedule
{
    // Return only the most recent due occurrence; never flood users with an outage backlog.
    public static DateTime? Due(UserEmailSubscription subscription, bool weekly, DateTime now)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(subscription.TimeZoneId);
        var today = TimeZoneInfo.ConvertTimeFromUtc(now, zone).Date;
        for (var offset = 0; offset < 9; offset++)
        {
            var date = today.AddDays(-offset);
            if (weekly && date.DayOfWeek != subscription.WeeklyDay) continue;
            var local = DateTime.SpecifyKind(date.AddMinutes(subscription.DeliveryMinute), DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local)) continue;
            var due = zone.IsAmbiguousTime(local) ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).UtcDateTime
                : TimeZoneInfo.ConvertTimeToUtc(local, zone);
            if (due > now) continue;
            return due >= subscription.EnabledAtUtc ? due : null;
        }
        return null;
    }
}
public sealed class EmailDigestRunner(ICloneDeployDbFactory factory, EmailDigestBuilder builder, IEmailTransport transport, TimeProvider clock)
{
    public async Task RunAsync(int userId, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        await db.Database.OpenConnectionAsync(ct);
        var locked = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock(68331, {userId}) AS \"Value\"").SingleAsync(ct);
        if (!locked) return;
        try
        {
            var s = await db.Set<UserEmailSubscription>().AsNoTracking().SingleOrDefaultAsync(x => x.AppUserId == userId && (x.Daily || x.Weekly), ct);
            var settings = await db.Set<EmailServerSettings>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1 && x.Enabled, ct);
            if (s is null || settings is null) return;
            var address = (await db.AppUsers.Where(x => x.Id == userId).Select(x => x.Email).SingleOrDefaultAsync(ct))?.Trim() ?? "";
            if (!SmtpEmailTransport.ValidAddress(address)) return;
            foreach (var weekly in new[] { false, true })
            {
                if (weekly ? !s.Weekly : !s.Daily) continue;
                var now = clock.GetUtcNow().UtcDateTime;
                var due = EmailDigestSchedule.Due(s, weekly, now);
                if (due is null) continue;
                var kind = weekly ? "Weekly" : "Daily";
                var period = TimeZoneInfo.ConvertTimeFromUtc(due.Value, TimeZoneInfo.FindSystemTimeZoneById(s.TimeZoneId)).ToString("yyyy-MM-dd");
                var delivery = await db.Set<EmailDigestDelivery>().SingleOrDefaultAsync(x => x.AppUserId == userId && x.Kind == kind && x.Period == period, ct);
                if (delivery?.Status == EmailDeliveryStatus.Sending)
                {
                    delivery.Status = EmailDeliveryStatus.Uncertain; delivery.Detail = "Delivery was interrupted; not retried to avoid duplicate email.";
                    delivery.UpdatedAtUtc = now; await db.SaveChangesAsync(ct); continue;
                }
                if (delivery is not null && (delivery.Status != EmailDeliveryStatus.Failed || delivery.Attempts >= 3 || delivery.UpdatedAtUtc > now.AddMinutes(-30))) continue;
                var content = await builder.BuildAsync(s, weekly, now, ct);
                if (delivery is null) { delivery = new() { AppUserId = userId, Kind = kind, Period = period }; db.Add(delivery); }
                delivery.UpdatedAtUtc = now;
                if (content is null || s.SkipEmpty && content.AttentionCount == 0)
                {
                    delivery.Status = EmailDeliveryStatus.Skipped; delivery.Detail = content is null ? "Account is unavailable or no longer permitted." : "No items need attention.";
                    await db.SaveChangesAsync(ct); continue;
                }
                delivery.Status = EmailDeliveryStatus.Sending; delivery.Attempts++; delivery.Detail = null;
                // Persist the send intent before contacting SMTP. A crash leaves an uncertain send, never an automatic duplicate.
                await db.SaveChangesAsync(ct);
                EmailSendOutcome outcome;
                try { outcome = await transport.SendAsync(settings, address, content.Subject, content.Body, $"digest-{userId}-{kind}-{period}", ct); }
                catch { outcome = EmailSendOutcome.Uncertain; }
                delivery.Status = outcome switch { EmailSendOutcome.Accepted => EmailDeliveryStatus.Sent, EmailSendOutcome.Rejected => EmailDeliveryStatus.Failed, _ => EmailDeliveryStatus.Uncertain };
                delivery.Detail = outcome switch { EmailSendOutcome.Accepted => "Accepted by SMTP server.", EmailSendOutcome.Rejected => "SMTP rejected delivery. Up to three attempts, 30 minutes apart.", _ => "SMTP acceptance could not be confirmed. Not retried to avoid duplicate email." };
                delivery.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(CancellationToken.None);
                if (outcome != EmailSendOutcome.Accepted) throw new InvalidOperationException("Email delivery failed or is uncertain. See the recipient's email delivery history.");
            }
        }
        finally
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock(68331, {userId})", CancellationToken.None);
            await db.Database.CloseConnectionAsync();
        }
    }
}
