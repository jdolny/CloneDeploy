namespace CloneDeploy_Imager.UI;

internal sealed partial class ImagerConsole
{
    private const int CountdownRefreshMilliseconds = 100;

    public async Task<bool> AutomaticDeploymentCountdownAsync(string rule, string image, string profile, string disk, CancellationToken ct)
    {
        Heading();
        Line("Automatic deployment: " + rule);
        Line("Image: " + image);
        Line("Deployment profile: " + profile);
        Line("Target: " + disk);
        Line("ALL EXISTING DATA ON THIS DISK WILL BE ERASED. Press any key to cancel.");
        for (var seconds = 10; seconds > 0; seconds--)
        {
            Line($"Starting in {seconds} seconds...");
            for (var tick = 0; tick < 10; tick++)
            {
                ct.ThrowIfCancellationRequested();
                if (Console.KeyAvailable) { Console.ReadKey(true); Line("Automatic deployment canceled."); return false; }
                await Task.Delay(CountdownRefreshMilliseconds, ct);
            }
        }
        if (Console.KeyAvailable) { Console.ReadKey(true); Line("Automatic deployment canceled."); return false; }
        return true;
    }
}
