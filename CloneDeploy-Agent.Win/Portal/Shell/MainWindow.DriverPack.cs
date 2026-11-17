using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.UI;

public partial class MainWindow
{
    private bool driverPackRunning, driverPackPolling, driverPackCommand;
    private int driverPackRevision;
    private readonly DispatcherTimer driverPackTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private void InitializeDriverPackTool()
    {
        DriverPackName.Text = PortalText.Format("Drivers.DefaultPackName", Environment.MachineName, DateTime.Now.ToString("yyyy-MM-dd"));
        driverPackTimer.Tick += async (_, _) =>
        {
            if (driverPackPolling || driverPackCommand || stop.IsCancellationRequested ||
                (!driverPackRunning && (adminStatus.Status != "SignedIn" || !IsVisible))) return;
            driverPackPolling = true;
            var revision = driverPackRevision;
            try
            {
                var status = await PortalClient.DriverPackAsync(PortalRequests.DriverPackStatus, stop.Token);
                if (revision == driverPackRevision) SetDriverPackStatus(status);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
            { if (revision == driverPackRevision) DriverPackStatusText.Text = PortalText.Get("Drivers.ReadProgressFailed"); }
            finally { driverPackPolling = false; }
        };
        driverPackTimer.Start(); Closed += (_, _) => driverPackTimer.Stop();
        UpdateDriverPackAccess();
    }
    private void UpdateDriverPackAccess()
    {
        CreateDriverPackButton.IsEnabled = adminStatus.Status == "SignedIn" && adminStatus.Capabilities.Contains("CreateDriverPack") && !driverPackRunning && !driverPackCommand;
        DriverPackName.IsEnabled = !driverPackRunning && !driverPackCommand;
    }
    private void SetDriverPackStatus(AgentDriverPackStatus status)
    {
        driverPackRunning = status.Status == "Running";
        DriverPackStatusText.Text = status.Message;
        DriverPackProgress.Visibility = driverPackRunning ? Visibility.Visible : Visibility.Collapsed;
        DriverPackProgress.IsIndeterminate = driverPackRunning && status.Progress < 30;
        DriverPackProgress.Value = status.Progress;
        CancelDriverPackButton.Visibility = driverPackRunning ? Visibility.Visible : Visibility.Collapsed;
        UpdateDriverPackAccess();
    }
    private async void CreateDriverPackClick(object sender, RoutedEventArgs e)
    {
        if (driverPackCommand || driverPackRunning || adminStatus.Status != "SignedIn") return;
        ++driverPackRevision;
        driverPackCommand = true; UpdateDriverPackAccess();
        try { SetDriverPackStatus(await PortalClient.DriverPackAsync(PortalRequests.StartDriverPack, stop.Token, new(DriverPackName.Text))); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
        { DriverPackStatusText.Text = PortalText.Get("Drivers.StartCollectionFailed"); }
        finally { driverPackCommand = false; UpdateDriverPackAccess(); }
    }
    private async void CancelDriverPackClick(object sender, RoutedEventArgs e)
    {
        ++driverPackRevision;
        try { SetDriverPackStatus(await PortalClient.DriverPackAsync(PortalRequests.CancelDriverPack, stop.Token)); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
        { DriverPackStatusText.Text = PortalText.Get("Drivers.CancelFailed"); }
    }
}
