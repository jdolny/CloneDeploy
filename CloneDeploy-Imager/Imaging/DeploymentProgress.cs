namespace CloneDeploy_Imager.Imaging;

internal enum DeploymentStage { Preparing, Deploying, Finalizing }
// Null percentage means work is in progress but no measurable total is available.
/// <summary>
/// Carries the current imaging stage, optional percentage, status text, and network transfer rate.
/// </summary>
internal sealed record DeploymentProgress(DeploymentStage Stage, double? Percent, bool Complete = false)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<CancellationToken, Task>? Interaction { get; init; }
    public string? Status { get; init; }
    public double? NetworkBytesPerSecond { get; init; }
    public bool Receiving { get; init; }
}
