namespace CloneDeploy_Agent.Contracts;

public enum PolicyConditionFailedAction
{
    MarkNotApplicable = 0,
    MarkSkipped = 1,
    MarkSuccess = 2,
    MarkFailed = 3,
    GotoModule = 4
}