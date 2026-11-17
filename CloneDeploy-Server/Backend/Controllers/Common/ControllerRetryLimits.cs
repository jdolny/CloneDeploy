namespace CloneDeploy_Server.Backend.Controllers.Common;

internal static class ControllerRetryLimits
{
    public const int MinDelaySeconds = 15;
    public const int MaxDelaySeconds = 45;
}
