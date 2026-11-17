using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.MacOS.Portal;
public sealed partial class PortalWindow : Window
{
    private readonly TextBlock title = Text(PortalText.Get("Portal.Title"), 22, FontWeight.SemiBold);
    private readonly TextBlock organization = new() { FontSize = 11, IsVisible = false, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock device = Text(Environment.MachineName, 24, FontWeight.SemiBold);
    private readonly TextBlock connection = Text(PortalText.Get("Connection.ConnectingToDeviceService"), 13);
    private readonly TextBlock enrollment = Text("", 14);
    private readonly TextBlock checkin = Text("", 13);
    private readonly TextBlock version = Text("", 12);
    private readonly TextBlock refreshed = Text("", 12);
    private readonly Image logo = new() { MaxWidth = 160, Height = 32, Stretch = Stretch.Uniform, IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    private readonly Border header = new() { Padding = new Thickness(24, 10), BorderThickness = new Thickness(0, 0, 0, 1), Classes = { "surface" } };
    private readonly Button refresh = new() { Width = 32, Height = 32, MinHeight = 32, Padding = new Thickness(7),
        VerticalAlignment = VerticalAlignment.Center, Classes = { "icon" },
        Content = new PathIcon { Width = 18, Height = 18, Data = Geometry.Parse(
            "M17.65,6.35 C16.2,4.9 14.21,4 12,4 C7.58,4 4,7.58 4,12 C4,16.42 7.58,20 12,20 C15.73,20 18.84,17.45 19.73,14 H17.65 C16.83,16.33 14.61,18 12,18 C8.69,18 6,15.31 6,12 C6,8.69 8.69,6 12,6 C13.66,6 15.14,6.69 16.22,7.78 L13,11 H20 V4 Z") } };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly CancellationTokenSource stop = new();
    private readonly TextBlock userLabel = Text(Environment.UserName, 13, FontWeight.SemiBold);
    private readonly Button signInBanner = new() { Content = PortalText.Get("SignIn.WorkAccountBanner"), IsVisible = false,
        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(14, 6) };
    private Action? openAccount;
    private Window? signInReminder;
    private bool remindedSignIn;
    private bool promptForMacSignIn;
    private bool refreshing;
    private Action? selectHelp;
    private Window? notification;
    private TextBlock? notificationText;
    private string? notificationToken;
    private long helpRevision = -1, notifiedUpdate;
    private DateTimeOffset nextHelpRecovery;
    private bool notificationBusy;
    private readonly PortalNotificationsPanel notificationsPanel;
    private readonly PortalHelpPanel helpPanel;
    private readonly PortalSelfServicePanel selfServicePanel;
    private readonly Button selfServiceTab = new() { IsVisible = false, Classes = { "navigation" } };
    private bool selfServiceEnabled;
    private readonly PortalRemoteDesktop remote;
    private readonly TextBlock helpTabLabel = Text(PortalText.Get("HelpDesk.Title"), 13);
    private readonly Button helpTab = new() { IsVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch };
    private bool helpEnabled;
    private string helpName = PortalText.Get("HelpDesk.Title");
    private string? brandingVersion;
    private AgentBranding currentBranding = new();
    private Bitmap? logoBitmap;

    public PortalWindow()
    {
        Title = PortalText.Get("Portal.Title");
        ActualThemeVariantChanged += (_, _) => { brandingVersion = null; ApplyBranding(currentBranding); };
        remote = new PortalRemoteDesktop(this);
        helpPanel = new PortalHelpPanel(RequestHelpAsync) { IsVisible = false };
        selfServicePanel = new PortalSelfServicePanel(async request =>
        {
            var response = await PortalClient.AuthenticateAsync(request, stop.Token);
            if (response.SelfService?.Status == "SignedOut") nextAccountCheck = DateTimeOffset.MinValue;
            return response.SelfService ?? new("Unavailable", PortalText.Get("SelfService.Unavailable"));
        }) { IsVisible = false };
        Width = 980; Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var brand = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(organization); brand.Children.Add(title);
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), MinHeight = 40 };
        headerGrid.Children.Add(logo); Grid.SetColumn(brand, 1); headerGrid.Children.Add(brand); header.Child = headerGrid;
        var sidebar = new StackPanel { Spacing = 6, Margin = new Thickness(14, 24) };
        var deviceTab = new Button { Content = NavigationItem(Text(PortalText.Get("Common.Device"), 13),
            "F0 M2,3 H22 V17 H14 V20 H18 V22 H6 V20 H10 V17 H2 Z M4,5 V15 H20 V5 Z"), Classes = { "navigation", "selected" } };
        helpTab.Content = NavigationItem(helpTabLabel,
            "F0 M3,3 H21 V17 H9 L3,22 Z M5,5 V18 L8,15 H19 V5 Z M7,8 H17 V10 H7 Z M7,12 H14 V14 H7 Z");
        helpTab.Classes.Add("navigation");
        var notificationsTab = new Button { Content = NavigationItem(Text(PortalText.Get("Common.Notifications"), 13),
            "M2,3 H22 V17 H14 V20 H18 V22 H6 V20 H10 V17 H2 Z M4,5 V15 H20 V5 Z"), Classes = { "navigation" } };
        selfServiceTab.Content = NavigationItem(Text(PortalText.Get("Common.SelfServiceMessage"), 13), "M3,3 H10 V10 H3 Z M14,3 H21 V10 H14 Z M3,14 H10 V21 H3 Z M14,14 H21 V21 H14 Z");
        sidebar.Children.Add(deviceTab); sidebar.Children.Add(selfServiceTab); sidebar.Children.Add(notificationsTab); sidebar.Children.Add(helpTab);
        var footer = new StackPanel { Spacing = 6, Margin = new Thickness(20), VerticalAlignment = VerticalAlignment.Bottom };
        var identity = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var deviceLabel = Text(Environment.MachineName, 12);
        userLabel.TextTrimming = deviceLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        identity.Children.Add(userLabel); identity.Children.Add(deviceLabel);
        var identityRow = new Grid { ColumnDefinitions = new("*,Auto") };
        identityRow.Children.Add(identity); Grid.SetColumn(refresh, 1); identityRow.Children.Add(refresh);
        ToolTip.SetTip(refresh, PortalText.Get("Common.Refresh")); ToolTip.SetShowDelay(refresh, 0);
        Avalonia.Automation.AutomationProperties.SetName(refresh, PortalText.Get("Common.Refresh"));
        footer.Children.Add(identityRow); footer.Children.Add(refreshed);
        foreach (var label in new[] { version, refreshed, checkin, accountMessage }) label.Classes.Add("muted");
        connection.Classes.Add("muted");
        var nav = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        nav.Children.Add(sidebar); Grid.SetRow(footer, 1); nav.Children.Add(footer);
        var body = new StackPanel { Spacing = 18, Margin = new Thickness(24) };
        var overview = new StackPanel { Spacing = 8 };
        overview.Children.Add(new TextBlock { Text = PortalText.Get("Device.Overview"), Classes = { "section" } });
        overview.Children.Add(device); overview.Children.Add(connection);
        var details = new StackPanel { Spacing = 5, Margin = new Thickness(0, 10, 0, 0) };
        enrollment.Classes.Add("muted"); details.Children.Add(enrollment); details.Children.Add(checkin);
        overview.Children.Add(details);
        body.Children.Add(new Border { Classes = { "card" }, Child = overview });
        body.Children.Add(new Border { Classes = { "card" }, Child = BuildAccountPanel() });
        body.Children.Add(new Border { Classes = { "card" }, Child = remote.Settings });
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("190,*") };
        columns.Children.Add(new Border { Child = nav, Classes = { "surface" }, BorderThickness = new Thickness(0, 0, 1, 0) });
        var devicePanel = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        notificationsPanel = new PortalNotificationsPanel(this, () =>
        {
            selfServicePanel.IsVisible = false; selfServiceTab.Classes.Remove("selected");
            devicePanel.IsVisible = helpPanel.IsVisible = false; notificationsPanel!.IsVisible = true;
            deviceTab.Classes.Remove("selected"); helpTab.Classes.Remove("selected"); notificationsTab.Classes.Add("selected");
            Show(); Activate();
        }) { IsVisible = false };
        notificationsPanel.SessionExpired = () =>
        {
            accountSession = null; accountToken = null; nextAccountCheck = DateTimeOffset.MinValue;
            ShowAccount(); AccountControls(); RemindSignIn();
        };
        openAccount = () =>
        {
            selfServicePanel.IsVisible = false; selfServiceTab.Classes.Remove("selected");
            notificationsPanel.IsVisible = helpPanel.IsVisible = false; devicePanel.IsVisible = true;
            notificationsTab.Classes.Remove("selected"); helpTab.Classes.Remove("selected"); deviceTab.Classes.Add("selected");
            Show(); WindowState = WindowState.Normal; Activate(); signIn.Focus();
        };
        signInBanner.Click += (_, _) => openAccount();
        var content = new Grid();
        content.Children.Add(selfServicePanel); content.Children.Add(devicePanel); content.Children.Add(helpPanel); content.Children.Add(notificationsPanel);
        Grid.SetColumn(content, 1); columns.Children.Add(content);
        deviceTab.Click += (_, _) => { selfServicePanel.IsVisible = false; selfServiceTab.Classes.Remove("selected"); notificationsPanel.IsVisible = false; notificationsTab.Classes.Remove("selected"); devicePanel.IsVisible = true; helpPanel.IsVisible = false; deviceTab.Classes.Add("selected"); helpTab.Classes.Remove("selected"); };
        selectHelp = () => { selfServicePanel.IsVisible = false; selfServiceTab.Classes.Remove("selected"); notificationsPanel.IsVisible = false; notificationsTab.Classes.Remove("selected"); devicePanel.IsVisible = false; helpPanel.IsVisible = true; deviceTab.Classes.Remove("selected"); helpTab.Classes.Add("selected"); };
        notificationsTab.Click += async (_, _) =>
        {
            selfServicePanel.IsVisible = false; selfServiceTab.Classes.Remove("selected");
            devicePanel.IsVisible = helpPanel.IsVisible = false; notificationsPanel.IsVisible = true;
            deviceTab.Classes.Remove("selected"); helpTab.Classes.Remove("selected"); notificationsTab.Classes.Add("selected");
            await notificationsPanel.RefreshAsync();
        };
        selfServiceTab.Click += async (_, _) =>
        {
            devicePanel.IsVisible = helpPanel.IsVisible = notificationsPanel.IsVisible = false; selfServicePanel.IsVisible = true;
            deviceTab.Classes.Remove("selected"); helpTab.Classes.Remove("selected"); notificationsTab.Classes.Remove("selected"); selfServiceTab.Classes.Add("selected");
            await selfServicePanel.RefreshAsync();
        };
        helpTab.Click += async (_, _) => { selectHelp(); await RefreshAsync(); };
        var attribution = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        attribution.Children.Add(new TextBlock { Text = PortalText.Get("Common.PoweredBy"), FontSize = 11, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center });
        attribution.Children.Add(BuildFooterLogo());
        attribution.Children.Add(new TextBlock { Text = "CloneDeploy", FontSize = 12, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center });
        version.FontSize = 11; version.VerticalAlignment = VerticalAlignment.Center;
        version.HorizontalAlignment = HorizontalAlignment.Right;
        var footerGrid = new Grid { ColumnDefinitions = new("*,Auto") };
        footerGrid.Children.Add(attribution); Grid.SetColumn(version, 1); footerGrid.Children.Add(version);
        var appFooter = new Border { Child = footerGrid, Classes = { "surface" }, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(24, 12) };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        root.Children.Add(header); Grid.SetRow(signInBanner, 1); root.Children.Add(signInBanner); Grid.SetRow(columns, 2); root.Children.Add(columns);
        Grid.SetRow(appFooter, 3); root.Children.Add(appFooter); Content = root;
        refresh.Click += async (_, _) => { nextAccountCheck = DateTimeOffset.MinValue; await RefreshAsync(); };
        timer.Tick += async (_, _) => await RefreshAsync();
        timer.Start();
        Dispatcher.UIThread.Post(async () => await RefreshAsync());
        Opened += async (_, _) => { FitScreen(); timer.Start(); nextAccountCheck = DateTimeOffset.MinValue; await RefreshAsync(); };
        Closing += (_, e) =>
        {
            // Keep the portal available after a normal window close, but never
            // cancel application shutdown (including macOS logout/restart).
            if (!stop.IsCancellationRequested && e.CloseReason == WindowCloseReason.WindowClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }
    private static Control BuildFooterLogo()
    {
        var canvas = new Canvas { Width = 268.435, Height = 185.087 };
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M91.37,44.609h-34.435c-2.053,0-3.717,1.664-3.717,3.717v86.478c0,2.053,1.664,3.717,3.717,3.717h70.826c2.053,0,3.717,1.664,3.717,3.717v39.13c0,2.053-1.664,3.717-3.717,3.717H24.261c-13.399,0-24.261-10.862-24.261-24.261V24.261C0,10.862,10.862,0,24.261,0h103.5C129.814,0,131.478,1.664,131.478,3.717v57.913c0,2.053-1.664,3.717-3.717,3.717h-28.957c-2.053,0-3.717-1.664-3.717-3.717v-13.304c0-2.053-1.664-3.717-3.717-3.717Z"), Fill = Brush.Parse("#182337") });
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M177.065,140.478h34.435c2.053,0,3.717-1.664,3.717-3.717V50.283c0-2.053-1.664-3.717-3.717-3.717h-70.826c-2.053,0-3.717-1.664-3.717-3.717V3.717C136.957,1.664,138.621,0,140.674,0h103.5c13.399,0,24.261,10.862,24.261,24.261v136.565c0,13.399-10.862,24.261-24.261,24.261h-103.5c-2.053,0-3.717-1.664-3.717-3.717v-57.913c0-2.053,1.664-3.717,3.717-3.717h28.957c2.053,0,3.717,1.664,3.717,3.717v13.304c0,2.053,1.664,3.717,3.717,3.717Z"), Fill = Brush.Parse("#2490FF") });
        return new Viewbox { Child = canvas, Width = 26, Height = 22, Stretch = Stretch.Uniform };
    }

    private static Control NavigationItem(TextBlock label, string geometry)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        row.Children.Add(new PathIcon { Data = Geometry.Parse(geometry), Width = 18, Height = 18,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 1); row.Children.Add(label);
        return row;
    }

    private void FitScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        Width = Math.Min(980, screen.WorkingArea.Width / screen.Scaling * .9);
        Height = Math.Min(680, screen.WorkingArea.Height / screen.Scaling * .9);
        Position = new PixelPoint(screen.WorkingArea.X + (int)((screen.WorkingArea.Width - Width * screen.Scaling) / 2),
            screen.WorkingArea.Y + (int)((screen.WorkingArea.Height - Height * screen.Scaling) / 2));
    }
    private async Task RefreshAsync()
    {
        if (refreshing || stop.IsCancellationRequested) return;
        refreshing = true; refresh.IsEnabled = false;
        try
        {
            var status = await PortalClient.ReadAsync(stop.Token);
            device.Text = status.DeviceName;
            connection.Classes.Set("success", status.Connected);
            connection.Classes.Set("muted", !status.Connected);
            connection.Text = status.Connected ? PortalText.Get("Connection.OrganizationConnected") : PortalText.Get("Connection.WaitingForOrganization");
            enrollment.Text = status.Enrollment switch
            {
                "CheckedIn" => PortalText.Get("Common.Enrolled"),
                "PendingApproval" or "PendingPreProvision" => PortalText.Get("Connection.WaitingForApproval"),
                "PendingReset" => PortalText.Get("Connection.WaitingForIdentityReset"),
                "RateLimited" => PortalText.Get("Connection.RetryingSoon"),
                _ => PortalText.Get("Connection.DeviceServiceStarting")
            };
            checkin.Text = status.LastCheckinUtc is { } time ? PortalText.Format("Device.LastCheckIn", time.LocalDateTime) : PortalText.Get("Connection.WaitingForFirstCheckIn");
            version.Text = PortalText.Get("Device.AgentVersionPrefix") + AgentUpdateOffer.DisplayVersion(status.Version);
            ApplyBranding(status.Branding);
            // Wait for the service's first check-in so default settings cannot prompt before policy arrives.
            var shouldPrompt = status.LastCheckinUtc is not null && status.Features.PromptForMacSignIn;
            if (shouldPrompt && !promptForMacSignIn) nextAccountCheck = DateTimeOffset.MinValue;
            promptForMacSignIn = shouldPrompt;
            if (!promptForMacSignIn) RemindSignIn();
            selfServiceEnabled = status.Features.SelfServiceEnabled; selfServiceTab.IsVisible = selfServiceEnabled;
            helpEnabled = status.Features.HelpDeskEnabled;
            helpName = status.Branding.HelpTabText;
            helpTabLabel.Text = helpName; helpTab.IsVisible = helpEnabled;
            UpdateHelpContext();
            refreshed.Text = PortalText.Format("Portal.UpdatedAt", DateTime.Now);
            await RefreshAccountAsync();
            selfServicePanel.SetContext(selfServiceEnabled, accountToken);
            if (IsVisible && selfServicePanel.IsVisible) await selfServicePanel.RefreshAsync();
            if (accountToken is not null)
                await PortalClient.AuthenticateAsync(new("identity-watch", Token: accountToken), stop.Token);
            if (IsVisible && helpPanel.IsVisible) await helpPanel.RefreshAsync();
            await RefreshHelpNotificationAsync();
            await notificationsPanel.RefreshAsync();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or System.Text.Json.JsonException or OperationCanceledException)
        {
            connection.Classes.Remove("success"); connection.Classes.Add("muted");
            connection.Text = PortalText.Get("Common.DeviceServiceUnavailable");
            enrollment.Text = PortalText.Get("Connection.ServiceRestartingHint");
            checkin.Text = ""; refreshed.Text = PortalText.Get("Common.UnableToRefresh");
        }
        finally { refreshing = false; refresh.IsEnabled = true; }
    }
    private void ApplyBranding(AgentBranding branding)
    {
        if (brandingVersion == branding.Version) return;
        brandingVersion = branding.Version;
        currentBranding = branding;
        branding = branding.ForTheme(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);
        Title = title.Text = branding.PortalTitleText;
        helpPanel.SetSupportResponse(branding.SupportResponseText);
        notificationsPanel.SetBranding(branding);
        organization.Text = branding.OrganizationName;
        organization.IsVisible = !string.IsNullOrWhiteSpace(branding.OrganizationName);
        if (branding.IsConfigured && AgentBranding.ValidColor(branding.BackgroundColor))
        {
            header.Background = Brush.Parse(branding.BackgroundColor);
            title.Foreground = organization.Foreground = Brush.Parse(branding.ForegroundColor);
        }
        else { header.ClearValue(Border.BackgroundProperty); title.ClearValue(TextBlock.ForegroundProperty); organization.ClearValue(TextBlock.ForegroundProperty); }
        if (AgentBranding.ValidColor(branding.AccentColor))
        {
            Resources["PortalAction"] = Brush.Parse(branding.AccentColor);
            Resources["PortalActionText"] = Brush.Parse(branding.AccentForegroundColor);
        }
        logo.Source = null; logo.IsVisible = false; logoBitmap?.Dispose(); logoBitmap = null;
        if (branding.LogoPng is { Length: > 0 and <= CloneDeploy_Agent.Contracts.AgentBranding.MaximumLogoBytes })
        {
            try { using var stream = new MemoryStream(branding.LogoPng); logoBitmap = Bitmap.DecodeToWidth(stream, 320); logo.Source = logoBitmap; logo.IsVisible = true; }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
        }
    }
    public void Stop() { if (stop.IsCancellationRequested) return; signInStop?.Cancel(); stop.Cancel(); timer.Stop(); remote.Dispose(); notificationsPanel.Dispose(); notification?.Close(); signInReminder?.Close(); helpPanel.Dispose(); selfServicePanel.Dispose(); logoBitmap?.Dispose(); }
    private void UpdateHelpContext()
    {
        helpPanel.SetContext(accountSession is null ? null : accountToken, helpEnabled, helpName);
        if (accountSession is null || !helpEnabled || notificationToken != accountToken)
        { notification?.Close(); notification = null; notificationToken = accountToken; helpRevision = -1; notifiedUpdate = 0; nextHelpRecovery = default; }
    }
    private async Task RefreshHelpNotificationAsync()
    {
        if (notificationBusy || accountSession is null || accountToken is null || !helpEnabled) return;
        notificationBusy = true;
        var token = accountToken;
        try
        {
            // Local lease renewal only. The daemon subscribes over its existing socket.
            var watch = await PortalClient.AuthenticateAsync(new("help-watch", Token: token), stop.Token);
            if (token != accountToken || !helpEnabled) return;
            if (watch.Status != "Watching") return;
            if (helpRevision == watch.Revision && DateTimeOffset.UtcNow < nextHelpRecovery) return;
            var count = await RequestHelpAsync(token, new(HelpOperation.Count), stop.Token);
            if (count.Error is not null || token != accountToken || !helpEnabled) return;
            helpRevision = watch.Revision; nextHelpRecovery = DateTimeOffset.UtcNow.AddMinutes(5);
            if (count.UnreadCount == 0) { notification?.Close(); notification = null; return; }
            if (count.LatestUpdate <= notifiedUpdate) return;
            notifiedUpdate = count.LatestUpdate;
            if (IsVisible && IsActive && helpPanel.IsVisible) return;
            if (notification is null)
            {
                var popup = new Window { Title = helpName, Width = 360, SizeToContent = SizeToContent.Height,
                    CanResize = false, ShowInTaskbar = false, ShowActivated = false, Topmost = true };
                var panel = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
                panel.Children.Add(PortalPopupBranding.Header(popup, PortalText.Format("HelpDesk.NamedUpdate", helpName)));
                notificationText = Text("", 14, FontWeight.SemiBold); panel.Children.Add(notificationText);
                var open = new Button { Content = PortalText.Format("HelpDesk.OpenNamed", helpName) };
                var dismiss = new Button { Content = PortalText.Get("Common.Dismiss") };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                buttons.Children.Add(open); buttons.Children.Add(dismiss); panel.Children.Add(buttons); popup.Content = panel;
                open.Click += async (_, _) => { popup.Close(); selectHelp?.Invoke(); Show(); WindowState = WindowState.Normal; Activate(); await RefreshAsync(); };
                dismiss.Click += (_, _) => popup.Close();
                popup.Closed += (_, _) => { if (notification == popup) notification = null; };
                notification = popup;
                PortalPopupPlacement.Configure(popup, this);
                notificationText.Text = PortalText.Format("HelpDesk.UnreadCount", count.UnreadCount, helpName);
                popup.Show();
            }
            else notificationText!.Text = PortalText.Format("HelpDesk.UnreadCount", count.UnreadCount, helpName);
        }
        finally { notificationBusy = false; }
    }
    private async Task<HelpResponse> RequestHelpAsync(string token, HelpRequest request, CancellationToken ct)
    {
        var result = await PortalClient.AuthenticateAsync(new("help", Token: token, Help: request), ct);
        ct.ThrowIfCancellationRequested();
        if (result.Status == "SignedOut" && accountToken == token)
        {
            accountSession = null; accountToken = null; ShowAccount(); AccountControls();
            return new HelpResponse(PortalText.Get("SignIn.SessionEndedDeviceTab"));
        }
        return result.Help ?? new HelpResponse(PortalText.Get("HelpDesk.SupportUnavailable"));
    }
    private static TextBlock Text(string text, double size, FontWeight? weight = null) => new()
    { Text = text, FontSize = size, FontWeight = weight ?? FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
}
