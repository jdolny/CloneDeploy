namespace CloneDeploy_Server.Backend.Services;

public sealed record AgentProvisioningOptionsDto(bool RequirePreProvision = false, bool RequireApproval = true, bool PreProvisionRequiresApproval = false)
{
    public bool AutoApproveResetRequests { get; init; }
    public int PendingRetentionDays { get; init; } = 30;
}
