using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.UI;

// Ticket notifications and read state are never written to the local message journal.
internal sealed class HelpDeskNotifications
{
    internal bool Enabled { get; private set; }
    internal void SetEnabled(bool enabled)
    {
        workAccountRevision++;
        Enabled = enabled;
        if (!enabled) { UnreadCount = 0; identity = null; notified = 0; Changed?.Invoke(); }
    }
    internal int UnreadCount { get; private set; }
    internal event Action? Changed;
    internal event Action? UpdatesAvailable;
    private long notified;
    private string? identity;
    private bool busy;
    private int workAccountRevision;
    internal async Task RunAsync(Action<int> notify, CancellationToken ct)
    {
        long? revision = null;
        var nextRefresh = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            if (!Enabled)
            {
                revision = null; nextRefresh = DateTimeOffset.MinValue;
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { break; }
                continue;
            }
            try
            {
                var current = await PortalClient.HelpPresenceAsync(ct);
                if (!busy && (revision != current || DateTimeOffset.UtcNow >= nextRefresh))
                {
                    revision = current;
                    await RefreshAsync(notify, ct);
                    UpdatesAvailable?.Invoke();
                    nextRefresh = DateTimeOffset.UtcNow.AddMinutes(15);
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Older service or local pipe unavailable: retain a slow recovery check.
                if (DateTimeOffset.UtcNow >= nextRefresh)
                {
                    await RefreshAsync(notify, ct);
                    nextRefresh = DateTimeOffset.UtcNow.AddMinutes(15);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task RefreshAsync(Action<int>? notify, CancellationToken ct)
    {
        if (busy || !Enabled) return;
        busy = true;
        try
        {
            var revision = workAccountRevision;
            var result = await PortalClient.HelpAsync(new(HelpOperation.Count), ct);
            if (!Enabled || revision != workAccountRevision) return;
            if (result.Error is not null) { UnreadCount = 0; identity = null; notified = 0; Changed?.Invoke(); return; }
            if (identity != result.Identity) { identity = result.Identity; notified = 0; }
            UnreadCount = result.UnreadCount;
            Changed?.Invoke();
            if (notify is not null && UnreadCount > 0 && result.LatestUpdate > notified)
            {
                notified = result.LatestUpdate;
                notify(UnreadCount);
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { UnreadCount = 0; Changed?.Invoke(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { busy = false; }
    }
}

