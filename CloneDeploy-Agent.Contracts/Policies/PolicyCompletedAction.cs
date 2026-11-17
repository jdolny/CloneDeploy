namespace CloneDeploy_Agent.Contracts;

public enum PolicyCompletedAction
{
    DoNothing = 0,
    Reboot = 1,
    RebootIfNoLogins = 2
}