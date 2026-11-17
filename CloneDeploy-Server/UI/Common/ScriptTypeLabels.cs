using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.UI.Common;

public static class ScriptTypeLabels
{
    public static string GetLabel(ActionScriptType type) => UiText.Label(type switch
    {
        ActionScriptType.Powershell => "Windows PowerShell 5.1",
        ActionScriptType.Powershell7 => "PowerShell 7",
        ActionScriptType.Batch => "Batch",
        ActionScriptType.Bash => "Bash (macOS)",
        ActionScriptType.Zsh => "Zsh (macOS)",
        _ => type.ToString()
    });
}
