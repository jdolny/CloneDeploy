using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.UI.Pages.Devices.Devices.Shared;

public static class DeviceEnrollmentLabels
{
    public static string Status(DeviceProvisionStatus status) => UiText.Label(status switch
    {
        DeviceProvisionStatus.NotStarted => "Not started",
        DeviceProvisionStatus.IntermediateInstalled => "Intermediate installed",
        DeviceProvisionStatus.PendingPreProvision => "Waiting for device record",
        DeviceProvisionStatus.PendingProvisionApproval => "Pending enrollment approval",
        DeviceProvisionStatus.PendingConfirmation => "Pending confirmation",
        DeviceProvisionStatus.PendingReset => "Pending reset",
        DeviceProvisionStatus.PreProvisioned => "Pre-created",
        DeviceProvisionStatus.ProvisionApproved => "Enrollment approved",
        DeviceProvisionStatus.Provisioned => "Enrolled",
        DeviceProvisionStatus.FullReset => "Full reset",
        _ => status.ToString()
    });
}
