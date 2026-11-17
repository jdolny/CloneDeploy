using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public static class HelpEmailNotifications
{
    // Caller holds the help-desk transaction lock, so event recording is atomic with the ticket.
    public static async Task QueueAsync(CloneDeployDbContext db, HelpTicket ticket, string? immediateReason, DateTime now, CancellationToken ct)
    {
        if (!await db.Set<EmailServerSettings>().AnyAsync(x => x.Enabled, ct)) return;
        if (ticket.QueueId is { } queue)
        {
            if (!await db.Set<HelpQueue>().AnyAsync(x => x.Id == queue && x.EmailEnabled, ct)) return;
        }
        else if (ticket.AssignedToId is null) return;
        var state = await db.Set<HelpEmailState>().SingleOrDefaultAsync(x => x.TicketId == ticket.Id, ct);
        if (state is null) { state = new() { TicketId = ticket.Id }; db.Add(state); }
        Schedule(state, now, immediateReason);
        await db.SaveChangesAsync(ct);
    }
    public static void Schedule(HelpEmailState state, DateTime now, string? reason)
    {
        state.FirstPendingUtc ??= now;
        state.Updates++;
        if (reason is not null) { state.Immediate = true; state.Reason = reason; }
        if (state.Immediate) { state.DueAtUtc = state.FirstPendingUtc; return; }
        state.Reason = "Requester update";
        var quiet = now.AddMinutes(2);
        var maximum = state.FirstPendingUtc.Value.AddMinutes(10);
        var due = quiet < maximum ? quiet : maximum;
        if (state.LastSentUtc is { } last && due < last.AddMinutes(5)) due = last.AddMinutes(5);
        state.DueAtUtc = due;
    }
}

public sealed class HelpEmailRunner(ICloneDeployDbFactory factory, HelpDeskService help, IEmailTransport transport,
    ServerUrlSettingsStore urls, TimeProvider clock, ILogger<HelpEmailRunner> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        await db.Database.OpenConnectionAsync(ct);
        if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock(68332, 0) AS \"Value\"").SingleAsync(ct)) return;
        try
        {
            // Only one sender owns this lock. A leftover Sending row means SMTP acceptance is unknown.
            var interrupted = await db.Set<HelpEmailDelivery>().Where(x => x.Status == EmailDeliveryStatus.Sending)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, EmailDeliveryStatus.Uncertain), ct);
            if (interrupted > 0) logger.LogWarning("{Count} interrupted help desk email deliveries will not be retried to avoid duplicates", interrupted);
            var settings = await db.Set<EmailServerSettings>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1 && x.Enabled, ct);
            if (settings is null) return;
            var baseUrl = await urls.RequireAsync(ct);
            var now = clock.GetUtcNow().UtcDateTime;
            var due = await db.Set<HelpEmailState>().Where(x => x.DueAtUtc <= now).OrderBy(x => x.DueAtUtc).Select(x => x.TicketId).Take(50).ToArrayAsync(ct);
            foreach (var id in due)
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947240, 1)", ct);
                var state = await db.Set<HelpEmailState>().SingleOrDefaultAsync(x => x.TicketId == id, ct);
                if (state?.DueAtUtc is null || state.DueAtUtc > clock.GetUtcNow().UtcDateTime) { await tx.CommitAsync(ct); continue; }
                var ticket = await db.HelpTickets.AsNoTracking().Include(x => x.Queue).SingleAsync(x => x.Id == id, ct);
                var recipients = await help.EmailRecipientsAsync(id, ct);
                var latest = await db.HelpTicketMessages.Where(x => x.TicketId == id && !x.FromStaff).OrderByDescending(x => x.Id)
                    .Select(x => x.Body).FirstOrDefaultAsync(ct) ?? "";
                var batch = Guid.NewGuid();
                var subject = $"[Help #{id}] {state.Reason}: {ticket.Subject}".Replace('\r', ' ').Replace('\n', ' ');
                var body = $"{state.Reason}: #{id} — {ticket.Subject}\nRequester: {ticket.RequesterName}\nDevice: {ticket.DeviceName}\nQueue: {ticket.Queue?.Name ?? "No queue"}\nUpdates in this notification: {state.Updates}\n\nLatest requester message:\n{latest[..Math.Min(latest.Length, 800)]}\n\nOpen ticket: {baseUrl}helpdesk/{id}\n\nSign in to view and respond. Do not reply to this email; replies are not monitored.";
                foreach (var address in recipients)
                    db.Add(new HelpEmailDelivery { TicketId = id, BatchId = batch, Address = address, Subject = subject, Body = body,
                        Status = EmailDeliveryStatus.Failed, NextAttemptUtc = now, UpdatedAtUtc = now });
                if (recipients.Length > 0) state.LastSentUtc = clock.GetUtcNow().UtcDateTime;
                state.FirstPendingUtc = null; state.DueAtUtc = null; state.Immediate = false; state.Updates = 0;
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            }
            var deliveries = await db.Set<HelpEmailDelivery>().Where(x => x.Status == EmailDeliveryStatus.Failed && x.Attempts < 3 && x.NextAttemptUtc <= now)
                .OrderBy(x => x.NextAttemptUtc).ThenBy(x => x.Id).Take(100).ToListAsync(ct);
            foreach (var delivery in deliveries)
            {
                ct.ThrowIfCancellationRequested();
                settings = await db.Set<EmailServerSettings>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1 && x.Enabled, ct);
                if (settings is null) break;
                // Recheck both permissions and assignment for retries, too.
                var recipients = await help.EmailRecipientsAsync(delivery.TicketId, ct);
                if (!recipients.Contains(delivery.Address, StringComparer.OrdinalIgnoreCase))
                { delivery.Status = EmailDeliveryStatus.Skipped; await db.SaveChangesAsync(ct); continue; }
                delivery.Status = EmailDeliveryStatus.Sending; delivery.Attempts++; delivery.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(ct);
                EmailSendOutcome outcome;
                try { outcome = await transport.SendAsync(settings, delivery.Address, delivery.Subject, delivery.Body, $"help-{delivery.BatchId:N}-{delivery.Id}", ct); }
                catch (Exception ex) { logger.LogWarning(ex, "Help email delivery {DeliveryId} failed with uncertain acceptance", delivery.Id); outcome = EmailSendOutcome.Uncertain; }
                delivery.Status = outcome switch { EmailSendOutcome.Accepted => EmailDeliveryStatus.Sent, EmailSendOutcome.Rejected => EmailDeliveryStatus.Failed, _ => EmailDeliveryStatus.Uncertain };
                delivery.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime; delivery.NextAttemptUtc = delivery.UpdatedAtUtc.AddMinutes(5);
                await db.SaveChangesAsync(CancellationToken.None);
                if (outcome == EmailSendOutcome.Accepted)
                {
                    // Anchor the cooldown to actual SMTP acceptance, including slow sends/retries.
                    await using var tx = await db.Database.BeginTransactionAsync(CancellationToken.None);
                    await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947240, 1)", CancellationToken.None);
                    var sentAt = delivery.UpdatedAtUtc;
                    var cooldown = sentAt.AddMinutes(5);
                    await db.Set<HelpEmailState>().Where(x => x.TicketId == delivery.TicketId)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSentUtc, sentAt)
                            .SetProperty(x => x.DueAtUtc, x => !x.Immediate && x.DueAtUtc < cooldown ? cooldown : x.DueAtUtc), CancellationToken.None);
                    await tx.CommitAsync(CancellationToken.None);
                }
                if (outcome != EmailSendOutcome.Accepted) logger.LogWarning("Help email delivery {DeliveryId}: {Status}, attempt {Attempt}", delivery.Id, delivery.Status, delivery.Attempts);
            }
            // Retain delivery diagnostics for thirty days without retaining email bodies indefinitely.
            await db.Set<HelpEmailDelivery>().Where(x => x.UpdatedAtUtc < now.AddDays(-30)).ExecuteDeleteAsync(ct);
        }
        finally { await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(68332, 0)", CancellationToken.None); }
    }
}

public sealed class HelpEmailWorker(IServiceScopeFactory scopes, ILogger<HelpEmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<HelpEmailRunner>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Help desk email processing failed; retrying shortly"); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
