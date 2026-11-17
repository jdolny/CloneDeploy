using System.Runtime.InteropServices;
using System.Text.Json;
using CloneDeploy_Imager.Api;
using CloneDeploy_Imager.Configuration;
using CloneDeploy_Imager.Diagnostics;
using CloneDeploy_Imager.Deployment;
using CloneDeploy_Imager.Discovery;
using CloneDeploy_Imager.Imaging;
using CloneDeploy_Imager.UI;

namespace CloneDeploy_Imager;

/// <summary>
/// Handles operator sign-in, MFA, and linking or registering the current device.
/// </summary>
internal sealed partial class ImagerApplication
{
    private async Task<string?> SignIn(ImagingClient client, CancellationToken ct)
    {
        var userName = "";
        string? error = null;
        while (true)
        {
            var credentials = await ui.AskCredentialsAsync(userName, error, client, ct);
            if (credentials is null) return null;
            userName = credentials.UserName;
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(credentials.Password))
            {
                error = "Enter both a username and password.";
                continue;
            }
            try
            {
                var result = await client.LoginAsync(userName, credentials.Password, null, ct);
                if (result == LoginStatus.MfaRequired)
                {
                    var code = await ui.AskLoginAsync("Authenticator code", true, userName, ct);
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        error = null;
                        continue;
                    }
                    result = await client.LoginAsync(userName, credentials.Password, code, ct);
                }
                if (result == LoginStatus.Success)
                {
                    var session = await client.SessionAsync(ct);
                    if (await client.RetryPendingFailureLogAsync())
                        ui.Line("Previous capture diagnostic log uploaded to the device's Imaging logs.");
                    return session.Name ?? session.Subject ?? userName;
                }
                error = result switch
                {
                    LoginStatus.MfaEnrollmentRequired => "Enroll an authenticator in the web application before signing in here.",
                    LoginStatus.Forbidden => "This account does not have imaging access.",
                    LoginStatus.RateLimited => "Too many login attempts. Wait before trying again.",
                    LoginStatus.MfaRequired => "An authenticator code is required.",
                    _ => "Sign-in was rejected. Check your credentials, authenticator code, and account status."
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                log.Failure("Sign in", exception);
                error = Describe(exception);
            }
        }
    }

    private async Task RenameDevice(ImagingClient client, CancellationToken ct)
    {
        await LinkDevice(client, ct);
        if (client.Device is null) return;
        while (true)
        {
            var name = await ui.AskAsync($"New name for {client.Device.Name} (blank to cancel):", false, ct);
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                await client.RenameDeviceAsync(name, ct);
                ui.DeviceName = client.Device.Name;
                ui.Line("Device renamed.");
                await ui.WaitForBackAsync(ct);
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
            { ui.Error(ex.Message, log.FilePath); }
        }
    }

    private async Task LinkDevice(ImagingClient client, CancellationToken ct)
    {
        var inventory = await new HardwareDiscovery().ReadAsync(ct);
        if (!inventory.IsWinPe) throw new InvalidOperationException("Device linking is available when running in WinPE.");
        var device = await client.LinkDeviceAsync(inventory, null, ct);
        while (device is null)
        {
            if (!client.Can("Device.Add")) throw new InvalidOperationException("Device not found. Add device permission is required to register it.");
            var name = await ui.AskAsync("Device not found. Enter a name to register it as unmanaged:", false, ct);
            if (string.IsNullOrWhiteSpace(name)) return;
            try { device = await client.LinkDeviceAsync(inventory, name, ct); }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
            { ui.Error(ex.Message, log.FilePath); }
        }
        ui.DeviceName = device.Name;
    }
}
