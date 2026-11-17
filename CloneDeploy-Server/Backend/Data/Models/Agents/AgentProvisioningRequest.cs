using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public enum AgentProvisioningState
{
    PendingPreProvision = 0,
    PendingApproval = 1,
    Approved = 2,
    Denied = 3,
    Provisioned = 4,
    PendingReset = 5,
    ResetApproved = 6,
    ResetDenied = 7
}

public sealed class AgentProvisioningRequest
{
    public Guid Id { get; set; }
    public Guid InstallationId { get; set; }
    public required string DeviceName { get; set; }
    public required string PublicKeyHash { get; set; }
    public string? IpAddress { get; set; }
    public string? HardwareUuid { get; set; }
    public string[] MacAddresses { get; set; } = [];
    public DateTime RequestedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public AgentProvisioningState State { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public int? ApprovedByUserId { get; set; }
    public int? TargetDeviceId { get; set; }
    public Device? TargetDevice { get; set; }
    public Guid? AgentCertificateId { get; set; }
    public AgentCertificate? AgentCertificate { get; set; }
}

public sealed class AgentProvisioningRequestConfiguration : IEntityTypeConfiguration<AgentProvisioningRequest>
{
    public void Configure(EntityTypeBuilder<AgentProvisioningRequest> builder)
    {
        builder.Property(x => x.DeviceName).HasMaxLength(128);
        builder.Property(x => x.PublicKeyHash).HasMaxLength(64);
        builder.Property(x => x.IpAddress).HasMaxLength(64);
        builder.HasIndex(x => x.InstallationId).IsUnique();
        builder.HasOne(x => x.TargetDevice).WithMany().HasForeignKey(x => x.TargetDeviceId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(x => x.AgentCertificate).WithMany().HasForeignKey(x => x.AgentCertificateId).OnDelete(DeleteBehavior.SetNull);
    }
}
