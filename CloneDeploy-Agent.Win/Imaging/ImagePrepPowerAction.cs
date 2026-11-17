using System.Diagnostics;
using System.Text.Json;

namespace CloneDeploy_Agent;

// Contains no credentials or executable paths. The service writes this before its
// independent finalizer stops it; uninstall copies these flags into its own request.
internal sealed record ImagePrepPowerAction(bool RunSysprep, bool UseAnswerFile)
{
    internal static string RequestPath(string directory) => Path.Combine(directory, "ImagePrep", "power-action.json");

    internal static Task SaveAsync(string directory, bool runSysprep, bool useAnswerFile, CancellationToken ct) =>
        File.WriteAllTextAsync(RequestPath(directory), JsonSerializer.Serialize(new ImagePrepPowerAction(runSysprep, useAnswerFile)), ct);

    internal static ImagePrepPowerAction Load(string directory)
    {
        var path = RequestPath(directory);
        PolicyActionRunner.RejectReparsePoints(path);
        return JsonSerializer.Deserialize<ImagePrepPowerAction>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Image prep power action is missing.");
    }

    internal async Task ExecuteAsync(bool capture, string log)
    {
        var panther = Path.Combine(Environment.SystemDirectory, "Sysprep", "Panther");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "Sysprep", "sysprep.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "/generalize", "/oobe", "/quiet", capture ? "/reboot" : "/shutdown" })
            start.ArgumentList.Add(argument);
        if (UseAnswerFile)
            start.ArgumentList.Add("/unattend:" + Path.Combine(Environment.SystemDirectory, "Sysprep", "unattend.xml"));
        File.AppendAllText(log, $"{DateTimeOffset.UtcNow:O} Starting Sysprep as the final operation ({(capture ? "reboot" : "shutdown")}). Logs: {panther}\r\n");
        using var sysprep = Process.Start(start) ?? throw new IOException("Could not start Sysprep.");
        // Do not tie this process to the service lifetime or kill a partially generalized OS on timeout.
        await sysprep.WaitForExitAsync();
        if (sysprep.ExitCode != 0)
            throw new InvalidOperationException($"Sysprep failed with exit code {sysprep.ExitCode} (0x{sysprep.ExitCode:X8}). Logs: {panther}");
    }
}
