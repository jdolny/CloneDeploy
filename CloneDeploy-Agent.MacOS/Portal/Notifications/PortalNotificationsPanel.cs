using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.MacOS.Portal;

internal sealed class PortalNotificationsPanel : UserControl, IDisposable
{
    private readonly Window owner;
    private readonly Action open;
    private readonly StackPanel items = new() { Spacing = 12 };
    private readonly TextBlock error = new() { IsVisible = false, TextWrapping = TextWrapping.Wrap, Classes = { "error" } };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly CancellationTokenSource stop = new();
    private readonly HashSet<string> presented = [];
    private HashSet<string> seen = [];
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private readonly string seenPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloneDeploy", "Portal", "seen-notifications.json");
    private AgentPortalNotification[] history = [];
    private HashSet<string> expired = [];
    private Window? popup;
    private string? popupId;
    private bool refreshing, acknowledging, disposed;
    internal Action? SessionExpired { get; set; }
    private string? accountToken;

    internal PortalNotificationsPanel(Window owner, Action open)
    {
        this.owner = owner; this.open = open;
        Margin = new Thickness(20);
        var panel = new Grid { RowDefinitions = new("Auto,Auto,*") };
        panel.Children.Add(new TextBlock { Text = PortalText.Get("Common.Notifications"), FontSize = 20, FontWeight = FontWeight.Medium, Margin = new Thickness(0, 0, 0, 12) });
        Grid.SetRow(error, 1); panel.Children.Add(error);
        var scroll = new ScrollViewer { Content = items, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2); panel.Children.Add(scroll); Content = panel;
        try { if (File.Exists(seenPath)) seen = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(seenPath)) ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        Render();
        timer.Tick += async (_, _) => await RefreshAsync(); timer.Start();
    }

    internal void SetAccount(string? token)
    {
        if (accountToken == token) return;
        accountToken = token; popup?.Close(); presented.Clear(); history = []; expired.Clear(); Render();
    }
    internal void SetBranding(AgentBranding value) => PortalPopupBranding.Update(value);

    internal async Task RefreshAsync()
    {
        if (refreshing || disposed || acknowledging) return;
        refreshing = true;
        var token = accountToken;
        try
        {
            var reply = await PortalClient.AuthenticateAsync(new("notifications", Token: token), stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            if (token != accountToken) return;
            if (reply.Status == "SignedOut") SessionExpired?.Invoke();
            if (reply.Status != "Notifications" || reply.Notifications is null)
            { popup?.Close(); throw new IOException(PortalText.Get("Notifications.UnavailableRetry")); }
            error.IsVisible = false;
            if (token != accountToken) return;
            var latest = reply.Notifications;
            var nowExpired = latest.Where(x => x.IsExpired).Select(x => x.Id).ToHashSet();
            if (!history.SequenceEqual(latest) || !expired.SetEquals(nowExpired))
            { history = latest; expired = nowExpired; Render(); }
            if (popupId is { } id && !history.Any(x => x.Id == id && !x.IsExpired && (!x.RequireAcknowledgment || x.PendingAcknowledgment)))
                popup?.Close();
            if (popup is null)
            {
                var next = history.Reverse().FirstOrDefault(x => ShouldPresent(x, started, seen, presented));
                if (next is not null) ShowPopup(next);
            }
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or JsonException or OperationCanceledException)
        {
            if (!disposed) { error.Text = PortalText.Get("Notifications.RefreshFailedAcknowledgmentsRetained"); error.IsVisible = true; }
        }
        finally { refreshing = false; }
    }

    internal static bool ShouldPresent(AgentPortalNotification message, DateTimeOffset started, HashSet<string> seen, HashSet<string> presented) =>
        !message.IsExpired && (message.RequireAcknowledgment
            ? message.PendingAcknowledgment && !presented.Contains(message.Id)
            : message.CreatedAt >= started && !seen.Contains(message.Id));

    private static TextBlock Text(string value, double size = 13) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap };
    private void Render()
    {
        items.Children.Clear();
        if (history.Length == 0) items.Children.Add(Text(PortalText.Get("Notifications.NoNotificationsYet")));
        foreach (var message in history)
        {
            var content = new StackPanel { Spacing = 8 };
            var title = Text(message.Title, 15); title.FontWeight = FontWeight.Medium; content.Children.Add(title);
            content.Children.Add(Text(message.Body));
            var status = message.AcknowledgedAt is { } at ? PortalText.Format("Notifications.AcknowledgedAt", at.LocalDateTime) : message.IsExpired ? PortalText.Get("Notifications.Expired") : message.PendingAcknowledgment ? PortalText.Get("Notifications.AcknowledgementRequired") : "";
            var metadata = Text($"{message.CreatedAt.LocalDateTime:g}" + (status.Length > 0 ? $" · {status}" : ""), 11);
            metadata.Classes.Add("muted"); content.Children.Add(metadata);
            if (message.PendingAcknowledgment)
            {
                var acknowledge = new Button { Content = PortalText.Get("Common.Acknowledge"), HorizontalAlignment = HorizontalAlignment.Left, Classes = { "primary" } };
                acknowledge.Click += async (_, _) => await AcknowledgeAsync(message, acknowledge);
                content.Children.Add(acknowledge);
            }
            items.Children.Add(new Border { Child = content, Classes = { "card" } });
        }
    }

    private async Task AcknowledgeAsync(AgentPortalNotification message, Button button)
    {
        if (acknowledging || disposed) return;
        acknowledging = true; button.IsEnabled = false;
        try
        {
            var reply = await PortalClient.AuthenticateAsync(new("notification-ack", Token: accountToken, NotificationId: message.Id), stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            if (reply.Status == "SignedOut") SessionExpired?.Invoke();
            if (reply.Status != "Acknowledged") throw new IOException(PortalText.Get("Notifications.AcknowledgeNotificationFailed"));
            if (popupId == message.Id) popup?.Close();
            error.IsVisible = false;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or JsonException or OperationCanceledException)
        {
            if (!disposed) { error.Text = PortalText.Get("Notifications.AcknowledgmentNotSaved"); error.IsVisible = true; open(); }
        }
        finally { acknowledging = false; button.IsEnabled = true; }
        await RefreshAsync();
    }

    private void ShowPopup(AgentPortalNotification message)
    {
        var window = new Window { Title = PortalText.Get("Notifications.Notification"), Width = 380, MaxHeight = 500, SizeToContent = SizeToContent.Height,
            CanResize = false, ShowActivated = false, ShowInTaskbar = false, Topmost = true };
        var content = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        content.Children.Add(PortalPopupBranding.Header(window, message.Title));
        if (message.Kind != NotificationKind.Information) content.Children.Add(Text(PortalText.Get(message.Kind switch { NotificationKind.Success => "Notifications.Success", NotificationKind.Warning => "Notifications.Warning", NotificationKind.Critical => "Notifications.Critical", _ => "Notifications.Information" }), 11));
        content.Children.Add(new ScrollViewer { Content = Text(message.Body), MaxHeight = 260, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        if (message.PendingAcknowledgment)
        {
            var acknowledge = new Button { Content = PortalText.Get("Common.Acknowledge"), Classes = { "primary" } };
            acknowledge.Click += async (_, _) => await AcknowledgeAsync(message, acknowledge); actions.Children.Add(acknowledge);
        }
        var show = new Button { Content = PortalText.Get("Notifications.OpenNotifications") }; show.Click += (_, _) => { open(); window.Close(); }; actions.Children.Add(show);
        var dismiss = new Button { Content = message.PendingAcknowledgment ? PortalText.Get("Common.Later") : PortalText.Get("Common.Dismiss") };
        dismiss.Click += (_, _) => window.Close(); actions.Children.Add(dismiss);
        foreach (var action in actions.Children) action.Margin = new Thickness(0, 0, 8, 6);
        content.Children.Add(actions); window.Content = content;
        PortalPopupPlacement.Configure(window, owner, message.Position == NotificationPosition.Center);
        popup = window; popupId = message.Id;
        window.Closed += (_, _) => { if (popup == window) { popup = null; popupId = null; } };
        window.Show(); presented.Add(message.Id); seen.Add(message.Id);
        seen.IntersectWith(history.Select(x => x.Id));
        try { Directory.CreateDirectory(Path.GetDirectoryName(seenPath)!); File.WriteAllText(seenPath, JsonSerializer.Serialize(seen)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() { disposed = true; timer.Stop(); stop.Cancel(); popup?.Close(); }
}
