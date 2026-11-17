namespace CloneDeploy_Agent.Contracts;

public enum PolicyTrigger
{
    StartupOrCheckin = 0,
    Startup = 1,
    Checkin = 2,
    Login = 3,
    SelfServiceOnly = 4
}
