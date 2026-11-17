namespace CloneDeploy_Agent.Contracts;

public enum ActionScriptType
{
    // Persisted values: keep existing assignments stable when adding runtimes.
    Powershell = 0,
    Batch = 2,
    Powershell7 = 3,
    Bash = 4,
    Zsh = 5,
}
