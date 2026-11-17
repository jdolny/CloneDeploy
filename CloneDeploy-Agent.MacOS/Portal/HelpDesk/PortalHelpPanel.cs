using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.MacOS.Portal;

internal sealed class PortalHelpPanel : UserControl, IDisposable
{
    private readonly Func<string, HelpRequest, CancellationToken, Task<HelpResponse>> request;
    private readonly Button create = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock createLabel = Label(PortalText.Get("HelpDesk.NewHelpRequest"));
    private readonly ProgressBar listLoading = new() { IsIndeterminate = true, Height = 2, IsVisible = false };
    private readonly Button previous = new() { Content = PortalText.Get("HelpDesk.Previous") };
    private readonly Button next = new() { Content = PortalText.Get("HelpDesk.Next") };
    private readonly Button older = new() { Content = PortalText.Get("HelpDesk.LoadEarlierMessages"), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button send = new() { Content = PortalText.Get("HelpDesk.SendReply") };
    private byte[]? attachment;
    private Bitmap? attachmentBitmap;
    private readonly Image attachmentPreview = new() { Height = 70, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button attach = new() { Content = PortalText.Get("HelpDesk.TakeScreenshot") };
    private readonly Button removeAttachment = new() { Content = PortalText.Get("HelpDesk.RemoveScreenshot"), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock responseNotice = Label(AgentBranding.DefaultSupportResponseMessage, 12);
    private readonly TextBlock notice = Label(PortalText.Get("HelpDesk.SignInOnDeviceTab"));
    private readonly TextBlock error = Label("");
    private readonly ComboBox filter = new() { HorizontalAlignment = HorizontalAlignment.Stretch,
        ItemsSource = new[] { PortalText.Get("HelpDesk.AllMyRequests"), PortalText.Get("HelpDesk.AwaitingReview"), PortalText.Get("HelpDesk.InProgress"), PortalText.Get("HelpDesk.WaitingOnYou"), PortalText.Get("HelpDesk.Closed") }, SelectedIndex = 0 };
    private readonly ListBox tickets = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox subject = new() { PlaceholderText = PortalText.Get("HelpDesk.BriefDescription"), MaxLength = 200 };
    private readonly TextBox body = new() { PlaceholderText = PortalText.Get("HelpDesk.ReplyPlaceholder"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        MaxLength = 4000, MinHeight = 80, MaxHeight = 150 };
    private readonly StackPanel conversation = new() { Spacing = 10 };
    private readonly ScrollViewer scroll;
    private readonly StackPanel editor = new() { Spacing = 8 };
    private readonly Grid workspace;
    private readonly Border emptyConversation = new()
    {
        Padding = new Thickness(30), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Child = new StackPanel { Spacing = 10, MaxWidth = 280, Children =
        {
            new TextBlock { Text = PortalText.Get("HelpDesk.HowCanWeHelp"), FontSize = 22, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = PortalText.Get("HelpDesk.ChooseRequestDescription"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap }
        }}
    };
    private readonly TextBlock emptyList = new() { Text = PortalText.Get("HelpDesk.NoRequestsYet"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(12, 24), HorizontalAlignment = HorizontalAlignment.Center };
    private CancellationTokenSource context = new();
    private CancellationTokenSource? refreshStop;
    private string? token;
    private bool enabled, busy, selecting, creating, moreMessages, moreTickets, disposed;
    private bool canCreate;
    private string? scopeMessage;
    private int offset;
    private HelpTicketSummary? selected;
    private List<HelpMessage> messages = [];
    private Guid command = Guid.NewGuid();
    private string helpName = PortalText.Get("HelpDesk.Title");
    private bool HasDraft => attachment is not null || !string.IsNullOrWhiteSpace(body.Text) || creating && !string.IsNullOrWhiteSpace(subject.Text);

    public PortalHelpPanel(Func<string, HelpRequest, CancellationToken, Task<HelpResponse>> request)
    {
        this.request = request;
        Margin = new Thickness(20);
        var createContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        createContent.Children.Add(new PathIcon { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center,
            Data = Geometry.Parse("M10,2 H14 V10 H22 V14 H14 V22 H10 V14 H2 V10 H10 Z") });
        createLabel.VerticalAlignment = VerticalAlignment.Center;
        createContent.Children.Add(createLabel); create.Content = createContent;
        create.Classes.Add("primary"); send.Classes.Add("primary");
        error.Classes.Add("error"); notice.Classes.Add("muted"); tickets.Classes.Add("tickets");
        subject.Classes.Add("composer"); body.Classes.Add("composer");
        tickets.Margin = new Thickness(0, 10, 0, 10);
        var leftHeader = new StackPanel { Spacing = 10, Margin = new Thickness(12, 12, 12, 0) };
        leftHeader.Children.Add(create); leftHeader.Children.Add(filter); leftHeader.Children.Add(listLoading);
        var pages = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8) };
        pages.Children.Add(previous); pages.Children.Add(next);
        var left = new Grid { RowDefinitions = new("Auto,*,Auto") };
        left.Children.Add(leftHeader); var listArea = new Grid(); listArea.Children.Add(tickets); listArea.Children.Add(emptyList);
        Grid.SetRow(listArea, 1); left.Children.Add(listArea); Grid.SetRow(pages, 2); left.Children.Add(pages);
        scroll = new ScrollViewer { Content = conversation, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        responseNotice.Classes.Add("muted");
        ToolTip.SetTip(responseNotice, PortalText.Get("HelpDesk.AutomaticRepliesWhileOpen"));
        ToolTip.SetShowDelay(responseNotice, 0);
        var rightHeader = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };
        rightHeader.Children.Add(new Border { Child = responseNotice, Classes = { "surface" }, BorderThickness = new Thickness(0, 1, 0, 1), Padding = new Thickness(0, 8) }); rightHeader.Children.Add(older);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(send); actions.Children.Add(attach);
        editor.Children.Add(subject); editor.Children.Add(body);
        editor.Children.Add(attachmentPreview); editor.Children.Add(removeAttachment); editor.Children.Add(actions);
        attach.Click += async (_, _) => await RunAsync(async ct =>
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return;
            var captured = await PortalScreenshot.CaptureAsync(owner, ct);
            ct.ThrowIfCancellationRequested();
            if (captured is not null) SetAttachment(captured);
        });
        removeAttachment.Click += (_, _) => { if (!busy) { SetAttachment(null); Controls(); } };
        var right = new Grid { RowDefinitions = new("Auto,*,Auto") };
        var messageArea = new Grid(); messageArea.Children.Add(scroll); messageArea.Children.Add(emptyConversation);
        right.Children.Add(rightHeader); Grid.SetRow(messageArea, 1); right.Children.Add(messageArea);
        editor.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(editor, 2); right.Children.Add(editor);
        workspace = new Grid { ColumnDefinitions = new("280,14,*") };
        var requestsCard = new Border { Classes = { "card" }, Padding = new Thickness(0), Child = left };
        var conversationCard = new Border { Classes = { "card" }, Padding = new Thickness(16), Child = right };
        workspace.Children.Add(requestsCard); Grid.SetColumn(conversationCard, 2); workspace.Children.Add(conversationCard);
        SizeChanged += (_, _) => workspace.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(Bounds.Width * .36, 230, 390));
        var header = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(notice); header.Children.Add(error);
        var root = new Grid { RowDefinitions = new("Auto,*") };
        root.Children.Add(header); Grid.SetRow(workspace, 1); root.Children.Add(workspace); Content = root;
        create.Click += (_, _) =>
        {
            if (busy || !canCreate || !CanLeaveDraft()) return;
            CancelRefresh();
            creating = true; selected = null; messages.Clear(); conversation.Children.Clear();
            subject.Text = body.Text = ""; command = Guid.NewGuid(); RestoreSelection(); Controls(); subject.Focus();
        };
        filter.SelectionChanged += async (_, _) => { if (!busy) { offset = 0; await RefreshCoreAsync(true); } };
        previous.Click += async (_, _) => { offset = Math.Max(0, offset - 50); await RefreshCoreAsync(true); };
        next.Click += async (_, _) => { offset += 50; await RefreshCoreAsync(true); };
        tickets.SelectionChanged += async (_, _) =>
        {
            if (selecting || busy || tickets.SelectedItem is not ListBoxItem { Tag: HelpTicketSummary ticket } || selected?.Id == ticket.Id) return;
            if (!CanLeaveDraft()) { RestoreSelection(); return; }
            creating = false; selected = ticket; messages.Clear(); moreMessages = false; body.Text = subject.Text = "";
            await RunAsync(async ct => { await ReadAsync(ct); await ListAsync(ct); });
        };
        older.Click += async (_, _) => await RunAsync(async ct =>
        {
            if (selected is null || messages.Count == 0) return;
            var result = await RequestAsync(new(HelpOperation.Read, selected.Id, MessageId: messages[0].Id), ct);
            messages = result.Messages.Concat(messages).DistinctBy(x => x.Id).OrderBy(x => x.Id).ToList();
            moreMessages = result.HasMore; RenderMessages();
        });
        subject.TextChanged += (_, _) => command = Guid.NewGuid();
        body.TextChanged += (_, _) => command = Guid.NewGuid();
        send.Click += async (_, _) => await RunAsync(async ct =>
        {
            var operation = creating ? HelpOperation.Create : selected?.Status == HelpTicketStatus.Closed ? HelpOperation.Reopen : HelpOperation.Reply;
            if (operation != HelpOperation.Reopen && (string.IsNullOrWhiteSpace(body.Text) || creating && string.IsNullOrWhiteSpace(subject.Text)))
            { error.Text = creating ? PortalText.Get("HelpDesk.DescriptionRequired") : PortalText.Get("HelpDesk.ReplyRequired"); return; }
            var result = await RequestAsync(new(operation, selected?.Id ?? 0, subject.Text, body.Text, command, attachment), ct);
            selected = result.Ticket; SetAttachment(null); creating = false; body.Text = subject.Text = ""; command = Guid.NewGuid();
            messages = result.Messages.ToList(); moreMessages = result.HasMore; RenderMessages(); ScrollToLatest();
            await MarkReadAsync(ct); await ListAsync(ct);
        });
        Controls();
    }

    public void SetSupportResponse(string text) => responseNotice.Text = text;

    public void SetContext(string? sessionToken, bool helpEnabled, string name)
    {
        helpName = name; createLabel.Text = PortalText.Format("HelpDesk.NewNamedRequest", name);
        if (token == sessionToken && enabled == helpEnabled) return;
        CancelRefresh();
        context.Cancel(); context.Dispose(); context = new();
        token = sessionToken; enabled = helpEnabled; selected = null; messages.Clear();
        creating = false; offset = 0; moreMessages = moreTickets = false;
        canCreate = false; scopeMessage = null;
        selecting = true; tickets.ItemsSource = null; selecting = false;
        conversation.Children.Clear(); SetAttachment(null); subject.Text = body.Text = ""; error.Text = "";
        Controls();
    }

    private void Controls()
    {
        var available = enabled && token is not null && !disposed;
        workspace.IsVisible = available;
        notice.IsVisible = !available || scopeMessage is not null;
        notice.Text = available ? scopeMessage : !enabled ? PortalText.Format("HelpDesk.DisabledByAdministrator", helpName) : PortalText.Get("HelpDesk.SignInOnDeviceTab");
        error.IsVisible = !string.IsNullOrEmpty(error.Text);
        filter.IsEnabled = tickets.IsEnabled = !busy;
        create.IsEnabled = !busy && canCreate;
        previous.IsEnabled = !busy && offset > 0; next.IsEnabled = !busy && moreTickets;
        older.IsVisible = !creating && selected is not null && moreMessages; older.IsEnabled = !busy;
        emptyConversation.IsVisible = !creating && selected is null;
        emptyList.IsVisible = tickets.ItemCount == 0;
        editor.IsVisible = available && (creating || selected is not null);
        subject.IsVisible = creating; subject.IsEnabled = body.IsEnabled = !busy;
        var closed = !creating && selected?.Status == HelpTicketStatus.Closed;
        attach.IsVisible = !closed; attach.IsEnabled = removeAttachment.IsEnabled = !busy;
        attach.Content = attachment is null ? PortalText.Get("HelpDesk.TakeScreenshot") : PortalText.Get("HelpDesk.RetakeScreenshot");
        attachmentPreview.IsVisible = removeAttachment.IsVisible = attachment is not null;
        body.IsVisible = !closed; body.PlaceholderText = creating ? PortalText.Get("HelpDesk.DetailedDescription") : PortalText.Get("HelpDesk.ReplyPlaceholder");
        send.Content = creating ? PortalText.Get("HelpDesk.SubmitRequest") : closed ? PortalText.Get("HelpDesk.ReopenRequest") : PortalText.Get("HelpDesk.SendReply");
        send.IsEnabled = !busy && (!creating || canCreate);
    }

    private bool CanLeaveDraft()
    {
        if (!HasDraft) return true;
        error.Text = PortalText.Get("HelpDesk.SendOrClearDraftFirst"); Controls(); return false;
    }

    // Refreshes do not own the editor: typing and user actions remain available.
    // Cancel stale reads before navigation or a command, including transports that
    // finish after cancellation (RequestAsync checks the token before applying data).
    private void CancelRefresh()
    {
        refreshStop?.Cancel();
        refreshStop = null;
        listLoading.IsVisible = false;
    }

    public Task RefreshAsync() => listLoading.IsVisible ? Task.CompletedTask : RefreshCoreAsync(false);

    private async Task RefreshCoreAsync(bool listOnly)
    {
        if (busy || token is null || !enabled || disposed) return;
        CancelRefresh();
        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(context.Token);
        refreshStop = refresh;
        listLoading.IsVisible = listOnly;
        var ct = refresh.Token;
        try
        {
            if (!listOnly && !creating && selected is not null) await ReadAsync(ct);
            await ListAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or System.Net.Sockets.SocketException or System.Text.Json.JsonException or OperationCanceledException)
        {
            if (!ct.IsCancellationRequested)
                error.Text = ex is IOException ? ex.Message : PortalText.Get("HelpDesk.RefreshFailedDraftSaved");
        }
        finally
        {
            if (ReferenceEquals(refreshStop, refresh)) { refreshStop = null; listLoading.IsVisible = false; Controls(); }
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (busy || token is null || !enabled || disposed) return;
        CancelRefresh();
        var ct = context.Token; busy = true; error.Text = ""; Controls();
        try { await work(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or System.Net.Sockets.SocketException or System.Text.Json.JsonException or OperationCanceledException)
        { if (!ct.IsCancellationRequested) error.Text = ex is IOException ? ex.Message : PortalText.Get("HelpDesk.SupportUnavailableDraftSaved"); }
        finally { busy = false; Controls(); }
    }

    private void SetAttachment(byte[]? data)
    {
        attachment = data; attachmentPreview.Source = null; attachmentBitmap?.Dispose(); attachmentBitmap = null;
        if (data is not null) { using var stream = new MemoryStream(data); attachmentBitmap = new Bitmap(stream); attachmentPreview.Source = attachmentBitmap; }
        command = Guid.NewGuid();
    }
    private sealed class HelpError(string message) : IOException(message);
    private async Task<HelpResponse> RequestAsync(HelpRequest input, CancellationToken ct)
    {
        var result = await request(token!, input, ct); ct.ThrowIfCancellationRequested();
        if (result.Error is { } message) throw new HelpError(message);
        return result;
    }

    private async Task ListAsync(CancellationToken ct)
    {
        var result = await RequestAsync(new(HelpOperation.List, Offset: offset,
            StatusFilter: filter.SelectedIndex > 0 ? (HelpTicketStatus)(filter.SelectedIndex - 1) : null), ct);
        moreTickets = result.HasMore;
        canCreate = result.CanCreate; scopeMessage = result.ScopeMessage;
        emptyList.Text = filter.SelectedIndex == 0 ? PortalText.Get("HelpDesk.NoRequestsYet") : PortalText.Get("HelpDesk.NoRequestsWithStatus");
        selecting = true;
        tickets.ItemsSource = result.Tickets.Select(ticket =>
        {
            var row = new StackPanel { Spacing = 7 };
            var subjectText = Label(ticket.Subject, 14, true); subjectText.MaxLines = 2; subjectText.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Children.Add(subjectText);
            var updated = Label(PortalText.Format("HelpDesk.RequestUpdated", ticket.Id, ticket.UpdatedAtUtc.ToLocalTime()), 11); updated.Classes.Add("muted"); row.Children.Add(updated);
            var statusText = Label(Status(ticket.Status), 11); statusText.Classes.Add("muted");
            var badge = new Border { Child = statusText, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3),
                HorizontalAlignment = HorizontalAlignment.Right, Classes = { "statusBadge" } };
            row.Children.Add(badge);
            if (ticket.Unread) row.Children.Add(Label(PortalText.Get("HelpDesk.UnreadIndicator"), 12, true));
            return new ListBoxItem { Content = row, Tag = ticket, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        }).ToArray();
        selecting = false; RestoreSelection();
    }

    private void RestoreSelection()
    {
        selecting = true;
        tickets.SelectedItem = tickets.Items.OfType<ListBoxItem>().FirstOrDefault(x => x.Tag is HelpTicketSummary row && row.Id == selected?.Id);
        selecting = false;
    }

    private async Task ReadAsync(CancellationToken ct)
    {
        var result = await RequestAsync(new(HelpOperation.Read, selected!.Id), ct);
        selected = result.Ticket;
        if (messages.Count == 0) moreMessages = result.HasMore;
        var changed = !messages.TakeLast(result.Messages.Length).SequenceEqual(result.Messages);
        // A long absence can exceed a page. Reset to the latest page instead of
        // displaying a conversation with an invisible gap in the middle.
        if (messages.Count > 0 && result.Messages.Length > 0 && !messages.Any(x => result.Messages.Any(y => y.Id == x.Id)))
        { messages.Clear(); moreMessages = result.HasMore; }
        messages = messages.Concat(result.Messages).GroupBy(x => x.Id).Select(x => x.Last()).OrderBy(x => x.Id).ToList();
        if (changed) { RenderMessages(); ScrollToLatest(); }
        await MarkReadAsync(ct);
    }

    private async Task MarkReadAsync(CancellationToken ct)
    {
        if (IsVisible && TopLevel.GetTopLevel(this) is Window { IsVisible: true } && selected is not null && messages.Count > 0)
            await RequestAsync(new(HelpOperation.MarkRead, selected.Id, MessageId: messages[^1].Id), ct);
    }

    private void ScrollToLatest() => Avalonia.Threading.Dispatcher.UIThread.Post(() => scroll.ScrollToEnd(),
        Avalonia.Threading.DispatcherPriority.Loaded);

    private void RenderMessages()
    {
        conversation.Children.Clear();
        foreach (var message in messages)
        {
            var content = new StackPanel { Spacing = 6 };
            content.Children.Add(Label(message.FromStaff ? message.Author : PortalText.Get("HelpDesk.You"), 13, true));
            content.Children.Add(Label(message.Body));
            var timestamp = Label(message.CreatedAtUtc.ToLocalTime().ToString("g"), 11); timestamp.Classes.Add("muted"); content.Children.Add(timestamp);
            if (message.HasScreenshot)
            {
                var screenshot = new Button { Content = PortalText.Get("HelpDesk.ViewScreenshot") };
                screenshot.Click += async (_, _) => await RunAsync(async ct =>
                {
                    var response = await RequestAsync(new(HelpOperation.Screenshot, selected!.Id, MessageId: message.Id), ct);
                    if (response.Screenshot is null) throw new HelpError(PortalText.Get("Screenshot.Unavailable"));
                    using var stream = new MemoryStream(response.Screenshot);
                    using var bitmap = new Bitmap(stream);
                    var preview = new Window { Title = PortalText.Get("HelpDesk.Screenshot"), Width = 720, Height = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Content = new Image { Source = bitmap, Stretch = Stretch.Uniform } };
                    var previewContent = new Grid { RowDefinitions = new("Auto,*"), Margin = new Thickness(16) };
                    var previewHeading = PortalPopupBranding.Header(preview, PortalText.Get("HelpDesk.Screenshot")); previewHeading.Margin = new Thickness(0, 0, 0, 12);
                    previewContent.Children.Add(previewHeading);
                    var previewImage = (Control)preview.Content!; Grid.SetRow(previewImage, 1); preview.Content = null;
                    previewContent.Children.Add(previewImage); preview.Content = previewContent;
                    if (TopLevel.GetTopLevel(this) is Window owner)
                    {
                        using var registration = ct.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(preview.Close));
                        await preview.ShowDialog(owner);
                    }
                });
                content.Children.Add(screenshot);
            }
            var bubble = new Border { Child = content, Classes = { "message" }, MaxWidth = 520, Margin = new Thickness(0, 0, 0, 3),
                HorizontalAlignment = message.FromStaff ? HorizontalAlignment.Left : HorizontalAlignment.Right };
            if (!message.FromStaff) bubble.Classes.Add("mine");
            conversation.Children.Add(bubble);
        }
    }

    private static TextBlock Label(string text, double size = 13, bool bold = false) => new()
    { Text = text, FontSize = size, FontWeight = bold ? FontWeight.Medium : FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
    private static string Status(HelpTicketStatus status) => status switch
    { HelpTicketStatus.InProgress => PortalText.Get("HelpDesk.InProgress"), HelpTicketStatus.WaitingOnUser => PortalText.Get("HelpDesk.WaitingOnYou"), HelpTicketStatus.Closed => PortalText.Get("HelpDesk.Closed"), _ => PortalText.Get("HelpDesk.AwaitingReview") };
    public void Dispose() { if (disposed) return; disposed = true; CancelRefresh(); SetAttachment(null); context.Cancel(); context.Dispose(); }
}
