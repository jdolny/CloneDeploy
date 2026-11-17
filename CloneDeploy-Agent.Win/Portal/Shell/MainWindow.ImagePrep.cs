using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.UI;

public partial class MainWindow
{
    private bool imagePrepBusy, imagePrepPolling, imagePrepRunning, imagePrepFinalizing;
    private AgentCaptureImage[] imagePrepImages = [];
    private readonly DispatcherTimer imagePrepTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private void InitializeImagePrepTool()
    {
        imagePrepTimer.Tick += async (_, _) =>
        {
            if (imagePrepBusy || imagePrepPolling || imagePrepFinalizing || stop.IsCancellationRequested
                || (!imagePrepRunning && (adminStatus.Status != "SignedIn" || !IsVisible)))
                return;

            imagePrepPolling = true;
            try
            {
                SetImagePrepStatus(await PortalClient.ImagePrepAsync(new(AgentImagePrepOperation.Status), stop.Token));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException
                or JsonException or System.ComponentModel.Win32Exception)
            {
                ImagePrepStatusText.Text = PortalText.Get("Imaging.ReadProgressFailed");
            }
            finally
            {
                imagePrepPolling = false;
            }
        };
        imagePrepTimer.Start();
        Closed += (_, _) => imagePrepTimer.Stop();
        UpdateImagePrepAccess();
    }

    private void UpdateImagePrepAccess()
    {
        var enabled = adminStatus.Status == "SignedIn" && adminStatus.Capabilities.Contains("RunImagePrep")
            && !imagePrepBusy && !imagePrepRunning && !imagePrepFinalizing;
        ImagePrepRefresh.IsEnabled = enabled;
        ImagePrepProfiles.IsEnabled = enabled;
        ImagePrepStart.IsEnabled = enabled && ImagePrepProfiles.SelectedItem is AgentImagePrepProfile;
        ImagePrepStart.Content = ImagePrepProfiles.SelectedItem is AgentImagePrepProfile { RebootAndCapture: true }
            ? PortalText.Get("Imaging.PrepareAndCapture") : PortalText.Get("Common.PrepareAndShutDown");
    }

    private void ImagePrepSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateImagePrepAccess();

    private void SetImagePrepStatus(AgentImagePrepStatus status)
    {
        imagePrepRunning = status.Status == "Running";
        imagePrepFinalizing = status.Status == "Finalizing";
        ImagePrepStatusText.Text = status.Message;
        ImagePrepProgress.Visibility = imagePrepRunning ? Visibility.Visible : Visibility.Collapsed;
        ImagePrepProgress.Value = status.Progress;
        UpdateImagePrepAccess();
    }

    private async void ImagePrepRefreshClick(object sender, RoutedEventArgs e)
    {
        if (imagePrepBusy || imagePrepRunning || imagePrepFinalizing)
            return;

        imagePrepBusy = true;
        UpdateImagePrepAccess();
        try
        {
            var result = await PortalClient.ImagePrepAsync(new(AgentImagePrepOperation.Profiles), stop.Token);
            ImagePrepProfiles.ItemsSource = result.Profiles;
            imagePrepImages = result.Images;
            SetImagePrepStatus(result);
            if (result.Profiles.Length == 0 && result.Status == "Idle")
                ImagePrepStatusText.Text = PortalText.Get("Imaging.NoEnabledProfiles");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException
            or JsonException or System.ComponentModel.Win32Exception)
        {
            ImagePrepStatusText.Text = PortalText.Get("Imaging.LoadProfilesFailed");
        }
        finally
        {
            imagePrepBusy = false;
            UpdateImagePrepAccess();
        }
    }

    private async void ImagePrepStartClick(object sender, RoutedEventArgs e)
    {
        if (imagePrepBusy || imagePrepRunning || imagePrepFinalizing
            || ImagePrepProfiles.SelectedItem is not AgentImagePrepProfile profile)
            return;

        ImagePrepCaptureDialog? destination = null;
        if (profile.RebootAndCapture)
        {
            destination = new(this, imagePrepImages) { CanCreateImage = adminStatus.Capabilities.Contains("CreateCaptureImage") };
            if (destination.ShowDialog() != true) return;
        }
        var ending = profile.RebootAndCapture
            ? PortalText.Get("Imaging.RestartAndCaptureDescription")
            : PortalText.Get("Imaging.ShutdownAfterPreparationDescription");
        if (profile.UninstallAgent) ending = PortalText.Get("Imaging.AgentUninstallWarning") + ending;
        if (MessageBox.Show(this,
            PortalText.Format("Imaging.ConfirmRunPreparation", profile.Name, ending),
            PortalText.Get("Imaging.PrepareComputerForImageCapture"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var startRequested = false;
        imagePrepBusy = true;
        UpdateImagePrepAccess();
        try
        {
            // A separate elevated viewer survives portal disconnection and MSI removal.
            using (var resource = typeof(MainWindow).Assembly.GetManifestResourceStream("CloneDeploy.ImagePrepProgress.ps1")
                ?? throw new IOException("Image prep progress viewer is missing."))
            using (var reader = new StreamReader(resource))
            {
                var script = await reader.ReadToEndAsync();
                var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
                var viewer = new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
                {
                    UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                    Arguments = "-NoLogo -NoProfile -STA -EncodedCommand " + encoded
                };
                using var launched = System.Diagnostics.Process.Start(viewer)
                    ?? throw new IOException("Could not open image prep progress.");
            }
            startRequested = true;
            SetImagePrepStatus(await PortalClient.ImagePrepAsync(new(AgentImagePrepOperation.Start, profile.Id)
            { ImageId = destination?.ImageId, NewImageName = destination?.NewImageName, Format = destination?.Format }, stop.Token));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException
            or JsonException or System.ComponentModel.Win32Exception)
        {
            ImagePrepStatusText.Text = startRequested ? PortalText.Get("Imaging.PreparationStartUnconfirmed")
                : "Image prep was not started. The progress viewer requires Windows administrator approval.";
        }
        finally
        {
            imagePrepBusy = false;
            UpdateImagePrepAccess();
        }
    }
}
