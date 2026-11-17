using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.UI;

/// <summary>Polls local message history and remembers which notices this Windows user has already seen.</summary>
internal sealed class PortalNotifications
{
    internal AgentFeatures Features { get; private set; } = new();
    internal event Action<AgentFeatures>? FeaturesChanged;
    internal AgentBranding Branding { get; private set; } = new();
    internal event Action<AgentBranding>? BrandingChanged;
    internal ObservableCollection<AgentPortalNotification> History { get; } = [];
    private int workAccountRevision;
    internal void ResetWorkAccount() { workAccountRevision++; History.Clear(); }
    private readonly string seenPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloneDeploy", "Portal", "seen-notifications.json");
    private HashSet<string> seen = [];
    private HashSet<string> expiredMessages = [];
    private bool dirty;
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    private readonly HashSet<string> presentedThisSession = [];
    private PortalNotificationJournal? localMessages;
    private PortalNotificationJournal LocalMessages => localMessages ??= new(new AgentOptions
        { StateDirectory = Path.GetDirectoryName(seenPath)! });
    internal AgentPortalNotification StoreLocal(AgentPortalNotification message)
    {
        var run = Guid.Parse(message.Id);
        LocalMessages.Enqueue(run, Guid.Empty, message.Title, message.Body, message.TimeoutSeconds);
        message = message with { Id = $"{run:N}:{Guid.Empty:N}" };
        History.Insert(0, message);
        return message;
    }
    internal void Presented(string id) { dirty |= seen.Add(id); presentedThisSession.Add(id); }
    internal async Task AcknowledgeAsync(string id)
    {
        await PortalClient.AcknowledgeNotificationAsync(id, CancellationToken.None);
        var message = History.FirstOrDefault(x => x.Id == id);
        if (message is not null) History[History.IndexOf(message)] = message with { AcknowledgedAt = DateTimeOffset.UtcNow };
    }
    internal PortalNotifications()
    {
        try { if (File.Exists(seenPath)) seen = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(seenPath)) ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
    internal bool ShouldNotify(AgentPortalNotification message) => !message.IsExpired && (message.RequireAcknowledgment
        ? message.PendingAcknowledgment && !presentedThisSession.Contains(message.Id)
        : message.CreatedAt >= startedAt && !seen.Contains(message.Id));

    internal async Task RunAsync(Action<AgentPortalNotification> notify, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var revision = workAccountRevision;
                var snapshot = await PortalClient.ReadNotificationSnapshotAsync(ct);
                if (revision != workAccountRevision) continue;
                if (Features != snapshot.Features)
                {
                    Features = snapshot.Features;
                    FeaturesChanged?.Invoke(Features);
                }
                if (Branding.Version != snapshot.Branding.Version)
                {
                    Branding = snapshot.Branding;
                    BrandingChanged?.Invoke(Branding);
                }
                var remote = snapshot.Notifications;
                var messages = remote.Concat(LocalMessages.Read()).OrderByDescending(x => x.PendingAcknowledgment).ThenByDescending(x => x.CreatedAt).Take(50).ToArray();
                var expired = messages.Where(x => x.IsExpired).Select(x => x.Id).ToHashSet();
                if (!History.SequenceEqual(messages) || !expiredMessages.SetEquals(expired))
                {
                    History.Clear(); foreach (var message in messages) History.Add(message);
                    expiredMessages = expired;
                }
                var previousCount = seen.Count;
                seen.IntersectWith(messages.Select(x => x.Id));
                dirty |= previousCount != seen.Count;
                foreach (var message in messages.Reverse())
                    if (ShouldNotify(message)) notify(message);
                if (dirty)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(seenPath)!);
                    // User-owned state only. It is never trusted to authorize a service operation.
                    var temporary = seenPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { File.WriteAllText(temporary, JsonSerializer.Serialize(seen)); File.Move(temporary, seenPath, true); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    dirty = false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or JsonException or System.ComponentModel.Win32Exception)
            { if (ct.IsCancellationRequested) break; }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}
