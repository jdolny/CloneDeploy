namespace CloneDeploy_Agent.Contracts;

/// <summary>Global feature availability and portal preferences.</summary>
public sealed record AgentFeatures
{
    public int PortalSessionDays { get; init; } = 90;
    public bool PromptForMacSignIn { get; init; } = true;
    public bool HelpDeskEnabled { get; init; }
    public bool SelfServiceEnabled { get; init; }
    public bool WinGetEnabled { get; init; }
}
