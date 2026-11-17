using System.Windows;
using System.Windows.Threading;
using System.Windows.Interop;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.UI;

/// <summary>Shows a plain-text notice without taking keyboard focus; history survives dismissal.</summary>
public partial class NotificationPopup : Window
{
    private const int MaximumNotificationSeconds = 86400;

    private readonly DispatcherTimer timer;
    private readonly Action openMessages;
    public AgentBranding Branding { get; set; } = new();
    public void UpdateBranding(AgentBranding branding)
    {
        Branding = branding;
        if (DataContext is AgentPortalNotification message) ApplyAppearance(message);
    }
    public bool IsHelpNotification { get; init; }
    public bool AutoClose { get; init; } = true;
    public Func<Task>? AcknowledgeNotification { get; init; }
    public Func<Task>? DismissMessage { get; init; }
    private bool interactive;
    private bool consentLayout;
    private bool closeAuthorized, dismissing;
    public void CloseFromService() { closeAuthorized = true; Close(); }
    public void ShowOpenAction(string label) { OpenActionButton.Content = label; OpenActionButton.Visibility = Visibility.Visible; }
    public string ActionFailureMessage { get; init; } = PortalText.Get("Notifications.SaveAcknowledgmentFailed");
    public void SetConsentButtons(bool active)
    {
        consentLayout = true;
        Width = 380;
        interactive = !active;
        Title = active ? PortalText.Get("Notifications.CloneDeployRemoteSharing") : PortalText.Get("Notifications.CloneDeployRemoteAccessRequest");
        ShowActivated = !active;
        DismissButton.IsDefault = !active;
        AcknowledgeButton.Visibility = Visibility.Visible;
        AcknowledgeButton.Content = active ? PortalText.Get("Common.StopSharing") : PortalText.Get("Common.Allow");
        DismissButton.Content = active ? PortalText.Get("Common.StopSharing") : PortalText.Get("Common.Deny");
        if (active) AcknowledgeButton.Visibility = Visibility.Collapsed;
    }
    public NotificationPopup(AgentPortalNotification message, Action openMessages)
    {
#pragma warning disable WPF0001 // Intentionally opt in to WPF's experimental theme API.
        ThemeMode = System.Windows.ThemeMode.System;
#pragma warning restore WPF0001
        InitializeComponent(); DataContext = message; this.openMessages = openMessages;
        Width = 380;
        PortalTheme.Attach(this, () => ApplyAppearance((AgentPortalNotification)DataContext));
        DataContextChanged += (_, _) => System.Windows.Automation.AutomationProperties.SetName(ToastOpenButton,
            DataContext is AgentPortalNotification current ? PortalText.Format("Notifications.OpenDetailsDescription", current.Title, current.Body) : PortalText.Get("Notifications.OpenDetails"));
        Closing += (_, e) =>
        {
            if (DismissMessage is null || closeAuthorized) return;
            e.Cancel = true;
            if (!dismissing) Dispatcher.BeginInvoke(new Action(() => Dismiss(this, new RoutedEventArgs())));
        };
        if (message.PendingAcknowledgment)
        {
            AcknowledgeButton.Visibility = Visibility.Visible;
            DismissButton.Content = PortalText.Get("Common.Later");
        }
        var timeout = TimeSpan.FromSeconds(Math.Clamp(message.TimeoutSeconds, 1, MaximumNotificationSeconds));
        if (message.ExpiresAt is { } expiry && expiry - DateTimeOffset.UtcNow < timeout)
            timeout = TimeSpan.FromMilliseconds(Math.Max(1, (expiry - DateTimeOffset.UtcNow).TotalMilliseconds));
        timer = new DispatcherTimer { Interval = timeout };
        timer.Tick += (_, _) =>
        {
            if (DismissMessage is not null && message.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow) CloseFromService();
            else Close();
        };
        Loaded += (_, _) =>
        {
            ApplyCompactLayout();
            ApplyToastLayout(message);
            ApplyAppearance(message);
            var area = SystemParameters.WorkArea;
            MaxWidth = Math.Max(320, area.Width - 32);
            MaxHeight = Math.Max(240, area.Height - 32);
            UpdateLayout();
            Left = Math.Max(area.Left, message.Position == NotificationPosition.Center ? area.Left + (area.Width - ActualWidth) / 2 : area.Right - ActualWidth - 16);
            Top = Math.Max(area.Top, message.Position == NotificationPosition.Center ? area.Top + (area.Height - ActualHeight) / 2 : area.Bottom - ActualHeight - 16);
            if (AutoClose) timer.Start();
        };
        Closed += (_, _) => timer.Stop();
    }
    private void ApplyCompactLayout()
    {
        NotificationLayout.MinHeight = 0;
        // All notifications share the stacked header and content layout.
        NotificationLayout.RowDefinitions.Clear();
        NotificationLayout.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        NotificationLayout.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        NotificationLayout.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        NotificationLayout.ColumnDefinitions[1].Width = new GridLength(0);
        System.Windows.Controls.Grid.SetColumn(NotificationContent, 0);
        System.Windows.Controls.Grid.SetRow(NotificationContent, 1);
        System.Windows.Controls.Grid.SetColumn(CloseButton, 0);
        BrandPanel.CornerRadius = new CornerRadius(13, 13, 0, 0);
        BrandPanel.Padding = new Thickness(14, 10, 44, 10);
        ExpandedBrandContent.Visibility = Visibility.Collapsed;
        CompactBrandHeader.Visibility = Visibility.Visible;
        NotificationContent.Margin = new Thickness(18, 14, 18, 16);
        NotificationTitle.FontSize = 18;
        NotificationTitle.Margin = new Thickness(0);
        NotificationBody.FontSize = 14;
        NotificationScroll.MaxHeight = 180;
        NotificationScroll.Margin = new Thickness(0, 10, 0, 0);
        NotificationActions.Margin = new Thickness(0, 16, 0, 0);
    }
    private void ApplyToastLayout(AgentPortalNotification message)
    {
        Width = 380;
        BrandPanel.Padding = new Thickness(12, 8, 42, 6);
        CompactBrandName.MaxWidth = 270;
        NotificationContent.Margin = new Thickness(12, 4, 12, 14);
        NotificationTitle.FontSize = 14;
        NotificationBody.FontSize = 12;
        NotificationScroll.Margin = new Thickness(0, 5, 0, 0);
        var requiresAction = consentLayout || message.PendingAcknowledgment;
        NotificationScroll.MaxHeight = requiresAction ? 180 : 44;
        NotificationScroll.VerticalScrollBarVisibility = requiresAction ? System.Windows.Controls.ScrollBarVisibility.Auto : System.Windows.Controls.ScrollBarVisibility.Disabled;
        NotificationActions.Visibility = Visibility.Visible;
        NotificationActions.Margin = new Thickness(0, 8, 0, 0);
        if (!requiresAction)
        {
            DismissButton.Visibility = Visibility.Collapsed;
            if (OpenActionButton.Content is null) OpenActionButton.Content = PortalText.Get("Notifications.OpenNotifications");
            OpenActionButton.Visibility = Visibility.Visible;
        }
        foreach (var element in new UIElement[] { NotificationTitle, NotificationScroll, NotificationActions })
            System.Windows.Controls.Grid.SetColumn(element, 1);
        NotificationIcon.Visibility = Visibility.Visible;
        NotificationGlyph.Text = consentLayout ? "\uE72E" : IsHelpNotification || Equals(OpenActionButton.Content, PortalText.Get("Notifications.OpenHelp")) ? "\uE897" : message.Kind switch
        {
            NotificationKind.Success => "\uE73E", NotificationKind.Warning or NotificationKind.Critical => "\uE7BA", _ => "\uE946"
        };
        ToastOpenButton.Visibility = requiresAction ? Visibility.Collapsed : Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetName(ToastOpenButton, PortalText.Format("Notifications.OpenDetailsDescription", message.Title, message.Body));
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(HandleWindowMessage);
    }

    private nint HandleWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int wmMouseActivate = 0x0021;
        const int maNoActivate = 3;
        if (message == wmMouseActivate && !interactive)
        {
            // ShowActivated=false covers showing the toast, not clicking it. Keep the current
            // foreground window when handling buttons to avoid a focus-restoration transition on close.
            // MA_NOACTIVATE preserves the mouse event; MA_NOACTIVATEANDEAT would swallow it.
            handled = true;
            return maNoActivate;
        }
        return 0;
    }

    private void ApplyAppearance(AgentPortalNotification message)
    {
        // Ignore legacy per-notification overrides, including previously queued notices.
        var brand = Branding.ForTheme(Resources["PortalDark"] is true);
        BrandAccent.Apply(this, brand);
        var background = AgentBranding.ValidColor(brand.BackgroundColor) ? brand.BackgroundColor : "#2459D6";
        BrandPanel.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(background)!;
        var foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(brand.ForegroundColor)!;
        foreach (var text in new[] { BrandName, BrandTagline, PoweredBy, ProductName }) text.Foreground = foreground;
        CompactBrandName.Text = string.IsNullOrWhiteSpace(brand.OrganizationName) ? "CloneDeploy" : brand.OrganizationName;
        CompactBrandName.ToolTip = CompactBrandName.Text;
        CompactBrandName.Foreground = foreground;
        if (!consentLayout) CloseButton.Foreground = foreground;
        BrandDivider.Background = foreground;
        var logo = ((System.Windows.Media.DrawingImage)FindResource("CloneDeployLogo")).Clone();
        if (logo.Drawing is System.Windows.Media.DrawingGroup group && group.Children[0] is System.Windows.Media.GeometryDrawing shape) shape.Brush = foreground;
        logo.Freeze(); BrandLogo.Source = logo; ProductLogo.Source = logo;
        BrandIdentity.Visibility = brand.HasIdentity ? Visibility.Visible : Visibility.Collapsed;
        BrandName.Text = brand.OrganizationName;
        BrandTagline.Text = brand.Tagline;
        if (brand.LogoPng is { Length: > 0 and <= CloneDeploy_Agent.Contracts.AgentBranding.MaximumLogoBytes })
        {
            try
            {
                using var stream = new System.IO.MemoryStream(brand.LogoPng);
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 272; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); BrandLogo.Source = bitmap;
            }
            catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException) { /* Use the built-in logo for invalid artwork. */ }
        }

        {
            BrandPanel.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "PortalSurface");
            CompactBrandName.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "PortalText");
            CloseButton.SetResourceReference(ForegroundProperty, "PortalMuted");
            if (BrandLogo.Source is System.Windows.Media.DrawingImage drawing)
            {
                var themedLogo = drawing.Clone();
                if (themedLogo.Drawing is System.Windows.Media.DrawingGroup parts && parts.Children[0] is System.Windows.Media.GeometryDrawing themedShape)
                    themedShape.Brush = (System.Windows.Media.Brush)FindResource("PortalText");
                themedLogo.Freeze(); BrandLogo.Source = themedLogo;
            }
        }

    }
    private async void Acknowledge(object sender, RoutedEventArgs e)
    {
        if (AcknowledgeNotification is null) return;
        timer.Stop(); AcknowledgeButton.IsEnabled = false;
        try { await AcknowledgeNotification(); closeAuthorized = true; Close(); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException or TimeoutException or System.ComponentModel.Win32Exception)
        { MessageBox.Show(this, ActionFailureMessage, "CloneDeploy"); AcknowledgeButton.IsEnabled = true; timer.Start(); }
    }
    private async void Dismiss(object sender, RoutedEventArgs e)
    {
        if (dismissing) return;
        dismissing = true;
        if (DismissMessage is not null)
        {
            DismissButton.IsEnabled = false;
            try { await DismissMessage(); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException or TimeoutException or System.ComponentModel.Win32Exception)
            { MessageBox.Show(this, PortalText.Get("Connection.ContactAgentFailed"), "CloneDeploy"); DismissButton.IsEnabled = true; dismissing = false; return; }
        }
        closeAuthorized = true;
        Close();
    }
    private void ViewMessages(object sender, RoutedEventArgs e) { openMessages(); Close(); }
}
