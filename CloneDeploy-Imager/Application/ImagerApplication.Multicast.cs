using CloneDeploy_Imager.Api;
using CloneDeploy_Imager.Discovery;
using CloneDeploy_Imager.UI;

namespace CloneDeploy_Imager;

internal sealed partial class ImagerApplication
{
    private async Task<string> SelectMulticast(ImagingClient client, CancellationToken ct)
    {
        if (client.Device is null) await LinkDevice(client, ct);
        if (client.Device is null) return "Main menu";
        var inventory = await new HardwareDiscovery().ReadAsync(ct);
        if (!inventory.IsWinPe) throw new InvalidOperationException("Boot WinPE to join multicast.");
        var sessions = await client.MulticastSessionsAsync(ct);
        if (sessions.Count == 0) { ui.Line("No manual multicast sessions are waiting. Create a session in Imaging Tasks."); return "Main menu"; }
        var selected = await ui.ChooseAvailableAsync("Join multicast session", [.. sessions.Select(x =>
            new MenuChoice(x.Id.ToString(), $"{x.Name} — {x.Image} ({x.SenderAddress}, waiting for {x.ExpectedReceivers})")), new("Cancel", "Cancel")], ct);
        if (selected == "Cancel") return "Main menu";
        if (await ui.ChooseAsync("Joining will erase the target disk and deploy the session's image.", ["Cancel", "Join and deploy"], ct) == "Cancel") return "Main menu";
        var expected = await client.JoinMulticastAsync(Guid.Parse(selected), ct);
        try
        {
            var task = await client.ClaimDeploymentAsync(inventory, ct);
            if (task is null || task.DeploymentId != expected || task.DeviceId != client.Device.Id)
                throw new InvalidOperationException("The multicast deployment could not be claimed for this device.");
            return await RunAssignedDeployment(client, ct, task);
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await client.ReleaseDeploymentAsync(timeout.Token); }
            catch (Exception ex) { log.Failure("Multicast deployment release", ex); }
        }
    }
}
