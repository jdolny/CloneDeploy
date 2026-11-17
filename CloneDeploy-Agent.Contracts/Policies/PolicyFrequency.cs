namespace CloneDeploy_Agent.Contracts;

public enum PolicyFrequency
{
    Ongoing = 0,
    OncePerComputer = 1,
    OncePerUserPerComputer = 2,
    OncePerDay = 3,
    OncePerWeek = 4,
    OncePerMonth = 5,
    EveryXhours = 6,
    EveryXdays = 7
}