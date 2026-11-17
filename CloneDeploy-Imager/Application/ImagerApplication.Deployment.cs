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
/// Handles assigned and manually selected deployments, target-disk selection, and claim release.
/// </summary>
internal sealed partial class ImagerApplication
{
    private async Task<string> CheckAssignedDeployment(ImagingClient client, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                try
                {
                    ui.Line("Checking for an assigned imaging deployment...");
                    var inventory = await new HardwareDiscovery().ReadAsync(ct);
                    var task = await client.ClaimDeploymentAsync(inventory, ct);
                    if (task is null)
                    {
                        ui.Line("No assigned deployment was found.");
                        return "Select image";
                    }
                    ui.DeviceName = task.DeviceName;
                    return await RunAssignedDeployment(client, ct, task);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    log.Failure("Deployment check", exception);
                    ui.Error(Describe(exception), log.FilePath);
                    var action = await ui.ChooseAsync("Deployment check failed", ["Retry", "Sign in", "Main menu", "Exit"], ct);
                    if (action != "Retry") return action;
                }
            }
        }
        finally
        {
            // Leaving deployment must release any remaining claim.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await client.ReleaseDeploymentAsync(timeout.Token); }
            catch (Exception ex) { log.Failure("Deployment release (lease will expire)", ex); }
        }
    }

    private Task<string> RunAssignedDeployment(ImagingClient client, CancellationToken ct, DeploymentAssignment task)
        => RunAssignedDeployment(client, ct, task, null);

    private async Task<string> RunAssignedDeployment(ImagingClient client, CancellationToken ct, DeploymentAssignment task, DeploymentDisk? reviewedDisk)
    {
        var disks = DeploymentDisks.Read(ct);
        if (disks.Length == 0) throw new InvalidOperationException("No eligible internal disk was found for deployment.");
        DeploymentDisk disk;
        if (reviewedDisk is not null)
            disk = disks.SingleOrDefault(x => x.Number == reviewedDisk.Number && x.UniqueId == reviewedDisk.UniqueId && x.Size == reviewedDisk.Size)
                ?? throw new InvalidOperationException("The selected disk changed after review. Deployment was stopped.");
        else if (disks.Length == 1) disk = disks[0];
        else
        {
            var choices = disks.Select(x => $"Erase disk {x.Number}: {x.Name}, {x.Size / 1073741824d:0.0} GiB ({x.UniqueId})").ToArray();
            var selection = await ui.ChooseAsync("Select the disk to erase for the assigned deployment", [.. choices, "Main menu"], ct);
            if (selection == "Main menu") return selection;
            disk = disks[Array.IndexOf(choices, selection)];
        }
        ui.Line($"Deploying {task.ImageName} to disk {disk.Number}: {disk.Name}. All existing data on this disk will be replaced.");
        CloneDeploy_Agent.Contracts.DeploymentCompletionAction? completedAction;
        await using (var machine = new DeploymentMachine(disk, settings, log)
                     { AgentInitiated = task.AgentInitiated, ReadFfuRange = client.ReadDeploymentFfuRangeAsync, DownloadDriver = client.DownloadDeploymentDriverAsync, ResolveSysprepCredentials = client.ResolveSysprepCredentialsAsync,
                         ResolveOu = (request, progress, token) => ResolveDeploymentOu(client, request, progress, token) })
        {
            var runner = new DeploymentRunner(client, machine, log);
            await ui.ShowProgressAsync(runner.RunAsync(task.DeploymentId, ct), ct);
            completedAction = runner.CompletedAction;
        }
        return await CompleteDeployment(completedAction, ct);
    }

    private async Task<string?> ResolveDeploymentOu(ImagingClient client, CloneDeploy_Agent.Contracts.ImagingOuRequest request,
        Action<DeploymentProgress> progress, CancellationToken ct)
    {
        var plan = await client.ResolveDeploymentOuAsync(request, ct);
        if (!plan.Success) throw new InvalidOperationException(plan.Message);
        if (!plan.Prompt) { log.Write(plan.Message); return plan.OrganizationalUnit; }
        var selected = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        progress(new(DeploymentStage.Finalizing, null)
        {
            Status = "Waiting for OU selection",
            Interaction = async uiToken =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, uiToken);
                var token = linked.Token;
                try
                {
                    var options = new List<MenuChoice> { new("default", "Domain default / existing account location") };
                    options.AddRange(plan.Choices.Select(x => new MenuChoice(x.DistinguishedName, x.DistinguishedName)));
                    var recommended = plan.OrganizationalUnit ?? "default";
                    options = options.OrderByDescending(x => x.Value == recommended).ToList();
                    options.Add(new("Cancel", "Cancel finalization (do not boot Windows)"));
                    log.Write(plan.Message);
                    var choice = await ui.ChooseOuAsync($"Select OU for {request.ComputerName} in {request.DomainName}", plan.Message, options.ToArray(), token);
                    if (choice == "Cancel") throw new InvalidOperationException("OU selection cancelled. Deployment was not finalized; Windows has not been booted.");
                    var confirmed = await client.ResolveDeploymentOuAsync(request with { SelectionMade = true, SelectedOu = choice == "default" ? null : choice }, token);
                    if (!confirmed.Success || confirmed.Prompt) throw new InvalidOperationException(confirmed.Message);
                    log.Write(confirmed.Message);
                    selected.TrySetResult(confirmed.OrganizationalUnit);
                }
                catch (Exception ex) { selected.TrySetException(ex); }
            }
        });
        return await selected.Task.WaitAsync(ct);
    }

    private async Task<string> SelectDeployment(ImagingClient client, CancellationToken ct)
    {
        if (client.Device is null) await LinkDevice(client, ct);
        if (client.Device is null) return "Main menu";
        var inventory = await new HardwareDiscovery().ReadAsync(ct);
        if (!inventory.IsWinPe) throw new InvalidOperationException("Boot WinPE to deploy an image.");
        if (DeploymentDisks.Read(ct).Length == 0) throw new InvalidOperationException("No eligible internal disk was found for deployment.");
        var catalog = await client.DeploymentCatalogAsync(ct);
        if (catalog.Images.Count == 0 || catalog.Profiles.Count == 0)
        {
            ui.Line("No deployment choices are available. Enable an image and a deployment profile visible in WinPE in the web app.");
            return "Main menu";
        }
        var imageChoice = await ui.ChooseAvailableAsync("Select image to deploy", [.. catalog.Images.Select(x =>
            new MenuChoice(x.Id.ToString(), $"{x.Name} ({x.Format})", x.UnavailableReason)), new("Cancel", "Cancel")], ct);
        if (imageChoice == "Cancel") return "Main menu";
        var image = catalog.Images.Single(x => x.Id.ToString() == imageChoice);
        Guid? versionId = null;
        if (catalog.CanChoose)
        {
            var choices = catalog.Versions.Where(x => x.ImageId == image.Id).ToList();
            var choice = await ui.ChooseAvailableAsync("Select image version", [.. choices.Select(x =>
                new MenuChoice(x.Id.ToString(), $"{x.Name} ({(x.Approved ? "approved" : "unapproved")}{(x.Active ? ", active" : "")})", x.UnavailableReason)), new("Cancel", "Cancel")], ct);
            if (choice == "Cancel") return "Main menu";
            versionId = Guid.Parse(choice);
        }
        var profileChoice = await ui.ChooseAvailableAsync("Select deployment profile", [.. catalog.Profiles.Select(x =>
            new MenuChoice(x.Id.ToString(), x.Name, x.UnavailableReason)), new("Cancel", "Cancel")], ct);
        if (profileChoice == "Cancel") return "Main menu";
        var profile = catalog.Profiles.Single(x => x.Id.ToString() == profileChoice);
        var chooseDrivers = client.Can("Imager.Override") && client.Can("DriverPack.View") && client.Can("DeploymentProfile.View");
            var drivers = chooseDrivers ? await client.SelectDriversAsync(profile.Id, inventory.Manufacturer, inventory.Model, ct) : new ManualDriverSelection(null, [], null);
        var driverId = drivers.SelectedId;
        if (chooseDrivers && driverId is null)
        {
            ui.Line(drivers.Warning ?? "Choose a driver pack.");
            var driverChoice = await ui.ChooseAvailableAsync("Select driver pack", [.. drivers.Options.Select(x =>
                new MenuChoice(x.Id.ToString(), x.Name)), new("0", "Continue without additional drivers"), new("Cancel", "Cancel")], ct);
            if (driverChoice == "Cancel") return "Main menu";
            driverId = int.Parse(driverChoice);
        }
        ui.Line($"Deploy {image.Name} using {profile.Name} to {client.Device.Name}. All existing data on the target disk will be erased.");
        var expected = await client.PrepareSelectedVersionAsync(image.Id, profile.Id, driverId, versionId, ct);
        try
        {
            var task = await client.ClaimDeploymentAsync(inventory, ct);
            if (task is null || task.DeploymentId != expected || task.DeviceId != client.Device.Id)
                throw new InvalidOperationException("The prepared deployment could not be claimed for this device. Check its assignment in the web app.");
            return await RunAssignedDeployment(client, ct, task);
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await client.ReleaseDeploymentAsync(timeout.Token); }
            catch (Exception ex) { log.Failure("Deployment release (lease will expire)", ex); }
        }
    }
}
