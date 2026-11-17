using CloneDeploy_Imager.Deployment;
using Microsoft.Win32;

namespace CloneDeploy_Imager;

/// <summary>Shows imaging success and handles the selected WinPE power action.</summary>
internal sealed partial class ImagerApplication
{
    private async Task<string> CompleteDeployment(CloneDeploy_Agent.Contracts.DeploymentCompletionAction? action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (action is null) throw new InvalidOperationException("Deployment success was not confirmed; no power action was taken.");
        ui.Line("Image deployed successfully.");
        log.Write($"After successful deployment: {action}.");
        if (action == CloneDeploy_Agent.Contracts.DeploymentCompletionAction.ReturnToMenu) return "Main menu";
        var command = action switch
        {
            CloneDeploy_Agent.Contracts.DeploymentCompletionAction.Restart => "reboot",
            CloneDeploy_Agent.Contracts.DeploymentCompletionAction.ShutDown => "shutdown",
            _ => throw new InvalidDataException("Invalid deployment completion action.")
        };
        using var winPe = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT");
        if (winPe is null) throw new InvalidOperationException("Reboot and shutdown are available only in WinPE.");
        await new DeploymentTools(log).RunAsync(Path.Combine(Environment.SystemDirectory, "wpeutil.exe"), [command], ct);
        return "Exit";
    }

    private async Task<string> CompleteImaging(string message, CancellationToken ct)
    {
        var action = await ui.ShowSuccessAsync(message, ct);
        if (action == "Main menu") return action;
        using var winPe = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT");
        if (winPe is null) throw new InvalidOperationException("Reboot and shutdown are available only in WinPE.");
        await new DeploymentTools(log).RunAsync(Path.Combine(Environment.SystemDirectory, "wpeutil.exe"),
            [action == "Reboot" ? "reboot" : "shutdown"], ct);
        return "Exit";
    }
}
