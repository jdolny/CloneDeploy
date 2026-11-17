using System.Runtime.InteropServices;
using System.Text.Json;
using CloneDeploy_Imager.Api;
using CloneDeploy_Imager.Configuration;
using CloneDeploy_Imager.Diagnostics;
using CloneDeploy_Imager.Deployment;
using CloneDeploy_Imager.Discovery;
using CloneDeploy_Imager.UI;

namespace CloneDeploy_Imager;

/// <summary>
/// Coordinates startup, the interactive menu, and error reporting for an imaging session.
/// </summary>
internal sealed partial class ImagerApplication(ImagerSettings settings, ImagerConsole ui, DiagnosticLog log)
{
    private const int Win32ErrorCodeMask = 0xffff;
    private const int ErrorDiskFull = 112;
    private const int ErrorHandleDiskFull = 39;

    public async Task<int> RunAsync(LaunchOptions options, CancellationToken ct)
    {
        if (options.Inventory)
        {
            var inventory = await new HardwareDiscovery().ReadAsync(ct);
            Console.WriteLine(JsonSerializer.Serialize(inventory, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (Console.IsInputRedirected)
            throw new InvalidOperationException("Use an interactive console or --inventory.");
        ImagingClient? client = null;
        string? signedInAs = null;
        try
        {
            ui.Heading();
            var startupAddress = settings.ServerUrl ?? await ui.AskAsync("Server HTTPS address:", false, ct);
            if (!string.IsNullOrWhiteSpace(startupAddress))
            {
                try
                {
                    client = new ImagingClient(ImagerSettings.ServerAddress(startupAddress), settings.RequestTimeoutSeconds, log, settings.ServerCaCertificatePath);
                    var captured = await TryPreparedCapture(client, ct);
                    if (captured == "Exit") return 0;
                    var destination = captured ?? await CheckAssignedDeployment(client, ct);
                    if (destination == "Exit") return 0;
                    if (destination is "Sign in" or "Select image")
                    {
                        signedInAs = await SignIn(client, ct);
                        if (signedInAs is not null)
                        {
                            await LinkDevice(client, ct);
                            if (client.Device is not null && await DeployAfterLogin(client, ct) == "Exit") return 0;
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception exception) { log.Failure("Startup", exception); ui.Error(Describe(exception), log.FilePath); }
            }
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                ui.Heading();
                string? Missing(string permission) => signedInAs is null || client?.Can(permission) == true ? null : "Permission required: " + permission;
                var action = await ui.ChooseAvailableAsync("", [
                    new("Deploy Image", "Deploy Image", Missing("Imager.Deploy")),
                    new("Join Multicast", "Join Multicast", Missing("Imager.Deploy") ?? Missing("Imager.Override") ?? Missing("Image.View") ?? Missing("DeploymentProfile.View")),
                    new("Rename Device", "Rename Device", Missing("Device.Edit")),
                    new("Upload Image", "Upload Image", Missing("Image.Capture")),
                    new("Display Hardware Info", "Display Hardware Info")], ct);
                try
                {
                    if (action is "Deploy Image" or "Join Multicast" or "Upload Image" or "Rename Device" && signedInAs is null)
                    {
                        if (client is null)
                        {
                            var address = settings.ServerUrl ?? await ui.AskAsync("Server HTTPS address:", false, ct);
                            if (string.IsNullOrWhiteSpace(address)) continue;
                            client = new ImagingClient(ImagerSettings.ServerAddress(address), settings.RequestTimeoutSeconds, log, settings.ServerCaCertificatePath);
                        }
                        signedInAs = await SignIn(client, ct);
                        if (signedInAs is null) continue;
                        var automatic = await DeployAfterLogin(client, ct);
                        if (automatic == "Exit") return 0;
                        if (automatic is not null) continue;
                    }
                    if (client is not null && signedInAs is not null) await client.SessionAsync(ct);
                    var required = action switch { "Deploy Image" or "Join Multicast" => "Imager.Deploy", "Upload Image" => "Image.Capture", "Rename Device" => "Device.Edit", _ => null };
                    if (required is not null && !client!.Can(required)) throw new InvalidOperationException("Permission required: " + required);
                    switch (action)
                    {
                        case "Join Multicast":
                            if (await SelectMulticast(client!, ct) == "Exit") return 0;
                            continue;
                        case "Rename Device":
                            await RenameDevice(client!, ct);
                            continue;
                        case "Deploy Image":
                            if (await SelectDeployment(client!, ct) == "Exit") return 0;
                            continue;
                        case "Upload Image":
                            try
                            {
                                if (await Capture(client!, ct) == "Exit") return 0;
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                            {
                                log.Failure("Capture", ex);
                                if (await client!.TryUploadFailureLogAsync()) ui.Line("Diagnostic log uploaded to the device's Imaging logs.");
                                else ui.Line("Diagnostic log remains local. Sign in again with the same account to retry uploading it before rebooting.");
                                throw;
                            }
                            continue;
                        case "Display Hardware Info":
                            var inventory = await new HardwareDiscovery().ReadAsync(ct);
                            log.Write($"Discovery: {inventory.Disks.Count} disks, {inventory.WindowsVolumes.Count} Windows installations.");
                            await ui.ShowHardwareAsync(inventory, ct);
                            continue;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    if (exception is SessionExpiredException || client?.IsSignedIn == false) signedInAs = null;
                    log.Failure(action, exception);
                    // Deployments report diagnostics through /finish; menu errors stay local.
                    ui.Error(Describe(exception), log.FilePath);
                }
                await ui.WaitForBackAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Failure("Imager session", ex);
            throw;
        }
        finally { client?.Dispose(); }
    }

    public static string Describe(Exception exception) => exception switch
    {
        DeploymentClaimConflictException => exception.Message,
        SessionExpiredException => "Your session expired or access was revoked. Sign in again.",
        HttpRequestException => "Cannot complete the server request. Check the server address, network, certificate trust, and server status.",
        OperationCanceledException or TimeoutException => "The operation timed out. Check connectivity and try again.",
        COMException => "Hardware discovery failed. WinPE requires WinPE-WMI and WinPE-StorageWMI; run with administrative privileges.",
        UnauthorizedAccessException => "Access was denied. Check privileges and file permissions.",
        IOException ex when (ex.HResult & Win32ErrorCodeMask) is ErrorDiskFull or ErrorHandleDiskFull => "A disk used by Imager or DISM is full. Check the WinPE RAM disk, DISM temporary workspace, and image storage.",
        JsonException or InvalidDataException => "The server returned an invalid response.",
        ArgumentException => "The configuration or server address is invalid. An HTTPS application address is required.",
        InvalidOperationException => exception.Message,
        _ => "The operation failed. See the diagnostic log."
    };
}
