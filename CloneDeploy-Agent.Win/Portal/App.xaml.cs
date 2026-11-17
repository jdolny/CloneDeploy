using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using Forms = System.Windows.Forms;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.UI;

/// <summary>Keeps one portal in each user's Windows session, with tray access and explicit exit.</summary>
public partial class App : Application
{
    private EventWaitHandle? openRequested;
    private RegisteredWaitHandle? openListener;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private Forms.ContextMenuStrip? menu;
    private bool exiting;
    internal PortalNotifications Notifications { get; } = new();
    internal HelpDeskNotifications HelpNotifications { get; } = new();
    private NotificationPopup? helpNotification;
    private readonly CancellationTokenSource messageStop = new();
    private readonly Queue<AgentPortalNotification> pendingMessages = new();
    private readonly HashSet<string> queuedMessages = [];
    private NotificationPopup? notification;
    private MainWindow? portalWindow;
    private PortalUpdateRestart? updateRestart;

    protected override void OnStartup(StartupEventArgs e)
    {
        PortalText.Initialize();
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage(
                System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag)));
        base.OnStartup(e);
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        // The event carries only an activation signal, never privileged requests or identity.
        var name = $@"Local\CloneDeploy-Portal-{identity.User!.Value}-{process.SessionId}";
        openRequested = new EventWaitHandle(false, EventResetMode.AutoReset, name, out var firstInstance);
        if (!firstInstance)
        {
            if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) openRequested.Set();
            Shutdown();
            return;
        }

        updateRestart = new PortalUpdateRestart(() => { exiting = true; Shutdown(); });
        menu = new Forms.ContextMenuStrip();
        menu.Items.Add(PortalText.Get("Common.Open"), null, (_, _) => ShowPortal());
        using (var iconStream = GetResourceStream(new Uri("pack://application:,,,/CloneDeploy-Agent.UI;component/Assets/CloneDeploy.ico")).Stream)
        using (var sourceIcon = new System.Drawing.Icon(iconStream))
            trayIcon = (System.Drawing.Icon)sourceIcon.Clone();
        tray = new Forms.NotifyIcon
        {
            Text = "CloneDeploy", Icon = trayIcon,
            ContextMenuStrip = menu, Visible = true
        };
        tray.DoubleClick += (_, _) => ShowPortal();
        openListener = ThreadPool.RegisterWaitForSingleObject(openRequested,
            (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (!exiting) ShowPortal(); })),
            null, Timeout.Infinite, false);
        // Logon startup creates only the tray. Build the window when the user opens it.
        _ = RemoteConsentNotifications.RunAsync(() => Notifications.Branding, messageStop.Token);
        HelpNotifications.Changed += UpdateHelpNotification;
        Notifications.FeaturesChanged += features => HelpNotifications.SetEnabled(features.HelpDeskEnabled);
        Notifications.BrandingChanged += branding =>
        {
            helpNotification?.UpdateBranding(branding);
            UpdateHelpNotification();
            notification?.UpdateBranding(branding);
        };
        _ = HelpNotifications.RunAsync(ShowHelpNotification, messageStop.Token);
        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) ShowPortal();
        _ = Notifications.RunAsync(message =>
        {
            if (exiting || pendingMessages.Count >= 50 || !queuedMessages.Add(message.Id)) return;
            pendingMessages.Enqueue(message);
            ShowNextMessage();
        }, messageStop.Token);
        _ = LoginPolicyClient.RunAsync(message =>
        {
            if (exiting) throw new OperationCanceledException();
            message = Notifications.StoreLocal(message);
            if (pendingMessages.Count >= 50) return;
            queuedMessages.Add(message.Id);
            pendingMessages.Enqueue(message);
            ShowNextMessage();
        }, messageStop.Token);
    }

    internal void QueuePolicyNotification(AgentPortalNotification message)
    {
        if (exiting) throw new OperationCanceledException();
        message = Notifications.StoreLocal(message);
        if (pendingMessages.Count >= 50 || !queuedMessages.Add(message.Id)) return;
        pendingMessages.Enqueue(message);
        ShowNextMessage();
    }

    private void ShowPortal()
    {
        var reopening = portalWindow is { IsLoaded: true } &&
            (!portalWindow.IsVisible || portalWindow.WindowState == WindowState.Minimized);
        if (portalWindow is null)
        {
            portalWindow = new MainWindow();
            MainWindow = portalWindow;
            portalWindow.Closing += (_, e) =>
            {
                if (exiting) return;
                e.Cancel = true;
                portalWindow.Hide();
            };
        }
        portalWindow.Show();
        if (portalWindow.WindowState == WindowState.Minimized) portalWindow.WindowState = WindowState.Normal;
        portalWindow.FitToScreen();
        portalWindow.Activate();
        if (reopening) portalWindow.RefreshOnOpen();
    }

    private void OpenNotifications() { ShowPortal(); portalWindow!.ShowNotifications(); }
    private void OpenHelp() { ShowPortal(); portalWindow!.ShowHelp(); }
    private void ShowHelpNotification(int unread)
    {
        if (exiting) return;
        if (helpNotification is not null) { UpdateHelpNotification(); return; }
        // Deliberately bypass PortalMessages: ticket data and notification state stay on the server.
        helpNotification = new NotificationPopup(new AgentPortalNotification(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow,
            PortalText.Format("HelpDesk.NamedUpdate", Notifications.Branding.HelpTabText), PortalText.Format("HelpDesk.UpdatesCount", unread, Notifications.Branding.HelpTabText), 15), OpenHelp)
            { Branding = Notifications.Branding, AutoClose = false, IsHelpNotification = true };
        var popup = helpNotification;
        popup.Closed += (_, _) => { if (ReferenceEquals(helpNotification, popup)) helpNotification = null; };
        helpNotification.ShowOpenAction(PortalText.Format("HelpDesk.OpenNamed", Notifications.Branding.HelpTabText));
        helpNotification.Show();
    }
    private void UpdateHelpNotification()
    {
        if (helpNotification is null) return;
        if (HelpNotifications.UnreadCount == 0) { helpNotification.CloseFromService(); return; }
        helpNotification.DataContext = new AgentPortalNotification(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow,
            PortalText.Format("HelpDesk.NamedUpdate", Notifications.Branding.HelpTabText), PortalText.Format("HelpDesk.UpdatesCount", HelpNotifications.UnreadCount, Notifications.Branding.HelpTabText), 15);
        helpNotification.ShowOpenAction(PortalText.Format("HelpDesk.OpenNamed", Notifications.Branding.HelpTabText));
    }
    private void ShowNextMessage()
    {
        if (exiting || notification is not null || !pendingMessages.TryDequeue(out var message)) return;
        message = Notifications.History.FirstOrDefault(x => x.Id == message.Id) ?? message;
        if (message.IsExpired || (message.RequireAcknowledgment && !message.PendingAcknowledgment))
        {
            queuedMessages.Remove(message.Id);
            ShowNextMessage();
            return;
        }
        notification = new NotificationPopup(message, OpenNotifications) { Branding = Notifications.Branding, AcknowledgeNotification = () => Notifications.AcknowledgeAsync(message.Id) };
        notification.Closed += (_, _) =>
        {
            notification = null;
            queuedMessages.Remove(message.Id);
            ShowNextMessage();
        };
        notification.Show();
        Notifications.Presented(message.Id);
    }

    internal void CloseFromAdminTools()
    {
        exiting = true;
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        exiting = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        exiting = true;
        messageStop.Cancel();
        updateRestart?.Dispose();
        openListener?.Unregister(null);
        openRequested?.Dispose();
        tray?.Dispose();
        trayIcon?.Dispose();
        menu?.Dispose();
        base.OnExit(e);
    }
}
