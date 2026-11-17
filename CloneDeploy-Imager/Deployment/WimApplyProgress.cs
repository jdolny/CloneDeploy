using System.Globalization;
using System.Text.RegularExpressions;
using CloneDeploy_Imager.Imaging;

namespace CloneDeploy_Imager.Deployment;

/// <summary>Combines wimlib's three apply steps into one forward-only progress bar.</summary>
internal sealed partial class WimApplyProgress(Action<DeploymentProgress> report)
{
    // Data extraction dominates apply time; these weights approximate work, not elapsed time.
    private const double FileCreationWeight = 5;
    private const double FileDataWeight = 90;
    private const double MetadataWeight = 100 - FileCreationWeight - FileDataWeight;
    private const double MaximumRunningPercent = 99;
    private readonly object gate = new();
    private int lastStep = -1;
    private double lastPercent;

    public void AcceptLine(string line)
    {
        var match = ApplyStep().Match(line);
        if (!match.Success || !double.TryParse(match.Groups[2].Value, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var percent) || percent > 100) return;
        var (step, start, weight, status) = match.Groups[1].Value switch
        {
            "Creating files" => (0, 0d, FileCreationWeight, "Creating Windows files and folders"),
            "Extracting file data" => (1, FileCreationWeight, FileDataWeight, "Writing Windows file data"),
            _ => (2, FileCreationWeight + FileDataWeight, MetadataWeight, "Applying Windows file metadata")
        };
        lock (gate)
        {
            if (step < lastStep) return;
            var overall = Math.Min(MaximumRunningPercent, start + percent * weight / 100);
            if (step == lastStep && overall <= lastPercent) return;
            lastStep = step;
            lastPercent = Math.Max(lastPercent, overall);
            report(new(DeploymentStage.Deploying, lastPercent) { Status = status });
        }
    }

    // Match named steps, not arbitrary percentages such as WIM integrity checks.
    [GeneratedRegex(@"^\s*(Creating files|Extracting file data|Applying metadata to files): .*\(([0-9]{1,3}(?:\.[0-9]+)?)%\) done\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ApplyStep();
}
