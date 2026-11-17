namespace CloneDeploy_Agent;

/// <summary>Persisted execution states; values must remain compatible with existing journals.</summary>
internal static class ExecutionState
{
    public const string Prepared = "Prepared";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Interrupted = "Interrupted";
    public const string Abandoned = "Abandoned";
}