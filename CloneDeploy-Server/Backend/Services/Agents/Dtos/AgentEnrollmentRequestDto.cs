namespace CloneDeploy_Server.Backend.Services;

public sealed record AgentEnrollmentRequestDto(string ProvisioningKey, string DeviceName, Guid InstallationId, string CsrPem, bool RequestReset = false)
{
    public string? HardwareUuid { get; init; }
    public string[]? MacAddresses { get; init; }
}
