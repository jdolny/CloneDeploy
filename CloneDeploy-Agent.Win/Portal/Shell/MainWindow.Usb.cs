using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.UI;

public partial class MainWindow
{
    private readonly DispatcherTimer usbTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool usbCommand, usbPolling, usbCatalogLoaded, usbDrivesLoaded;
    private int usbRevision;
    private AgentUsbStatus usbStatus = new("Idle", PortalText.Get("Usb.SelectBootEnvironment"));
    private void InitializeUsbTool()
    {
        UsbArchitecture.ItemsSource = new[] { "x64", "arm64" };
        UsbArchitecture.SelectedItem = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        usbTimer.Tick += async (_, _) =>
        {
            if (usbCommand || usbPolling || stop.IsCancellationRequested || !IsVisible) return;
            if (adminStatus.Status != "SignedIn" && usbStatus.Status is not ("Running" or "Ready")) return;
            usbPolling = true;
            var revision = usbRevision;
            try
            {
                var status = await PortalClient.UsbAsync(new("Status"), stop.Token);
                if (revision != usbRevision) return;
                if (status != usbStatus) SetUsbStatus(status);
                if (adminStatus.Status == "SignedIn")
                {
                    if (status.Status == "Ready" && !usbDrivesLoaded) await UsbCommandAsync(new("Drives"));
                    else if (!usbCatalogLoaded && status.Status is not ("Running" or "Ready")) await UsbCommandAsync(new("Catalog"));
                }
            }
            catch (Exception ex) when (UsbConnectionError(ex)) { UsbStatusText.Text = PortalText.Get("Usb.ReadProgressFailed"); }
            finally { usbPolling = false; }
        };
        usbTimer.Start(); Closed += (_, _) => usbTimer.Stop();
        UpdateUsbAccess();
    }
    private static bool UsbConnectionError(Exception ex) => ex is IOException or OperationCanceledException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception;
    private void UpdateUsbAccess()
    {
        if (UsbDownload is null) return;
        var allowed = adminStatus.Status == "SignedIn" && adminStatus.Capabilities.Contains("CreateBootableUsb") && !usbCommand;
        var active = usbStatus.Status is "Running" or "Ready";
        UsbArchitecture.IsEnabled = allowed && !active;
        UsbRefreshVariants.IsEnabled = allowed && !active;
        UsbVariants.IsEnabled = allowed && !active;
        UsbDownload.IsEnabled = allowed && !active && UsbVariants.SelectedItem is AgentUsbVariant;
        UsbDownload.ToolTip = adminStatus.Status != "SignedIn" ? PortalText.Get("Admin.ConnectBeforeSignIn")
            : !adminStatus.Capabilities.Contains("CreateBootableUsb") ? PortalText.Get("Usb.AdministratorPermissionRequired")
            : usbCommand ? PortalText.Get("Usb.WaitingForAgent")
            : active ? PortalText.Get("Usb.OperationInProgress")
            : UsbVariants.SelectedItem is not AgentUsbVariant ? PortalText.Get("Usb.SelectReadyBuildFirst")
            : PortalText.Get("Usb.DownloadAndVerifyDescription");
        UsbDrivePanel.Visibility = usbStatus.Status == "Ready" ? Visibility.Visible : Visibility.Collapsed;
        UsbRefreshDrives.IsEnabled = allowed && usbStatus.Status == "Ready";
        UsbDrives.IsEnabled = allowed && usbStatus.Status == "Ready";
        UsbWrite.IsEnabled = allowed && usbStatus.Status == "Ready" && UsbDrives.SelectedItem is AgentUsbDrive;
        UsbCancel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        UsbCancel.IsEnabled = !usbCommand;
        if (adminStatus.Status != "SignedIn") { usbCatalogLoaded = false; usbDrivesLoaded = false; }
    }
    private void SetUsbStatus(AgentUsbStatus status)
    {
        if (status.Status is "Catalog" or "Error" or "Busy")
        {
            UsbStatusText.Text = status.Message;
        }
        else
        {
            if (usbStatus.Status is "Running" or "Ready" && status.Status is "Succeeded" or "Failed" or "Cancelled") usbCatalogLoaded = false;
            usbStatus = status;
            if (status.Status is "Running" or "Ready") UsbArchitecture.SelectedItem = status.Architecture;
            UsbStatusText.Text = status.Message;
            UsbProgress.Visibility = status.Status == "Running" ? Visibility.Visible : Visibility.Collapsed;
            UsbProgress.IsIndeterminate = status.Status == "Running" && status.Progress >= 60;
            UsbProgress.Value = status.Progress;
            if (status.Status != "Ready") { usbDrivesLoaded = false; UsbDrives.ItemsSource = null; }
        }
        if (status.Variants is { } variants)
        {
            if (status.Status == "Catalog") usbCatalogLoaded = true;
            UsbVariants.ItemsSource = variants;
            if (variants.Length > 0) UsbVariants.SelectedIndex = 0;
        }
        if (status.Drives is { } drives)
        {
            usbDrivesLoaded = true; UsbDrives.ItemsSource = drives;
            UsbDrives.SelectedIndex = -1; // Never preselect a destructive target.
        }
        UpdateUsbAccess();
    }
    private async Task UsbCommandAsync(AgentUsbRequest request)
    {
        if (usbCommand) return;
        if (request.Operation == "Catalog") request = request with { Architecture = UsbArchitecture.SelectedItem as string ?? "x64" };
        ++usbRevision; usbCommand = true; UpdateUsbAccess();
        try { SetUsbStatus(await PortalClient.UsbAsync(request, stop.Token)); }
        catch (Exception ex) when (UsbConnectionError(ex)) { UsbStatusText.Text = PortalText.Get("Usb.RequestFailed"); }
        finally { usbCommand = false; UpdateUsbAccess(); }
    }
    private async void UsbArchitectureChanged(object sender, SelectionChangedEventArgs e)
    {
        if (usbCommand || usbStatus.Status is "Running" or "Ready" || UsbVariants is null) return;
        ++usbRevision; usbCatalogLoaded = false; UsbVariants.ItemsSource = null;
        if (adminStatus.Status == "SignedIn") await UsbCommandAsync(new("Catalog"));
    }
    private void UsbSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateUsbAccess();
    private async void UsbRefreshVariantsClick(object sender, RoutedEventArgs e) => await UsbCommandAsync(new("Catalog"));
    private async void UsbRefreshDrivesClick(object sender, RoutedEventArgs e) => await UsbCommandAsync(new("Drives"));
    private async void UsbCancelClick(object sender, RoutedEventArgs e) => await UsbCommandAsync(new("Cancel"));
    private async void UsbDownloadClick(object sender, RoutedEventArgs e)
    {
        if (UsbVariants.SelectedItem is AgentUsbVariant variant) await UsbCommandAsync(new("Download", variant.BuildId) { Architecture = variant.Architecture });
    }
    private async void UsbWriteClick(object sender, RoutedEventArgs e)
    {
        if (usbCommand || usbStatus.Status != "Ready" || UsbDrives.SelectedItem is not AgentUsbDrive drive) return;
        if (MessageBox.Show(this, PortalText.Format("Usb.ConfirmEraseDisk", drive.DisplayName, usbStatus.Architecture),
                PortalText.Get("Usb.EraseExternalDisk"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await UsbCommandAsync(new("Write", Drive: drive, ConfirmErase: true));
    }
}
