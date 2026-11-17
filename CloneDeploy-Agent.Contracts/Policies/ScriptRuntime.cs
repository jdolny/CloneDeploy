namespace CloneDeploy_Agent.Contracts;

public static class ScriptRuntime
{
    public static IReadOnlyList<ActionScriptType> SupportedTypes { get; } = Array.AsReadOnly(new[]
    {
        ActionScriptType.Powershell, ActionScriptType.Powershell7, ActionScriptType.Batch,
        ActionScriptType.Bash, ActionScriptType.Zsh
    });

    public static string? ValidationError(ActionScriptType type) => !Enum.IsDefined(type) ? "Choose a valid script type." : null;
}
