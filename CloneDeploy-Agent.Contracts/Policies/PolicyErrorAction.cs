namespace CloneDeploy_Agent.Contracts;

public enum PolicyErrorAction
{
    AbortCurrentPolicy = 0,
    AbortRemainingPolicies = 1,
    Continue = 2
}