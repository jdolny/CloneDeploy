using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.UI;

public sealed partial class HelpDeskPanel : UserControl
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(15) };
    private readonly CancellationTokenSource stop = new();
    private bool busy, creating, more, refreshPending;
    private bool canCreate;
    private int offset;
    private int? selected;
    private HelpTicketSummary? currentTicket;
    private List<HelpMessage> messages = [];
    private byte[]? attachment;
    private Guid command = Guid.NewGuid();
    private sealed record TicketRow(HelpTicketSummary Ticket)
    {
        public string Updated => PortalText.Format("HelpDesk.RequestUpdated", Ticket.Id, Ticket.UpdatedAtUtc.ToLocalTime());
        public string UnreadLabel => Ticket.Unread ? PortalText.Get("HelpDesk.UnreadIndicator") : "";
        public string StatusText => Status(Ticket.Status);
        public string StatusBackground => Ticket.Status switch { HelpTicketStatus.New => "#DCF5E6", HelpTicketStatus.InProgress => "#DCEAFF", HelpTicketStatus.WaitingOnUser => "#FFF0CC", _ => "#E9EDF2" };
        public string StatusForeground => Ticket.Status switch { HelpTicketStatus.New => "#17643D", HelpTicketStatus.InProgress => "#1D4EC5", HelpTicketStatus.WaitingOnUser => "#795700", _ => "#526171" };
    }
    private static string Status(HelpTicketStatus value) => value switch
    {
        HelpTicketStatus.InProgress => PortalText.Get("HelpDesk.InProgress"), HelpTicketStatus.WaitingOnUser => PortalText.Get("HelpDesk.WaitingOnYou"),
        HelpTicketStatus.Closed => PortalText.Get("HelpDesk.Closed"), _ => PortalText.Get("HelpDesk.AwaitingReview")
    };
    public HelpDeskPanel()
    {
        InitializeComponent();
        async void HelpUpdated() { if (IsVisible) { refreshPending = true; await RefreshAsync(); } }
        Loaded += (_, _) => { if (Application.Current is App app) { app.HelpNotifications.UpdatesAvailable -= HelpUpdated; app.HelpNotifications.UpdatesAvailable += HelpUpdated; } };
        Unloaded += (_, _) => { if (Application.Current is App app) app.HelpNotifications.UpdatesAvailable -= HelpUpdated; };
        newRequest.Click += (_, _) => BeginRequest();
        previous.Click += async (_, _) => { if (busy) return; offset = Math.Max(0, offset - 50); await RefreshAsync(); };
        next.Click += async (_, _) => { if (busy) return; offset += 50; await RefreshAsync(); };
        filter.SelectionChanged += async (_, _) => { offset = 0; await RefreshAsync(); };
        subject.TextChanged += (_, _) => command = Guid.NewGuid();
        body.TextChanged += (_, _) => command = Guid.NewGuid();
        submit.Click += async (_, _) => await SendAsync();
        screenshot.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true;
            try
            {
                var data = await HelpScreenshot.CaptureAsync(Window.GetWindow(this));
                if (data is not null)
                {
                    attachment = data; command = Guid.NewGuid();
                    using var stream = new System.IO.MemoryStream(data);
                    var image = System.Windows.Media.Imaging.BitmapFrame.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    image.Freeze(); attachmentThumbnail.Source = image;
                    Editor();
                }
            }
            catch { status.Text = PortalText.Get("Screenshot.CaptureFailed"); }
            finally { busy = false; }
        };
        removeScreenshot.Click += (_, _) => { if (!busy) ClearAttachment(); };
        previewScreenshot.Click += (_, _) => { if (!busy && attachment is not null) HelpScreenshot.Preview(Window.GetWindow(this), attachment, false); };
        tickets.SelectionChanged += async (_, _) =>
        {
            if (busy || tickets.SelectedItem is not TicketRow row || row.Ticket.Id == selected) return;
            if (!DiscardDraft())
            {
                busy = true;
                tickets.SelectedItem = tickets.Items.Cast<TicketRow>().FirstOrDefault(x => x.Ticket.Id == selected);
                busy = false;
                return;
            }
            selected = row.Ticket.Id; currentTicket = row.Ticket; creating = false; command = Guid.NewGuid(); body.Clear(); ClearAttachment(); messages.Clear();
            await RefreshAsync();
        };
        older.Click += async (_, _) =>
        {
            if (busy || selected is null || messages.Count == 0) return;
            busy = true;
            try
            {
                var result = await Request(new(HelpOperation.Read, selected.Value, MessageId: messages[0].Id));
                messages.InsertRange(0, result.Messages); more = result.HasMore; Render();
                await MarkRead(result);
            }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { busy = false; }
        };
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible) { timer.Start(); await RefreshAsync(); }
            else { timer.Stop(); }
        };
        timer.Tick += async (_, _) => { if (IsVisible) await RefreshAsync(); };
        Loaded += (_, _) =>
        {
            var window = Window.GetWindow(this);
            if (window is not null) window.Closed += (_, _) => { timer.Stop(); stop.Cancel(); ClearPrivateData(); };
        };
        Editor();
    }
    private bool DiscardDraft() => body.Text.Length == 0 && attachment is null && (!creating || subject.Text.Length == 0) || MessageBox.Show(Window.GetWindow(this),
        PortalText.Get("HelpDesk.ConfirmDiscardDraft"), PortalText.Get("HelpDesk.Title"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    private void New()
    {
        if (busy || !canCreate || !DiscardDraft()) return;
        creating = true; selected = null; currentTicket = null; tickets.SelectedItem = null; messages.Clear(); subject.Clear(); body.Clear();
        command = Guid.NewGuid(); ClearAttachment(); Render(); Editor(); subject.Focus();
    }
    internal void BeginRequest() => New();
    internal void SetSupportResponse(string text) => SupportResponseText.Text = text;

    internal void SetHelpName(string name)
    {
        newRequestLabel.Text = PortalText.Format("HelpDesk.NewNamedRequest", name);
        System.Windows.Automation.AutomationProperties.SetName(newRequest, newRequestLabel.Text);
    }
    private void ClearAttachment() { if (attachment is not null) command = Guid.NewGuid(); attachment = null; attachmentThumbnail.Source = null; screenshot.Content = PortalText.Get("HelpDesk.TakeScreenshot"); attachmentCard.Visibility = Visibility.Collapsed; }
    private void ClearPrivateData() { tickets.ItemsSource = null; messages.Clear(); selected = null; currentTicket = null; creating = false; subject.Clear(); body.Clear(); ClearAttachment(); Render(); Editor(); }
    private int workAccountRevision;
    internal void ResetWorkAccount() { workAccountRevision++; offset = 0; canCreate = false; ClearPrivateData(); }
    private void Editor()
    {
        newRequest.IsEnabled = canCreate;
        submit.IsEnabled = !creating || canCreate;
        var closed = !creating && currentTicket?.Status == HelpTicketStatus.Closed;
        var editing = creating || selected.HasValue;
        subject.Visibility = subjectLabel.Visibility = creating ? Visibility.Visible : Visibility.Collapsed;
        submit.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        body.Visibility = screenshot.Visibility = editing && !closed ? Visibility.Visible : Visibility.Collapsed;
        attachmentCard.Visibility = editing && !closed && attachment is not null ? Visibility.Visible : Visibility.Collapsed;
        screenshot.Content = attachment is null ? PortalText.Get("HelpDesk.TakeScreenshot") : PortalText.Get("HelpDesk.RetakeScreenshot");
        submit.Content = creating ? PortalText.Get("HelpDesk.SubmitRequest") : closed ? PortalText.Get("HelpDesk.ReopenRequest") : PortalText.Get("HelpDesk.SendReply");
        bodyLabel.Visibility = creating ? Visibility.Visible : Visibility.Collapsed;
        body.Tag = creating ? "new" : "reply";
    }
    private async Task<HelpResponse> Request(HelpRequest request)
    {
        var revision = workAccountRevision;
        var result = await PortalClient.HelpAsync(request, stop.Token);
        if (revision != workAccountRevision) throw new InvalidOperationException(PortalText.Get("HelpDesk.WorkAccountChanged"));
        if (result.Error is not null) throw new InvalidOperationException(result.Error);
        return result;
    }
    private async Task RefreshAsync()
    {
        if (!IsVisible) return;
        if (busy) { if (refreshPending) timer.Interval = TimeSpan.FromSeconds(1); return; }
        refreshPending = false;
        timer.Interval = TimeSpan.FromMinutes(15);
        busy = true;
        filter.IsEnabled = false;
        try
        {
            var list = await Request(new(HelpOperation.List, Offset: offset,
                StatusFilter: filter.SelectedIndex > 0 ? (HelpTicketStatus)(filter.SelectedIndex - 1) : null));
            canCreate = list.CanCreate;
            tickets.ItemsSource = list.Tickets.Select(x => new TicketRow(x)).ToArray();
            tickets.SelectedItem = tickets.Items.Cast<TicketRow>().FirstOrDefault(x => x.Ticket.Id == selected);
            previous.IsEnabled = offset > 0; next.IsEnabled = list.HasMore;
            if (selected is { } id)
            {
                var result = await Request(new(HelpOperation.Read, id));
                currentTicket = result.Ticket;
                var followLatest = messages.Count == 0 || conversationScroll.ScrollableHeight - conversationScroll.VerticalOffset < 30;
                messages = messages.Concat(result.Messages).DistinctBy(x => x.Id).OrderBy(x => x.Id).ToList();
                if (messages.Count <= 25) more = result.HasMore;
                Render();
                if (followLatest) conversationScroll.ScrollToEnd();
                await MarkRead(result);
            }
            status.Text = list.ScopeMessage ?? (list.Tickets.Length == 0 ? PortalText.Get("HelpDesk.NoMatchingRequestsHint") : "");
            Editor();
        }
        catch (Exception ex)
        {
            if (ex is InvalidOperationException) ClearPrivateData();
            status.Text = ex is InvalidOperationException ? ex.Message : PortalText.Get("HelpDesk.UnavailableRetry");
        }
        finally { busy = false; filter.IsEnabled = true; if (refreshPending) timer.Interval = TimeSpan.FromSeconds(1); }
    }
    private async Task MarkRead(HelpResponse result)
    {
        if (!IsVisible || result.Ticket is null || result.Messages.Length == 0) return;
        await Request(new(HelpOperation.MarkRead, result.Ticket.Id, MessageId: result.Messages.Max(x => x.Id)));
        if (Application.Current is App app) await app.HelpNotifications.RefreshAsync(null, stop.Token);
    }
    private void Render()
    {
        conversation.Children.Clear();
        older.Visibility = more && selected.HasValue ? Visibility.Visible : Visibility.Collapsed;
        foreach (var message in messages)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = message.FromStaff ? message.Author : PortalText.Get("HelpDesk.You"), FontSize = 11, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = message.Body, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            if (message.HasScreenshot)
            {
                var button = new Button { Content = PortalText.Get("HelpDesk.ViewScreenshot"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
                button.Click += async (_, _) =>
                {
                    if (busy || selected is null) return;
                    busy = true;
                    try { var result = await Request(new(HelpOperation.Screenshot, selected.Value, MessageId: message.Id)); if (result.Screenshot is not null) HelpScreenshot.Preview(Window.GetWindow(this), result.Screenshot, false); }
                    catch { status.Text = PortalText.Get("HelpDesk.TheScreenshotCouldNotBeLoaded"); }
                    finally { busy = false; }
                };
                content.Children.Add(button);
            }
            content.Children.Add(new TextBlock { Text = message.CreatedAtUtc.ToLocalTime().ToString("g"), FontSize = 10,
                Foreground = Brushes.SlateGray, Margin = new Thickness(0, 8, 0, 0) });
            var bubble = new Border { Background = new SolidColorBrush(Color.FromRgb(242, 244, 247)),
                HorizontalAlignment = message.FromStaff ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                MaxWidth = Math.Max(160, conversationScroll.ActualWidth - 32),
                CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Margin = new Thickness(message.FromStaff ? 0 : 24, 6, message.FromStaff ? 24 : 0, 6), Child = content };
            bubble.SetResourceReference(BackgroundProperty, message.FromStaff ? "PortalRaised" : "BrandAccentTint");
            ((TextBlock)content.Children[^1]).SetResourceReference(TextBlock.ForegroundProperty, "PortalMuted");
            conversation.Children.Add(bubble);
        }
    }
    private async Task SendAsync()
    {
        if (busy || creating && !canCreate || !creating && selected is null) return;
        var reopening = !creating && currentTicket?.Status == HelpTicketStatus.Closed;
        if (!reopening && (string.IsNullOrWhiteSpace(body.Text) || creating && string.IsNullOrWhiteSpace(subject.Text))) { status.Text = PortalText.Get("HelpDesk.DetailedDescriptionRequired"); return; }
        busy = true; submit.IsEnabled = false;
        body.IsReadOnly = subject.IsReadOnly = true;
        screenshot.IsEnabled = removeScreenshot.IsEnabled = false;
        try
        {
            var result = await Request(new(creating ? HelpOperation.Create : reopening ? HelpOperation.Reopen : HelpOperation.Reply, selected ?? 0,
                Subject: subject.Text, Body: reopening ? null : body.Text, CommandId: command, Screenshot: reopening ? null : attachment));
            currentTicket = result.Ticket;
            selected = result.Ticket!.Id; creating = false; command = Guid.NewGuid(); body.Clear(); ClearAttachment(); messages = result.Messages.ToList(); more = result.HasMore;
            Render(); Editor(); status.Text = reopening ? PortalText.Get("HelpDesk.RequestReopened") : PortalText.Get("HelpDesk.MessageSent");
            conversationScroll.ScrollToEnd();
        }
        catch (Exception ex) { status.Text = ex is InvalidOperationException ? ex.Message : PortalText.Get("HelpDesk.SendUnconfirmed"); return; }
        finally { busy = false; submit.IsEnabled = !creating || canCreate; body.IsReadOnly = subject.IsReadOnly = false; screenshot.IsEnabled = removeScreenshot.IsEnabled = true; }
        await RefreshAsync();
    }
}
