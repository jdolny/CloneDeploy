using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CloneDeploy_Agent.Contracts;
using Microsoft.Win32;

namespace CloneDeploy_Agent.UI;

public partial class MainWindow
{
    private bool adminBusy;
    private bool adminPolling;
    private int adminRevision;
    private AgentAdminStatus adminStatus = new("SignedOut");
    private string? selectedAdminTool;
    private void OpenAdminToolClick(object sender, RoutedEventArgs e)
    {
        if (adminStatus.Status != "SignedIn" || sender is not System.Windows.Controls.Button { Tag: string tool, IsEnabled: true }) return;
        ShowAdminTool(tool);
    }
    private void AdminToolBackClick(object sender, RoutedEventArgs e) => ShowAdminTool(null);
    private void ShowAdminTool(string? tool)
    {
        selectedAdminTool = tool;
        AdminToolMenu.Visibility = tool is null ? Visibility.Visible : Visibility.Collapsed;
        AdminToolBackButton.Visibility = tool is null ? Visibility.Collapsed : Visibility.Visible;
        DriverPackToolPanel.Visibility = tool == "drivers" ? Visibility.Visible : Visibility.Collapsed;
        ImagePrepToolPanel.Visibility = tool == "imageprep" ? Visibility.Visible : Visibility.Collapsed;
        UsbToolPanel.Visibility = tool == "usb" ? Visibility.Visible : Visibility.Collapsed;
        PoliciesPanel.Visibility = tool == "assigned" ? Visibility.Visible : Visibility.Collapsed;
        AdminPanel.ScrollToTop();
    }
    private readonly DispatcherTimer adminTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private void InitializeAdminSession()
    {
        adminTimer.Tick += async (_, _) =>
        {
            if (adminBusy || adminPolling || stop.IsCancellationRequested ||
                (adminStatus.Status != "Pending" && adminStatus.Status != "SignedIn" && adminStatus.Status != "Unavailable" && !IsVisible)) return;
            adminPolling = true;
            var revision = adminRevision;
            try
            {
                var status = await PortalClient.AdminAsync(PortalRequests.AdminStatus, stop.Token);
                if (revision == adminRevision) SetAdminStatus(status);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
            { if (revision == adminRevision) SetAdminStatus(new("Unavailable", Message: PortalText.Get("Admin.VerifyAccessFailed"))); }
            finally { adminPolling = false; adminTimer.Interval = TimeSpan.FromSeconds(adminStatus.Status == "Pending" ? 2 : 15); }
        };
        adminTimer.Start();
        InitializeDriverPackTool();
        InitializeUsbTool();
        InitializeImagePrepTool();
        SystemEvents.SessionSwitch += AdminSessionSwitch;
        Closed += (_, _) => { adminTimer.Stop(); SystemEvents.SessionSwitch -= AdminSessionSwitch; };
    }
    private void SetAdminStatus(AgentAdminStatus status)
    {
        AdminSignedInIndicator.Visibility = status.Status == "SignedIn" ? Visibility.Visible : Visibility.Collapsed;
        AdminToolsButton.ToolTip = status.Status == "SignedIn" ? PortalText.Format("Admin.SignedInAs", status.UserName) : PortalText.Get("Common.AdministratorTools");
        System.Windows.Automation.AutomationProperties.SetName(AdminToolsButton, (string)AdminToolsButton.ToolTip);
        adminStatus = status;
        AssignedPoliciesToolCard.IsEnabled = status.Status == "SignedIn";
        CloseUiToolCard.IsEnabled = status.Status == "SignedIn";
        DriverPackToolCard.IsEnabled = status.Status == "SignedIn" && status.Capabilities.Contains("CreateDriverPack");
        ImagePrepToolCard.IsEnabled = status.Status == "SignedIn" && status.Capabilities.Contains("RunImagePrep");
        UsbToolCard.IsEnabled = status.Status == "SignedIn" && status.Capabilities.Contains("CreateBootableUsb");
        // Keep job progress and cancellation visible after interactive access expires.
        if (status.Status != "SignedIn" && selectedAdminTool is not ("drivers" or "imageprep" or "usb"))
            ShowAdminTool(null);
        AdminIdentityText.Text = status.Status == "SignedIn" ? PortalText.Format("SignIn.SignedInAs", status.UserName) : PortalText.Get("Admin.SignInWithCloneDeploy");
        AdminStatusText.Text = status.Message ?? (status.Status == "SignedIn" ? PortalText.Format("Admin.SessionExpiresAt", status.ExpiresAt?.LocalDateTime) :
            PortalText.Get("SignIn.BrowserVerificationHint"));
        AdminToolsPanel.Visibility = status.Status == "SignedIn" || selectedAdminTool is "drivers" or "imageprep" or "usb"
            ? Visibility.Visible : Visibility.Collapsed;
        AdminSignOutButton.Visibility = status.Status is "SignedIn" or "Pending" or "Unavailable" ? Visibility.Visible : Visibility.Collapsed;
        AdminSignOutButton.Content = status.Status == "Pending" ? PortalText.Get("Common.CancelSignIn") : PortalText.Get("SignIn.SignOut");
        AdminSignInButton.Visibility = status.Status is "SignedIn" or "Pending" ? Visibility.Collapsed : Visibility.Visible;
        UpdateDriverPackAccess();
        UpdateUsbAccess();
        UpdateImagePrepAccess();
    }
    private async void CloseUiClick(object sender, RoutedEventArgs e)
    {
        if (adminBusy || adminStatus.Status != "SignedIn") return;
        var revision = ++adminRevision;
        adminBusy = true;
        CloseUiToolCard.IsEnabled = false;
        try
        {
            // Revalidate the session before closing; the displayed status may be from an earlier poll.
            var status = await PortalClient.AdminAsync(PortalRequests.AdminStatus, stop.Token);
            if (revision != adminRevision) return;
            SetAdminStatus(status);
            if (status.Status != "SignedIn") return;
            await PortalClient.AdminAsync(PortalRequests.AdminSignOut, stop.Token);
            if (revision != adminRevision) return;
            SetAdminStatus(new("SignedOut"));
            (Application.Current as App)?.CloseFromAdminTools();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
        {
            if (revision == adminRevision)
                SetAdminStatus(new("Unavailable", Message: PortalText.Get("Admin.VerifyEndSessionFailed")));
        }
        finally
        {
            if (revision == adminRevision)
            {
                adminBusy = false;
                CloseUiToolCard.IsEnabled = adminStatus.Status == "SignedIn";
            }
        }
    }
    private async void AdminSignInClick(object sender, RoutedEventArgs e)
    {
        if (adminBusy) return;
        var revision = ++adminRevision;
        adminBusy = true; AdminSignInButton.IsEnabled = false;
        try
        {
            var status = await PortalClient.AdminAsync(PortalRequests.AdminSignIn, stop.Token);
            if (revision != adminRevision) return;
            SetAdminStatus(status);
            if (status.Status == "Pending" && Uri.TryCreate(status.SignInUrl, UriKind.Absolute, out var url) && url.Scheme == "https")
            {
                Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
                adminTimer.Interval = TimeSpan.FromSeconds(2);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
        {
            if (revision != adminRevision) return;
            try { await PortalClient.AdminAsync(PortalRequests.AdminSignOut, stop.Token); } catch (Exception) { }
            SetAdminStatus(new("SignedOut", Message: PortalText.Get("Admin.StartBrowserSignInFailed")));
        }
        finally { if (revision == adminRevision) { adminBusy = false; AdminSignInButton.IsEnabled = true; } }
    }
    private async void AdminSignOutClick(object sender, RoutedEventArgs e)
    {
        var revision = ++adminRevision;
        adminBusy = true;
        SetAdminStatus(new("SignedOut"));
        try { await PortalClient.AdminAsync(PortalRequests.AdminSignOut, stop.Token); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
        { if (revision == adminRevision) AdminStatusText.Text = PortalText.Get("Admin.EndSessionAgentUnavailable"); }
        finally { if (revision == adminRevision) { adminBusy = false; AdminSignInButton.IsEnabled = true; } }
    }
    private void AdminSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect)
            Dispatcher.BeginInvoke(new Action(() => AdminSignOutClick(this, new RoutedEventArgs())));
    }
}
