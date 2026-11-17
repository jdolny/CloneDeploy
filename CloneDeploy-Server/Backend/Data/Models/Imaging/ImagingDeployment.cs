using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

// A deployment keeps its original choices even when catalog settings change later.
public sealed class ImagingDeployment
{
    public Guid Id { get; set; }
    public int DeviceId { get; set; }
    public Guid ImageVersionId { get; set; }
    public Guid? MulticastSessionId { get; set; }
    public int DeploymentProfileId { get; set; }
    public string? AgentBootPlanJson { get; set; }
    public bool AgentBootReady { get; set; }
    public required string SettingsJson { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ImagingCompletedAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public DateTime? SetupCompletedAtUtc { get; set; }
    public DateTime? WindowsSetupReadyAtUtc { get; set; }
    public string? SetupReadinessMessage { get; set; }
    public DateTime? ImagerStartedAtUtc { get; set; }
    public DateTime? ImagerFailedAtUtc { get; set; }
    public Guid? ImagerClaimId { get; set; }
    public string? ImagerClaimKeyHash { get; set; }
    public DateTime? ImagerClaimExpiresAtUtc { get; set; }
    public ICollection<ImagingDeploymentPolicy> Policies { get; set; } = [];
}

public sealed class ImagingDeploymentPolicy
{
    public Guid Id { get; set; }
    public Guid ImagingDeploymentId { get; set; }
    public int PolicyId { get; set; }
    public int Position { get; set; }
    public required string Hash { get; set; }
    public required string SnapshotJson { get; set; }
    public Guid? AgentPolicyRunId { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public int? Result { get; set; }
    public ImagingDeployment? Deployment { get; set; }
}

public sealed class ImagingDeploymentConfiguration : IEntityTypeConfiguration<ImagingDeployment>
{
    public void Configure(EntityTypeBuilder<ImagingDeployment> b)
    {
        b.Property(x => x.ImagerClaimKeyHash).HasMaxLength(64);
        b.HasOne<MulticastSession>().WithMany().HasForeignKey(x => x.MulticastSessionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        // Historical version identity survives explicit deletion of image content.
        b.HasIndex(x => x.ImageVersionId);
        b.HasOne<DeploymentProfile>().WithMany().HasForeignKey(x => x.DeploymentProfileId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.DeviceId).IsUnique().HasFilter("\"CancelledAtUtc\" IS NULL AND \"SetupCompletedAtUtc\" IS NULL");
        b.HasMany(x => x.Policies).WithOne(x => x.Deployment).HasForeignKey(x => x.ImagingDeploymentId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ImagingDeploymentPolicyConfiguration : IEntityTypeConfiguration<ImagingDeploymentPolicy>
{
    public void Configure(EntityTypeBuilder<ImagingDeploymentPolicy> b)
    {
        b.HasIndex(x => new { x.ImagingDeploymentId, x.Position }).IsUnique();
        b.HasIndex(x => x.AgentPolicyRunId).IsUnique();
        b.HasOne<Policy>().WithMany().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Hash).HasMaxLength(64);
    }
}
