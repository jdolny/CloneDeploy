namespace CloneDeploy_Agent;

/// <summary>Inclusive retry bounds shared by background and run-once task processing.</summary>
internal static class TaskRetry
{
    public const int MinimumSeconds = 15;
    public const int MaximumSeconds = 45;
}