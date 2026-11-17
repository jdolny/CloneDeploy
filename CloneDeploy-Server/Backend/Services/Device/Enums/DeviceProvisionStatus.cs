namespace CloneDeploy_Server.Backend.Enum;

public enum DeviceProvisionStatus
{
    NotStarted = 0, 
    IntermediateInstalled = 1, 
    PendingPreProvision = 2, 
    PendingProvisionApproval = 3,
    PendingConfirmation = 4,
    PendingReset = 5,     
    PreProvisioned = 6,
    ProvisionApproved = 7,
    Provisioned = 8,
    Reset = 9,
    Error = 10,
    Archived = 11,
    FullReset = 12,
    Unmanaged = 13
}