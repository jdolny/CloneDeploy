using CloneDeploy_Imager.Api;
using CloneDeploy_Imager.Deployment;
using CloneDeploy_Imager.Discovery;
using CloneDeploy_Imager.UI;

namespace CloneDeploy_Imager;

internal sealed partial class ImagerApplication
{
    // Only called after a successful operator login. Existing jobs always get first refusal.
    private async Task<string?> DeployAfterLogin(ImagingClient client, CancellationToken ct)
    {
        var inventory = await new HardwareDiscovery().ReadAsync(ct);
        if (!inventory.IsWinPe) return null;
        try
        {
            var assigned = await client.ClaimDeploymentAsync(inventory, ct);
            if (assigned is not null)
            {
                ui.DeviceName = assigned.DeviceName;
                return await RunAssignedDeployment(client, ct, assigned);
            }
            if (!client.Can("Imager.Deploy") || !client.Can("Imager.Override") || !client.Can("Image.View") || !client.Can("DeploymentProfile.View")) return null;
            var matches = await client.AutomaticMatchesAsync(inventory.Manufacturer, inventory.Model, ct);
            if (matches.Count == 0) return null;
            AutomaticDeploymentMatch match;
            if (matches.Count == 1) match = matches[0];
            else
            {
                var choice = await ui.ChooseAvailableAsync("Multiple deployment rules match. Select a deployment", [.. matches.Select(x =>
                    new MenuChoice(x.Id.ToString(), $"{x.Name}: {x.ImageName} / {x.ProfileName}", x.UnavailableReason)), new("Cancel", "Cancel")], ct);
                if (choice == "Cancel") return "Main menu";
                match = matches.Single(x => x.Id.ToString() == choice);
            }
            if (match.UnavailableReason is not null)
            {
                ui.Line($"Deployment rule {match.Name} is unavailable: {match.UnavailableReason}");
                await ui.WaitForBackAsync(ct);
                return "Main menu";
            }
            if (client.Device is null) await LinkDevice(client, ct);
            if (client.Device is null) return "Main menu";
            var disks = DeploymentDisks.Read(ct);
            if (disks.Length == 0) throw new InvalidOperationException("No eligible internal disk was found for deployment.");
            var disk = disks[0];
            if (disks.Length > 1)
            {
                var choice = await ui.ChooseAvailableAsync("Select the disk to erase", [.. disks.Select(x =>
                    new MenuChoice(x.Number.ToString(), $"Disk {x.Number}: {x.Name}, {x.Size / 1073741824d:0.0} GiB ({x.UniqueId})")), new("Cancel", "Cancel")], ct);
                if (choice == "Cancel") return "Main menu";
                disk = disks.Single(x => x.Number.ToString() == choice);
            }
            var chooseDrivers = client.Can("Imager.Override") && client.Can("DriverPack.View") && client.Can("DeploymentProfile.View");
            var drivers = chooseDrivers ? await client.SelectDriversAsync(match.ProfileId, inventory.Manufacturer, inventory.Model, ct) : new ManualDriverSelection(null, [], null);
            var driverId = drivers.SelectedId;
            if (chooseDrivers && driverId is null)
            {
                ui.Line(drivers.Warning ?? "Choose a driver pack.");
                var choice = await ui.ChooseAvailableAsync("Select driver pack", [.. drivers.Options.Select(x => new MenuChoice(x.Id.ToString(), x.Name)),
                    new("0", "Continue without additional drivers"), new("Cancel", "Cancel")], ct);
                if (choice == "Cancel") return "Main menu";
                driverId = int.Parse(choice);
            }
            if (!await ui.AutomaticDeploymentCountdownAsync(match.Name, match.ImageName, match.ProfileName,
                    $"Disk {disk.Number}: {disk.Name}, {disk.Size / 1073741824d:0.0} GiB ({disk.UniqueId})", ct)) return "Main menu";
            // Recheck rule changes during review before preparing destructive work.
            var current = await client.AutomaticMatchesAsync(inventory.Manufacturer, inventory.Model, ct);
            if (!current.Any(x => x == match) || (matches.Count == 1 && current.Count != 1))
                throw new InvalidOperationException("Deployment rules changed during review. Use manual deployment or sign in again.");
            var expected = await client.PrepareDeploymentAsync(match.ImageId, match.ProfileId, driverId, ct);
            var task = await client.ClaimDeploymentAsync(inventory, ct);
            if (task is null || task.DeploymentId != expected || task.DeviceId != client.Device.Id)
                throw new InvalidOperationException("The prepared deployment could not be claimed. Check the device's assignment in the web app.");
            return await RunAssignedDeployment(client, ct, task, disk);
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await client.ReleaseDeploymentAsync(timeout.Token); }
            catch (Exception ex) { log.Failure("Deployment release (lease will expire)", ex); }
        }
    }
}
